#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Clip,
    [ValidateRange(1, 1200)][int]$ReplaySeconds = 120,
    [string]$FfmpegDirectory = (Join-Path $PSScriptRoot '../native/vendor/ffmpeg'),
    [switch]$SkipDecode
)
$ErrorActionPreference = 'Stop'
$clipPath = (Resolve-Path -LiteralPath $Clip).Path
$probe = Join-Path $FfmpegDirectory 'ffprobe.exe'
$ffmpeg = Join-Path $FfmpegDirectory 'ffmpeg.exe'
$raw = & $probe -v error -show_entries 'format=duration:stream=index,codec_name,codec_type,start_time,duration,nal_length_size:packet=stream_index,pts_time,dts_time,duration_time,flags,pos,size' -of json $clipPath
if ($LASTEXITCODE -ne 0) { throw 'ffprobe failed.' }
$media = ($raw -join "`n") | ConvertFrom-Json
$video = @($media.streams | Where-Object codec_type -eq video)[0]
$packets = @($media.packets | Where-Object stream_index -eq $video.index)
$keys = @($packets | Where-Object flags -Match K)
if (!$packets.Count -or !$keys.Count -or $packets[0].flags -notmatch 'K') { throw 'Clip does not start on a keyframe.' }
$duration = [double]$video.duration
$maximumGap = 0.0
$lastKey = [double]$keys[0].pts_time
foreach ($key in $keys | Select-Object -Skip 1) {
    $maximumGap = [Math]::Max($maximumGap, [double]$key.pts_time - $lastKey)
    $lastKey = [double]$key.pts_time
}
$maximumGap = [Math]::Max($maximumGap, $duration - $lastKey)
$summary = [ordered]@{ Clip = $clipPath; Codec = $video.codec_name; RequestedSeconds = $ReplaySeconds; DurationSeconds = $duration; Frames = $packets.Count; Keyframes = $keys.Count; MaximumKeyframeGapSeconds = $maximumGap }
if ($duration -gt $ReplaySeconds + 1.1) { $summary | ConvertTo-Json; throw 'Replay duration exceeds requested duration plus 1.1-second keyframe allowance.' }
if ($maximumGap -gt 1.1) { $summary | ConvertTo-Json; throw 'Keyframe gap exceeds 1.1 seconds.' }
$lastDts = [double]::NegativeInfinity
foreach ($packet in $packets) {
    if ([double]$packet.dts_time -le $lastDts) { throw 'Non-increasing video DTS.' }
    $lastDts = [double]$packet.dts_time
}
if ($video.codec_name -eq 'h264') {
    $nalSize = [int]$video.nal_length_size
    if ($nalSize -lt 1 -or $nalSize -gt 4) { throw 'Unsupported H.264 packet format; expected length-prefixed MP4.' }
    $file = [IO.File]::OpenRead($clipPath)
    try {
        foreach ($key in $keys) {
            $file.Position = [long]$key.pos
            $end = $file.Position + [long]$key.size
            $idr = $false
            while ($file.Position + $nalSize -lt $end) {
                $length = 0L
                for ($i = 0; $i -lt $nalSize; $i++) { $length = ($length -shl 8) -bor $file.ReadByte() }
                if ($length -lt 1 -or $file.Position + $length -gt $end) { throw 'Invalid H.264 NAL length.' }
                $type = $file.ReadByte() -band 31
                if ($type -eq 5) { $idr = $true }
                $file.Position += $length - 1
            }
            if (!$idr) { throw "Key packet at $($key.pts_time) is not an IDR." }
        }
    } finally { $file.Dispose() }
    $summary.VerifiedIdrPackets = $keys.Count
}
foreach ($track in $media.streams | Where-Object codec_type -eq audio) {
    if ([Math]::Abs([double]$track.start_time - [double]$video.start_time) -gt 0.05 -or [Math]::Abs([double]$track.duration - $duration) -gt 0.05) {
        throw "Audio stream $($track.index) timestamps do not align with video within 50 ms."
    }
}
function Measure-Decode([string[]]$Arguments, [int]$TimeoutSeconds) {
    $start = [Diagnostics.ProcessStartInfo]::new($ffmpeg)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-hide_banner', '-nostdin', '-v', 'error', '-xerror', '-threads', '4') + $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (!$process.Start()) { throw 'Could not start FFmpeg.' }
        $errors = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill($true); $process.WaitForExit(); throw "Decode exceeded $TimeoutSeconds seconds." }
        $message = $errors.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0 -or $message.Trim().Length) { throw "Decode failed: $message" }
        return $clock.Elapsed.TotalSeconds
    } finally { $process.Dispose() }
}
if (!$SkipDecode) {
    $summary.StartDecodeSeconds = Measure-Decode @('-i', $clipPath, '-t', '1', '-map', '0:v:0', '-an', '-f', 'null', 'NUL') 40
    $nearEnd = [Math]::Max(0, $duration - 5).ToString([Globalization.CultureInfo]::InvariantCulture)
    $summary.EndSeekDecodeSeconds = Measure-Decode @('-ss', $nearEnd, '-i', $clipPath, '-t', '1', '-map', '0:v:0', '-an', '-f', 'null', 'NUL') 40
    $summary.FullDecodeSeconds = Measure-Decode @('-err_detect', 'explode', '-i', $clipPath, '-map', '0', '-f', 'null', 'NUL') 600
}
$summary.Result = 'PASS (listen to tracks and inspect recorder restart diagnostics separately)'
$summary | ConvertTo-Json
