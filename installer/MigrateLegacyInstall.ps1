param()

$ErrorActionPreference = 'Stop'

function Stop-LegacyProcess([string] $installDirectory) {
    if ([string]::IsNullOrWhiteSpace($installDirectory) -or -not (Test-Path -LiteralPath $installDirectory)) { return }
    $prefix = ([IO.Path]::GetFullPath($installDirectory).TrimEnd('\') + '\')
    Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'ClypDat.exe' -and $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
    }
}

$legacyInstallDirectory = (Get-ItemProperty -Path 'HKCU:\Software\ClypDat' -Name InstallDir -ErrorAction SilentlyContinue).InstallDir
if ($legacyInstallDirectory) {
    Stop-LegacyProcess $legacyInstallDirectory
    $legacyUninstaller = Join-Path $legacyInstallDirectory 'Uninstall.exe'
    if (Test-Path -LiteralPath $legacyUninstaller) {
        $legacy = Start-Process -FilePath $legacyUninstaller -ArgumentList '/S' -Wait -PassThru
        if ($legacy.ExitCode -ne 0) { throw "Legacy NSIS uninstaller failed with exit code $($legacy.ExitCode)." }
    }
}

# Old MSI releases were per-user. Query only the current user's uninstall
# registry, then remove every Windows Installer product registered as ClypDat.
$uninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
Get-ChildItem -Path $uninstallRoot -ErrorAction SilentlyContinue | ForEach-Object {
    $entry = Get-ItemProperty -Path $_.PSPath
    if ($entry.DisplayName -ne 'ClypDat' -or $entry.WindowsInstaller -ne 1) { return }
    $productCode = $_.PSChildName
    if ($productCode -notmatch '^\{[0-9A-Fa-f-]+\}$') { throw "Invalid legacy MSI product code '$productCode'." }
    $legacy = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList "/x $productCode /qn /norestart" -Wait -PassThru
    if ($legacy.ExitCode -notin 0, 3010, 1605) { throw "Legacy MSI uninstaller failed with exit code $($legacy.ExitCode)." }
}
