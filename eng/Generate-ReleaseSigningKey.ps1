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
if (Test-Path -LiteralPath $OutputPath) { throw "Refusing to overwrite existing key: $OutputPath" }

$directory = Split-Path -Parent $OutputPath
if ([string]::IsNullOrWhiteSpace($directory)) { $directory = '.' }
New-Item -ItemType Directory -Force -Path $directory | Out-Null

$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    # Lock the file down before the key bytes land in it.
    [IO.File]::WriteAllText($OutputPath, '', [Text.UTF8Encoding]::new($false))
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $acl = Get-Acl -LiteralPath $OutputPath
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRule($rule) }
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'Allow')))
    Set-Acl -LiteralPath $OutputPath -AclObject $acl

    [IO.File]::WriteAllText($OutputPath, $rsa.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
    $publicKey = [Convert]::ToBase64String($rsa.ExportSubjectPublicKeyInfo())
}
finally {
    $rsa.Dispose()
}

Write-Host ""
Write-Host "Private key written to $OutputPath (readable only by $([Security.Principal.WindowsIdentity]::GetCurrent().Name))."
Write-Host "Move it somewhere offline. Do NOT commit it and do NOT put it in CI secrets."
Write-Host ""
Write-Host "Paste this into ReleaseSigning.PublicKeySubjectPublicKeyInfoBase64:"
Write-Host ""
Write-Host $publicKey
Write-Host ""
Write-Host "Until that constant is set, the updater keeps its old digest-only behaviour."
