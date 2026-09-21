using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Media.Imaging;
using LibVLCSharp.Shared;

namespace ClypDat.App.Services;

/// <summary>Owns one editor output context. The plugin DLL remains loaded until process exit,
/// so VLC and the bridge share its registry even while players are being replaced.</summary>
internal sealed unsafe class NativeVideoOutput : IDisposable
{
    internal const uint Abi = 1;
    private const string CoreSha256 = "D3475B834DD3EB77910F37F71B0341D358BCBDDA5B9F04CC4A3A8E2BE1BC8E35";
    private static readonly Lazy<nint> Module = new(LoadModule);
    private readonly object _gate = new();
    private ulong _token;
    private ulong _generation = 1, _revision;
    private bool _seeking;
    private readonly Stopwatch _opened = Stopwatch.StartNew();
    private bool _wasAttached;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rectangle { public float X, Y, Width, Height; public Rectangle(Rect r) { X = (float)r.X; Y = (float)r.Y; Width = (float)r.Width; Height = (float)r.Height; } }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Blur { public Rectangle Bounds; public double Start, End; public float Sigma; public uint Shape; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Artwork { public ulong Id; public Rectangle Bounds; public double Start, End; public uint Layer, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Size, Version;
        public ulong Generation, Revision;
        public double MediaSeconds, Rate;
        public long ClockMicroseconds;
        public uint BlurCount, ArtworkCount;
        public Blur* Blurs;
        public Artwork* Artworks;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status
    {
        public uint Size, Version;
        public ulong Generation, Revision, DecodedPicture, PresentedPicture, Redraws;
        public uint Width, Height, Attached, Failed;
        public fixed byte Error[256];
        public string ErrorMessage { get { fixed (byte* p = Error) return Marshal.PtrToStringUTF8((nint)p) ?? "Video composition failed."; } }
    }
    private static nint Export(string name) => NativeLibrary.GetExport(Module.Value, name);
    private static readonly delegate* unmanaged[Cdecl]<uint, ulong> Create = (delegate* unmanaged[Cdecl]<uint, ulong>)Export("cdvo_create");
    private static readonly delegate* unmanaged[Cdecl]<ulong, nint, int> Bind = (delegate* unmanaged[Cdecl]<ulong, nint, int>)Export("cdvo_bind_player");
    private static readonly delegate* unmanaged[Cdecl]<ulong, State*, int> SubmitState = (delegate* unmanaged[Cdecl]<ulong, State*, int>)Export("cdvo_submit");
    private static readonly delegate* unmanaged[Cdecl]<ulong, ulong, ulong, uint, uint, uint, void*, int> Upload = (delegate* unmanaged[Cdecl]<ulong, ulong, ulong, uint, uint, uint, void*, int>)Export("cdvo_update_artwork");
    private static readonly delegate* unmanaged[Cdecl]<ulong, Status*, int> Query = (delegate* unmanaged[Cdecl]<ulong, Status*, int>)Export("cdvo_query");
    private static readonly delegate* unmanaged[Cdecl]<ulong, void> Release = (delegate* unmanaged[Cdecl]<ulong, void>)Export("cdvo_release");
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)] private static extern long libvlc_clock();
    internal static long ClockMicroseconds => libvlc_clock();
    internal bool UpdateScene(Action update) { lock (_gate) { if (_token == 0) return false; update(); return true; } }

    private static nint LoadModule()
    {
        using var process = Process.GetCurrentProcess();
        var core = process.Modules.Cast<ProcessModule>().SingleOrDefault(m => string.Equals(m.ModuleName, "libvlccore.dll", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("VLC must be initialized before the editor compositor.");
        using var file = File.OpenRead(core.FileName);
        if (Convert.ToHexString(SHA256.HashData(file)) != CoreSha256)
            throw new InvalidOperationException("Editor compositor requires bundled VLC 3.0.23.1. Reinstall ClypDat to restore its matching video runtime.");
        var path = Path.Combine(Path.GetDirectoryName(core.FileName)!, "plugins", "video_output", "libclypdat_d3d11_plugin.dll");
        return NativeLibrary.Load(path);
    }
    internal NativeVideoOutput() { _token = Create(Abi); if (_token == 0) throw new InvalidOperationException("Could not create the editor GPU compositor."); BeginSeek(TimeSpan.Zero); }
    internal ulong Generation { get { lock (_gate) return _generation; } }
    internal string MediaOption { get { lock (_gate) return $":clypdat-context={_token}"; } }
    internal void BindPlayer(MediaPlayer player) { lock (_gate) { if (_token != 0 && Bind(_token, player.NativeReference) == 0) throw new InvalidOperationException("Could not select the editor GPU compositor."); } }
    internal void BeginSeek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_token == 0) return;
            _seeking = true;
            _generation++; _revision = 0;
            var state = new State
            {
                Size = (uint)sizeof(State),
                Version = Abi,
                Generation = _generation,
                MediaSeconds = position.TotalSeconds,
                ClockMicroseconds = libvlc_clock()
            };
            if (SubmitState(_token, &state) == 0) throw new InvalidOperationException("Could not reset editor composition after seeking.");
        }
    }
    internal void EndSeek(TimeSpan position)
    {
        lock (_gate)
        {
            if (!_seeking || _token == 0) return;
            // BeginSeek already published this transport barrier. Completing it
            // must keep that generation: VLC can prepare the landing picture
            // before Avalonia publishes the complete scene.
            _seeking = false;
        }
    }
    internal void UpdateArtwork(ulong id, Bitmap bitmap)
    {
        var width = bitmap.PixelSize.Width; var height = bitmap.PixelSize.Height;
        var pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        fixed (byte* p = pixels)
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), (nint)p, pixels.Length, width * 4);
            if (bitmap.Format == Avalonia.Platform.PixelFormat.Rgba8888)
                for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            lock (_gate) if (_token != 0 && Upload(_token, _generation, id, (uint)width, (uint)height, (uint)(width * 4), p) == 0)
                throw new InvalidOperationException("Could not upload editor artwork to the GPU compositor.");
        }
    }
    internal void Submit(Blur[] blurs, Artwork[] artwork, TimeSpan position, double rate, long? anchorMicroseconds = null)
    {
        lock (_gate)
        {
            if (_token == 0) return;
            fixed (Blur* b = blurs) fixed (Artwork* a = artwork)
            {
                var state = new State
                {
                    Size = (uint)sizeof(State),
                    Version = Abi,
                    Generation = _generation,
                    Revision = _seeking ? 0 : ++_revision,
                    MediaSeconds = position.TotalSeconds,
                    Rate = rate,
                    ClockMicroseconds = anchorMicroseconds ?? libvlc_clock(),
                    BlurCount = (uint)blurs.Length,
                    ArtworkCount = (uint)artwork.Length,
                    Blurs = b,
                    Artworks = a
                };
                if (SubmitState(_token, &state) == 0) throw new InvalidOperationException("The GPU compositor rejected an editor update.");
            }
        }
    }
    internal Status ReadStatus()
    {
        lock (_gate)
        {
            var status = new Status { Size = (uint)sizeof(Status), Version = Abi };
            if (_token == 0 || Query(_token, &status) == 0) throw new InvalidOperationException("The editor GPU compositor is unavailable.");
            _wasAttached |= status.Attached != 0;
            if (status.Failed != 0) throw new InvalidOperationException(status.ErrorMessage);
            if (!_wasAttached && _opened.Elapsed > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("The editor GPU compositor did not initialize. Reopen the clip; reinstall ClypDat if this persists.");
            return status;
        }
    }
    internal bool TryReadStatus(out Status status)
    {
        lock (_gate)
        {
            var snapshot = new Status { Size = (uint)sizeof(Status), Version = Abi };
            var available = _token != 0 && Query(_token, &snapshot) != 0;
            status = snapshot;
            return available;
        }
    }
    internal bool HasPresentedPicture
    {
        get
        {
            lock (_gate)
            {
                var status = new Status { Size = (uint)sizeof(Status), Version = Abi };
                return _token != 0 && Query(_token, &status) != 0 && status.Failed == 0 &&
                    status.Attached != 0 && status.Generation == _generation && status.PresentedPicture != 0;
            }
        }
    }
    public void Dispose() { lock (_gate) { if (_token == 0) return; Release(_token); _token = 0; } }
}
