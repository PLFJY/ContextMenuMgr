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

#ifndef MyDistributionMode
  #error MyDistributionMode must be explicitly supplied
#endif
#if (MyDistributionMode != "self-contained") && (MyDistributionMode != "framework-dependent")
  #error Unsupported MyDistributionMode
#endif
#ifndef MyArchitecture
  #error MyArchitecture must be explicitly supplied
#endif
#if (MyArchitecture != "win-x86") && (MyArchitecture != "win-x64") && (MyArchitecture != "win-arm64")
  #error Unsupported MyArchitecture
#endif
#if ((MyArchitecture == "win-x86") && ((MyArchitecturesAllowed != "x86compatible") || (MyArchitecturesInstallIn64BitMode != ""))) || ((MyArchitecture == "win-x64") && ((MyArchitecturesAllowed != "x64compatible") || (MyArchitecturesInstallIn64BitMode != "x64compatible"))) || ((MyArchitecture == "win-arm64") && ((MyArchitecturesAllowed != "arm64") || (MyArchitecturesInstallIn64BitMode != "arm64")))
  #error Architecture and Inno registry/install mode disagree
#endif
#ifndef MyRuntimeInventory
  #error MyRuntimeInventory must be generated from published runtime-pack assets
#endif
#ifndef MyUseDotNetDependencyInstaller
  #error MyUseDotNetDependencyInstaller must be explicitly supplied
#endif
#if ((MyDistributionMode == "self-contained") && (MyUseDotNetDependencyInstaller != "0")) || ((MyDistributionMode == "framework-dependent") && (MyUseDotNetDependencyInstaller != "1"))
  #error Deployment mode and dependency installer disagree
#endif

#if MyUseDotNetDependencyInstaller == "1"
  #include "InnoDependencyInstaller\CodeDependencies.iss"
#endif

#define MyAppName "Context Menu Manager Plus"
#define MyAppUserModelID "PLFJY.ContextMenuManagerPlus"
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
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesetraditional"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkablealone

[Files]
Source: "{#MyAppBuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; BeforeInstall: CleanupPreviousRuntime

[INI]
Filename: "{app}\ContextMenuMgr.deployment.ini"; Section: "Deployment"; Key: "AppId"; String: "{#MyAppId}"
Filename: "{app}\ContextMenuMgr.deployment.ini"; Section: "Deployment"; Key: "DistributionMode"; String: "{#MyDistributionMode}"
Filename: "{app}\ContextMenuMgr.deployment.ini"; Section: "Deployment"; Key: "Architecture"; String: "{#MyArchitecture}"
Filename: "{app}\ContextMenuMgr.deployment.ini"; Section: "Deployment"; Key: "InstallPath"; String: "{app}"
Filename: "{app}\ContextMenuMgr.deployment.ini"; Section: "Deployment"; Key: "RuntimeFiles"; String: "{code:GetIncomingRuntimeFiles}"
Filename: "{app}\ContextMenuMgr.deployment.ini"; Section: "Deployment"; Key: "RuntimeFilesSHA256"; String: "{code:GetIncomingRuntimeHash}"
Filename: "{app}\ContextMenuMgr.deployment.ini"; Section: "Deployment"; Key: "Complete"; String: "0"

[Code]
const
  ServiceName = 'ContextMenuManagerPlusService';
  LegacyServiceName = 'ContextMenuManagerService';
  ServiceDisplayName = 'Context Menu Manager Plus Service';

#include "UpgradeDeployment.iss"

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

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; AppUserModelID: "{#MyAppUserModelID}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; AppUserModelID: "{#MyAppUserModelID}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: postinstall shellexec skipifdoesntexist; Check: DeploymentSucceeded
