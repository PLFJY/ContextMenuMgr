#Requires -Version 5.1
param(
    [string] $Configuration = "Release",
    [string] $AppId = "45156332-3408-47B7-B5D2-2567E5888F64"
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

$ScriptDir = Get-ScriptDirectory
Set-Location -Path $ScriptDir

$RepoRoot = $ScriptDir
$SolutionPath = Join-Path $RepoRoot "ContextMenuMgr.slnx"
$FrontendProject = Join-Path $RepoRoot "ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj"
$BackendProject = Join-Path $RepoRoot "ContextMenuMgr.Backend\ContextMenuMgr.Backend.csproj"
$PublishRoot = Join-Path $RepoRoot "build\ContextMenuManager"
$MainExe = Join-Path $PublishRoot "ContextMenuManager.exe"
$ServiceExe = Join-Path $PublishRoot "ContextMenuManager.Service.exe"
$ServiceDll = Join-Path $PublishRoot "ContextMenuManager.Service.dll"
$IsccPath = Join-Path $RepoRoot "Installer\Inno Setup 6\ISCC.exe"
$InstallerIss = Join-Path $RepoRoot "Installer\build_Installer.iss"

if (Test-Path -LiteralPath $PublishRoot) {
    Remove-Item -LiteralPath $PublishRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $PublishRoot | Out-Null

Invoke-External -FilePath "dotnet" -Arguments @(
    "restore",
    $SolutionPath,
    "--configfile", (Join-Path $RepoRoot "NuGet.Config")
) -ErrorMessage "dotnet restore failed"

Invoke-External -FilePath "dotnet" -Arguments @(
    "publish", $FrontendProject,
    "-c", $Configuration,
    "-o", $PublishRoot,
    "--no-restore"
) -ErrorMessage "dotnet publish failed"

Invoke-External -FilePath "dotnet" -Arguments @(
    "publish", $BackendProject,
    "-c", $Configuration,
    "-o", $PublishRoot,
    "--no-restore"
) -ErrorMessage "dotnet publish for backend failed"

if (-not (Test-Path -LiteralPath $MainExe)) {
    Write-Host "Build output missing: $MainExe" -ForegroundColor Red
    Get-ChildItem -Path $PublishRoot -Recurse | Format-Table -AutoSize
    throw "dotnet publish finished but the frontend executable was not produced."
}

if (-not (Test-Path -LiteralPath $ServiceExe)) {
    Write-Host "Build output missing: $ServiceExe" -ForegroundColor Red
    Get-ChildItem -Path $PublishRoot -Recurse | Format-Table -AutoSize
    throw "dotnet publish finished but the backend service executable was not produced."
}

if (-not (Test-Path -LiteralPath $ServiceDll)) {
    Write-Host "Build output missing: $ServiceDll" -ForegroundColor Red
    Get-ChildItem -Path $PublishRoot -Recurse | Format-Table -AutoSize
    throw "dotnet publish finished but the backend service DLL was not produced."
}

if (-not (Test-Path -LiteralPath $IsccPath)) {
    throw "ISCC.exe not found at: $IsccPath"
}

if (-not (Test-Path -LiteralPath $InstallerIss)) {
    throw ".iss script not found at: $InstallerIss"
}

Invoke-External -FilePath $IsccPath -Arguments @(
    "/DMyAppId=$AppId",
    "/DMyAppBuildDir=$PublishRoot",
    $InstallerIss
) -ErrorMessage "Inno Setup packaging failed"
