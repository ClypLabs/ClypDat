# ClypDat's per-user install registration, kept pointing at the directory
# build.ps1 publishes to and launches from.
#
# The NSIS installer takes its install location from
# HKCU\Software\ClypDat\InstallDir - release updates included - and Windows
# lists, uninstalls and launches ClypDat through the Uninstall key and the
# Start menu and desktop shortcuts. Left pointing at another copy (an old
# installer test under %TEMP%), updates install there and the shortcuts start
# that copy instead of the one just published.
#
# Only ClypDat's own values and shortcuts are changed. The directory they used
# to point at is never deleted or modified.

function Get-ClypDatCanonicalInstallDirectory {
    # Where the official installer and build.ps1 both install.
    [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\ClypDat')).TrimEnd('\')
}

function Test-ClypDatSameDirectory {
    param([string]$Left, [string]$Right)
    if ([string]::IsNullOrWhiteSpace($Left) -or [string]::IsNullOrWhiteSpace($Right)) { return $false }
    try {
        $a = [IO.Path]::GetFullPath($Left.Trim().Trim('"')).TrimEnd('\', '/')
        $b = [IO.Path]::GetFullPath($Right.Trim().Trim('"')).TrimEnd('\', '/')
        return $a.Equals($b, [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

# The file a registry value names: a quoted or bare path, and a DisplayIcon's
# ",index" suffix dropped.
function Get-ClypDatValuePath {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
    $path = $Value.Trim()
    if ($path.StartsWith('"')) {
        $end = $path.IndexOf('"', 1)
        if ($end -lt 0) { return $null }
        $path = $path.Substring(1, $end - 1)
    }
    elseif ($path -match '^(.+\.exe),-?\d+$') {
        $path = $Matches[1]
    }
    try { return [IO.Path]::GetFullPath($path) } catch { return $null }
}

function Get-ClypDatRegistryValue {
    param([string]$Key, [string]$Name)
    if (-not (Test-Path -LiteralPath $Key)) { return $null }
    $item = Get-ItemProperty -LiteralPath $Key -ErrorAction SilentlyContinue
    if ($null -eq $item -or $null -eq $item.PSObject.Properties[$Name]) { return $null }
    return [string]$item.$Name
}

<#
Points ClypDat's per-user registration at $InstallDirectory, which must be the
canonical install directory and contain ClypDat.exe. Returns what changed:
  - HKCU\Software\ClypDat\InstallDir: re-pointed when it names another
    directory; left absent when absent (the installer defaults to the
    canonical directory anyway).
  - The Uninstall\ClypDat entry: kept, and re-pointed where needed, only when
    this directory has the Uninstall.exe it would run. Otherwise it is removed:
    pointed at this directory it would be an uninstall that cannot run, and
    left alone it keeps Windows listing another copy as the installed ClypDat.
  - The ClypDat Start menu and desktop shortcuts: re-targeted when they start a
    ClypDat.exe elsewhere; the Uninstall shortcut follows the Uninstall entry.
#>
function Repair-ClypDatInstallRegistration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$InstallDirectory,
        [string]$CanonicalDirectory = (Get-ClypDatCanonicalInstallDirectory),
        [string]$RegistryRoot = 'HKCU:\Software',
        [string[]]$LaunchShortcuts = @(
            (Join-Path ([Environment]::GetFolderPath('Programs')) 'ClypDat\ClypDat.lnk'),
            (Join-Path ([Environment]::GetFolderPath('Desktop')) 'ClypDat.lnk')),
        [string[]]$UninstallShortcuts = @(
            (Join-Path ([Environment]::GetFolderPath('Programs')) 'ClypDat\Uninstall ClypDat.lnk'))
    )

    if (-not (Test-ClypDatSameDirectory $InstallDirectory $CanonicalDirectory)) {
        throw "Refusing to register '$InstallDirectory': ClypDat's install directory is '$CanonicalDirectory'."
    }
    $directory = [IO.Path]::GetFullPath($CanonicalDirectory).TrimEnd('\')
    $executable = Join-Path $directory 'ClypDat.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Refusing to register '$directory': it has no ClypDat.exe."
    }
    $uninstaller = Join-Path $directory 'Uninstall.exe'
    $hasUninstaller = Test-Path -LiteralPath $uninstaller -PathType Leaf
    $changes = New-Object 'System.Collections.Generic.List[object]'
    $record = { param($Item, $Before, $After) $changes.Add([pscustomobject]@{ Item = $Item; Before = $Before; After = $After }) }

    $appKey = Join-Path $RegistryRoot 'ClypDat'
    $installDir = Get-ClypDatRegistryValue $appKey 'InstallDir'
    if ($null -ne $installDir -and -not (Test-ClypDatSameDirectory $installDir $directory)) {
        Set-ItemProperty -LiteralPath $appKey -Name 'InstallDir' -Value $directory
        & $record "$appKey\InstallDir" $installDir $directory
    }

    $uninstallKey = Join-Path $RegistryRoot 'Microsoft\Windows\CurrentVersion\Uninstall\ClypDat'
    if (Test-Path -LiteralPath $uninstallKey) {
        $wanted = [ordered]@{ InstallLocation = $directory; DisplayIcon = $executable; UninstallString = $uninstaller }
        if ($hasUninstaller) {
            foreach ($name in $wanted.Keys) {
                $current = Get-ClypDatRegistryValue $uninstallKey $name
                $currentPath = if ($name -eq 'InstallLocation') { $current } else { Get-ClypDatValuePath $current }
                $pointsHere = if ($name -eq 'InstallLocation') { Test-ClypDatSameDirectory $currentPath $directory }
                    else { $null -ne $currentPath -and $currentPath.Equals($wanted[$name], [StringComparison]::OrdinalIgnoreCase) }
                if (-not $pointsHere) {
                    Set-ItemProperty -LiteralPath $uninstallKey -Name $name -Value $wanted[$name]
                    & $record "$uninstallKey\$name" $current $wanted[$name]
                }
            }
        }
        else {
            foreach ($name in $wanted.Keys) {
                $current = Get-ClypDatRegistryValue $uninstallKey $name
                if ($null -ne $current) { & $record "$uninstallKey\$name" $current '(entry removed)' }
            }
            Remove-Item -LiteralPath $uninstallKey -Recurse -Force
        }
    }

    $shell = $null
    foreach ($shortcut in @($LaunchShortcuts) + @($UninstallShortcuts)) {
        if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) { continue }
        if ($null -eq $shell) { $shell = New-Object -ComObject WScript.Shell }
        $link = $shell.CreateShortcut($shortcut)
        $target = $link.TargetPath
        $uninstall = $UninstallShortcuts -contains $shortcut
        $expected = if ($uninstall) { $uninstaller } else { $executable }
        # Only shortcuts that start ClypDat.exe, or its Uninstall.exe.
        if ([string]::IsNullOrWhiteSpace($target) -or
            -not [IO.Path]::GetFileName($target).Equals([IO.Path]::GetFileName($expected), [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($uninstall -and -not $hasUninstaller) {
            # Nothing here for it to run; its Uninstall entry is gone too.
            Remove-Item -LiteralPath $shortcut -Force
            & $record $shortcut $target '(shortcut removed)'
            continue
        }
        if ($target.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $link.TargetPath = $expected
        $link.WorkingDirectory = $directory
        if ($link.IconLocation.StartsWith($target, [StringComparison]::OrdinalIgnoreCase)) { $link.IconLocation = "$expected,0" }
        $link.Save()
        & $record $shortcut $target $expected
    }

    return $changes.ToArray()
}

Export-ModuleMember -Function Get-ClypDatCanonicalInstallDirectory, Test-ClypDatSameDirectory, Get-ClypDatValuePath, Repair-ClypDatInstallRegistration
