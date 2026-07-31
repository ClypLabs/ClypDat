[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$publishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path

$requiredFiles = @(
    'ClypDat.exe',
    'ClypDat.runtimeconfig.json',
    'coreclr.dll',
    'hostfxr.dll',
    'hostpolicy.dll'
)

foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $file))) {
        throw "Self-contained publish is missing $file."
    }
}

$runtimeConfig = Get-Content -Raw (Join-Path $publishDirectory 'ClypDat.runtimeconfig.json') | ConvertFrom-Json
$runtimeOptions = $runtimeConfig.runtimeOptions
if ($null -eq $runtimeOptions.includedFrameworks) {
    throw 'Self-contained publish runtimeconfig has no includedFrameworks.'
}

$frameworkNames = @($runtimeOptions.includedFrameworks | ForEach-Object { $_.name })
foreach ($framework in 'Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') {
    if ($frameworkNames -notcontains $framework) {
        throw "Self-contained publish does not include $framework."
    }
}

if ($null -ne $runtimeOptions.framework -or $null -ne $runtimeOptions.frameworks) {
    throw 'Publish is framework-dependent.'
}

$originalDotnetRoot = $env:DOTNET_ROOT
$originalMultilevelLookup = $env:DOTNET_MULTILEVEL_LOOKUP
try {
    $env:DOTNET_ROOT = Join-Path ([IO.Path]::GetTempPath()) 'clypdat-no-dotnet'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    & (Join-Path $publishDirectory 'ClypDat.exe') --verify-self-contained
    if ($LASTEXITCODE -ne 0) {
        throw "Bundled ClypDat runtime check failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:DOTNET_ROOT = $originalDotnetRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = $originalMultilevelLookup
}

Write-Host 'Self-contained publish verified.'
