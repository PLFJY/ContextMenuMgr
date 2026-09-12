#Requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [string] $PackageIdentifier,

    [Parameter(Mandatory)]
    [string] $PackageVersion,

    [Parameter(Mandatory)]
    [string] $ReleaseTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$channel = if ($PackageIdentifier -like '*.Beta') { 'Beta' } else { 'Stable' }
$escapedReleaseTag = [System.Uri]::EscapeDataString($ReleaseTag)
$releaseUrl = "https://github.com/PLFJY/ContextMenuMgr/releases/tag/$escapedReleaseTag"

return @(
    '## Description'
    ''
    "Updates ``$PackageIdentifier`` to version ``$PackageVersion``."
    ''
    '## Release details'
    ''
    "- Upstream release: [$ReleaseTag]($releaseUrl)"
    "- Channel: $channel"
    '- Architectures: x64, x86, arm64'
    '- Installer type: Inno Setup'
    ''
    '## Validation'
    ''
    '- [x] Manifests validated with `winget validate --ignore-warnings`'
    '- [x] Changes are limited to this package version'
) -join [System.Environment]::NewLine
