[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$TestGpu,
    [switch]$EnableEncoderInputDiagnostics,
    # Only the recorder DLL the app loads; the app build passes this so it
    # skips linking every native test. Ignored with -Test/-TestGpu.
    [switch]$AppOnly
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../native/capture-native'))
$build = Join-Path $source 'build'
$tools = & (Join-Path $PSScriptRoot 'Get-NativeBuildTools.ps1')
$installation = $tools.Installation
$cmake = $tools.CMake
$generator = $tools.Generator
$msvcVersion = (Get-Content (Join-Path $installation.installationPath 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$msvcBin = Join-Path $installation.installationPath "VC/Tools/MSVC/$msvcVersion/bin/Hostx64/x64"
$sdk = & (Join-Path $PSScriptRoot 'Prepare-CaptureFfmpegSdk.ps1') -MsvcBin $msvcBin -BuildDirectory $build
$encoderInputDiagnostics = if ($EnableEncoderInputDiagnostics) { 'ON' } else { 'OFF' }
# All but four logical processors, or three quarters on small machines (CLYPDAT_BUILD_JOBS
# overrides), at below-normal priority so the desktop or a running game wins.
$jobs = if ($env:CLYPDAT_BUILD_JOBS) { [int]$env:CLYPDAT_BUILD_JOBS } else { [Math]::Max(1, [Math]::Max([int][Math]::Floor([Environment]::ProcessorCount * 0.75), [Environment]::ProcessorCount - 4)) }
$cacheArguments = @(& (Join-Path $PSScriptRoot 'Get-CMakeCacheArguments.ps1') -BuildDirectory $build -Generator $generator -InstallationPath $installation.installationPath)
& $cmake @cacheArguments -S $source -B $build -G $generator -A x64 "-DCLYPDAT_COMPILE_JOBS=$jobs" "-DCMAKE_GENERATOR_INSTANCE=$($installation.installationPath)" "-DCLYPDAT_FFMPEG_SDK=$sdk" "-DCLYPDAT_TEST_GPU=$($TestGpu.IsPresent.ToString().ToUpperInvariant())" "-DCLYPDAT_ENABLE_ENCODER_INPUT_DIAGNOSTICS=$encoderInputDiagnostics"
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($LASTEXITCODE)." }
# RecorderCore compiles alone with /MP before the projects that depend on it,
# so project and per-file parallelism rarely stack. MSBuild and cl inherit
# the below-normal priority.
$process = [Diagnostics.Process]::GetCurrentProcess()
$priority = $process.PriorityClass
try {
    $process.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
    $target = if ($AppOnly -and -not ($Test -or $TestGpu)) { @('--target', 'ClypDat.Capture.Native') } else { @() }
    & $cmake --build $build --config $Configuration --parallel $jobs @target -- /nodeReuse:false
}
finally { $process.PriorityClass = $priority }
if ($LASTEXITCODE -ne 0) { throw "CMake build failed ($LASTEXITCODE)." }
if ($Test -or $TestGpu) {
    & (Join-Path (Split-Path $cmake -Parent) 'ctest.exe') --test-dir $build -C $Configuration --output-on-failure --no-tests=error --timeout 600
    if ($LASTEXITCODE -ne 0) { throw "Native tests failed ($LASTEXITCODE)." }
}
