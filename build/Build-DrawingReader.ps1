param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$cmake=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe' | Select-Object -First 1
if(!$cmake){throw 'Building the independent reader requires the Visual Studio CMake component.'}
$build=Join-Path $repo '.artifacts/drawing-reader'
& $cmake -S (Join-Path $repo 'DrawingReader') -B $build -G 'Visual Studio 17 2022' -A x64
if($LASTEXITCODE){throw 'Drawing reader configuration failed.'}
& $cmake --build $build --config Release --parallel 4
if($LASTEXITCODE){throw 'Drawing reader build failed.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'Release/WanluoDrawingReader.exe') -Destination $OutputDirectory -Force
$licenses=Join-Path $OutputDirectory 'licenses/MiniDWG'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
foreach($name in @('LICENSE','THIRD_PARTY_NOTICES.md','ORIGIN.md')){
    Copy-Item -LiteralPath (Join-Path $repo "ThirdParty/MiniCAD/$name") -Destination $licenses -Force
}
