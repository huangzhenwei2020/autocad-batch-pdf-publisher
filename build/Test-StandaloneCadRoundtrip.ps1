param(
    [Parameter(Mandatory=$true)][string]$PackageRoot,
    [string]$WorkbenchDirectory,
    [switch]$UserApproved
)
$ErrorActionPreference='Stop'
if(!$UserApproved){throw 'Ask the user before CAD-hosted checks.'}
$repo=Split-Path $PSScriptRoot -Parent
$package=(Resolve-Path -LiteralPath $PackageRoot).Path
if(!$WorkbenchDirectory){$WorkbenchDirectory=Join-Path $package '独立工作台'}
$cad='C:/Program Files/Autodesk/AutoCAD 2022'
$root=Join-Path ([IO.Path]::GetTempPath()) ('WanLuoCadEditorQA-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root|Out-Null
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$compiler=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/**/Roslyn/csc.exe'|Select-Object -First 1
$refs=@('AcCoreMgd.dll','AcMgd.dll','AcDbMgd.dll')|ForEach-Object {'/r:'+(Join-Path $cad $_)}
& $compiler /nologo /target:library "/out:$root/WanluoCadRoundtrip.Tests.dll" @refs /r:System.Core.dll (Join-Path $repo 'BatchPdfPublisher.Tests/StandaloneCadRoundtripNativeTests.cs')
if($LASTEXITCODE){throw 'CAD round-trip test compilation failed.'}
$previousRoot=$env:WANLUO_USER_DATA_ROOT
$previousQa=$env:WANLUO_CAD_ROUNDTRIP_QA
$previousOutput=$env:WANLUO_CAD_ROUNDTRIP_OUTPUT
function Invoke-CadCheck([string]$Command,[string]$Marker){
    $script=Join-Path $root ($Command+'.scr')
    $slash=$root.Replace('\','/')
    @"
(setvar "TRUSTEDPATHS" (strcat (getvar "TRUSTEDPATHS") ";$slash"))
(command "_.NETLOAD" "$slash/WanluoCadRoundtrip.Tests.dll")
$Command
_.QUIT
_Y

"@|Set-Content -LiteralPath $script -Encoding utf8
    $process=Start-Process -FilePath (Join-Path $cad 'accoreconsole.exe') -ArgumentList "/s `"$script`" /l en-US" -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root ($Command+'.log')) -RedirectStandardError (Join-Path $root ($Command+'.err'))
    try{
        $deadline=(Get-Date).AddSeconds(90)
        while(!$process.WaitForExit(5000)){if((Get-Date) -gt $deadline){throw "Own CAD check timed out $root"}}
        if(Test-Path -LiteralPath "$root/failure.txt"){throw (Get-Content -LiteralPath "$root/failure.txt" -Raw)}
        if($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $root $Marker))){throw "CAD check failed $root"}
        Get-Content -LiteralPath (Join-Path $root $Marker)
    }finally{if(!$process.HasExited){$process.Kill()}}
}
try{
    $env:WANLUO_USER_DATA_ROOT=Join-Path $root 'userdata'
    $env:WANLUO_CAD_ROUNDTRIP_QA=$root
    Invoke-CadCheck 'WLCADPREPAREQA' 'prepare-ok.txt'
    $testOutput=& (Join-Path $PSScriptRoot 'Test-StandaloneWorkbench.ps1') -WorkbenchDirectory $WorkbenchDirectory -PackageRoot $package -DrawingPath (Join-Path $root 'CAD2022 源图.dwg')
    $testOutput|Write-Output
    $qaLine=$testOutput|Where-Object {$_ -like 'STANDALONE_QA_DIRECTORY *'}|Select-Object -Last 1
    if(!$qaLine){throw 'Independent drawing editor did not complete.'}
    $env:WANLUO_CAD_ROUNDTRIP_OUTPUT=Join-Path $qaLine.Substring('STANDALONE_QA_DIRECTORY '.Length) 'userdata'
    Invoke-CadCheck 'WLCADROUNDTRIPQA' 'roundtrip-ok.txt'
    "CAD_EDITOR_QA_DIRECTORY $root"
}finally{
    $env:WANLUO_USER_DATA_ROOT=$previousRoot
    $env:WANLUO_CAD_ROUNDTRIP_QA=$previousQa
    $env:WANLUO_CAD_ROUNDTRIP_OUTPUT=$previousOutput
}
