using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ClypDat.App.Services;

// Converts Windows scRGB capture directly into BGRA consumed by recorder.
internal sealed unsafe class HdrToSdrGpuConverter : IDisposable
{
    private const string Shader = """
        struct VsOut { float4 Position : SV_Position; };
        // Full-screen triangle (-1,-1), (-1,3), (3,-1): clockwise, so the
        // default back-face cull keeps it. The previous (3,3), (-1,3), (3,-1)
        // lay wholly outside the viewport and every converted frame was black.
        VsOut VS(uint id : SV_VertexID) {
            VsOut o; o.Position = float4(id == 2 ? 3 : -1, id == 1 ? 3 : -1, 0, 1); return o;
        }
        Texture2D<float4> Source : register(t0);
        float Srgb(float v) { return v <= 0.0031308 ? v * 12.92 : 1.055 * pow(v, 1.0 / 2.4) - 0.055; }
        float4 PS(VsOut input) : SV_Target {
            float3 rgb = max(Source.Load(int3(input.Position.xy, 0)).rgb, 0);
            rgb *= SCALE;
            // SDR content (<= reference white) passes through untouched, so it
            // matches an SDR capture. Only HDR highlights above white are
            // pulled back, by their brightest channel so the hue survives.
            float maximum = max(rgb.r, max(rgb.g, rgb.b));
            if (maximum > 1) rgb /= maximum;
            return float4(saturate(Srgb(rgb.r)), saturate(Srgb(rgb.g)), saturate(Srgb(rgb.b)), 1);
        }
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11DeviceContext _immediateContext;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private ID3D11Texture2D? _output;
    private int _width, _height;
    private bool _disposed;

    public HdrToSdrGpuConverter(ID3D11Device device, HdrCaptureCompatibility.DisplayProfile profile)
    {
        _device = device;
        _context = device.CreateDeferredContext();
        _immediateContext = device.ImmediateContext;
        var white = profile.SdrWhiteLevelNits is > 0 and < 10000 ? profile.SdrWhiteLevelNits : 80f;
        var source = Shader.Replace("SCALE", (80f / white).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        var vertexBytecode = Compile(source, "VS", "vs_5_0");
        var pixelBytecode = Compile(source, "PS", "ps_5_0");
        fixed (byte* bytes = vertexBytecode)
            _vertexShader = device.CreateVertexShader(bytes, (nuint)vertexBytecode.Length);
        fixed (byte* bytes = pixelBytecode)
            _pixelShader = device.CreatePixelShader(bytes, (nuint)pixelBytecode.Length);
    }

    public ID3D11Texture2D Convert(ID3D11Texture2D source)
    {
        ThrowIfDisposed();
        var description = source.Description;
        if (description.Format != Format.R16G16B16A16_Float)
            throw new InvalidOperationException($"HDR conversion requires R16G16B16A16_FLOAT, got {description.Format}.");
        EnsureOutput((int)description.Width, (int)description.Height);
        using var input = _device.CreateShaderResourceView(source);
        using var target = _device.CreateRenderTargetView(_output!);
        try
        {
            _context.OMSetRenderTargets(target);
            _context.RSSetViewports(new[] { new Viewport(0, 0, description.Width, description.Height) });
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vertexShader);
            _context.PSSetShader(_pixelShader);
            _context.PSSetShaderResource(0, input);
            _context.Draw(3, 0);
            _context.FinishCommandList(true, out var commandList);
            using (commandList) _immediateContext.ExecuteCommandList(commandList, true);
            return _output!;
        }
        finally
        {
            _context.PSSetShaderResource(0, null!);
            _context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        }
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
        _width = width; _height = height;
    }

    private static byte[] Compile(string source, string entryPoint, string profile)
    {
        var sourceBytes = System.Text.Encoding.UTF8.GetBytes(source);
        fixed (byte* sourcePointer = sourceBytes)
        {
            var hr = D3DCompile(sourcePointer, (nuint)sourceBytes.Length, null, 0, 0, entryPoint, profile, 0, 0, out var code, out var errors);
            try
            {
                if (hr < 0)
                {
                    var message = errors == 0 ? "unknown shader error" : Marshal.PtrToStringAnsi(GetBlobPointer(errors)) ?? "unknown shader error";
                    throw new InvalidOperationException($"HDR shader compilation failed: {message}");
                }
                var result = new byte[(int)GetBlobSize(code)];
                Marshal.Copy(GetBlobPointer(code), result, 0, result.Length);
                return result;
            }
            finally
            {
                if (code != 0) Marshal.Release(code);
                if (errors != 0) Marshal.Release(errors);
            }
        }
    }

    private static nint GetBlobPointer(nint blob) => Marshal.GetDelegateForFunctionPointer<GetBlobPointerDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(blob) + 3 * IntPtr.Size))(blob);
    private static nuint GetBlobSize(nint blob) => Marshal.GetDelegateForFunctionPointer<GetBlobSizeDelegate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(blob) + 4 * IntPtr.Size))(blob);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nint GetBlobPointerDelegate(nint blob);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate nuint GetBlobSizeDelegate(nint blob);
    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(byte* sourceData, nuint sourceDataSize, [MarshalAs(UnmanagedType.LPStr)] string? sourceName,
        nint defines, nint include, [MarshalAs(UnmanagedType.LPStr)] string entryPoint, [MarshalAs(UnmanagedType.LPStr)] string profile,
        uint flags1, uint flags2, out nint code, out nint errorMessages);

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(HdrToSdrGpuConverter)); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _output?.Dispose(); _pixelShader.Dispose(); _vertexShader.Dispose(); _context.Dispose();
    }
}
