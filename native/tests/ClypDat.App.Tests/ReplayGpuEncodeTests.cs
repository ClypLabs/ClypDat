using System.Collections.Concurrent;
using System.Diagnostics;
using ClypDat.App.Services;
using FFmpeg.AutoGen;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;
using Xunit.Abstractions;

namespace ClypDat.App.Tests;

public sealed unsafe class ReplayGpuEncodeTests(ITestOutputHelper output)
{
    [GpuTheory]
    [InlineData(60)]
    public void SyntheticFramesDrainAcrossEncoderSwapAtConfiguredThroughput(int frameRate)
    {
        const int width = 1280, height = 720, frameCount = 180;
        FfmpegPathResolver.EnsureBundledFfmpeg();
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out var device, out _, out var context).CheckError();
        using var ownedDevice = device;
        using var ownedContext = context;
        using var session = new ReplaySessionLifetime(CancellationToken.None);
        var config = new ReplayBufferConfig(60, height, frameRate, 0, 0, width, height,
            "", "", [], [], "", [], "Synthetic", "synthetic.exe", "", "");
        using var buffer = new NativeReplayBuffer(() => config);
        var pools = new (nint DeviceRef, nint FramesRef)[2];
        var codecs = new nint[2];
        var path = Path.Combine(Path.GetTempPath(), $"clypdat-synthetic-{Guid.NewGuid():N}.mp4");
        AVFormatContext* format = null;
        var packet = ffmpeg.av_packet_alloc();
        using var queue = new BlockingCollection<NativeReplayBuffer.EncodeJob>(12);
        Thread? encoder = null;
        var stopped = false;
        try
        {
            for (var i = 0; i < 2; i++)
            {
                pools[i] = NativeReplayBuffer.TryCreateD3D11EncodeFrames(device, width, height, 32);
                Assert.NotEqual(0, pools[i].FramesRef);
                var codec = NativeReplayBuffer.CreateEncoder(config, width, height, pools[i].FramesRef, device,
                    out _, out var name, out var hardware, candidateOrder:
                    [new ReplayEncoderCandidate("h264_nvenc", ReplayVideoCodecPolicy.H264, ReplayEncoderInputPath.D3D11, 0)]);
                Assert.True(hardware, name);
                codecs[i] = (nint)codec;
            }
            Assert.True(ffmpeg.avformat_alloc_output_context2(&format, null, "mp4", path) >= 0);
            var stream = ffmpeg.avformat_new_stream(format, null);
            var firstCodec = (AVCodecContext*)codecs[0];
            stream->time_base = firstCodec->time_base;
            Assert.True(ffmpeg.avcodec_parameters_from_context(stream->codecpar, firstCodec) >= 0);
            Assert.True(ffmpeg.avio_open(&format->pb, path, ffmpeg.AVIO_FLAG_WRITE) >= 0);
            Assert.True(ffmpeg.avformat_write_header(format, null) >= 0);
            var packetPtr = (nint)packet;
            var formatPtr = (nint)format;
            var streamPtr = (nint)stream;
            encoder = new Thread(() => buffer.EncodeLoop(queue, codecs[0], packetPtr, formatPtr, streamPtr,
                new ReplayLatencyHistogram(), new ReplayLatencyHistogram(), session.NativeGate)) { IsBackground = true };
            encoder.Start();
            // Generated NV12 only. Never acquires desktop/window pixels.
            var pixels = new byte[width * height * 3 / 2];
            Array.Fill(pixels, (byte)80, 0, width * height);
            Array.Fill(pixels, (byte)128, width * height, width * height / 2);
            fixed (byte* data = pixels)
            {
                using var source = device.CreateTexture2D(new Texture2DDescription
                {
                    Width = width, Height = height, MipLevels = 1, ArraySize = 1,
                    Format = Format.NV12, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default
                }, new SubresourceData((nint)data, width));
                var timer = Stopwatch.StartNew();
                for (var i = 0; i < frameCount; i++)
                {
                    if (i == frameCount / 2)
                    {
                        var completion = session.CreateSwapCompletion();
                        Assert.True(queue.TryAdd(new(0, DateTime.UtcNow, codecs[1], completion), TimeSpan.FromSeconds(5)));
                        Assert.True(completion.Wait(TimeSpan.FromSeconds(5)));
                    }
                    var frame = ffmpeg.av_frame_alloc();
                    try
                    {
                        using (session.EnterNative())
                        {
                            Assert.True(ffmpeg.av_hwframe_get_buffer((AVBufferRef*)pools[i < frameCount / 2 ? 0 : 1].FramesRef, frame, 0) >= 0);
                            var texture = new Vortice.Direct3D11.ID3D11Texture2D((nint)frame->data[0]); // borrowed pool reference
                            context.CopySubresourceRegion(texture, (uint)(nint)frame->data[1], 0, 0, 0, source, 0);
                        }
                        frame->pts = i * 1_000_000L / frameRate;
                        Assert.True(queue.TryAdd(new((nint)frame, DateTime.UtcNow), TimeSpan.FromSeconds(5)));
                        frame = null;
                    }
                    finally { if (frame is not null) ffmpeg.av_frame_free(&frame); }
                }
                stopped = session.StopWorkers(() => true, queue.CompleteAdding, () => encoder.Join(TimeSpan.FromSeconds(10)));
                Assert.True(stopped);
                timer.Stop();
                var throughput = frameCount / timer.Elapsed.TotalSeconds;
                output.WriteLine($"Synthetic D3D11/NVENC: {frameCount} frames, encoder swap, {throughput:0.0} FPS throughput.");
                Assert.True(throughput >= frameRate, $"Throughput {throughput:0.0} below configured {frameRate} FPS.");
            }
            Assert.True(ffmpeg.av_write_trailer(format) >= 0);
            ffmpeg.avio_closep(&format->pb);
            ffmpeg.avformat_free_context(format);
            format = null;
            AVFormatContext* input = null;
            Assert.True(ffmpeg.avformat_open_input(&input, path, null, null) >= 0);
            try
            {
                var count = 0;
                while (ffmpeg.av_read_frame(input, packet) >= 0) { count++; ffmpeg.av_packet_unref(packet); }
                Assert.Equal(frameCount, count);
            }
            finally { ffmpeg.avformat_close_input(&input); }
        }
        finally
        {
            if (!stopped) stopped = session.StopWorkers(() => true, queue.CompleteAdding, () => encoder is null || encoder.Join(TimeSpan.FromSeconds(10)));
            if (stopped)
            {
                while (queue.TryTake(out var abandoned)) { var frame = (AVFrame*)abandoned.FramePtr; if (frame is not null) ffmpeg.av_frame_free(&frame); }
                ffmpeg.av_packet_free(&packet);
                if (format is not null) { ffmpeg.avio_closep(&format->pb); ffmpeg.avformat_free_context(format); }
                foreach (var pointer in codecs) { var codec = (AVCodecContext*)pointer; if (codec is not null) ffmpeg.avcodec_free_context(&codec); }
                foreach (var pool in pools)
                {
                    var frames = (AVBufferRef*)pool.FramesRef; var hardware = (AVBufferRef*)pool.DeviceRef;
                    if (frames is not null) ffmpeg.av_buffer_unref(&frames);
                    if (hardware is not null) ffmpeg.av_buffer_unref(&hardware);
                }
                File.Delete(path);
            }
        }
    }
}
