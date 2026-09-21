[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$eventPrefix = 'ClypDat-Recorder-UpdateShutdownRequest-9F3D2A61-'
$appProductName = 'ClypDat'
$recorderProductName = 'ClypDat Recorder'

# NSIS runs this under 32-bit Windows PowerShell, where Process.MainModule
# throws for every 64-bit process - so ClypDat was never found and its files
# were overwritten while still locked. Win32_Process.ExecutablePath has no
# bitness restriction.
function Get-ProcessPathMap {
    $map = @{}
    try
    {
        foreach ($entry in @(Get-CimInstance Win32_Process -ErrorAction Stop))
        {
            if (-not [string]::IsNullOrWhiteSpace($entry.ExecutablePath)) { $map[[int]$entry.ProcessId] = $entry.ExecutablePath }
        }
    }
    catch { throw "Could not enumerate processes safely: $($_.Exception.Message)" }
    return $map
}

function Get-ProcessPath {
    param([System.Diagnostics.Process] $Process, [hashtable] $PathMap)

    if ($null -ne $PathMap -and $PathMap.ContainsKey([int]$Process.Id)) { return $PathMap[[int]$Process.Id] }
    try { return $Process.MainModule.FileName }
    catch { return $null }
}

function Test-ClypDatExecutable {
    param([string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $fileName = [System.IO.Path]::GetFileName($Path)
    $expectedProductName = switch ($fileName)
    {
        'ClypDat.exe' { $appProductName }
        'ClypDatRecorder.exe' { $recorderProductName }
        default { return $false }
    }

    try
    {
        return [string]::Equals(
            ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)).ProductName,
            $expectedProductName,
            [System.StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

function Get-OwnedProcesses {
    $owned = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
    $pathMap = Get-ProcessPathMap

    foreach ($process in Get-Process -ErrorAction SilentlyContinue)
    {
        $path = Get-ProcessPath $process $pathMap
        if (Test-ClypDatExecutable $path)
        {
            $owned.Add($process)
            continue
        }

        if ($process.ProcessName -notin @('ffmpeg', 'ffprobe')) { continue }
        if ([string]::IsNullOrWhiteSpace($path)) { continue }

        # ClypDat's bundled tools live in <install>\ffmpeg. Only close an
        # ffmpeg/ffprobe whose parent directory has a verified ClypDat.exe
        # sibling. This intentionally leaves every unrelated FFmpeg alone.
        $ffmpegDirectory = [System.IO.Path]::GetDirectoryName($path)
        if (-not [string]::Equals([System.IO.Path]::GetFileName($ffmpegDirectory), 'ffmpeg', [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $installDirectory = [System.IO.Directory]::GetParent($ffmpegDirectory)
        if ($null -eq $installDirectory) { continue }
        $appPath = Join-Path $installDirectory.FullName 'ClypDat.exe'
        if (Test-ClypDatExecutable $appPath) { $owned.Add($process) }
    }

    return @($owned)
}

$signalled = $false
try
{
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $event = [System.Threading.EventWaitHandle]::OpenExisting($eventPrefix + $sid)
    try
    {
        [void]$event.Set()
        $signalled = $true
        Write-Output 'Requested graceful ClypDat shutdown.'
    }
    finally { $event.Dispose() }
}
catch [System.Threading.WaitHandleCannotBeOpenedException]
{
    Write-Output 'No running ClypDat instance exposed update shutdown IPC.'
}

# A signalled ClypDat may hold its exit behind Closing Safely while a save,
# export or the recorder's file drain (up to 5 minutes) finishes. Without the
# IPC listener nothing is coming, so keep the short wait.
$graceSeconds = if ($signalled) { 330 } else { 10 }
$deadline = [DateTime]::UtcNow.AddSeconds($graceSeconds)
while ([DateTime]::UtcNow -lt $deadline)
{
    if ((Get-OwnedProcesses).Count -eq 0) { exit 0 }
    Start-Sleep -Milliseconds 250
}

$remaining = Get-OwnedProcesses
if ($remaining.Count -gt 0)
{
    Write-Output 'Graceful shutdown timed out; stopping verified ClypDat processes.'
    foreach ($process in $remaining)
    {
        try { Stop-Process -Id $process.Id -Force -ErrorAction Stop }
        catch { Write-Output "Could not stop PID $($process.Id): $($_.Exception.Message)" }
    }
    Start-Sleep -Milliseconds 500
}

$locked = Get-OwnedProcesses
if ($locked.Count -gt 0)
{
    $pathMap = Get-ProcessPathMap
    $details = $locked | ForEach-Object {
        $path = Get-ProcessPath $_ $pathMap
        "PID $($_.Id) ($path)"
    }
    Write-Error ("Verified ClypDat processes remain: " + ($details -join '; '))
    exit 1
}

exit 0
