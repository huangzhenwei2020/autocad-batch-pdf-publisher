[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PluginAssembly,
    [Parameter(Mandatory = $true)][string]$AutoCadDirectory
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$plugin = (Resolve-Path -LiteralPath $PluginAssembly).Path
$cad = (Resolve-Path -LiteralPath $AutoCadDirectory).Path
$console = Join-Path $cad 'accoreconsole.exe'
if (-not (Test-Path -LiteralPath $console)) { throw 'AutoCAD Core Console not found.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found.' }
$csc = Join-Path (Split-Path -Parent $msbuild) 'Roslyn/csc.exe'
$root = Join-Path ([IO.Path]::GetTempPath()) ('WanLuoDiagnosticQA-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
Get-ChildItem -LiteralPath (Split-Path -Parent $plugin) -Filter *.dll | Copy-Item -Destination $root
$test = Join-Path $root 'DiagnosticQA.dll'
$references = @('AcCoreMgd.dll', 'AcMgd.dll', 'AcDbMgd.dll') | ForEach-Object { '/r:' + (Join-Path $cad $_) }
& $csc /nologo /target:library "/out:$test" @references "/r:$root/BatchPdfPublisher.dll" /r:System.Runtime.Serialization.dll `
    (Join-Path $repo 'BatchPdfPublisher.Tests/CadBuildingDiagnosticNativeTests.cs') `
    (Join-Path $repo 'BatchPdfPublisher/Features/BuildingModel/Services/CadBuildingDiagnosticGeometry.cs')
if ($LASTEXITCODE) { throw 'Native diagnostic test compile failed.' }
$slash = $root.Replace('\', '/')
$script = @"
(setq wlqa-trusted (getvar "TRUSTEDPATHS") wlqa-filedia (getvar "FILEDIA"))
(setvar "TRUSTEDPATHS" (strcat wlqa-trusted ";$slash"))
(setvar "FILEDIA" 0)
(command "_.NETLOAD" "$slash/DiagnosticQA.dll")
WLDIAGNOSTICGEOMETRYQA
(setvar "TRUSTEDPATHS" wlqa-trusted)
(setvar "FILEDIA" wlqa-filedia)
(princ (strcat "DIAGNOSTIC_QA_SETTINGS_RESTORED=" (if (and (= (getvar "TRUSTEDPATHS") wlqa-trusted) (= (getvar "FILEDIA") wlqa-filedia)) "YES" "NO")))
_.QUIT
_Y

"@
$script | Set-Content -LiteralPath (Join-Path $root 'run.scr') -Encoding UTF8
$process = Start-Process -FilePath $console -ArgumentList "/s `"$root/run.scr`" /l en-US" -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $root 'cad.log') -RedirectStandardError (Join-Path $root 'cad.err')
if (-not $process.WaitForExit(90000)) {
    # Only this disposable test process; never touch the user's CAD instance.
    Stop-Process -Id $process.Id -Force
    throw "Native QA timed out. Logs: $root"
}
$log = Get-Content -LiteralPath (Join-Path $root 'cad.log') -Raw -Encoding Unicode
if ($process.ExitCode -ne 0 -or $log.Contains('CAD_DIAGNOSTIC_GEOMETRY_NATIVE_FAILED') `
    -or -not $log.Contains('CAD_DIAGNOSTIC_GEOMETRY_NATIVE_OK') -or -not $log.Contains('DIAGNOSTIC_QA_SETTINGS_RESTORED=YES')) {
    throw "Native diagnostic QA failed. Logs: $root"
}
$log -split "`n" | Select-String 'CAD_DIAGNOSTIC_GEOMETRY_NATIVE_OK|DIAGNOSTIC_QA_SETTINGS_RESTORED=YES'
"Logs: $root"
