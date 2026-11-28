#Requires -Version 5.1
param(
    [ValidateSet('Debug', 'Release', 'Beta')]
    [string] $Configuration = 'Release',

    [string] $AppId = '45156332-3408-47B7-B5D2-2567E5888F64',

    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string[]] $Platforms = @('win-x64', 'win-x86', 'win-arm64'),

    [ValidateSet('self-contained', 'framework-dependent')]
    [string[]] $DistributionModes = @('self-contained', 'framework-dependent'),

    [int] $MaxParallel = 3
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-ProcessArgument {
    param([Parameter(Mandatory)] [string] $Value)

    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    return '"' + $Value.Replace('"', '\"') + '"'
}

function New-TargetName {
    param(
        [Parameter(Mandatory)] [object] $Task
    )

    return "$($Task.Kind)-$($Task.Platform)-$($Task.DistributionMode)"
}

function Start-BuildTargetProcess {
    param(
        [Parameter(Mandatory)] [object] $Task,
        [Parameter(Mandatory)] [string] $PowerShellPath,
        [Parameter(Mandatory)] [string] $BuildTargetScript,
        [Parameter(Mandatory)] [string] $Configuration,
        [Parameter(Mandatory)] [string] $AppId,
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [string] $LogPath,
        [Parameter(Mandatory)] [string] $PublishRoot,
        [Parameter(Mandatory)] [string] $DistRoot
    )

    $arguments = @(
        '-NoLogo',
        '-ExecutionPolicy', 'Bypass',
        '-File', $BuildTargetScript,
        '-Kind', $Task.Kind,
        '-Configuration', $Configuration,
        '-Platform', $Task.Platform,
        '-DistributionMode', $Task.DistributionMode,
        '-AppId', $AppId,
        '-Version', $Version,
        '-LogPath', $LogPath,
        '-PublishRoot', $PublishRoot,
        '-DistRoot', $DistRoot
    ) | ForEach-Object { ConvertTo-ProcessArgument -Value ([string] $_) }

    $processParameters = @{
        FilePath     = $PowerShellPath
        ArgumentList = $arguments
        WindowStyle  = 'Hidden'
        PassThru     = $true
    }

    # Preserve PATH when launching Microsoft Store PowerShell.
    # The Environment parameter requires PowerShell 7.4 or later.
    if ((Get-Command Start-Process).Parameters.ContainsKey('Environment')) {
        $processParameters['Environment'] = @{
            PATH = $env:Path
        }
    }

    $process = Start-Process @processParameters

    return $process
}

function Stop-ProcessTree {
    param([Parameter(Mandatory)] [int] $ProcessId)

    $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProcessId" -ErrorAction SilentlyContinue)
    foreach ($child in $children) {
        Stop-ProcessTree -ProcessId ([int] $child.ProcessId)
    }

    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
}

function Stop-StaleBuildTargetProcesses {
    param(
        [Parameter(Mandatory)] [string] $RepoRoot,
        [Parameter(Mandatory)] [int] $CurrentProcessId
    )

    $buildTargetMarker = (Join-Path $RepoRoot 'Scripts\Build-Target.ps1')
    $candidates = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProcessId -ne $CurrentProcessId -and
            $_.Name -in @('pwsh.exe', 'powershell.exe') -and
            $_.CommandLine -and
            $_.CommandLine.IndexOf($buildTargetMarker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0
        })

    foreach ($candidate in $candidates) {
        Write-Host "Stopping stale build target process $($candidate.ProcessId)." -ForegroundColor DarkGray
        Stop-ProcessTree -ProcessId ([int] $candidate.ProcessId)
    }
}

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}

$repoRoot = (Resolve-Path -LiteralPath $scriptDir).Path
$scriptsRoot = Join-Path $repoRoot 'Scripts'
$buildTargetScript = Join-Path $scriptsRoot 'Build-Target.ps1'
$commonModule = Join-Path $scriptsRoot 'Build.Common.psm1'

Import-Module $commonModule -Force -DisableNameChecking
Set-Location -LiteralPath $repoRoot

if ($MaxParallel -lt 1) {
    throw 'MaxParallel must be 1 or greater.'
}

$solutionPath = Join-Path $repoRoot 'ContextMenuMgr.slnx'
$frontendProject = Join-Path $repoRoot 'ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj'
$installerIss = Join-Path $repoRoot 'Installer\build_Installer.iss'
$publishRunName = "run-$([System.DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))-$PID"
$publishRoot = Join-Path $repoRoot (Join-Path 'build\publish-runs' $publishRunName)
$distRoot = Join-Path $repoRoot 'build\dist'
$logsRoot = Join-Path $repoRoot 'build\logs'

Ensure-FileExists -Path $solutionPath -Description 'Solution'
Ensure-FileExists -Path $frontendProject -Description 'Frontend project'
Ensure-FileExists -Path $installerIss -Description 'Inno Setup script'
Ensure-FileExists -Path $buildTargetScript -Description 'Build target script'

Stop-StaleBuildTargetProcesses -RepoRoot $repoRoot -CurrentProcessId $PID

Write-Host 'Stopping dotnet build servers...' -ForegroundColor DarkGray
$buildServerOutput = & dotnet build-server shutdown 2>&1
foreach ($line in @($buildServerOutput)) {
    Write-Host $line
}

if ($LASTEXITCODE -ne 0) {
    Write-Warning 'dotnet build-server shutdown failed; continuing with build output cleanup.'
}

Write-Host 'Cleaning build output...' -ForegroundColor DarkGray
Remove-DirectoryIfExists -Path $publishRoot
Remove-DirectoryIfExists -Path $distRoot
Remove-DirectoryIfExists -Path $logsRoot

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
New-Item -ItemType Directory -Path $logsRoot -Force | Out-Null

$version = Get-BuildVersion -ProjectPath $frontendProject -Configuration $Configuration

$tasks = New-Object System.Collections.Generic.List[object]
foreach ($distributionMode in $DistributionModes) {
    foreach ($platform in $Platforms) {
        $tasks.Add([pscustomobject]@{
            Kind = 'installer'
            Platform = $platform
            DistributionMode = $distributionMode
        }) | Out-Null
    }
}

$tasks.Add([pscustomobject]@{
    Kind = 'portable'
    Platform = 'anycpu'
    DistributionMode = 'framework-dependent'
}) | Out-Null

$pwshCommand = Get-Command pwsh -ErrorAction SilentlyContinue
if ($pwshCommand -and $pwshCommand.Source) {
    $powerShellPath = $pwshCommand.Source
}
else {
    $powerShellPath = (Get-Command powershell -ErrorAction Stop).Source
}

Write-Host "Version: $version"
Write-Host "Publish workspace: $publishRoot"
Write-Host "Logs: $logsRoot"
Write-Host "Starting $($tasks.Count) build targets with MaxParallel=$MaxParallel..."

$pending = New-Object System.Collections.Generic.Queue[object]
foreach ($task in $tasks) {
    $pending.Enqueue($task)
}

$running = New-Object System.Collections.Generic.List[object]
$completed = New-Object System.Collections.Generic.List[object]
$stopScheduling = $false

while ($pending.Count -gt 0 -or $running.Count -gt 0) {
    while (-not $stopScheduling -and $pending.Count -gt 0 -and $running.Count -lt $MaxParallel) {
        $task = $pending.Dequeue()
        $targetName = New-TargetName -Task $task
        $logPath = Join-Path $logsRoot "$targetName.log"

        Write-Host "Starting $targetName"
        $process = Start-BuildTargetProcess `
            -Task $task `
            -PowerShellPath $powerShellPath `
            -BuildTargetScript $buildTargetScript `
            -Configuration $Configuration `
            -AppId $AppId `
            -Version $version `
            -LogPath $logPath `
            -PublishRoot $publishRoot `
            -DistRoot $distRoot

        $running.Add([pscustomobject]@{
            Task = $task
            TargetName = $targetName
            Process = $process
            LogPath = $logPath
        }) | Out-Null
    }

    $finished = @($running | Where-Object { $_.Process.HasExited })
    foreach ($item in $finished) {
        $exitCode = $item.Process.ExitCode
        if ($exitCode -eq 0) {
            Write-Host "Completed $($item.TargetName)"
        }
        else {
            Write-Host "Failed $($item.TargetName) (ExitCode=$exitCode). Log: $($item.LogPath)" -ForegroundColor Red
            $stopScheduling = $true
            $pending.Clear()
        }

        $completed.Add([pscustomobject]@{
            TargetName = $item.TargetName
            ExitCode = $exitCode
            LogPath = $item.LogPath
        }) | Out-Null

        $running.Remove($item) | Out-Null
    }

    if ($stopScheduling -and $running.Count -gt 0) {
        $runningSnapshot = [object[]] $running.ToArray()
        foreach ($item in $runningSnapshot) {
            if (-not $item.Process.HasExited) {
                Write-Host "Stopping $($item.TargetName) after build failure. Log: $($item.LogPath)" -ForegroundColor Yellow
                Stop-ProcessTree -ProcessId $item.Process.Id
                $item.Process.WaitForExit()
            }

            $completed.Add([pscustomobject]@{
                TargetName = $item.TargetName
                ExitCode = -1
                LogPath = $item.LogPath
            }) | Out-Null

            $running.Remove($item) | Out-Null
        }
    }

    if ($running.Count -gt 0 -or $pending.Count -gt 0) {
        Start-Sleep -Seconds 1
    }
}

$failedTargets = @($completed | Where-Object { $_.ExitCode -ne 0 })
if ($failedTargets.Count -gt 0) {
    Write-Host ''
    Write-Host 'One or more build targets failed:' -ForegroundColor Red
    foreach ($failedTarget in $failedTargets) {
        Write-Host "  $($failedTarget.TargetName): $($failedTarget.LogPath)" -ForegroundColor Red
    }

    throw 'Build failed.'
}

$artifacts = @(Get-ChildItem -LiteralPath $distRoot -File |
    Where-Object { $_.Extension -in '.exe', '.zip' } |
    Sort-Object Name |
    ForEach-Object { $_.FullName })

if ($artifacts.Count -eq 0) {
    throw "No artifacts were produced in $distRoot."
}

$manifestPath = Join-Path $distRoot 'artifacts.txt'
$artifacts | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host ''
Write-Host 'Build completed successfully.' -ForegroundColor Green
Write-Host "Configuration: $Configuration"
Write-Host "Version: $version"
Write-Host "AppId: $AppId"
Write-Host 'Artifacts:'
foreach ($artifact in $artifacts) {
    Write-Host "  $artifact"
}
