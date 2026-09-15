using ClypDat.App.Services;
using FFmpeg.AutoGen;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace ClypDat.App.Tests;

public sealed unsafe class CaptureAspectFitTests
{
    private const int Width = 1758, Height = 1080;
    private static readonly (int Width, int Height)[] Sizes =
        [(858, 527), (3840, 2160), (858, 527), (1080, 1920), (3440, 1440), (857, 529)];

    [Fact]
    public void FullscreenFitsFixedCanvasAndRejectsOutsideCursor()
    {
        var bounds = CaptureAspectFit.Create(3840, 2160, Width, Height);
        Assert.Equal(new CaptureAspectFit(0, 46, 1758, 988), bounds);
        Assert.Equal((879, 540), bounds.MapCursor(1920, 1080, 3840, 2160));
        Assert.Equal((int.MinValue, int.MinValue), bounds.MapCursor(-1, 0, 3840, 2160));
        Assert.Equal((int.MinValue, int.MinValue), bounds.MapCursor(3840, 0, 3840, 2160));
        foreach (var (w, h) in Sizes)
        {
            var fit = CaptureAspectFit.Create(w, h, 1759, 1081);
            Assert.Equal(0, (fit.X | fit.Y | fit.Width | fit.Height) & 1);
            Assert.InRange(fit.Right, 2, 1759);
            Assert.InRange(fit.Bottom, 2, 1081);
        }
    }

    [Fact]
    public void SoftwareResizeSequenceKeepsSquareContentAndClearsPadding()
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        var frame = ffmpeg.av_frame_alloc();
        frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        frame->width = Width;
        frame->height = Height;
        Assert.True(ffmpeg.av_frame_get_buffer(frame, 32) >= 0);
        try
        {
            foreach (var (w, h) in Sizes)
            {
                var bounds = CaptureAspectFit.Create(w, h, Width, Height);
                var source = SquareGrid(w, h);
                var scaler = NativeReplayBuffer.CreateScaler(w, h, Width, Height);
                try
                {
                    fixed (byte* pixels = source)
                        NativeReplayBuffer.ScaleSoftwareFrame(scaler, [pixels], [w * 4], h, frame, bounds);
                    CheckPixels(frame->data[0], frame->linesize[0], frame->data[1], frame->linesize[1], bounds);
                    NativeReplayBuffer.DrawDesktopCursorNv12(frame, bounds, bounds.Right - 1, bounds.Bottom - 1);
                    CheckPadding(frame->data[0], frame->linesize[0], frame->data[1], frame->linesize[1], bounds);
                }
                finally { ffmpeg.sws_freeContext(scaler); }
            }
        }
        finally { ffmpeg.av_frame_free(&frame); }
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void GpuResizeAndRecoveryKeepSquaresAndClearPadding(bool directCrop)
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out var device, out _, out var context).CheckError();
        using (device)
        using (context)
        {
            // Repeat complete creation to exercise the same setup used by device recovery.
            for (var recovery = 0; recovery < 2; recovery++)
            {
                var gpu = NativeReplayBuffer.CreateGpuScaler(device, 858, 527, Width, Height, 60);
                var processor = gpu.Processor;
                var enumerator = gpu.Enumerator;
                try
                {
                    foreach (var (w, h) in Sizes)
                    {
                        processor.Dispose();
                        enumerator.Dispose();
                        (enumerator, processor) = NativeReplayBuffer.CreateVideoProcessorForSize(gpu.VideoDevice, w, h, Width, Height, 60);
                        NativeReplayBuffer.ConfigureVideoProcessor(gpu.VideoContext, processor, w, h, Width, Height);
                        var offset = directCrop ? 16 : 0;
                        var source = SquareGrid(w, h, offset);
                        fixed (byte* pixels = source)
                        {
                            using var input = device.CreateTexture2D(new Texture2DDescription
                            {
                                Width = (uint)(w + offset * 2), Height = (uint)(h + offset * 2),
                                MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default
                            }, new SubresourceData((nint)pixels, (uint)((w + offset * 2) * 4)));
                            using var view = gpu.VideoDevice.CreateVideoProcessorInputView(input, enumerator,
                                new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
                            var cursor = NativeReplayBuffer.CreateGpuCursorOverlay(device, gpu.VideoDevice, enumerator);
                            using var cursorTexture = cursor.Texture;
                            using var cursorView = cursor.InputView;
                            NativeReplayBuffer.UpdateGpuCursorOverlay(device, cursorTexture);
                            var bounds = CaptureAspectFit.Create(w, h, Width, Height);
                            Assert.False(NativeReplayBuffer.ConfigureGpuCursorBounds(gpu.VideoContext, processor, bounds, bounds.Right, bounds.Bottom));
                            Assert.True(NativeReplayBuffer.ConfigureGpuCursorBounds(gpu.VideoContext, processor, bounds, bounds.Right - 6, bounds.Bottom - 8));
                            if (directCrop)
                                gpu.VideoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, new Vortice.RawRect(offset, offset, w + offset, h + offset));
                            gpu.VideoContext.VideoProcessorBlt(processor, gpu.OutputView, 0, 2,
                                [new VideoProcessorStream { Enable = true, InputSurface = view },
                                 new VideoProcessorStream { Enable = true, InputSurface = cursorView }]);
                            context.CopyResource(gpu.Nv12StagingRing[0], gpu.Nv12Output);
                            var mapped = context.Map(gpu.Nv12StagingRing[0], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                            try
                            {
                                Assert.True(((byte*)mapped.DataPointer)[(bounds.Bottom - 2) * mapped.RowPitch + bounds.Right - 4] > 210,
                                    "The clipped cursor must remain aligned with the gameplay edge.");
                                CheckPixels((byte*)mapped.DataPointer, (int)mapped.RowPitch,
                                    (byte*)mapped.DataPointer + mapped.RowPitch * Height, (int)mapped.RowPitch,
                                    CaptureAspectFit.Create(w, h, Width, Height));
                            }
                            finally { context.Unmap(gpu.Nv12StagingRing[0], 0); }
                        }
                    }
                }
                finally
                {
                    processor.Dispose(); enumerator.Dispose(); gpu.OutputView.Dispose();
                    foreach (var staging in gpu.Nv12StagingRing) staging.Dispose();
                    gpu.Nv12Output.Dispose(); gpu.VideoContext.Dispose(); gpu.VideoDevice.Dispose();
                }
            }
        }
    }

    private static byte[] SquareGrid(int width, int height, int offset = 0)
    {
        var pitch = (width + offset * 2) * 4;
        var result = new byte[pitch * (height + offset * 2)];
        var side = Math.Min(width, height) / 3;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var square = Math.Abs(x - width / 2) < side / 2 && Math.Abs(y - height / 2) < side / 2;
            var value = square ? (byte)255 : x % 64 < 2 || y % 64 < 2 ? (byte)0 : (byte)80;
            var index = (y + offset) * pitch + (x + offset) * 4;
            result[index] = result[index + 1] = result[index + 2] = value;
            result[index + 3] = 255;
        }
        return result;
    }

    private static void CheckPixels(byte* y, int stride, byte* uv, int uvStride, CaptureAspectFit bounds)
    {
        var horizontal = 0;
        var vertical = 0;
        for (var x = bounds.X; x < bounds.Right; x++)
            if (y[(bounds.Y + bounds.Height / 2) * stride + x] > 210) horizontal++;
        for (var row = bounds.Y; row < bounds.Bottom; row++)
            if (y[row * stride + bounds.X + bounds.Width / 2] > 210) vertical++;
        Assert.True(horizontal > 50);
        Assert.InRange(Math.Abs(horizontal - vertical), 0, 3);
        CheckPadding(y, stride, uv, uvStride, bounds);
    }

    private static void CheckPadding(byte* y, int stride, byte* uv, int uvStride, CaptureAspectFit bounds)
    {
        for (var row = 0; row < Height; row++)
        for (var x = 0; x < Width; x++)
        {
            if (x >= bounds.X && x < bounds.Right && row >= bounds.Y && row < bounds.Bottom) continue;
            Assert.Equal(16, y[row * stride + x]);
            Assert.Equal(128, uv[(row / 2) * uvStride + x]);
        }
    }
}

// Hosted CI has no video-processing device. Opt in on a capture-capable machine:
// CLYPDAT_TEST_GPU=1 dotnet test --filter FullyQualifiedName~CaptureAspectFitTests
internal sealed class GpuTheoryAttribute : TheoryAttribute
{
    public GpuTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("CLYPDAT_TEST_GPU") != "1")
            Skip = "Set CLYPDAT_TEST_GPU=1 to exercise the physical D3D11 video processor.";
    }
}
