[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceDrawing,
    [Parameter(Mandatory = $true)][string]$PluginAssembly,
    [Parameter(Mandatory = $true)][string]$AutoCadDirectory,
    [string]$TianzhengKernel
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$source = (Resolve-Path -LiteralPath $SourceDrawing).Path
$plugin = (Resolve-Path -LiteralPath $PluginAssembly).Path
$cad = (Resolve-Path -LiteralPath $AutoCadDirectory).Path
$kernel = if ($TianzhengKernel) { (Resolve-Path -LiteralPath $TianzhengKernel).Path.Replace('\', '/') } else { $null }
$before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$root = Join-Path ([IO.Path]::GetTempPath()) ('WanLuoObjectInventory-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
Copy-Item -LiteralPath $source -Destination (Join-Path $root 'source-private.dwg')
Get-ChildItem -LiteralPath (Split-Path -Parent $plugin) -Filter *.dll | Copy-Item -Destination $root
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$csc = Join-Path (Split-Path -Parent $msbuild) 'Roslyn/csc.exe'
$references = @('AcCoreMgd.dll', 'AcMgd.dll', 'AcDbMgd.dll') | ForEach-Object { '/r:' + (Join-Path $cad $_) }
& $csc /nologo /target:library "/out:$root/InventoryQA.dll" @references "/r:$root/BatchPdfPublisher.dll" /r:System.Runtime.Serialization.dll `
    (Join-Path $repo 'build/CadBuildingObjectInventory.cs') `
    (Join-Path $repo 'BatchPdfPublisher/Features/BuildingModel/Services/CadBuildingDiagnosticGeometry.cs')
if ($LASTEXITCODE) { throw 'Inventory assembly compile failed.' }
$slash = $root.Replace('\', '/')
$loadKernel = if ($kernel) {
    "(setq wlqa-kernel (vl-catch-all-apply 'arxload (list `"$kernel`")))`n" +
    '(if (vl-catch-all-error-p wlqa-kernel) (princ (strcat "TIANZHENG_KERNEL_UNAVAILABLE: " (vl-catch-all-error-message wlqa-kernel))) (princ "TIANZHENG_KERNEL_LOADED"))'
} else { '' }
@"
(setq wlqa-trusted (getvar "TRUSTEDPATHS") wlqa-filedia (getvar "FILEDIA"))
(setvar "TRUSTEDPATHS" (strcat wlqa-trusted ";$slash"))
(setvar "FILEDIA" 0)
$loadKernel
(command "_.NETLOAD" "$slash/InventoryQA.dll")
WLOUTDOORINVENTORY
(setvar "TRUSTEDPATHS" wlqa-trusted)
(setvar "FILEDIA" wlqa-filedia)
_.QUIT
_Y

"@ | Set-Content -LiteralPath (Join-Path $root 'run.scr') -Encoding UTF8
$oldInput = $env:WANLUO_INVENTORY_INPUT; $oldOutput = $env:WANLUO_INVENTORY_OUTPUT; $oldPath = $env:PATH
try {
    $env:WANLUO_INVENTORY_INPUT = Join-Path $root 'source-private.dwg'
    $env:WANLUO_INVENTORY_OUTPUT = Join-Path $root 'objects.cadprobe.json'
    if ($kernel) { $env:PATH = (Split-Path -Parent $kernel) + ';' + $env:PATH }
    $p = Start-Process -FilePath (Join-Path $cad 'accoreconsole.exe') -ArgumentList "/s `"$root/run.scr`" /l en-US" `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'cad.log') -RedirectStandardError (Join-Path $root 'cad.err')
    if (-not $p.WaitForExit(90000)) { Stop-Process -Id $p.Id -Force; throw "Inventory timed out: $root" }
    $log = Get-Content -LiteralPath (Join-Path $root 'cad.log') -Raw -Encoding Unicode
    if ($p.ExitCode -ne 0 -or -not $log.Contains('CAD_BUILDING_INVENTORY_OK') -or $log.Contains('CAD_BUILDING_INVENTORY_FAILED')) {
        throw "Inventory failed: $root"
    }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $before) { throw 'Source drawing hash changed.' }
    $log -split "`n" | Select-String 'CAD_BUILDING_INVENTORY_OK|^TIANZHENG_KERNEL_'
    Get-Content -LiteralPath ($env:WANLUO_INVENTORY_OUTPUT + '.classes.txt')
    "Original DWG SHA256 unchanged: $before"
    "Private inventory: $root"
} finally { $env:WANLUO_INVENTORY_INPUT = $oldInput; $env:WANLUO_INVENTORY_OUTPUT = $oldOutput; $env:PATH = $oldPath }
