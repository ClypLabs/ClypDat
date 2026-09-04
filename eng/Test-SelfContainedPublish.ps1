[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$publishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path

$requiredFiles = @(
    'ClypDat.exe',
    'ClypDatRecorder.exe',
    'ClypDatDetectorHost.exe',
    'ClypDat.runtimeconfig.json',
    'ClypDatRecorder.dll',
    'ClypDatRecorder.deps.json',
    'ClypDatRecorder.runtimeconfig.json',
    'ClypDatDetectorHost.dll',
    'ClypDatDetectorHost.deps.json',
    'ClypDatDetectorHost.runtimeconfig.json',
    'coreclr.dll',
    'hostfxr.dll',
    'hostpolicy.dll'
)

foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $file))) {
        throw "Self-contained publish is missing $file."
    }
}

$workerInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDirectory 'ClypDatRecorder.exe'))
if ($workerInfo.FileDescription -ne 'ClypDat Recorder') {
    throw "Recorder FileDescription is '$($workerInfo.FileDescription)', expected 'ClypDat Recorder'."
}
if ($workerInfo.ProductName -ne 'ClypDat Recorder') {
    throw "Recorder ProductName is '$($workerInfo.ProductName)', expected 'ClypDat Recorder'."
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
    $process = Start-Process -FilePath (Join-Path $publishDirectory 'ClypDat.exe') `
        -ArgumentList '--verify-self-contained' `
        -WorkingDirectory $publishDirectory `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Bundled ClypDat runtime check failed with exit code $($process.ExitCode)."
    }
    $workerProcess = Start-Process -FilePath (Join-Path $publishDirectory 'ClypDatRecorder.exe') `
        -ArgumentList '--verify-self-contained' `
        -WorkingDirectory $publishDirectory `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($workerProcess.ExitCode -ne 0) {
        throw "Bundled recorder runtime check failed with exit code $($workerProcess.ExitCode)."
    }
    $detectorProcess = Start-Process -FilePath (Join-Path $publishDirectory 'ClypDatDetectorHost.exe') `
        -ArgumentList '--verify-self-contained' `
        -WorkingDirectory $publishDirectory `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($detectorProcess.ExitCode -ne 0) {
        throw "Bundled detector-host runtime check failed with exit code $($detectorProcess.ExitCode)."
    }
}
finally {
    $env:DOTNET_ROOT = $originalDotnetRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = $originalMultilevelLookup
}

Write-Host 'Self-contained publish verified.'
