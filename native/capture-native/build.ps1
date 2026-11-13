[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$build = Join-Path $source 'build'
$tools = & (Join-Path $PSScriptRoot '../../eng/Get-NativeBuildTools.ps1')
$installation = $tools.Installation
$cmake = $tools.CMake
$generator = $tools.Generator
$cacheArguments = @(& (Join-Path $PSScriptRoot '../../eng/Get-CMakeCacheArguments.ps1') -BuildDirectory $build -Generator $generator -InstallationPath $installation.installationPath)

& $cmake @cacheArguments -S $source -B $build -G $generator -A x64 "-DCMAKE_GENERATOR_INSTANCE=$($installation.installationPath)"
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($LASTEXITCODE)." }
& $cmake --build $build --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "CMake build failed ($LASTEXITCODE)." }
