using FFmpeg.AutoGen;

namespace ClypDat.App.Services;

// One retained pool reference, shared by capture recovery and pacing. Submission
// stays inside the resource lock so an encoder switch cannot overtake its clone.
internal sealed unsafe class RetainedHardwareFrame(object resourceGate, object nativeGate)
{
    private AVFrame* _frame;
    private nint _pool;
    internal bool HasFrame { get { lock (resourceGate) return _frame is not null; } }

    // Takes ownership, including when replacing the previous pool generation.
    internal void Replace(AVFrame* frame, nint pool)
    {
        lock (resourceGate)
        lock (nativeGate)
        {
            if (_frame is not null) { var old = _frame; _frame = null; ffmpeg.av_frame_free(&old); }
            _frame = frame;
            _pool = pool;
        }
    }

    internal bool CloneAndSubmit(nint pool, long pts, AVPictureType pictureType, Func<nint, bool> submit)
    {
        lock (resourceGate)
        {
            if (_frame is null || _pool != pool) return false;
            _frame->pts = pts;
            _frame->pict_type = pictureType;
            var clone = ffmpeg.av_frame_clone(_frame);
            if (clone is null) return false;
            // submit owns the clone on normal return, including rejection.
            try { return submit((nint)clone); }
            catch
            {
                lock (nativeGate) ffmpeg.av_frame_free(&clone);
                throw;
            }
        }
    }
}
