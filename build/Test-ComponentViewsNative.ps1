param([Parameter(Mandatory=$true)][string]$PluginAssembly,[Parameter(Mandatory=$true)][string]$AutoCadDirectory,[switch]$UserApproved)
$ErrorActionPreference='Stop'
if(-not $UserApproved){throw 'Ask the user before running CAD plugin tests; pass this flag only after approval.'}
if((Split-Path -Leaf $AutoCadDirectory) -ne 'AutoCAD 2022'){throw 'Only AutoCAD 2022 is authorized on this machine.'}
$plugin=(Resolve-Path -LiteralPath $PluginAssembly).Path;$cad=(Resolve-Path -LiteralPath $AutoCadDirectory).Path
$root=Join-Path ([IO.Path]::GetTempPath()) ('WanLuoComponentViewsQA-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $root|Out-Null
Get-ChildItem -LiteralPath (Split-Path $plugin -Parent) -Filter *.dll|Copy-Item -Destination $root
$framework='C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8.1'
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$refs=@('System.dll','System.Core.dll','System.Runtime.Serialization.dll')|ForEach-Object {'/r:'+(Join-Path $framework $_)}
$refs+=@('AcMgd.dll','AcCoreMgd.dll','AcDbMgd.dll')|ForEach-Object {'/r:'+(Join-Path $cad $_)}
& $csc /nologo /target:library "/out:$root/ComponentViewsQA.dll" "/r:$root/BatchPdfPublisher.dll" @refs (Join-Path $PSScriptRoot '../BatchPdfPublisher.Tests/ComponentViewCadNativeTests.cs')
if($LASTEXITCODE){throw 'QA compile failed.'}
$slash=$root.Replace('\','/')
@"
(setq wlqt (getvar "TRUSTEDPATHS") wlqf (getvar "FILEDIA"))
(setvar "TRUSTEDPATHS" (strcat wlqt ";$slash"))
(setvar "FILEDIA" 0)
(command "_.NETLOAD" "$slash/BatchPdfPublisher.dll")
(command "_.NETLOAD" "$slash/ComponentViewsQA.dll")
WLCOMPONENTVIEWQAPREP
WLCOMPONENTVIEWS
10000,20000,0
WLCOMPONENTVIEWQACHECK
_.U
WLCOMPONENTVIEWQAUNDO
WLCOMPONENTVIEWQAREJECT
(setvar "TRUSTEDPATHS" wlqt)
(setvar "FILEDIA" wlqf)
_.QUIT
_Y

"@|Set-Content -LiteralPath (Join-Path $root 'run.scr') -Encoding UTF8
$old=$env:WANLUO_COMPONENT_QA;$oldRoot=$env:WANLUO_COMPONENT_CAD_ROOT
try{
    $env:WANLUO_COMPONENT_QA=$root;$env:WANLUO_COMPONENT_CAD_ROOT=Join-Path $root 'spool'
    $p=Start-Process -FilePath (Join-Path $cad 'accoreconsole.exe') -ArgumentList "/s `"$root/run.scr`" /l en-US" -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'cad.log') -RedirectStandardError (Join-Path $root 'cad.err')
    if(!$p.WaitForExit(60000)){Stop-Process -Id $p.Id -Force;throw "QA timeout: $root"}
    $log=Get-Content -LiteralPath (Join-Path $root 'cad.log') -Raw -Encoding Unicode
    if($p.ExitCode -ne 0 -or !$log.Contains('COMPONENT_CAD_INSERT_OK') -or !$log.Contains('COMPONENT_CAD_UNDO_OK') -or !$log.Contains('COMPONENT_CAD_REJECT_OK')){throw "CAD QA failed: $root exit=$($p.ExitCode)"}
    $log -split "`n"|Select-String 'COMPONENT_CAD_';"QA_DIRECTORY $root"
}finally{$env:WANLUO_COMPONENT_QA=$old;$env:WANLUO_COMPONENT_CAD_ROOT=$oldRoot}
