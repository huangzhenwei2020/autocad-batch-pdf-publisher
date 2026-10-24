param([Parameter(Mandatory=$true)][string]$PluginAssembly,[Parameter(Mandatory=$true)][string]$AutoCadDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$plugin=(Resolve-Path -LiteralPath $PluginAssembly).Path
$cad=(Resolve-Path -LiteralPath $AutoCadDirectory).Path
$root=Join-Path ([IO.Path]::GetTempPath()) ('WanLuoLineCorrectionQA-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
Get-ChildItem -LiteralPath (Split-Path $plugin -Parent) -Filter *.dll | Copy-Item -Destination $root
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$refs=@('AcCoreMgd.dll','AcMgd.dll','AcDbMgd.dll')|ForEach-Object {'/r:'+(Join-Path $cad $_)}
& $csc /nologo /target:library "/out:$root/LineQA.dll" @refs "/r:$root/BatchPdfPublisher.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    (Join-Path $repo 'BatchPdfPublisher.Tests/LineCorrectionNativeTests.cs') `
    (Join-Path $repo 'BatchPdfPublisher/Features/Drafting/Services/LineCorrectionGeometry.cs') `
    (Join-Path $repo 'BatchPdfPublisher/Features/Drafting/Services/LineCorrectionService.cs')
if($LASTEXITCODE){throw 'Native line correction QA compile failed.'}
$slash=$root.Replace('\','/')
$script=@"
(setq wlqatrusted (getvar "TRUSTEDPATHS") wlqafiledia (getvar "FILEDIA"))
(setvar "TRUSTEDPATHS" (strcat wlqatrusted ";$slash"))
(setvar "FILEDIA" 0)
(command "_.NETLOAD" "$slash/LineQA.dll")
WLLINECORRECTIONSETUP
WLLINECORRECTIONQA
_.U
WLLINECORRECTIONUNDOQA
(setvar "TRUSTEDPATHS" wlqatrusted)
(setvar "FILEDIA" wlqafiledia)
(princ (strcat "LINE_CORRECTION_SETTINGS_RESTORED=" (if (and (= (getvar "TRUSTEDPATHS") wlqatrusted) (= (getvar "FILEDIA") wlqafiledia)) "YES" "NO")))
_.QUIT
_Y

"@
$script | Set-Content -LiteralPath (Join-Path $root 'run.scr') -Encoding UTF8
$old=$env:WANLUO_LINE_QA
try {
    $env:WANLUO_LINE_QA=$root
    $p=Start-Process -FilePath (Join-Path $cad 'accoreconsole.exe') -ArgumentList "/s `"$root/run.scr`" /l en-US" -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $root 'cad.log') -RedirectStandardError (Join-Path $root 'cad.err')
    if(!$p.WaitForExit(90000)){Stop-Process -Id $p.Id -Force;throw "Native line QA timeout: $root"}
    $log=Get-Content -LiteralPath (Join-Path $root 'cad.log') -Raw -Encoding Unicode
    $log -split "`n" | Select-String 'LINE_CORRECTION_'
    if($p.ExitCode -ne 0 -or $log.Contains('LINE_CORRECTION_NATIVE_FAILED') -or !$log.Contains('LINE_CORRECTION_NATIVE_OK') -or !$log.Contains('LINE_CORRECTION_UNDO_OK') -or !$log.Contains('LINE_CORRECTION_SETTINGS_RESTORED=YES')) {throw "Native line QA failed: $root exit=$($p.ExitCode)"}
    "LINE_CORRECTION_QA_DIRECTORY $root"
} finally {$env:WANLUO_LINE_QA=$old}
