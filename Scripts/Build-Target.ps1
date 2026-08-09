#Requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [ValidateSet('installer', 'portable')]
    [string] $Kind,

    [Parameter(Mandatory)]
    [ValidateSet('Debug', 'Release', 'Beta')]
    [string] $Configuration,

    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-x86', 'win-arm64', 'anycpu')]
    [string] $Platform,

    [Parameter(Mandatory)]
    [ValidateSet('self-contained', 'framework-dependent')]
    [string] $DistributionMode,

    [string] $AppId = '45156332-3408-47B7-B5D2-2567E5888F64',

    [string] $Version = '',

    [string] $LogPath = '',

    [string] $PublishRoot = '',

    [string] $DistRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$transcriptStarted = $false

try {
    $scriptDir = $PSScriptRoot
    if (-not $scriptDir) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }

    $repoRoot = (Resolve-Path -LiteralPath (Join-Path $scriptDir '..')).Path
    Import-Module (Join-Path $scriptDir 'Build.Common.psm1') -Force -DisableNameChecking
    Set-Location -LiteralPath $repoRoot

    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        $logDirectory = Split-Path -Parent $LogPath
        if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
            New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        }

        Start-Transcript -Path $LogPath -Force | Out-Null
        $transcriptStarted = $true
    }

    $solutionPath = Join-Path $repoRoot 'ContextMenuMgr.slnx'
    $frontendProject = Join-Path $repoRoot 'ContextMenuMgr.Frontend\ContextMenuMgr.Frontend.csproj'
    $backendProject = Join-Path $repoRoot 'ContextMenuMgr.Backend\ContextMenuMgr.Backend.csproj'
    $trayHostProject = Join-Path $repoRoot 'ContextMenuMgr.TrayHost\ContextMenuMgr.TrayHost.csproj'
    $probeHostProject = Join-Path $repoRoot 'ContextMenuMgr.ProbeHost\ContextMenuMgr.ProbeHost.vcxproj'
    $nuGetConfig = Join-Path $repoRoot 'NuGet.Config'
    $installerIss = Join-Path $repoRoot 'Installer\build_Installer.iss'
    if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
        $publishRoot = Join-Path $repoRoot 'build\publish'
    }
    elseif ([System.IO.Path]::IsPathRooted($PublishRoot)) {
        $publishRoot = $PublishRoot
    }
    else {
        $publishRoot = Join-Path $repoRoot $PublishRoot
    }

    if ([string]::IsNullOrWhiteSpace($DistRoot)) {
        $distRoot = Join-Path $repoRoot 'build\dist'
    }
    elseif ([System.IO.Path]::IsPathRooted($DistRoot)) {
        $distRoot = $DistRoot
    }
    else {
        $distRoot = Join-Path $repoRoot $DistRoot
    }
    $artifactProductName = 'ContextMenuMgrPlus'

    Ensure-FileExists -Path $solutionPath -Description 'Solution'
    Ensure-FileExists -Path $frontendProject -Description 'Frontend project'
    Ensure-FileExists -Path $backendProject -Description 'Backend project'
    Ensure-FileExists -Path $trayHostProject -Description 'Tray host project'
    Ensure-FileExists -Path $probeHostProject -Description 'Native ProbeHost project'
    Ensure-FileExists -Path $nuGetConfig -Description 'NuGet config'
    Ensure-FileExists -Path $installerIss -Description 'Inno Setup script'

    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = Get-BuildVersion -ProjectPath $frontendProject -Configuration $Configuration
    }

    New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $distRoot -Force | Out-Null

    $artifacts = @()
    switch ($Kind) {
        'installer' {
            if ($Platform -eq 'anycpu') {
                throw 'Installer targets require platform win-x64, win-x86, or win-arm64.'
            }

            $isccPath = Resolve-IsccPath -RepoRoot $repoRoot
            Ensure-FileExists -Path $isccPath -Description 'Inno Setup compiler'

            $artifacts = Invoke-InstallerBuildTarget `
                -Configuration $Configuration `
                -DistributionMode $DistributionMode `
                -Platform $Platform `
                -FrontendProject $frontendProject `
                -BackendProject $backendProject `
                -TrayHostProject $trayHostProject `
                -ProbeHostProject $probeHostProject `
                -PublishRoot $publishRoot `
                -DistRoot $distRoot `
                -Version $Version `
                -ArtifactProductName $artifactProductName `
                -IsccPath $isccPath `
                -InstallerIss $installerIss `
                -AppId $AppId `
                -NuGetConfig $nuGetConfig
        }
        'portable' {
            if ($DistributionMode -ne 'framework-dependent' -or $Platform -ne 'anycpu') {
                throw 'Portable targets currently support only framework-dependent anycpu.'
            }

            $artifacts = Invoke-PortableFrameworkDependentBuildTarget `
                -Configuration $Configuration `
                -FrontendProject $frontendProject `
                -BackendProject $backendProject `
                -TrayHostProject $trayHostProject `
                -ProbeHostProject $probeHostProject `
                -PublishRoot $publishRoot `
                -DistRoot $distRoot `
                -Version $Version `
                -ArtifactProductName $artifactProductName `
                -NuGetConfig $nuGetConfig
        }
        default {
            throw "Unsupported build target kind '$Kind'."
        }
    }

    foreach ($artifact in @($artifacts)) {
        Write-Output $artifact
    }
}
catch {
    Write-Error $_
    exit 1
}
finally {
    if ($transcriptStarted) {
        Stop-Transcript | Out-Null
    }
}
