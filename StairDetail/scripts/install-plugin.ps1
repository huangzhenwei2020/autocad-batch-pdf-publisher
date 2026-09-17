[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$InstallRoot = (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$bundleName = "WanLuoArchitecture2022.bundle"
$stagingBundleName = "WanLuoArchitecture2022-install-$PID.bundle"
$stagingBundle = Join-Path $repoRoot ("artifacts\" + $stagingBundleName)
$resolvedInstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$destination = Join-Path $resolvedInstallRoot $bundleName
$temporaryDestination = Join-Path $resolvedInstallRoot (".WanLuoArchitecture2022.installing." + $PID)

if (Get-Process -Name "acad" -ErrorAction SilentlyContinue) {
    throw "AutoCAD or TArch is running. Save the current drawings, close AutoCAD/TArch, and run the installer again."
}

& (Join-Path $PSScriptRoot "package-cad2022.ps1") `
    -Configuration $Configuration `
    -BundleName $stagingBundleName

$sourceDll = Join-Path $stagingBundle "Contents\Windows\2022\WL.Stair.Cad2022.dll"
$sourceManifest = Join-Path $stagingBundle "PackageContents.xml"
if (-not (Test-Path -LiteralPath $sourceDll) -or -not (Test-Path -LiteralPath $sourceManifest)) {
    throw "The installation package is incomplete: $stagingBundle"
}

New-Item -ItemType Directory -Path $resolvedInstallRoot -Force | Out-Null
if (Test-Path -LiteralPath $temporaryDestination) {
    Remove-Item -LiteralPath $temporaryDestination -Recurse -Force
}
Copy-Item -LiteralPath $stagingBundle -Destination $temporaryDestination -Recurse -Force

$installedDll = Join-Path $temporaryDestination "Contents\Windows\2022\WL.Stair.Cad2022.dll"
if (-not (Test-Path -LiteralPath $installedDll)) {
    throw "The staged installation does not contain WL.Stair.Cad2022.dll."
}

$backup = $null
try {
    if (Test-Path -LiteralPath $destination) {
        $backup = Join-Path $resolvedInstallRoot (".WanLuoArchitecture2022.backup." + (Get-Date -Format "yyyyMMddHHmmss"))
        Move-Item -LiteralPath $destination -Destination $backup
    }

    Move-Item -LiteralPath $temporaryDestination -Destination $destination
}
catch {
    if ($backup -and -not (Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $backup)) {
        Move-Item -LiteralPath $backup -Destination $destination
    }
    throw
}

if ($backup -and (Test-Path -LiteralPath $backup)) {
    Remove-Item -LiteralPath $backup -Recurse -Force
}

$installedFile = Get-Item -LiteralPath (Join-Path $destination "Contents\Windows\2022\WL.Stair.Cad2022.dll")
if (Test-Path -LiteralPath $stagingBundle) {
    Remove-Item -LiteralPath $stagingBundle -Recurse -Force
}
Write-Output ""
Write-Output "WanLuo Architecture stair plugin installed successfully."
Write-Output "Install location: $destination"
Write-Output "Plugin file: $($installedFile.FullName)"
Write-Output "Next step: start AutoCAD 2022/TArch and enter LTDY."
