<#
.SYNOPSIS
Builds and signs the release manifest for a set of release assets.

.DESCRIPTION
Produces ClypDat-Release.manifest.json (tag, version, and each asset's size and SHA-256)
plus a detached ClypDat-Release.manifest.sig, signed with the offline release key using
RSA-PSS/SHA-256 - the same scheme the Dev channel uses.

Run this locally against the built artifacts, then upload both files to the GitHub
release alongside the installers. The private key never leaves this machine.

.EXAMPLE
.\eng\Sign-ReleaseManifest.ps1 -Tag v1.4.0 -ArtifactDirectory .\artifacts -PrivateKeyPath D:\keys\clypdat-release-private.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$PrivateKeyPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

# PowerShell 7+ required. The key handling here uses ExportPkcs8PrivateKeyPem /
# ImportFromPem, which are .NET Core 3.0+ APIs and simply do not exist on the .NET
# Framework that Windows PowerShell 5.1 runs on - where this fails with a confusing
# "does not contain a method named" error partway through.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    throw "This script needs PowerShell 7+. Re-run it with 'pwsh' instead of 'powershell' (current: $($PSVersionTable.PSVersion))."
}
if ($Tag -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$') { throw "Tag '$Tag' is not vMAJOR.MINOR.PATCH." }
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) { throw "Private key not found: $PrivateKeyPath" }
if (-not (Test-Path -LiteralPath $ArtifactDirectory -PathType Container)) { throw "Artifact directory not found: $ArtifactDirectory" }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = $ArtifactDirectory }

# ClypDat-Setup.exe is what the in-app updater downloads, so it must be covered.
# The others are listed when present so a manual download can be checked too.
$assetNames = @('ClypDat-Setup.exe', 'ClypDat-Portable.exe', 'ClypDat-win-x64.zip', 'ClypDat.msi')

$assets = @()
foreach ($name in $assetNames) {
    $path = Join-Path $ArtifactDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Host "  skipping $name (not present)"
        continue
    }

    $item = Get-Item -LiteralPath $path
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $assets += [ordered]@{ name = $name; size = $item.Length; sha256 = $hash }
    Write-Host "  $name  $($item.Length) bytes  $hash"
}

if ($assets.Count -eq 0) { throw "No known release assets found in $ArtifactDirectory." }
if (-not ($assets.name -contains 'ClypDat-Setup.exe')) { throw "ClypDat-Setup.exe is required - the updater resolves that asset by name." }

$manifest = [ordered]@{
    schema     = 1
    tag        = $Tag
    version    = $Tag.TrimStart('v')
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    assets     = $assets
}

# Compact and written as UTF-8 without BOM: the signature covers these exact bytes.
$manifestBytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 5 -Compress))
$manifestPath = Join-Path $OutputDirectory 'ClypDat-Release.manifest.json'
$signaturePath = Join-Path $OutputDirectory 'ClypDat-Release.manifest.sig'
[IO.File]::WriteAllBytes($manifestPath, $manifestBytes)

$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText($PrivateKeyPath))
    $signature = $rsa.SignData($manifestBytes, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)

    # Verify locally before publishing, so a bad key or encoding fails here rather than
    # in every user's updater.
    if (-not $rsa.VerifyData($manifestBytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) {
        throw "Signature failed to verify against its own key."
    }

    [IO.File]::WriteAllText($signaturePath, [Convert]::ToBase64String($signature), [Text.UTF8Encoding]::new($false))
    $publicKey = [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
}
finally {
    $rsa.Dispose()
}

Write-Host ""
Write-Host "Manifest:  $manifestPath"
Write-Host "Signature: $signaturePath"
Write-Host ""
Write-Host "Upload BOTH to the $Tag release:"
Write-Host "  gh release upload $Tag `"$manifestPath`" `"$signaturePath`" --repo ClypLabs/ClypDat"
Write-Host ""
Write-Host "Signing key's public half (must match ReleaseSigning.PublicKeySubjectPublicKeyInfoBase64):"
Write-Host $publicKey
