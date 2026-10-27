param([Parameter(Mandatory=$true)][string]$PluginAssembly,[Parameter(Mandatory=$true)][string]$AutoCadDirectory)
$ErrorActionPreference='Stop'
$plugin=(Resolve-Path -LiteralPath $PluginAssembly).Path
$cad=(Resolve-Path -LiteralPath $AutoCadDirectory).Path
$root=Join-Path ([IO.Path]::GetTempPath()) ('WanLuoPlanElevationQA-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
Get-ChildItem -LiteralPath (Split-Path $plugin -Parent) -Filter *.dll | Copy-Item -Destination $root
$slash=$root.Replace('\','/')
@"
(setq wlqatrusted (getvar "TRUSTEDPATHS") wlqafiledia (getvar "FILEDIA"))
(setvar "TRUSTEDPATHS" (strcat wlqatrusted ";$slash"))
(setvar "FILEDIA" 0)
(command "_.NETLOAD" "$slash/BatchPdfPublisher.dll")
WLREGISTEREDARCQA
(setvar "TRUSTEDPATHS" wlqatrusted)
(setvar "FILEDIA" wlqafiledia)
(princ (strcat "PLAN_Z_SETTINGS_RESTORED=" (if (and (= (getvar "TRUSTEDPATHS") wlqatrusted) (= (getvar "FILEDIA") wlqafiledia)) "YES" "NO")))
_.QUIT
_Y

"@ | Set-Content -LiteralPath (Join-Path $root 'run.scr') -Encoding UTF8
$p=Start-Process -FilePath (Join-Path $cad 'accoreconsole.exe') -ArgumentList "/s `"$root/run.scr`" /l en-US" -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $root 'cad.log') -RedirectStandardError (Join-Path $root 'cad.err')
if(!$p.WaitForExit(60000)){Stop-Process -Id $p.Id -Force;throw "Native plan elevation QA timeout: $root"}
$log=Get-Content -LiteralPath (Join-Path $root 'cad.log') -Raw -Encoding Unicode
$log -split "`n" | Select-String 'CAD_REGISTERED_|PLAN_Z_SETTINGS_'
if($p.ExitCode -ne 0 -or $log.Contains('CAD_REGISTERED_ARC_NATIVE_FAILED') -or !$log.Contains('CAD_REGISTERED_PLAN_Z_NATIVE_OK') -or
    !$log.Contains('CAD_REGISTERED_ARC_NATIVE_OK') -or !$log.Contains('PLAN_Z_SETTINGS_RESTORED=YES')){throw "Native plan elevation QA failed: $root exit=$($p.ExitCode)"}
"PLAN_ELEVATION_QA_DIRECTORY $root"
