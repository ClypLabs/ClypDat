using Vortice.Direct3D11;

namespace ClypDat.App.Services;

// The replay pipeline only consumes an owned D3D11 texture. WGC and the DX11
// hook can change over without exposing transport details to scaling or encode.
internal interface IGameFrameSource
{
    string CaptureMode { get; }
    string? Failure { get; }
    bool WaitAndTakeLatestFrame(TimeSpan timeout, CancellationToken cancellationToken, out GameFrameLease? frame);
}

// Source owns transport synchronization. Lease survives crop/scale, preventing
// producer overwrite while consumer reads pixels.
internal abstract class GameFrameLease : IDisposable
{
    public abstract ID3D11Texture2D Texture { get; }
    public abstract long SourceTimestamp { get; }
    public abstract long AccumulatedPresents { get; }
    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract long Generation { get; }
    // A desktop image update and a pointer update are separate DXGI events.
    // Other sources deliver composed frames, so their existing behavior is a
    // desktop-content update by default.
    public virtual bool HasDesktopContentUpdate => true;
    public virtual bool HasPointerUpdate => false;
    public virtual long ContentTimestamp => SourceTimestamp;
    public abstract void Dispose();
}
