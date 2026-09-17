[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins")
)

$ErrorActionPreference = "Stop"
$bundleName = "WanLuoArchitecture2022.bundle"
$resolvedInstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
$destination = Join-Path $resolvedInstallRoot $bundleName

if (Get-Process -Name "acad" -ErrorAction SilentlyContinue) {
    throw "AutoCAD or TArch is running. Save the current drawings, close AutoCAD/TArch, and run the uninstaller again."
}

if (-not (Test-Path -LiteralPath $destination)) {
    Write-Output "WanLuo Architecture stair plugin is not installed."
    exit 0
}

$destinationPath = [System.IO.Path]::GetFullPath($destination)
$expectedParent = $resolvedInstallRoot.TrimEnd('\') + '\'
if ((-not $destinationPath.StartsWith($expectedParent, [System.StringComparison]::OrdinalIgnoreCase)) -or
    ([System.IO.Path]::GetFileName($destinationPath) -ne $bundleName)) {
    throw "Refusing to remove an unexpected path: $destinationPath"
}

Remove-Item -LiteralPath $destinationPath -Recurse -Force
Write-Output "WanLuo Architecture stair plugin was removed from: $destinationPath"
