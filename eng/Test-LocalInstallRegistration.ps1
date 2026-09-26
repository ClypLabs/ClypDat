# Tests eng\LocalInstallRegistration.psm1 against a scratch registry key and
# temporary folders - never the real ClypDat registration or shortcuts.
#   ./eng/Test-LocalInstallRegistration.ps1
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LocalInstallRegistration.psm1') -Force

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('ClypDatRegistrationTest-' + [Guid]::NewGuid().ToString('N'))
$registryRoot = 'HKCU:\Software\ClypDatRegistrationTest-' + [Guid]::NewGuid().ToString('N')
$shell = New-Object -ComObject WScript.Shell
$failures = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
}

function New-File([string]$Path) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    Set-Content -LiteralPath $Path -Value 'stub'
}

function New-Shortcut([string]$Path, [string]$Target) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $Target
    $link.WorkingDirectory = Split-Path -Parent $Target
    $link.IconLocation = "$Target,0"
    $link.Arguments = '--minimized'
    $link.Save()
}

function Get-Value([string]$Key, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Key)) { return $null }
    $item = Get-ItemProperty -LiteralPath $Key
    if ($null -eq $item.PSObject.Properties[$Name]) { return $null }
    return $item.$Name
}

# A fresh sandbox per scenario: canonical install, the old installer-test copy
# (with its own uninstaller), a sibling ClypDat-Dev registration, shortcuts.
function New-Scenario([switch]$CanonicalUninstaller, [switch]$Registered, [string]$RegisteredDirectory) {
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
    if (Test-Path -LiteralPath $registryRoot) { Remove-Item -LiteralPath $registryRoot -Recurse -Force }
    $s = [pscustomobject]@{
        Canonical = Join-Path $sandbox 'Programs\ClypDat'
        Old = Join-Path $sandbox 'Temp\ClypDatNsiTest'
        Launch = Join-Path $sandbox 'Start Menu\ClypDat\ClypDat.lnk'
        Desktop = Join-Path $sandbox 'Desktop\ClypDat.lnk'
        Uninstall = Join-Path $sandbox 'Start Menu\ClypDat\Uninstall ClypDat.lnk'
        AppKey = Join-Path $registryRoot 'ClypDat'
        UninstallKey = Join-Path $registryRoot 'Microsoft\Windows\CurrentVersion\Uninstall\ClypDat'
        DevKey = Join-Path $registryRoot 'Microsoft\Windows\CurrentVersion\Uninstall\ClypDat-Dev'
    }
    New-File (Join-Path $s.Canonical 'ClypDat.exe')
    if ($CanonicalUninstaller) { New-File (Join-Path $s.Canonical 'Uninstall.exe') }
    New-File (Join-Path $s.Old 'ClypDat.exe')
    New-File (Join-Path $s.Old 'Uninstall.exe')
    New-Item -Path $s.DevKey -Force | Out-Null
    Set-ItemProperty -LiteralPath $s.DevKey -Name 'InstallLocation' -Value $s.Old
    if ($Registered) {
        $at = if ($RegisteredDirectory) { $RegisteredDirectory } else { $s.Old }
        New-Item -Path $s.AppKey -Force | Out-Null
        Set-ItemProperty -LiteralPath $s.AppKey -Name 'InstallDir' -Value $at
        New-Item -Path $s.UninstallKey -Force | Out-Null
        foreach ($pair in @(('DisplayName', 'ClypDat'), ('DisplayIcon', "$at\ClypDat.exe"), ('DisplayVersion', '1.6.1'), ('Publisher', 'ClypLabs'),
                ('UninstallString', "$at\Uninstall.exe"), ('InstallLocation', $at))) {
            Set-ItemProperty -LiteralPath $s.UninstallKey -Name $pair[0] -Value $pair[1]
        }
        New-Shortcut $s.Launch "$at\ClypDat.exe"
        New-Shortcut $s.Desktop "$at\ClypDat.exe"
        New-Shortcut $s.Uninstall "$at\Uninstall.exe"
    }
    return $s
}

function Invoke-Repair($s, [string]$InstallDirectory = $s.Canonical) {
    Repair-ClypDatInstallRegistration -InstallDirectory $InstallDirectory -CanonicalDirectory $s.Canonical -RegistryRoot $registryRoot `
        -LaunchShortcuts @($s.Launch, $s.Desktop) -UninstallShortcuts @($s.Uninstall)
}

function Test-Scenario([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        Write-Host "PASS  $Name"
    }
    catch {
        $script:failures++
        Write-Host "FAIL  $Name - $($_.Exception.Message)"
    }
}

try {
    Test-Scenario 'stale installer-test registration, no uninstaller here: re-pointed and entry removed' {
        $s = New-Scenario -Registered
        $changes = @(Invoke-Repair $s)
        Assert-True ((Get-Value $s.AppKey 'InstallDir') -eq $s.Canonical) 'InstallDir points at the canonical directory'
        Assert-True (-not (Test-Path -LiteralPath $s.UninstallKey)) 'the Uninstall entry for the other copy is gone'
        foreach ($lnk in $s.Launch, $s.Desktop) {
            $link = $shell.CreateShortcut($lnk)
            Assert-True ($link.TargetPath -eq (Join-Path $s.Canonical 'ClypDat.exe')) "$lnk starts the canonical ClypDat.exe"
            Assert-True ($link.WorkingDirectory -eq $s.Canonical) "$lnk starts in the canonical directory"
            Assert-True ($link.Arguments -eq '--minimized') "$lnk keeps its arguments"
            Assert-True ($link.IconLocation -like "$($s.Canonical)\ClypDat.exe,0") "$lnk takes its icon from the canonical exe"
        }
        Assert-True (-not (Test-Path -LiteralPath $s.Uninstall)) 'the Uninstall shortcut is gone with its entry'
        # The old directory and its files are never touched.
        Assert-True ((Test-Path -LiteralPath (Join-Path $s.Old 'ClypDat.exe')) -and (Test-Path -LiteralPath (Join-Path $s.Old 'Uninstall.exe'))) 'the old directory is untouched'
        # Nor is anything that is not ClypDat's.
        Assert-True ((Get-Value $s.DevKey 'InstallLocation') -eq $s.Old) 'the ClypDat-Dev entry is untouched'
        Assert-True (@($changes | Where-Object { $_.Before -like "$($s.Old)*" }).Count -eq 7) 'every change is reported with its old value'
    }

    Test-Scenario 'stale registration, uninstaller here: entry re-pointed and kept' {
        $s = New-Scenario -Registered -CanonicalUninstaller
        Invoke-Repair $s | Out-Null
        Assert-True ((Get-Value $s.UninstallKey 'InstallLocation') -eq $s.Canonical) 'InstallLocation'
        Assert-True ((Get-Value $s.UninstallKey 'DisplayIcon') -eq (Join-Path $s.Canonical 'ClypDat.exe')) 'DisplayIcon'
        Assert-True ((Get-Value $s.UninstallKey 'UninstallString') -eq (Join-Path $s.Canonical 'Uninstall.exe')) 'UninstallString'
        Assert-True ((Get-Value $s.UninstallKey 'DisplayVersion') -eq '1.6.1' -and (Get-Value $s.UninstallKey 'Publisher') -eq 'ClypLabs') 'other values kept'
        Assert-True ($shell.CreateShortcut($s.Uninstall).TargetPath -eq (Join-Path $s.Canonical 'Uninstall.exe')) 'Uninstall shortcut re-targeted'
    }

    Test-Scenario 'registration already canonical and valid: nothing changes' {
        $s = New-Scenario -Registered -CanonicalUninstaller -RegisteredDirectory (Join-Path $sandbox 'Programs\ClypDat')
        $changes = @(Invoke-Repair $s)
        Assert-True ($changes.Count -eq 0) "no changes (got $($changes.Count))"
    }

    Test-Scenario 'canonical entry whose uninstaller a local publish replaced: removed, not left broken' {
        $s = New-Scenario -Registered -RegisteredDirectory (Join-Path $sandbox 'Programs\ClypDat')
        Invoke-Repair $s | Out-Null
        Assert-True ((Get-Value $s.AppKey 'InstallDir') -eq $s.Canonical) 'InstallDir kept'
        Assert-True (-not (Test-Path -LiteralPath $s.UninstallKey)) 'an Uninstall entry naming a missing Uninstall.exe is removed'
    }

    Test-Scenario 'nothing registered: nothing created' {
        $s = New-Scenario
        $changes = @(Invoke-Repair $s)
        Assert-True ($changes.Count -eq 0) 'no changes'
        Assert-True (-not (Test-Path -LiteralPath $s.AppKey) -and -not (Test-Path -LiteralPath $s.UninstallKey)) 'no keys created'
    }

    Test-Scenario 'refuses any directory but the canonical one' {
        $s = New-Scenario -Registered
        $refused = $false
        try { Invoke-Repair $s -InstallDirectory $s.Old | Out-Null } catch { $refused = $_.Exception.Message -like 'Refusing to register*' }
        Assert-True $refused 'a non-canonical directory is refused'
        Assert-True ((Get-Value $s.AppKey 'InstallDir') -eq $s.Old) 'and nothing was changed'
    }

    Test-Scenario 'refuses a canonical directory with no ClypDat.exe' {
        $s = New-Scenario -Registered
        Remove-Item -LiteralPath (Join-Path $s.Canonical 'ClypDat.exe')
        $refused = $false
        try { Invoke-Repair $s | Out-Null } catch { $refused = $_.Exception.Message -like '*has no ClypDat.exe*' }
        Assert-True $refused 'refused'
        Assert-True (Test-Path -LiteralPath $s.UninstallKey) 'and nothing was changed'
    }

    Test-Scenario 'canonical-path comparison' {
        Assert-True (Test-ClypDatSameDirectory 'C:\Users\A\AppData\Local\Programs\ClypDat\' 'c:\users\a\appdata\local\programs\clypdat') 'case and trailing slash'
        Assert-True (Test-ClypDatSameDirectory '"C:\Programs\ClypDat"' 'C:\Programs\ClypDat') 'quotes'
        Assert-True (-not (Test-ClypDatSameDirectory 'C:\Programs\ClypDat-Dev' 'C:\Programs\ClypDat')) 'a sibling directory'
        Assert-True (-not (Test-ClypDatSameDirectory '' 'C:\Programs\ClypDat')) 'empty'
        Assert-True ((Get-ClypDatValuePath '"C:\Temp\ClypDatNsiTest\Uninstall.exe" /S') -eq 'C:\Temp\ClypDatNsiTest\Uninstall.exe') 'quoted UninstallString'
        Assert-True ((Get-ClypDatValuePath 'C:\Temp\ClypDatNsiTest\ClypDat.exe,0') -eq 'C:\Temp\ClypDatNsiTest\ClypDat.exe') 'DisplayIcon index'
        Assert-True ((Get-ClypDatCanonicalInstallDirectory) -eq [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\ClypDat'))) 'canonical directory'
    }
}
finally {
    if (Test-Path -LiteralPath $registryRoot) { Remove-Item -LiteralPath $registryRoot -Recurse -Force }
    if (Test-Path -LiteralPath $sandbox) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
}

if ($failures -gt 0) { throw "$failures install registration test(s) failed." }
Write-Host 'Install registration tests passed.'
