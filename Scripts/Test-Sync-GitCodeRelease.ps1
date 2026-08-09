[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Sync-GitCodeRelease.ps1') -Tag 'test'

function Assert-True {
    param([Parameter(Mandatory)] [bool] $Condition, [Parameter(Mandatory)] [string] $Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([Parameter(Mandatory)] [scriptblock] $Action, [Parameter(Mandatory)] [string] $Message)
    try { & $Action } catch { return }
    throw $Message
}

function New-Asset {
    param([string] $Name)
    return [pscustomobject]@{ name = $Name }
}

$markdown = @'
# 发布说明

| 项目 | 值 |
| --- | --- |
| 状态 | 已发布 |

```powershell
Write-Output '保留 backticks, "quotes", and Unicode'
```
'@

Assert-True (Test-GitCodeTagInput -Value 'v1.7.3') 'Valid tag should be accepted.'
Assert-Throws { Test-GitCodeTagInput -Value '' } 'Empty tag must be rejected.'
Assert-Throws { Test-GitCodeTagInput -Value "v1`n7" } 'Multiline tag must be rejected.'
Assert-True (@(ConvertTo-ReleaseArray $null).Count -eq 0) 'Null must normalize to zero assets.'
Assert-True (@(ConvertTo-ReleaseArray (New-Asset 'one.exe')).Count -eq 1) 'Scalar must normalize to one asset.'
Assert-True (@(ConvertTo-ReleaseArray @((New-Asset 'one.exe'), (New-Asset 'two.zip'))).Count -eq 2) 'Array must preserve multiple assets.'

$sourceRelease = [pscustomobject]@{
    tag_name = 'v1.7.3'
    target_commitish = '0123456789abcdef'
    prerelease = $false
    name = 'Context Menu Manager Plus 1.7.3'
    body = $markdown
    assets = @()
}
$payload = New-GitCodeReleasePayload -GitHubRelease $sourceRelease
Assert-True ($payload.body -ceq $markdown) 'Multiline Chinese Markdown must be preserved exactly.'
Assert-True ($payload.Count -eq 5) 'Create payload must contain exactly the five documented fields.'
Assert-True ($payload.release_status -ceq 'latest') 'Stable release must use the documented latest status.'
Assert-True (-not $payload.Contains('prerelease')) 'Create payload must not invent a writable prerelease field.'
$sourceRelease.prerelease = $true
Assert-True ((New-GitCodeReleasePayload -GitHubRelease $sourceRelease).release_status -ceq 'pre') 'Prerelease must use the documented pre status.'
$updatePayload = New-GitCodeReleaseUpdatePayload -GitHubRelease $sourceRelease
Assert-True ($updatePayload.Count -eq 3) 'Update payload must contain exactly the three documented fields.'
Assert-True ($updatePayload.release_status -ceq 'pre') 'Update payload must preserve prerelease state.'
Assert-True (-not $updatePayload.Contains('tag_name') -and -not $updatePayload.Contains('target_commitish')) 'Update payload must contain only fields supported by PATCH.'
Assert-True ((Get-GitCodeReleaseOperation -ExistingRelease $null) -eq 'created') 'Missing GitCode Release must create.'
Assert-True ((Get-GitCodeReleaseOperation -ExistingRelease ([pscustomobject]@{})) -eq 'updated') 'Existing GitCode Release must update.'

$tagOne = [pscustomobject]@{ name = 'v1.7.2'; commit = [pscustomobject]@{ sha = 'abc' } }
$tagTwo = [pscustomobject]@{ name = 'v1.7.1'; commit = [pscustomobject]@{ sha = 'def' } }
Assert-True ($null -eq (Find-GitCodeTag -ReleaseTag 'v1.7.2' -Tags $null)) 'No tags must report the source tag as missing.'
Assert-True ((Find-GitCodeTag -ReleaseTag 'v1.7.2' -Tags $tagOne).commit.sha -ceq 'abc') 'Scalar tag response must be normalized.'
Assert-True ((Find-GitCodeTag -ReleaseTag 'v1.7.1' -Tags @($tagOne, $tagTwo)).commit.sha -ceq 'def') 'Multiple tags must be matched by exact name.'

$uploadResponse = [pscustomobject]@{
    url = 'https://object-storage.example/upload?signed=redacted'
    headers = [pscustomobject]@{
        'x-obs-meta-project-id' = 'project'
        'x-obs-acl' = 'private'
        'x-obs-callback' = 'callback'
        'Content-Type' = 'application/octet-stream'
        'Authorization' = 'must-not-forward'
    }
}
$uploadDescriptor = ConvertTo-GitCodeUploadDescriptor -Response $uploadResponse
Assert-True ($uploadDescriptor.Headers.Count -eq 4) 'Upload must forward only the four documented OBS headers.'
Assert-True (-not $uploadDescriptor.Headers.Contains('Authorization')) 'Upload must never forward Authorization to object storage.'
Assert-Throws { ConvertTo-GitCodeUploadDescriptor -Response ([pscustomobject]@{ url = 'https://example/upload'; headers = [pscustomobject]@{} }) } 'Missing required upload headers must fail.'

$uploadPathCases = @(
    @{ FileName = 'ContextMenuMgrPlus-1.7.2-x64-self-contained-Setup.exe'; Expected = 'ContextMenuMgrPlus-1.7.2-x64-self-contained-Setup.exe' },
    @{ FileName = 'file with spaces.zip'; Expected = 'file%20with%20spaces.zip' },
    @{ FileName = 'file+plus.zip'; Expected = 'file%2Bplus.zip' },
    @{ FileName = 'file#hash.zip'; Expected = 'file%23hash.zip' },
    @{ FileName = 'file&value.zip'; Expected = 'file%26value.zip' }
)
foreach ($uploadPathCase in $uploadPathCases) {
    $actualPath = Get-GitCodeUploadDescriptorPath -ReleaseTag 'v1.7.2' -FileName $uploadPathCase.FileName
    $expectedPath = "/repos/PLFJY/ContextMenuMgr/releases/v1.7.2/upload_url?file_name=$($uploadPathCase.Expected)"
    Assert-True ($actualPath -ceq $expectedPath) "Upload URL path must URI-encode '$($uploadPathCase.FileName)'."
}

$expected = @((New-Asset 'installer x64.exe'), (New-Asset 'portable.zip'), (New-Asset '说明.txt'))
$none = Get-GitCodeAssetPlan -ExpectedAssets $expected -ExistingAssets @()
Assert-True ($none.Missing.Count -eq 3 -and $none.AlreadyPresent.Count -eq 0) 'Zero target assets must plan all uploads.'
$one = Get-GitCodeAssetPlan -ExpectedAssets $expected -ExistingAssets (New-Asset 'portable.zip')
Assert-True ($one.Missing.Count -eq 2 -and $one.AlreadyPresent.Count -eq 1) 'Scalar target asset must be normalized.'
$partial = Get-GitCodeAssetPlan -ExpectedAssets $expected -ExistingAssets @((New-Asset 'installer x64.exe'), (New-Asset 'portable.zip'))
Assert-True ($partial.Missing.Count -eq 1 -and $partial.AlreadyPresent.Count -eq 2) 'Partial target assets must only plan missing names.'

Assert-True (-not (Test-GitCodeTransientStatus -StatusCode 401)) '401 must not retry.'
Assert-True (-not (Test-GitCodeTransientStatus -StatusCode 403)) '403 must not retry.'
Assert-True (-not (Test-GitCodeTransientStatus -StatusCode 404)) '404 must not retry as a generic API failure.'
Assert-True (-not (Test-GitCodeTransientStatus -StatusCode 409)) '409 must not retry as a generic API failure.'
Assert-True (Test-GitCodeRetryAllowed -StatusCode 429 -Attempt 1 -MaxAttempts 4) '429 must retry within the bound.'
Assert-True (Test-GitCodeRetryAllowed -StatusCode 500 -Attempt 1 -MaxAttempts 4) '500 must retry within the bound.'
Assert-True (Test-GitCodeRetryAllowed -StatusCode 503 -Attempt 1 -MaxAttempts 4) '503 must retry within the bound.'
Assert-True (-not (Test-GitCodeRetryAllowed -StatusCode 504 -Attempt 4 -MaxAttempts 4)) 'Retry bound must stop retrying.'
Assert-True (Test-GitCodeTagRetryAllowed -Attempt 1 -MaxAttempts 3) 'A newly missing GitCode tag must be retried within the bounded policy.'
Assert-True (-not (Test-GitCodeTagRetryAllowed -Attempt 3 -MaxAttempts 3)) 'GitCode tag wait must be bounded.'
Assert-Throws { Assert-GitCodeUploadSucceeded -Succeeded $false -FileName 'bad.exe' -StatusCode 400 -ResponseBody 'bad request' } 'Asset upload failure must fail the synchronization.'

$sourceRelease.prerelease = $false
$sourceRelease.assets = $expected
$matchingTarget = [pscustomobject]@{ tag_name = 'v1.7.3'; target_commitish = '0123456789abcdef'; name = $sourceRelease.name; body = $markdown; prerelease = $false; release_status = 'latest'; assets = $expected }
Assert-True ((Assert-GitCodeReleaseParity -GitHubRelease $sourceRelease -GitCodeRelease $matchingTarget) -eq 3) 'Matching release must verify every expected asset.'
$missingTarget = [pscustomobject]@{ tag_name = 'v1.7.3'; target_commitish = '0123456789abcdef'; name = $sourceRelease.name; body = $markdown; prerelease = $false; release_status = 'latest'; assets = @((New-Asset 'portable.zip')) }
Assert-Throws { Assert-GitCodeReleaseParity -GitHubRelease $sourceRelease -GitCodeRelease $missingTarget } 'Final verification must fail for a missing asset.'
Assert-Throws { Test-ReleaseAssetFilename -Name 'folder/file.exe' } 'Nested filenames must not escape the download directory.'

Write-Host 'Sync-GitCodeRelease pure decision tests passed.'
