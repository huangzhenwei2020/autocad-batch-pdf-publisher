[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$AutoCadApiPath,
    [string]$DotNetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
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
Compress-Archive -Path (Join-Path $stagingRoot '*') -DestinationPath $payload -CompressionLevel Optimal -Force
Write-Host "楼梯大样 R25 模块已生成：$payload" -ForegroundColor Green
Get-FileHash -LiteralPath $payload -Algorithm SHA256
