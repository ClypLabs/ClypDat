using ClypDat.App.Services;
using FFmpeg.AutoGen;
using Xunit;

namespace ClypDat.App.Tests;

public sealed unsafe class RetainedHardwareFrameTests
{
    [Fact]
    public void FailedSubmissionReleasesCloneAndKeepsRetainedFrame()
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        var retained = new RetainedHardwareFrame(new object(), new object());
        var frame = MakeFrame(17);
        retained.Replace(frame, 1);
        try
        {
            Assert.Throws<InvalidOperationException>((Action)(() =>
                retained.CloneAndSubmit(1, 123, AVPictureType.AV_PICTURE_TYPE_NONE,
                    _ => throw new InvalidOperationException("queue closed"))));
            Assert.Equal(1, ffmpeg.av_buffer_get_ref_count(frame->buf[0]));
        }
        finally { retained.Replace(null, 0); }
    }

    [Fact]
    public void RecoveryCannotFreeRetainedFrameOrOvertakeItsSubmission()
    {
        FfmpegPathResolver.EnsureBundledFfmpeg();
        var retained = new RetainedHardwareFrame(new object(), new object());
        var original = MakeFrame(17);
        retained.Replace(original, 1);
        using var attemptingSwap = new ManualResetEventSlim();
        using var swapped = new ManualResetEventSlim();
        Thread? recovery = null;
        nint outgoing = 0;
        try
        {
            Assert.True(retained.CloneAndSubmit(1, 123, AVPictureType.AV_PICTURE_TYPE_I, pointer =>
            {
                outgoing = pointer;
                recovery = new Thread(() =>
                {
                    attemptingSwap.Set();
                    retained.Replace(MakeFrame(29), 2);
                    swapped.Set();
                }) { IsBackground = true };
                recovery.Start();
                Assert.True(attemptingSwap.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(swapped.Wait(TimeSpan.FromMilliseconds(100)));
                return true;
            }));
            Assert.True(recovery!.Join(TimeSpan.FromSeconds(5)));
            Assert.False(retained.CloneAndSubmit(1, 124, AVPictureType.AV_PICTURE_TYPE_NONE, _ => throw new Exception("stale pool submitted")));
            var clone = (AVFrame*)outgoing;
            Assert.Equal(17, clone->data[0][0]);
            Assert.Equal(123, clone->pts);
            Assert.True(retained.CloneAndSubmit(2, 125, AVPictureType.AV_PICTURE_TYPE_NONE, pointer =>
            {
                var next = (AVFrame*)pointer;
                try { Assert.Equal(29, next->data[0][0]); return true; }
                finally { ffmpeg.av_frame_free(&next); }
            }));
        }
        finally
        {
            recovery?.Join(TimeSpan.FromSeconds(5));
            retained.Replace(null, 0);
            var clone = (AVFrame*)outgoing;
            if (clone is not null) ffmpeg.av_frame_free(&clone);
        }
    }

    private static AVFrame* MakeFrame(byte value)
    {
        var frame = ffmpeg.av_frame_alloc();
        frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        frame->width = frame->height = 32;
        Assert.True(ffmpeg.av_frame_get_buffer(frame, 32) >= 0);
        frame->data[0][0] = value;
        return frame;
    }
}
