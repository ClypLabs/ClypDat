[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+$')]
    [string] $Tag,

    [Parameter(Mandatory)]
    [ValidatePattern('^https://github\.com/ClypLabs/ClypDat/releases/download/v[0-9]+\.[0-9]+\.[0-9]+/ClypDat-Setup\.exe$')]
    [string] $InstallerUrl,

    [Parameter(Mandatory)]
    [string] $OutputDirectory,

    [string] $InstallerPath,

    # Signed release manifest and its detached signature. Downloaded from the same
    # release as the installer when not given.
    [string] $ReleaseManifestPath,

    [string] $ReleaseSignaturePath,

    [switch] $Submit,

    [string] $WingetCreatePath = (Join-Path $PSScriptRoot '..\wingetcreate.exe')
)

$ErrorActionPreference = 'Stop'
$version = $Tag.Substring(1)
if ($InstallerUrl -cne "https://github.com/ClypLabs/ClypDat/releases/download/$Tag/ClypDat-Setup.exe") { throw "Installer URL does not match release tag." }

function Save-ReleaseAsset
{
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string] $Destination)
    $url = "https://github.com/ClypLabs/ClypDat/releases/download/$Tag/$Name"
    try { Invoke-WebRequest -Uri $url -OutFile $Destination }
    catch { throw "Could not download release asset ${url}: $($_.Exception.Message)" }
}

$downloadDirectory = [System.IO.Path]::GetTempPath()
if ([string]::IsNullOrWhiteSpace($ReleaseManifestPath))
{
    $ReleaseManifestPath = Join-Path $downloadDirectory "ClypDat-Release-$version.manifest.json"
    Save-ReleaseAsset -Name 'ClypDat-Release.manifest.json' -Destination $ReleaseManifestPath
}
if ([string]::IsNullOrWhiteSpace($ReleaseSignaturePath))
{
    $ReleaseSignaturePath = Join-Path $downloadDirectory "ClypDat-Release-$version.manifest.sig"
    Save-ReleaseAsset -Name 'ClypDat-Release.manifest.sig' -Destination $ReleaseSignaturePath
}
if (-not (Test-Path -LiteralPath $ReleaseManifestPath -PathType Leaf)) { throw "Release manifest does not exist: $ReleaseManifestPath" }
if (-not (Test-Path -LiteralPath $ReleaseSignaturePath -PathType Leaf)) { throw "Release manifest signature does not exist: $ReleaseSignaturePath" }

$releaseManifest = & (Join-Path $PSScriptRoot 'Read-VerifiedReleaseManifest.ps1') `
    -ManifestPath $ReleaseManifestPath -SignaturePath $ReleaseSignaturePath -Tag $Tag
$setupAssets = @($releaseManifest.assets | Where-Object { $_.name -eq 'ClypDat-Setup.exe' })
if ($setupAssets.Count -ne 1) { throw 'Signed release manifest does not list exactly one ClypDat-Setup.exe.' }
$sha256 = ([string] $setupAssets[0].sha256).Trim().ToLowerInvariant()
if ($sha256 -notmatch '^[0-9a-f]{64}$') { throw 'Signed release manifest has no valid SHA-256 for ClypDat-Setup.exe.' }

if ([string]::IsNullOrWhiteSpace($InstallerPath))
{
    $InstallerPath = Join-Path $downloadDirectory "ClypDat-Setup-$version.exe"
    try { Invoke-WebRequest -Uri $InstallerUrl -OutFile $InstallerPath }
    catch { throw "Could not download published installer ${InstallerUrl}: $($_.Exception.Message)" }
}

# The served installer must be the signed one; WinGet users would otherwise get a
# hash mismatch, or a host swap would go unnoticed until then.
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) { throw "Installer does not exist: $InstallerPath" }
if ((Get-Item -LiteralPath $InstallerPath).Length -ne $setupAssets[0].size) { throw 'Published installer size does not match its signed manifest.' }
$servedSha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($servedSha256 -ne $sha256) { throw "Published installer SHA-256 $servedSha256 does not match the signed manifest's $sha256." }

$manifestDirectory = Join-Path $OutputDirectory "ClypLabs.ClypDat\$version"
New-Item -ItemType Directory -Force $manifestDirectory | Out-Null

$versionManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.10.0.schema.json
PackageIdentifier: ClypLabs.ClypDat
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.10.0
"@

$installerManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.10.0.schema.json
PackageIdentifier: ClypLabs.ClypDat
PackageVersion: $version
InstallerType: nullsoft
InstallModes:
  - interactive
  - silent
  - silentWithProgress
UpgradeBehavior: install
Installers:
  - Architecture: x64
    Scope: user
    InstallerUrl: $InstallerUrl
    InstallerSha256: $sha256
    InstallerSwitches:
      Silent: /S
      SilentWithProgress: /S
ManifestType: installer
ManifestVersion: 1.10.0
"@

$defaultLocaleManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.10.0.schema.json
PackageIdentifier: ClypLabs.ClypDat
PackageVersion: $version
PackageLocale: en-US
Publisher: ClypLabs
PublisherUrl: https://github.com/ClypLabs
PublisherSupportUrl: https://github.com/ClypLabs/ClypDat/issues
PackageName: ClypDat
PackageUrl: https://github.com/ClypLabs/ClypDat
License: GPL-3.0-or-later
LicenseUrl: https://github.com/ClypLabs/ClypDat/blob/master/LICENSE
ShortDescription: Game clipping and recording for Windows.
Description: ClypDat records gameplay and creates clips on Windows.
Moniker: clypdat
Tags:
  - recording
  - clipping
  - gameplay
ReleaseNotesUrl: https://github.com/ClypLabs/ClypDat/releases/tag/$Tag
ManifestType: defaultLocale
ManifestVersion: 1.10.0
"@

[System.IO.File]::WriteAllText((Join-Path $manifestDirectory 'ClypLabs.ClypDat.yaml'), $versionManifest, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText((Join-Path $manifestDirectory 'ClypLabs.ClypDat.installer.yaml'), $installerManifest, [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText((Join-Path $manifestDirectory 'ClypLabs.ClypDat.locale.en-US.yaml'), $defaultLocaleManifest, [System.Text.UTF8Encoding]::new($false))

Write-Verbose "Generated WinGet manifests in $manifestDirectory."

if ($Submit)
{
    if ([string]::IsNullOrWhiteSpace($env:WINGET_CREATE_GITHUB_TOKEN))
    {
        throw 'WINGET_CREATE_GITHUB_TOKEN is not configured.'
    }
    if (-not (Test-Path -LiteralPath $WingetCreatePath -PathType Leaf))
    {
        throw "Pinned WingetCreate executable does not exist: $WingetCreatePath"
    }

    # WingetCreate reads WINGET_CREATE_GITHUB_TOKEN directly. Do not pass it
    # on the command line, where a diagnostic could expose it in a job log.
    & $WingetCreatePath submit --prtitle "New version: ClypDat version $version" --no-open $manifestDirectory
    if ($LASTEXITCODE -ne 0) { throw "WingetCreate submission failed with exit code $LASTEXITCODE." }
}

Write-Output $manifestDirectory
