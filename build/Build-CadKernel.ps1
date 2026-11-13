param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$cmake=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'|Select-Object -First 1
if(!$cmake){throw 'Visual Studio CMake and C++ components are required.'}
$build=Join-Path $repo '.artifacts/cad-kernel'
& $cmake -S (Join-Path $repo 'CadKernel') -B $build -G 'Visual Studio 17 2022' -A x64
if($LASTEXITCODE){throw 'CAD kernel configuration failed.'}
& $cmake --build $build --config Release --parallel 4
if($LASTEXITCODE){throw 'CAD kernel build failed.'}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
Copy-Item -LiteralPath (Join-Path $build 'Release/WanluoCadKernel.dll') -Destination $OutputDirectory -Force
$licenses=Join-Path $OutputDirectory 'licenses/MiniCAD'
New-Item -ItemType Directory -Path $licenses -Force|Out-Null
foreach($name in @('LICENSE','THIRD_PARTY_NOTICES.md','ORIGIN.md')){Copy-Item -LiteralPath (Join-Path $repo "ThirdParty/MiniCAD/$name") -Destination $licenses -Force}
