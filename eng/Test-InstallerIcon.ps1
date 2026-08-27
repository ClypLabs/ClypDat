[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $InstallerPath,

    [Parameter(Mandatory)]
    [string] $IconPath
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

function Get-RenderedIconPixels {
    param([string] $Path, [int] $Size)

    $icon = [System.Drawing.Icon]::ExtractAssociatedIcon($Path)
    if ($null -eq $icon) { throw "Could not extract an icon from $Path" }
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try
    {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.DrawIcon($icon, 0, 0) }
        finally { $graphics.Dispose() }

        $pixels = [System.Collections.Generic.List[int]]::new($Size * $Size)
        for ($y = 0; $y -lt $Size; $y++)
        {
            for ($x = 0; $x -lt $Size; $x++) { $pixels.Add($bitmap.GetPixel($x, $y).ToArgb()) }
        }
        return $pixels.ToArray()
    }
    finally
    {
        $bitmap.Dispose()
        $icon.Dispose()
    }
}

foreach ($path in @($InstallerPath, $IconPath))
{
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Icon validation file does not exist: $path" }
}

# Windows returns this associated icon for the setup executable in Apps &
# Features. Rendering both sources through the same GDI+ path makes this an
# exact pixel comparison, not merely an icon-presence check.
foreach ($size in 32)
{
    $expected = Get-RenderedIconPixels -Path $IconPath -Size $size
    $actual = Get-RenderedIconPixels -Path $InstallerPath -Size $size
    if ($expected.Length -ne $actual.Length -or (Compare-Object $expected $actual -SyncWindow 0))
    {
        throw "Installer icon differs from $IconPath at ${size}x${size}."
    }
}

Write-Output 'Installer icon pixel validation passed.'
