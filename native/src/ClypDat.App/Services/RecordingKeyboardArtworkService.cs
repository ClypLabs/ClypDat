using ClypDat.Capture.Abstractions;
using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

/// <summary>Called by the native adapter's control task on input, layout, or
/// settings changes. Contains no capture, timer, frame callback, or compositor.</summary>
internal sealed class RecordingKeyboardArtworkService : IDisposable
{
    private RecorderKeyboardRasterizer _rasterizer = new();
    private OverlayCaptureSettings? _settings;
    private long _inputRevision = -1;
    private int _frameWidth, _frameHeight;
    private ulong _artworkRevision;

    public RecordingKeyboardArtwork? Update(OverlayCaptureSettings settings,
        IReadOnlyList<InputPhysicalKey> pressed, long inputRevision, int frameWidth,
        int frameHeight, long timestampMicroseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameHeight);
        if (Equals(settings, _settings) && inputRevision == _inputRevision &&
            frameWidth == _frameWidth && frameHeight == _frameHeight) return null;

        if (!Equals(settings, _settings))
        {
            _rasterizer.Dispose();
            _rasterizer = new();
        }
        _settings = settings;
        _inputRevision = inputRevision;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        if (string.Equals(settings.KeyboardLayout, "None", StringComparison.OrdinalIgnoreCase)) return null;

        var board = Board(settings);
        var aspect = board is null
            ? KeyboardOverlayCatalog.Get(settings.KeyboardLayout).AspectRatio
            : CustomKeyboardBoard.AspectRatio(board);
        var width = Math.Clamp((int)Math.Round(settings.KeyboardTransform.Width * frameWidth), 1, frameWidth);
        var height = Math.Clamp((int)Math.Round(width / Math.Max(.01, aspect)), 1, frameHeight);
        var down = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in pressed)
        {
            var code = key.MouseButton ?? InputKeyMap.Code(key.ScanCode, key.E0);
            if (code is not null) down.Add(code);
        }
        // The rasterizer caches its buffer. Ownership crosses the ABI only by
        // copying; callers cannot observe later rendering mutate this revision.
        var pixels = _rasterizer.Render(settings.KeyboardLayout, board, width, height, down).ToArray();
        return new(++_artworkRevision, timestampMicroseconds, width, height, width * 4, pixels);
    }

    private static CustomKeyboardBoardShape? Board(OverlayCaptureSettings settings)
    {
        if (settings.KeyboardKeys is not { Count: > 0 } caps) return null;
        IReadOnlyList<CustomKeyCap> Row(int row) => caps.Where(cap => cap.Row == row)
            .Select(cap => new CustomKeyCap(cap.Code, cap.Label, cap.Units)).ToArray();
        return new(caps.Where(cap => cap.Row >= 0).Select(cap => cap.Row).Distinct().Order().Select(Row).ToArray(),
            caps.Where(cap => cap.Row < 0).Select(cap => cap.Row).Distinct().OrderDescending().Select(Row).ToArray(),
            settings.KeyboardShowMouse);
    }

    public void Dispose() => _rasterizer.Dispose();
}

internal sealed record RecordingKeyboardArtwork(ulong Revision, long TimestampMicroseconds,
    int Width, int Height, int Stride, byte[] PremultipliedBgra);
