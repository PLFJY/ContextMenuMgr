#Requires -Version 5.1

Set-StrictMode -Version Latest

function Invoke-External {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter()] [string[]] $Arguments = @(),
        [Parameter()] [string] $ErrorMessage = "External command failed."
    )

    Write-Host ">> $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $FilePath @Arguments 2>&1 | ForEach-Object { Write-Host $_ }

    if ($LASTEXITCODE -ne 0) {
        throw "$ErrorMessage (ExitCode=$LASTEXITCODE): $FilePath $($Arguments -join ' ')"
    }
}

function ConvertTo-CommandLineArgument {
    param([Parameter(Mandatory)] [string] $Argument)

    if ($Argument.Length -eq 0) {
        return '""'
    }

    if ($Argument.IndexOfAny([char[]] @(' ', "`t", "`n", "`v", '"')) -lt 0) {
        return $Argument
    }

    $builder = [System.Text.StringBuilder]::new()
    [void] $builder.Append('"')
    $backslashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }

        if ($character -eq '"') {
            [void] $builder.Append([char] '\', ($backslashes * 2) + 1)
            [void] $builder.Append('"')
            $backslashes = 0
            continue
        }

        if ($backslashes -gt 0) {
            [void] $builder.Append([char] '\', $backslashes)
            $backslashes = 0
        }

        [void] $builder.Append($character)
    }

    if ($backslashes -gt 0) {
        [void] $builder.Append([char] '\', $backslashes * 2)
    }

    [void] $builder.Append('"')
    return $builder.ToString()
}

function Invoke-ExternalWithNormalizedEnvironment {
    param(
        [Parameter(Mandatory)] [string] $FilePath,
        [Parameter()] [string[]] $Arguments = @(),
        [Parameter()] [string] $ErrorMessage = "External command failed."
    )

    Write-Host ">> $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray

    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $FilePath
    $processInfo.UseShellExecute = $false
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $pathValue = $processInfo.EnvironmentVariables["Path"]
    if ([string]::IsNullOrEmpty($pathValue)) {
        $pathValue = $processInfo.EnvironmentVariables["PATH"]
    }

    $processInfo.EnvironmentVariables.Remove("PATH")
    $processInfo.EnvironmentVariables.Remove("Path")
    if (-not [string]::IsNullOrEmpty($pathValue)) {
        $processInfo.EnvironmentVariables["Path"] = $pathValue
    }

    $argumentListProperty = $processInfo.GetType().GetProperty("ArgumentList")
    if ($null -ne $argumentListProperty) {
        $argumentList = $argumentListProperty.GetValue($processInfo)
        foreach ($argument in $Arguments) {
            [void] $argumentList.Add($argument)
        }
    }
    else {
        $processInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-CommandLineArgument -Argument $_ }) -join ' ')
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    [void] $process.Start()
    $standardOutput = $process.StandardOutput.ReadToEndAsync()
    $standardError = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()

    $output = $standardOutput.GetAwaiter().GetResult()
    $errorOutput = $standardError.GetAwaiter().GetResult()
    if (-not [string]::IsNullOrWhiteSpace($output)) {
        $output -split "`r?`n" | Where-Object { $_ -ne "" } | ForEach-Object { Write-Host $_ }
    }

    if (-not [string]::IsNullOrWhiteSpace($errorOutput)) {
        $errorOutput -split "`r?`n" | Where-Object { $_ -ne "" } | ForEach-Object { Write-Host $_ }
    }

    if ($process.ExitCode -ne 0) {
        throw "$ErrorMessage (ExitCode=$($process.ExitCode)): $FilePath $($Arguments -join ' ')"
    }
}

function Get-MsBuildPropertyValue {
    param(
        [Parameter(Mandatory)] [string] $ProjectPath,
        [Parameter(Mandatory)] [string] $Configuration,
        [Parameter(Mandatory)] [string] $PropertyName
    )

    $value = & dotnet msbuild $ProjectPath `
        -nologo `
        "-getProperty:$PropertyName" `
        "-p:Configuration=$Configuration"

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to evaluate MSBuild property '$PropertyName' from '$ProjectPath' for configuration '$Configuration'."
    }

    $resolvedValue = ($value | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 1)
    if ($null -eq $resolvedValue) {
        return ""
    }

    return $resolvedValue.Trim()
}

function Get-FrontendVersion {
    param(
        [Parameter(Mandatory)] [string] $ProjectPath,
        [Parameter(Mandatory)] [string] $Configuration
    )

    $informationalVersion = Get-MsBuildPropertyValue `
        -ProjectPath $ProjectPath `
        -Configuration $Configuration `
        -PropertyName "InformationalVersion"

    if (-not [string]::IsNullOrWhiteSpace($informationalVersion)) {
        return $informationalVersion
    }

    $fileVersion = Get-MsBuildPropertyValue `
        -ProjectPath $ProjectPath `
        -Configuration $Configuration `
        -PropertyName "FileVersion"

    if (-not [string]::IsNullOrWhiteSpace($fileVersion)) {
        return $fileVersion
    }

    throw "Neither InformationalVersion nor FileVersion was found for configuration '$Configuration'."
}

function Get-GitShortCommit {
    $commit = & git rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
        throw "Unable to resolve the current Git commit hash."
    }

    return $commit.Trim()
}

function Get-BuildVersion {
    param(
        [Parameter(Mandatory)] [string] $ProjectPath,
        [Parameter(Mandatory)] [string] $Configuration
    )

    $version = Get-FrontendVersion -ProjectPath $ProjectPath -Configuration $Configuration
    if (
        [string]::Equals($Configuration, "Beta", [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($Configuration, "Debug", [System.StringComparison]::OrdinalIgnoreCase)
    ) {
        $version = "$version+$(Get-GitShortCommit)"
    }

    return $version
}

function Ensure-FileExists {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }
}

function Reset-DirectoryAttributes {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue |
        ForEach-Object {
            try {
                $_.Attributes = [System.IO.FileAttributes]::Normal
            }
            catch {
            }
        }
}

function Remove-DirectoryIfExists {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $lastError = $null

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            [System.IO.Directory]::Delete($resolvedPath, $true)
            return
        }
        catch {
            $lastError = $_
            Reset-DirectoryAttributes -Path $resolvedPath
            Start-Sleep -Milliseconds (200 * $attempt)
        }
    }

    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $resolvedPath -Recurse -Force -Confirm:$false -ErrorAction Stop
            return
        }
        catch {
            $lastError = $_
            Reset-DirectoryAttributes -Path $resolvedPath
            Start-Sleep -Milliseconds (200 * $attempt)
        }
    }

    Get-ChildItem -LiteralPath $resolvedPath -Recurse -Force -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            for ($attempt = 1; $attempt -le 5; $attempt++) {
                try {
                    $_.Attributes = [System.IO.FileAttributes]::Normal
                    Remove-Item -LiteralPath $_.FullName -Force -Confirm:$false -ErrorAction Stop
                    return
                }
                catch {
                    $lastError = $_
                    Start-Sleep -Milliseconds (200 * $attempt)
                }
            }
        }

    Get-ChildItem -LiteralPath $resolvedPath -Recurse -Force -Directory -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
        }

    try {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force -Confirm:$false -ErrorAction Stop
    }
    catch {
        $lastError = $_
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        throw "Unable to remove directory '$resolvedPath'. Close processes that may be using files under it and retry. Last error: $lastError"
    }
}

function Add-TrailingDirectorySeparator {
    param([Parameter(Mandatory)] [string] $Path)

    if ($Path.EndsWith("\", [System.StringComparison]::Ordinal) -or
        $Path.EndsWith("/", [System.StringComparison]::Ordinal)) {
        return $Path
    }

    return $Path + [System.IO.Path]::DirectorySeparatorChar
}

function Resolve-IsccPath {
    param([Parameter(Mandatory)] [string] $RepoRoot)

    $candidates = @(
        (Join-Path $RepoRoot "Installer\Inno Setup 6\ISCC.exe"),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }

    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command -and $command.Source) {
        return $command.Source
    }

    throw "Unable to locate ISCC.exe. Install Inno Setup 6 or place it under Installer\Inno Setup 6\ISCC.exe."
}

function Resolve-MSBuildPath {
    $command = Get-Command "MSBuild.exe" -ErrorAction SilentlyContinue
    if ($command -and $command.Source) {
        return $command.Source
    }

    $vswhereCandidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\Installer\vswhere.exe"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($vswhere in $vswhereCandidates) {
        if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
            continue
        }

        $installationPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
        if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($installationPath)) {
            $candidate = Join-Path $installationPath.Trim() "MSBuild\Current\Bin\MSBuild.exe"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }

    throw "Unable to locate MSBuild.exe. Install Visual Studio Build Tools with the C++ workload."
}

function Get-MSBuildInstallationRoot {
    param([Parameter(Mandatory)] [string] $MSBuildPath)

    $directory = (Get-Item -LiteralPath $MSBuildPath).Directory
    while ($null -ne $directory) {
        if ([string]::Equals($directory.Name, "MSBuild", [System.StringComparison]::OrdinalIgnoreCase) -and
            $null -ne $directory.Parent) {
            return $directory.Parent.FullName
        }

        $directory = $directory.Parent
    }

    throw "Unable to determine Visual Studio installation root from MSBuild path '$MSBuildPath'."
}

function Assert-NativeProbeHostToolchain {
    param(
        [Parameter(Mandatory)] [string] $MSBuildPath,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $MSBuildPlatform
    )

    $targetDirectory = switch ($MSBuildPlatform) {
        "Win32" { "x86" }
        "x64" { "x64" }
        "ARM64" { "arm64" }
        default { throw "Unsupported ProbeHost MSBuild platform '$MSBuildPlatform'." }
    }

    $installRoot = Get-MSBuildInstallationRoot -MSBuildPath $MSBuildPath
    $msvcToolsRoot = Join-Path $installRoot "VC\Tools\MSVC"
    if (-not (Test-Path -LiteralPath $msvcToolsRoot -PathType Container)) {
        throw "Unable to locate MSVC tools under '$msvcToolsRoot'. Install Visual Studio Build Tools with the C++ workload."
    }

    $toolsetDirectories = @(Get-ChildItem -LiteralPath $msvcToolsRoot -Directory | Sort-Object Name -Descending)
    foreach ($toolsetDirectory in $toolsetDirectories) {
        foreach ($hostDirectory in @("Hostx64", "HostX64", "Hostx86", "HostX86", "HostArm64", "HostARM64")) {
            $compilerPath = Join-Path $toolsetDirectory.FullName (Join-Path "bin" (Join-Path $hostDirectory (Join-Path $targetDirectory "cl.exe")))
            if (Test-Path -LiteralPath $compilerPath -PathType Leaf) {
                return
            }
        }
    }

    $requiredComponent = if ($MSBuildPlatform -eq "ARM64") {
        "the Visual Studio C++ ARM64 build tools"
    }
    else {
        "the Visual Studio C++ $Label build tools"
    }

    throw "Native ProbeHost $Label build tools are missing. Install $requiredComponent and the Windows SDK, then rerun the release build."
}

function Get-RuntimeIdentifier {
    param([Parameter(Mandatory)] [string] $Platform)

    switch ($Platform.ToLowerInvariant()) {
        "anycpu" { return "" }
        "win-x64" { return "win-x64" }
        "win-x86" { return "win-x86" }
        "win-arm64" { return "win-arm64" }
        default { throw "Unsupported platform '$Platform'. Supported values: anycpu, win-x64, win-x86, win-arm64." }
    }
}

function Get-ArtifactPlatformLabel {
    param([Parameter(Mandatory)] [string] $Platform)

    switch ($Platform.ToLowerInvariant()) {
        "anycpu" { return "anycpu" }
        "win-x64" { return "x64" }
        "win-x86" { return "x86" }
        "win-arm64" { return "arm64" }
        default { throw "Unsupported platform '$Platform'. Supported values: anycpu, win-x64, win-x86, win-arm64." }
    }
}

function Get-ProbeHostArchitectureMap {
    param([Parameter(Mandatory)] [string] $Platform)

    switch ($Platform.ToLowerInvariant()) {
        "win-x86" {
            return @(@{ Runtime = "win-x86"; Label = "x86" })
        }
        "win-x64" {
            return @(
                @{ Runtime = "win-x64"; Label = "x64" },
                @{ Runtime = "win-x86"; Label = "x86" }
            )
        }
        "win-arm64" {
            return @(
                @{ Runtime = "win-arm64"; Label = "arm64" },
                @{ Runtime = "win-x64"; Label = "x64" },
                @{ Runtime = "win-x86"; Label = "x86" }
            )
        }
        "anycpu" {
            return @()
        }
        default {
            throw "Unsupported platform '$Platform'. Supported values: anycpu, win-x64, win-x86, win-arm64."
        }
    }
}

function Get-NativeProbeHostPlatform {
    param([Parameter(Mandatory)] [string] $Label)

    switch ($Label.ToLowerInvariant()) {
        "x86" { return "Win32" }
        "x64" { return "x64" }
        "arm64" { return "ARM64" }
        default { throw "Unsupported ProbeHost architecture label '$Label'." }
    }
}

function Get-NativeProbeHostExpectedMachine {
    param([Parameter(Mandatory)] [string] $Label)

    switch ($Label.ToLowerInvariant()) {
        "x86" { return [uint16] 0x014C }
        "x64" { return [uint16] 0x8664 }
        "arm64" { return [uint16] 0xAA64 }
        default { throw "Unsupported ProbeHost architecture label '$Label'." }
    }
}

function Get-PeMachine {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "ProbeHost executable is missing: $Path"
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 0x40) {
            throw "ProbeHost executable is too small to contain a PE header: $Path"
        }

        $reader = [System.IO.BinaryReader]::new($stream)
        $stream.Position = 0x3C
        $peHeaderOffset = $reader.ReadInt32()
        if ($peHeaderOffset -le 0 -or $peHeaderOffset + 6 -gt $stream.Length) {
            throw "ProbeHost executable has an invalid PE header offset: $Path"
        }

        $stream.Position = $peHeaderOffset
        $signature = $reader.ReadUInt32()
        if ($signature -ne 0x00004550) {
            throw "ProbeHost executable is missing a PE signature: $Path"
        }

        return $reader.ReadUInt16()
    }
    finally {
        $stream.Dispose()
    }
}

function Test-NativeProbeHostArchitectureAfterBuild {
    param(
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $MSBuildPlatform,
        [Parameter(Mandatory)] [string] $Path
    )

    $expected = Get-NativeProbeHostExpectedMachine -Label $Label
    $actual = Get-PeMachine -Path $Path
    if ($actual -ne $expected) {
        throw @"
Native ProbeHost architecture mismatch after build:
Label=$Label
MSBuildPlatform=$MSBuildPlatform
ExpectedMachine=0x$($expected.ToString('X4'))
ActualMachine=0x$($actual.ToString('X4'))
Path=$Path
"@
    }

    return $actual
}

function Get-NlohmannJsonLicensePath {
    param([Parameter(Mandatory)] [string] $NativeProbeHostProject)

    $projectDirectory = Split-Path -Parent $NativeProbeHostProject
    return Join-Path $projectDirectory "third_party\nlohmann\nlohmann-json-LICENSE.MIT"
}

function Invoke-NativeProbeHostBuild {
    param(
        [Parameter(Mandatory)] [string] $MSBuildPath,
        [Parameter(Mandatory)] [string] $NativeProbeHostProject,
        [Parameter(Mandatory)] [string] $Configuration,
        [Parameter(Mandatory)] [string] $Label,
        [Parameter(Mandatory)] [string] $OutputDirectory,
        [Parameter(Mandatory)] [string] $IntermediateDirectory
    )

    $platform = Get-NativeProbeHostPlatform -Label $Label
    $resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
    $resolvedIntermediateDirectory = [System.IO.Path]::GetFullPath($IntermediateDirectory)
    $msbuildOutputDirectory = Add-TrailingDirectorySeparator -Path $resolvedOutputDirectory
    $msbuildIntermediateDirectory = Add-TrailingDirectorySeparator -Path $resolvedIntermediateDirectory
    New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $resolvedIntermediateDirectory -Force | Out-Null
    Assert-NativeProbeHostToolchain -MSBuildPath $MSBuildPath -Label $Label -MSBuildPlatform $platform

    Invoke-ExternalWithNormalizedEnvironment `
        -FilePath $MSBuildPath `
        -Arguments @(
            $NativeProbeHostProject,
            "/nologo",
            "/m",
            "/t:Build",
            "/p:Configuration=$Configuration",
            "/p:Platform=$platform",
            "/p:OutDir=$msbuildOutputDirectory",
            "/p:IntDir=$msbuildIntermediateDirectory"
        ) `
        -ErrorMessage "MSBuild failed for native ProbeHost ($Label)"

    $targetExe = Join-Path $resolvedOutputDirectory "ContextMenuMgr.ProbeHost.exe"
    $detectedMachine = Test-NativeProbeHostArchitectureAfterBuild `
        -Label $Label `
        -MSBuildPlatform $platform `
        -Path $targetExe

    Write-Host "Native ProbeHost build output:"
    Write-Host "  Label=$Label"
    Write-Host "  MSBuildPlatform=$platform"
    Write-Host "  OutputDirectory=$resolvedOutputDirectory"
    Write-Host "  IntermediateDirectory=$resolvedIntermediateDirectory"
    Write-Host "  TargetExe=$targetExe"
    Write-Host "  DetectedPEMachine=0x$($detectedMachine.ToString('X4'))"
}

function Get-InstallerArchitectureOptions {
    param([Parameter(Mandatory)] [string] $Platform)

    switch ($Platform.ToLowerInvariant()) {
        "win-x64" {
            return @{
                Allowed = "x64compatible"
                InstallIn64BitMode = "x64compatible"
            }
        }
        "win-x86" {
            return @{
                Allowed = "x86compatible"
                InstallIn64BitMode = ""
            }
        }
        "win-arm64" {
            return @{
                Allowed = "arm64"
                InstallIn64BitMode = "arm64"
            }
        }
        default {
            throw "Unsupported installer platform '$Platform'. Supported values: win-x64, win-x86, win-arm64."
        }
    }
}

function Get-DistributionModeOptions {
    param([Parameter(Mandatory)] [string] $DistributionMode)

    switch ($DistributionMode.ToLowerInvariant()) {
        "self-contained" {
            return @{
                SelfContained = "true"
                InstallerSuffix = "self-contained"
                UseDotNetDependencyInstaller = "0"
            }
        }
        "framework-dependent" {
            return @{
                SelfContained = "false"
                InstallerSuffix = "framework-dependent"
                UseDotNetDependencyInstaller = "1"
            }
        }
        default {
            throw "Unsupported distribution mode '$DistributionMode'. Supported values: self-contained, framework-dependent."
        }
    }
}

function New-DotNetRestoreArguments {
    param(
        [Parameter(Mandatory)] [string] $ProjectPath,
        [Parameter()] [string] $RuntimeIdentifier = "",
        [Parameter(Mandatory)] [string] $NuGetConfig,
        [Parameter(Mandatory)] [string] $ArtifactsPath
    )

    $arguments = @("restore", $ProjectPath)

    if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        $arguments += @("-r", $RuntimeIdentifier)
    }

    $baseOutputPath = Join-Path $ArtifactsPath "bin"
    if (-not $baseOutputPath.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString())) {
        $baseOutputPath += [System.IO.Path]::DirectorySeparatorChar
    }

    $arguments += @(
        "--configfile", $NuGetConfig,
        "--artifacts-path", $ArtifactsPath,
        "-p:BaseOutputPath=$baseOutputPath"
    )

    return $arguments
}

function New-DotNetPublishArguments {
    param(
        [Parameter(Mandatory)] [string] $ProjectPath,
        [Parameter(Mandatory)] [string] $Configuration,
        [Parameter()] [string] $RuntimeIdentifier = "",
        [Parameter(Mandatory)] [string] $SelfContained,
        [Parameter(Mandatory)] [string] $OutputPath,
        [Parameter(Mandatory)] [string] $ArtifactsPath,
        [Parameter(Mandatory)] [string] $InformationalVersion
    )

    $arguments = @(
        "publish", $ProjectPath,
        "-c", $Configuration
    )

    if (-not [string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        $arguments += @("-r", $RuntimeIdentifier)
    }

    $baseOutputPath = Join-Path $ArtifactsPath "bin"
    if (-not $baseOutputPath.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString())) {
        $baseOutputPath += [System.IO.Path]::DirectorySeparatorChar
    }

    $arguments += @(
        "--self-contained", $SelfContained,
        "--no-restore",
        "--artifacts-path", $ArtifactsPath,
        "-p:BaseOutputPath=$baseOutputPath",
        "-p:UseAppHost=true",
        "-p:InformationalVersion=$InformationalVersion",
        "-o", $OutputPath
    )

    if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier) -and $SelfContained -eq "false") {
        $arguments += @("-p:PlatformTarget=AnyCPU")
    }

    return $arguments
}

function Publish-Application {
    param(
        [Parameter(Mandatory)] [string] $Configuration,
        [Parameter(Mandatory)] [string] $DistributionMode,
        [Parameter(Mandatory)] [string] $Platform,
        [Parameter(Mandatory)] [string] $FrontendProject,
        [Parameter(Mandatory)] [string] $BackendProject,
        [Parameter(Mandatory)] [string] $TrayHostProject,
        [Parameter(Mandatory)] [string] $ProbeHostProject,
        [Parameter(Mandatory)] [string] $PublishRoot,
        [Parameter(Mandatory)] [string] $NuGetConfig,
        [Parameter(Mandatory)] [string] $PublishGroup,
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [ValidateSet("Installer", "Portable")] [string] $PackageKind
    )

    $distributionOptions = Get-DistributionModeOptions -DistributionMode $DistributionMode
    $runtimeIdentifier = Get-RuntimeIdentifier -Platform $Platform
    $platformLabel = Get-ArtifactPlatformLabel -Platform $Platform

    $publishDir = Join-Path $PublishRoot (Join-Path $PublishGroup (Join-Path $DistributionMode $Platform))
    $taskArtifactsRoot = Join-Path $PublishRoot (Join-Path "_artifacts" (Join-Path $PublishGroup (Join-Path $DistributionMode $Platform)))

    Remove-DirectoryIfExists -Path $publishDir

    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
    New-Item -ItemType Directory -Path $taskArtifactsRoot -Force | Out-Null

    $frontendRestoreArguments = New-DotNetRestoreArguments `
        -ProjectPath $FrontendProject `
        -RuntimeIdentifier $runtimeIdentifier `
        -NuGetConfig $NuGetConfig `
        -ArtifactsPath $taskArtifactsRoot

    $frontendPublishArguments = New-DotNetPublishArguments `
        -ProjectPath $FrontendProject `
        -Configuration $Configuration `
        -RuntimeIdentifier $runtimeIdentifier `
        -SelfContained $distributionOptions.SelfContained `
        -OutputPath $publishDir `
        -ArtifactsPath $taskArtifactsRoot `
        -InformationalVersion $Version

    if ($Platform -eq "anycpu") {
        $frontendPublishArguments += "-p:NativeProbeHostPlatforms=Win32%2Cx64%2CARM64"
    }
    else {
        $frontendPublishArguments += "-p:SkipProbeHostArchitectureBuild=true"
    }

    $backendRestoreArguments = New-DotNetRestoreArguments `
        -ProjectPath $BackendProject `
        -RuntimeIdentifier $runtimeIdentifier `
        -NuGetConfig $NuGetConfig `
        -ArtifactsPath $taskArtifactsRoot

    $backendPublishArguments = New-DotNetPublishArguments `
        -ProjectPath $BackendProject `
        -Configuration $Configuration `
        -RuntimeIdentifier $runtimeIdentifier `
        -SelfContained $distributionOptions.SelfContained `
        -OutputPath $publishDir `
        -ArtifactsPath $taskArtifactsRoot `
        -InformationalVersion $Version

    $trayHostRestoreArguments = New-DotNetRestoreArguments `
        -ProjectPath $TrayHostProject `
        -RuntimeIdentifier $runtimeIdentifier `
        -NuGetConfig $NuGetConfig `
        -ArtifactsPath $taskArtifactsRoot

    $trayHostPublishArguments = New-DotNetPublishArguments `
        -ProjectPath $TrayHostProject `
        -Configuration $Configuration `
        -RuntimeIdentifier $runtimeIdentifier `
        -SelfContained $distributionOptions.SelfContained `
        -OutputPath $publishDir `
        -ArtifactsPath $taskArtifactsRoot `
        -InformationalVersion $Version

    Invoke-External -FilePath "dotnet" -Arguments $frontendRestoreArguments -ErrorMessage "dotnet restore failed for frontend ($platformLabel, $DistributionMode)"
    Invoke-External -FilePath "dotnet" -Arguments $frontendPublishArguments -ErrorMessage "dotnet publish failed for frontend ($platformLabel, $DistributionMode)"

    Invoke-External -FilePath "dotnet" -Arguments $backendRestoreArguments -ErrorMessage "dotnet restore failed for backend ($platformLabel, $DistributionMode)"
    Invoke-External -FilePath "dotnet" -Arguments $backendPublishArguments -ErrorMessage "dotnet publish failed for backend ($platformLabel, $DistributionMode)"

    Invoke-External -FilePath "dotnet" -Arguments $trayHostRestoreArguments -ErrorMessage "dotnet restore failed for tray host ($platformLabel, $DistributionMode)"
    Invoke-External -FilePath "dotnet" -Arguments $trayHostPublishArguments -ErrorMessage "dotnet publish failed for tray host ($platformLabel, $DistributionMode)"

    $msBuildPath = $null
    $probeHostLicense = Get-NlohmannJsonLicensePath -NativeProbeHostProject $ProbeHostProject
    Ensure-FileExists -Path $probeHostLicense -Description "nlohmann/json license notice"
    $probeHostLabels = @()
    foreach ($probeHostArchitecture in @(Get-ProbeHostArchitectureMap -Platform $Platform)) {
        $probeHostLabel = [string] $probeHostArchitecture.Label
        $probeHostLabels += $probeHostLabel
        $probeHostOutput = Join-Path $publishDir (Join-Path "ProbeHost" $probeHostLabel)
        $probeHostIntermediate = Join-Path $taskArtifactsRoot (Join-Path "probehost-native-obj" $probeHostLabel)
        if ($null -eq $msBuildPath) {
            $msBuildPath = Resolve-MSBuildPath
        }

        Invoke-NativeProbeHostBuild `
            -MSBuildPath $msBuildPath `
            -NativeProbeHostProject $ProbeHostProject `
            -Configuration $Configuration `
            -Label $probeHostLabel `
            -OutputDirectory $probeHostOutput `
            -IntermediateDirectory $probeHostIntermediate
        Ensure-FileExists -Path (Join-Path $probeHostOutput "ContextMenuMgr.ProbeHost.exe") -Description "ProbeHost executable ($probeHostLabel)"
    }

    if ($Platform -eq "anycpu" -and $probeHostLabels.Count -eq 0) {
        $probeHostLabels = @("x86", "x64", "arm64")
    }

    $thirdPartyNoticeDir = Join-Path $publishDir "ThirdPartyNotices"
    New-Item -ItemType Directory -Path $thirdPartyNoticeDir -Force | Out-Null
    Copy-Item -LiteralPath $probeHostLicense -Destination (Join-Path $thirdPartyNoticeDir "nlohmann-json-LICENSE.MIT") -Force
    Ensure-FileExists -Path (Join-Path $thirdPartyNoticeDir "nlohmann-json-LICENSE.MIT") -Description "nlohmann/json license notice"

    $probeHostRoot = Join-Path $publishDir "ProbeHost"
    if (Test-Path -LiteralPath $probeHostRoot -PathType Container) {
        $verifier = Join-Path (Split-Path -Parent $PSScriptRoot) "Scripts\Verify-ProbeHostArchitecture.ps1"
        if (-not (Test-Path -LiteralPath $verifier -PathType Leaf)) {
            $verifier = Join-Path $PSScriptRoot "Verify-ProbeHostArchitecture.ps1"
        }

        $verifyArguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $verifier, "-Root", $probeHostRoot)
        if ($probeHostLabels.Count -gt 0) {
            $verifyArguments += "-Labels"
            $verifyArguments += $probeHostLabels
        }

        Invoke-External `
            -FilePath "powershell" `
            -Arguments $verifyArguments `
            -ErrorMessage "ProbeHost architecture verification failed"
    }

    Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManagerPlus.exe") -Description "Frontend executable"
    Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManagerPlus.Service.exe") -Description "Backend service executable"
    Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManagerPlus.Service.dll") -Description "Backend service DLL"
    Ensure-FileExists -Path (Join-Path $publishDir "ContextMenuManagerPlus.TrayHost.exe") -Description "Tray host executable"

    Assert-TrayHostLocalizationArtifacts -PublishDir $publishDir
    Write-PackageManifest -PublishDir $publishDir -PackageKind $PackageKind

    return $publishDir
}

function Test-BinaryContainsUtf8String {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Needle
    )

    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $Path).Path)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    return $text.Contains($Needle)
}

function Assert-TrayHostLocalizationArtifacts {
    param([Parameter(Mandatory)] [string] $PublishDir)

    $trayHostDll = Join-Path $PublishDir "ContextMenuManagerPlus.TrayHost.dll"
    Ensure-FileExists -Path $trayHostDll -Description "TrayHost assembly for localization validation"

    Write-Host "Verifying TrayHost localization artifacts in $PublishDir ..."

    $expectedNeutral = "ContextMenuMgr.TrayHost.Resources.Strings.resources"
    if (-not (Test-BinaryContainsUtf8String -Path $trayHostDll -Needle $expectedNeutral)) {
        throw "TrayHost neutral resource '$expectedNeutral' was not found in ContextMenuManagerPlus.TrayHost.dll via binary scan."
    }

    Write-Host "  Neutral resource '$expectedNeutral' found via binary scan."

    $satelliteCultures = @("zh-CN", "zh-TW")
    foreach ($culture in $satelliteCultures) {
        $satellitePath = Join-Path $PublishDir (Join-Path $culture "ContextMenuManagerPlus.TrayHost.resources.dll")
        if (-not (Test-Path -LiteralPath $satellitePath -PathType Leaf)) {
            Write-Warning "  TrayHost satellite resource '$culture' is missing: $satellitePath. Fallback dictionary will be used at runtime."
            continue
        }

        Write-Host "  Satellite resource '$culture': present"

        $expectedSat = "ContextMenuMgr.TrayHost.Resources.Strings.$culture.resources"
        if (Test-BinaryContainsUtf8String -Path $satellitePath -Needle $expectedSat) {
            Write-Host "    Contains: $expectedSat"
        }
        else {
            Write-Warning "  Satellite '$culture' does not appear to contain '$expectedSat'. Fallback dictionary will be used at runtime."
        }
    }

    Write-Host "TrayHost localization artifacts validation complete."
}

function Write-PackageManifest {
    param(
        [Parameter(Mandatory)] [string] $PublishDir,
        [Parameter(Mandatory)] [ValidateSet("Installer", "Portable")] [string] $PackageKind
    )

    New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
    $manifestPath = Join-Path $PublishDir "ContextMenuMgr.package.json"
    $payload = [ordered] @{ packageKind = $PackageKind }
    $payload | ConvertTo-Json -Compress | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Ensure-FileExists -Path $manifestPath -Description "Runtime package manifest"
    Write-Host "Runtime package manifest: $manifestPath ($PackageKind)"
}

function Copy-PublishDirectory {
    param(
        [Parameter(Mandatory)] [string] $SourceDir,
        [Parameter(Mandatory)] [string] $DestinationDir
    )

    Remove-DirectoryIfExists -Path $DestinationDir
    New-Item -ItemType Directory -Path $DestinationDir -Force | Out-Null
    Get-ChildItem -LiteralPath $SourceDir -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $DestinationDir -Recurse -Force
    }
}

function New-PortableArchive {
    param(
        [Parameter(Mandatory)] [string] $PublishDir,
        [Parameter(Mandatory)] [string] $DistRoot,
        [Parameter(Mandatory)] [string] $ArtifactProductName,
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [string] $DistributionMode,
        [Parameter(Mandatory)] [string] $Platform
    )

    $platformLabel = Get-ArtifactPlatformLabel -Platform $Platform

    if ($DistributionMode -eq "framework-dependent" -and $platformLabel -eq "anycpu") {
        $archiveFileName = "$ArtifactProductName-$Version-framework-dependent-portable.zip"
    }
    else {
        $archiveFileName = "$ArtifactProductName-$Version-$platformLabel-$DistributionMode-portable.zip"
    }

    $archivePath = Join-Path $DistRoot $archiveFileName

    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    New-Item -ItemType Directory -Path $DistRoot -Force | Out-Null
    Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $archivePath -CompressionLevel Optimal -Force
    Ensure-FileExists -Path $archivePath -Description "Portable package"

    return $archivePath
}

function Invoke-InstallerBuildTarget {
    param(
        [Parameter(Mandatory)] [string] $Configuration,
        [Parameter(Mandatory)] [string] $DistributionMode,
        [Parameter(Mandatory)] [string] $Platform,
        [Parameter(Mandatory)] [string] $FrontendProject,
        [Parameter(Mandatory)] [string] $BackendProject,
        [Parameter(Mandatory)] [string] $TrayHostProject,
        [Parameter(Mandatory)] [string] $ProbeHostProject,
        [Parameter(Mandatory)] [string] $PublishRoot,
        [Parameter(Mandatory)] [string] $DistRoot,
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [string] $ArtifactProductName,
        [Parameter(Mandatory)] [string] $IsccPath,
        [Parameter(Mandatory)] [string] $InstallerIss,
        [Parameter(Mandatory)] [string] $AppId,
        [Parameter(Mandatory)] [string] $NuGetConfig
    )

    $distributionOptions = Get-DistributionModeOptions -DistributionMode $DistributionMode
    $platformLabel = Get-ArtifactPlatformLabel -Platform $Platform
    $publishDir = Publish-Application `
        -Configuration $Configuration `
        -DistributionMode $DistributionMode `
        -Platform $Platform `
        -FrontendProject $FrontendProject `
        -BackendProject $BackendProject `
        -TrayHostProject $TrayHostProject `
        -ProbeHostProject $ProbeHostProject `
        -PublishRoot $PublishRoot `
        -NuGetConfig $NuGetConfig `
        -PublishGroup "installer" `
        -Version $Version `
        -PackageKind "Installer"

    $installerOptions = Get-InstallerArchitectureOptions -Platform $Platform
    $setupBaseName = "$ArtifactProductName-$Version-$platformLabel-$($distributionOptions.InstallerSuffix)-Setup"

    $isccArguments = @(
        "/DMyArchitecturesAllowed=$($installerOptions.Allowed)",
        "/DMyArchitecturesInstallIn64BitMode=$($installerOptions.InstallIn64BitMode)",
        "/DMyUseDotNetDependencyInstaller=$($distributionOptions.UseDotNetDependencyInstaller)",
        "/DMyAppId=$AppId",
        "/DMyAppBuildDir=$publishDir",
        "/DMyOutputDir=$DistRoot",
        "/DMyAppSetupName=$setupBaseName",
        $InstallerIss
    )

    New-Item -ItemType Directory -Path $DistRoot -Force | Out-Null
    Invoke-External -FilePath $IsccPath -Arguments $isccArguments -ErrorMessage "Inno Setup packaging failed for $platformLabel ($DistributionMode)"

    $artifacts = New-Object System.Collections.Generic.List[string]
    $installerPath = Join-Path $DistRoot ($setupBaseName + ".exe")
    Ensure-FileExists -Path $installerPath -Description "Installer package"
    $artifacts.Add($installerPath) | Out-Null

    if ($DistributionMode -eq "self-contained") {
        $portablePublishDir = Join-Path $PublishRoot (Join-Path "_portable-staging" (Join-Path $DistributionMode $Platform))
        Copy-PublishDirectory -SourceDir $publishDir -DestinationDir $portablePublishDir
        Write-PackageManifest -PublishDir $portablePublishDir -PackageKind "Portable"

        $portableArchive = New-PortableArchive `
            -PublishDir $portablePublishDir `
            -DistRoot $DistRoot `
            -ArtifactProductName $ArtifactProductName `
            -Version $Version `
            -DistributionMode $DistributionMode `
            -Platform $Platform
        $artifacts.Add($portableArchive) | Out-Null
    }

    return $artifacts.ToArray()
}

function Invoke-PortableFrameworkDependentBuildTarget {
    param(
        [Parameter(Mandatory)] [string] $Configuration,
        [Parameter(Mandatory)] [string] $FrontendProject,
        [Parameter(Mandatory)] [string] $BackendProject,
        [Parameter(Mandatory)] [string] $TrayHostProject,
        [Parameter(Mandatory)] [string] $ProbeHostProject,
        [Parameter(Mandatory)] [string] $PublishRoot,
        [Parameter(Mandatory)] [string] $DistRoot,
        [Parameter(Mandatory)] [string] $Version,
        [Parameter(Mandatory)] [string] $ArtifactProductName,
        [Parameter(Mandatory)] [string] $NuGetConfig
    )

    $publishDir = Publish-Application `
        -Configuration $Configuration `
        -DistributionMode "framework-dependent" `
        -Platform "anycpu" `
        -FrontendProject $FrontendProject `
        -BackendProject $BackendProject `
        -TrayHostProject $TrayHostProject `
        -ProbeHostProject $ProbeHostProject `
        -PublishRoot $PublishRoot `
        -NuGetConfig $NuGetConfig `
        -PublishGroup "portable" `
        -Version $Version `
        -PackageKind "Portable"

    return New-PortableArchive `
        -PublishDir $publishDir `
        -DistRoot $DistRoot `
        -ArtifactProductName $ArtifactProductName `
        -Version $Version `
        -DistributionMode "framework-dependent" `
        -Platform "anycpu"
}
