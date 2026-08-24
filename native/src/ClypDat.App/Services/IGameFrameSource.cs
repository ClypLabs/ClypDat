using Vortice.Direct3D11;

namespace ClypDat.App.Services;

// The replay pipeline only consumes an owned D3D11 texture. WGC and the DX11
// hook can change over without exposing transport details to scaling or encode.
internal interface IGameFrameSource
{
    string CaptureMode { get; }
    string? Failure { get; }
    bool WaitAndTakeLatestTexture(TimeSpan timeout, CancellationToken cancellationToken, out ID3D11Texture2D? texture);
}
