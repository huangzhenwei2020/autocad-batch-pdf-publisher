[CmdletBinding()]
param(
    [string[]]$Bands = @('R24'),
    [string]$PortableRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if ([string]::IsNullOrWhiteSpace($PortableRoot)) {
    $PortableRoot = Split-Path $repositoryRoot -Parent
}
$PortableRoot = [System.IO.Path]::GetFullPath($PortableRoot).TrimEnd('\')

if ([string]::IsNullOrWhiteSpace($PortableRoot) -or $PortableRoot -eq [System.IO.Path]::GetPathRoot($PortableRoot)) {
    throw "便携版目录无效：$PortableRoot"
}
if ([System.IO.Path]::GetFullPath($repositoryRoot).StartsWith($PortableRoot + '\', [System.StringComparison]::OrdinalIgnoreCase) -eq $false) {
    throw "源码目录必须位于便携版目录之下。源码：$repositoryRoot；便携版：$PortableRoot"
}

$releaseRoot = Join-Path $repositoryRoot 'dist\WanLuoArchitectureTools'
& (Join-Path $PSScriptRoot 'Build-Release.ps1') -Bands $Bands
if ($LASTEXITCODE -ne 0) { throw "发布编译失败，退出代码 $LASTEXITCODE" }

# Only replace generated payloads. Never remove or overwrite unrelated files in
# the portable root; especially preserve the complete user configuration tree.
foreach ($band in $Bands) {
    $sourceBand = Join-Path $releaseRoot "CadApi\$band"
    $targetBand = Join-Path $PortableRoot "CadApi\$band"
    if (-not (Test-Path -LiteralPath $sourceBand)) { throw "发布目录缺少 $band：$sourceBand" }
    New-Item -ItemType Directory -Path $targetBand -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceBand 'BatchPdfPublisher.dll') -Destination $targetBand -Force
    Copy-Item -LiteralPath (Join-Path $sourceBand 'PdfSharp.dll') -Destination $targetBand -Force
}

Copy-Item -LiteralPath (Join-Path $releaseRoot '万落建筑工具启动器.exe') -Destination $PortableRoot -Force
Copy-Item -LiteralPath (Join-Path $releaseRoot 'Resources') -Destination $PortableRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $releaseRoot 'build-info.json') -Destination $PortableRoot -Force

$sourceHatches = Join-Path $releaseRoot '用户配置文件\填充素材'
$targetHatches = Join-Path $PortableRoot '用户配置文件\填充素材'
if (Test-Path -LiteralPath $sourceHatches) {
    New-Item -ItemType Directory -Path $targetHatches -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceHatches '*') -Destination $targetHatches -Force
}

$launcherHash = (Get-FileHash -LiteralPath (Join-Path $PortableRoot '万落建筑工具启动器.exe') -Algorithm SHA256).Hash
Write-Host "便携版已完整更新：$PortableRoot" -ForegroundColor Green
foreach ($band in $Bands) {
    $pluginHash = (Get-FileHash -LiteralPath (Join-Path $PortableRoot "CadApi\$band\BatchPdfPublisher.dll") -Algorithm SHA256).Hash
    Write-Host "$band 主插件：$pluginHash"
}
Write-Host "启动器：$launcherHash"
