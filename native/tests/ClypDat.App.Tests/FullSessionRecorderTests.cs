using System.Diagnostics;
using System.Text.Json;
using ClypDat.App.Services;
using ClypDat.Core.Settings;
using ClypDat.Capture.Abstractions;
using FFmpeg.AutoGen;
using NAudio.Wave;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class FullSessionRecorderTests
{
    internal static ReplayBufferConfig Config(string root, string container) => new(60, 64, 30, 0, 0, 64, 64,
        "", "", ["Discord"], ["default"], "", [], "Synthetic", "synthetic.exe", "", "", LibraryFolder: root,
        FullSessionRecordingEnabled: true, FullSessionRecordingFolder: Path.Combine(root, "VODs"), FullSessionContainer: container);

    [Theory]
    [InlineData("MKV", 6, "libx264")]
    public unsafe void LiveFileHasVideoNamedAudioAndRecoverableCompletedFootage(string container, int secondsToRecord, string encoder)
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        var root = Path.Combine(AppContext.BaseDirectory, "full-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inputPath = Path.Combine(root, "input.mp4");
        var inputArgs = new List<string> { "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=30", "-t", secondsToRecord.ToString(), "-c:v", encoder, "-g", "30", "-threads", "2" };
        inputArgs.AddRange(encoder == "libx264" ? new[] { "-preset", "ultrafast", "-bf", "0" } : new[] { "-cpu-used", "8", "-lag-in-frames", "0" });
        inputArgs.Add(inputPath);
        Run(FfmpegPathResolver.FfmpegPath, inputArgs.ToArray());
        AVFormatContext* input = null;
        AVCodecContext* codec = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        FullSessionRecorder? recorder = null;
        try
        {
            Assert.True(ffmpeg.avformat_open_input(&input, inputPath, null, null) >= 0);
            Assert.True(ffmpeg.avformat_find_stream_info(input, null) >= 0);
            var stream = input->streams[0];
            codec = ffmpeg.avcodec_alloc_context3(null);
            Assert.True(ffmpeg.avcodec_parameters_to_context(codec, stream->codecpar) >= 0);
            codec->time_base = stream->time_base;
            var config = Config(root, container);
            recorder = new FullSessionRecorder(config, codec, _ => { });
            var started = DateTime.UtcNow;
            var game = Guid.NewGuid(); var mic = Guid.NewGuid();
            var wave = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
            var microphone = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);
            var count = 0;
            long liveSize = 0;
            string? interrupted = null;
            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                var seconds = packet->pts * ffmpeg.av_q2d(stream->time_base);
                // A source running slightly fast exercises compensation each packet.
                var audioStart = started.AddSeconds(count / 30d * 0.9998);
                recorder.EnqueueAudio(new(AudioCapturePipeline.AudioCaptureKind.Game, "default", game, wave, Tone(1470, 2, count * 1470, 44100), 1470 * 2 * 4, audioStart));
                if (count == 1800) mic = Guid.NewGuid(); // default microphone endpoint changed
                if (count >= 90) // selected microphone appears late
                    recorder.EnqueueAudio(new(AudioCapturePipeline.AudioCaptureKind.Microphone, "default", mic, microphone, Tone(1600, 1, count * 1600, 48000), 1600 * 4, started.AddSeconds(seconds)));
                recorder.EnqueueVideo(packet, started.AddSeconds(seconds));
                ffmpeg.av_packet_unref(packet);
                count++;
                if (count % 30 == 0)
                {
                    Assert.True(SpinWait.SpinUntil(() => recorder.QueuedBytes == 0 || recorder.Completion.IsCompleted, TimeSpan.FromSeconds(10)));
                    Assert.NotEqual(FullSessionState.Failed, recorder.Status.State);
                }
                if (count == 90)
                {
                    Assert.True(File.Exists(recorder.OutputPath));
                    Assert.True(RecordingFileOwnership.IsActive(recorder.OutputPath));
                    Assert.False(MediaProbeService.IsVideoFile(recorder.OutputPath));
                    liveSize = new FileInfo(recorder.OutputPath).Length;
                    Assert.True(liveSize > 1024);
                    interrupted = Path.Combine(root, "interrupted." + container.ToLowerInvariant());
                    File.Copy(recorder.OutputPath, interrupted);
                    File.WriteAllText(interrupted + ".recording", "abandoned");
                    Assert.True(MediaProbeService.IsVideoFile(interrupted));
                    Assert.False(File.Exists(interrupted + ".recording"));
                }
            }
            recorder.Dispose();
            Assert.Equal(FullSessionState.Completed, recorder.Status.State);
            Assert.True(new FileInfo(recorder.OutputPath).Length > liveSize);
            Assert.False(RecordingFileOwnership.IsActive(recorder.OutputPath));
            Assert.True(MediaProbeService.IsVideoFile(recorder.OutputPath));
            var info = ClipInfoSidecar.Load(root, recorder.OutputPath);
            Assert.NotNull(info);
            Assert.Equal("Session - Synthetic", info.FileTitle);
            using var probe = JsonDocument.Parse(Run(FfmpegPathResolver.FfprobePath, "-v", "error", "-show_streams", "-show_format", "-of", "json", recorder.OutputPath));
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = Assert.Single(streams, s => s.GetProperty("codec_type").GetString() == "video");
            Assert.Equal(encoder == "libx264" ? "h264" : "av1", video.GetProperty("codec_name").GetString());
            var audio = streams.Where(s => s.GetProperty("codec_type").GetString() == "audio").ToArray();
            Assert.Equal(3, audio.Length);
            Assert.Equal(new[] { "Game Audio", "Discord", "Microphone" }, audio.Select(AudioTitle));
            Assert.All(audio, s => Assert.Equal("48000", s.GetProperty("sample_rate").GetString()));
            Assert.Equal(1, audio[2].GetProperty("channels").GetInt32());
            var duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(duration, secondsToRecord - 0.05, secondsToRecord + 0.1);
            Run(FfmpegPathResolver.FfmpegPath, "-v", "error", "-i", recorder.OutputPath, "-map", "0", "-f", "null", "-");
            Run(FfmpegPathResolver.FfmpegPath, "-v", "error", "-ss", "4", "-i", recorder.OutputPath, "-t", "1", "-map", "0", "-f", "null", "-");
            using var recovered = JsonDocument.Parse(Run(FfmpegPathResolver.FfprobePath, "-v", "error", "-show_streams", "-of", "json", interrupted!));
            Assert.Equal(4, recovered.RootElement.GetProperty("streams").GetArrayLength());
            // AAC decoding proves sources resume into the declared lanes, while
            // an absent configured application remains silence.
            Assert.InRange(DecodeRms(recorder.OutputPath, 1, root, 1), 0.1, 0.3);
            Assert.InRange(DecodeRms(recorder.OutputPath, 2, root, 1), 0, 0.0001);
            Assert.InRange(DecodeRms(recorder.OutputPath, 3, root, 1), 0, 0.0001);
            Assert.InRange(DecodeRms(recorder.OutputPath, 3, root, 4), 0.1, 0.3);
            if (secondsToRecord > 60)
            {
                Assert.InRange(DecodeRms(recorder.OutputPath, 1, root, secondsToRecord - 2), 0.1, 0.3);
                Assert.InRange(DecodeRms(recorder.OutputPath, 3, root, 61), 0.1, 0.3);
            }
        }
        finally
        {
            recorder?.Dispose();
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&codec);
            ffmpeg.avformat_close_input(&input);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public unsafe void QueueOverflowAndDiskFailureStayInsideSession()
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        var root = Path.Combine(AppContext.BaseDirectory, "full-session-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var codec = ffmpeg.avcodec_alloc_context3(null);
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            codec->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO; codec->codec_id = AVCodecID.AV_CODEC_ID_H264;
            codec->width = 64; codec->height = 64; codec->time_base = new AVRational { num = 1, den = 30 };
            Assert.True(ffmpeg.av_new_packet(packet, 100) >= 0);
            using (var recorder = new FullSessionRecorder(Config(root, "MP4"), codec, _ => { }, 1))
            {
                recorder.EnqueueVideo(packet, DateTime.UtcNow);
                Assert.True(SpinWait.SpinUntil(() => recorder.Completion.IsCompleted, TimeSpan.FromSeconds(10)));
                Assert.Equal(FullSessionState.Failed, recorder.Status.State);
                Assert.Contains("queue overflow", recorder.Status.Failure);
                Assert.Equal(0, recorder.QueuedBytes);
                recorder.EnqueueVideo(packet, DateTime.UtcNow); // replay producer remains callable
            }
            var obstruction = Path.Combine(root, "blocked"); File.WriteAllText(obstruction, "file");
            using var diskFailure = new FullSessionRecorder(Config(root, "MKV") with { FullSessionRecordingFolder = obstruction }, codec, _ => { });
            Assert.True(SpinWait.SpinUntil(() => diskFailure.Completion.IsCompleted, TimeSpan.FromSeconds(10)));
            Assert.Equal(FullSessionState.Failed, diskFailure.Status.State);
            Assert.NotEmpty(diskFailure.Status.Failure);
            diskFailure.EnqueueVideo(packet, DateTime.UtcNow);
            using var closed = new ManualResetEventSlim();
            var failedConfig = Config(root, "MKV") with { FullSessionRecordingFolder = obstruction };
            using var buffer = new NativeReplayBuffer(() => failedConfig, _ => { });
            buffer.FullSessionClosed += (_, _) => closed.Set();
            buffer.StartFullSession(failedConfig, codec);
            Assert.True(closed.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(FullSessionState.Failed, buffer.GetHealthSnapshot().FullSession.State);
        }
        finally { ffmpeg.av_packet_free(&packet); ffmpeg.avcodec_free_context(&codec); Directory.Delete(root, true); }
    }

    internal static string Run(string executable, params string[] args)
    {
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException(executable); }
        Assert.True(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }
    internal static byte[] Tone(int frames, int channels, int offset, int rate)
    {
        var samples = new float[frames * channels];
        for (var i = 0; i < frames; i++) for (var c = 0; c < channels; c++) samples[i * channels + c] = (float)(0.25 * Math.Sin(2 * Math.PI * 440 * (offset + i) / rate));
        var bytes = new byte[samples.Length * 4]; Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length); return bytes;
    }
    private static string AudioTitle(JsonElement stream) => stream.GetProperty("tags").EnumerateObject().First(tag => tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase) || tag.Name.Equals("handler_name", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
    private static double DecodeRms(string path, int stream, string root, int second)
    {
        var raw = Path.Combine(root, "audio.raw");
        Run(FfmpegPathResolver.FfmpegPath, "-v", "error", "-y", "-ss", second.ToString(), "-i", path, "-t", "1", "-map", $"0:{stream}", "-f", "f32le", raw);
        var bytes = File.ReadAllBytes(raw); Assert.True(bytes.Length > 0);
        var sum = 0d; for (var i = 0; i < bytes.Length; i += 4) sum += Math.Pow(BitConverter.ToSingle(bytes, i), 2);
        return Math.Sqrt(sum / (bytes.Length / 4));
    }
}
