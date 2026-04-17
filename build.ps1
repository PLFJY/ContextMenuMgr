#Requires -Version 5.1
param(
    [string] $Configuration = "Release",
    [string] $AppId = "45156332-3408-47B7-B5D2-2567E5888F64",
    [string[]] $Platforms = @("win-x64", "win-x86", "win-arm64"),
    [string[]] $DistributionModes = @("self-contained", "framework-dependent"),
    [int] $MaxParallel = 3
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

    Write-Host ">> $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ }

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

function Ensure-FileExists {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description is missing: $Path"
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

    throw "Unable to locate ISCC.exe. Install Inno Setup 6 or place it under Installer\Inno Setup 6\ISCC.exe."
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

$buildTasks = New-Object System.Collections.Generic.List[object]
foreach ($distributionMode in $DistributionModes) {
    foreach ($platform in $Platforms) {
        $buildTasks.Add([pscustomobject]@{
            DistributionMode = $distributionMode
            Platform = $platform
        }) | Out-Null
    }
}

$jobInitScript = {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    function Invoke-External {
        param(
            [Parameter(Mandatory)] [string] $FilePath,
            [Parameter()] [string[]] $Arguments = @(),
            [Parameter()] [string] $ErrorMessage = "External command failed."
        )

        Write-Host ">> $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
        & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ }

        if ($LASTEXITCODE -ne 0) {
            throw "$ErrorMessage (ExitCode=$LASTEXITCODE): $FilePath $($Arguments -join ' ')"
        }
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

    function Invoke-BuildTarget {
        param(
            [Parameter(Mandatory)] [string] $Configuration,
            [Parameter(Mandatory)] [string] $DistributionMode,
            [Parameter(Mandatory)] [string] $Platform,
            [Parameter(Mandatory)] [string] $FrontendProject,
            [Parameter(Mandatory)] [string] $BackendProject,
            [Parameter(Mandatory)] [string] $TrayHostProject,
            [Parameter(Mandatory)] [string] $PublishRoot,
            [Parameter(Mandatory)] [string] $DistRoot,
            [Parameter(Mandatory)] [string] $Version,
            [Parameter(Mandatory)] [string] $IsccPath,
            [Parameter(Mandatory)] [string] $InstallerIss,
            [Parameter(Mandatory)] [string] $AppId,
            [Parameter(Mandatory)] [string] $NuGetConfig
        )

        $distributionOptions = Get-DistributionModeOptions -DistributionMode $DistributionMode
        $runtimeIdentifier = Get-RuntimeIdentifier -Platform $Platform
        $platformLabel = $Platform

        $publishDir = Join-Path $PublishRoot (Join-Path $DistributionMode $Platform)
        $taskArtifactsRoot = Join-Path $PublishRoot (Join-Path "_artifacts" (Join-Path $DistributionMode $Platform))

        if (Test-Path -LiteralPath $publishDir) {
            Remove-Item -LiteralPath $publishDir -Recurse -Force
        }

        New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
        New-Item -ItemType Directory -Path $taskArtifactsRoot -Force | Out-Null

        $frontendRestoreArguments = @(
            "restore", $FrontendProject,
            "-r", $runtimeIdentifier,
            "--configfile", $NuGetConfig,
            "--artifacts-path", $taskArtifactsRoot
        )

        $frontendPublishArguments = @(
            "publish", $FrontendProject,
            "-c", $Configuration,
            "-r", $runtimeIdentifier,
            "--self-contained", $distributionOptions.SelfContained,
            "--no-restore",
            "--artifacts-path", $taskArtifactsRoot,
            "-p:UseAppHost=true",
            "-o", $publishDir
        )

        $backendRestoreArguments = @(
            "restore", $BackendProject,
            "-r", $runtimeIdentifier,
            "--configfile", $NuGetConfig,
            "--artifacts-path", $taskArtifactsRoot
        )

        $backendPublishArguments = @(
            "publish", $BackendProject,
            "-c", $Configuration,
            "-r", $runtimeIdentifier,
            "--self-contained", $distributionOptions.SelfContained,
            "--no-restore",
            "--artifacts-path", $taskArtifactsRoot,
            "-p:UseAppHost=true",
            "-o", $publishDir
        )

        $trayHostRestoreArguments = @(
            "restore", $TrayHostProject,
            "-r", $runtimeIdentifier,
            "--configfile", $NuGetConfig,
            "--artifacts-path", $taskArtifactsRoot
        )

        $trayHostPublishArguments = @(
            "publish", $TrayHostProject,
            "-c", $Configuration,
            "-r", $runtimeIdentifier,
            "--self-contained", $distributionOptions.SelfContained,
            "--no-restore",
            "--artifacts-path", $taskArtifactsRoot,
            "-p:UseAppHost=true",
            "-o", $publishDir
        )

        Invoke-External -FilePath "dotnet" -Arguments $frontendRestoreArguments -ErrorMessage "dotnet restore failed for frontend ($platformLabel, $DistributionMode)"
        Invoke-External -FilePath "dotnet" -Arguments $frontendPublishArguments -ErrorMessage "dotnet publish failed for frontend ($platformLabel, $DistributionMode)"

        Invoke-External -FilePath "dotnet" -Arguments $backendRestoreArguments -ErrorMessage "dotnet restore failed for backend ($platformLabel, $DistributionMode)"
        Invoke-External -FilePath "dotnet" -Arguments $backendPublishArguments -ErrorMessage "dotnet publish failed for backend ($platformLabel, $DistributionMode)"

        Invoke-External -FilePath "dotnet" -Arguments $trayHostRestoreArguments -ErrorMessage "dotnet restore failed for tray host ($platformLabel, $DistributionMode)"
        Invoke-External -FilePath "dotnet" -Arguments $trayHostPublishArguments -ErrorMessage "dotnet publish failed for tray host ($platformLabel, $DistributionMode)"

        Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManager.exe") -Description "Frontend executable"
        Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManager.Service.exe") -Description "Backend service executable"
        Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManager.Service.dll") -Description "Backend service DLL"
        Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManager.TrayHost.exe") -Description "Tray host executable"

        $installerOptions = Get-InstallerArchitectureOptions -Platform $Platform
        $setupBaseName = "ContextMenuManager-$Version-$Platform-$($distributionOptions.InstallerSuffix)-Setup"

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

        Invoke-External -FilePath $IsccPath -Arguments $isccArguments -ErrorMessage "Inno Setup packaging failed for $platformLabel ($DistributionMode)"

        $installerPath = Join-Path $DistRoot ($setupBaseName + ".exe")
        Ensure-FileExists -Path $installerPath -Description "Installer package"

        return $installerPath
    }
}

$installers = New-Object System.Collections.Generic.List[string]
$runningJobs = @()
$failed = $false

foreach ($task in $buildTasks) {
    while (@($runningJobs | Where-Object { $_.State -eq 'Running' }).Count -ge $MaxParallel) {
        $finishedJob = Wait-Job -Job $runningJobs -Any

        try {
            $jobOutput = Receive-Job -Job $finishedJob -ErrorAction Stop
            foreach ($item in @($jobOutput)) {
                if (-not [string]::IsNullOrWhiteSpace($item)) {
                    $installers.Add([string] $item) | Out-Null
                }
            }
        }
        catch {
            $failed = $true
            Write-Host ""
            Write-Host "Parallel build job failed: $($finishedJob.Name)" -ForegroundColor Red
            Write-Host $_
        }
        finally {
            Remove-Job -Job $finishedJob -Force
            $runningJobs = @($runningJobs | Where-Object { $_.Id -ne $finishedJob.Id })
        }

        if ($failed) {
            break
        }
    }

    if ($failed) {
        break
    }

    $jobName = "$($task.DistributionMode)-$($task.Platform)"
    $job = Start-Job -Name $jobName -InitializationScript $jobInitScript -ScriptBlock {
        param(
            $Configuration,
            $DistributionMode,
            $Platform,
            $FrontendProject,
            $BackendProject,
            $TrayHostProject,
            $PublishRoot,
            $DistRoot,
            $Version,
            $IsccPath,
            $InstallerIss,
            $AppId,
            $NuGetConfig
        )

        Invoke-BuildTarget `
            -Configuration $Configuration `
            -DistributionMode $DistributionMode `
            -Platform $Platform `
            -FrontendProject $FrontendProject `
            -BackendProject $BackendProject `
            -TrayHostProject $TrayHostProject `
            -PublishRoot $PublishRoot `
            -DistRoot $DistRoot `
            -Version $Version `
            -IsccPath $IsccPath `
            -InstallerIss $InstallerIss `
            -AppId $AppId `
            -NuGetConfig $NuGetConfig
    } -ArgumentList @(
        $Configuration,
        $task.DistributionMode,
        $task.Platform,
        $FrontendProject,
        $BackendProject,
        $TrayHostProject,
        $PublishRoot,
        $DistRoot,
        $Version,
        $IsccPath,
        $InstallerIss,
        $AppId,
        $NuGetConfig
    )

    $runningJobs += $job
}

while (-not $failed -and $runningJobs.Count -gt 0) {
    $finishedJob = Wait-Job -Job $runningJobs -Any

    try {
        $jobOutput = Receive-Job -Job $finishedJob -ErrorAction Stop
        foreach ($item in @($jobOutput)) {
            if (-not [string]::IsNullOrWhiteSpace($item)) {
                $installers.Add([string] $item) | Out-Null
            }
        }
    }
    catch {
        $failed = $true
        Write-Host ""
        Write-Host "Parallel build job failed: $($finishedJob.Name)" -ForegroundColor Red
        Write-Host $_
    }
    finally {
        Remove-Job -Job $finishedJob -Force
        $runningJobs = @($runningJobs | Where-Object { $_.Id -ne $finishedJob.Id })
    }
}

if ($failed) {
    throw "One or more parallel build jobs failed."
}

$manifestPath = Join-Path $DistRoot "artifacts.txt"
$installers = $installers | Sort-Object
$installers | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host ""
Write-Host "Build completed successfully." -ForegroundColor Green
Write-Host "Version: $Version"
Write-Host "AppId: $AppId"
Write-Host "Installers:"
foreach ($installer in $installers) {
    Write-Host "  $installer"
}