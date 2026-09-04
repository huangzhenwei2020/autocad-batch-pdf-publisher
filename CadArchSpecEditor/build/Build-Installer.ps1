param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $projectRoot
$dotnet = Join-Path $workspaceRoot "tmp\dotnet-sdk-8\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = Join-Path $workspaceRoot ".tools\dotnet\dotnet.exe"
}
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = "dotnet"
}

$webRoot = Join-Path $projectRoot "src\CadArchSpec.Editor.Web"
$host2022 = Join-Path $projectRoot "src\CadArchSpec.Host.AutoCAD2022\bin\$Configuration\net48"
$host2026 = Join-Path $projectRoot "src\CadArchSpec.Host.AutoCAD2026\bin\$Configuration\net8.0-windows"
$installerRoot = Join-Path $projectRoot "src\CadArchSpec.Installer"
$payloadRoot = Join-Path $installerRoot "Payload"
$bundleRoot = Join-Path $payloadRoot "CadArchSpecEditor.bundle"
$payloadZip = Join-Path $workspaceRoot "BatchPdfPublisherLauncher\Modules\ArchitectureAssistant\CadArchSpecEditor.bundle.zip"
$releaseRoot = Join-Path $projectRoot "release"

Push-Location $webRoot
try {
    npm run build
}
finally {
    Pop-Location
}

$hostProjects = @(
    (Join-Path $projectRoot "src\CadArchSpec.Host.AutoCAD2022\CadArchSpec.Host.AutoCAD2022.csproj")
    (Join-Path $projectRoot "src\CadArchSpec.Host.AutoCAD2026\CadArchSpec.Host.AutoCAD2026.csproj")
)
foreach ($hostProject in $hostProjects) {
    & $dotnet build $hostProject -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "宿主编译失败：$hostProject" }
}

if (Test-Path -LiteralPath $bundleRoot) {
    Remove-Item -LiteralPath $bundleRoot -Recurse -Force
}
if (Test-Path -LiteralPath $payloadZip) {
    Remove-Item -LiteralPath $payloadZip -Force
}
New-Item -ItemType Directory -Path (Join-Path $bundleRoot "Contents\R24.1") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $bundleRoot "Contents\R25.1") -Force | Out-Null

$excludeExtensions = @(".pdb", ".xml")
Get-ChildItem -LiteralPath $host2022 -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring($host2022.Length).TrimStart('\')
    $target = Join-Path (Join-Path $bundleRoot "Contents\R24.1") $relative
    if ($_.PSIsContainer) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
    }
    elseif ($excludeExtensions -notcontains $_.Extension) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}
Get-ChildItem -LiteralPath $host2026 -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring($host2026.Length).TrimStart('\')
    $target = Join-Path (Join-Path $bundleRoot "Contents\R25.1") $relative
    if ($_.PSIsContainer) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
    }
    elseif ($excludeExtensions -notcontains $_.Extension) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}

$newtonsoft2022 = Join-Path $host2022 "Newtonsoft.Json.dll"
$newtonsoft2026 = Join-Path $bundleRoot "Contents\R25.1\Newtonsoft.Json.dll"
if ((Test-Path -LiteralPath $newtonsoft2022) -and -not (Test-Path -LiteralPath $newtonsoft2026)) {
    Copy-Item -LiteralPath $newtonsoft2022 -Destination $newtonsoft2026 -Force
}

$packageContents = @'
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" AutodeskProduct="AutoCAD" Name="CadArchSpecEditor" AppVersion="1.4.5" ProductCode="{1E296FC4-E75B-4B8B-80B7-CA2376D71D32}">
  <CompanyDetails Name="CadArchSpecEditor" />
  <Components>
    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R24.1" SeriesMax="R24.1" />
    <ComponentEntry AppName="CadArchSpecEditor-2022" ModuleName="./Contents/R24.1/CadArchSpec.Host.AutoCAD2022.dll" AppDescription="建筑设计说明助手" LoadReasons="LoadOnStartup" />
  </Components>
  <Components>
    <RuntimeRequirements OS="Win64" Platform="AutoCAD*" SeriesMin="R25.1" SeriesMax="R25.1" />
    <ComponentEntry AppName="CadArchSpecEditor-2026" ModuleName="./Contents/R25.1/CadArchSpec.Host.AutoCAD2026.dll" AppDescription="建筑设计说明助手" LoadReasons="LoadOnStartup" />
  </Components>
</ApplicationPackage>
'@
Set-Content -LiteralPath (Join-Path $bundleRoot "PackageContents.xml") -Value $packageContents -Encoding UTF8

Compress-Archive -Path (Join-Path $bundleRoot "*") -DestinationPath $payloadZip -CompressionLevel Optimal

& $dotnet restore (Join-Path $installerRoot "CadArchSpec.Installer.csproj")
if ($LASTEXITCODE -ne 0) { throw "安装器依赖还原失败。" }
& $dotnet build (Join-Path $installerRoot "CadArchSpec.Installer.csproj") -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "安装器编译失败。" }

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$installerExe = Join-Path $installerRoot "bin\$Configuration\net48\建筑设计说明助手安装程序.exe"
$releaseExe = Join-Path $releaseRoot "建筑设计说明助手安装程序.exe"
Copy-Item -LiteralPath $installerExe -Destination $releaseExe -Force
Copy-Item -LiteralPath $installerExe -Destination (Join-Path $workspaceRoot "建筑设计说明助手安装程序.exe") -Force

Write-Host "安装程序已生成：$releaseExe"
