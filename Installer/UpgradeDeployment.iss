// Upgrade-only lifecycle. Explicit uninstall/data removal stays in build_Installer.iss.
#include MyRuntimeInventory

type
  TServiceStatus = record
    ServiceType, State, Controls, Win32Exit, ServiceExit, CheckPoint, WaitHint: LongWord;
  end;

function OpenSCManager(Machine, Database: string; Access: LongWord): LongWord;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenService(Manager: LongWord; Name: string; Access: LongWord): LongWord;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(Service: LongWord; var Status: TServiceStatus): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function ControlService(Service, Control: LongWord; var Status: TServiceStatus): Boolean;
  external 'ControlService@advapi32.dll stdcall';
function StartService(Service, Count, Arguments: LongWord): Boolean;
  external 'StartServiceW@advapi32.dll stdcall';
function CloseServiceHandle(Handle: LongWord): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';
function GetFileAttributes(Name: string): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function CreateFile(Name: string; Access, Share, Security, Disposition, Flags, Template: LongWord): LongWord;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseHandle(Handle: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function WriteFile(Handle: LongWord; var Buffer: LongWord; Count: LongWord; var Written: LongWord; Overlapped: LongWord): Boolean;
  external 'WriteFile@kernel32.dll stdcall';

const
  CurrentDistributionMode = '{#MyDistributionMode}';
  CurrentArchitecture = '{#MyArchitecture}';
  DeploymentFileName = 'ContextMenuMgr.deployment.ini';
var
  PreviousDistributionMode, PreviousArchitecture, PreviousInstallPath, CurrentInstallPath: string;
  RuntimeRows: TArrayOfString;
  CleanupRequired, CleanupDone, FilesStarted, MutationStarted, DeploymentFinished: Boolean;
  RestartCurrentService, RestartLegacyService: Boolean;

function NormalPath(const Path: string): string;
begin
  Result := RemoveBackslashUnlessRoot(ExpandFileName(Path));
end;

procedure DeploymentError(const Message: string);
begin
  RaiseException('Deployment upgrade: ' + Message + ' Use /LOG for diagnostics.');
end;

procedure CheckSafePath(const Path: string);
var
  Parent: string;
begin
  if (Length(Path) < 4) or (Copy(Path, 2, 2) <> ':\') or
     (CompareText(Path, NormalPath(Path)) <> 0) then
    DeploymentError('Select a dedicated local application directory.');
  if (CompareText(Path, ExpandConstant('{commonpf}')) = 0) or
     (CompareText(Path, ExpandConstant('{commonpf32}')) = 0) or
     (Pos(Lowercase(AddBackslash(ExpandConstant('{win}'))), Lowercase(AddBackslash(Path))) = 1) then
    DeploymentError('System directories are not valid application destinations.');
  if IsWin64 and (CompareText(Path, ExpandConstant('{commonpf64}')) = 0) then
    DeploymentError('Program Files root is not a valid application destination.');
  Parent := Path;
  while Length(Parent) > 3 do begin
    if DirExists(Parent) and ((GetFileAttributes(Parent) and $400) <> 0) then
      DeploymentError('Reparse-point application paths are not supported: ' + Parent);
    Parent := RemoveBackslashUnlessRoot(ExtractFileDir(Parent));
  end;
end;

function DeploymentFile: string;
begin
  Result := CurrentInstallPath + '\' + DeploymentFileName;
end;

procedure MarkDeploymentIncomplete;
var
  BOM, Handle, Written: LongWord;
begin
  if (FileExists(DeploymentFile) or DirExists(DeploymentFile)) and
     ((GetFileAttributes(DeploymentFile) and $411) <> 0) then
    DeploymentError('The deployment marker is read-only, a directory or a reparse point.');
  // Write the UTF-16 BOM as two bytes, without ANSI code-page conversions.
  // Recreate our marker also when replacing an older ANSI/corrupt record.
  Handle := CreateFile(DeploymentFile, $40000000, 0, 0, 2, 0, 0);
  if Handle = $FFFFFFFF then
    DeploymentError('Cannot create the deployment marker in the installation directory.');
  try
    BOM := $FEFF;
    if not WriteFile(Handle, BOM, 2, Written, 0) or (Written <> 2) then
      DeploymentError('Cannot initialize the Unicode deployment marker.');
  finally
    CloseHandle(Handle);
  end;
  if not SetIniString('Deployment', 'AppId', '{#MyAppId}', DeploymentFile) or
     not SetIniString('Deployment', 'Architecture', CurrentArchitecture, DeploymentFile) or
     not SetIniString('Deployment', 'InstallPath', CurrentInstallPath, DeploymentFile) or
     not SetIniString('Deployment', 'Complete', '0', DeploymentFile) then
    DeploymentError('Cannot mark deployment in progress in the installation directory.');
end;

function GetIncomingRuntimeFiles(Param: string): string;
begin
  Result := IncomingRuntimeFiles;
end;

function GetIncomingRuntimeHash(Param: string): string;
begin
  Result := GetSHA256OfString(IncomingRuntimeFiles);
end;

procedure DetectPreviousInstallation;
var
  I, J: Integer;
  Name, Inventory, InventoryHash, MetadataPath: string;
  Parts: TArrayOfString;
  PackageText: AnsiString;
begin
  CleanupRequired := False;
  SetArrayLength(RuntimeRows, 0);
  PreviousDistributionMode := 'Unknown';
  PreviousArchitecture := 'Unknown';
  PreviousInstallPath := '';
  CurrentInstallPath := NormalPath(ExpandConstant('{app}'));
  CheckSafePath(CurrentInstallPath);
  if LoadStringFromFile(CurrentInstallPath + '\ContextMenuMgr.package.json', PackageText) and
     (Pos('"portable"', Lowercase(String(PackageText))) > 0) then
    DeploymentError('The destination contains a Portable package. Choose a separate installer directory; its Data directory is preserved.');
  // The local marker controls optional cleanup, never admission to an upgrade.
  // Inno retains responsibility for AppId, uninstall registration and log append.
  try
    if not FileExists(DeploymentFile) then begin
      if DirExists(DeploymentFile) then DeploymentError('The deployment marker path is a directory.');
      Log('Legacy deployment: Unknown; no installation-directory marker, no runtime deletion.');
      exit;
    end;
    if (GetFileAttributes(DeploymentFile) and $400) <> 0 then
      DeploymentError('The deployment marker must not be a reparse point.');
    if GetIniString('Deployment', 'AppId', '', DeploymentFile) <> '{#MyAppId}' then
      DeploymentError('The installation-directory marker belongs to a different AppId.');
    PreviousArchitecture := GetIniString('Deployment', 'Architecture', '', DeploymentFile);
    MetadataPath := GetIniString('Deployment', 'InstallPath', '', DeploymentFile);
    if (PreviousArchitecture <> CurrentArchitecture) or (MetadataPath = '') or
       (CompareText(NormalPath(MetadataPath), CurrentInstallPath) <> 0) then
      DeploymentError('Malformed, different-architecture or different-directory deployment marker.');
    PreviousInstallPath := NormalPath(MetadataPath);
    if GetIniString('Deployment', 'Complete', '', DeploymentFile) <> '1' then
      DeploymentError('Incomplete deployment marker cannot authorize runtime cleanup.');
    PreviousDistributionMode := GetIniString('Deployment', 'DistributionMode', '', DeploymentFile);
    if (PreviousDistributionMode <> 'self-contained') and (PreviousDistributionMode <> 'framework-dependent') then
      DeploymentError('Malformed deployment mode in the installation-directory marker.');
    CleanupRequired := (PreviousDistributionMode = 'self-contained') and (CurrentDistributionMode = 'framework-dependent');
    Log('Deployment: ' + PreviousDistributionMode + ' -> ' + CurrentDistributionMode + '; architecture=' + PreviousArchitecture + '; path=' + PreviousInstallPath);
    if not CleanupRequired then exit;
    Inventory := GetIniString('Deployment', 'RuntimeFiles', '', DeploymentFile);
    InventoryHash := GetIniString('Deployment', 'RuntimeFilesSHA256', '', DeploymentFile);
    if (Inventory = '') or (CompareText(GetSHA256OfString(Inventory), InventoryHash) <> 0) then
      DeploymentError('The previous runtime ownership record is missing, corrupt or incomplete.');
    RuntimeRows := StringSplit(Inventory, [';'], stExcludeEmpty);
    for I := 0 to GetArrayLength(RuntimeRows) - 1 do begin
      Parts := StringSplit(RuntimeRows[I], ['|'], stAll);
      if GetArrayLength(Parts) <> 2 then DeploymentError('Invalid runtime ownership record.');
      Name := Parts[0];
      if (Name = '') or (ExtractFileName(Name) <> Name) or (Pos('..', Name) <> 0) or
         (Pos(':', Name) <> 0) or ((CompareText(ExtractFileExt(Name), '.dll') <> 0) and (Name <> 'createdump.exe')) or
         (Length(Parts[1]) <> 64) then DeploymentError('Unsafe runtime ownership record: ' + Name);
      for J := 1 to Length(Name) do
        if Pos(Copy(Name, J, 1), 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-') = 0 then
          DeploymentError('Invalid runtime filename: ' + Name);
      for J := 1 to Length(Parts[1]) do
        if Pos(Lowercase(Copy(Parts[1], J, 1)), '0123456789abcdef') = 0 then
          DeploymentError('Invalid runtime file hash: ' + Name);
    end;
  except
    // No files have been changed here. Incomplete, foreign or malformed metadata
    // cannot authorize deletion, but must not prevent ordinary file replacement.
    Log('Skipping optional runtime cleanup; continuing ordinary installation. ' + GetExceptionMessage);
    CleanupRequired := False;
    SetArrayLength(RuntimeRows, 0);
    PreviousDistributionMode := 'Unknown';
    PreviousArchitecture := 'Unknown';
    PreviousInstallPath := '';
  end;
end;

function RuntimeFilePresent(const FileName: string): Boolean;
var
  Attributes, Error: LongWord;
begin
  Attributes := GetFileAttributes(FileName);
  if Attributes = $FFFFFFFF then begin
    Error := DLLGetLastError;
    if (Error <> 2) and (Error <> 3) then
      DeploymentError('Cannot inspect runtime file: ' + FileName + ' (' + IntToStr(Error) + ')');
    Result := False;
  end else begin
    if (Attributes and $411) <> 0 then
      DeploymentError('Read-only, directory or reparse-point runtime file: ' + FileName);
    Result := True;
  end;
end;

procedure ChangeOwnedService(const Name: string; Stop: Boolean; var MustRestart: Boolean);
var
  Manager, Service: LongWord;
  Status: TServiceStatus;
  Attempt, Desired: Integer;
  Image: string;
begin
  if not Stop and not MustRestart then exit;
  Manager := OpenSCManager('', '', 1);
  if Manager = 0 then DeploymentError('Cannot open Windows Service Manager.');
  try
    Service := OpenService(Manager, Name, $34); // query, start, stop only
    if Service = 0 then begin
      if DLLGetLastError = 1060 then exit;
      DeploymentError('Cannot open service ' + Name + ': ' + IntToStr(DLLGetLastError));
    end;
    try
      if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + Name, 'ImagePath', Image) then
        DeploymentError('Cannot verify service executable: ' + Name);
      if Pos('"', Image) = 1 then begin
        Delete(Image, 1, 1);
        Image := Copy(Image, 1, Pos('"', Image) - 1);
      end else if Pos(' --', Image) > 0 then Image := Copy(Image, 1, Pos(' --', Image) - 1);
      if CompareText(NormalPath(Image), CurrentInstallPath + '\ContextMenuManagerPlus.Service.exe') <> 0 then begin
        Log('Leaving service in another installation directory unchanged: ' + Name);
        exit;
      end;
      if not QueryServiceStatus(Service, Status) then DeploymentError('Cannot query service: ' + Name);
      if Stop then begin
        if Status.State = 1 then exit; // stopped
        if (Status.State <> 4) and (Status.State <> 3) then
          DeploymentError('Service is not running/stopped/stop-pending: ' + Name);
        if Status.State = 4 then MustRestart := True;
        Desired := 1;
        if (Status.State = 4) and not ControlService(Service, 1, Status) then
          DeploymentError('Cannot stop service: ' + Name);
      end else begin
        Desired := 4;
        if (Status.State <> 4) and (Status.State <> 2) and not StartService(Service, 0, 0) then
          DeploymentError('Cannot restart service: ' + Name + '. Repair or restart it before using the application.');
      end;
      for Attempt := 1 to 150 do begin
        if not QueryServiceStatus(Service, Status) then DeploymentError('Cannot query service: ' + Name);
        if Status.State = Desired then begin
          if not Stop then MustRestart := False;
          exit;
        end;
        Sleep(200);
      end;
      DeploymentError('Timed out waiting for service: ' + Name + '. Restart Windows and retry.');
    finally
      CloseServiceHandle(Service);
    end;
  finally
    CloseServiceHandle(Manager);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  Result := '';
  try
    DetectPreviousInstallation;
    ChangeOwnedService(ServiceName, True, RestartCurrentService);
    ChangeOwnedService(LegacyServiceName, True, RestartLegacyService);
  except
    Result := GetExceptionMessage;
  end;
end;

procedure RegisterExtraCloseApplicationsResources;
var
  I: Integer;
  Parts: TArrayOfString;
begin
  for I := 0 to GetArrayLength(RuntimeRows) - 1 do begin
    Parts := StringSplit(RuntimeRows[I], ['|'], stExcludeEmpty);
    if not RegisterExtraCloseApplicationsResource(False, CurrentInstallPath + '\' + Parts[0]) then
      DeploymentError('Cannot register runtime file with Restart Manager.');
  end;
end;

procedure CleanupPreviousRuntime;
var
  I: Integer;
  FileName, ProcessPath: string;
  Parts: TArrayOfString;
  Handle: LongWord;
  Locator, Wmi, Processes: Variant;
begin
  if FilesStarted then exit;
  FilesStarted := True;
  CheckSafePath(CurrentInstallPath);
  if not CleanupRequired then begin
    MarkDeploymentIncomplete;
    MutationStarted := True;
    exit;
  end;
  // Restart Manager may be declined by the user. Never proceed with destructive
  // cleanup while a known host in this directory is still running.
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Wmi := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Wmi.ExecQuery('SELECT ExecutablePath FROM Win32_Process WHERE ' +
      'Name="ContextMenuManagerPlus.exe" OR Name="ContextMenuManagerPlus.TrayHost.exe" OR Name="ContextMenuManagerPlus.Service.exe"');
  except
    DeploymentError('Cannot verify running application hosts through Windows WMI: ' + GetExceptionMessage);
  end;
  for I := 0 to Processes.Count - 1 do begin
    try
      ProcessPath := Processes.ItemIndex(I).Properties_.Item('ExecutablePath').Value;
    except
      ProcessPath := '';
    end;
    if (ProcessPath = '') or (CompareText(NormalPath(ExtractFileDir(ProcessPath)), CurrentInstallPath) = 0) then
      DeploymentError('An application host is still running, or its path cannot be verified. Close Frontend/TrayHost or restart Windows before switching to framework-dependent.');
  end;
  // All ownership, hashes and exclusive access checked before the first deletion.
  for I := 0 to GetArrayLength(RuntimeRows) - 1 do begin
    Parts := StringSplit(RuntimeRows[I], ['|'], stExcludeEmpty);
    FileName := CurrentInstallPath + '\' + Parts[0];
    if RuntimeFilePresent(FileName) then begin
      if CompareText(GetSHA256OfFile(FileName), Parts[1]) <> 0 then
        DeploymentError('Runtime file has been changed externally; it will not be deleted: ' + FileName);
      Handle := CreateFile(FileName, $80010000, 0, 0, 3, 0, 0);
      if Handle = $FFFFFFFF then DeploymentError('Runtime file is locked or inaccessible: ' + FileName + '. Close the application/TrayHost or restart Windows.');
      CloseHandle(Handle);
    end;
  end;
  MarkDeploymentIncomplete;
  MutationStarted := True;
  for I := 0 to GetArrayLength(RuntimeRows) - 1 do begin
    Parts := StringSplit(RuntimeRows[I], ['|'], stExcludeEmpty);
    FileName := CurrentInstallPath + '\' + Parts[0];
    if RuntimeFilePresent(FileName) then begin
      // Recheck immediately before deletion; externally replaced files are not owned.
      if CompareText(GetSHA256OfFile(FileName), Parts[1]) <> 0 then
        DeploymentError('Runtime file changed during cleanup: ' + FileName);
      if not DeleteFile(FileName) or FileExists(FileName) then
        DeploymentError('Runtime cleanup failed: ' + FileName + '. Reinstall self-contained to recover; cleanup is not transactional.');
      Log('Removed verified runtime file: ' + FileName);
    end;
  end;
  CleanupDone := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  I: Integer;
  Rows, Parts: TArrayOfString;
begin
  if CurStep = ssPostInstall then begin
    if CleanupRequired and not CleanupDone then DeploymentError('Runtime cleanup did not complete.');
    Rows := StringSplit(IncomingRuntimeFiles + IncomingApplicationFiles, [';'], stExcludeEmpty);
    for I := 0 to GetArrayLength(Rows) - 1 do begin
      Parts := StringSplit(Rows[I], ['|'], stAll);
      if not FileExists(CurrentInstallPath + '\' + Parts[0]) or
         (CompareText(GetSHA256OfFile(CurrentInstallPath + '\' + Parts[0]), Parts[1]) <> 0) then
        DeploymentError('Installed payload verification failed: ' + Parts[0]);
    end;
    if (GetIniString('Deployment', 'AppId', '', DeploymentFile) <> '{#MyAppId}') or
       (GetIniString('Deployment', 'DistributionMode', '', DeploymentFile) <> CurrentDistributionMode) or
       (GetIniString('Deployment', 'Architecture', '', DeploymentFile) <> CurrentArchitecture) or
       (CompareText(GetIniString('Deployment', 'InstallPath', '', DeploymentFile), CurrentInstallPath) <> 0) or
       (GetIniString('Deployment', 'RuntimeFiles', '', DeploymentFile) <> IncomingRuntimeFiles) or
       (GetIniString('Deployment', 'RuntimeFilesSHA256', '', DeploymentFile) <> GetSHA256OfString(IncomingRuntimeFiles)) then
      DeploymentError('Cannot verify the installation-directory deployment record.');
    ChangeOwnedService(ServiceName, False, RestartCurrentService);
    ChangeOwnedService(LegacyServiceName, False, RestartLegacyService);
    if not SetIniString('Deployment', 'Complete', '1', DeploymentFile) then
      DeploymentError('Cannot finalize deployment metadata.');
    DeploymentFinished := True;
  end;
end;

function DeploymentSucceeded: Boolean;
begin
  Result := DeploymentFinished;
end;

function DeploymentMarkerBelongsToUninstaller: Boolean;
var
  AppPath, MarkerPath: string;
begin
  AppPath := NormalPath(ExpandConstant('{app}'));
  MarkerPath := AppPath + '\' + DeploymentFileName;
  Result := False;
  if not FileExists(MarkerPath) or ((GetFileAttributes(MarkerPath) and $400) <> 0) then exit;
  Result := (GetIniString('Deployment', 'AppId', '', MarkerPath) = '{#MyAppId}') and
    (GetIniString('Deployment', 'Architecture', '', MarkerPath) = CurrentArchitecture) and
    (CompareText(GetIniString('Deployment', 'InstallPath', '', MarkerPath), AppPath) = 0);
end;

<event('CurUninstallStepChanged')>
procedure RemoveOwnedDeploymentMarker(CurUninstallStep: TUninstallStep);
begin
  // [UninstallDelete] Check is evaluated while Setup records the uninstall log,
  // not against the marker that exists at the time of a later uninstall.
  if (CurUninstallStep = usUninstall) and DeploymentMarkerBelongsToUninstaller then
    if not DeleteFile(ExpandConstant('{app}') + '\' + DeploymentFileName) then
      DeploymentError('Cannot remove this installation deployment marker.');
end;

function GetCustomSetupExitCode: Integer;
begin
  // Inno does not turn ssPostInstall script exceptions into a failure exit code.
  if DeploymentFinished then Result := 0 else Result := 1;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and not DeploymentFinished then begin
    WizardForm.FinishedHeadingLabel.Caption := 'Installation incomplete';
    WizardForm.FinishedLabel.Caption := 'Deployment verification did not complete. Review the setup log and restore application files before starting the application.';
    WizardForm.RunList.Visible := False;
  end;
end;

procedure DeinitializeSetup;
begin
  // BeforeInstall errors can be ignored in Inno's file-error dialog. Once file
  // installation has begun, do not assume an error left the payload unchanged.
  if not MutationStarted and not FilesStarted then begin
    try
      ChangeOwnedService(ServiceName, False, RestartCurrentService);
      ChangeOwnedService(LegacyServiceName, False, RestartLegacyService);
    except
      Log(GetExceptionMessage);
      SuppressibleMsgBox(GetExceptionMessage, mbError, MB_OK, IDOK);
    end;
  end else if not DeploymentFinished then begin
    Log('Deployment incomplete after file installation began. Verify/restore the payload before restarting a stopped backend service.');
    SuppressibleMsgBox('Deployment did not complete. Keep the setup log and reinstall the self-contained installer of the same architecture to restore application files. A previously running backend service may remain stopped until recovery.', mbError, MB_OK, IDOK);
  end;
end;
