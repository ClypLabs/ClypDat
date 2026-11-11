[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$VlcRoot = "$env:USERPROFILE/.nuget/packages/videolan.libvlc.windows/3.0.23.1/build/x64"
)
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer (vswhere.exe) is required.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json | Select-Object -First 1
if (-not $installation) { throw 'Install Visual Studio C++ x64 build tools.' }
$cmake = $env:CLYPDAT_CMAKE
if (-not $cmake) { $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source }
if (-not $cmake) { $cmake = Join-Path $installation.installationPath 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe' }
if (-not (Test-Path -LiteralPath $cmake)) { throw 'Install CMake or set CLYPDAT_CMAKE.' }
$major = ([version]$installation.installationVersion).Major
$generator = switch ($major) { 17 { 'Visual Studio 17 2022' }; 18 { 'Visual Studio 18 2026' }; default { throw "Unsupported Visual Studio version: $major" } }
$build = Join-Path $PSScriptRoot 'build'
# CMake refuses a cache made by another Visual Studio version.
$cache = Join-Path $build 'CMakeCache.txt'
if ((Test-Path -LiteralPath $cache) -and -not (Select-String -LiteralPath $cache -SimpleMatch "CMAKE_GENERATOR:INTERNAL=$generator" -Quiet)) {
    Remove-Item -LiteralPath $cache -Force
    Remove-Item -LiteralPath (Join-Path $build 'CMakeFiles') -Recurse -Force -ErrorAction SilentlyContinue
}
& $cmake -S $PSScriptRoot -B $build -G $generator -A x64 "-DCMAKE_GENERATOR_INSTANCE=$($installation.installationPath)" "-DVLC_ROOT=$VlcRoot"
if ($LASTEXITCODE -ne 0) { throw "Video output configuration failed ($LASTEXITCODE)." }
# Same CPU budget and priority as eng/Build-NativeCapture.ps1.
$jobs = if ($env:CLYPDAT_BUILD_JOBS) { [int]$env:CLYPDAT_BUILD_JOBS } else { [Math]::Max(1, [Math]::Max([int][Math]::Floor([Environment]::ProcessorCount * 0.75), [Environment]::ProcessorCount - 4)) }
$process = [Diagnostics.Process]::GetCurrentProcess()
$priority = $process.PriorityClass
try {
    $process.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
    & $cmake --build $build --config $Configuration --parallel $jobs -- /nodeReuse:false
}
finally { $process.PriorityClass = $priority }
if ($LASTEXITCODE -ne 0) { throw "Video output build failed ($LASTEXITCODE)." }
