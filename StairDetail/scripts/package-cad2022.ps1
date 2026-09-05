[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$BundleName = "WanLuoArchitecture2022.bundle"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration

$bundleRoot = Join-Path $repoRoot ("artifacts\" + $BundleName)
$contentsRoot = Join-Path $bundleRoot "Contents\Windows\2022"
New-Item -ItemType Directory -Path $contentsRoot -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $repoRoot "src\WL.Stair.Cad2022\bin\$Configuration\WL.Stair.Cad2022.dll") -Destination $contentsRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "src\WL.Stair.Core\bin\$Configuration\WL.Stair.Core.dll") -Destination $contentsRoot -Force
$webViewPackage = Join-Path ${env:USERPROFILE} ".nuget\packages\microsoft.web.webview2\1.0.4078.44"
Copy-Item -LiteralPath (Join-Path $webViewPackage "lib\net462\Microsoft.Web.WebView2.Core.dll") -Destination $contentsRoot -Force
Copy-Item -LiteralPath (Join-Path $webViewPackage "lib\net462\Microsoft.Web.WebView2.Wpf.dll") -Destination $contentsRoot -Force
$nativeRoot = Join-Path $contentsRoot "runtimes\win-x64\native"
New-Item -ItemType Directory -Path $nativeRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $webViewPackage "runtimes\win-x64\native\WebView2Loader.dll") -Destination $nativeRoot -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "packaging\PackageContents.2022.xml") -Destination (Join-Path $bundleRoot "PackageContents.xml") -Force

Write-Output "Package created: $bundleRoot"
