[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MsvcBin,
    [string]$BuildDirectory = (Join-Path $PSScriptRoot '../native/capture-native/build')
)

$ErrorActionPreference = 'Stop'
function Get-Sha256([string]$Path) {
    # MSBuild invokes Windows PowerShell; its module search path can inherit
    # PowerShell 7 modules where Get-FileHash cannot autoload.
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
$archiveName = 'ffmpeg-8.1.2-full_build-shared.7z'
# Publisher: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-full_build-shared.7z.sha256
$archiveHash = 'cba748035c21ce1431d0823c7a3a711f38616f89f87a265dceddf9b7f6749d2d'
$sdk = Join-Path $BuildDirectory 'sdk'
$archive = Join-Path $sdk $archiveName
$extracted = Join-Path $sdk 'extracted'
$package = Join-Path $extracted 'ffmpeg-8.1.2-full_build-shared'
$runtime = Join-Path $PSScriptRoot '../native/vendor/ffmpeg'
$libraries = @('avcodec-62', 'avformat-62', 'avutil-60', 'swresample-6', 'swscale-9')
New-Item -ItemType Directory -Force $sdk | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest "https://www.gyan.dev/ffmpeg/builds/packages/$archiveName" -OutFile $archive -UseBasicParsing
}
if ((Get-Sha256 $archive) -ne $archiveHash) {
    throw "FFmpeg SDK checksum mismatch: $archive. Remove the archive and retry."
}
$sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
if (-not $sevenZip) { $sevenZip = Join-Path $env:ProgramFiles '7-Zip/7z.exe' }
if (-not (Test-Path -LiteralPath $sevenZip)) { throw '7-Zip is required to extract the pinned FFmpeg SDK.' }
# Always re-extract verified headers and reference DLLs; an edited cached header
# must not silently change the ABI used to build the recorder.
& $sevenZip x $archive "-o$extracted" '-y' '*/include/*' '*/bin/*.dll' | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg SDK extraction failed.' }
$importDirectory = Join-Path $sdk 'lib'
New-Item -ItemType Directory -Force $importDirectory | Out-Null
foreach ($library in $libraries) {
    $dll = Join-Path $runtime "$library.dll"
    if ((Get-Sha256 $dll) -ne (Get-Sha256 (Join-Path $package "bin/$library.dll"))) {
        throw "Bundled $library.dll does not match the pinned FFmpeg 8.1.2 SDK."
    }
    $exports = & (Join-Path $MsvcBin 'dumpbin.exe') /nologo /exports $dll
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect exports of $dll." }
    $names = @($exports | ForEach-Object {
        if ($_ -match '^\s+\d+\s+[0-9A-F]+\s+[0-9A-F]+\s+([A-Za-z_][A-Za-z_0-9]*)\s*$') { $Matches[1] }
    })
    if ($names.Count -eq 0) { throw "No exports found in $dll." }
    $definition = Join-Path $importDirectory "$library.def"
    @("LIBRARY $library.dll", 'EXPORTS') + $names | Set-Content -LiteralPath $definition -Encoding Ascii
    $name = $library -replace '-\d+$', ''
    & (Join-Path $MsvcBin 'lib.exe') /nologo /machine:x64 "/def:$definition" "/out:$(Join-Path $importDirectory "$name.lib")" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not generate MSVC import library for $library." }
}
Write-Output $sdk
