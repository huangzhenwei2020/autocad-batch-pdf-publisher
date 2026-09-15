[CmdletBinding()]
param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'dist'))
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $distRoot 'OptionalComponents\WanLuo-PaddleOCR-3.7.0.zip'
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$distPrefix = $distRoot.TrimEnd('\') + '\'
if (-not $OutputPath.StartsWith($distPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "PaddleOCR 用户组件包必须输出到项目 dist 目录：$OutputPath"
}

$stagingRoot = Join-Path $repositoryRoot '.artifacts\paddleocr-user-package'
$artifactPrefix = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '.artifacts')).TrimEnd('\') + '\'
if (-not $stagingRoot.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "PaddleOCR 暂存目录不安全：$stagingRoot"
}
if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

& (Join-Path $repositoryRoot 'build\Build-LineVisionPaddleOcrWorker.ps1') -OutputRoot $stagingRoot
if ($LASTEXITCODE -ne 0) { throw "PaddleOCR Worker 构建失败，退出代码 $LASTEXITCODE" }
$componentRoot = Join-Path $stagingRoot 'LineVisionPaddleOcrWorker'
foreach ($required in @('LineVisionPaddleOcrWorker.exe', 'component-manifest.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $componentRoot $required))) { throw "组件缺少文件：$required" }
}

$outputDirectory = Split-Path $OutputPath -Parent
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force }
Compress-Archive -LiteralPath $componentRoot -DestinationPath $OutputPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
Set-Content -LiteralPath ($OutputPath + '.sha256') -Value ($hash + '  ' + (Split-Path $OutputPath -Leaf)) -Encoding ASCII

Write-Host "PaddleOCR 用户组件包已生成：$OutputPath" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Green
