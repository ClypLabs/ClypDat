[CmdletBinding()]
param(
    [string]$RuntimeDirectory,
    [switch]$VerifyPackage
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RuntimeDirectory)) { $RuntimeDirectory = Join-Path $PSScriptRoot '../native/vendor/ffmpeg' }
function Get-FileSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

$vendor = Join-Path $PSScriptRoot '../native/vendor/ffmpeg'
$manifestPath = Join-Path $vendor 'runtime-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.packageSha256 -cne '064c2f354be3cd5ccdd17fc6a9026ca6ece27101b454394b8d4d56d55de4bec6' -or
    $manifest.sourceSha256 -cne '03289ac6a72fa6bd825f4de2cab4781388e5704a0fa74846f53c5889ad13b499') {
    throw 'FFmpeg runtime manifest is not the accepted deterministic r3 package.'
}
$expected = @($manifest.runtime)
if ($expected.Count -ne 13 -or @($expected.file | Select-Object -Unique).Count -ne 13) { throw 'FFmpeg runtime manifest is incomplete or duplicated.' }
$names = @($expected | ForEach-Object { $_.file })
foreach ($required in @('ffmpeg.exe','ffprobe.exe','avcodec-62.dll','avdevice-62.dll','avfilter-11.dll','avformat-62.dll','avutil-60.dll','swresample-6.dll','swscale-9.dll','libvpl.dll','MSVCP140.dll','VCRUNTIME140.dll','VCRUNTIME140_1.dll')) {
    if ($names -cnotcontains $required) { throw "FFmpeg runtime manifest is missing $required." }
}
$actual = @(Get-ChildItem -LiteralPath $RuntimeDirectory -File | Where-Object { $_.Name -cne 'runtime-manifest.json' })
if ($actual.Count -ne $expected.Count) { throw "FFmpeg runtime file count mismatch in $RuntimeDirectory." }
foreach ($item in $expected) {
    if ([IO.Path]::GetFileName([string]$item.file) -cne $item.file) { throw 'Unsafe FFmpeg runtime filename.' }
    $path = Join-Path $RuntimeDirectory $item.file
    $file = Get-Item -LiteralPath $path -ErrorAction Stop
    if ($file.Length -ne [long]$item.bytes -or (Get-FileSha256 $path) -cne $item.sha256) {
        throw "FFmpeg runtime differs from accepted package: $path"
    }
}
foreach ($item in $actual) {
    if ($names -cnotcontains $item.Name) { throw "Unexpected FFmpeg runtime file: $($item.FullName)" }
}
if ((Test-Path -LiteralPath (Join-Path $RuntimeDirectory 'runtime-manifest.json')) -and
    (Get-FileSha256 (Join-Path $RuntimeDirectory 'runtime-manifest.json')) -cne (Get-FileSha256 $manifestPath)) {
    throw 'Packaged FFmpeg runtime manifest differs from the vendor manifest.'
}
if ($VerifyPackage) {
    $artifacts = Join-Path $PSScriptRoot 'ffmpeg/artifacts'
    foreach ($entry in @(@($manifest.package,$manifest.packageSha256),@($manifest.sourcePackage,$manifest.sourceSha256))) {
        $path = Join-Path $artifacts $entry[0]
        if ((Get-FileSha256 $path) -cne $entry[1]) { throw "FFmpeg package hash mismatch: $path" }
    }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $artifacts $manifest.package))
    try {
        $prefix = [IO.Path]::GetFileNameWithoutExtension($manifest.package) + '/'
        $packageManifest = $zip.GetEntry($prefix + 'manifest.json')
        if ($null -eq $packageManifest) { throw 'Accepted FFmpeg package manifest is missing.' }
        $reader = [IO.StreamReader]::new($packageManifest.Open())
        try { $packageRows = ConvertFrom-Json -InputObject ($reader.ReadToEnd()) }
        finally { $reader.Dispose() }
        $packageRuntime = @($packageRows | Where-Object { $_.path -like 'bin/*' })
        if ($packageRuntime.Count -ne $expected.Count) { throw 'Vendor manifest and package runtime counts differ.' }
        foreach ($item in $expected) {
            $matches = @($packageRuntime | Where-Object { $_.path -ceq "bin/$($item.file)" -and $_.sha256 -ceq $item.sha256 -and [long]$_.bytes -eq [long]$item.bytes })
            if ($matches.Count -ne 1) { throw "Vendor runtime manifest does not match accepted package: $($item.file)" }
        }
    }
    finally { $zip.Dispose() }
}
Write-Host "Verified $($expected.Count) FFmpeg runtime files in $RuntimeDirectory."
