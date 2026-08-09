[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Project,
    [Parameter(Mandatory)] [string] $Configuration,
    [Parameter(Mandatory)] [string] $Platforms,
    [Parameter(Mandatory)] [string] $OutputRoot,
    [Parameter(Mandatory)] [string] $IntermediateRoot
)

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) { throw "Visual Studio locator was not found: $vswhere" }
$msbuild = (& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($msbuild)) { throw 'MSBuild was not found.' }
foreach ($platform in $Platforms.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
    $label = switch ($platform.Trim()) { 'Win32' { 'x86' } 'x64' { 'x64' } 'ARM64' { 'arm64' } default { throw "Unsupported ShellProxy platform: $platform" } }
    $out = Join-Path $OutputRoot $label; $obj = Join-Path $IntermediateRoot $label
    New-Item -ItemType Directory -Force -Path $out, $obj | Out-Null
    & $msbuild $Project /nologo /m /t:Build "/p:Configuration=$Configuration" "/p:Platform=$($platform.Trim())" "/p:OutDir=$out\" "/p:IntDir=$obj\"
    if ($LASTEXITCODE -ne 0) { throw "ShellProxy build failed for $label." }
    if (-not (Test-Path -LiteralPath (Join-Path $out 'ContextMenuMgr.ShellProxy.dll'))) { throw "ShellProxy DLL was not produced for $label." }
}
