[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = $PSScriptRoot
$build = Join-Path $source 'build'
$cmakeCandidates = @(
    $env:CLYPDAT_CMAKE,
    'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe',
    'C:\Program Files\Microsoft Visual Studio\17\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
) | Where-Object { $_ -and (Test-Path $_) }
$cmake = $cmakeCandidates | Select-Object -First 1
if (-not $cmake) {
    $cmake = (Get-Command cmake -ErrorAction Stop).Source
}

& $cmake -S $source -B $build -G 'Visual Studio 18 2026' -A x64
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($LASTEXITCODE)." }
& $cmake --build $build --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "CMake build failed ($LASTEXITCODE)." }
