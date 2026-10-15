using System.Buffers.Binary;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClipPeaksSidecarTests
{
    // 2.5 ms slices at 48 kHz, as recording_save.cpp writes them.
    private static byte[] Blob(params (int Stream, byte[] Slices)[] streams)
    {
        var bytes = new List<byte>("CDPK"u8.ToArray());
        void U32(uint value) { var buffer = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(buffer, value); bytes.AddRange(buffer); }
        U32(1); U32(120); U32(48000); U32((uint)streams.Length);
        foreach (var (stream, slices) in streams) { U32((uint)stream); U32((uint)slices.Length); bytes.AddRange(slices); }
        return bytes.ToArray();
    }

    [Fact]
    public void Parse_ResamplesEachStreamOntoTheBucketGrid()
    {
        // One second: 400 slices. A full-scale spike in slice 100 only.
        var game = new byte[400]; game[100] = 255;
        var mic = Enumerable.Repeat((byte)51, 400).ToArray();

        var peaks = ClipPeaksSidecar.Parse(Blob((1, game), (2, mic)), [1, 2], TimeSpan.FromSeconds(1), 4);

        Assert.NotNull(peaks);
        Assert.Equal([0d, 1d, 0d, 0d], peaks![1]);
        Assert.All(peaks[2], value => Assert.Equal(0.2, value, 3));
    }

    [Fact]
    public void Parse_RejectsPeaksForADifferentLength()
    {
        // A trim saved over the clip: its old 1 s of peaks against 0.5 s of media.
        Assert.Null(ClipPeaksSidecar.Parse(Blob((1, new byte[400])), [1], TimeSpan.FromSeconds(0.4), 8));
    }

    [Fact]
    public void Parse_RejectsWhenARequestedStreamIsMissing()
    {
        Assert.Null(ClipPeaksSidecar.Parse(Blob((1, new byte[400])), [1, 3], TimeSpan.FromSeconds(1), 8));
    }

    [Fact]
    public void Parse_RejectsForeignOrTruncatedData()
    {
        Assert.Null(ClipPeaksSidecar.Parse("NOPE0000000000000000"u8, [1], TimeSpan.FromSeconds(1), 8));
        var truncated = Blob((1, new byte[400]))[..30];
        Assert.Null(ClipPeaksSidecar.Parse(truncated, [1], TimeSpan.FromSeconds(1), 8));
    }
}
