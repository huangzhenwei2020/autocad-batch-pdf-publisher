param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$cmake=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'|Select-Object -First 1
if(!$cmake){throw 'Visual Studio CMake and built native CAD libraries are required.'}
$build=Join-Path $repo '.artifacts/cad-feature-fixtures'
& $cmake -S (Join-Path $repo 'CadKernel.Tests') -B $build -G 'Visual Studio 17 2022' -A x64 "-DWANLUO_REPO=$repo"
if($LASTEXITCODE){throw 'Fixture configuration failed.'}
& $cmake --build $build --config Release --parallel 4
if($LASTEXITCODE){throw 'Fixture build failed.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
& (Join-Path $build 'Release/WanluoCadFeatureFixtures.exe') ([IO.Path]::GetFullPath($OutputDirectory))
if($LASTEXITCODE){throw 'Fixture generation failed.'}
