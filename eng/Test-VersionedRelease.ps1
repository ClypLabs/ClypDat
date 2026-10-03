$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot ('.release-fixture-' + [guid]::NewGuid().ToString('N'))
$rsa = [Security.Cryptography.RSA]::Create(2048)
try {
    New-Item -ItemType Directory -Path $fixture | Out-Null
    foreach ($tag in @('v1.2.3', 'v1.2.3-rc.1')) {
        $names = @("ClypDat-$tag-Setup.exe", "ClypDat-$tag.msi", "ClypDat-$tag-Portable.exe", "ClypDat-$tag-win-x64.zip", 'ClypDat-Setup.exe', 'clypdat-ffmpeg-8.1.2-win64-shared-r3-sources.zip')
        foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $fixture $name), $(if ($name -like '*Setup.exe') { 'same setup bytes' } else { $name })) }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ffmpeg/artifacts/clypdat-ffmpeg-8.1.2-win64-shared-r3-sources.zip') -Destination $fixture -Force
        $key = Join-Path $fixture 'fixture.pem'
        [IO.File]::WriteAllText($key, $rsa.ExportPkcs8PrivateKeyPem())
        & (Join-Path $PSScriptRoot 'Sign-ReleaseManifest.ps1') -Tag $tag -ArtifactDirectory $fixture -PrivateKeyPath $key
        $bytes = [IO.File]::ReadAllBytes((Join-Path $fixture 'ClypDat-Release.manifest.json'))
        $manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        $signature = [Convert]::FromBase64String([IO.File]::ReadAllText((Join-Path $fixture 'ClypDat-Release.manifest.sig')))
        if (-not $rsa.VerifyData($bytes, $signature, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'Invalid fixture signature' }
        if ($manifest.tag -cne $tag -or $manifest.version -cne $tag.Substring(1) -or $manifest.assets.Count -ne 6) { throw 'Wrong signed release identity or asset count' }
        foreach ($name in $names) {
            $entry = @($manifest.assets | Where-Object name -CEQ $name)
            if ($entry.Count -ne 1 -or $entry[0].sha256 -cne (Get-FileHash -LiteralPath (Join-Path $fixture $name)).Hash.ToLowerInvariant()) { throw "Wrong signed coverage: $name" }
        }
        function Assert-Rejected([scriptblock]$Action) {
            $rejected = $false
            try { & $Action | Out-Null } catch { $rejected = $true }
            if (-not $rejected) { throw 'Expected fixture rejection' }
        }
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Tag 'v1.2.4' -ArtifactDirectory $fixture }
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Tag "v1.2.3`n" -ArtifactDirectory $fixture }
        [IO.File]::WriteAllText((Join-Path $fixture 'ClypDat-Setup.exe'), 'different bytes')
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Sign-ReleaseManifest.ps1') -Tag $tag -ArtifactDirectory $fixture -PrivateKeyPath $key }
        Copy-Item -LiteralPath (Join-Path $fixture "ClypDat-$tag-Setup.exe") -Destination (Join-Path $fixture 'ClypDat-Setup.exe') -Force
        $source = Join-Path $fixture 'clypdat-ffmpeg-8.1.2-win64-shared-r3-sources.zip'
        [IO.File]::Delete($source)
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Sign-ReleaseManifest.ps1') -Tag $tag -ArtifactDirectory $fixture -PrivateKeyPath $key }
        [IO.File]::WriteAllText($source, '')
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -Tag $tag -ArtifactDirectory $fixture }
        [IO.File]::WriteAllText($source, 'wrong source bytes')
        Assert-Rejected { & (Join-Path $PSScriptRoot 'Sign-ReleaseManifest.ps1') -Tag $tag -ArtifactDirectory $fixture -PrivateKeyPath $key }
        Write-Host "Versioned release fixtures passed: $tag."
    }
}
finally {
    $rsa.Dispose()
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar)) { throw 'Fixture cleanup escaped eng directory' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
