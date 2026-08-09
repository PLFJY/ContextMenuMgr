#Requires -Version 5.1
param(
    [Parameter(Mandatory)]
    [string] $ManifestDirectory,

    [Parameter(Mandatory)]
    [string] $WingetForkRepository,

    [string] $TargetRepository = 'microsoft/winget-pkgs',

    [Parameter(Mandatory)]
    [string] $PackageIdentifier,

    [Parameter(Mandatory)]
    [string] $PackageVersion,

    [Parameter(Mandatory)]
    [string] $ReleaseTag,

    [string] $TokenEnvVar = 'WINGET_PR_TOKEN',

    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-Command {
    param([Parameter(Mandatory)] [string] $Name)

    if ($null -eq (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found."
    }
}

function Invoke-Git {
    param(
        [Parameter(Mandatory)] [string[]] $Arguments,
        [string] $WorkingDirectory = ''
    )

    $previousLocation = Get-Location
    try {
        if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
            Set-Location -LiteralPath $WorkingDirectory
        }

        & git @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "git $($Arguments -join ' ') failed."
        }
    }
    finally {
        Set-Location -LiteralPath $previousLocation
    }
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

function Get-WingetManifestRelativePath {
    param(
        [Parameter(Mandatory)] [string] $Identifier,
        [Parameter(Mandatory)] [string] $Version
    )

    $segments = $Identifier.Split('.')
    if ($segments.Count -lt 2) {
        throw "Invalid winget package identifier '$Identifier'."
    }

    $publisher = $segments[0]
    $firstLetter = $publisher.Substring(0, 1).ToLowerInvariant()
    return "manifests/$firstLetter/$($segments -join '/')/$Version"
}

function Get-ManifestValue {
    param(
        [Parameter(Mandatory)] [string] $Content,
        [Parameter(Mandatory)] [string] $Key,
        [Parameter(Mandatory)] [string] $Path
    )

    $match = [regex]::Match($Content, "(?m)^$([regex]::Escape($Key)):\s*(.+?)\s*$")
    if (-not $match.Success) {
        throw "winget manifest '$Path' is missing '$Key'."
    }

    $value = $match.Groups[1].Value.Trim()
    if ($value.Length -ge 2 -and $value.StartsWith("'") -and $value.EndsWith("'")) {
        $value = $value.Substring(1, $value.Length - 2) -replace "''", "'"
    }

    return $value
}

function Assert-WingetTargetDirectory {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Identifier,
        [Parameter(Mandatory)] [string] $Version
    )

    $expectedFiles = @(
        "$Identifier.yaml",
        "$Identifier.installer.yaml",
        "$Identifier.locale.zh-CN.yaml",
        "$Identifier.locale.zh-TW.yaml",
        "$Identifier.locale.en-US.yaml"
    )

    $actualFiles = @(Get-ChildItem -LiteralPath $Path -File | ForEach-Object { $_.Name } | Sort-Object)
    $expectedSorted = @($expectedFiles | Sort-Object)

    Assert-True -Condition ($actualFiles.Count -eq $expectedSorted.Count) -Message "winget target directory must contain exactly $($expectedSorted.Count) manifest files."
    for ($i = 0; $i -lt $expectedSorted.Count; $i++) {
        Assert-True -Condition ($actualFiles[$i] -eq $expectedSorted[$i]) -Message "winget target directory file mismatch. Expected '$($expectedSorted[$i])', got '$($actualFiles[$i])'."
    }

    $manifestTypes = @{}
    foreach ($fileName in $expectedFiles) {
        $filePath = Join-Path $Path $fileName
        $content = Get-Content -LiteralPath $filePath -Raw
        $fileIdentifier = Get-ManifestValue -Content $content -Key 'PackageIdentifier' -Path $filePath
        $fileVersion = Get-ManifestValue -Content $content -Key 'PackageVersion' -Path $filePath
        $manifestType = Get-ManifestValue -Content $content -Key 'ManifestType' -Path $filePath

        Assert-True -Condition ($fileIdentifier -eq $Identifier) -Message "PackageIdentifier mismatch in '$fileName'."
        Assert-True -Condition ($fileVersion -eq $Version) -Message "PackageVersion mismatch in '$fileName'."

        if (-not $manifestTypes.ContainsKey($manifestType)) {
            $manifestTypes[$manifestType] = 0
        }

        $manifestTypes[$manifestType]++
    }

    foreach ($requiredType in @('version', 'defaultLocale', 'installer')) {
        Assert-True -Condition ($manifestTypes.ContainsKey($requiredType) -and $manifestTypes[$requiredType] -eq 1) -Message "winget target directory must contain exactly one ManifestType: $requiredType."
    }

    Assert-True -Condition ($manifestTypes.ContainsKey('locale') -and $manifestTypes['locale'] -ge 1) -Message 'winget target directory must contain at least one locale manifest.'
}

function Assert-GitStatusOnlyUnderPath {
    param(
        [Parameter(Mandatory)] [string] $ClonePath,
        [Parameter(Mandatory)] [string] $RelativePath
    )

    $normalizedTarget = ($RelativePath -replace '\\', '/').TrimEnd('/') + '/'
    $statusLines = @(git -C $ClonePath status --porcelain --untracked-files=all)
    foreach ($line in $statusLines) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.Length -lt 4) {
            continue
        }

        $pathPart = $line.Substring(3).Trim()
        $paths = @($pathPart)
        if ($pathPart -match ' -> ') {
            $paths = @($pathPart -split ' -> ')
        }

        foreach ($changedPath in $paths) {
            $normalizedChangedPath = ($changedPath.Trim('"') -replace '\\', '/')
            if (-not $normalizedChangedPath.StartsWith($normalizedTarget, [System.StringComparison]::Ordinal)) {
                throw "Unexpected changed file outside winget target path '$RelativePath': $changedPath"
            }
        }
    }

    return $statusLines
}

if (-not (Test-Path -LiteralPath $ManifestDirectory)) {
    throw "Generated winget manifest directory was not found: $ManifestDirectory"
}

if ([string]::IsNullOrWhiteSpace($WingetForkRepository)) {
    throw 'Winget fork repository is required.'
}

Assert-Command -Name 'git'
if (-not $DryRun) {
    Assert-Command -Name 'gh'
}

$token = [System.Environment]::GetEnvironmentVariable($TokenEnvVar)
if (-not $DryRun -and [string]::IsNullOrWhiteSpace($token)) {
    throw "Environment variable '$TokenEnvVar' is required when winget PR submission is enabled."
}

if (-not $DryRun) {
    $env:GH_TOKEN = $token
}

$branchPrefix = 'contextmenumgrplus'
if ($PackageIdentifier -like '*.Beta') {
    $branchPrefix = 'contextmenumgrplus-beta'
}

$safeVersion = $PackageVersion -replace '[^A-Za-z0-9._-]', '-'
$branchName = "$branchPrefix-$safeVersion"
$targetRelativePath = Get-WingetManifestRelativePath -Identifier $PackageIdentifier -Version $PackageVersion
$workRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("ContextMenuMgr-winget-" + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
$clonePath = Join-Path $workRoot 'winget-pkgs'
$scriptDir = $PSScriptRoot
if (-not $scriptDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if ($DryRun) {
    $cloneUrl = "https://github.com/$WingetForkRepository.git"
}
else {
    $escapedToken = [System.Uri]::EscapeDataString($token)
    $cloneUrl = "https://x-access-token:$escapedToken@github.com/$WingetForkRepository.git"
}

<# 
winget-pkgs contains hundreds of thousands of manifest files. A full checkout is
slow on Windows runners and can fail on unrelated existing manifests with long
paths before this package is even touched. Use a partial no-checkout clone plus
sparse checkout so only the target PLFJY manifest directory is materialized.
#>
Invoke-Git -Arguments @(
    '-c', 'core.longpaths=true',
    'clone',
    '--filter=blob:none',
    '--no-checkout',
    $cloneUrl,
    $clonePath
)
Invoke-Git -Arguments @('config', 'core.longpaths', 'true') -WorkingDirectory $clonePath
Invoke-Git -Arguments @('config', 'user.name', 'github-actions[bot]') -WorkingDirectory $clonePath
Invoke-Git -Arguments @('config', 'user.email', '41898282+github-actions[bot]@users.noreply.github.com') -WorkingDirectory $clonePath
Invoke-Git -Arguments @('remote', 'add', 'upstream', "https://github.com/$TargetRepository.git") -WorkingDirectory $clonePath
Invoke-Git -Arguments @('fetch', 'upstream', 'master', '--depth=1') -WorkingDirectory $clonePath
Invoke-Git -Arguments @('sparse-checkout', 'init', '--cone') -WorkingDirectory $clonePath
Invoke-Git -Arguments @('sparse-checkout', 'set', '--skip-checks', $targetRelativePath) -WorkingDirectory $clonePath
Invoke-Git -Arguments @('checkout', '-B', $branchName, 'upstream/master') -WorkingDirectory $clonePath

$targetPath = Join-Path $clonePath $targetRelativePath
New-Item -ItemType Directory -Force -Path $targetPath | Out-Null
Get-ChildItem -LiteralPath $ManifestDirectory -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $targetPath -Force
}

Assert-WingetTargetDirectory -Path $targetPath -Identifier $PackageIdentifier -Version $PackageVersion

$testWingetManifest = Join-Path $scriptDir 'Test-WingetManifest.ps1'
$validationOutputPath = Join-Path $workRoot 'winget-target-validation-output.txt'
& pwsh $testWingetManifest `
    -ManifestDirectory $targetPath `
    -ValidationOutputPath $validationOutputPath
if ($LASTEXITCODE -ne 0) {
    throw "winget target-path validation failed for '$targetPath'."
}

$status = Assert-GitStatusOnlyUnderPath -ClonePath $clonePath -RelativePath $targetRelativePath
if ($DryRun) {
    Write-Host "Dry run enabled; winget manifest path would be '$targetRelativePath'."
    Write-Host "winget target path: $targetRelativePath"
    git -C $clonePath diff -- $targetRelativePath
    git -C $clonePath status --short
    return
}

if ([string]::IsNullOrWhiteSpace(($status -join [Environment]::NewLine))) {
    Write-Host 'microsoft/winget-pkgs already contains the generated manifest; no winget PR is required.'
    return
}
else {
    Invoke-Git -Arguments @('add', $targetRelativePath) -WorkingDirectory $clonePath
    Invoke-Git -Arguments @('commit', '-m', "New version: $PackageIdentifier version $PackageVersion") -WorkingDirectory $clonePath
    Invoke-Git -Arguments @('push', '--force-with-lease', 'origin', $branchName) -WorkingDirectory $clonePath
}

$existingPrUrl = gh pr list `
    --repo $TargetRepository `
    --head "$($WingetForkRepository.Split('/')[0]):$branchName" `
    --state open `
    --json url `
    --jq '.[0].url' `
    --limit 1

if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($existingPrUrl)) {
    Write-Host "Existing winget PR: $existingPrUrl"
    return
}

$body = @"
Generated from PLFJY/ContextMenuMgr release $ReleaseTag.

Stable and Beta are mutually exclusive channels and share the same installer AppId and service identity intentionally.

The Beta package-manager channel only tracks GitHub Pre-releases. Stable GitHub Releases do not generate or update this Beta manifest. For Beta prereleases the package version is derived from the GitHub Release publish time.
"@

$title = "New version: $PackageIdentifier version $PackageVersion"
gh pr create --repo $TargetRepository --head "$($WingetForkRepository.Split('/')[0]):$branchName" --base master --title $title --body $body
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to create winget PR.'
}
