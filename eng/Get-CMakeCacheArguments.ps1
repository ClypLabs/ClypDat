[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [Parameter(Mandatory)][string]$Generator,
    [Parameter(Mandatory)][string]$InstallationPath
)
$ErrorActionPreference = 'Stop'
$cache = Join-Path $BuildDirectory 'CMakeCache.txt'
if (-not (Test-Path -LiteralPath $cache -PathType Leaf)) { return }

$entries = @{}
foreach ($line in Get-Content -LiteralPath $cache) {
    if ($line -match '^(CMAKE_GENERATOR|CMAKE_GENERATOR_INSTANCE|CMAKE_C_COMPILER|CMAKE_CXX_COMPILER|CMAKE_AR|CMAKE_LINKER):[^=]*=(.*)$') {
        $entries[$Matches[1]] = $Matches[2]
    }
}

# Switching VS editions keeps the generator name but invalidates cached tools.
$cachedInstance = ([string]$entries['CMAKE_GENERATOR_INSTANCE']).Replace('/', '\').TrimEnd('\')
$selectedInstance = $InstallationPath.Replace('/', '\').TrimEnd('\')
$refresh = $entries['CMAKE_GENERATOR'] -ne $Generator -or $cachedInstance -ne $selectedInstance
foreach ($tool in @('CMAKE_C_COMPILER', 'CMAKE_CXX_COMPILER', 'CMAKE_AR', 'CMAKE_LINKER')) {
    # A failed configure can already contain the new instance and the old tools.
    if ($entries[$tool] -and -not (Test-Path -LiteralPath $entries[$tool] -PathType Leaf)) {
        $refresh = $true
    }
}
if ($refresh) {
    Write-Host "Refreshing stale CMake toolchain cache in $BuildDirectory."
    # CMake 3.24+ removes only its cache and CMakeFiles, preserving build/sdk.
    '--fresh'
}
