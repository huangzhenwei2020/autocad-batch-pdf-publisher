param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path (Split-Path -Parent $root) "tmp\dotnet-sdk-8\dotnet.exe"
if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = "dotnet"
}

$webViewRoot = "C:\Program Files (x86)\Microsoft\EdgeWebView\Application"
$webViewVersion = Get-ChildItem -LiteralPath $webViewRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^\d+(\.\d+){3}$' } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1 -ExpandProperty Name

$targets = @(
    [pscustomobject]@{
        Name = "AutoCAD 2022"
        SdkPath = "C:\Program Files\Autodesk\AutoCAD 2022"
        Project = Join-Path $root "src\CadArchSpec.Host.AutoCAD2022\CadArchSpec.Host.AutoCAD2022.csproj"
        CanBuild = $true
    },
    [pscustomobject]@{
        Name = "AutoCAD 2026"
        SdkPath = "D:\Program Files\Autodesk\AutoCAD 2026"
        Project = Join-Path $root "src\CadArchSpec.Host.AutoCAD2026\CadArchSpec.Host.AutoCAD2026.csproj"
        CanBuild = $true
    }
)

Write-Host "WebView2 Runtime: $($webViewVersion ?? '未检测到')"
foreach ($target in $targets) {
    $sdkAvailable =
        (Test-Path -LiteralPath (Join-Path $target.SdkPath "acmgd.dll")) -and
        (Test-Path -LiteralPath (Join-Path $target.SdkPath "acdbmgd.dll")) -and
        (Test-Path -LiteralPath (Join-Path $target.SdkPath "accoremgd.dll"))

    if (-not $sdkAvailable) {
        Write-Warning "$($target.Name): 未找到托管 SDK，跳过真实宿主编译。"
        continue
    }

    Write-Host "$($target.Name): SDK 已找到，开始编译宿主。"
    & $dotnet build $target.Project -c $Configuration -p:AutoCadSdkPath="$($target.SdkPath)"
    if ($LASTEXITCODE -ne 0) {
        throw "$($target.Name) 宿主编译失败。"
    }
}
