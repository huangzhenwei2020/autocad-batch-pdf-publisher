[CmdletBinding()]
param(
    [string]$PythonPath,
    [string]$ModelCacheRoot,
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if ([string]::IsNullOrWhiteSpace($PythonPath)) { $PythonPath = Join-Path $repositoryRoot '.tools\paddleocr-build\Scripts\python.exe' }
if ([string]::IsNullOrWhiteSpace($ModelCacheRoot)) { $ModelCacheRoot = Join-Path $env:USERPROFILE '.paddlex\official_models' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $repositoryRoot '.artifacts\linevision-paddle-worker' }
$PythonPath = [System.IO.Path]::GetFullPath($PythonPath)
$ModelCacheRoot = [System.IO.Path]::GetFullPath($ModelCacheRoot)
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)

if (-not (Test-Path -LiteralPath $PythonPath)) { throw "找不到 PaddleOCR 构建环境：$PythonPath" }
$safeParents = @()
$safeParents += [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot '.artifacts')).TrimEnd('\') + '\'
$safeParents += [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'dist')).TrimEnd('\') + '\'
$isSafeOutput = $false
foreach ($safeParent in $safeParents) {
    if ($OutputRoot.StartsWith($safeParent, [System.StringComparison]::OrdinalIgnoreCase)) { $isSafeOutput = $true; break }
}
if (-not $isSafeOutput) {
    throw "PaddleOCR 输出目录必须位于项目的 .artifacts 或 dist 目录下：$OutputRoot"
}

$versions = & $PythonPath -c "import json,paddle,paddleocr,PyInstaller; print(json.dumps({'paddle':paddle.__version__,'paddleocr':paddleocr.__version__,'pyinstaller':PyInstaller.__version__}))" 2>$null | ConvertFrom-Json
if ($versions.paddle -ne '3.3.1' -or $versions.paddleocr -ne '3.7.0' -or $versions.pyinstaller -ne '6.16.0') {
    throw "PaddleOCR 构建依赖版本不匹配：$($versions | ConvertTo-Json -Compress)"
}

$modelNames = @('PP-LCNet_x1_0_textline_ori', 'PP-OCRv6_small_det', 'PP-OCRv6_small_rec')
foreach ($name in $modelNames) {
    $model = Join-Path $ModelCacheRoot $name
    if (-not (Test-Path -LiteralPath (Join-Path $model 'inference.json')) -or -not (Test-Path -LiteralPath (Join-Path $model 'inference.pdiparams'))) {
        throw "固定 OCR 模型不完整：$model"
    }
}

if (Test-Path -LiteralPath $OutputRoot) { Remove-Item -LiteralPath $OutputRoot -Recurse -Force }
$workRoot = Join-Path $repositoryRoot '.artifacts\linevision-paddle-pyinstaller'
if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
New-Item -ItemType Directory -Path $OutputRoot, $workRoot -Force | Out-Null

$workerSource = Join-Path $repositoryRoot 'LineVisionPaddleOcrWorker\worker.py'
$arguments = @(
    '-m', 'PyInstaller', '--noconfirm', '--clean', '--onedir', '--console',
    '--name', 'LineVisionPaddleOcrWorker', '--distpath', $OutputRoot,
    '--workpath', $workRoot, '--specpath', $workRoot,
    '--collect-data', 'paddle', '--collect-binaries', 'paddle',
    '--collect-all', 'paddlex', '--collect-all', 'paddleocr',
    '--recursive-copy-metadata', 'paddlex'
)
foreach ($metadata in @('imagesize','opencv-contrib-python','pyclipper','pypdfium2','python-bidi','shapely')) {
    $arguments += @('--copy-metadata', $metadata)
}
$arguments += $workerSource
& $PythonPath @arguments
if ($LASTEXITCODE -ne 0) { throw "PaddleOCR Worker 打包失败，退出代码 $LASTEXITCODE" }

$packageRoot = Join-Path $OutputRoot 'LineVisionPaddleOcrWorker'
$executable = Join-Path $packageRoot 'LineVisionPaddleOcrWorker.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw "未生成 PaddleOCR Worker：$executable" }
$packagedModels = Join-Path $packageRoot 'models'
New-Item -ItemType Directory -Path $packagedModels -Force | Out-Null
foreach ($name in $modelNames) { Copy-Item -LiteralPath (Join-Path $ModelCacheRoot $name) -Destination $packagedModels -Recurse -Force }

$licenseRoot = Join-Path $packageRoot 'licenses'
New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null
$sitePackages = Join-Path (Split-Path (Split-Path $PythonPath -Parent) -Parent) 'Lib\site-packages'
$licenseSources = @{
    'PaddlePaddle-LICENSE.txt' = 'paddlepaddle-3.3.1.dist-info\LICENSE'
    'PaddleOCR-LICENSE.txt' = 'paddleocr-3.7.0.dist-info\LICENSE'
    'PaddleX-LICENSE.txt' = 'paddlex-3.7.2.dist-info\licenses\LICENSE'
}
foreach ($entry in $licenseSources.GetEnumerator()) {
    $source = Join-Path $sitePackages $entry.Value
    if (-not (Test-Path -LiteralPath $source)) { throw "缺少第三方许可证：$source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $licenseRoot $entry.Key) -Force
}

$capabilities = & $executable --capabilities | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $capabilities.EngineId -ne 'paddleocr-worker' -or $capabilities.ProtocolVersion -ne 2) {
    throw 'PaddleOCR Worker 能力探测失败。'
}
$files = @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | ForEach-Object {
    [ordered]@{
        Path = $_.FullName.Substring($packageRoot.TrimEnd('\').Length + 1).Replace('\','/')
        Size = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$manifest = [ordered]@{
    Component = 'LineVisionPaddleOcrWorker'
    ProtocolVersion = 2
    PaddleOCR = '3.7.0'
    PaddlePaddle = '3.3.1'
    Model = 'PP-OCRv6-small'
    Models = $modelNames
    BuiltAt = (Get-Date).ToString('o')
    Files = $files
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot 'component-manifest.json') -Encoding UTF8
Write-Host "PaddleOCR 用户组件已生成：$packageRoot" -ForegroundColor Green
