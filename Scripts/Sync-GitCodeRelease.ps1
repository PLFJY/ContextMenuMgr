[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Tag,

    [ValidateSet('release', 'workflow_dispatch', 'automatic', 'manual')]
    [string] $Trigger = $env:GITHUB_EVENT_NAME,

    [string] $GitCodeAccessToken = $env:GITCODE_ACCESS_TOKEN,

    [int] $TagMaxAttempts = 15,

    [int] $TagRetrySeconds = 12
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:GitHubRepository = 'PLFJY/ContextMenuMgr'
$script:GitCodeOwner = 'PLFJY'
$script:GitCodeRepository = 'ContextMenuMgr'
$script:GitCodeApiBase = 'https://api.atomgit.com/api/v5'
$script:GitCodeAccessToken = $GitCodeAccessToken
$script:Summary = [ordered]@{
    Trigger = ''
    Tag = ''
    ReleaseName = ''
    Prerelease = $false
    Operation = ''
    GitHubAssetCount = 0
    AlreadyPresentAssetCount = 0
    UploadedAssetCount = 0
    VerifiedAssetCount = 0
    MetadataVerified = $false
    AssetsVerified = $false
    Status = 'Failed'
}

function ConvertTo-ReleaseArray {
    [OutputType([object[]])]
    param([object] $Value)

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Test-GitCodeTagInput {
    param([Parameter(Mandatory)] [string] $Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw 'A GitHub Release tag is required.'
    }

    if ($Value -ne $Value.Trim() -or $Value.IndexOf([char]0) -ge 0 -or $Value -match '[\r\n]') {
        throw 'The Release tag contains unsupported whitespace or control characters.'
    }

    return $true
}

function Test-ReleaseAssetFilename {
    param([Parameter(Mandatory)] [string] $Name)

    if ([string]::IsNullOrWhiteSpace($Name) -or $Name -in @('.', '..') -or
        $Name.IndexOfAny([char[]]'\\/') -ge 0 -or $Name -match '[\x00-\x1f]') {
        throw "GitHub Release asset filename '$Name' cannot be used as a local filename."
    }
}

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory)] [object] $Object,
        [Parameter(Mandatory)] [string[]] $Names
    )

    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties[$name]
        if ($null -ne $property) {
            return $property.Value
        }
    }

    return $null
}

function ConvertTo-GitCodePathSegment {
    param([Parameter(Mandatory)] [string] $Value)

    return [uri]::EscapeDataString($Value)
}

function Get-SanitizedHttpErrorBody {
    param([AllowNull()] [string] $Body)

    if ([string]::IsNullOrWhiteSpace($Body)) {
        return ''
    }

    $sanitized = $Body -replace '(?i)(Bearer\s+)[^\s"'']+', '$1***' `
        -replace '(?i)([?&](?:access_token|token|signature|x-amz-signature|credential)=)[^&\s"'']+', '$1***'
    if ($sanitized.Length -gt 2000) {
        $sanitized = $sanitized.Substring(0, 2000) + '...'
    }

    return $sanitized
}

function Get-RetryAfterSeconds {
    param([System.Net.Http.Headers.HttpResponseHeaders] $Headers)

    if ($null -eq $Headers -or $null -eq $Headers.RetryAfter) {
        return $null
    }

    if ($null -ne $Headers.RetryAfter.Delta) {
        return [Math]::Max(1, [int][Math]::Ceiling($Headers.RetryAfter.Delta.Value.TotalSeconds))
    }

    if ($null -ne $Headers.RetryAfter.Date) {
        return [Math]::Max(1, [int][Math]::Ceiling(($Headers.RetryAfter.Date.Value.UtcDateTime - [datetime]::UtcNow).TotalSeconds))
    }

    return $null
}

function Test-GitCodeTransientStatus {
    param([int] $StatusCode)

    return $StatusCode -in @(429, 500, 502, 503, 504)
}

function Test-GitCodeRetryAllowed {
    param(
        [int] $StatusCode,
        [int] $Attempt,
        [int] $MaxAttempts
    )

    return (Test-GitCodeTransientStatus -StatusCode $StatusCode) -and $Attempt -lt $MaxAttempts
}

function Test-GitCodeTagRetryAllowed {
    param([int] $Attempt, [int] $MaxAttempts)

    return $Attempt -lt $MaxAttempts
}

function Get-GitCodeReleaseOperation {
    param([AllowNull()] [object] $ExistingRelease)

    if ($null -eq $ExistingRelease) {
        return 'created'
    }

    return 'updated'
}

function Assert-GitCodeUploadSucceeded {
    param(
        [Parameter(Mandatory)] [bool] $Succeeded,
        [Parameter(Mandatory)] [string] $FileName,
        [int] $StatusCode = 0,
        [AllowNull()] [string] $ResponseBody
    )

    if (-not $Succeeded) {
        $details = Get-SanitizedHttpErrorBody -Body $ResponseBody
        throw "GitCode upload of '$FileName' failed with HTTP $StatusCode. $details"
    }
}

function New-GitCodeReleasePayload {
    param([Parameter(Mandatory)] [object] $GitHubRelease)

    return [ordered]@{
        tag_name = [string] $GitHubRelease.tag_name
        target_commitish = [string] $GitHubRelease.target_commitish
        prerelease = [bool] $GitHubRelease.prerelease
        name = [string] $GitHubRelease.name
        body = [string] $GitHubRelease.body
    }
}

function Get-GitCodeAssetPlan {
    param(
        [object[]] $ExpectedAssets,
        [object[]] $ExistingAssets
    )

    $missing = [System.Collections.Generic.List[object]]::new()
    $present = [System.Collections.Generic.List[object]]::new()

    foreach ($expectedAsset in (ConvertTo-ReleaseArray $ExpectedAssets)) {
        $expectedName = [string] $expectedAsset.name
        Test-ReleaseAssetFilename -Name $expectedName
        $matches = @((ConvertTo-ReleaseArray $ExistingAssets) | Where-Object {
                [string]::Equals([string] $_.name, $expectedName, [System.StringComparison]::Ordinal)
            })

        if ($matches.Count -eq 0) {
            [void] $missing.Add($expectedAsset)
        }
        elseif ($matches.Count -eq 1) {
            [void] $present.Add($expectedAsset)
        }
        else {
            throw "GitCode Release contains multiple attachments named '$expectedName'. Refusing to add another attachment."
        }
    }

    return [pscustomobject]@{
        Missing = $missing.ToArray()
        AlreadyPresent = $present.ToArray()
    }
}

function Assert-GitCodeReleaseParity {
    param(
        [Parameter(Mandatory)] [object] $GitHubRelease,
        [Parameter(Mandatory)] [object] $GitCodeRelease
    )

    foreach ($field in @('tag_name', 'name', 'body', 'prerelease')) {
        $expected = $GitHubRelease.$field
        $actual = $GitCodeRelease.$field
        if ($field -eq 'prerelease') {
            if ([bool] $expected -ne [bool] $actual) {
                throw "GitCode Release metadata verification failed for '$field'."
            }
        }
        elseif ([string] $expected -cne [string] $actual) {
            throw "GitCode Release metadata verification failed for '$field'."
        }
    }

    $plan = Get-GitCodeAssetPlan -ExpectedAssets (ConvertTo-ReleaseArray $GitHubRelease.assets) -ExistingAssets (ConvertTo-ReleaseArray $GitCodeRelease.assets)
    if ($plan.Missing.Count -ne 0) {
        $names = ($plan.Missing | ForEach-Object { $_.name }) -join ', '
        throw "GitCode Release attachment verification failed; missing: $names"
    }

    return $plan.AlreadyPresent.Count
}

function Invoke-GitCodeApi {
    param(
        [Parameter(Mandatory)] [ValidateSet('GET', 'POST', 'PATCH')] [string] $Method,
        [Parameter(Mandatory)] [string] $Path,
        [AllowNull()] [object] $Body,
        [int[]] $AllowStatusCodes = @(),
        [int] $MaxAttempts = 4
    )

    if ([string]::IsNullOrWhiteSpace($script:GitCodeAccessToken)) {
        throw 'GITCODE_ACCESS_TOKEN is required.'
    }

    $uri = "$script:GitCodeApiBase$Path"
    $client = [System.Net.Http.HttpClient]::new()
    try {
        for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
            $response = $null
            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::$Method, $uri)
            $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $script:GitCodeAccessToken)
            $request.Headers.Accept.Add([System.Net.Http.Headers.MediaTypeWithQualityHeaderValue]::new('application/json'))
            if ($null -ne $Body) {
                $json = $Body | ConvertTo-Json -Depth 12 -Compress
                $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.UTF8Encoding]::new($false), 'application/json')
            }

            try {
                $response = $client.SendAsync($request).GetAwaiter().GetResult()
                $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                $statusCode = [int] $response.StatusCode
                $result = [pscustomobject]@{
                    StatusCode = $statusCode
                    Headers = $response.Headers
                    Body = $responseBody
                    Json = if ([string]::IsNullOrWhiteSpace($responseBody)) { $null } else { $responseBody | ConvertFrom-Json }
                }

                if ($response.IsSuccessStatusCode -or $AllowStatusCodes -contains $statusCode) {
                    return $result
                }

                if (Test-GitCodeRetryAllowed -StatusCode $statusCode -Attempt $attempt -MaxAttempts $MaxAttempts) {
                    $delay = Get-RetryAfterSeconds -Headers $response.Headers
                    if ($null -eq $delay) { $delay = [Math]::Min(30, 3 * $attempt) }
                    Write-Warning "GitCode API returned HTTP $statusCode. Retrying in $delay seconds."
                    Start-Sleep -Seconds $delay
                    continue
                }

                $details = Get-SanitizedHttpErrorBody -Body $responseBody
                if ([string]::IsNullOrWhiteSpace($details)) {
                    throw "GitCode API $Method $Path failed with HTTP $statusCode."
                }
                throw "GitCode API $Method $Path failed with HTTP ${statusCode}: $details"
            }
            finally {
                if ($null -ne $response) { $response.Dispose() }
                $request.Dispose()
            }
        }
    }
    finally {
        $client.Dispose()
    }
}

function Get-GitCodeRelease {
    param([Parameter(Mandatory)] [string] $ReleaseTag)

    $encodedTag = ConvertTo-GitCodePathSegment -Value $ReleaseTag
    $response = Invoke-GitCodeApi -Method GET -Path "/repos/$script:GitCodeOwner/$script:GitCodeRepository/releases/tags/$encodedTag" -AllowStatusCodes @(404)
    if ($response.StatusCode -eq 404) {
        return $null
    }

    return $response.Json
}

function Get-GitCodeTag {
    param([Parameter(Mandatory)] [string] $ReleaseTag)

    $encodedTag = ConvertTo-GitCodePathSegment -Value $ReleaseTag
    return Invoke-GitCodeApi -Method GET -Path "/repos/$script:GitCodeOwner/$script:GitCodeRepository/tags/$encodedTag" -AllowStatusCodes @(404)
}

function Wait-GitCodeTag {
    param(
        [Parameter(Mandatory)] [string] $ReleaseTag,
        [Parameter(Mandatory)] [string] $ExpectedCommit,
        [int] $MaxAttempts = 15,
        [int] $RetrySeconds = 12
    )

    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        $tagResponse = Get-GitCodeTag -ReleaseTag $ReleaseTag
        if ($tagResponse.StatusCode -eq 200) {
            $commit = Get-JsonPropertyValue -Object $tagResponse.Json -Names @('commit')
            $actualCommit = if ($null -ne $commit) { Get-JsonPropertyValue -Object $commit -Names @('sha', 'id') } else { $null }
            if ([string]::IsNullOrWhiteSpace([string] $actualCommit)) {
                throw "GitCode tag '$ReleaseTag' was found, but its commit SHA was not returned by the API."
            }
            if (-not [string]::Equals([string] $actualCommit, $ExpectedCommit, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "GitCode tag '$ReleaseTag' resolves to $actualCommit, not GitHub's release commit $ExpectedCommit. Refusing to create a Release for a different commit."
            }
            return
        }

        if (Test-GitCodeTagRetryAllowed -Attempt $attempt -MaxAttempts $MaxAttempts) {
            Write-Host "GitCode tag '$ReleaseTag' has not mirrored yet (attempt $attempt/$MaxAttempts). Waiting $RetrySeconds seconds."
            Start-Sleep -Seconds $RetrySeconds
        }
    }

    throw "GitCode tag '$ReleaseTag' was not available after $MaxAttempts attempts. The mirror may still be propagating; rerun this workflow with the same tag later."
}

function Get-GitHubRelease {
    param([Parameter(Mandatory)] [string] $ReleaseTag)

    if ([string]::IsNullOrWhiteSpace($env:GH_TOKEN)) {
        throw 'GH_TOKEN is required to read the published GitHub Release.'
    }

    $encodedTag = ConvertTo-GitCodePathSegment -Value $ReleaseTag
    $releaseJson = ((& gh api "repos/$script:GitHubRepository/releases/tags/$encodedTag") -join [Environment]::NewLine)
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub Release '$ReleaseTag' could not be resolved."
    }
    $release = $releaseJson | ConvertFrom-Json
    if ([bool] $release.draft) {
        throw "GitHub Release '$ReleaseTag' is still a draft and must not be synchronized."
    }
    if ($null -eq $release.published_at) {
        throw "GitHub Release '$ReleaseTag' is not published."
    }
    if ([string]::IsNullOrWhiteSpace([string] $release.name)) {
        $release.name = $release.tag_name
    }
    if ($null -eq $release.body) {
        $release.body = ''
    }
    $release.assets = @(ConvertTo-ReleaseArray $release.assets)
    foreach ($asset in $release.assets) {
        Test-ReleaseAssetFilename -Name ([string] $asset.name)
    }

    return $release
}

function Get-GitHubTagCommit {
    param([Parameter(Mandatory)] [string] $ReleaseTag)

    $encodedTag = ConvertTo-GitCodePathSegment -Value $ReleaseTag
    $referenceJson = ((& gh api "repos/$script:GitHubRepository/git/ref/tags/$encodedTag") -join [Environment]::NewLine)
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub tag '$ReleaseTag' could not be resolved."
    }
    $reference = $referenceJson | ConvertFrom-Json
    $object = $reference.object
    if ($object.type -eq 'tag') {
        $tagObjectJson = ((& gh api "repos/$script:GitHubRepository/git/tags/$($object.sha)") -join [Environment]::NewLine)
        if ($LASTEXITCODE -ne 0) {
            throw "Annotated GitHub tag '$ReleaseTag' could not be dereferenced."
        }
        $object = ($tagObjectJson | ConvertFrom-Json).object
    }
    if ($object.type -ne 'commit' -or [string]::IsNullOrWhiteSpace([string] $object.sha)) {
        throw "GitHub tag '$ReleaseTag' does not resolve to a commit."
    }

    return [string] $object.sha
}

function New-GitCodeRelease {
    param([Parameter(Mandatory)] [object] $GitHubRelease)

    return Invoke-GitCodeApi -Method POST -Path "/repos/$script:GitCodeOwner/$script:GitCodeRepository/releases" -Body (New-GitCodeReleasePayload -GitHubRelease $GitHubRelease) -AllowStatusCodes @(409)
}

function Update-GitCodeRelease {
    param([Parameter(Mandatory)] [object] $GitHubRelease)

    $encodedTag = ConvertTo-GitCodePathSegment -Value ([string] $GitHubRelease.tag_name)
    return (Invoke-GitCodeApi -Method PATCH -Path "/repos/$script:GitCodeOwner/$script:GitCodeRepository/releases/$encodedTag" -Body (New-GitCodeReleasePayload -GitHubRelease $GitHubRelease)).Json
}

function Get-GitCodeUploadUrl {
    param([Parameter(Mandatory)] [string] $ReleaseTag)

    $encodedTag = ConvertTo-GitCodePathSegment -Value $ReleaseTag
    $response = Invoke-GitCodeApi -Method GET -Path "/repos/$script:GitCodeOwner/$script:GitCodeRepository/releases/$encodedTag/upload_url"
    $uploadUrl = Get-JsonPropertyValue -Object $response.Json -Names @('upload_url')
    if ([string]::IsNullOrWhiteSpace([string] $uploadUrl)) {
        throw "GitCode did not return an upload_url for Release '$ReleaseTag'."
    }

    return [string] $uploadUrl
}

function Invoke-GitCodeAttachmentUpload {
    param(
        [Parameter(Mandatory)] [string] $UploadUrl,
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter(Mandatory)] [string] $FileName
    )

    # The upload URL is handled as an independent upload endpoint. Do not forward
    # the GitCode API bearer token or log the (potentially signed) URL.
    $client = [System.Net.Http.HttpClient]::new()
    $stream = $null
    $content = $null
    $response = $null
    try {
        $stream = [System.IO.File]::OpenRead($FilePath)
        $fileContent = [System.Net.Http.StreamContent]::new($stream)
        $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/octet-stream')
        $content = [System.Net.Http.MultipartFormDataContent]::new()
        $content.Add($fileContent, 'file', $FileName)
        $response = $client.PostAsync($UploadUrl, $content).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return [pscustomobject]@{
            StatusCode = [int] $response.StatusCode
            Headers = $response.Headers
            Body = $body
            Success = $response.IsSuccessStatusCode
        }
    }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        if ($null -ne $content) { $content.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
        $client.Dispose()
    }
}

function Download-GitHubReleaseAssets {
    param(
        [Parameter(Mandatory)] [object] $GitHubRelease,
        [Parameter(Mandatory)] [string] $Directory
    )

    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $assets = @(ConvertTo-ReleaseArray $GitHubRelease.assets)
    if ($assets.Count -eq 0) {
        return
    }

    & gh release download ([string] $GitHubRelease.tag_name) --repo $script:GitHubRepository --dir $Directory --clobber
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub Release asset download failed for '$($GitHubRelease.tag_name)'."
    }

    foreach ($asset in $assets) {
        $fileName = [string] $asset.name
        $filePath = Join-Path $Directory $fileName
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Downloaded GitHub Release asset '$fileName' is missing. GitCode uploads will not begin."
        }
        $file = Get-Item -LiteralPath $filePath
        if ($null -ne $asset.size -and [int64] $file.Length -ne [int64] $asset.size) {
            throw "Downloaded GitHub Release asset '$fileName' has size $($file.Length), expected $($asset.size). GitCode uploads will not begin."
        }
        if (-not [string]::IsNullOrWhiteSpace([string] $asset.digest)) {
            $digestParts = ([string] $asset.digest).Split(':', 2)
            if ($digestParts.Count -eq 2 -and $digestParts[0] -ieq 'sha256') {
                $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($actualHash -cne $digestParts[1].ToLowerInvariant()) {
                    throw "Downloaded GitHub Release asset '$fileName' does not match its GitHub SHA-256 digest. GitCode uploads will not begin."
                }
            }
        }
    }
}

function Sync-GitCodeReleaseAssets {
    param(
        [Parameter(Mandatory)] [string] $ReleaseTag,
        [Parameter(Mandatory)] [object[]] $MissingAssets,
        [Parameter(Mandatory)] [string] $Directory
    )

    $uploaded = 0
    foreach ($asset in (ConvertTo-ReleaseArray $MissingAssets)) {
        $fileName = [string] $asset.name
        $filePath = Join-Path $Directory $fileName
        $completed = $false
        for ($attempt = 1; $attempt -le 4; $attempt++) {
            # Request a fresh URL for every attempt; this is safe for single-use URLs.
            $uploadUrl = Get-GitCodeUploadUrl -ReleaseTag $ReleaseTag
            $result = Invoke-GitCodeAttachmentUpload -UploadUrl $uploadUrl -FilePath $filePath -FileName $fileName
            if ($result.Success) {
                $completed = $true
                $uploaded++
                break
            }
            if (Test-GitCodeRetryAllowed -StatusCode $result.StatusCode -Attempt $attempt -MaxAttempts 4) {
                $delay = Get-RetryAfterSeconds -Headers $result.Headers
                if ($null -eq $delay) { $delay = [Math]::Min(30, 3 * $attempt) }
                Write-Warning "GitCode upload of '$fileName' returned HTTP $($result.StatusCode). Retrying in $delay seconds."
                Start-Sleep -Seconds $delay
                continue
            }

            Assert-GitCodeUploadSucceeded -Succeeded $false -FileName $fileName -StatusCode $result.StatusCode -ResponseBody $result.Body
        }
        if (-not $completed) {
            throw "GitCode upload of '$fileName' exhausted its bounded retry attempts."
        }
    }

    return $uploaded
}

function Write-SyncSummary {
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
        return
    }

    $channel = if ($script:Summary.Prerelease) { 'prerelease' } else { 'stable' }
    @(
        '## GitCode Release synchronization',
        '',
        "- Status: $($script:Summary.Status)",
        "- Trigger: $($script:Summary.Trigger)",
        "- GitHub tag: $($script:Summary.Tag)",
        "- GitHub Release name: $($script:Summary.ReleaseName)",
        "- Channel: $channel",
        "- GitCode repository: $script:GitCodeOwner/$script:GitCodeRepository",
        "- Release operation: $($script:Summary.Operation)",
        "- GitHub Release assets: $($script:Summary.GitHubAssetCount)",
        "- GitCode assets already present: $($script:Summary.AlreadyPresentAssetCount)",
        "- Assets uploaded: $($script:Summary.UploadedAssetCount)",
        "- Final verified assets: $($script:Summary.VerifiedAssetCount)",
        "- Metadata verification: $($script:Summary.MetadataVerified)",
        "- Asset verification: $($script:Summary.AssetsVerified)"
    ) | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
}

function Invoke-GitCodeReleaseSync {
    param(
        [Parameter(Mandatory)] [string] $SourceTag,
        [Parameter(Mandatory)] [string] $SourceTrigger,
        [int] $MaxTagAttempts,
        [int] $TagWaitSeconds
    )

    Test-GitCodeTagInput -Value $SourceTag | Out-Null
    if ([string]::IsNullOrWhiteSpace($script:GitCodeAccessToken)) {
        throw 'GITCODE_ACCESS_TOKEN is required.'
    }
    $script:Summary.Trigger = if ($SourceTrigger -in @('release', 'automatic')) { 'automatic' } else { 'manual' }
    $script:Summary.Tag = $SourceTag
    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('ContextMenuMgr-GitCodeRelease-' + [guid]::NewGuid().ToString('N'))

    try {
        $githubRelease = Get-GitHubRelease -ReleaseTag $SourceTag
        if ([string] $githubRelease.tag_name -cne $SourceTag) {
            throw "GitHub Release lookup returned tag '$($githubRelease.tag_name)', not requested tag '$SourceTag'."
        }
        $githubRelease.target_commitish = Get-GitHubTagCommit -ReleaseTag $SourceTag
        $script:Summary.ReleaseName = [string] $githubRelease.name
        $script:Summary.Prerelease = [bool] $githubRelease.prerelease
        $script:Summary.GitHubAssetCount = @(ConvertTo-ReleaseArray $githubRelease.assets).Count

        Wait-GitCodeTag -ReleaseTag $SourceTag -ExpectedCommit ([string] $githubRelease.target_commitish) -MaxAttempts $MaxTagAttempts -RetrySeconds $TagWaitSeconds
        $gitCodeRelease = Get-GitCodeRelease -ReleaseTag $SourceTag
        $script:Summary.Operation = Get-GitCodeReleaseOperation -ExistingRelease $gitCodeRelease
        if ($null -eq $gitCodeRelease) {
            $createResult = New-GitCodeRelease -GitHubRelease $githubRelease
            if ($createResult.StatusCode -eq 409) {
                # A concurrent create can return 409; read and update the resulting Release.
                $gitCodeRelease = Get-GitCodeRelease -ReleaseTag $SourceTag
                if ($null -eq $gitCodeRelease) {
                    throw "GitCode reported that Release '$SourceTag' already exists, but it could not be read afterwards."
                }
                $gitCodeRelease = Update-GitCodeRelease -GitHubRelease $githubRelease
                $script:Summary.Operation = 'updated'
            }
            else {
                $gitCodeRelease = $createResult.Json
                if ($null -eq $gitCodeRelease) {
                    $gitCodeRelease = Get-GitCodeRelease -ReleaseTag $SourceTag
                }
                if ($null -eq $gitCodeRelease) {
                    throw "GitCode created Release '$SourceTag' but did not return a readable Release."
                }
            }
        }
        else {
            $gitCodeRelease = Update-GitCodeRelease -GitHubRelease $githubRelease
        }

        $assetPlan = Get-GitCodeAssetPlan -ExpectedAssets (ConvertTo-ReleaseArray $githubRelease.assets) -ExistingAssets (ConvertTo-ReleaseArray $gitCodeRelease.assets)
        $script:Summary.AlreadyPresentAssetCount = $assetPlan.AlreadyPresent.Count
        $downloadDirectory = Join-Path $temporaryRoot 'github-release-assets'
        Download-GitHubReleaseAssets -GitHubRelease $githubRelease -Directory $downloadDirectory
        $script:Summary.UploadedAssetCount = Sync-GitCodeReleaseAssets -ReleaseTag $SourceTag -MissingAssets $assetPlan.Missing -Directory $downloadDirectory

        $finalRelease = Get-GitCodeRelease -ReleaseTag $SourceTag
        if ($null -eq $finalRelease) {
            throw "GitCode Release '$SourceTag' disappeared before verification."
        }
        $script:Summary.VerifiedAssetCount = Assert-GitCodeReleaseParity -GitHubRelease $githubRelease -GitCodeRelease $finalRelease
        $script:Summary.MetadataVerified = $true
        $script:Summary.AssetsVerified = $true
        $script:Summary.Status = 'Succeeded'
    }
    finally {
        Write-SyncSummary
        if (Test-Path -LiteralPath $temporaryRoot) {
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-GitCodeReleaseSync -SourceTag $Tag -SourceTrigger $Trigger -MaxTagAttempts $TagMaxAttempts -TagWaitSeconds $TagRetrySeconds
}
