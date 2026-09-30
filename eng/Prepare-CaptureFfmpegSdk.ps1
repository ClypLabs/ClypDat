[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsvcBin,
    [string]$BuildDirectory = (Join-Path $PSScriptRoot '../native/capture-native/build')
)

$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

$vendor = Join-Path $PSScriptRoot '../native/vendor/ffmpeg'
& (Join-Path $PSScriptRoot 'Verify-CaptureFfmpegRuntime.ps1') -RuntimeDirectory $vendor -VerifyPackage | Out-Host
$manifest = Get-Content -LiteralPath (Join-Path $vendor 'runtime-manifest.json') -Raw | ConvertFrom-Json
$archive = Join-Path $PSScriptRoot "ffmpeg/artifacts/$($manifest.package)"
$sdk = Join-Path $BuildDirectory 'sdk'
$buildRoot = [IO.Path]::GetFullPath($BuildDirectory).TrimEnd('\')
$sdkRoot = [IO.Path]::GetFullPath($sdk)
if (-not $sdkRoot.Equals((Join-Path $buildRoot 'sdk'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected FFmpeg SDK directory.' }
$sdk = $sdkRoot
New-Item -ItemType Directory -Path $sdk -Force | Out-Null
if (((Get-Item -LiteralPath $sdkRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'FFmpeg SDK staging must not be a junction.' }
foreach ($directory in @('include','lib','extracted')) {
    $target = Join-Path $sdkRoot $directory
    if (-not ([IO.Path]::GetFullPath($target)).StartsWith($sdkRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'FFmpeg SDK cleanup escaped staging.' }
    if (Test-Path -LiteralPath $target) {
        if (((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'FFmpeg SDK cleanup target must not be a junction.' }
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

# The package SHA authenticates its own manifest and every header/import library.
# Re-extract on each configure so an edited SDK cache never changes the ABI.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $prefix = [IO.Path]::GetFileNameWithoutExtension($manifest.package) + '/'
    $manifestEntry = $zip.GetEntry($prefix + 'manifest.json')
    if ($null -eq $manifestEntry) { throw 'Accepted FFmpeg package has no manifest.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $payload = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
    $sdkFiles = @($payload | Where-Object { $_.path -like 'include/*' -or $_.path -like 'lib/*' })
    if (-not ($sdkFiles.path -contains 'include/libavcodec/avcodec.h')) { throw 'Accepted FFmpeg SDK has no avcodec headers.' }
    foreach ($library in 'avcodec','avformat','avutil','swresample','swscale') {
        if (-not ($sdkFiles.path -contains "lib/$library.lib")) { throw "Accepted FFmpeg SDK is missing $library.lib." }
    }
    foreach ($item in $sdkFiles) {
        $entry = $zip.GetEntry($prefix + $item.path)
        if ($null -eq $entry -or $entry.Length -ne [long]$item.bytes) { throw "FFmpeg SDK entry missing or wrong size: $($item.path)" }
        $relative = [string]$item.path
        if ($relative.Contains('..') -or $relative.StartsWith('/') -or $relative.Contains('\')) { throw 'Unsafe FFmpeg SDK entry path.' }
        $target = Join-Path $sdk ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        $inputStream = $entry.Open()
        $outputStream = [IO.File]::Create($target)
        try { $inputStream.CopyTo($outputStream) }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }
        if ((Get-Sha256 $target) -cne $item.sha256) { throw "FFmpeg SDK payload hash mismatch: $relative" }
    }
    foreach ($directory in @('include','lib')) {
        $expectedNames = @($sdkFiles | Where-Object { $_.path -like "$directory/*" } | ForEach-Object { $_.path.Substring($directory.Length + 1).Replace('/', '\') })
        $actualNames = @(Get-ChildItem -LiteralPath (Join-Path $sdk $directory) -File -Recurse | ForEach-Object { $_.FullName.Substring((Join-Path $sdk $directory).Length + 1) })
        $unexpected = @($actualNames | Where-Object { $_ -notin $expectedNames })
        if ($unexpected.Count -ne 0) { throw "Unverified file remains in FFmpeg SDK ${directory}: $($unexpected[0])" }
    }
}
finally { $zip.Dispose() }
Write-Output $sdk
