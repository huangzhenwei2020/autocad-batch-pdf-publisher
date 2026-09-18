# 重新编译骨架矢量内核并部署到 dist，然后跑诊断。
# 之所以要写成脚本：这个 exe 之前出现过"编译其实失败了、却把旧产物复制过去"的情况，
# 于是拿旧内核测出一堆错数字。这里每一步都强制校验，任一环节不过就停。
[CmdletBinding()]
param(
    [string]$Image,
    [string]$Region,
    [switch]$SkipMeasure
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$projectDirectory = Join-Path $repositoryRoot 'LineVisionVectorWorker'
$project = Join-Path $projectDirectory 'LineVisionVectorWorker.csproj'
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { throw "找不到 MSBuild：$msbuild" }

Write-Host '[1/4] 清理编译矢量内核……' -ForegroundColor Cyan
foreach ($directory in @('obj', 'bin')) {
    $path = Join-Path $projectDirectory $directory
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
$log = & $msbuild $project /t:Rebuild /p:Configuration=Release /p:TargetFrameworkVersion=v4.8.1 /p:UseSharedCompilation=false /v:minimal 2>&1
$errors = @($log | Select-String 'error CS|error MSB')
if ($errors.Count -gt 0) {
    $errors | Select-Object -First 20 | ForEach-Object { Write-Host ('  ' + $_.Line.Trim()) -ForegroundColor Red }
    throw "矢量内核编译失败（$($errors.Count) 条错误）。"
}

$built = Join-Path $projectDirectory 'bin\Release\LineVisionVectorWorker.exe'
if (-not (Test-Path -LiteralPath $built)) { throw "编译未产出：$built" }

Write-Host '[2/4] 校验新参数确实编进了产物……' -ForegroundColor Cyan
# .NET 的字符串字面量以 UTF-16 存在元数据 #US 堆里，必须按 Unicode 读；按 ASCII 读会误判为缺失。
$text = [System.Text.Encoding]::Unicode.GetString([System.IO.File]::ReadAllBytes($built))
$missing = @()
foreach ($probe in @('--minimum', '--merge-gap', '--collinear', '--dump-mask', '01-binary.png')) {
    if (-not $text.Contains($probe)) { $missing += $probe }
}
if ($missing.Count -gt 0) { throw "产物里缺少参数：$($missing -join ', ')" }
Write-Host '  ✔ --minimum / --merge-gap / --collinear / --dump-mask 均已编入' -ForegroundColor Green

Write-Host '[3/4] 部署到 dist 的 R24 与 R25……' -ForegroundColor Cyan
foreach ($band in @('R24', 'R25')) {
    $target = Join-Path $repositoryRoot "dist\WanLuoArchitectureTools\CadApi\$band\LineVisionVectorWorker.exe"
    $directory = Split-Path $target -Parent
    if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    Copy-Item -LiteralPath $built -Destination $target -Force
    Write-Host "  ✔ $band" -ForegroundColor Green
}

if ($SkipMeasure) { return }
if ([string]::IsNullOrWhiteSpace($Image)) { Write-Host '[4/4] 未指定 -Image，跳过测量。' -ForegroundColor Yellow; return }

Write-Host '[4/4] 跑诊断……' -ForegroundColor Cyan
$dotnet = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
& $dotnet build (Join-Path $repositoryRoot 'LineVisionDiagnostics\LineVisionDiagnostics.csproj') -c Release --nologo | Out-Null
$exe = Join-Path $repositoryRoot 'LineVisionDiagnostics\bin\Release\net8.0-windows\LineVisionDiagnostics.exe'
$arguments = @('--image', $Image)
if (-not [string]::IsNullOrWhiteSpace($Region)) { $arguments += @('--region', $Region) }
& $exe @arguments
