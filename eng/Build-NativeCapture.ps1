[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$TestGpu,
    [switch]$EnableEncoderInputDiagnostics
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../native/capture-native'))
$build = Join-Path $source 'build'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer (vswhere.exe) is required.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json | Select-Object -First 1
if (-not $installation) { throw 'Install Visual Studio C++ x64 build tools.' }
$msvcVersion = (Get-Content (Join-Path $installation.installationPath 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$msvcBin = Join-Path $installation.installationPath "VC/Tools/MSVC/$msvcVersion/bin/Hostx64/x64"
$cmake = $env:CLYPDAT_CMAKE
if (-not $cmake) { $cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source }
if (-not $cmake) { $cmake = Join-Path $installation.installationPath 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe' }
if (-not (Test-Path -LiteralPath $cmake)) { throw 'Install CMake or set CLYPDAT_CMAKE.' }
$major = ([version]$installation.installationVersion).Major
$generator = switch ($major) { 17 { 'Visual Studio 17 2022' }; 18 { 'Visual Studio 18 2026' }; default { throw "Unsupported Visual Studio version: $major" } }
$sdk = & (Join-Path $PSScriptRoot 'Prepare-CaptureFfmpegSdk.ps1') -MsvcBin $msvcBin -BuildDirectory $build
$encoderInputDiagnostics = if ($EnableEncoderInputDiagnostics) { 'ON' } else { 'OFF' }
& $cmake -S $source -B $build -G $generator -A x64 "-DCMAKE_GENERATOR_INSTANCE=$($installation.installationPath)" "-DCLYPDAT_FFMPEG_SDK=$sdk" "-DCLYPDAT_TEST_GPU=$($TestGpu.IsPresent.ToString().ToUpperInvariant())" "-DCLYPDAT_ENABLE_ENCODER_INPUT_DIAGNOSTICS=$encoderInputDiagnostics"
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($LASTEXITCODE)." }
# Uses about three quarters of the logical processors (CLYPDAT_BUILD_JOBS
# overrides). MultiToolTask shares one compiler-process limit across every
# project, and below-normal priority, inherited by MSBuild and cl, yields to
# the desktop or a running game.
$jobs = if ($env:CLYPDAT_BUILD_JOBS) { [int]$env:CLYPDAT_BUILD_JOBS } else { [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount * 0.75)) }
$process = [Diagnostics.Process]::GetCurrentProcess()
$priority = $process.PriorityClass
try {
    $process.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
    & $cmake --build $build --config $Configuration --parallel $jobs -- /nodeReuse:false /p:UseMultiToolTask=true /p:EnforceProcessCountAcrossBuilds=true /p:MultiProcMaxCount=$jobs
}
finally { $process.PriorityClass = $priority }
if ($LASTEXITCODE -ne 0) { throw "CMake build failed ($LASTEXITCODE)." }
if ($Test -or $TestGpu) {
    & (Join-Path (Split-Path $cmake -Parent) 'ctest.exe') --test-dir $build -C $Configuration --output-on-failure --no-tests=error --timeout 600
    if ($LASTEXITCODE -ne 0) { throw "Native tests failed ($LASTEXITCODE)." }
}
