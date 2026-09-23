using System.Buffers;

namespace ClypDat.App.Services;

internal interface ICameraPreviewService : IDisposable
{
    event Action<CameraPreviewFrame>? FrameReady;
    event Action<CameraPreviewFailure>? Failed;
    bool IsRunning { get; }
    int Session { get; }
    void Start(string deviceMoniker);
    void Stop();
}
internal sealed class CameraPreviewFrame : IDisposable
{
    private byte[]? _pixels;
    public CameraPreviewFrame(int session, byte[] pixels, long sequence = 0, long timestamp = 0) { Session = session; _pixels = pixels; Sequence = sequence; Timestamp = timestamp; }
    public int Session { get; }
    public long Sequence { get; }
    public long Timestamp { get; }
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(CameraPreviewFrame));
    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels);
    }
}

internal sealed record CameraPreviewFailure(int Session, string Message);
internal sealed record CameraPreviewMode(int Width, int Height, double FramesPerSecond, string Format, bool IsCompressed)
{
    public override string ToString() => $"{Width}x{Height} {Format} {FramesPerSecond:0.###} FPS";
}
