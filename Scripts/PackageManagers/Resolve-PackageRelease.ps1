#Requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [string] $ReleaseEventJson,

    [string] $OutputPath = '',

    [string] $GitHubOutput = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-JsonFile {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Release event JSON was not found: $Path"
    }

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-JsonValue {
    param(
        [Parameter(Mandatory)] $Object,
        [Parameter(Mandatory)] [string[]] $Names
    )

    foreach ($name in $Names) {
        if ($null -ne $Object.PSObject.Properties[$name]) {
            return $Object.$name
        }
    }

    return $null
}

function ConvertTo-Bool {
    param([AllowNull()] $Value)

    if ($null -eq $Value) {
        return $false
    }

    if ($Value -is [bool]) {
        return $Value
    }

    return [System.Convert]::ToBoolean($Value, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-StableBaseVersion {
    param([Parameter(Mandatory)] [string] $AssetVersion)

    $candidate = $AssetVersion -replace '\+.*$', ''
    $candidate = $candidate -replace '(?i)[-\.]?(beta|preview|pre|rc|alpha)([-\.]?\d+)?$', ''

    if ($candidate -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        throw "Cannot parse base version from prerelease asset version '$AssetVersion'."
    }

    return $candidate
}

function ConvertTo-PackageStamp {
    param([Parameter(Mandatory)] [string] $PublishedAt)

    try {
        $dto = [System.DateTimeOffset]::Parse(
            $PublishedAt,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
    }
    catch {
        throw "Cannot parse release published_at value '$PublishedAt'."
    }

    return $dto.UtcDateTime.ToString('yyyyMMddHHmmss', [System.Globalization.CultureInfo]::InvariantCulture)
}

$event = Read-JsonFile -Path $ReleaseEventJson
$release = $event
if ($null -ne $event.PSObject.Properties['release']) {
    $release = $event.release
}

$tagName = [string] (Get-JsonValue -Object $release -Names @('tag_name', 'tagName'))
$releaseName = [string] (Get-JsonValue -Object $release -Names @('name'))
$publishedAt = [string] (Get-JsonValue -Object $release -Names @('published_at', 'publishedAt'))
$htmlUrl = [string] (Get-JsonValue -Object $release -Names @('html_url', 'url'))
$prerelease = ConvertTo-Bool -Value (Get-JsonValue -Object $release -Names @('prerelease', 'isPrerelease'))

if ([string]::IsNullOrWhiteSpace($tagName)) {
    throw 'Release tag_name is missing.'
}

if ([string]::IsNullOrWhiteSpace($publishedAt)) {
    throw 'Release published_at is missing.'
}

$assetVersion = $tagName -replace '^[vV]', ''
if ($assetVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?([\-+].+)?$') {
    throw "Cannot parse asset version from release tag '$tagName'."
}

# Channel resolution is fixed and derived only from the GitHub Release
# prerelease flag:
#   - GitHub Pre-release (prerelease=true)  -> beta channel
#   - GitHub Stable Release (prerelease=false) -> stable channel
# Beta package-manager manifests are generated only from GitHub Pre-releases.
# There is no override and no way to force a Stable Release into the Beta
# channel; a Stable Release never produces Beta manifests.
if ($prerelease) {
    $channel = 'beta'
    $wingetPackageIdentifier = 'PLFJY.ContextMenuMgrPlus.Beta'
    $wingetPackageName = 'Context Menu Manager Plus Beta'
    $scoopApp = 'contextmenumgrplus-beta'
    $scoopManifestFile = 'contextmenumgrplus-beta.json'
    $scoopShortcutName = 'Context Menu Manager Plus Beta'

    # Beta prerelease version includes a publish-time stamp for deterministic
    # ordering within the Beta channel.
    $baseVersion = Get-StableBaseVersion -AssetVersion $assetVersion
    $publishedStamp = ConvertTo-PackageStamp -PublishedAt $publishedAt
    $packageVersion = "$baseVersion-beta.$publishedStamp"
}
else {
    $channel = 'stable'
    if ($assetVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        throw "Stable release version '$assetVersion' must be a plain semantic version."
    }

    $baseVersion = $assetVersion
    $publishedStamp = ''
    $packageVersion = $assetVersion
    $wingetPackageIdentifier = 'PLFJY.ContextMenuMgrPlus'
    $wingetPackageName = 'Context Menu Manager Plus'
    $scoopApp = 'contextmenumgrplus'
    $scoopManifestFile = 'contextmenumgrplus.json'
    $scoopShortcutName = 'Context Menu Manager Plus'
}

if ([string]::IsNullOrWhiteSpace($releaseName)) {
    $releaseName = $tagName
}

if ([string]::IsNullOrWhiteSpace($htmlUrl)) {
    $htmlUrl = "https://github.com/PLFJY/ContextMenuMgr/releases/tag/$tagName"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Join-Path (Get-Location) 'build') 'package-managers/release-metadata.json'
}

$metadata = [ordered] @{
    tagName = $tagName
    releaseName = $releaseName
    prerelease = $prerelease
    publishedAt = $publishedAt
    htmlUrl = $htmlUrl
    channel = $channel
    assetVersion = $assetVersion
    baseVersion = $baseVersion
    publishedStamp = $publishedStamp
    packageVersion = $packageVersion
    wingetPackageIdentifier = $wingetPackageIdentifier
    wingetPackageName = $wingetPackageName
    scoopApp = $scoopApp
    scoopManifestFile = $scoopManifestFile
    scoopShortcutName = $scoopShortcutName
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json | Out-Null

$outputs = [ordered] @{
    channel = $channel
    prerelease = $prerelease.ToString().ToLowerInvariant()
    release_tag = $tagName
    asset_version = $assetVersion
    base_version = $baseVersion
    package_version = $packageVersion
    winget_package_identifier = $wingetPackageIdentifier
    scoop_app = $scoopApp
    metadata_path = $OutputPath
}

foreach ($item in $outputs.GetEnumerator()) {
    Write-Host "$($item.Key): $($item.Value)"
}

$githubOutputPath = $GitHubOutput
if ([string]::IsNullOrWhiteSpace($githubOutputPath)) {
    $githubOutputPath = $env:GITHUB_OUTPUT
}

if (-not [string]::IsNullOrWhiteSpace($githubOutputPath)) {
    foreach ($item in $outputs.GetEnumerator()) {
        "$($item.Key)=$($item.Value)" | Out-File -FilePath $githubOutputPath -Encoding utf8 -Append
    }
}
