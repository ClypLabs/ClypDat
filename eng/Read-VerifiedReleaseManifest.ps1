[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ManifestPath,
    [Parameter(Mandatory)][string] $SignaturePath,
    [Parameter(Mandatory)][ValidatePattern('\Av[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?\z')][string] $Tag
)
$ErrorActionPreference = 'Stop'
$ReleaseManifestPath = $ManifestPath
$ReleaseSignaturePath = $SignaturePath
if ((Get-Item -LiteralPath $ManifestPath).Length -gt 262144) { throw 'Release manifest exceeds its size limit.' }
if ((Get-Item -LiteralPath $SignaturePath).Length -gt 8192) { throw 'Release signature exceeds its size limit.' }
# Public halves of the release-signing keys the app trusts. Keep this list in sync
# with ReleaseSigning.PinnedPublicKeys in native/src/ClypDat.App/Services/ReleaseSigning.cs.
# The installer hash submitted to WinGet comes from a manifest signed by one of
# these, never from whatever bytes the release host happens to serve.
$pinnedReleaseKeys = @(
    'MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA1REPK3NqTAR33oWpngYIh4Wmvp5sgDFRAn2YWiqwV56dOeAitGmXDfvE0NtVm7igLiqGiNSBW/vrH8GErllwIUcUpbTddIPG9gVf9H0QsVXzudeK0/REsc++3j+kq3FASujX0+cAtiB0yatGyMhiq2c0HSICjqOA5YhrzNMgCXNbdlEHNK3zPdhmQIoUEwxTDv6VFzfGSzTX7Xq43FF9Jsv0Yc0Cvm54KYthDnxl3zH4JyTI6Za1PSQivJNFfP/7b3UriccCShwyrzIvu6sW2n37GltJFSzH1EXzWrC8gXagcqk8Ym9CASV2p78oLo95k2oT0+LVfPfIfV60tWFbPCyqLFd4RzoryWaQaQtEWPmSg798VDNR7qR8evf4/W2ettUs+z55QF10TqGWozzCNJHLRKjhd+3pixT8kkiXeUYGUk7xdBsZOBWdRBT1GBFJqnpDHu/1oEjzgnU6NyyD70fclq+W5t8um1mAJt2e5xZXISCGsaMrvJSnTHMnsJeBAgMBAAE='
)

# Same scheme as ReleaseSigning.VerifyDetached: RSA-PSS with SHA-256 over the exact
# manifest bytes; the .sig file is base64 text.
$manifestBytes = [System.IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $ReleaseManifestPath).Path)
try { $signature = [Convert]::FromBase64String([System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReleaseSignaturePath).Path).Trim()) }
catch { throw "Release manifest signature is not base64: $($_.Exception.Message)" }

$verified = $false
foreach ($publicKey in $pinnedReleaseKeys)
{
    $rsa = [System.Security.Cryptography.RSA]::Create()
    try
    {
        $bytesRead = 0
        $rsa.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($publicKey), [ref] $bytesRead)
        if ($rsa.VerifyData($manifestBytes, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pss))
        {
            $verified = $true
            break
        }
    }
    finally { $rsa.Dispose() }
}
if (-not $verified) { throw "Release manifest signature did not verify against any of the $($pinnedReleaseKeys.Count) pinned release key(s)." }

$manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.tag -cne $Tag) { throw 'Release manifest schema or tag does not match.' }
if (@($manifest.assets).Count -eq 0) { throw 'Release manifest contains no assets.' }
$names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($asset in $manifest.assets) {
    if ($asset.name -notmatch '\A[A-Za-z0-9][A-Za-z0-9._-]*\z' -or -not $names.Add($asset.name)) { throw 'Release manifest contains an invalid or duplicate asset name.' }
    if ([string]$asset.sha256 -notmatch '\A[0-9A-Fa-f]{64}\z') { throw "Invalid SHA-256 for $($asset.name)." }
    if (($asset.size -isnot [int] -and $asset.size -isnot [long]) -or $asset.size -le 0 -or $asset.size -gt 2147483648 -or [long]$asset.size -ne $asset.size) { throw "Invalid size for $($asset.name)." }
}
$manifest
