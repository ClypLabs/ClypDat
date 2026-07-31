[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$globalJsonPath = Join-Path $repoRoot 'global.json'
$globalJson = Get-Content -Raw $globalJsonPath | ConvertFrom-Json
$sdkVersion = $globalJson.sdk.version
$installDirectory = Join-Path $repoRoot '.dotnet'
$dotnetExecutable = Join-Path $installDirectory 'dotnet.exe'
$sdkDirectory = Join-Path $installDirectory "sdk\\$sdkVersion"

if (-not (Test-Path -LiteralPath $dotnetExecutable) -or -not (Test-Path -LiteralPath $sdkDirectory)) {
    Write-Host "Installing .NET SDK $sdkVersion into $installDirectory"
    New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null

    $installerPath = Join-Path ([IO.Path]::GetTempPath()) "clypdat-dotnet-install-$sdkVersion.ps1"
    try {
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath
        & "$env:SystemRoot\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" `
            -NoProfile -ExecutionPolicy Bypass -File $installerPath `
            -Version $sdkVersion -InstallDir $installDirectory -Architecture x64 -NoPath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw ".NET SDK installation failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Remove-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $dotnetExecutable) -or -not (Test-Path -LiteralPath $sdkDirectory)) {
    throw "Repo-local .NET SDK $sdkVersion was not installed successfully."
}

Write-Output $dotnetExecutable
