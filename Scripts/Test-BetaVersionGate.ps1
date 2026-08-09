#Requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [string] $BetaVersion,

    [Parameter(Mandatory)]
    [string] $OwnerRepo,

    [string] $GitHubTokenEnvVar = 'GH_TOKEN'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-StableBaseVersion {
    param([Parameter(Mandatory)] [string] $Version)

    $candidate = $Version -replace '^[vV]', ''
    $candidate = $candidate -replace '\+.*$', ''
    $candidate = $candidate -replace '(?i)[-\.]?(beta|preview|pre|rc|alpha)([-\.]?\d+)?$', ''

    if ($candidate -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        throw "Cannot parse base version from '$Version'."
    }

    return $candidate
}

function Compare-Versions {
    param(
        [Parameter(Mandatory)] [string] $VersionA,
        [Parameter(Mandatory)] [string] $VersionB
    )

    <#
        Returns -1 if VersionA < VersionB, 0 if equal, 1 if greater.
        Only compares numeric dotted segments; pre-release suffixes must be
        stripped before calling this function.
    #>

    $aParts = $VersionA.Split('.') | ForEach-Object { [int] $_ }
    $bParts = $VersionB.Split('.') | ForEach-Object { [int] $_ }
    $maxLen = [Math]::Max($aParts.Count, $bParts.Count)

    for ($i = 0; $i -lt $maxLen; $i++) {
        $a = if ($i -lt $aParts.Count) { $aParts[$i] } else { 0 }
        $b = if ($i -lt $bParts.Count) { $bParts[$i] } else { 0 }
        if ($a -lt $b) { return -1 }
        if ($a -gt $b) { return 1 }
    }

    return 0
}

function Assert-Command {
    param([Parameter(Mandatory)] [string] $Name)

    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found."
    }
}

$betaBase = Get-StableBaseVersion -Version $BetaVersion
Write-Host "Beta base version: $betaBase"

Assert-Command -Name 'gh'

$token = [System.Environment]::GetEnvironmentVariable($GitHubTokenEnvVar)
if (-not [string]::IsNullOrWhiteSpace($token)) {
    $env:GH_TOKEN = $token
}

# Query the latest published non-prerelease, non-draft release.
# The GitHub API /releases/latest endpoint returns the most recent non-prerelease
# non-draft release, which is exactly the stable version we need to compare against.
$latestStableTag = $null
try {
    $latestStableTag = gh api "repos/$OwnerRepo/releases/latest" --jq '.tag_name' 2>$null
    if ($LASTEXITCODE -ne 0) {
        $latestStableTag = $null
    }
}
catch {
    $latestStableTag = $null
}

if ([string]::IsNullOrWhiteSpace($latestStableTag)) {
    Write-Host "No published stable release found in '$OwnerRepo'; Beta version gate skipped."
    return
}

$stableVersion = Get-StableBaseVersion -Version $latestStableTag
Write-Host "Latest stable version: $stableVersion (from tag '$latestStableTag')"

$comparison = Compare-Versions -VersionA $betaBase -VersionB $stableVersion

if ($comparison -le 0) {
    $relation = if ($comparison -eq 0) { 'equals' } else { 'is lower than' }
    throw @"
Beta version gate failed.
Beta base version '$betaBase' $relation the latest stable version '$stableVersion'.

The Beta package-manager channel only publishes GitHub Pre-releases and does not
track Stable GitHub Releases. Once a Stable release exists at version
'$stableVersion', a Beta prerelease must use a strictly newer base version so the
Beta release line stays ahead of the latest Stable release.

Publish a Beta release with a base version strictly higher than '$stableVersion'.
"@
}

Write-Host "Beta version gate passed: '$betaBase' > '$stableVersion'."
