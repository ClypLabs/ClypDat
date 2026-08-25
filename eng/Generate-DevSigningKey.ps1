[CmdletBinding()]
param(
    # Deliberately mandatory with no default. This used to default to a fixed name
    # under [IO.Path]::GetTempPath(); on an agent where TEMP resolves to
    # C:\Windows\Temp (the default for LocalSystem) that path is predictable and
    # world-readable, so any local account could read the key and sign a Dev
    # manifest that DevPackageVerifier would then accept.
    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) {
    throw "Refusing to overwrite existing key: $OutputPath"
}

$directory = Split-Path -Parent $OutputPath
if ([string]::IsNullOrWhiteSpace($directory)) { $directory = '.' }
New-Item -ItemType Directory -Force -Path $directory | Out-Null

$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    # Create the file empty and lock it down BEFORE the key bytes are written, so
    # the private key is never briefly readable under the directory's inherited ACL.
    [IO.File]::WriteAllText($OutputPath, '', [Text.UTF8Encoding]::new($false))

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $acl = Get-Acl -LiteralPath $OutputPath
    # Break inheritance and drop the inherited entries rather than copying them.
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($rule in @($acl.Access)) { [void]$acl.RemoveAccessRule($rule) }
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $identity, 'FullControl', 'Allow')))
    Set-Acl -LiteralPath $OutputPath -AclObject $acl

    [IO.File]::WriteAllText($OutputPath, $rsa.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
}
finally {
    $rsa.Dispose()
}

Write-Host "Generated a new private signing key at $OutputPath, readable only by $([Security.Principal.WindowsIdentity]::GetCurrent().Name)."
Write-Host "Store it in the CLYPDAT_DEV_SIGNING_PRIVATE_KEY secret, then delete this file. Do not commit it."
