using ClypDat.App.Services;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace ClypDat.App.Tests;

public sealed unsafe class HdrToSdrGpuConverterTests
{
    [Fact]
    public void ConvertsHdrPatchesWithoutStaleOutputAndFeedsNv12Scaler()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out var device, out _, out var context).CheckError();
        using var ownedDevice = device;
        using var ownedContext = context;
        using var converter = new HdrToSdrGpuConverter(device,
            new HdrCaptureCompatibility.DisplayProfile(true, 80, 1000));

        using var rejected = device.CreateTexture2D(new Texture2DDescription
        {
            Width = 4, Height = 4, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource
        });
        Assert.Throws<InvalidOperationException>(() => converter.Convert(rejected));

        using var first = CreateHdrTexture(device, 4, 4, FirstPatches);
        var firstOutput = converter.Convert(first);
        var gray = ReadBgra(device, context, firstOutput, 1, 0);
        var white = ReadBgra(device, context, firstOutput, 2, 0);
        var red = ReadBgra(device, context, firstOutput, 3, 0);
        var green = ReadBgra(device, context, firstOutput, 0, 1);
        var blue = ReadBgra(device, context, firstOutput, 1, 1);
        Assert.True(gray.R > 0 && gray.G > 0 && gray.B > 0, "Gray HDR patch became black.");
        Assert.True(white.R > gray.R && white.G > gray.G && white.B > gray.B, "White patch lost contrast.");
        Assert.True(red.R > red.G + 12 && red.R > red.B + 12, "Red patch lost channel separation.");
        Assert.True(green.G > green.R + 12 && green.G > green.B + 12, "Green patch lost channel separation.");
        Assert.True(blue.B > blue.R + 12 && blue.B > blue.G + 12, "Blue patch lost channel separation.");
        Assert.Equal((byte)255, red.A);
        Assert.Equal((byte)255, green.A);
        Assert.Equal((byte)255, blue.A);

        using var changed = CreateHdrTexture(device, 4, 4, ChangedPatches);
        var changedOutput = converter.Convert(changed);
        var changedRed = ReadBgra(device, context, changedOutput, 3, 0);
        Assert.True(changedRed.G > changedRed.R + 12 && changedRed.G > changedRed.B + 12,
            "Second conversion returned stale pixels.");

        using var resized = CreateHdrTexture(device, 6, 4, FirstPatches);
        var resizedOutput = converter.Convert(resized);
        Assert.Equal((uint)6, resizedOutput.Description.Width);
        Assert.Equal((uint)4, resizedOutput.Description.Height);
        var resizedRed = ReadBgra(device, context, resizedOutput, 3, 0);
        Assert.True(resizedRed.R > resizedRed.G + 12 && resizedRed.R > resizedRed.B + 12,
            "Resized conversion lost patch color.");

        var scaler = NativeReplayBuffer.CreateGpuScaler(device, 6, 4, 6, 4, 60);
        try
        {
            using var inputView = scaler.VideoDevice.CreateVideoProcessorInputView(resizedOutput, scaler.Enumerator,
                new VideoProcessorInputViewDescription { ViewDimension = VideoProcessorInputViewDimension.Texture2D });
            scaler.VideoContext.VideoProcessorBlt(scaler.Processor, scaler.OutputView, 0, 1,
                [new VideoProcessorStream { Enable = true, InputSurface = inputView }]);
            context.CopyResource(scaler.Nv12StagingRing[0], scaler.Nv12Output);
            var mapped = context.Map(scaler.Nv12StagingRing[0], 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var luma = (byte*)mapped.DataPointer;
                var grayY = luma[mapped.RowPitch + 1];
                var whiteY = luma[mapped.RowPitch + 2];
                var redY = luma[3];
                Assert.True(grayY > 16, "NV12 gray patch became video-range black.");
                Assert.True(whiteY > grayY, "NV12 path lost luminance contrast.");
                Assert.True(redY > 16, "NV12 colored patch became black.");
            }
            finally { context.Unmap(scaler.Nv12StagingRing[0], 0); }
        }
        finally { DisposeScaler(scaler); }
    }

    private static ID3D11Texture2D CreateHdrTexture(ID3D11Device device, int width, int height,
        Func<int, int, (float R, float G, float B)> color)
    {
        var pixels = new ushort[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (r, g, b) = color(x, y);
            var offset = (y * width + x) * 4;
            pixels[offset] = BitConverter.HalfToUInt16Bits((Half)r);
            pixels[offset + 1] = BitConverter.HalfToUInt16Bits((Half)g);
            pixels[offset + 2] = BitConverter.HalfToUInt16Bits((Half)b);
            pixels[offset + 3] = BitConverter.HalfToUInt16Bits((Half)1);
        }
        fixed (ushort* data = pixels)
            return device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                Format = Format.R16G16B16A16_Float, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource
            }, new SubresourceData((nint)data, (uint)(width * sizeof(ushort) * 4)));
    }

    private static (byte B, byte G, byte R, byte A) ReadBgra(ID3D11Device device, ID3D11DeviceContext context,
        ID3D11Texture2D texture, int x, int y)
    {
        using var staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = texture.Description.Width, Height = texture.Description.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read
        });
        context.CopyResource(staging, texture);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixel = (byte*)mapped.DataPointer + y * mapped.RowPitch + x * 4;
            return (pixel[0], pixel[1], pixel[2], pixel[3]);
        }
        finally { context.Unmap(staging, 0); }
    }

    private static (float R, float G, float B) FirstPatches(int x, int y) => (x, y) switch
    {
        (0, 0) => (0, 0, 0), (1, 0) => (0.25f, 0.25f, 0.25f), (2, 0) => (1, 1, 1),
        (3, 0) => (1, 0, 0), (0, 1) => (0, 1, 0), (1, 1) => (0, 0, 1), _ => (0.5f, 0.5f, 0.5f)
    };

    private static (float R, float G, float B) ChangedPatches(int x, int y) => (x, y) switch
    {
        (3, 0) => (0, 1, 0), _ => FirstPatches(x, y)
    };

    private static void DisposeScaler((ID3D11VideoDevice VideoDevice, ID3D11VideoContext VideoContext,
        ID3D11VideoProcessorEnumerator Enumerator, ID3D11VideoProcessor Processor, ID3D11Texture2D Nv12Output,
        ID3D11Texture2D[] Nv12StagingRing, ID3D11VideoProcessorOutputView OutputView) scaler)
    {
        scaler.OutputView.Dispose();
        foreach (var staging in scaler.Nv12StagingRing) staging.Dispose();
        scaler.Nv12Output.Dispose();
        scaler.Processor.Dispose();
        scaler.Enumerator.Dispose();
        scaler.VideoContext.Dispose();
        scaler.VideoDevice.Dispose();
    }
}
