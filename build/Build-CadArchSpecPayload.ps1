[CmdletBinding()]
param(
    [string[]]$Bands,
    [string]$R24ApiPath,
    [string]$R25ApiPath,
    [string]$DotNetPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $portable = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
    $DotNetPath = if (Test-Path -LiteralPath $portable) { $portable } else { (Get-Command dotnet -ErrorAction Stop).Source }
}
$payload = Join-Path $repositoryRoot 'BatchPdfPublisherLauncher\Modules\ArchitectureAssistant\CadArchSpecEditor.bundle.zip'
$staging = Join-Path $repositoryRoot '.artifacts\architecture-payload'
$webProject = Join-Path $repositoryRoot 'CadArchSpecEditor\src\CadArchSpec.Editor.Web'
$webIndex = Join-Path $webProject 'dist\index.html'
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
if (Test-Path -LiteralPath $payload) { Expand-Archive -LiteralPath $payload -DestinationPath $staging -Force }

$npm = (Get-Command npm.cmd -ErrorAction Stop).Source
Push-Location $webProject
try {
    & $npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "建筑设计说明 Web 依赖还原失败，退出代码 $LASTEXITCODE。" }
    & $npm run build
    if ($LASTEXITCODE -ne 0) { throw "建筑设计说明 Web 编译失败，退出代码 $LASTEXITCODE。" }
}
finally {
    Pop-Location
}
if (-not (Test-Path -LiteralPath $webIndex)) { throw "建筑设计说明 Web 编译后缺少入口页面：$webIndex" }

foreach ($band in @($Bands | Select-Object -Unique)) {
    $isR25 = $band -eq 'R25'
    $apiPath = if ($isR25) { $R25ApiPath } else { $R24ApiPath }
    if ([string]::IsNullOrWhiteSpace($apiPath) -and -not $isR25) { throw "未提供 $band 的 AutoCAD API 路径。" }
    $projectName = if ($isR25) { 'CadArchSpec.Host.AutoCAD2026' } else { 'CadArchSpec.Host.AutoCAD2022' }
    $series = if ($isR25) { 'R25.1' } else { 'R24.1' }
    $project = Join-Path $repositoryRoot "CadArchSpecEditor\src\$projectName\$projectName.csproj"
    $output = Join-Path $repositoryRoot ".artifacts\architecture-$band"
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $arguments = @('build', $project, '-c', 'Release', '--nologo', '-o', $output)
    if ([string]::IsNullOrWhiteSpace($apiPath)) { $arguments += '-p:UseAutoCadNuGet=true' }
    else { $arguments += "-p:AutoCadSdkPath=$apiPath" }
    & $DotNetPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "建筑设计说明 $band 编译失败，退出代码 $LASTEXITCODE。" }
    $destination = Join-Path $staging "Contents\$series"
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $output -Force | Where-Object { $_.Extension -ne '.pdb' } |
        Copy-Item -Destination $destination -Recurse -Force
    $hostFile = Join-Path $destination "$projectName.dll"
    if (-not (Test-Path -LiteralPath $hostFile)) { throw "建筑设计说明 $band 缺少宿主 DLL：$hostFile" }
    $webFile = Join-Path $destination 'Web\index.html'
    if (-not (Test-Path -LiteralPath $webFile)) { throw "建筑设计说明 $band 缺少 Web 入口页面：$webFile" }
    $nativeLoader = Join-Path $destination 'runtimes\win-x64\native\WebView2Loader.dll'
    if (-not (Test-Path -LiteralPath $nativeLoader)) { throw "建筑设计说明 $band 缺少 WebView2Loader.dll：$nativeLoader" }
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'CadArchSpecEditor\src\CadArchSpec.Installer\Payload\CadArchSpecEditor.bundle\PackageContents.xml') `
    -Destination (Join-Path $staging 'PackageContents.xml') -Force
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $payload -CompressionLevel Optimal -Force
Write-Host "建筑设计说明模块已生成：$payload" -ForegroundColor Green
Get-FileHash -LiteralPath $payload -Algorithm SHA256
