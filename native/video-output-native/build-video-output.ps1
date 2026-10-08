[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$VlcRoot = "$env:USERPROFILE/.nuget/packages/videolan.libvlc.windows/3.0.23.1/build/x64"
)
$ErrorActionPreference = 'Stop'
$cmake = @(
    $env:CLYPDAT_CMAKE,
    'C:/Program Files/Microsoft Visual Studio/18/Community/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe',
    'C:/Program Files/Microsoft Visual Studio/17/Community/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $cmake) { $cmake = (Get-Command cmake -ErrorAction Stop).Source }
& $cmake -S $PSScriptRoot -B "$PSScriptRoot/build" -G 'Visual Studio 18 2026' -A x64 "-DVLC_ROOT=$VlcRoot"
if ($LASTEXITCODE -ne 0) { throw "Video output configuration failed ($LASTEXITCODE)." }
# Same CPU budget and priority as eng/Build-NativeCapture.ps1.
$jobs = if ($env:CLYPDAT_BUILD_JOBS) { [int]$env:CLYPDAT_BUILD_JOBS } else { [Math]::Max(1, [Math]::Max([int][Math]::Floor([Environment]::ProcessorCount * 0.75), [Environment]::ProcessorCount - 4)) }
$process = [Diagnostics.Process]::GetCurrentProcess()
$priority = $process.PriorityClass
try {
    $process.PriorityClass = [Diagnostics.ProcessPriorityClass]::BelowNormal
    & $cmake --build "$PSScriptRoot/build" --config $Configuration --parallel $jobs -- /nodeReuse:false
}
finally { $process.PriorityClass = $priority }
if ($LASTEXITCODE -ne 0) { throw "Video output build failed ($LASTEXITCODE)." }
