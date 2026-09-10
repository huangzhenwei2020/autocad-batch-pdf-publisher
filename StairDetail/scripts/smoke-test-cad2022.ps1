[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$ArtifactFileName = "cad2022-smoke-test.dwg",

    [string]$BundleName = "WanLuoArchitecture2022.bundle"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$autoCadRoot = "C:\Program Files\Autodesk\AutoCAD 2022"
$console = Join-Path $autoCadRoot "accoreconsole.exe"
$template = Join-Path $env:LOCALAPPDATA "Autodesk\AutoCAD 2022\R24.1\chs\Template\acadiso.dwt"
$stagingRoot = Join-Path $env:LOCALAPPDATA "Temp\WLStair2022Smoke"
$result = Join-Path $stagingRoot "result.dwg"
$artifactResult = Join-Path $repoRoot ("artifacts\" + $ArtifactFileName)

if (-not (Test-Path -LiteralPath $console)) {
    throw "AutoCAD 2022 Core Console was not found at $console."
}

if (-not (Test-Path -LiteralPath $template)) {
    throw "AutoCAD 2022 metric template was not found at $template."
}

& (Join-Path $PSScriptRoot "package-cad2022.ps1") -Configuration $Configuration -BundleName $BundleName

New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
$packageBin = Join-Path $repoRoot ("artifacts\" + $BundleName + "\Contents\Windows\2022")
Copy-Item -LiteralPath (Join-Path $packageBin "WL.Stair.Cad2022.dll") -Destination $stagingRoot -Force
Copy-Item -LiteralPath (Join-Path $packageBin "WL.Stair.Core.dll") -Destination $stagingRoot -Force

$scriptPath = Join-Path $stagingRoot "smoke-test.scr"
$cadPluginPath = (Join-Path $stagingRoot "WL.Stair.Cad2022.dll").Replace("\", "/")
$cadResultPath = $result.Replace("\", "/")
$scriptLines = @(
    "FILEDIA",
    "0",
    "CMDECHO",
    "1",
    "SECURELOAD",
    "0",
    "_.NETLOAD",
    ('"' + $cadPluginPath + '"'),
    "WLSTAIRTEST",
    "_.ZOOM",
    "_E",
    "_.SAVEAS",
    "2018",
    ('"' + $cadResultPath + '"'),
    "_.QUIT"
)

Set-Content -LiteralPath $scriptPath -Value $scriptLines -Encoding ASCII
Remove-Item -LiteralPath $result -Force -ErrorAction SilentlyContinue

Push-Location $stagingRoot
try {
    & $console /i $template /s $scriptPath /l chs /product ACAD
    if ($LASTEXITCODE -ne 0) {
        throw "AutoCAD 2022 Core Console failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $result)) {
    throw "The smoke test did not create a DWG result."
}

Copy-Item -LiteralPath $result -Destination $artifactResult -Force
$resultFile = Get-Item -LiteralPath $artifactResult
if ($resultFile.Length -le 0) {
    throw "The smoke-test DWG is empty."
}

Write-Output "AutoCAD 2022 smoke test passed: $($resultFile.FullName) ($($resultFile.Length) bytes)"
