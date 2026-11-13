[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$VlcRoot = "$env:USERPROFILE/.nuget/packages/videolan.libvlc.windows/3.0.23.1/build/x64"
)
$ErrorActionPreference = 'Stop'
$tools = & (Join-Path $PSScriptRoot '../../eng/Get-NativeBuildTools.ps1')
$installation = $tools.Installation
$cmake = $tools.CMake
$generator = $tools.Generator
$build = Join-Path $PSScriptRoot 'build'
$cacheArguments = @(& (Join-Path $PSScriptRoot '../../eng/Get-CMakeCacheArguments.ps1') -BuildDirectory $build -Generator $generator -InstallationPath $installation.installationPath)
& $cmake @cacheArguments -S $PSScriptRoot -B $build -G $generator -A x64 "-DCMAKE_GENERATOR_INSTANCE=$($installation.installationPath)" "-DVLC_ROOT=$VlcRoot"
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
