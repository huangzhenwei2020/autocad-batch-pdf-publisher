[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)).TrimEnd('\')
$retainedPaths = New-Object 'System.Collections.Generic.List[string]'

function Remove-InRepository([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    if (-not $full.StartsWith($repositoryRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除仓库外路径：$full"
    }
    if (-not (Test-Path -LiteralPath $full)) { return }
    if ($PSCmdlet.ShouldProcess($full, '删除历史构建产物')) {
        try {
            Remove-Item -LiteralPath $full -Recurse -Force
        }
        catch {
            $retainedPaths.Add($full)
            Write-Warning "无法删除（通常是 AutoCAD 正在占用）：$full"
        }
    }
}

Get-ChildItem (Join-Path $repositoryRoot 'BatchPdfPublisher') -Directory -Filter 'obj-*' -ErrorAction SilentlyContinue | ForEach-Object { Remove-InRepository $_.FullName }
Get-ChildItem (Join-Path $repositoryRoot 'BatchPdfPublisherLauncher') -Directory -Filter 'obj-*' -ErrorAction SilentlyContinue | ForEach-Object { Remove-InRepository $_.FullName }

foreach ($projectRoot in @('BatchPdfPublisher', 'BatchPdfPublisherLauncher')) {
    foreach ($name in @('bin', 'obj')) { Remove-InRepository (Join-Path $repositoryRoot "$projectRoot\$name") }
}

# The architecture assistant keeps its distributable payload in source control,
# but NuGet/npm build caches are always reproducible and must not remain in the
# repository working tree.
Get-ChildItem (Join-Path $repositoryRoot 'CadArchSpecEditor') -Directory -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @('bin', 'obj', 'node_modules') } |
    Sort-Object FullName -Descending |
    ForEach-Object { Remove-InRepository $_.FullName }
Remove-InRepository (Join-Path $repositoryRoot 'CadArchSpecEditor\src\CadArchSpec.Editor.Web\dist')

foreach ($path in @(
    '.artifacts', 'CadApi', 'ArchitectureAssistant', 'StairDetail', 'WanLuoArchitecture', 'tmp', 'bin',
    'build\dotnet-sdk', 'build\bzs-net8', 'build\stair-bzs', 'build\ribbon-fix-r25',
    '批量PDF发布工具-v0.8.6-安装包', '万落建筑工具-v1.0.0-安装包',
    '万落建筑工具-v1.1.0-安装包', '万落建筑工具-v1.1.1-安装包'
)) { Remove-InRepository (Join-Path $repositoryRoot $path) }

Get-ChildItem (Join-Path $repositoryRoot 'build') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(obj-|out-|latest-|frame-|portable-|bzs-|stair-|ribbon-)' } |
    ForEach-Object { Remove-InRepository $_.FullName }

Get-ChildItem (Join-Path $repositoryRoot 'build') -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.log','.scr') -or $_.Name -in @('LoadIntoCad.cs','LoadIntoCad.exe','dotnet-install.ps1') } |
    ForEach-Object { Remove-InRepository $_.FullName }

foreach ($path in @('build\launcher-bzs', 'build\launcher-ribbon-fix', 'build\r19-style')) {
    Remove-InRepository (Join-Path $repositoryRoot $path)
}

foreach ($file in @(
    '建筑设计说明助手安装程序.exe', '万落建筑工具启动器.exe', '万落建筑工具启动器.pdb',
    'BatchPdfPublisher.dll', 'BatchPdfPublisher.pdb', 'Drawing1.dwl', 'Drawing1.dwl2'
)) { Remove-InRepository (Join-Path $repositoryRoot $file) }

if ($retainedPaths.Count -gt 0) {
    Write-Warning "其余内容已清理，但有 $($retainedPaths.Count) 个路径仍被占用。关闭全部 AutoCAD/天正后重新运行本脚本："
    $retainedPaths | ForEach-Object { Write-Warning "  $_" }
}
else {
    Write-Host '清理完成。dist 发布目录保留不动。' -ForegroundColor Green
}
