#Requires -Version 5.1
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}

function Assert-True {
    param(
        [Parameter(Mandatory)] [bool] $Condition,
        [Parameter(Mandatory)] [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal {
    param(
        [AllowNull()] $Actual,
        [AllowNull()] $Expected,
        [Parameter(Mandatory)] [string] $Message
    )

    if ($Actual -ne $Expected) {
        throw "$Message Expected '$Expected', got '$Actual'."
    }
}

function Assert-WingetHeader {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $SchemaType,
        [Parameter(Mandatory)] [string] $CaseName
    )

    $lines = Get-Content -LiteralPath $Path
    $schemaHeader = "# yaml-language-server: `$schema=https://aka.ms/winget-manifest.$SchemaType.1.12.0.schema.json"

    Assert-True -Condition ($lines -contains '# Created with ContextMenuMgr package manager automation') -Message "$CaseName winget manifest is missing the creator header: $Path"
    Assert-True -Condition ($lines -contains $schemaHeader) -Message "$CaseName winget manifest is missing schema header '$schemaHeader': $Path"

    $firstContentLine = $lines |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_) -and
            -not $_.TrimStart().StartsWith('#')
        } |
        Select-Object -First 1

    Assert-True -Condition ($firstContentLine -match '^PackageIdentifier:') -Message "$CaseName first non-empty, non-comment winget line should start with PackageIdentifier: $Path"
}

function Write-Json {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] $Value
    )

    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function New-SampleAssets {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Tag,
        [Parameter(Mandatory)] [string] $AssetVersion
    )

    $hashA = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
    $hashB = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    $hashC = 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc'
    $hashD = 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'
    $hashE = 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'
    $hashF = 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff'

    Write-Json -Path $Path -Value ([ordered] @{
        releaseTag = $Tag
        assetVersion = $AssetVersion
        assets = [ordered] @{
            wingetX64 = [ordered] @{
                name = "ContextMenuMgrPlus-$AssetVersion-x64-self-contained-Setup.exe"
                url = "https://github.com/PLFJY/ContextMenuMgr/releases/download/$Tag/ContextMenuMgrPlus-$AssetVersion-x64-self-contained-Setup.exe"
                sha256 = $hashA
            }
            wingetX86 = [ordered] @{
                name = "ContextMenuMgrPlus-$AssetVersion-x86-self-contained-Setup.exe"
                url = "https://github.com/PLFJY/ContextMenuMgr/releases/download/$Tag/ContextMenuMgrPlus-$AssetVersion-x86-self-contained-Setup.exe"
                sha256 = $hashB
            }
            wingetArm64 = [ordered] @{
                name = "ContextMenuMgrPlus-$AssetVersion-arm64-self-contained-Setup.exe"
                url = "https://github.com/PLFJY/ContextMenuMgr/releases/download/$Tag/ContextMenuMgrPlus-$AssetVersion-arm64-self-contained-Setup.exe"
                sha256 = $hashC
            }
            scoopPortableX64 = [ordered] @{
                name = "ContextMenuMgrPlus-$AssetVersion-x64-self-contained-portable.zip"
                url = "https://github.com/PLFJY/ContextMenuMgr/releases/download/$Tag/ContextMenuMgrPlus-$AssetVersion-x64-self-contained-portable.zip"
                sha256 = $hashD
            }
            scoopPortableX86 = [ordered] @{
                name = "ContextMenuMgrPlus-$AssetVersion-x86-self-contained-portable.zip"
                url = "https://github.com/PLFJY/ContextMenuMgr/releases/download/$Tag/ContextMenuMgrPlus-$AssetVersion-x86-self-contained-portable.zip"
                sha256 = $hashE
            }
            scoopPortableArm64 = [ordered] @{
                name = "ContextMenuMgrPlus-$AssetVersion-arm64-self-contained-portable.zip"
                url = "https://github.com/PLFJY/ContextMenuMgr/releases/download/$Tag/ContextMenuMgrPlus-$AssetVersion-arm64-self-contained-portable.zip"
                sha256 = $hashF
            }
        }
    })
}

function Invoke-GenerationCase {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Tag,
        [Parameter(Mandatory)] [bool] $Prerelease,
        [Parameter(Mandatory)] [string] $PublishedAt,
        [Parameter(Mandatory)] [string] $ExpectedPackageVersion,
        [Parameter(Mandatory)] [string] $ExpectedWingetId,
        [Parameter(Mandatory)] [string] $ExpectedScoopFile,
        [string] $TargetChannel = ''
    )

    $caseRoot = Join-Path $Root $Name
    New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null

    $eventPath = Join-Path $caseRoot 'release-event.json'
    $metadataPath = Join-Path $caseRoot 'release-metadata.json'
    $assetPath = Join-Path $caseRoot 'assets.json'
    $scoopOut = Join-Path $caseRoot 'scoop'
    $wingetOut = Join-Path $caseRoot 'winget'
    $assetVersion = $Tag -replace '^[vV]', ''

    Write-Json -Path $eventPath -Value ([ordered] @{
        release = [ordered] @{
            tag_name = $Tag
            name = $Tag
            prerelease = $Prerelease
            published_at = $PublishedAt
            html_url = "https://github.com/PLFJY/ContextMenuMgr/releases/tag/$Tag"
        }
    })

    New-SampleAssets -Path $assetPath -Tag $Tag -AssetVersion $assetVersion

    $resolveArgs = @{
        ReleaseEventJson = $eventPath
        OutputPath = $metadataPath
    }
    if (-not [string]::IsNullOrWhiteSpace($TargetChannel)) {
        $resolveArgs['TargetChannel'] = $TargetChannel
    }
    & (Join-Path $scriptDir 'Resolve-PackageRelease.ps1') @resolveArgs
    & (Join-Path $scriptDir 'New-ScoopManifest.ps1') -ReleaseMetadataJson $metadataPath -AssetManifestJson $assetPath -OutputDirectory $scoopOut | Out-Null
    & (Join-Path $scriptDir 'New-WingetManifest.ps1') -ReleaseMetadataJson $metadataPath -AssetManifestJson $assetPath -OutputDirectory $wingetOut | Out-Null

    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    Assert-Equal -Actual $metadata.packageVersion -Expected $ExpectedPackageVersion -Message "$Name package version mismatch."
    Assert-Equal -Actual $metadata.wingetPackageIdentifier -Expected $ExpectedWingetId -Message "$Name winget PackageIdentifier mismatch."

    $scoopPath = Join-Path $scoopOut $ExpectedScoopFile
    Assert-True -Condition (Test-Path -LiteralPath $scoopPath) -Message "$Name Scoop manifest was not created."
    & (Join-Path $scriptDir 'Test-ScoopManifest.ps1') `
        -ManifestPath $scoopPath `
        -ExpectedAppName ([System.IO.Path]::GetFileNameWithoutExtension($ExpectedScoopFile)) `
        -ExpectedVersion $ExpectedPackageVersion

    $scoop = Get-Content -LiteralPath $scoopPath -Raw | ConvertFrom-Json
    Assert-Equal -Actual $scoop.license -Expected 'GPL-3.0-only' -Message "$Name Scoop license mismatch."
    Assert-Equal -Actual $scoop.persist -Expected 'Data' -Message "$Name Scoop persist mismatch."
    Assert-True -Condition ($scoop.PSObject.Properties.Name -notcontains 'url') -Message "$Name Scoop manifest must not have top-level url."
    Assert-True -Condition ($scoop.PSObject.Properties.Name -notcontains 'hash') -Message "$Name Scoop manifest must not have top-level hash."
    Assert-True -Condition (-not [string]::IsNullOrWhiteSpace([string] $scoop.architecture.'64bit'.url)) -Message "$Name Scoop 64bit URL is missing."
    Assert-True -Condition (-not [string]::IsNullOrWhiteSpace([string] $scoop.architecture.'32bit'.url)) -Message "$Name Scoop 32bit URL is missing."
    Assert-True -Condition (-not [string]::IsNullOrWhiteSpace([string] $scoop.architecture.arm64.url)) -Message "$Name Scoop arm64 URL is missing."
    Assert-True -Condition ([string] $scoop.architecture.'64bit'.url -match 'x64-self-contained-portable\.zip') -Message "$Name Scoop 64bit URL must point to x64 self-contained portable zip."
    Assert-True -Condition ([string] $scoop.architecture.'32bit'.url -match 'x86-self-contained-portable\.zip') -Message "$Name Scoop 32bit URL must point to x86 self-contained portable zip."
    Assert-True -Condition ([string] $scoop.architecture.arm64.url -match 'arm64-self-contained-portable\.zip') -Message "$Name Scoop arm64 URL must point to arm64 self-contained portable zip."
    Assert-True -Condition (($scoop.notes -join "`n") -notmatch '\.NET 10 Desktop Runtime') -Message "$Name Scoop notes must not mention .NET runtime requirement."

    if ($metadata.channel -eq 'beta' -and -not [bool]$metadata.prerelease) {
        $notesText = ($scoop.notes -join "`n")
        Assert-True -Condition ($notesText -match 'tracks the latest stable release') -Message "$Name Scoop notes must mention tracking stable release for stable-to-beta."
        Assert-True -Condition ($notesText -notmatch 'may contain regressions') -Message "$Name Scoop notes must not mention regressions for stable-to-beta."
    }

    $preInstall = ($scoop.pre_install -join "`n")
    if ($metadata.channel -eq 'beta') {
        Assert-True -Condition ($preInstall -match 'contextmenumgrplus\\current') -Message 'Scoop Beta manifest does not check the stable app.'
    }
    else {
        Assert-True -Condition ($preInstall -match 'contextmenumgrplus-beta\\current') -Message 'Scoop Stable manifest does not check the beta app.'
    }

    $versionManifestPath = Join-Path $wingetOut "$ExpectedWingetId.yaml"
    $zhCnLocaleManifestPath = Join-Path $wingetOut "$ExpectedWingetId.locale.zh-CN.yaml"
    $zhTwLocaleManifestPath = Join-Path $wingetOut "$ExpectedWingetId.locale.zh-TW.yaml"
    $enUsLocaleManifestPath = Join-Path $wingetOut "$ExpectedWingetId.locale.en-US.yaml"
    $installerManifestPath = Join-Path $wingetOut "$ExpectedWingetId.installer.yaml"

    foreach ($path in @($versionManifestPath, $zhCnLocaleManifestPath, $zhTwLocaleManifestPath, $enUsLocaleManifestPath, $installerManifestPath)) {
        Assert-True -Condition (Test-Path -LiteralPath $path) -Message "$Name missing winget manifest: $path"
        $content = Get-Content -LiteralPath $path -Raw
        Assert-True -Condition (-not [string]::IsNullOrWhiteSpace($content)) -Message "$Name winget manifest is empty: $path"
        Assert-True -Condition ($content -match 'ManifestVersion: 1\.12\.0') -Message "$Name winget manifest is missing ManifestVersion: $path"
    }

    Assert-WingetHeader -Path $versionManifestPath -SchemaType 'version' -CaseName $Name
    Assert-WingetHeader -Path $zhCnLocaleManifestPath -SchemaType 'defaultLocale' -CaseName $Name
    Assert-WingetHeader -Path $zhTwLocaleManifestPath -SchemaType 'locale' -CaseName $Name
    Assert-WingetHeader -Path $enUsLocaleManifestPath -SchemaType 'locale' -CaseName $Name
    Assert-WingetHeader -Path $installerManifestPath -SchemaType 'installer' -CaseName $Name

    $versionManifest = Get-Content -LiteralPath $versionManifestPath -Raw
    $zhCnLocaleManifest = Get-Content -LiteralPath $zhCnLocaleManifestPath -Raw
    $zhTwLocaleManifest = Get-Content -LiteralPath $zhTwLocaleManifestPath -Raw
    $enUsLocaleManifest = Get-Content -LiteralPath $enUsLocaleManifestPath -Raw
    $installerManifest = Get-Content -LiteralPath $installerManifestPath -Raw

    Assert-True -Condition ($versionManifest -match [regex]::Escape("PackageIdentifier: '$ExpectedWingetId'")) -Message "$Name version manifest has wrong PackageIdentifier."
    Assert-True -Condition ($versionManifest -match 'DefaultLocale: zh-CN') -Message "$Name version manifest does not use zh-CN as DefaultLocale."
    Assert-True -Condition ($zhCnLocaleManifest -match 'PackageLocale: zh-CN') -Message "$Name zh-CN locale manifest has wrong PackageLocale."
    Assert-True -Condition ($zhCnLocaleManifest -match 'ManifestType: defaultLocale') -Message "$Name zh-CN locale manifest is not defaultLocale."
    Assert-True -Condition ($zhTwLocaleManifest -match 'PackageLocale: zh-TW') -Message "$Name zh-TW locale manifest has wrong PackageLocale."
    Assert-True -Condition ($zhTwLocaleManifest -match 'ManifestType: locale') -Message "$Name zh-TW locale manifest is not locale."
    Assert-True -Condition ($enUsLocaleManifest -match 'PackageLocale: en-US') -Message "$Name en-US locale manifest has wrong PackageLocale."
    Assert-True -Condition ($enUsLocaleManifest -match 'ManifestType: locale') -Message "$Name en-US locale manifest is not locale."
    foreach ($localeContent in @($zhCnLocaleManifest, $zhTwLocaleManifest, $enUsLocaleManifest)) {
        Assert-True -Condition ($localeContent -match 'License: GPL-3\.0') -Message "$Name winget locale manifest has wrong license."
        Assert-True -Condition ($localeContent -match [regex]::Escape("PackageName: 'Context Menu Manager Plus'")) -Message "$Name winget PackageName should remain Context Menu Manager Plus."
    }
    Assert-True -Condition ($installerManifest -match 'Architecture: x64') -Message "$Name installer manifest is missing x64."
    Assert-True -Condition ($installerManifest -match 'Architecture: x86') -Message "$Name installer manifest is missing x86."
    Assert-True -Condition ($installerManifest -match 'Architecture: arm64') -Message "$Name installer manifest is missing arm64."
    Assert-True -Condition ($installerManifest -match 'x64-self-contained-Setup\.exe') -Message "$Name winget installer manifest must use x64 self-contained setup."
    Assert-True -Condition ($installerManifest -match 'x86-self-contained-Setup\.exe') -Message "$Name winget installer manifest must use x86 self-contained setup."
    Assert-True -Condition ($installerManifest -match 'arm64-self-contained-Setup\.exe') -Message "$Name winget installer manifest must use arm64 self-contained setup."

    foreach ($manifestPath in @($scoopPath, $versionManifestPath, $zhCnLocaleManifestPath, $zhTwLocaleManifestPath, $enUsLocaleManifestPath, $installerManifestPath)) {
        $manifestContent = Get-Content -LiteralPath $manifestPath -Raw
        Assert-True -Condition ($manifestContent -notmatch 'framework-dependent') -Message "$Name package-manager manifest references framework-dependent assets: $manifestPath"
    }

    Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json | Out-Null
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("ContextMenuMgr-package-manager-tests-" + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $root | Out-Null

try {
    Invoke-GenerationCase `
        -Root $root `
        -Name 'stable' `
        -Tag 'v1.7.3' `
        -Prerelease $false `
        -PublishedAt '2026-07-04T13:58:22Z' `
        -ExpectedPackageVersion '1.7.3' `
        -ExpectedWingetId 'PLFJY.ContextMenuMgrPlus' `
        -ExpectedScoopFile 'contextmenumgrplus.json'

    Invoke-GenerationCase `
        -Root $root `
        -Name 'beta' `
        -Tag 'v1.7.3-Beta+abcdef0' `
        -Prerelease $true `
        -PublishedAt '2026-07-04T13:58:22Z' `
        -ExpectedPackageVersion '1.7.3-beta.20260704135822' `
        -ExpectedWingetId 'PLFJY.ContextMenuMgrPlus.Beta' `
        -ExpectedScoopFile 'contextmenumgrplus-beta.json'

    Invoke-GenerationCase `
        -Root $root `
        -Name 'stable-to-beta' `
        -Tag 'v1.7.3' `
        -Prerelease $false `
        -PublishedAt '2026-07-05T10:00:00Z' `
        -ExpectedPackageVersion '1.7.3' `
        -ExpectedWingetId 'PLFJY.ContextMenuMgrPlus.Beta' `
        -ExpectedScoopFile 'contextmenumgrplus-beta.json' `
        -TargetChannel 'beta'

    Write-Host "Package manager script tests passed. Fixture root: $root"
}
catch {
    Write-Error $_
    exit 1
}
