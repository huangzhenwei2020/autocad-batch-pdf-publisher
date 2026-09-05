[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$AutoCadApiPath
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "Visual Studio Installer was not found. Install Visual Studio Build Tools with MSBuild."
}

$installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $installPath) {
    throw "A Visual Studio installation containing MSBuild was not found."
}

$msbuild = Join-Path $installPath "MSBuild\Current\Bin\MSBuild.exe"
$solution = Join-Path $repoRoot "WanLuoArchitecture.sln"
$frameworkReferenceRoot = Join-Path ${env:ProgramFiles(x86)} "Reference Assemblies\Microsoft\Framework\.NETFramework"
$framework = @("v4.8.1", "v4.8") |
    Where-Object { Test-Path -LiteralPath (Join-Path $frameworkReferenceRoot "$_\mscorlib.dll") } |
    Select-Object -First 1
if (-not $framework) {
    throw ".NET Framework 4.8 or 4.8.1 targeting pack was not found."
}

$arguments = @($solution, '/restore', '/m', "/p:Configuration=$Configuration", "/p:TargetFrameworkVersion=$framework", '/verbosity:minimal')
if (-not [string]::IsNullOrWhiteSpace($AutoCadApiPath)) { $arguments += "/p:AutoCad2022Dir=$AutoCadApiPath" }
& $msbuild @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$testRunner = Join-Path $repoRoot "tests\WL.Stair.Tests\bin\$Configuration\WL.Stair.Tests.exe"
& $testRunner
if ($LASTEXITCODE -ne 0) {
    throw "Tests failed with exit code $LASTEXITCODE."
}

