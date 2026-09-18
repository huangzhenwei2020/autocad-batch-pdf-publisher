[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$AutoCadApiPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# Compress-Archive 会把每个条目的修改时间写进 ZIP 字节。文件条目取盘上的时间，
# 目录条目却取"打包这一刻"的时间（PowerShell 的行为），而暂存文件都来自刚刚的编译，
# 于是同一份源码每次打包出来都是不同的 ZIP，发布包的哈希无法复核。
# 这里自己按固定时间戳、固定条目顺序写 ZIP，让包只由内容决定。
function New-DeterministicZip([string]$SourceDirectory, [string]$DestinationPath) {
    Add-Type -AssemblyName System.IO.Compression | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
    $epoch = [datetime]'2000-01-01 00:00:00'
    $root = (Resolve-Path -LiteralPath $SourceDirectory).Path.TrimEnd('\')
    if (Test-Path -LiteralPath $DestinationPath) { Remove-Item -LiteralPath $DestinationPath -Force }
    $archive = [System.IO.Compression.ZipFile]::Open($DestinationPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $entries = @(Get-ChildItem -LiteralPath $root -Recurse -Force |
            ForEach-Object { [pscustomobject]@{ Full = $_.FullName; IsDir = $_.PSIsContainer } } |
            Sort-Object Full)
        foreach ($item in $entries) {
            $relative = $item.Full.Substring($root.Length + 1).Replace('\', '/')
            if ($item.IsDir) { $relative += '/' }
            $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $epoch
            if (-not $item.IsDir) {
                $input = [System.IO.File]::OpenRead($item.Full)
                try {
                    $output = $entry.Open()
                    try { $input.CopyTo($output) } finally { $output.Dispose() }
                } finally { $input.Dispose() }
            }
        }
    } finally { $archive.Dispose() }
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$stairRoot = Join-Path $repositoryRoot 'StairDetail'
$buildScript = Join-Path $stairRoot 'scripts\build.ps1'
$sourceRoot = Join-Path $stairRoot "src\WL.Stair.Cad2022\bin\$Configuration"
$payload = Join-Path $repositoryRoot 'BatchPdfPublisherLauncher\Modules\StairDetail\StairDetail.R24.zip'
$stagingRoot = Join-Path $repositoryRoot '.artifacts\stair-r24-payload'

if (-not (Test-Path -LiteralPath $buildScript)) {
    throw "未找到楼梯大样构建脚本：$buildScript"
}

& $buildScript -Configuration $Configuration -AutoCadApiPath $AutoCadApiPath

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

New-DeterministicZip -SourceDirectory $stagingRoot -DestinationPath $payload
Write-Host "楼梯大样 R24 模块已生成：$payload" -ForegroundColor Green
Get-FileHash -LiteralPath $payload -Algorithm SHA256
