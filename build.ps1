#Requires -Version 5.1
param(
    [string] $Configuration = "Release",
    [string] $AppId = "45156332-3408-47B7-B5D2-2567E5888F64",
    [string[]] $Platforms = @("win-x64", "win-x86", "win-arm64"),
    [string[]] $DistributionModes = @("self-contained", "framework-dependent")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ScriptDirectory {
    if ($PSScriptRoot) { return $PSScriptRoot }

    $path = $MyInvocation.MyCommand.Path
    if ($path) { return (Split-Path -Parent $path) }

    throw "Cannot determine script directory. Please run this as a .ps1 file."
}

function Invoke-External {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter()] [string[]] $Arguments = @(),
        [Parameter()] [string] $ErrorMessage = "External command failed."
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$ErrorMessage (ExitCode=$LASTEXITCODE): $FilePath $($Arguments -join ' ')"
    }
}

function Get-FrontendVersion {
    param([Parameter(Mandatory)] [string] $ProjectPath)

    [xml] $projectXml = Get-Content -LiteralPath $ProjectPath
    $propertyGroups = @($projectXml.Project.PropertyGroup)
    foreach ($group in $propertyGroups) {
        if ($group.InformationalVersion) {
            return [string] $group.InformationalVersion
        }
    }

    foreach ($group in $propertyGroups) {
        if ($group.FileVersion) {
            return [string] $group.FileVersion
        }
    }

    return "0.0.0"
}

function Get-RuntimeIdentifier {
    param([Parameter(Mandatory)] [string] $Platform)

    switch ($Platform.ToLowerInvariant()) {
        "win-x64" { return "win-x64" }
        "win-x86" { return "win-x86" }
        "win-arm64" { return "win-arm64" }
        default { throw "Unsupported platform '$Platform'. Supported values: win-x64, win-x86, win-arm64." }
    }
}

function Ensure-FileExists {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description is missing: $Path"
    }
}

function Get-InstallerArchitectureOptions {
    param([Parameter(Mandatory)] [string] $Platform)

    switch ($Platform.ToLowerInvariant()) {
        "win-x64" {
            return @{
                Allowed = "x64compatible"
                InstallIn64BitMode = "x64compatible"
            }
        }
        "win-x86" {
            return @{
                Allowed = "x86compatible"
                InstallIn64BitMode = ""
            }
        }
        "win-arm64" {
            return @{
                Allowed = "arm64"
                InstallIn64BitMode = "arm64"
            }
        }
        default {
            throw "Unsupported platform '$Platform'. Supported values: win-x64, win-x86, win-arm64."
        }
    }
}

function Get-DistributionModeOptions {
    param([Parameter(Mandatory)] [string] $DistributionMode)

    switch ($DistributionMode.ToLowerInvariant()) {
        "self-contained" {
            return @{
                SelfContained = "true"
                InstallerSuffix = "self-contained"
                UseDotNetDependencyInstaller = "0"
            }
        }
        "framework-dependent" {
            return @{
                SelfContained = "false"
                InstallerSuffix = "framework-dependent"
                UseDotNetDependencyInstaller = "1"
            }
        }
        default {
            throw "Unsupported distribution mode '$DistributionMode'. Supported values: self-contained, framework-dependent."
        }
    }
}

function Resolve-IsccPath {
    param([Parameter(Mandatory)] [string] $RepoRoot)

    $candidates = @(
        (Join-Path $RepoRoot "Installer\Inno Setup 6\ISCC.exe"),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command -and $command.Source) {
        return $command.Source
    }

    throw "Unable to locate ISCC.exe. Install Inno Setup 6 or place it under Installer\\Inno Setup 6\\ISCC.exe."
}

$ScriptDir = Get-ScriptDirectory
Set-Location -Path $ScriptDir

$RepoRoot = $ScriptDir
$SolutionPath = Join-Path $RepoRoot "ContextMenuMgr.slnx"
$FrontendProject = Join-Path $RepoRoot "ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj"
$BackendProject = Join-Path $RepoRoot "ContextMenuMgr.Backend\ContextMenuMgr.Backend.csproj"
$TrayHostProject = Join-Path $RepoRoot "ContextMenuMgr.TrayHost\ContextMenuMgr.TrayHost.csproj"
$NuGetConfig = Join-Path $RepoRoot "NuGet.Config"
$PublishRoot = Join-Path $RepoRoot "build\publish"
$DistRoot = Join-Path $RepoRoot "build\dist"
$Version = Get-FrontendVersion -ProjectPath $FrontendProject
$IsccPath = Resolve-IsccPath -RepoRoot $RepoRoot
$InstallerIss = Join-Path $RepoRoot "Installer\build_Installer.iss"

if (Test-Path -LiteralPath $PublishRoot) {
    Remove-Item -LiteralPath $PublishRoot -Recurse -Force
}

if (Test-Path -LiteralPath $DistRoot) {
    Remove-Item -LiteralPath $DistRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $PublishRoot | Out-Null
New-Item -ItemType Directory -Path $DistRoot | Out-Null

Ensure-FileExists -Path $IsccPath -Description "Inno Setup compiler"
Ensure-FileExists -Path $InstallerIss -Description "Inno Setup script"

Invoke-External -FilePath "dotnet" -Arguments @(
    "restore",
    $SolutionPath,
    "--configfile", $NuGetConfig
) -ErrorMessage "dotnet restore failed"

$installers = New-Object System.Collections.Generic.List[string]

foreach ($distributionMode in $DistributionModes) {
    $distributionOptions = Get-DistributionModeOptions -DistributionMode $distributionMode
    $targetPlatforms = if ($distributionOptions.ContainsKey("IsPlatformSpecific") -and -not $distributionOptions.IsPlatformSpecific) {
        @("")
    }
    else {
        $Platforms
    }

    foreach ($platform in $targetPlatforms) {
        $isPlatformSpecific = -not [string]::IsNullOrWhiteSpace($platform)
        $publishDir = if ($isPlatformSpecific) {
            Join-Path $PublishRoot (Join-Path $distributionMode $platform)
        }
        else {
            Join-Path $PublishRoot $distributionMode
        }

        if (Test-Path -LiteralPath $publishDir) {
            Remove-Item -LiteralPath $publishDir -Recurse -Force
        }

        New-Item -ItemType Directory -Path $publishDir | Out-Null

        $frontendArguments = @(
            "publish", $FrontendProject,
            "-c", $Configuration,
            "--self-contained", $distributionOptions.SelfContained,
            "-p:UseAppHost=true",
            "-o", $publishDir
        )
        $backendArguments = @(
            "publish", $BackendProject,
            "-c", $Configuration,
            "--self-contained", $distributionOptions.SelfContained,
            "-p:UseAppHost=true",
            "-o", $publishDir
        )

        if ($isPlatformSpecific) {
            $runtimeIdentifier = Get-RuntimeIdentifier -Platform $platform
            $frontendArguments = @(
                "publish", $FrontendProject,
                "-c", $Configuration,
                "-r", $runtimeIdentifier,
                "--self-contained", $distributionOptions.SelfContained,
                "-p:UseAppHost=true",
                "-o", $publishDir
            )
            $backendArguments = @(
                "publish", $BackendProject,
                "-c", $Configuration,
                "-r", $runtimeIdentifier,
                "--self-contained", $distributionOptions.SelfContained,
                "-p:UseAppHost=true",
                "-o", $publishDir
            )
        }

        $platformLabel = if ($isPlatformSpecific) { $platform } else { "generic" }

        Invoke-External -FilePath "dotnet" -Arguments $frontendArguments -ErrorMessage "dotnet publish failed for frontend ($platformLabel, $distributionMode)"

        Invoke-External -FilePath "dotnet" -Arguments $backendArguments -ErrorMessage "dotnet publish failed for backend ($platformLabel, $distributionMode)"

        $trayHostArguments = @($backendArguments)
        $trayHostArguments[1] = $TrayHostProject
        Invoke-External -FilePath "dotnet" -Arguments $trayHostArguments -ErrorMessage "dotnet publish failed for tray host ($platformLabel, $distributionMode)"

        Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManager.exe") -Description "Frontend executable"
        Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManager.Service.exe") -Description "Backend service executable"
        Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManager.Service.dll") -Description "Backend service DLL"
        Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManager.TrayHost.exe") -Description "Tray host executable"

        if ($isPlatformSpecific) {
            $installerOptions = Get-InstallerArchitectureOptions -Platform $platform
            $setupBaseName = "ContextMenuManager-$Version-$platform-$($distributionOptions.InstallerSuffix)-Setup"
        }
        else {
            $installerOptions = @{
                Allowed = ""
                InstallIn64BitMode = ""
            }
            $setupBaseName = "ContextMenuManager-$Version-$($distributionOptions.InstallerSuffix)-Setup"
        }

        $isccArguments = @(
            "/DMyArchitecturesAllowed=$($installerOptions.Allowed)",
            "/DMyArchitecturesInstallIn64BitMode=$($installerOptions.InstallIn64BitMode)",
            "/DMyUseDotNetDependencyInstaller=$($distributionOptions.UseDotNetDependencyInstaller)",
            "/DMyAppId=$AppId",
            "/DMyAppBuildDir=$publishDir",
            "/DMyOutputDir=$DistRoot",
            "/DMyAppSetupName=$setupBaseName",
            $InstallerIss
        )

        Invoke-External -FilePath $IsccPath -Arguments $isccArguments -ErrorMessage "Inno Setup packaging failed for $platformLabel ($distributionMode)"

        $installerPath = Join-Path $DistRoot ($setupBaseName + ".exe")
        Ensure-FileExists -Path $installerPath -Description "Installer package"
        $installers.Add($installerPath) | Out-Null
    }
}

$manifestPath = Join-Path $DistRoot "artifacts.txt"
$installers | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host ""
Write-Host "Build completed successfully." -ForegroundColor Green
Write-Host "Version: $Version"
Write-Host "AppId: $AppId"
Write-Host "Installers:"
foreach ($installer in $installers) {
    Write-Host "  $installer"
}
