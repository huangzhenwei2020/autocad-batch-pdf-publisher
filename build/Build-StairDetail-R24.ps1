[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$stairRoot = Join-Path $repositoryRoot 'StairDetail'
$buildScript = Join-Path $stairRoot 'scripts\build.ps1'
$sourceRoot = Join-Path $stairRoot "src\WL.Stair.Cad2022\bin\$Configuration"
$payload = Join-Path $repositoryRoot 'BatchPdfPublisherLauncher\Modules\StairDetail\StairDetail.R24.zip'
$stagingRoot = Join-Path $repositoryRoot '.artifacts\stair-r24-payload'

if (-not (Test-Path -LiteralPath $buildScript)) {
    throw "未找到楼梯大样构建脚本：$buildScript"
}

& $buildScript -Configuration $Configuration

if (Test-Path -LiteralPath $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

$requiredFiles = @(
    'WL.Stair.Cad2022.dll',
    'WL.Stair.Core.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll',
    'Microsoft.Web.WebView2.Wpf.dll',
    'WebView2Loader.dll'
)
foreach ($file in $requiredFiles) {
    $source = Join-Path $sourceRoot $file
    if (-not (Test-Path -LiteralPath $source)) {
        throw "楼梯大样缺少发布文件：$source"
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stagingRoot $file) -Force
}

$hatchPatternSource = Join-Path $sourceRoot 'HatchPatterns'
if (-not (Test-Path -LiteralPath $hatchPatternSource)) {
    throw "楼梯大样缺少填充素材：$hatchPatternSource"
}
Copy-Item -LiteralPath $hatchPatternSource -Destination (Join-Path $stagingRoot 'HatchPatterns') -Recurse -Force

Compress-Archive -Path (Join-Path $stagingRoot '*') -DestinationPath $payload -CompressionLevel Optimal -Force
Write-Host "楼梯大样 R24 模块已生成：$payload" -ForegroundColor Green
Get-FileHash -LiteralPath $payload -Algorithm SHA256
