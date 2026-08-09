namespace ClypDat.App.Services;

// Hardware encoders can accept a frame before they emit its packet. Keep the
// submitted AVFrame alive until that packet is drained: the capture side then
// gets copy-on-write storage before it writes the next frame.
internal readonly record struct PendingEncodeFrame(nint FramePointer, DateTime WallClockUtc);

internal sealed class EncoderFrameLifetimeQueue
{
    private readonly Queue<PendingEncodeFrame> _frames = new();
    private readonly Action<nint> _release;

    public EncoderFrameLifetimeQueue(Action<nint> release) => _release = release;

    public int Count => _frames.Count;
    public int PeakCount { get; private set; }

    public void Enqueue(nint framePointer, DateTime wallClockUtc)
    {
        _frames.Enqueue(new PendingEncodeFrame(framePointer, wallClockUtc));
        PeakCount = Math.Max(PeakCount, _frames.Count);
    }

    public bool TryTake(out PendingEncodeFrame frame) => _frames.TryDequeue(out frame);

    public void Release(PendingEncodeFrame frame) => _release(frame.FramePointer);

    public void ReleaseAll()
    {
        while (TryTake(out var frame)) Release(frame);
    }
}
