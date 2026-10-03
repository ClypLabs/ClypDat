[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$ArtifactDirectory
)
$ErrorActionPreference = 'Stop'
if ($Tag -cnotmatch '\Av[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z') {
    throw "Invalid release tag: $Tag"
}
$names = @("ClypDat-$Tag-Setup.exe", "ClypDat-$Tag.msi", "ClypDat-$Tag-Portable.exe", "ClypDat-$Tag-win-x64.zip", 'ClypDat-Setup.exe', 'clypdat-ffmpeg-8.1.2-win64-shared-r3-sources.zip')
foreach ($name in $names) {
    $path = Join-Path $ArtifactDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Missing or empty release asset: $name"
    }
}
$setupHash = (Get-FileHash -LiteralPath (Join-Path $ArtifactDirectory $names[0]) -Algorithm SHA256).Hash
$aliasHash = (Get-FileHash -LiteralPath (Join-Path $ArtifactDirectory 'ClypDat-Setup.exe') -Algorithm SHA256).Hash
if ($setupHash -cne $aliasHash) { throw 'Setup compatibility alias differs from versioned installer.' }
$names
