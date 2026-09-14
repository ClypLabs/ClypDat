using System.Globalization;
using System.Text.Json;
using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal sealed record LinuxClipInterval(double Start, double End)
{
    internal double Duration => End - Start;
    internal static LinuxClipInterval FromRequest(double recorderNow, DateTime utcNow, int duration, ReplayClipWindow? request)
    {
        var start = request is null ? Math.Max(0, recorderNow - duration) : recorderNow + (request.StartUtc - utcNow).TotalSeconds;
        var end = request is null ? recorderNow : recorderNow + (request.EndUtc - utcNow).TotalSeconds;
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > recorderNow + 0.01)
            throw new ArgumentException("Requested clip interval is outside the recording timeline.");
        return new(start, end);
    }
    internal LinuxClipInterval InStaging(double retainedStart, double retainedEnd, double frameDuration)
    {
        if (!double.IsFinite(retainedStart) || !double.IsFinite(retainedEnd) || Start < retainedStart - frameDuration || End > retainedEnd + frameDuration || retainedEnd <= retainedStart)
            throw new InvalidOperationException("Requested clip interval is no longer available; staging was retained.");
        return new(Math.Max(0, Start - retainedStart), Math.Min(End, retainedEnd) - retainedStart);
    }
}

internal sealed record LinuxAudioTrackPlan(string Title, string RecorderInput, int Gain, bool Microphone = false)
{
    private static string Identity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('|')) throw new ArgumentException("Invalid PipeWire audio identity.");
        return value;
    }
    internal static IReadOnlyList<LinuxAudioTrackPlan> Create(ReplayBufferConfig config)
    {
        var chat = config.ChatAudioProcessNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var additional = (config.AdditionalAudioProcesses ?? new Dictionary<string, int>())
            .Where(p => !chat.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var excluded = config.GameAudioExcludedProcesses.Concat(chat).Concat(additional.Select(p => p.Key))
            .Append("ClypDat").Append("ClypDatRecorder").Distinct(StringComparer.OrdinalIgnoreCase);
        List<LinuxAudioTrackPlan> tracks = [new("Game", "name:Game|" + string.Join('|', excluded.Select(p => "app-inverse:exe:" + Identity(p))), config.GameAudioVolumePercent)];
        if (chat.Length > 0) tracks.Add(new("Chat", "name:Chat|" + string.Join('|', chat.Select(p => "app:exe:" + Identity(p))), 100));
        foreach (var p in additional) tracks.Add(new(p.Key, $"name:{Identity(p.Key)}|app:exe:{Identity(p.Key)}", p.Value));
        var index = 0;
        foreach (var mic in config.MicrophoneDeviceIds.Distinct(StringComparer.Ordinal))
        {
            var device = string.Equals(mic, "default", StringComparison.OrdinalIgnoreCase) ? "default_input" : Identity(mic);
            tracks.Add(new($"Microphone {++index}", $"name:Microphone {index}|device:{device}", config.MicrophoneVolumePercent, true));
        }
        return tracks;
    }
}

internal static class LinuxClipFinalizer
{
    private static string Number(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    internal static IReadOnlyList<string> Arguments(string staging, string pending, LinuxClipInterval? interval, ReplayBufferConfig config, bool session, bool copyExactInterval = false)
    {
        var codec = session ? config.FullSessionVideoCodec : config.VideoCodec;
        var reencode = (interval is not null && !copyExactInterval) || !string.Equals(codec, config.VideoCodec, StringComparison.Ordinal);
        List<string> args = ["-nostdin", "-v", "error", "-n", "-i", staging];
        // Output seeking decodes from the retained keyframe before trimming.
        if (interval is not null) args.AddRange(["-ss", Number(interval.Start), "-t", Number(interval.Duration)]);
        args.AddRange(["-map", "0:v:0", "-map", "0:a?", "-map_metadata", "0", "-c:v", reencode ? VideoEncoder(codec, config.EncoderMode) : "copy"]);
        if (reencode) args.AddRange(["-b:v", config.BitrateMbps + "M", "-pix_fmt", "yuv420p"]);
        args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
        var tracks = LinuxAudioTrackPlan.Create(config);
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            var filter = "volume=" + Number(Math.Clamp(track.Gain, 0, 200) / 100.0);
            if (track.Microphone && config.MicrophoneChannelMode == "Mono") filter += ",aformat=channel_layouts=mono";
            args.AddRange([$"-filter:a:{index}", filter, $"-metadata:s:a:{index}", "title=" + track.Title]);
        }
        args.AddRange(["-metadata", "comment=" + ClipMetadataTagger.BuildCommentValue(session ? "KDE Full Session" : "KDE / GSR"), "-f", "matroska", pending]);
        return args;
    }
    internal static async Task<bool> CanCopyIntervalAsync(string source, LinuxClipInterval interval, string codec, CancellationToken token)
    {
        // Require a keyframe start and an exact packet end with no reordered frames.
        // Probe small windows around each boundary rather than loading the whole session.
        using var probe = JsonDocument.Parse(await LinuxMediaProcess.RunAsync(FfmpegPathResolver.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-read_intervals", Number(interval.Start) + "%+2," + Number(interval.End) + "%+2",
             "-show_entries", "packet=pts_time,duration_time,flags:stream=has_b_frames,codec_name", "-of", "json", source], token));
        var root = probe.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() != 1 ||
            streams[0].GetProperty("codec_name").GetString() != LinuxReplayBuffer.RecorderCodec(codec) ||
            !streams[0].TryGetProperty("has_b_frames", out var bFrames) || bFrames.GetInt32() != 0 ||
            !root.TryGetProperty("packets", out var packets)) return false;
        var start = false; var end = false;
        foreach (var packet in packets.EnumerateArray()) {
            if (!packet.TryGetProperty("pts_time", out var timestamp) || !double.TryParse(timestamp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pts)) continue;
            if (Math.Abs(pts - interval.Start) < .000001 && packet.TryGetProperty("flags", out var flags) && flags.GetString()!.Contains('K')) start = true;
            if (Math.Abs(pts - interval.End) < .000001) end = true;
            if (packet.TryGetProperty("duration_time", out var duration) && double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && Math.Abs(pts + d - interval.End) < .000001) end = true;
        }
        return start && end;
    }
    private static string VideoEncoder(string codec, string mode)
    {
        if (mode.Equals("CPU", StringComparison.OrdinalIgnoreCase)) return SoftwareCodec(codec);
        var family = codec == "AV1" ? ExportEncoderProbe.Av1Family : ExportEncoderProbe.Family;
        if (family is null) throw new NotSupportedException("GPU clip conversion is unavailable; staging retained. Select a supported GPU encoder or CPU mode explicitly.");
        return LinuxReplayBuffer.RecorderCodec(codec) + "_" + family;
    }
    private static string SoftwareCodec(string codec) => codec switch
    { "H.264" => "libx264", "H.265" or "HEVC" => "libx265", "AV1" => "libsvtav1", _ => throw new NotSupportedException($"Unsupported finalization codec: {codec}") };
    internal static async Task FinalizeAsync(string staging, string destination, LinuxClipInterval? interval, ReplayBufferConfig config, bool session, CancellationToken token, Action<double, double>? progress = null)
    {
        var pending = Path.Combine(Path.GetDirectoryName(destination)!, "." + Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            double duration = interval?.Duration ?? 0;
            if (progress is not null && duration == 0) {
                var value = await LinuxMediaProcess.RunAsync(FfmpegPathResolver.FfprobePath, ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", staging], token);
                double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            }
            var copy = interval is not null && await CanCopyIntervalAsync(staging, interval, session ? config.FullSessionVideoCodec : config.VideoCodec, token);
            var args = Arguments(staging, pending, interval, config, session, copy).ToList();
            if (progress is not null) args.InsertRange(0, ["-progress", "pipe:1", "-stats_period", "0.5"]);
            await LinuxMediaProcess.RunAsync(FfmpegPathResolver.FfmpegPath, args, token, progress is null ? null : line => {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out var microseconds))
                    progress(Math.Max(0, microseconds / 1_000_000.0), duration);
            });
            using var probe = JsonDocument.Parse(await LinuxMediaProcess.RunAsync(FfmpegPathResolver.FfprobePath,
                ["-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", pending], token));
            if (!probe.RootElement.GetProperty("streams").EnumerateArray().Any(s => s.GetProperty("codec_type").GetString() == "video") || new FileInfo(pending).Length == 0)
                throw new InvalidDataException("Finalized clip has no video.");
            File.Move(pending, destination, false);
        }
        catch
        {
            // Staging and request metadata remain recoverable; never publish an incomplete card.
            try { File.Delete(pending); } catch (IOException) { }
            throw;
        }
    }
}
