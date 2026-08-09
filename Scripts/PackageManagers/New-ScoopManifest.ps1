#Requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [string] $ReleaseMetadataJson,

    [Parameter(Mandatory)]
    [string] $AssetManifestJson,

    [Parameter(Mandatory)]
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Json {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "JSON file was not found: $Path"
    }

    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-PreInstallLines {
    param([Parameter(Mandatory)] [string] $Channel)

    if ($Channel -eq 'beta') {
        return @(
            'if (Test-Path "$scoopdir\apps\contextmenumgrplus\current") {',
            "    error 'Context Menu Manager Plus stable is already installed. Uninstall contextmenumgrplus before installing the beta package.'",
            '    exit 1',
            '}'
        )
    }

    return @(
        'if (Test-Path "$scoopdir\apps\contextmenumgrplus-beta\current") {',
        "    error 'Context Menu Manager Plus Beta is already installed. Uninstall contextmenumgrplus-beta before installing the stable package.'",
        '    exit 1',
        '}'
    )
}

$metadata = Read-Json -Path $ReleaseMetadataJson
$assets = Read-Json -Path $AssetManifestJson

foreach ($assetName in @('scoopPortableX64', 'scoopPortableX86', 'scoopPortableArm64')) {
    if ($null -eq $assets.assets.$assetName) {
        throw "Asset manifest is missing assets.$assetName."
    }
}

$notes = @(
    'Context Menu Manager Plus may ask to install or repair its Windows service for elevated menu operations.'
)

if ($metadata.channel -eq 'beta') {
    # The Beta package-manager channel only originates from a GitHub
    # Pre-release, so it always uses the prerelease regression warning.
    $notes += 'Beta builds may contain regressions; use them only when you want to validate prerelease changes.'
}

$shortcutEntry = [object[]] @('ContextMenuManagerPlus.exe', [string] $metadata.scoopShortcutName)

$manifest = [ordered] @{
    version = [string] $metadata.packageVersion
    description = 'A Windows context menu management tool.'
    homepage = 'https://github.com/PLFJY/ContextMenuMgr'
    license = 'GPL-3.0-only'
    architecture = [ordered] @{
        '64bit' = [ordered] @{
            url = [string] $assets.assets.scoopPortableX64.url
            hash = [string] $assets.assets.scoopPortableX64.sha256
        }
        '32bit' = [ordered] @{
            url = [string] $assets.assets.scoopPortableX86.url
            hash = [string] $assets.assets.scoopPortableX86.sha256
        }
        'arm64' = [ordered] @{
            url = [string] $assets.assets.scoopPortableArm64.url
            hash = [string] $assets.assets.scoopPortableArm64.sha256
        }
    }
    shortcuts = [object[]] @(, $shortcutEntry)
    persist = 'Data'
    notes = $notes
    pre_install = @(Get-PreInstallLines -Channel ([string] $metadata.channel))
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$manifestPath = Join-Path $OutputDirectory ([string] $metadata.scoopManifestFile)
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json | Out-Null

Write-Host "Scoop manifest: $manifestPath"
Write-Output $manifestPath
