#ifndef MyAppId
  #define MyAppId "45156332-3408-47B7-B5D2-2567E5888F64"
#endif

#ifndef MyAppBuildDir
  #define MyAppBuildDir "..\build\ContextMenuManagerPlus"
#endif

#ifndef MyAppSetupName
  #define MyAppSetupName "ContextMenuManagerPlus_Setup"
#endif

#ifndef MyOutputDir
  #define MyOutputDir "..\build"
#endif

#ifndef MyArchitecturesAllowed
  #define MyArchitecturesAllowed "x64compatible"
#endif

#ifndef MyArchitecturesInstallIn64BitMode
  #define MyArchitecturesInstallIn64BitMode "x64compatible"
#endif

#ifndef MyUseDotNetDependencyInstaller
  #define MyUseDotNetDependencyInstaller "0"
#endif

#if MyUseDotNetDependencyInstaller == "1"
  #include "InnoDependencyInstaller\CodeDependencies.iss"
#endif

#define MyAppName "Context Menu Manager Plus"
#define MyAppPublisher "PLFJY"
#define MyAppURL "https://plfjy.top/"
#define MyAppExeName "ContextMenuManagerPlus.exe"
#define MyServiceExeName "ContextMenuManagerPlus.Service.exe"
#define AppExePath AddBackslash(MyAppBuildDir) + MyAppExeName
#define MyAppVersion GetVersionNumbersString(AppExePath)
#define AppProductTextVersion GetStringFileInfo(AppExePath, "ProductVersion")

[Setup]
AppId={{{#MyAppId}}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#AppProductTextVersion}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductTextVersion={#AppProductTextVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableWelcomePage=no
DisableReadyPage=yes
#if MyArchitecturesAllowed != ""
ArchitecturesAllowed={#MyArchitecturesAllowed}
#endif
#if MyArchitecturesInstallIn64BitMode != ""
ArchitecturesInstallIn64BitMode={#MyArchitecturesInstallIn64BitMode}
#endif
DisableProgramGroupPage=yes
LicenseFile=..\License
OutputDir={#MyOutputDir}
OutputBaseFilename={#MyAppSetupName}
SetupIconFile=..\ContextMenuMgr.Frontend\Assets\AppIcon.ico
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkablealone

[Files]
Source: "{#MyAppBuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
const
  ServiceName = 'ContextMenuManagerPlusService';
  LegacyServiceName = 'ContextMenuManagerService';
  ServiceDisplayName = 'Context Menu Manager Plus Service';

procedure InitializeWizard();
begin
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;

#if MyUseDotNetDependencyInstaller == "1"
function InitializeSetup: Boolean;
begin
  Dependency_AddDotNet100Desktop;
  Result := True;
end;
#endif

function RunHidden(const FileName, Params: string): Integer;
var
  ResultCode: Integer;
begin
  if Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode
  else
    Result := -1;
end;

procedure StopAndDeleteServiceIfPresentByName(const TargetName: string);
var
  ScPath: string;
begin
  ScPath := ExpandConstant('{sys}\sc.exe');

  if not RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\' + TargetName) then
    exit;

  RunHidden(ScPath, 'stop ' + TargetName);
  Sleep(1200);
  RunHidden(ScPath, 'delete ' + TargetName);
  Sleep(1200);
end;

procedure StopAndDeleteServiceIfPresent();
begin
  StopAndDeleteServiceIfPresentByName(ServiceName);
  StopAndDeleteServiceIfPresentByName(LegacyServiceName);
end;

procedure InstallAndStartService();
var
  ScPath: string;
  ServiceExePath: string;
  BinPath: string;
begin
  ScPath := ExpandConstant('{sys}\sc.exe');
  ServiceExePath := ExpandConstant('{app}\{#MyServiceExeName}');

  if not FileExists(ServiceExePath) then
    exit;

  BinPath := '""' + ServiceExePath + '" --service"';

  RunHidden(ScPath, 'create ' + ServiceName + ' start= auto DisplayName= "' + ServiceDisplayName + '" binPath= ' + BinPath);
  RunHidden(ScPath, 'description ' + ServiceName + ' "Context Menu Manager Plus elevated backend service"');
  RunHidden(ScPath, 'start ' + ServiceName);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAndDeleteServiceIfPresent();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Response: Integer;
begin
  case CurUninstallStep of
    usUninstall:
      begin
        StopAndDeleteServiceIfPresent();
        Response := MsgBox(
          '是否同时删除本地数据？' + #13#10 + #13#10 +
          '包括：配置、日志、缓存、状态库和删除备份。' + #13#10 +
          '选择“否”将保留这些数据。',
          mbConfirmation,
          MB_YESNO or MB_DEFBUTTON2);

        if Response = IDYES then
        begin
          DelTree(ExpandConstant('{localappdata}\ContextMenuMgr'), True, True, True);
          DelTree(ExpandConstant('{commonappdata}\ContextMenuMgr'), True, True, True);
        end;
      end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallAndStartService();
end;

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: postinstall shellexec skipifdoesntexist
