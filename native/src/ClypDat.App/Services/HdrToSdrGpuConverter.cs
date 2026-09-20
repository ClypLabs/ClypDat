using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClypDat.App.Services;

// Kept on recorder device: no readback, and output stays a normal BGRA input
// for the existing D3D11 video processor/NV12 path.
internal sealed class HdrToSdrGpuConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID2D1Device _d2dDevice;
    private readonly ID2D1DeviceContext _context;
    private readonly ID2D1Effect _toneMap;
    private ID3D11Texture2D? _output;
    private int _width, _height;
    private bool _disposed;

    public HdrToSdrGpuConverter(ID3D11Device device, HdrCaptureCompatibility.DisplayProfile profile)
    {
        _device = device;
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        _d2dDevice = D2D1.D2D1CreateDevice(dxgiDevice);
        _context = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _toneMap = new ID2D1Effect(_context.CreateEffect(EffectGuids.HdrToneMap));
        SetFloat("InputMaxLuminance", Math.Max(80f, profile.PeakLuminanceNits));
        SetFloat("OutputMaxLuminance", 80f);
        SetInt("DisplayMode", (int)HDRToneMapDisplayMode.Sdr);
    }

    public ID3D11Texture2D Convert(ID3D11Texture2D source)
    {
        ThrowIfDisposed();
        var description = source.Description;
        if (description.Format != Format.R16G16B16A16_Float)
            throw new InvalidOperationException($"HDR conversion requires R16G16B16A16_FLOAT, got {description.Format}.");
        EnsureOutput((int)description.Width, (int)description.Height);
        using var sourceSurface = source.QueryInterface<IDXGISurface>();
        using var outputSurface = _output!.QueryInterface<IDXGISurface>();
        using var sourceBitmap = _context.CreateBitmapFromDxgiSurface(sourceSurface,
            // WGC's desktop surface has no meaningful alpha. Premultiplied
            // alpha treats its undefined/zero alpha as transparent black.
            new BitmapProperties1(new PixelFormat(Format.R16G16B16A16_Float, Vortice.DCommon.AlphaMode.Ignore), 96, 96, BitmapOptions.CannotDraw));
        using var outputBitmap = _context.CreateBitmapFromDxgiSurface(outputSurface,
            new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Ignore), 96, 96, BitmapOptions.Target));
        _toneMap.SetInput(0, sourceBitmap, true);
        _context.Target = outputBitmap;
        _context.BeginDraw();
        _context.DrawImage(_toneMap);
        _context.EndDraw();
        _context.Target = null;
        return _output;
    }

    private void EnsureOutput(int width, int height)
    {
        if (_output is not null && _width == width && _height == height) return;
        _output?.Dispose();
        _output = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });
        _width = width;
        _height = height;
    }

    private void SetFloat(string name, float value) => _toneMap.SetValueByName(name, PropertyType.Float, BitConverter.GetBytes(value), sizeof(float));
    private void SetInt(string name, int value) => _toneMap.SetValueByName(name, PropertyType.Enum, BitConverter.GetBytes(value), sizeof(int));
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(HdrToSdrGpuConverter)); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _output?.Dispose();
        _toneMap.Dispose();
        _context.Dispose();
        _d2dDevice.Dispose();
    }
}
