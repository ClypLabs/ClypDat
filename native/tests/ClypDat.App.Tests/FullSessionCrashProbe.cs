using ClypDat.App.Services;
using FFmpeg.AutoGen;
using NAudio.Wave;

namespace ClypDat.App.Tests;

// Only invoked explicitly by the crash test. All video is generated; no screen,
// microphone or device is opened. The parent kills this process with a live mux.
internal static class FullSessionCrashProbe
{
    public static unsafe int Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "--full-session-crash") return 2;
        var root = args[1]; var container = args[2];
        FfmpegPathResolver.EnsureBundledFfmpeg();
        var inputPath = Path.Combine(root, "source.mp4");
        FullSessionRecorderTests.Run(FfmpegPathResolver.FfmpegPath, "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=30", "-t", "5", "-c:v", "libx264", "-preset", "ultrafast", "-g", "30", "-bf", "0", inputPath);
        AVFormatContext* input = null;
        if (ffmpeg.avformat_open_input(&input, inputPath, null, null) < 0) return 3;
        ffmpeg.avformat_find_stream_info(input, null);
        var stream = input->streams[0];
        var codec = ffmpeg.avcodec_alloc_context3(null);
        ffmpeg.avcodec_parameters_to_context(codec, stream->codecpar); codec->time_base = stream->time_base;
        using var recorder = new FullSessionRecorder(FullSessionRecorderTests.Config(root, container), codec, _ => { });
        var packet = ffmpeg.av_packet_alloc();
        var started = DateTime.UtcNow; var source = Guid.NewGuid(); var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var count = 0;
        while (ffmpeg.av_read_frame(input, packet) >= 0)
        {
            var time = started.AddSeconds(count / 30d);
            recorder.EnqueueAudio(new(AudioCapturePipeline.AudioCaptureKind.Game, "default", source, format,
                FullSessionRecorderTests.Tone(1600, 2, count * 1600, 48000), 1600 * 2 * 4, time));
            recorder.EnqueueVideo(packet, time); ffmpeg.av_packet_unref(packet); count++;
            if (count % 30 == 0 && !SpinWait.SpinUntil(() => recorder.QueuedBytes == 0, TimeSpan.FromSeconds(10))) return 4;
        }
        // Publish only once all media reached the live file. No trailer yet.
        File.WriteAllText(Path.Combine(root, "ready.tmp"), recorder.OutputPath);
        File.Move(Path.Combine(root, "ready.tmp"), Path.Combine(root, "ready.txt"));
        Thread.Sleep(TimeSpan.FromSeconds(45)); // bounded fallback if the parent dies
        return 5;
    }
}
