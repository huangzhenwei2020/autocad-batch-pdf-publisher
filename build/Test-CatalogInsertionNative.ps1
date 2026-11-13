param(
    [Parameter(Mandatory=$true)][string]$PluginAssembly,
    [string]$AutoCadDirectory='C:\Program Files\Autodesk\AutoCAD 2022',
    [switch]$UserApproved
)
$ErrorActionPreference='Stop'
if(!$UserApproved){throw 'Ask the user before running CAD-hosted tests.'}
if((Split-Path -Leaf $AutoCadDirectory) -ne 'AutoCAD 2022'){throw 'Only AutoCAD 2022 is authorized.'}
$repo=Split-Path $PSScriptRoot -Parent
$plugin=(Resolve-Path -LiteralPath $PluginAssembly).Path
$cad=(Resolve-Path -LiteralPath $AutoCadDirectory).Path
$root=Join-Path ([IO.Path]::GetTempPath()) ('WanLuoCatalogQA-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
Get-ChildItem -LiteralPath (Split-Path $plugin -Parent) -Filter *.dll | Copy-Item -Destination $root
$releaseRoot=Split-Path (Split-Path (Split-Path $plugin -Parent) -Parent) -Parent
Copy-Item -LiteralPath (Join-Path $releaseRoot 'Resources') -Destination $root -Recurse
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$compiler=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Roslyn\csc.exe' | Select-Object -First 1
if(!$compiler){throw 'Cannot find C# compiler.'}
$refs=@('AcCoreMgd.dll','AcMgd.dll','AcDbMgd.dll') | ForEach-Object {'/r:'+(Join-Path $cad $_)}
$framework='C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8.1'
$refs+=@('PresentationFramework','PresentationCore','WindowsBase','System.Xaml') | ForEach-Object {'/r:'+(Join-Path $framework ($_+'.dll'))}
& $compiler /nologo /target:library "/out:$root/BatchPdfPublisher.DimensionEdit.Native.Tests.dll" @refs "/r:$root/BatchPdfPublisher.dll" /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll (Join-Path $repo 'BatchPdfPublisher.Tests/CatalogInsertionNativeTests.cs')
if($LASTEXITCODE){throw 'Catalog native test compilation failed.'}
$slash=$root.Replace('\','/')
$cases=1..15 | ForEach-Object {"WLCATALOGSCALEQA`n0,0`n_.U`nWLCATALOGUNDOQA"}
@"
(setq wlqatrusted (getvar "TRUSTEDPATHS"))
(setvar "TRUSTEDPATHS" (strcat wlqatrusted ";$slash"))
(command "_.NETLOAD" "$slash/BatchPdfPublisher.dll")
(command "_.NETLOAD" "$slash/BatchPdfPublisher.DimensionEdit.Native.Tests.dll")
$($cases -join "`n")
(setvar "TRUSTEDPATHS" wlqatrusted)
_.QUIT
_Y

"@ | Set-Content -LiteralPath (Join-Path $root 'run.scr') -Encoding UTF8
$previous=$env:WANLUO_USER_DATA_ROOT
$process=$null
try {
    $env:WANLUO_USER_DATA_ROOT=Join-Path $root 'userdata'
    New-Item -ItemType Directory -Path $env:WANLUO_USER_DATA_ROOT | Out-Null
    $process=Start-Process -FilePath (Join-Path $cad 'accoreconsole.exe') -ArgumentList "/s `"$root/run.scr`" /l en-US" -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'cad.log') -RedirectStandardError (Join-Path $root 'cad.err')
    $deadline=(Get-Date).AddSeconds(90)
    while(!$process.WaitForExit(5000)){
        if(Test-Path -LiteralPath (Join-Path $env:WANLUO_USER_DATA_ROOT 'failure.txt')){break}
        if((Get-Date) -gt $deadline){throw "Catalog test timed out. Inspect $root"}
    }
    $failure=Join-Path $env:WANLUO_USER_DATA_ROOT 'failure.txt'
    if(Test-Path -LiteralPath $failure){throw (Get-Content -LiteralPath $failure -Raw)}
    $log=Get-Content -LiteralPath (Join-Path $root 'cad.log') -Raw -Encoding Unicode
    if($process.ExitCode -ne 0 -or !$log.Contains('CATALOG_SCALE_QA_OK')){throw "Catalog test failed. Inspect $root"}
    'CATALOG_SCALE_QA_OK'
    "CATALOG_QA_DIRECTORY $root"
}
finally {
    if($process -and !$process.HasExited){Stop-Process -Id $process.Id -Force}
    $env:WANLUO_USER_DATA_ROOT=$previous
}
