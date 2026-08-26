using System.Diagnostics;
using System.Globalization;

namespace ClypDat.App.Services;

/// <summary>
/// Detects and repairs clips whose container stores one encoder's H.264
/// parameter sets over another encoder's slices.
///
/// Builds up to 1.3.0 could replace the replay encoder mid-session (the
/// sustained-overload failover) while keeping the ring buffer, but recorded
/// extradata only once per session. Because the replay encoder runs with
/// AV_CODEC_FLAG_GLOBAL_HEADER, the packets carry no in-band SPS/PPS, so a clip
/// cut after such a swap is muxed under the wrong avcC and decodes as flat grey
/// with vertical streaking. The audio is untouched, and so is the video slice
/// data - only the parameter sets are wrong, which makes this repairable without
/// re-encoding anything.
///
/// 1.3.1 stops producing these. This exists for clips already on disk.
/// </summary>
public sealed class ClipCorruptionRepairService
{
    public enum RepairStatus { Healthy, Repaired, Unrepairable, Skipped }

    public readonly record struct RepairResult(RepairStatus Status, string Detail);

    private const int InspectFrames = 8;
    private const int VerifyFrames = 24;
    private const int ThumbnailEdge = 64;
    private const int ThumbnailBytes = ThumbnailEdge * ThumbnailEdge;

    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(4);

    /// <summary>
    /// Read-only check. Decodes a few frames and reports whether the stream is
    /// self-consistent; never touches the file.
    /// </summary>
    public static async Task<bool> IsCorruptAsync(string clipPath, CancellationToken token)
    {
        if (!FfmpegPathResolver.IsAvailable || !File.Exists(clipPath)) return false;
        var (errors, frames) = await DecodeProbeAsync(clipPath, InspectFrames, token).ConfigureAwait(false);
        // Decoder errors are the whole signal. An earlier version also required
        // tonal spread, on the theory that the broken decode is flat grey - but
        // real corrupt clips here measured spreads of 0, 30, 52 and 70, while a
        // perfectly healthy clip that opens on a black frame measured 0. Spread
        // says nothing either way; mismatched parameter sets cannot be parsed
        // without complaint, and a clip that decodes silently is fine.
        return frames > 0 && errors > 0;
    }

    /// <summary>
    /// Rebuilds the clip's H.264 parameter sets and re-muxes in place. Only the
    /// container is rewritten - no frame is ever re-encoded, and audio is copied
    /// straight from the original.
    /// </summary>
    public static async Task<RepairResult> RepairAsync(string clipPath, CancellationToken token)
    {
        if (!FfmpegPathResolver.IsAvailable) return new RepairResult(RepairStatus.Skipped, "ffmpeg unavailable");
        if (!File.Exists(clipPath)) return new RepairResult(RepairStatus.Skipped, "file missing");

        var (errors, frames) = await DecodeProbeAsync(clipPath, InspectFrames, token).ConfigureAwait(false);
        if (frames == 0) return new RepairResult(RepairStatus.Skipped, "no frames decoded");
        if (errors == 0) return new RepairResult(RepairStatus.Healthy, string.Empty);
        AppLog.Info($"Clip repair: {Path.GetFileName(clipPath)} decodes with {errors} error(s) - rebuilding its parameter sets.");

        // Beside the clip, not in %TEMP%: File.Replace cannot move across
        // volumes, and a library on D: with a temp folder on C: made every
        // repair fail with "Unable to move the replacement file to the file to
        // be replaced".
        var workFolder = Path.Combine(Path.GetDirectoryName(clipPath)!, ".clypdat-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workFolder);
        var rawPath = Path.Combine(workFolder, "raw.h264");
        var candidatePath = Path.Combine(workFolder, "candidate.h264");
        var rebuiltPath = Path.Combine(workFolder, "rebuilt.mp4");
        try
        {
            // -c copy: the slices are never re-encoded, only re-framed from
            // length-prefixed to Annex-B so the parameter sets become editable.
            await RunAsync(FfmpegPathResolver.FfmpegPath,
                $"-v error -y -i \"{clipPath}\" -map 0:v:0 -c copy -bsf:v h264_mp4toannexb -f h264 \"{rawPath}\"", token)
                .ConfigureAwait(false);
            if (!File.Exists(rawPath) || new FileInfo(rawPath).Length == 0)
                return new RepairResult(RepairStatus.Unrepairable, "could not extract the video stream");

            var stream = await File.ReadAllBytesAsync(rawPath, token).ConfigureAwait(false);
            var nals = H264ParameterSets.SplitAnnexB(stream);
            H264ParameterSets.SequenceParameterSet? sps = null;
            H264ParameterSets.PictureParameterSet? pps = null;
            foreach (var (start, length) in nals)
            {
                if (length <= 0) continue;
                var type = stream[start] & 0x1F;
                try
                {
                    if (type == 7 && sps is null) sps = H264ParameterSets.ParseSps(stream.AsSpan(start, length));
                    else if (type == 8 && pps is null) pps = H264ParameterSets.ParsePps(stream.AsSpan(start, length));
                }
                catch (InvalidDataException error)
                {
                    // Something this rewriter does not model (scaling matrices,
                    // an encoder shape we have never produced). Leave the file
                    // exactly as it is rather than guess at it.
                    return new RepairResult(RepairStatus.Unrepairable, error.Message);
                }
                if (sps is not null && pps is not null) break;
            }
            if (sps is null || pps is null) return new RepairResult(RepairStatus.Unrepairable, "no parameter sets in the stream");

            foreach (var (candidateSps, candidatePps, label) in Candidates(sps, pps))
            {
                token.ThrowIfCancellationRequested();
                await WriteCandidateStreamAsync(candidatePath, stream, nals,
                    H264ParameterSets.WriteSps(candidateSps), H264ParameterSets.WritePps(candidatePps), token).ConfigureAwait(false);

                var candidateProbe = await DecodeProbeAsync(candidatePath, InspectFrames, token, rawH264: true).ConfigureAwait(false);
                if (candidateProbe.Errors != 0 || candidateProbe.Frames == 0) continue;

                // Re-mux rather than patch the original in place: the corrected
                // SPS is not always the same length as the stale one, and growing
                // a box inside an MP4 invalidates every chunk offset after it.
                // Audio and metadata come straight off the original file.
                var frameRate = await ProbeFrameRateAsync(clipPath, token).ConfigureAwait(false);
                await RunAsync(FfmpegPathResolver.FfmpegPath,
                    $"-v error -y -fflags +genpts -r {frameRate} -f h264 -i \"{candidatePath}\" -i \"{clipPath}\" " +
                    $"-map 0:v:0 -map 1:a? -c copy -movflags +faststart -map_metadata 1 \"{rebuiltPath}\"", token)
                    .ConfigureAwait(false);
                if (!File.Exists(rebuiltPath) || new FileInfo(rebuiltPath).Length == 0) continue;

                // Verify the finished file, not just the elementary stream: a
                // clean decode here is the only thing that authorises
                // overwriting the user's clip.
                var verify = await DecodeProbeAsync(rebuiltPath, VerifyFrames, token).ConfigureAwait(false);
                if (verify.Errors != 0 || verify.Frames == 0) continue;

                await ReplaceInPlaceAsync(clipPath, rebuiltPath).ConfigureAwait(false);
                AppLog.Info($"Clip repair: {Path.GetFileName(clipPath)} repaired ({label}).");
                return new RepairResult(RepairStatus.Repaired, label);
            }

            AppLog.Info($"Clip repair: {Path.GetFileName(clipPath)} could not be repaired - no candidate parameter set decoded cleanly.");
            return new RepairResult(RepairStatus.Unrepairable, "no candidate decoded cleanly");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            AppLog.Error($"Clip repair failed: {clipPath}", error);
            return new RepairResult(RepairStatus.Unrepairable, error.Message);
        }
        finally
        {
            try { Directory.Delete(workFolder, recursive: true); } catch { /* best effort */ }
        }
    }

    // Ordered by what actually differs between the encoders this app falls over
    // between. entropy_coding_mode_flag is first because CABAC vs CAVLC is the
    // split between the hardware encoders and libx264's ultrafast preset, and it
    // is the field whose mismatch garbles every slice.
    private static IEnumerable<(H264ParameterSets.SequenceParameterSet Sps, H264ParameterSets.PictureParameterSet Pps, string Label)>
        Candidates(H264ParameterSets.SequenceParameterSet sps, H264ParameterSets.PictureParameterSet pps)
    {
        var entropyModes = new[] { 1 - pps.EntropyCodingModeFlag, pps.EntropyCodingModeFlag };
        var transformModes = new[] { 1 - pps.Transform8x8ModeFlag, pps.Transform8x8ModeFlag };
        var frameNumWidths = Distinct(sps.Log2MaxFrameNumMinus4, 0, 4, 1, 2, 3, 5, 6, 8);
        var refFrames = Distinct(sps.MaxNumRefFrames, 1, 2, 3, 4);
        var pocTypes = Distinct(sps.PicOrderCntType, 2, 0);

        foreach (var entropy in entropyModes)
        foreach (var transform in transformModes)
        foreach (var frameNum in frameNumWidths)
        foreach (var refs in refFrames)
        foreach (var poc in pocTypes)
        foreach (var pocLsb in poc == 0 ? Distinct(sps.Log2MaxPicOrderCntLsbMinus4, 4, 0, 1, 2, 3, 5, 6, 8) : new[] { sps.Log2MaxPicOrderCntLsbMinus4 })
        {
            if (entropy == pps.EntropyCodingModeFlag && transform == pps.Transform8x8ModeFlag &&
                frameNum == sps.Log2MaxFrameNumMinus4 && refs == sps.MaxNumRefFrames &&
                poc == sps.PicOrderCntType && pocLsb == sps.Log2MaxPicOrderCntLsbMinus4)
            {
                continue; // The file's own sets; already known not to decode.
            }

            var candidateSps = sps.Clone();
            candidateSps.Log2MaxFrameNumMinus4 = frameNum;
            candidateSps.MaxNumRefFrames = refs;
            candidateSps.PicOrderCntType = poc;
            candidateSps.Log2MaxPicOrderCntLsbMinus4 = pocLsb;
            var candidatePps = pps.Clone();
            candidatePps.EntropyCodingModeFlag = entropy;
            candidatePps.Transform8x8ModeFlag = transform;

            yield return (candidateSps, candidatePps,
                $"cabac={entropy} 8x8={transform} log2_max_frame_num_minus4={frameNum} refs={refs} poc={poc}");
        }
    }

    private static int[] Distinct(int current, params int[] preferred)
    {
        var ordered = new List<int>();
        foreach (var value in preferred)
        {
            if (value != current && !ordered.Contains(value)) ordered.Add(value);
        }
        ordered.Add(current);
        return ordered.ToArray();
    }

    private static async Task WriteCandidateStreamAsync(string path, byte[] stream,
        List<(int Start, int Length)> nals, byte[] sps, byte[] pps, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var startCode = new byte[] { 0, 0, 0, 1 };
        foreach (var (start, length) in nals)
        {
            if (length <= 0) continue;
            var type = stream[start] & 0x1F;
            await output.WriteAsync(startCode, token).ConfigureAwait(false);
            if (type == 7) await output.WriteAsync(sps, token).ConfigureAwait(false);
            else if (type == 8) await output.WriteAsync(pps, token).ConfigureAwait(false);
            else await output.WriteAsync(stream.AsMemory(start, length), token).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceInPlaceAsync(string clipPath, string rebuiltPath)
    {
        // File.Replace writes the original aside before swapping, so a failure
        // mid-way can never leave the user with neither file. The aside copy is
        // only deleted once the replacement is in place.
        var backupPath = clipPath + ".corrupt";
        await FileRetry.RunAsync(() =>
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Replace(rebuiltPath, clipPath, backupPath, ignoreMetadataErrors: true);
            File.Delete(backupPath);
        }, $"Clip repair replace: {Path.GetFileName(clipPath)}").ConfigureAwait(false);
    }

    private static async Task<(int Errors, int Frames)> DecodeProbeAsync(
        string path, int frames, CancellationToken token, bool rawH264 = false)
    {
        // Scaled to a fixed tiny size so one decoded frame is exactly
        // ThumbnailBytes regardless of the clip's resolution - that makes the
        // frame count exact without having to probe the dimensions first, and
        // keeps the piped output to a few KB.
        var input = rawH264 ? $"-f h264 -i \"{path}\"" : $"-i \"{path}\" -map 0:v:0";
        var arguments = $"-hide_banner -v error {input} -frames:v {frames} " +
                        $"-vf scale={ThumbnailEdge}:{ThumbnailEdge} -f rawvideo -pix_fmt gray -";
        var result = await RunAsync(FfmpegPathResolver.FfmpegPath, arguments, token, captureStdout: true).ConfigureAwait(false);

        var errors = result.StandardError
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        return (errors, result.StandardOutput.Length / ThumbnailBytes);
    }

    private static async Task<string> ProbeFrameRateAsync(string clipPath, CancellationToken token)
    {
        var result = await RunAsync(FfmpegPathResolver.FfprobePath,
            $"-v error -select_streams v:0 -show_entries stream=avg_frame_rate -of csv=p=0 \"{clipPath}\"",
            token, captureStdout: true).ConfigureAwait(false);
        var text = System.Text.Encoding.UTF8.GetString(result.StandardOutput).Trim();
        if (!text.Contains('/') || text.StartsWith("0/", StringComparison.Ordinal)) return "60";
        var parts = text.Split('/');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den) &&
            num > 0 && den > 0)
        {
            return text;
        }
        return "60";
    }

    private readonly record struct ProcessResult(byte[] StandardOutput, string StandardError);

    private static async Task<ProcessResult> RunAsync(string executable, string arguments, CancellationToken token, bool captureStdout = false)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = FfmpegPathResolver.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();

        var stdoutTask = captureStdout
            ? ReadAllAsync(process.StandardOutput.BaseStream, token)
            : Task.Run(async () => { await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, token).ConfigureAwait(false); return Array.Empty<byte>(); }, token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {ProcessTimeout.TotalMinutes:F0} minutes.");
        }

        return new ProcessResult(await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, 1 << 20, token).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
