[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)

# 建筑模型程序（BuildingModelStudio → 万落建筑模型.exe）的编译 + 自检。
#
# 为什么单独有个脚本：这个程序不在 Build-Release.ps1 的发布流程里（它是独立程序，
# 不打包进 CAD 插件），但它自己的画布曾经出过一次致命绘制 bug，所以自检必须能一条命令跑完。
#
#   --selftest-canvas : 不弹窗口，把平面画布真正画到离屏位图上（正常/极端视图、坏模型、坏 Graphics）
#   --selftest        : 真正构造并显示主窗口，强制绘制一次画布
#
# 用法：
#   pwsh -ExecutionPolicy Bypass -File .\build\Test-BuildingModelStudio.ps1

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw "找不到 .NET SDK：$dotnet（本仓库用 .tools\dotnet 下的 8.0 SDK 编译建筑模型程序）"
}

$project = Join-Path $root 'BuildingModelStudio\BuildingModelStudio.csproj'
if (-not $SkipBuild) {
    Write-Host "编译建筑模型程序（$Configuration）…" -ForegroundColor Cyan
    & $dotnet build $project -c $Configuration -v:m
    if ($LASTEXITCODE -ne 0) { throw "编译失败：$project" }
}

$dll = Join-Path $root "BuildingModelStudio\bin\$Configuration\net8.0-windows\万落建筑模型.dll"
if (-not (Test-Path -LiteralPath $dll)) { throw "找不到编译产物：$dll" }

foreach ($mode in @('--selftest-canvas', '--selftest')) {
    Write-Host "自检 $mode …" -ForegroundColor Cyan
    & $dotnet $dll $mode
    if ($LASTEXITCODE -ne 0) { throw "自检失败：$mode" }
}

Write-Host "建筑模型程序：编译 + 自检全部通过。" -ForegroundColor Green
