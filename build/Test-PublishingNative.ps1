param([Parameter(Mandatory=$true)][string]$PluginAssembly,[string]$AutoCadDirectory='C:\Program Files\Autodesk\AutoCAD 2022',[switch]$UserApproved)
$ErrorActionPreference='Stop'
if(-not $UserApproved){throw 'Ask the user before running CAD plugin tests; pass this flag only after approval.'}
if((Split-Path -Leaf $AutoCadDirectory) -ne 'AutoCAD 2022'){throw 'Only AutoCAD 2022 is authorized on this machine.'}
$repo=Split-Path $PSScriptRoot -Parent
$plugin=(Resolve-Path -LiteralPath $PluginAssembly).Path
$cad=(Resolve-Path -LiteralPath $AutoCadDirectory).Path
$root=Join-Path ([IO.Path]::GetTempPath()) ('WanLuoPublishingQA-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
Get-ChildItem -LiteralPath (Split-Path $plugin -Parent) -Filter *.dll | Copy-Item -Destination $root
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$csc=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Roslyn\csc.exe' | Select-Object -First 1
if(!$csc){throw 'Cannot find C# compiler'}
$refs=@('AcCoreMgd.dll','AcMgd.dll','AcDbMgd.dll') | ForEach-Object {'/r:'+(Join-Path $cad $_)}
& $csc /nologo /target:library "/out:$root/BatchPdfPublisher.DimensionEdit.Native.Tests.dll" @refs "/r:$root/BatchPdfPublisher.dll" "/r:$root/PdfSharp.dll" /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll (Join-Path $repo 'BatchPdfPublisher.Tests/PublishingPageNativeTests.cs')
if($LASTEXITCODE){throw 'Publishing native QA compile failed'}
$slash=$root.Replace('\','/')
@"
(setq wlqatrusted (getvar "TRUSTEDPATHS"))
(setvar "TRUSTEDPATHS" (strcat wlqatrusted ";$slash"))
(command "_.NETLOAD" "$slash/BatchPdfPublisher.dll")
(command "_.NETLOAD" "$slash/BatchPdfPublisher.DimensionEdit.Native.Tests.dll")
WLPAGEQA
(setvar "TRUSTEDPATHS" wlqatrusted)
_.QUIT
_Y

"@ | Set-Content -LiteralPath (Join-Path $root 'run.scr') -Encoding UTF8
$old=$env:WANLUO_USER_DATA_ROOT
try {
    $env:WANLUO_USER_DATA_ROOT=Join-Path $root 'userdata'
    $p=Start-Process -FilePath (Join-Path $cad 'accoreconsole.exe') -ArgumentList "/s `"$root/run.scr`" /l en-US" -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'cad.log') -RedirectStandardError (Join-Path $root 'cad.err')
    if(!$p.WaitForExit(60000)){Stop-Process -Id $p.Id -Force;throw "Native QA timeout: $root"}
    $log=Get-Content -LiteralPath (Join-Path $root 'cad.log') -Raw -Encoding Unicode
    $log -split "`n" | Select-String 'PUBLISH_PAGE_QA_OK'
    if($p.ExitCode -ne 0 -or $log.Contains('PUBLISH_PAGE_QA_FAILED') -or !$log.Contains('PUBLISH_PAGE_QA_OK')){throw "Publishing native QA failed: $root exit=$($p.ExitCode)"}
    "PUBLISHING_QA_DIRECTORY $root"
} finally {$env:WANLUO_USER_DATA_ROOT=$old}
