using System.Runtime.InteropServices;
using Avalonia;
namespace ClypDat.App.Services;

internal static unsafe class EditorVideoModels
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rectangle { public float X, Y, Width, Height; public Rectangle(Rect r) { X = (float)r.X; Y = (float)r.Y; Width = (float)r.Width; Height = (float)r.Height; } }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Blur { public Rectangle Bounds; public double Start, End; public float Sigma; public uint Shape; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Artwork { public ulong Id; public Rectangle Bounds; public double Start, End; public uint Layer, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Status
    {
        public uint Size, Version;
        public ulong Generation, Revision, DecodedPicture, PresentedPicture, Redraws;
        public uint Width, Height, Attached, Failed;
        public fixed byte Error[256];
        public string ErrorMessage { get { fixed (byte* p = Error) return Marshal.PtrToStringUTF8((nint)p) ?? "Video composition failed."; } }
    }
}
