[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path ([IO.Path]::GetTempPath()) 'clypdat-dev-signing-private.pem')
)

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) {
    throw "Refusing to overwrite existing key: $OutputPath"
}

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    [IO.File]::WriteAllText($OutputPath, $rsa.ExportPkcs8PrivateKeyPem(), [Text.UTF8Encoding]::new($false))
}
finally {
    $rsa.Dispose()
}

Write-Host "Generated a new private signing key at $OutputPath. Store it in the CLYPDAT_DEV_SIGNING_PRIVATE_KEY secret; do not commit this file."
