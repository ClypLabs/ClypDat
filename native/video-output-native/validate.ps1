[CmdletBinding()]
param([switch]$Benchmark)
$ErrorActionPreference = 'Stop'
$repository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ffmpeg = Join-Path $repository 'native/vendor/ffmpeg/ffmpeg.exe'
$fixtures = Join-Path $PSScriptRoot 'build/fixtures'
New-Item -ItemType Directory -Force $fixtures | Out-Null
& "$PSScriptRoot/build-video-output.ps1" -Configuration Release
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
& "$PSScriptRoot/build/Release/compositor_tests.exe"
if ($LASTEXITCODE -ne 0) { throw 'GPU composition tests failed.' }
$vlcRoot = "$env:USERPROFILE/.nuget/packages/videolan.libvlc.windows/3.0.23.1/build/x64"
$env:PATH = "$vlcRoot;" + $env:PATH
$env:VLC_PLUGIN_PATH = "$PSScriptRoot/build/Release"
$cases = @(
    @{ Name='24'; Rate='24'; Size='640x360' },
    @{ Name='29_97'; Rate='30000/1001'; Size='640x360' },
    @{ Name='30'; Rate='30'; Size='640x360' },
    @{ Name='59_94'; Rate='60000/1001'; Size='640x360' },
    @{ Name='60'; Rate='60'; Size='640x360' },
    @{ Name='120'; Rate='120'; Size='640x360' },
    @{ Name='vfr'; Rate='120'; Size='640x360' }
)
if ($Benchmark) {
    $cases += @(
        @{ Name='1080p60'; Rate='60'; Size='1920x1080' },
        @{ Name='1440p60'; Rate='60'; Size='2560x1440' },
        @{ Name='4k60'; Rate='60'; Size='3840x2160' }
    )
}
foreach ($case in $cases) {
    $path = Join-Path $fixtures "$($case.Name).mp4"
    if (-not (Test-Path -LiteralPath $path)) {
        $arguments = @('-hide_banner','-loglevel','error','-f','lavfi','-i',"testsrc2=size=$($case.Size):rate=$($case.Rate):duration=12")
        if ($case.Name -eq 'vfr') { $arguments += @('-vf',"select='not(mod(n,2))+not(mod(n,5))'",'-fps_mode','vfr') }
        $arguments += @('-c:v','libx264','-preset','ultrafast','-g','30','-threads','4','-y',$path)
        & $ffmpeg @arguments
        if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed: $($case.Name)." }
    }
}
$results = @()
foreach ($case in $cases) {
    $path = Join-Path $fixtures "$($case.Name).mp4"
    $modes = if ($case.Size -eq '640x360') { @('transport') } else { @('baseline','compositor') }
    foreach ($mode in $modes) {
        $log = Join-Path $fixtures "$($case.Name)-$mode.log"
        $measurement = if ($mode -eq 'transport') { 2 } else { 5 }
        & "$PSScriptRoot/build/Release/vlc_tests.exe" $path $mode $measurement *> $log
        $exitCode = $LASTEXITCODE
        $line = Get-Content $log | Where-Object { $_ -like 'RESULT *' } | Select-Object -Last 1
        Write-Output "$($case.Name): $line"
        $results += [pscustomobject]@{ Clip=$case.Name; Mode=$mode; ExitCode=$exitCode; Result=$line }
        if ($mode -ne 'baseline') {
            $size = $case.Size.Split('x')
            if ($line -notmatch "width=$($size[0]) height=$($size[1]) ") { throw "Original dimensions lost: $($case.Name)." }
            if ($case.Name -eq '120' -and ($line -notmatch 'displayed=(\d+)' -or [int]$Matches[1] -le 180)) { throw '120 fps playback was capped or stalled.' }
        }
        if ($exitCode -ne 0) { throw "VLC integration failed: $($case.Name), $mode. See $log" }
    }
}
$softwareLog = Join-Path $fixtures 'software.log'
& "$PSScriptRoot/build/Release/vlc_tests.exe" (Join-Path $fixtures '60.mp4') software 2 *> $softwareLog
if ($LASTEXITCODE -ne 0) { throw "Software decode failed. See $softwareLog" }
Get-Content $softwareLog | Where-Object { $_ -like 'RESULT *' }
$results | ConvertTo-Json | Set-Content (Join-Path $fixtures 'results.json')
if ($Benchmark) {
    foreach ($name in '1080p60','1440p60','4k60') {
        $rates = @{}
        foreach ($row in $results | Where-Object Clip -eq $name) {
            if ($row.Result -notmatch 'displayed=(\d+) lost=(\d+) hardware=(\d+)') { throw "Missing counters for $name." }
            if ($Matches[3] -ne '1') { throw "Hardware decode missing for $name." }
            $total = [double]$Matches[1] + [double]$Matches[2]
            $rates[$row.Mode] = if ($total -gt 0) { 100 * [double]$Matches[2] / $total } else { 100 }
        }
        $delta = $rates.compositor - $rates.baseline
        if ($delta -gt 1) { throw "Drop-rate regression: $name, $delta percentage points." }
        Write-Output "$name additional dropped frames: $delta percentage points."
    }
}
