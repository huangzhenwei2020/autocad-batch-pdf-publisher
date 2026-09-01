[CmdletBinding()]
param(
    [string[]]$Bands,
    [string]$OutputRoot,
    [switch]$KeepIntermediate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'dist\WanLuoArchitectureTools'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$distRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'dist'))
$artifactRoot = Join-Path $repositoryRoot '.artifacts\release'

function Assert-ChildPath([string]$Path, [string]$Parent, [string]$Description) {
    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullParent, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description 必须位于 $Parent 之下，实际为 $Path"
    }
}

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        $candidate = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
        if ($candidate -and (Test-Path -LiteralPath $candidate)) { return $candidate }
    }
    $candidate = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio') -Filter MSBuild.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $candidate) { throw '找不到 Visual Studio Build Tools / MSBuild。' }
    return $candidate
}

function Test-AutoCadApi([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) { return $false }
    foreach ($file in @('acmgd.dll', 'acdbmgd.dll', 'accoremgd.dll', 'AdWindows.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $file))) { return $false }
    }
    return $true
}

function Add-AutoCadCandidate([System.Collections.Generic.List[object]]$Target, [string]$Path) {
    if (-not (Test-AutoCadApi $Path)) { return }
    $leaf = Split-Path $Path -Leaf
    $match = [regex]::Match($leaf, '(20\d{2})')
    if (-not $match.Success) { return }
    $year = [int]$match.Groups[1].Value
    if ($Target | Where-Object { [string]::Equals($_.Path, $Path, [System.StringComparison]::OrdinalIgnoreCase) }) { return }
    $normalizedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $Target.Add([pscustomobject]@{ Year = $year; Path = $normalizedPath })
}

function Find-AutoCadInstallations {
    $result = New-Object 'System.Collections.Generic.List[object]'

    # Only scan fixed local disks. Disconnected mapped/network drives can make
    # Test-Path block for tens of seconds.
    $fixedRoots = Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' -ErrorAction SilentlyContinue |
        ForEach-Object { $_.DeviceID + '\' }
    foreach ($driveRoot in $fixedRoots) {
        foreach ($year in 2021..2026) {
            Add-AutoCadCandidate $result (Join-Path $driveRoot "Program Files\Autodesk\AutoCAD $year")
            Add-AutoCadCandidate $result (Join-Path $driveRoot "Autodesk\AutoCAD $year")
        }
    }

    foreach ($registryRoot in @(
        'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Autodesk\AutoCAD',
        'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Autodesk\AutoCAD'
    )) {
        if (-not (Test-Path $registryRoot)) { continue }
        # Autodesk stores locations at release/flavour depth (for example
        # R24.1\ACAD-xxxx:409). Avoid a full recursive registry scan.
        $keys = New-Object 'System.Collections.Generic.List[object]'
        foreach ($releaseKey in Get-ChildItem $registryRoot -ErrorAction SilentlyContinue) {
            $keys.Add($releaseKey)
            foreach ($flavourKey in Get-ChildItem $releaseKey.PSPath -ErrorAction SilentlyContinue) { $keys.Add($flavourKey) }
        }
        foreach ($key in $keys) {
            $properties = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if ($null -eq $properties) { continue }
            foreach ($propertyName in @('ACADLOCATION', 'InstallLocation')) {
                $property = $properties.PSObject.Properties[$propertyName]
                if ($property -and $property.Value) { Add-AutoCadCandidate $result ([string]$property.Value) }
            }
        }
    }
    return @($result | Sort-Object Year, Path -Unique)
}

function Get-Band([int]$Year) {
    if ($Year -le 2024) { return 'R24' }
    return 'R25'
}

function Find-DotNet8 {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($command) {
        $sdks = & $command.Source --list-sdks 2>$null
        if ($sdks -match '^8\.') { return $command.Source }
    }
    $local = Join-Path $repositoryRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $local) {
        $sdks = & $local --list-sdks 2>$null
        if ($sdks -match '^8\.') { return $local }
    }
    # One-time migration path for older local workspaces. Clean-Workspace removes
    # this legacy SDK after a successful release; fresh clones should use a system
    # SDK or .tools\dotnet.
    $legacy = Join-Path $repositoryRoot 'build\dotnet-sdk\dotnet.exe'
    if (Test-Path -LiteralPath $legacy) {
        $sdks = & $legacy --list-sdks 2>$null
        if ($sdks -match '^8\.') { return $legacy }
    }
    throw '编译 R25（AutoCAD 2025-2026）需要 .NET 8 SDK。请安装 SDK 后重试，或暂时使用 -Bands R24。'
}

function Invoke-Checked([scriptblock]$Command, [string]$Description) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description 失败，退出代码 $LASTEXITCODE" }
}

Assert-ChildPath $OutputRoot $distRoot '发布输出目录'
Assert-ChildPath $artifactRoot (Join-Path $repositoryRoot '.artifacts') '中间目录'

$installations = @(Find-AutoCadInstallations)
if ($installations.Count -eq 0) { throw '未找到安装了 .NET API 的 AutoCAD 2021-2026。' }

$available = @{}
foreach ($installation in $installations) {
    $band = Get-Band $installation.Year
    if (-not $available.ContainsKey($band) -or $installation.Year -gt $available[$band].Year) {
        $available[$band] = $installation
    }
}

if (-not $Bands -or $Bands.Count -eq 0) { $Bands = @($available.Keys | Sort-Object) }
$Bands = @($Bands | ForEach-Object { $_.Trim().ToUpperInvariant() } | Select-Object -Unique)
foreach ($band in $Bands) {
    if ($band -notin @('R24','R25')) { throw "不支持的 API 组：$band。当前仅支持 AutoCAD 2021-2026。" }
    if (-not $available.ContainsKey($band)) { throw "本机没有可用于 $band 的 AutoCAD API。请安装对应 CAD，或使用 build\AutodeskSdk 方案。" }
}

if (Test-Path -LiteralPath $OutputRoot) {
    # User projects, registrations and settings must survive an in-place update.
    # Clean only generated payloads and leave the legacy portable data available
    # for the new version's one-time migration to the stable AppData location.
    Get-ChildItem -LiteralPath $OutputRoot -Force |
        Where-Object { $_.Name -ne '用户配置文件' } |
        Remove-Item -Recurse -Force
}
if (-not $KeepIntermediate -and (Test-Path -LiteralPath $artifactRoot)) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force }
New-Item -ItemType Directory -Path $OutputRoot, $artifactRoot -Force | Out-Null

$msbuild = Find-MSBuild
$frameworkReferenceRoot = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework'
$framework = @('v4.8.1','v4.8') | Where-Object { Test-Path -LiteralPath (Join-Path $frameworkReferenceRoot "$_\mscorlib.dll") } | Select-Object -First 1
if (-not $framework) { throw '未安装 .NET Framework 4.8/4.8.1 Targeting Pack。' }

$buildRecords = @()
foreach ($band in $Bands) {
    $installation = $available[$band]
    $bandOutput = Join-Path $OutputRoot "CadApi\$band"
    $bandObject = Join-Path $artifactRoot "obj-$band\"
    New-Item -ItemType Directory -Path $bandOutput, $bandObject -Force | Out-Null
    Write-Host "[$band] AutoCAD $($installation.Year): $($installation.Path)" -ForegroundColor Cyan

    if ($band -eq 'R25') {
        $dotnet = Find-DotNet8
        $project = Join-Path $repositoryRoot 'BatchPdfPublisher\BatchPdfPublisher.Net8.csproj'
        Invoke-Checked {
            & $dotnet build $project -c Release --nologo `
                "-p:AutoCadApiPath=$($installation.Path)" `
                "-p:OutputPath=$bandOutput\" `
                "-p:BaseIntermediateOutputPath=$bandObject"
        } "编译 $band"
    }
    else {
        $project = Join-Path $repositoryRoot 'BatchPdfPublisher\BatchPdfPublisher.csproj'
        $defineConstants = ''
        Invoke-Checked {
            & $msbuild $project /t:Rebuild /p:Configuration=Release `
                "/p:TargetFrameworkVersion=$framework" `
                "/p:AutoCadApiPath=$($installation.Path)" `
                "/p:OutputPath=$bandOutput\" `
                "/p:BaseIntermediateOutputPath=$bandObject" `
                "/p:DefineConstants=$defineConstants" /v:minimal
        } "编译 $band"
    }

    $plugin = Join-Path $bandOutput 'BatchPdfPublisher.dll'
    if (-not (Test-Path -LiteralPath $plugin)) { throw "$band 未生成 BatchPdfPublisher.dll" }
    $buildRecords += [pscustomobject]@{
        Band = $band
        AutoCadYear = $installation.Year
        ApiPath = $installation.Path
        PluginSha256 = (Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash
    }
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Resources') -Destination (Join-Path $OutputRoot 'Resources') -Recurse -Force

# Custom hatch definitions are user-visible, portable resources. Keep the
# canonical copies under the plugin's user configuration folder so moving the
# entire plugin directory to another computer preserves the stair materials.
$hatchPatternSource = Join-Path $repositoryRoot 'StairDetail\assets\HatchPatterns'
$hatchPatternTarget = Join-Path $OutputRoot '用户配置文件\填充素材'
New-Item -ItemType Directory -Path $hatchPatternTarget -Force | Out-Null
Copy-Item -Path (Join-Path $hatchPatternSource '*.pat') -Destination $hatchPatternTarget -Force

$launcherProject = Join-Path $repositoryRoot 'BatchPdfPublisherLauncher\BatchPdfPublisherLauncher.csproj'
$launcherObject = Join-Path $artifactRoot 'obj-launcher\'
Invoke-Checked {
    & $msbuild $launcherProject /t:Rebuild /p:Configuration=Release `
        "/p:TargetFrameworkVersion=$framework" `
        "/p:OutputPath=$OutputRoot\" `
        "/p:BaseIntermediateOutputPath=$launcherObject" /v:minimal
} '编译启动器'

$launcher = Join-Path $OutputRoot '万落建筑工具启动器.exe'
if (-not (Test-Path -LiteralPath $launcher)) { throw '未生成万落建筑工具启动器.exe' }

# PDB files are developer symbols, not runtime dependencies. Keeping them out of
# dist makes the installation directory unambiguous and prevents stale symbols
# from being mistaken for payloads.
Get-ChildItem $OutputRoot -Recurse -Filter '*.pdb' -File -ErrorAction SilentlyContinue | Remove-Item -Force

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination (Join-Path $OutputRoot 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'COMPATIBILITY.md') -Destination (Join-Path $OutputRoot 'COMPATIBILITY.md') -Force
if (Test-Path -LiteralPath (Join-Path $repositoryRoot 'docs\BUILD_AND_INSTALL.md')) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\BUILD_AND_INSTALL.md') -Destination (Join-Path $OutputRoot '构建与安装说明.md') -Force
}
if (Test-Path -LiteralPath (Join-Path $repositoryRoot 'docs\用户版安装说明.md')) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\用户版安装说明.md') -Destination (Join-Path $OutputRoot '用户版安装说明.md') -Force
}

# 源码发布目录可不携带 .git（便于用户只保留可编辑源码）。无 Git 时仍必须能完整构建。
$gitCommit = ''
$gitBranch = ''
$gitDirty = $false
if (Test-Path -LiteralPath (Join-Path $repositoryRoot '.git')) {
    $gitCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null | Select-Object -First 1)
    $gitBranch = (& git -C $repositoryRoot branch --show-current 2>$null | Select-Object -First 1)
    $gitDirty = [bool](& git -C $repositoryRoot status --porcelain 2>$null | Select-Object -First 1)
}
$manifest = [ordered]@{
    Product = '万落建筑工具'
    BuiltAt = (Get-Date).ToString('o')
    GitCommit = $gitCommit
    GitBranch = $gitBranch
    GitDirty = $gitDirty
    LauncherSha256 = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    Bands = $buildRecords
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'build-info.json') -Encoding UTF8

$unexpected = Get-ChildItem (Join-Path $OutputRoot 'CadApi') -Recurse -Filter 'BatchPdfPublisher*.dll' |
    Where-Object { $_.Name -ne 'BatchPdfPublisher.dll' }
if ($unexpected) { throw "发布目录含历史后缀 DLL：$($unexpected.FullName -join ', ')" }

# 发布前核对启动器必需的嵌入模块，避免主 DLL 能运行但建筑说明或楼梯漏装。
$launcherAssembly = [System.Reflection.Assembly]::LoadFrom($launcher)
$embeddedNames = @($launcherAssembly.GetManifestResourceNames())
foreach ($requiredResource in @(
    'WanluoArchitectureTools.CadArchSpecEditor.bundle.zip',
    'WanluoArchitectureTools.StairDetail.R24.zip',
    'WanluoArchitectureTools.StairDetail.R25.zip')) {
    if ($embeddedNames -notcontains $requiredResource) { throw "启动器缺少嵌入功能模块：$requiredResource" }
}

$featureSource = Join-Path $repositoryRoot 'BatchPdfPublisher\Features\Shortcuts\FeatureRegistry.cs'
$commandSource = Join-Path $repositoryRoot 'BatchPdfPublisher\Commands.cs'
if (-not (Test-Path -LiteralPath $featureSource) -or -not (Test-Path -LiteralPath $commandSource)) { throw '缺少统一功能登记表或命令入口。' }
$featureText = Get-Content -LiteralPath $featureSource -Raw
$commandText = Get-Content -LiteralPath $commandSource -Raw
$registeredCommands = [regex]::Matches($featureText, 'F\("[^"]+",\s*"[^"]+",\s*"([A-Z0-9_]+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
$externalCommands = @('WLJZSM','WLLTDY')
foreach ($registeredCommand in $registeredCommands) {
    if ($externalCommands -contains $registeredCommand) { continue }
    if ($commandText -notmatch ('CommandMethod\("' + [regex]::Escape($registeredCommand) + '"')) {
        throw "统一功能登记表中的命令未在主插件注册：$registeredCommand"
    }
}

Write-Host ''
Write-Host '干净发布完成：' -ForegroundColor Green
Write-Host $OutputRoot
$buildRecords | Format-Table Band, AutoCadYear, PluginSha256 -AutoSize
