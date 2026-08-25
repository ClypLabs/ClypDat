<#
.SYNOPSIS
Generates the offline release-signing key pair.

.DESCRIPTION
This key signs the release manifest that ClypDat's updater verifies before installing
an update. It is the control that makes the installer's SHA-256 independent of whoever
serves the release metadata, so it MUST stay off CI and off every release host - if the
private half ever reaches the same place as the download URL, it stops proving anything.

Keep the private key offline (a password manager, an encrypted volume, a hardware token).
Signing is done locally with eng/Sign-ReleaseManifest.ps1 and only the resulting .sig is
published.

.EXAMPLE
.\eng\Generate-ReleaseSigningKey.ps1 -OutputPath D:\keys\clypdat-release-private.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

# PowerShell 7+ required. The key handling here uses ExportPkcs8PrivateKeyPem /
# ImportFromPem, which are .NET Core 3.0+ APIs and simply do not exist on the .NET
# Framework that Windows PowerShell 5.1 runs on - where this fails with a confusing
# "does not contain a method named" error partway through.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    throw "This script needs PowerShell 7+. Re-run it with 'pwsh' instead of 'powershell' (current: $($PSVersionTable.PSVersion))."
}
if (Test-Path -LiteralPath $OutputPath) { throw "Refusing to overwrite existing key: $OutputPath" }

$directory = Split-Path -Parent $OutputPath
if ([string]::IsNullOrWhiteSpace($directory)) { $directory = '.' }
New-Item -ItemType Directory -Force -Path $directory | Out-Null

$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    # Create the file empty and lock it down BEFORE the key bytes land in it, so the
    # private key is never briefly readable under the directory's inherited ACL.
    [IO.File]::WriteAllText($OutputPath, '', [Text.UTF8Encoding]::new($false))

    # icacls rather than Get-Acl/Set-Acl: those live in Microsoft.PowerShell.Security,
    # which does not autoload in every host (a constrained Windows PowerShell 5.1 will
    # fail with CommandNotFoundException), and an ACL failure here must not be something
    # you can shrug off - it is the only thing keeping the key from other accounts.
    #   /inheritance:r  drop inherited entries instead of copying them
    #   /grant:r        replace any existing grant for this identity
    $icacls = & icacls "$OutputPath" /inheritance:r /grant:r "${identity}:(F)" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not restrict permissions on ${OutputPath}: $icacls"
    }

    [IO.File]::WriteAllText($OutputPath, $rsa.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
    $publicKey = [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
}
catch {
    # Never leave a key file behind that is not protected, and never leave an empty stub
    # that blocks the next attempt.
    if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force -ErrorAction SilentlyContinue }
    throw
}
finally {
    $rsa.Dispose()
}

Write-Host ""
Write-Host "Private key written to $OutputPath (readable only by $([Security.Principal.WindowsIdentity]::GetCurrent().Name))."
Write-Host "Move it somewhere offline. Do NOT commit it and do NOT put it in CI secrets."
Write-Host ""
Write-Host "Add this to ReleaseSigning.PinnedPublicKeys as:"
Write-Host '    new PinnedReleaseKey("<label>", "<the base64 below>")'
Write-Host ""
# Write-Output, not Write-Host, so callers can capture it:
#   $pub = .\eng\Generate-ReleaseSigningKey.ps1 -OutputPath ... | Select-Object -Last 1
Write-Output $publicKey
Write-Host ""
Write-Host "Until at least one key is pinned, the updater keeps its old digest-only behaviour."
