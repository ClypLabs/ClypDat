[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PrivateKeyPath,
    [string]$Repository = 'ClypLabs/ClypDat'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) {
    throw "Private key file was not found: $PrivateKeyPath"
}

$key = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $PrivateKeyPath))
if ([string]::IsNullOrWhiteSpace($key) -or $key -notmatch 'BEGIN PRIVATE KEY') {
    throw 'The supplied file does not contain a PKCS#8 PEM private key.'
}

$key | gh secret set CLYPDAT_DEV_SIGNING_PRIVATE_KEY --repo $Repository
if ($LASTEXITCODE -ne 0) { throw "gh secret set failed with exit code $LASTEXITCODE." }
Write-Host "Configured CLYPDAT_DEV_SIGNING_PRIVATE_KEY for $Repository without printing its value."
