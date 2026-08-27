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

    [switch] $Submit,

    [string] $WingetCreatePath = (Join-Path $PSScriptRoot '..\wingetcreate.exe')
)

$ErrorActionPreference = 'Stop'
$version = $Tag.Substring(1)

if ([string]::IsNullOrWhiteSpace($InstallerPath))
{
    $InstallerPath = Join-Path ([System.IO.Path]::GetTempPath()) "ClypDat-Setup-$version.exe"
    try { Invoke-WebRequest -Uri $InstallerUrl -OutFile $InstallerPath }
    catch { throw "Could not download published installer ${InstallerUrl}: $($_.Exception.Message)" }
}

if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) { throw "Installer does not exist: $InstallerPath" }
$sha256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()

$manifestDirectory = Join-Path $OutputDirectory "ClypLabs.ClypDat\$version"
New-Item -ItemType Directory -Force $manifestDirectory | Out-Null

$versionManifest = @"
PackageIdentifier: ClypLabs.ClypDat
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.10.0
"@

$installerManifest = @"
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
