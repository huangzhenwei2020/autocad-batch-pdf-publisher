param(
    [Parameter(Mandatory = $true)][string]$PluginAssembly,
    [string]$AutoCadDirectory = 'C:\Program Files\Autodesk\AutoCAD 2022',
    [string]$DesktopProfile,
    [switch]$UserApproved,
    [switch]$Desktop
)

$ErrorActionPreference = 'Stop'
if (!$UserApproved) { throw 'Ask the user before running CAD-hosted tests.' }
if ((Split-Path -Leaf $AutoCadDirectory) -ne 'AutoCAD 2022') {
    throw 'Only AutoCAD 2022 is authorized on this machine.'
}
$repo = Split-Path $PSScriptRoot -Parent
$plugin = (Resolve-Path -LiteralPath $PluginAssembly).Path
$cad = (Resolve-Path -LiteralPath $AutoCadDirectory).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('WanLuoWpfQA-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
Get-ChildItem -LiteralPath (Split-Path $plugin -Parent) -Filter *.dll | Copy-Item -Destination $testRoot
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$compiler = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Roslyn\csc.exe' | Select-Object -First 1
if (!$compiler) { throw 'Cannot find C# compiler.' }
$references = @('AcCoreMgd.dll', 'AcMgd.dll', 'AcDbMgd.dll') | ForEach-Object { '/r:' + (Join-Path $cad $_) }
$framework = 'C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8.1'
$references += @('PresentationFramework', 'PresentationCore', 'WindowsBase', 'System.Xaml') | ForEach-Object { '/r:' + (Join-Path $framework ($_ + '.dll')) }
& $compiler /nologo /target:library "/out:$testRoot/BatchPdfPublisher.DimensionEdit.Native.Tests.dll" @references "/r:$testRoot/BatchPdfPublisher.dll" "/r:$testRoot/PdfSharp.dll" /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll (Join-Path $repo 'BatchPdfPublisher.Tests/PublishingWpfNativeTests.cs')
if ($LASTEXITCODE) { throw 'WPF native test compilation failed.' }

$slash = $testRoot.Replace('\', '/')
$command = if ($Desktop) { 'WLBPPWPFDESKTOPQA' } else { 'WLBPPWPFQA' }
$finish = if ($Desktop) { '' } else { "(setvar `"TRUSTEDPATHS`" wlqatrusted)`n_.QUIT`n_Y`n" }
@"
(setq wlqatrusted (getvar "TRUSTEDPATHS"))
(setvar "TRUSTEDPATHS" (strcat wlqatrusted ";$slash"))
(command "_.NETLOAD" "$slash/BatchPdfPublisher.dll")
(command "_.NETLOAD" "$slash/BatchPdfPublisher.DimensionEdit.Native.Tests.dll")
$command
$finish
"@ | Set-Content -LiteralPath (Join-Path $testRoot 'run.scr') -Encoding UTF8

$previousRoot = $env:WANLUO_USER_DATA_ROOT
$process = $null
try {
    $env:WANLUO_USER_DATA_ROOT = Join-Path $testRoot 'userdata'
    if ($Desktop) {
        $template = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Autodesk/AutoCAD 2022') -Recurse -Filter acadiso.dwt | Select-Object -First 1 -ExpandProperty FullName
        if (!$template) { throw 'A disposable drawing template is required.' }
        $profileArgument = if ($DesktopProfile) { "/p `"$DesktopProfile`"" } else { '' }
        # A visible desktop is required to validate the real WPF message loop.
        $process = Start-Process -FilePath (Join-Path $cad 'acad.exe') -ArgumentList "/nologo /nossm $profileArgument /t `"$template`" /b `"$testRoot/run.scr`"" -PassThru
    }
    else {
        $process = Start-Process -FilePath (Join-Path $cad 'accoreconsole.exe') -ArgumentList "/s `"$testRoot/run.scr`" /l en-US" -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testRoot 'cad.log') -RedirectStandardError (Join-Path $testRoot 'cad.err')
    }
    $deadline = (Get-Date).AddMinutes(2)
    while (!$process.WaitForExit(10000)) {
        if (Test-Path -LiteralPath (Join-Path $env:WANLUO_USER_DATA_ROOT 'failure.txt')) { break }
        if ((Get-Date) -gt $deadline) { throw "WPF test timed out. Inspect $testRoot" }
    }
    $failure = Join-Path $env:WANLUO_USER_DATA_ROOT 'failure.txt'
    if (Test-Path -LiteralPath $failure) { throw (Get-Content -LiteralPath $failure -Raw) }
    if ($Desktop) {
        if ($process.ExitCode -ne 0) { throw "CAD desktop exited with code $($process.ExitCode). Inspect $testRoot" }
        if (!(Test-Path -LiteralPath (Join-Path $env:WANLUO_USER_DATA_ROOT 'desktop-ok.txt'))) {
            throw "WPF desktop test did not complete. Inspect $testRoot"
        }
        'PUBLISH_WPF_DESKTOP_OK'
    }
    else {
        $log = Get-Content -LiteralPath (Join-Path $testRoot 'cad.log') -Raw -Encoding Unicode
        if ($process.ExitCode -ne 0 -or !$log.Contains('PUBLISH_WPF_QA_OK')) {
            throw "WPF native test failed. Inspect $testRoot"
        }
        'PUBLISH_WPF_QA_OK'
    }
    "PUBLISH_WPF_QA_DIRECTORY $testRoot"
}
finally {
    # Only the process created by this test belongs to the runner.
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id -Force }
    $env:WANLUO_USER_DATA_ROOT = $previousRoot
}
