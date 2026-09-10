[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
if (-not $installPath) {
    throw "A Visual Studio installation containing MSBuild was not found."
}

$msbuild = Join-Path $installPath "MSBuild\Current\Bin\MSBuild.exe"
$cadProject = Join-Path $repoRoot "src\WL.Stair.Cad2024\WL.Stair.Cad2024.csproj"
& $msbuild $cadProject /restore /m /p:Configuration=$Configuration /verbosity:minimal
if ($LASTEXITCODE -ne 0) {
    throw "AutoCAD 2024 build failed with exit code $LASTEXITCODE."
}

$bundleRoot = Join-Path $repoRoot "artifacts\WanLuoArchitecture.bundle"
$contentsRoot = Join-Path $bundleRoot "Contents\Windows\2024"
New-Item -ItemType Directory -Path $contentsRoot -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $repoRoot "src\WL.Stair.Cad2024\bin\$Configuration\WL.Stair.Cad2024.dll") -Destination $contentsRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "src\WL.Stair.Core\bin\$Configuration\WL.Stair.Core.dll") -Destination $contentsRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "packaging\PackageContents.xml") -Destination $bundleRoot -Force

Write-Output "Package created: $bundleRoot"
