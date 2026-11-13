param(
    [Parameter(Mandatory=$true)][string]$WorkbenchDirectory,
    [Parameter(Mandatory=$true)][string]$PackageRoot,
    [string]$DrawingPath
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$workbench=(Resolve-Path -LiteralPath $WorkbenchDirectory).Path
$package=(Resolve-Path -LiteralPath $PackageRoot).Path
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('WanLuoStandaloneQA-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path "$testRoot/userdata" -Force | Out-Null
if(!$DrawingPath){
    $DrawingPath=Join-Path $testRoot '中文图纸 基础预览.dxf'
    @'
0
SECTION
2
HEADER
9
$ACADVER
1
AC1027
0
ENDSEC
0
SECTION
2
ENTITIES
0
LINE
8
建筑轮廓
10
0
20
0
11
6000
21
0
0
LINE
8
建筑轮廓
10
6000
20
0
11
6000
21
4000
0
LINE
8
建筑轮廓
10
6000
20
4000
11
0
21
4000
0
LINE
8
建筑轮廓
10
0
20
4000
11
0
21
0
0
CIRCLE
8
辅助图形
10
3000
20
2000
40
800
0
TEXT
8
标注
10
1300
20
600
40
220
1
独立版图纸预览
0
ENDSEC
0
EOF
'@ | Set-Content -LiteralPath $DrawingPath -Encoding utf8
}
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=Join-Path $workbench '万落独立工作台.exe'
$start.UseShellExecute=$false
$start.WorkingDirectory=$workbench
foreach($value in @('--self-test','--drawing',(Resolve-Path -LiteralPath $DrawingPath).Path,'--package-root',$package)){$start.ArgumentList.Add($value)}
$start.Environment['WANLUO_USER_DATA_ROOT']=Join-Path $testRoot 'userdata'
$start.Environment['DOTNET_ROOT']=Join-Path $repo '.tools/dotnet'
$start.Environment['DOTNET_ROOT_X64']=Join-Path $repo '.tools/dotnet'
$process=[Diagnostics.Process]::Start($start)
try {
    $deadline=(Get-Date).AddMinutes(2)
    while(!$process.WaitForExit(5000)){
        if((Get-Date) -gt $deadline){throw "Independent workbench timed out. Inspect $testRoot"}
    }
    $resultPath=Join-Path $testRoot 'userdata/selftest-result.txt'
    if(!(Test-Path -LiteralPath $resultPath)){throw "No workbench result. Inspect $testRoot"}
    $result=Get-Content -LiteralPath $resultPath -Raw
    if($process.ExitCode -ne 0 -or $result.Trim() -ne 'STANDALONE_WORKBENCH_OK'){throw $result}
    $result.Trim()
    "STANDALONE_QA_DIRECTORY $testRoot"
}
finally {
    if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()}
    $process.Dispose()
}
