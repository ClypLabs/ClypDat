param(
    [Parameter(Mandatory = $true)]
    [string]$ObsStudioRoot
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$sourceRoot = Resolve-Path $ObsStudioRoot
$targetRoot = Join-Path $repoRoot "native\vendor\obs-runtime"

if (-not (Test-Path (Join-Path $sourceRoot "bin\64bit"))) {
    throw "OBS root must contain bin\64bit: $sourceRoot"
}

New-Item -ItemType Directory -Force -Path $targetRoot | Out-Null

foreach ($folder in @("bin", "data", "obs-plugins")) {
    $source = Join-Path $sourceRoot $folder
    if (Test-Path $source) {
        Copy-Item -LiteralPath $source -Destination $targetRoot -Recurse -Force
    }
}

$bridge = Join-Path $repoRoot "native\vendor\Eve.ObsBridge.dll"
if (Test-Path $bridge) {
    Copy-Item -LiteralPath $bridge -Destination (Join-Path $targetRoot "Eve.ObsBridge.dll") -Force
}

Write-Host "OBS runtime staged: $targetRoot"
