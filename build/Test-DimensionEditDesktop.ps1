param([Parameter(Mandatory=$true)][string]$PluginAssembly,[string]$AutoCadDirectory='C:\Program Files\Autodesk\AutoCAD 2022',[switch]$UserApproved)
$ErrorActionPreference='Stop'
if(-not $UserApproved){throw 'Ask the user before running CAD plugin tests; pass this flag only after approval.'}
if((Split-Path -Leaf $AutoCadDirectory) -ne 'AutoCAD 2022'){throw 'Only AutoCAD 2022 is authorized on this machine.'}
$repo=Split-Path $PSScriptRoot -Parent
$plugin=(Resolve-Path -LiteralPath $PluginAssembly).Path
$cad=(Resolve-Path -LiteralPath $AutoCadDirectory).Path
$root=Join-Path ([IO.Path]::GetTempPath()) ('WanLuoDimensionDesktop-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$csc=& (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe') -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Roslyn\csc.exe' | Select-Object -First 1
$refs=@('AcMgd.dll','AcCoreMgd.dll','AcDbMgd.dll') | ForEach-Object {'/r:'+(Join-Path $cad $_)}
& $csc /nologo /target:library "/out:$root/BatchPdfPublisher.DimensionEdit.Native.Tests.dll" @refs "/r:$plugin" /r:System.Core.dll (Join-Path $repo 'BatchPdfPublisher.Tests/DimensionEditNativeTests.cs')
if($LASTEXITCODE){throw 'Desktop fixture compile failed'}
$slash=$root.Replace('\','/')
@"
(setq wlqatrusted (getvar "TRUSTEDPATHS"))
(setvar "TRUSTEDPATHS" (strcat wlqatrusted ";$slash"))
(command "_.NETLOAD" (getenv "WANLUO_DIM_QA_PLUGIN"))
(command "_.NETLOAD" "$slash/BatchPdfPublisher.DimensionEdit.Native.Tests.dll")
(setvar "TRUSTEDPATHS" wlqatrusted)
WLDIMUISETUP
(princ)
"@ | Set-Content -LiteralPath (Join-Path $root 'desktop.scr') -Encoding utf8
$oldData=$env:WANLUO_USER_DATA_ROOT; $oldQa=$env:WANLUO_DIM_QA_ROOT; $oldPlugin=$env:WANLUO_DIM_QA_PLUGIN
try {
    $env:WANLUO_USER_DATA_ROOT=Join-Path $root 'userdata';$env:WANLUO_DIM_QA_ROOT=$root
    # Keep the script ASCII: CAD 2022 may read .scr using the active legacy
    # code page. getenv supplies the original installed Unicode path intact.
    $env:WANLUO_DIM_QA_PLUGIN=$plugin.Replace('\','/')
    $p=Start-Process -FilePath (Join-Path $cad 'acad.exe') -ArgumentList '/nologo','/nossm','/b',(Join-Path $root 'desktop.scr') -WindowStyle Hidden -PassThru
    [pscustomobject]@{Root=$root;Pid=$p.Id} | ConvertTo-Json
} finally {$env:WANLUO_USER_DATA_ROOT=$oldData;$env:WANLUO_DIM_QA_ROOT=$oldQa;$env:WANLUO_DIM_QA_PLUGIN=$oldPlugin}
