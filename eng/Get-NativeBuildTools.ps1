[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio Installer (vswhere.exe) is required.' }
# All editions, including standalone Build Tools, with the x64 C++ workload.
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -format json | ConvertFrom-Json | Select-Object -First 1
if ($LASTEXITCODE -ne 0) { throw 'Visual Studio discovery failed.' }
if (-not $installation) { throw 'Visual Studio C++ x64 build tools are required. Finish any pending Visual Studio installation or update before building.' }
$major = ([version]$installation.installationVersion).Major

if ($env:CLYPDAT_CMAKE) {
    # An explicit override is authoritative; do not silently choose another tool.
    $candidates = @($env:CLYPDAT_CMAKE)
}
else {
    $candidates = @(
        (Join-Path $installation.installationPath 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe')
        (Get-Command cmake -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
    ) | Where-Object { $_ } | Select-Object -Unique
}
foreach ($cmake in $candidates) {
    if (-not (Test-Path -LiteralPath $cmake -PathType Leaf)) { continue }
    $json = & $cmake -E capabilities
    if ($LASTEXITCODE -ne 0) { continue }
    $capabilities = $json | ConvertFrom-Json
    $version = $capabilities.version
    if ([version]"$($version.major).$($version.minor).$($version.patch)" -lt [version]'3.25') { continue }
    # Ask CMake for the generator name instead of mapping VS majors to years.
    $generator = $capabilities.generators | Where-Object {
        $_.name -match "^Visual Studio $major(?: |$)" -and $_.platformSupport
    } | Select-Object -First 1
    if (-not $generator) { continue }
    Write-Host "Using $($installation.displayName) with CMake $($version.string)."
    return [PSCustomObject]@{
        Installation = $installation
        CMake = $cmake
        Generator = $generator.name
    }
}
throw "No CMake 3.25+ installation supports Visual Studio $major at $($installation.installationPath). Install Visual Studio's CMake tools, update CMake, or set CLYPDAT_CMAKE to a compatible cmake.exe."
