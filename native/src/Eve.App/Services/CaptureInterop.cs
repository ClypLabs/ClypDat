using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace Eve.App.Services;

// Windows.Graphics.Capture has no public "give me an item for this HWND/HMONITOR"
// constructor - the factory that does this (IGraphicsCaptureItemInterop) is a classic
// COM interface reachable only via RoGetActivationFactory, not through the normal WinRT
// projection. This is the standard, well-established pattern for using WGC from a
// desktop (non-UWP) app.
//
// Calls go through raw vtable function pointers rather than a [ComImport] interface -
// casting a Marshal.GetObjectForIUnknown-wrapped pointer to a ComImport interface here
// threw InvalidCastException at the first method call (a known fragility of classic COM
// interop dispatch in this configuration); direct vtable calls sidestep it entirely.
[SupportedOSPlatform("windows10.0.17763.0")]
internal static unsafe class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid Direct3DDxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    // The documented IID of Windows.Graphics.Capture.IGraphicsCaptureItem itself -
    // typeof(GraphicsCaptureItem).GUID does NOT reliably match what the native
    // CreateForWindow/CreateForMonitor factory expects as riid (returns E_NOINTERFACE).
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void RoGetActivationFactory(IntPtr activatableClassId, [In] ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern void WindowsDeleteString(IntPtr hstring);

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var factory = GetActivationFactoryPointer("Windows.Graphics.Capture.GraphicsCaptureItem", GraphicsCaptureItemInteropIid);
        try
        {
            // IGraphicsCaptureItemInterop vtable: 0=QueryInterface, 1=AddRef, 2=Release,
            // 3=CreateForWindow(HWND, REFIID, void**), 4=CreateForMonitor(HMONITOR, REFIID, void**).
            return InvokeCreate(factory, slot: 3, hwnd);
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor)
    {
        var factory = GetActivationFactoryPointer("Windows.Graphics.Capture.GraphicsCaptureItem", GraphicsCaptureItemInteropIid);
        try
        {
            return InvokeCreate(factory, slot: 4, hmonitor);
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    public static IDirect3DDevice CreateDirect3DDevice(Vortice.Direct3D11.ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<Vortice.DXGI.IDXGIDevice>();
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var winrtPointer);
        return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(winrtPointer);
    }

    public static Vortice.Direct3D11.ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var surfacePointer = WinRT.MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var accessIid = Direct3DDxgiInterfaceAccessIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePointer, ref accessIid, out var accessPointer));
            try
            {
                // IDirect3DDxgiInterfaceAccess vtable: 0=QueryInterface, 1=AddRef, 2=Release,
                // 3=GetInterface(REFIID, void**).
                var vtbl = *(IntPtr**)accessPointer;
                var fn = (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)vtbl[3];
                var textureIid = typeof(Vortice.Direct3D11.ID3D11Texture2D).GUID;
                IntPtr texturePointer;
                var hr = fn(accessPointer, &textureIid, &texturePointer);
                Marshal.ThrowExceptionForHR(hr);
                return new Vortice.Direct3D11.ID3D11Texture2D(texturePointer);
            }
            finally
            {
                Marshal.Release(accessPointer);
            }
        }
        finally
        {
            Marshal.Release(surfacePointer);
        }
    }

    private static GraphicsCaptureItem InvokeCreate(IntPtr factory, int slot, IntPtr handle)
    {
        var vtbl = *(IntPtr**)factory;
        var fn = (delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtbl[slot];
        var itemIid = GraphicsCaptureItemIid;
        IntPtr itemPointer;
        var hr = fn(factory, handle, &itemIid, &itemPointer);
        Marshal.ThrowExceptionForHR(hr);
        return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
    }

    private static IntPtr GetActivationFactoryPointer(string classId, Guid iid)
    {
        WindowsCreateString(classId, classId.Length, out var hstring);
        try
        {
            RoGetActivationFactory(hstring, ref iid, out var factory);
            return factory;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }
}
