[CmdletBinding()]
param(
    [string[]]$Bands,
    [string]$OutputRoot,
    [switch]$KeepIntermediate,
    [switch]$IncludePaddleOcr
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# MSBuild 默认会把被复用的编译节点留在后台。实测的害处有两个：一是这些常驻节点继续
# 占着 dist 里刚生成的 exe，于是紧接着再跑一次发布就会 "Access to the path ... denied"；
# 二是跨调用复用节点时，同一份源码会在不同次调用里编译出不同字节。发布构建要的是可
# 复现，所以关掉节点复用。
#
# 尚未解决：嵌入载荷（CadArchSpecEditor.bundle.zip、StairDetail.R25.zip）的 ZIP 哈希
# 在冷启动的首次构建里仍可能与上一次不同，之后同一会话内的重复构建则稳定。已排除的
# 原因：Compress-Archive 的目录条目时间戳（见各载荷脚本里的 New-DeterministicZip）、
# 启动器 exe 被 Assembly::LoadFrom 锁住、R25 载荷与 R24 共用中间目录。剩下的差异表现为
# 编译产出的 DLL 字节不同，怀疑与多个编译步骤共用工程目录下的 obj/bin 有关，尚未定位。
# 这不影响发布件的正确性——每次构建的 dist 与其内嵌载荷始终自洽，build-info.json 里的
# GitDirty 会如实反映"构建时工作区是否有改动"。
$env:MSBUILDDISABLENODEREUSE = '1'

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

# 刚被写入的 dist 产物可能被扫描程序或收尾中的编译进程短暂占用，于是紧接着再跑一次
# 发布就会 "Access to the path ... denied"。这类锁会自己消失，重试几次通常就过去了；
# 重试仍失败时跳过该条目并把路径报出来，而不是让整个发布失败——真正要紧的是发布包里
# 不能混进旧的 DLL，那件事由后面的"历史后缀 DLL"检查兜住。
function Remove-PathResilient([string]$Path, [int]$Attempts = 6, [switch]$SkipLocked) {
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return $true
        }
        catch {
            if ($attempt -eq $Attempts) {
                if ($SkipLocked) {
                    Write-Warning "清理 $Path 失败，将跳过（后续会用重建覆盖它）：$($_.Exception.Message)"
                    return $false
                }
                throw "无法清理 $Path：$($_.Exception.Message)。请先关闭占用该目录的 AutoCAD 或万落建筑工具启动器，然后重试。"
            }
            Start-Sleep -Milliseconds (400 * $attempt)
        }
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

# Capture source identity before any packaging step regenerates tracked payloads.
$sourceGitCommit = ''
$sourceGitBranch = ''
$sourceGitDirty = $false
if (Test-Path -LiteralPath (Join-Path $repositoryRoot '.git')) {
    $sourceGitCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null | Select-Object -First 1)
    $sourceGitBranch = (& git -C $repositoryRoot branch --show-current 2>$null | Select-Object -First 1)
    $sourceGitDirty = [bool](& git -C $repositoryRoot status --porcelain 2>$null | Select-Object -First 1)
}

$productVersionSource = Join-Path $repositoryRoot 'Shared\ProductVersion.cs'
$productVersionText = Get-Content -LiteralPath $productVersionSource -Raw
$productVersionMatch = [regex]::Match($productVersionText, 'Semantic\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $productVersionMatch.Success) { throw '无法从 Shared\ProductVersion.cs 读取产品版本。' }
$productVersion = $productVersionMatch.Groups[1].Value
foreach ($versionedFile in @(
    'StairDetail\src\WL.Stair.Core\Properties\AssemblyInfo.cs',
    'StairDetail\src\WL.Stair.Cad2022\Properties\AssemblyInfo.cs',
    'StairDetail\packaging\PackageContents.2022.xml',
    'CadArchSpecEditor\Directory.Build.props')) {
    $versionedText = Get-Content -LiteralPath (Join-Path $repositoryRoot $versionedFile) -Raw
    if ($versionedText -notmatch [regex]::Escape($productVersion)) {
        throw "组件版本未与 $productVersion 同步：$versionedFile"
    }
}

$installations = @(Find-AutoCadInstallations)

$available = @{}
foreach ($installation in $installations) {
    $band = Get-Band $installation.Year
    if (-not $available.ContainsKey($band) -or $installation.Year -gt $available[$band].Year) {
        $available[$band] = $installation
    }
}

# R25 targets .NET 8. Autodesk's official compile-time package allows a full
# R25 build on an R24-only workstation. AutoCAD still supplies runtime APIs.
if (-not $available.ContainsKey('R25')) {
    $available['R25'] = [pscustomobject]@{
        Year = 2026
        Path = ''
        Source = 'AutoCAD.NET 25.0.1'
    }
}

if (-not $Bands -or $Bands.Count -eq 0) { $Bands = @($available.Keys | Sort-Object) }
# Accept the documented "-Bands R24,R25" form. A [string[]] parameter binds the
# command-line token as one element (PowerShell only splits commas in argument
# *lists*, not in a single token), so each element is split again here.
$Bands = @($Bands | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToUpperInvariant() } |
    Where-Object { $_ } | Select-Object -Unique)
foreach ($band in $Bands) {
    if ($band -notin @('R24','R25')) { throw "不支持的 API 组：$band。当前仅支持 AutoCAD 2021-2026。" }
    if (-not $available.ContainsKey($band)) { throw "本机没有可用于 $band 的 AutoCAD API。R24 需要安装对应 CAD。" }
}

if (Test-Path -LiteralPath $OutputRoot) {
    # User projects, registrations and settings must survive an in-place update.
    # Clean only generated payloads and leave the legacy portable data available
    # for the new version's one-time migration to the stable AppData location.
    Get-ChildItem -LiteralPath $OutputRoot -Force |
        Where-Object { $_.Name -ne '用户配置文件' } |
        ForEach-Object { [void](Remove-PathResilient $_.FullName -SkipLocked) }
}
if (-not $KeepIntermediate -and (Test-Path -LiteralPath $artifactRoot)) { Remove-PathResilient $artifactRoot }
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
    # 发布清单会被用户看到（也是排障依据），因此不写入本机安装路径这类
    # 机器特定信息：它对用户没有意义，还会暴露构建机的目录结构。
    # 只在控制台输出路径，便于构建者自己核对用的是哪一套 API。
    $apiPathForLog = if ([string]::IsNullOrWhiteSpace($installation.Path)) { $installation.Source } else { $installation.Path }
    $apiSource = if ([string]::IsNullOrWhiteSpace($installation.Path)) { 'AutoCAD.NET NuGet（通用引用，不依赖本机 CAD）' } else { '本机已安装的 AutoCAD' }
    Write-Host "[$band] AutoCAD $($installation.Year): $apiPathForLog" -ForegroundColor Cyan

    if ($band -eq 'R25') {
        $dotnet = Find-DotNet8
        $project = Join-Path $repositoryRoot 'BatchPdfPublisher\BatchPdfPublisher.Net8.csproj'
        if ([string]::IsNullOrWhiteSpace($installation.Path)) {
            Invoke-Checked {
                & $dotnet build $project -c Release --nologo `
                    '-p:UseAutoCadNuGet=true' `
                    "-p:OutputPath=$bandOutput\" `
                    "-p:BaseIntermediateOutputPath=$bandObject" `
                    '-p:UseSharedCompilation=false'
            } "编译 $band"
        }
        else {
            Invoke-Checked {
                & $dotnet build $project -c Release --nologo `
                    "-p:AutoCadApiPath=$($installation.Path)" `
                    "-p:OutputPath=$bandOutput\" `
                    "-p:BaseIntermediateOutputPath=$bandObject" `
                    '-p:UseSharedCompilation=false'
            } "编译 $band"
        }
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
                "/p:UseSharedCompilation=false" `
                "/p:DefineConstants=$defineConstants" /v:minimal
        } "编译 $band"
    }

    $plugin = Join-Path $bandOutput 'BatchPdfPublisher.dll'
    if (-not (Test-Path -LiteralPath $plugin)) { throw "$band 未生成 BatchPdfPublisher.dll" }
    $buildRecords += [pscustomobject]@{
        Band = $band
        AutoCadYear = $installation.Year
        ApiSource = $apiSource
        PluginSha256 = (Get-FileHash -LiteralPath $plugin -Algorithm SHA256).Hash
    }
}

$windowsMetadata = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\UnionMetadata') -Filter Windows.winmd -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -match '^10\.' } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $windowsMetadata) { throw '找不到 Windows 10/11 SDK 的 Windows.winmd，无法编译本地 OCR Worker。' }
$ocrWorkerProject = Join-Path $repositoryRoot 'LineVisionOcrWorker\LineVisionOcrWorker.csproj'
$ocrWorkerOutput = Join-Path $artifactRoot 'linevision-ocr-worker'
New-Item -ItemType Directory -Path $ocrWorkerOutput -Force | Out-Null
Invoke-Checked {
    & $msbuild $ocrWorkerProject /t:Rebuild /p:Configuration=Release `
        "/p:TargetFrameworkVersion=$framework" `
        "/p:WindowsMetadataPath=$windowsMetadata" `
        "/p:UseSharedCompilation=false" `
        "/p:OutputPath=$ocrWorkerOutput\" /v:minimal
} '编译图像转 CAD 本地 OCR Worker'
$ocrWorker = Join-Path $ocrWorkerOutput 'LineVisionOcrWorker.exe'
if (-not (Test-Path -LiteralPath $ocrWorker)) { throw '本地 OCR Worker 没有生成。' }
foreach ($band in $Bands) { Copy-Item -LiteralPath $ocrWorker -Destination (Join-Path $OutputRoot "CadApi\$band\LineVisionOcrWorker.exe") -Force }

# PaddleOCR is a large shared component. Keep one copy at the product root
# instead of duplicating roughly 650 MB into every AutoCAD version folder.
$paddleOcrWorker = $null
if ($IncludePaddleOcr) {
    $paddleOutput = Join-Path $OutputRoot 'OcrEngine'
    & (Join-Path $repositoryRoot 'build\Build-LineVisionPaddleOcrWorker.ps1') -OutputRoot $paddleOutput
    if ($LASTEXITCODE -ne 0) { throw "PaddleOCR 用户组件打包失败，退出代码 $LASTEXITCODE" }
    $paddleOcrWorker = Join-Path $paddleOutput 'LineVisionPaddleOcrWorker\LineVisionPaddleOcrWorker.exe'
    if (-not (Test-Path -LiteralPath $paddleOcrWorker)) { throw "PaddleOCR 用户组件没有生成：$paddleOcrWorker" }
}

$vectorWorkerProject = Join-Path $repositoryRoot 'LineVisionVectorWorker\LineVisionVectorWorker.csproj'
$vectorWorkerOutput = Join-Path $artifactRoot 'linevision-vector-worker'
$vectorWorkerObject = Join-Path $artifactRoot 'obj-linevision-vector-worker\'
New-Item -ItemType Directory -Path $vectorWorkerOutput, $vectorWorkerObject -Force | Out-Null
Invoke-Checked {
    & $msbuild $vectorWorkerProject /t:Rebuild /p:Configuration=Release `
        "/p:TargetFrameworkVersion=$framework" "/p:OutputPath=$vectorWorkerOutput\" `
        "/p:BaseIntermediateOutputPath=$vectorWorkerObject" `
        "/p:UseSharedCompilation=false" /v:minimal
} '编译图像转 CAD 矢量化 Worker'
$vectorWorker = Join-Path $vectorWorkerOutput 'LineVisionVectorWorker.exe'
$vtracer = Join-Path $repositoryRoot 'ThirdParty\VTracer\win-x64\vtracer.exe'
if (-not (Test-Path -LiteralPath $vectorWorker) -or -not (Test-Path -LiteralPath $vtracer)) { throw '矢量化 Worker 或 vtracer.exe 没有生成。' }
foreach ($band in $Bands) {
    $bandPath = Join-Path $OutputRoot "CadApi\$band"
    Copy-Item -LiteralPath $vectorWorker, $vtracer -Destination $bandPath -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'ThirdParty\VTracer\LICENSE.txt') -Destination (Join-Path $bandPath 'VTracer-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'ThirdParty\SkeletonTracing\LICENSE.txt') -Destination (Join-Path $bandPath 'SkeletonTracing-LICENSE.txt') -Force
}

$lineVisionTests = Join-Path $repositoryRoot 'BatchPdfPublisher.Tests\BatchPdfPublisher.LineVision.Tests.csproj'
$testDotNet = Find-DotNet8
Invoke-Checked {
    $previousWorker = $env:WANLUO_LINEVISION_OCR_WORKER
    $previousPaddleWorker = $env:WANLUO_LINEVISION_PADDLE_WORKER
    $previousVectorWorker = $env:WANLUO_LINEVISION_VECTOR_WORKER
    try {
        Copy-Item -LiteralPath $vtracer -Destination $vectorWorkerOutput -Force
        $env:WANLUO_LINEVISION_OCR_WORKER = $ocrWorker
        $env:WANLUO_LINEVISION_PADDLE_WORKER = $paddleOcrWorker
        $env:WANLUO_LINEVISION_VECTOR_WORKER = $vectorWorker
        & $testDotNet run --project $lineVisionTests -c Release --nologo
    }
    finally {
        $env:WANLUO_LINEVISION_OCR_WORKER = $previousWorker
        $env:WANLUO_LINEVISION_PADDLE_WORKER = $previousPaddleWorker
        $env:WANLUO_LINEVISION_VECTOR_WORKER = $previousVectorWorker
    }
} '图像转 CAD 算法和 OCR 测试'

# 启动器“项目管理”窗口是纯 WinForms 装配，只有真正构造并显示一次才能发现
# 布局期异常（例如 SplitContainer 在拿到宽度前设 SplitterDistance 会抛异常）。
$launcherTests = Join-Path $repositoryRoot 'BatchPdfPublisher.Tests\BatchPdfPublisher.Launcher.Tests.csproj'
Invoke-Checked { & $testDotNet run --project $launcherTests -c Release --nologo } '启动器项目管理窗口冒烟测试'

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'Resources') -Destination (Join-Path $OutputRoot 'Resources') -Recurse -Force

# Custom hatch definitions are user-visible, portable resources. Keep the
# canonical copies under the plugin's user configuration folder so moving the
# entire plugin directory to another computer preserves the stair materials.
$hatchPatternSource = Join-Path $repositoryRoot 'StairDetail\assets\HatchPatterns'
$hatchPatternTarget = Join-Path $OutputRoot '用户配置文件\填充素材'
New-Item -ItemType Directory -Path $hatchPatternTarget -Force | Out-Null
Copy-Item -Path (Join-Path $hatchPatternSource '*.pat') -Destination $hatchPatternTarget -Force

# Rebuild the stair payload from the same source revision as the main plug-in.
if ($Bands -contains 'R24') {
    & (Join-Path $repositoryRoot 'build\Build-StairDetail-R24.ps1') -Configuration Release -AutoCadApiPath $available['R24'].Path
}
if ($Bands -contains 'R25') {
    $r25DotNet = Find-DotNet8
    & (Join-Path $repositoryRoot 'build\Build-StairDetail-R25.ps1') -Configuration Release `
        -AutoCadApiPath $available['R25'].Path -DotNetPath $r25DotNet
}
$payloadDotNet = Find-DotNet8
& (Join-Path $repositoryRoot 'build\Build-CadArchSpecPayload.ps1') -Bands $Bands `
    -R24ApiPath $(if ($available.ContainsKey('R24')) { $available['R24'].Path } else { '' }) `
    -R25ApiPath $(if ($available.ContainsKey('R25')) { $available['R25'].Path } else { '' }) `
    -DotNetPath $payloadDotNet

$launcherProject = Join-Path $repositoryRoot 'BatchPdfPublisherLauncher\BatchPdfPublisherLauncher.csproj'
$launcherObject = Join-Path $artifactRoot 'obj-launcher\'
Invoke-Checked {
    & $msbuild $launcherProject /t:Rebuild /p:Configuration=Release `
        "/p:TargetFrameworkVersion=$framework" `
        "/p:OutputPath=$OutputRoot\" `
        "/p:BaseIntermediateOutputPath=$launcherObject" `
        "/p:UseSharedCompilation=false" /v:minimal
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
$manifest = [ordered]@{
    Product = '万落建筑工具'
    ProductVersion = $productVersion
    BuiltAt = (Get-Date).ToString('o')
    GitCommit = $sourceGitCommit
    GitBranch = $sourceGitBranch
    GitDirty = $sourceGitDirty
    LauncherSha256 = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    PaddleOcrIncluded = [bool]$IncludePaddleOcr
    PaddleOcrSha256 = $(if ($paddleOcrWorker) { (Get-FileHash -LiteralPath $paddleOcrWorker -Algorithm SHA256).Hash } else { $null })
    Bands = $buildRecords
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputRoot 'build-info.json') -Encoding UTF8

$unexpected = Get-ChildItem (Join-Path $OutputRoot 'CadApi') -Recurse -Filter 'BatchPdfPublisher*.dll' |
    Where-Object { $_.Name -ne 'BatchPdfPublisher.dll' }
if ($unexpected) { throw "发布目录含历史后缀 DLL：$($unexpected.FullName -join ', ')" }

# 发布前核对启动器必需的嵌入模块，避免主 DLL 能运行但建筑说明或楼梯漏装。
# 这里必须从字节加载，不能用 Assembly::LoadFrom：在 .NET 上 LoadFrom 会把程序集文件
# 锁到当前进程结束，于是本次发布一切正常，紧接着再跑一次就会在这行之后的每处覆盖或
# 删除上报 "Access to the path ... denied"。Load(byte[]) 只读内容，不持有文件锁。
$embeddedNames = @([System.Reflection.Assembly]::Load(
    [System.IO.File]::ReadAllBytes($launcher)).GetManifestResourceNames())
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

# 功能区是网格排版，按钮文字统一用四字简称（见 FeatureRegistry.F 的最后一个参数）。
# 名字长短不一排版就不齐整，而"不齐整"在代码评审里看不出来，只有装上才看得见，
# 所以在这里卡住：每个 F(...) 调用的最后一个字符串参数必须是恰好四个字。
$featureLines = $featureText -split "`r?`n" | Where-Object { $_ -match '^\s*F\("' }
if ($featureLines.Count -eq 0) { throw '未能从功能登记表里解析出 F(...) 条目。' }
foreach ($line in $featureLines) {
    $shortName = [regex]::Match($line, ',\s*"([^"]*)"\s*\)\s*,?\s*$')
    if (-not $shortName.Success) {
        throw "功能登记表条目缺少四字简称（F(...) 的最后一个字符串参数）：$($line.Trim())"
    }
    $text = $shortName.Groups[1].Value
    if ($text.Length -ne 4) {
        throw "功能简称必须是四个字，实际为「$text」（$($text.Length) 个字）：$($line.Trim())"
    }
}
Write-Host "功能简称校验通过：$($featureLines.Count) 个功能均为四字简称" -ForegroundColor DarkGray

# 图层直达快捷键靠 AutoLISP 直接调用 .NET 的 LispFunction 传目标图层和预选集
# （setenv 只是兼容通道，AutoCAD 不保证它同步进 Windows 进程环境块）。函数名写在
# LayerCommandLisp 里，注册写在 Commands.cs 的 [LispFunction(...)] 上——两边改名
# 不同步就会静默失效，表现只是"图层快捷键按下去弹 GL 对话框"，很难查，所以在这里卡住。
$layerLispSource = Join-Path $repositoryRoot 'BatchPdfPublisher\Features\Shortcuts\LayerCommandLisp.cs'
if (-not (Test-Path -LiteralPath $layerLispSource)) { throw '缺少图层直达命令的 AutoLISP 生成器 LayerCommandLisp.cs。' }
$layerLispText = Get-Content -LiteralPath $layerLispSource -Raw
foreach ($constantName in @('SetFunctionName', 'SelectionFunctionName')) {
    $match = [regex]::Match($layerLispText, $constantName + '\s*=\s*"([^"]+)"')
    if (-not $match.Success) { throw "未能从 LayerCommandLisp.cs 解析出 $constantName。" }
    $functionName = $match.Groups[1].Value
    if ($commandText -notmatch ('LispFunction\("' + [regex]::Escape($functionName) + '"\)')) {
        throw ('图层暂存函数名不一致：LayerCommandLisp.' + $constantName + ' = ' + $functionName + '，但 Commands.cs 里没有 [LispFunction("' + $functionName + '")]。')
    }
}
Write-Host "图层直达命令校验通过：WLSETLAYER / WLSETSELECTION LispFunction 均已注册" -ForegroundColor DarkGray

# 图层命令的功能 id 前缀不能和固定功能的 id 撞车：固定功能里有一个 id 就叫
# layer_assignment（归层 GL），前缀若是 "layer_" 就会被当成"图层命令 assignment"，
# 于是归层的快捷键在设置窗口里改不了、统计图层命令时还会多出一条假的 "GL→归层"。
$layerIdsSource = Join-Path $repositoryRoot 'BatchPdfPublisher\Features\Shortcuts\LayerFeatureIds.cs'
if (-not (Test-Path -LiteralPath $layerIdsSource)) { throw '缺少图层命令 id 约定 LayerFeatureIds.cs。' }
$layerIdsText = Get-Content -LiteralPath $layerIdsSource -Raw
$prefixMatch = [regex]::Match($layerIdsText, 'Prefix\s*=\s*"([^"]+)"')
if (-not $prefixMatch.Success) { throw '未能从 LayerFeatureIds.cs 解析出 Prefix。' }
$layerPrefix = $prefixMatch.Groups[1].Value
$fixedIds = [regex]::Matches($featureText, 'F\("([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique
foreach ($fixedId in $fixedIds) {
    if ($fixedId.StartsWith($layerPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('固定功能 id 与图层命令前缀 "' + $layerPrefix + '" 撞车：' + $fixedId + '。请改 LayerFeatureIds.Prefix 或这个功能 id。')
    }
}
Write-Host "图层功能 id 校验通过：$($fixedIds.Count) 个固定功能 id 都不以「$layerPrefix」开头" -ForegroundColor DarkGray

Write-Host ''
Write-Host '干净发布完成：' -ForegroundColor Green
Write-Host $OutputRoot
$buildRecords | Format-Table Band, AutoCadYear, PluginSha256 -AutoSize
