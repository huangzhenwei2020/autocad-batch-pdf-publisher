[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PluginAssembly,
    [Parameter(Mandatory=$true)][string]$AutoCadDirectory
)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$plugin=(Resolve-Path -LiteralPath $PluginAssembly).Path
$cad=(Resolve-Path -LiteralPath $AutoCadDirectory).Path
$root=Join-Path ([IO.Path]::GetTempPath()) ('WanLuoModelessQA-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
Get-ChildItem -LiteralPath (Split-Path -Parent $plugin) -Filter *.dll | Copy-Item -Destination $root
$year=[regex]::Match((Split-Path -Leaf $cad),'20\d{2}').Value
$template=Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Autodesk') -Filter acadiso.dwt -Recurse |
    Where-Object { $_.FullName -like "*AutoCAD $year*" } | Select-Object -First 1
if(!$template){throw 'Disposable template not found.'}
Copy-Item -LiteralPath $template.FullName -Destination (Join-Path $root 'fixture.dwt')
$framework='C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8.1'
$csc='C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/Roslyn/csc.exe'
$references=@('System.dll','System.Core.dll','System.Runtime.Serialization.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll') |
    ForEach-Object { '/r:'+(Join-Path $framework $_) }
$references+=@('AcMgd.dll','AcCoreMgd.dll','AcDbMgd.dll') | ForEach-Object { '/r:'+(Join-Path $cad $_) }
& $csc /nologo /target:library "/out:$root/ModelessQA.dll" "/r:$root/BatchPdfPublisher.dll" @references (Join-Path $repo 'BatchPdfPublisher.Tests/CadModelWorkspaceModelessNativeTests.cs')
if($LASTEXITCODE){throw 'Modeless native QA compilation failed.'}
$slash=$root.Replace('\','/')
$script=@"
(setq wlqa-trusted (getvar "TRUSTEDPATHS") wlqa-filedia (getvar "FILEDIA"))
(setvar "TRUSTEDPATHS" (strcat wlqa-trusted ";$slash"))
(setvar "FILEDIA" 0)
(setq wlqa-logmode (getvar "LOGFILEMODE") wlqa-logpath (getvar "LOGFILEPATH"))
(setvar "LOGFILEPATH" "$slash")
(setvar "LOGFILEMODE" 1)
(command "_.NETLOAD" "$slash/ModelessQA.dll")
(setvar "TRUSTEDPATHS" wlqa-trusted)
(setvar "FILEDIA" wlqa-filedia)
WLMODELESSWORKSPACEQA
(setvar "LOGFILEMODE" wlqa-logmode)
(setvar "LOGFILEPATH" wlqa-logpath)
"@
[IO.File]::WriteAllText((Join-Path $root 'run.scr'),$script+"`n",[Text.UTF8Encoding]::new($true))
$oldQa=$env:WANLUO_MODELESS_QA_ROOT;$oldData=$env:WANLUO_USER_DATA_ROOT
try {
    $env:WANLUO_MODELESS_QA_ROOT=$root;$env:WANLUO_USER_DATA_ROOT=Join-Path $root 'settings'
    $process=Start-Process -FilePath (Join-Path $cad 'acad.exe') -ArgumentList "/nologo /t `"$root/fixture.dwt`" /b `"$root/run.scr`"" -WorkingDirectory $root -WindowStyle Hidden -PassThru
    [IO.File]::WriteAllText((Join-Path $root 'process.txt'),[string]$process.Id)
    $deadline=[DateTime]::UtcNow.AddSeconds(150)
    $resultPath=Join-Path $root 'result.txt'
    while(!$process.HasExited -and !(Test-Path -LiteralPath $resultPath) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 500}
    if(!$process.HasExited -and !$process.WaitForExit(5000)) {
        # CAD can remain at its Start tab after managed Quit. Only terminate our disposable process.
        Stop-Process -Id $process.Id -Force
        if(!(Test-Path -LiteralPath $resultPath)){throw "Modeless CAD QA timed out: $root"}
    }
    $result=Get-Content -LiteralPath (Join-Path $root 'result.txt') -Raw -Encoding UTF8
    if(!$result.StartsWith('CAD_MODELESS_WORKSPACE_NATIVE_OK')){throw $result}
    $result
    "Artifacts: $root"
}finally{$env:WANLUO_MODELESS_QA_ROOT=$oldQa;$env:WANLUO_USER_DATA_ROOT=$oldData}
