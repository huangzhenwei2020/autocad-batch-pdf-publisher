[CmdletBinding()]
param(
    [string]$SdkRoot = (Join-Path $PSScriptRoot 'AutodeskSdk'),
    [string]$OutputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'CadApi'),
    [string[]]$Bands
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$matrixPath = Join-Path $PSScriptRoot 'CompatibilityMatrix.json'
$matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
if ($Bands -and $Bands.Count -gt 0) {
    $matrix = @($matrix | Where-Object { $Bands -contains $_.Band })
}

$msbuildCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe')
)
$msbuild = $null
foreach ($vswhere in $msbuildCandidates) {
    if (Test-Path -LiteralPath $vswhere) {
        $msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    }
}
if (-not $msbuild) {
    $msbuild = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio', 'H:\Program Files\Microsoft Visual Studio' -Filter MSBuild.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}

$results = foreach ($target in $matrix) {
    $sdkPath = Join-Path $SdkRoot $target.Band
    $missing = @('acmgd.dll', 'acdbmgd.dll', 'accoremgd.dll', 'AdWindows.dll') |
        Where-Object { -not (Test-Path -LiteralPath (Join-Path $sdkPath $_)) }
    if ($missing.Count -gt 0) {
        [pscustomobject]@{ Band = $target.Band; AutoCad = $target.AutoCad; Status = '缺少SDK'; Detail = "$sdkPath -> $($missing -join ', ')" }
        continue
    }

    $outputPath = Join-Path $OutputRoot $target.Band
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    $projectPath = Join-Path $repositoryRoot $target.Project
    try {
        if ($target.Framework -like 'net*') {
            $sdkList = & dotnet --list-sdks 2>$null
            if (-not ($sdkList -match '^8\.')) { throw '缺少 .NET 8 SDK' }
            & dotnet build $projectPath -c Release "-p:AutoCadApiPath=$sdkPath" "-p:OutputPath=$outputPath\\" --nologo
        }
        else {
            if (-not $msbuild) { throw '找不到 Visual Studio MSBuild' }
            $frameworkPath = Join-Path ${env:ProgramFiles(x86)} "Reference Assemblies\Microsoft\Framework\.NETFramework\$($target.Framework)"
            if (-not (Test-Path -LiteralPath (Join-Path $frameworkPath 'mscorlib.dll'))) {
                throw "缺少 .NET Framework $($target.Framework) Targeting Pack"
            }
            $constants = [string]$target.DefineConstants
            & $msbuild $projectPath /t:Rebuild /p:Configuration=Release "/p:TargetFrameworkVersion=$($target.Framework)" "/p:AutoCadApiPath=$sdkPath" "/p:OutputPath=$outputPath\\" "/p:DefineConstants=$constants" /m /v:minimal
        }
        if ($LASTEXITCODE -ne 0) { throw "编译退出代码 $LASTEXITCODE" }
        [pscustomobject]@{ Band = $target.Band; AutoCad = $target.AutoCad; Status = '成功'; Detail = $outputPath }
    }
    catch {
        [pscustomobject]@{ Band = $target.Band; AutoCad = $target.AutoCad; Status = '失败'; Detail = $_.Exception.Message }
    }
}

$results | Format-Table -AutoSize
if (@($results | Where-Object { $_.Status -ne '成功' }).Count -gt 0) { exit 1 }
