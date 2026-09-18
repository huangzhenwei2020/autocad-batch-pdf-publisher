[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$AutoCadApiPath,
    [string]$DotNetPath
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
if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $portable = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
    $DotNetPath = if (Test-Path -LiteralPath $portable) { $portable } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$project = Join-Path $repositoryRoot 'StairDetail\src\WL.Stair.Cad2026\WL.Stair.Cad2026.csproj'
$stagingRoot = Join-Path $repositoryRoot '.artifacts\stair-r25-payload'
$buildOutput = Join-Path $repositoryRoot '.artifacts\stair-r25-build'
$payload = Join-Path $repositoryRoot 'BatchPdfPublisherLauncher\Modules\StairDetail\StairDetail.R25.zip'
foreach ($directory in @($stagingRoot, $buildOutput)) {
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$buildArguments = @('build', $project, '-c', $Configuration, '--nologo', '-o', $buildOutput)
if ([string]::IsNullOrWhiteSpace($AutoCadApiPath)) { $buildArguments += '-p:UseAutoCadNuGet=true' }
else { $buildArguments += "-p:AutoCadApiPath=$AutoCadApiPath" }
& $DotNetPath @buildArguments
if ($LASTEXITCODE -ne 0) { throw "楼梯大样 R25 编译失败，退出代码 $LASTEXITCODE。" }

$requiredFiles = @(
    'WL.Stair.Cad2026.dll', 'WL.Stair.Core.dll',
    'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'Microsoft.Web.WebView2.Wpf.dll'
)
foreach ($file in $requiredFiles) {
    $source = Join-Path $buildOutput $file
    if (-not (Test-Path -LiteralPath $source)) { throw "楼梯大样 R25 缺少发布文件：$source" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $stagingRoot $file) -Force
}
$nativeLoader = Get-ChildItem -LiteralPath $buildOutput -Filter 'WebView2Loader.dll' -Recurse -File | Select-Object -First 1
if (-not $nativeLoader) { throw '楼梯大样 R25 缺少 WebView2Loader.dll。' }
Copy-Item -LiteralPath $nativeLoader.FullName -Destination (Join-Path $stagingRoot 'WebView2Loader.dll') -Force
$hatchPatternSource = Join-Path $repositoryRoot 'StairDetail\assets\HatchPatterns'
Copy-Item -LiteralPath $hatchPatternSource -Destination (Join-Path $stagingRoot 'HatchPatterns') -Recurse -Force
New-DeterministicZip -SourceDirectory $stagingRoot -DestinationPath $payload
Write-Host "楼梯大样 R25 模块已生成：$payload" -ForegroundColor Green
Get-FileHash -LiteralPath $payload -Algorithm SHA256
