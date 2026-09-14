using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using LibVLCSharp.Shared;

namespace ClypDat.App.Services;

internal interface IEditorVideoOutput : IDisposable
{
    ulong Generation { get; }
    string MediaOption { get; }
    bool HasPresentedPicture { get; }
    void BindPlayer(MediaPlayer player);
    void BeginSeek(TimeSpan position);
    void EndSeek(TimeSpan position);
    void UpdateScene(Action update);
    void UpdateArtwork(ulong id, Bitmap bitmap);
    void Submit(EditorVideoModels.Blur[] blurs, EditorVideoModels.Artwork[] artwork, TimeSpan position, double rate, long? anchorMicroseconds = null);
    EditorVideoModels.Status ReadStatus();
}

internal static class EditorVideoClock
{
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)] private static extern long libvlc_clock();
    internal static long Microseconds => libvlc_clock();
}
