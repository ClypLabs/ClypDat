using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using LibVLCSharp.Shared;
using SkiaSharp;

namespace ClypDat.App.Services;

internal sealed class LinuxEditorVideoOutput : IEditorVideoOutput
{
    private static readonly ConditionalWeakTable<MediaPlayer, LinuxEditorVideoOutput> Players = new();
    internal static LinuxEditorVideoOutput? For(MediaPlayer? player) => player is not null && Players.TryGetValue(player, out var output) ? output : null;
    private readonly object _gate = new();
    private readonly MediaPlayer.LibVLCVideoLockCb _lock;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;
    private readonly MediaPlayer.LibVLCVideoFormatCb _format;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    private Frame[] _frames = [];
    private Frame? _latest;
    private ulong _generation = 1, _decoded, _presented;
    private bool _disposed;
    private readonly Stopwatch _performance = Stopwatch.StartNew();
    private ulong _previousDecoded, _previousPresented;
    private MediaPlayer? _player;
    private EditorVideoModels.Blur[] _blurs = [];
    private EditorVideoModels.Artwork[] _artworks = [];
    private readonly Dictionary<ulong, SKBitmap> _images = [];
    private double _position;
    private readonly uint _visibleWidth, _visibleHeight;
    internal LinuxEditorVideoOutput(uint visibleWidth = 0, uint visibleHeight = 0)
    {
        _visibleWidth = visibleWidth; _visibleHeight = visibleHeight;
        _lock = Lock; _unlock = Unlock; _display = Display;
        _format = Format; _cleanup = (ref nint _) => Cleanup();
    }
    internal sealed class Frame
    {
        internal nint Pixels;
        internal int Width, Height, Pitch, Leases;
        internal bool Decoding, Retired;
        internal ulong Generation;
        internal void FreeIfUnused() { if (Retired && Leases == 0 && !Decoding && Pixels != 0) { Marshal.FreeHGlobal(Pixels); Pixels = 0; } }
    }
    internal string DebugState { get { lock (_gate) return $"generation={_generation}, decoded={_decoded}, latest={_latest?.Generation}, buffers=" + string.Join(';', _frames.Select(f => $"{f.Generation}/{f.Decoding}/{f.Leases}")); } }
    public ulong Generation { get { lock (_gate) return _generation; } }
    public string MediaOption => ":vout=vmem";
    public bool HasPresentedPicture { get { lock (_gate) return _latest is not null && _latest.Generation == _generation; } }
    public void BindPlayer(MediaPlayer player)
    {
        lock (_gate)
        {
            if (_player == player) return;
            _player = player; Players.Remove(player); Players.Add(player, this);
            player.SetVideoCallbacks(_lock, _unlock, _display);
            player.SetVideoFormatCallbacks(_format, _cleanup);
        }
    }
    private uint Format(ref nint opaque, nint chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        try
        {
            if (_visibleWidth > 0 && _visibleHeight > 0 && width >= _visibleWidth && width - _visibleWidth < 32 && height >= _visibleHeight && height - _visibleHeight < 32)
                { width = _visibleWidth; height = _visibleHeight; }
            if (width == 0 || height == 0 || width > 8192 || height > 8192) return 0;
            Marshal.Copy(new byte[] { 82, 86, 51, 50 }, 0, chroma, 4); // RV32, native little-endian BGRX
            pitches = (width * 4 + 31) & ~31u; lines = height;
            lock (_gate)
            {
                Cleanup();
                var w = (int)width; var h = (int)height; var pitch = (int)pitches;
                _frames = Enumerable.Range(0, 3).Select(_ => new Frame { Width = w, Height = h, Pitch = pitch,
                    Pixels = Marshal.AllocHGlobal(checked(pitch * h)) }).ToArray();
            }
            return 3;
        }
        catch { return 0; } // exceptions must never cross a native callback
    }
    private nint Lock(nint opaque, nint planes)
    {
        lock (_gate)
        {
            while (!_disposed)
            {
                var frame = _frames.FirstOrDefault(f => !f.Decoding && f.Leases == 0 && f != _latest);
                if (frame is not null)
                {
                    frame.Decoding = true; frame.Generation = _generation;
                    Marshal.WriteIntPtr(planes, frame.Pixels);
                    return frame.Pixels;
                }
                Monitor.Wait(_gate, 20);
            }
            return 0;
        }
    }
    private void Unlock(nint opaque, nint picture, nint planes)
    {
        lock (_gate) {
            var frame = _frames.FirstOrDefault(f => f.Pixels == picture);
            if (frame is not null) { frame.Decoding = false; frame.FreeIfUnused(); }
            // vmem serializes Prepare/Display. A prepared picture may be discarded
            // during seeking without Display; the next Prepare can reuse it.
            Monitor.PulseAll(_gate);
        }
    }
    private void Display(nint opaque, nint picture)
    {
        lock (_gate)
        {
            var frame = _frames.FirstOrDefault(f => f.Pixels == picture);
            if (frame is null) return;
            frame.Decoding = false; _decoded++;
            if (!_disposed && frame.Generation == _generation) _latest = frame;
            frame.FreeIfUnused(); Monitor.PulseAll(_gate);
        }
    }
    private void Cleanup()
    {
        lock (_gate)
        {
            _latest = null;
            foreach (var frame in _frames) { frame.Retired = true; frame.Decoding = false; frame.FreeIfUnused(); }
            _frames = []; Monitor.PulseAll(_gate);
        }
    }
    public void BeginSeek(TimeSpan position) { lock (_gate) { _generation++; _latest = null; _position = position.TotalSeconds; } }
    // Keep the confirmed paused-seek frame; dropping it requires playback to produce another frame.
    public void EndSeek(TimeSpan position) { lock (_gate) _position = position.TotalSeconds; }
    public void UpdateScene(Action update) { lock (_gate) if (!_disposed) update(); }
    public void UpdateArtwork(ulong id, Bitmap bitmap)
    {
        using var stream = new MemoryStream(); bitmap.Save(stream); stream.Position = 0;
        var image = SKBitmap.Decode(stream);
        lock (_gate) { if (_images.Remove(id, out var old)) old.Dispose(); _images[id] = image; }
    }
    public void Submit(EditorVideoModels.Blur[] blurs, EditorVideoModels.Artwork[] artwork, TimeSpan position, double rate, long? anchorMicroseconds = null)
    { lock (_gate) { _blurs = blurs; _artworks = artwork; _position = position.TotalSeconds; } }
    public EditorVideoModels.Status ReadStatus()
    {
        lock (_gate) return new() { Generation = _generation, DecodedPicture = _decoded, PresentedPicture = _presented,
            Width = (uint)(_latest?.Width ?? 0), Height = (uint)(_latest?.Height ?? 0), Attached = _player is null ? 0u : 1u };
    }
    internal Frame? Acquire()
    {
        lock (_gate) { if (_latest is not { } frame) return null; frame.Leases++; return frame; }
    }
    internal void Release(Frame frame) { lock (_gate) { frame.Leases--; frame.FreeIfUnused(); Monitor.PulseAll(_gate); } }
    internal void Draw(SKCanvas canvas, Frame frame, Rect bounds)
    {
        lock (_gate)
        {
            if (_disposed || frame.Generation != _generation || frame.Pixels == 0) return;
            using var image = new SKBitmap();
            image.InstallPixels(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque), frame.Pixels, frame.Pitch);
            var target = new SKRect((float)bounds.X, (float)bounds.Y, (float)bounds.Right, (float)bounds.Bottom);
            canvas.DrawBitmap(image, target);
            foreach (var blur in _blurs.Where(b => _position >= b.Start && _position <= b.End))
            {
                var r = Bounds(blur.Bounds, target);
                canvas.Save();
                if (blur.Shape == 2) { using var path = new SKPath(); path.AddOval(r); canvas.ClipPath(path); }
                else if (blur.Shape == 1) canvas.ClipRoundRect(new SKRoundRect(r, r.Height * .1f));
                else canvas.ClipRect(r);
                using var filter = SKImageFilter.CreateBlur(blur.Sigma, blur.Sigma);
                using var paint = new SKPaint { ImageFilter = filter };
                canvas.DrawBitmap(image, target, paint); canvas.Restore();
            }
            foreach (var artwork in _artworks.OrderBy(a => a.Layer).Where(a => _position >= a.Start && _position <= a.End))
                if (_images.TryGetValue(artwork.Id, out var bitmap)) canvas.DrawBitmap(bitmap, Bounds(artwork.Bounds, target));
            _presented++;
            if (_performance.Elapsed.TotalSeconds >= 2) {
                var seconds = _performance.Elapsed.TotalSeconds;
                AppLog.Debug($"Linux editor: decoded={(_decoded - _previousDecoded) / seconds:0.0} FPS, drawn={(_presented - _previousPresented) / seconds:0.0} FPS, leased={_frames.Sum(f => f.Leases)}.");
                _previousDecoded = _decoded; _previousPresented = _presented; _performance.Restart();
            }
        }
    }
    private static SKRect Bounds(EditorVideoModels.Rectangle r, SKRect target) => new(target.Left + r.X * target.Width, target.Top + r.Y * target.Height,
        target.Left + (r.X + r.Width) * target.Width, target.Top + (r.Y + r.Height) * target.Height);
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true; if (_player is not null) Players.Remove(_player);
            Cleanup(); foreach (var image in _images.Values) image.Dispose(); _images.Clear(); Monitor.PulseAll(_gate);
        }
    }
}
