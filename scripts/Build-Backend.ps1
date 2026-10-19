param([ValidateSet('cpu','cuda')][string]$Mode = 'cuda')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'repos\jev.cpp'
$build = Join-Path $projectRoot ('.build\jev-' + $Mode)
$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
if ($cmakeCommand) { $cmakePath = $cmakeCommand.Source } else {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vsPath = & $vswhere -latest -products '*' -property installationPath
    $cmakePath = Join-Path $vsPath 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
    if (-not (Test-Path -LiteralPath $cmakePath)) { throw 'CMake was not found.' }
}
$cuda = if ($Mode -eq 'cuda') {'ON'} else {'OFF'}
& $cmakePath -S $source -B $build -DGGML_CUDA=$cuda -DCMAKE_CUDA_ARCHITECTURES=89 -DGGML_NATIVE=OFF -DLLAMA_CURL=OFF -DLLAMA_BUILD_TESTS=OFF
if ($LASTEXITCODE -ne 0) { throw 'Backend configuration failed. Install Visual Studio C++ Build Tools, CMake, and CUDA 12.5 for the CUDA build.' }
& $cmakePath --build $build --config Release --target llama-server -j 8
if ($LASTEXITCODE -ne 0) { throw 'Backend build failed.' }
Write-Output ('Built backend at ' + (Join-Path $build 'bin\Release'))
Write-Output 'Stop AutoJev before replacing runtime files. Keep CUDA and VC runtime DLLs in the runtime folder.'
