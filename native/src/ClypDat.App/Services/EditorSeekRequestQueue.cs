namespace ClypDat.App.Services;

internal sealed class EditorSeekRequestQueue
{
    private readonly object _sync = new();
    private TimeSpan? _preview;
    private bool _finalSeekPending;
    private long _finalSeekGeneration;
    private long _generation;

    public long QueuePreview(TimeSpan target)
    {
        lock (_sync)
        {
            if (_finalSeekPending) return _generation;
            _generation++;
            _preview = Normalize(target);
            return _generation;
        }
    }

    public long BeginFinalSeek()
    {
        lock (_sync)
        {
            _preview = null;
            _finalSeekPending = true;
            _generation++;
            _finalSeekGeneration = _generation;
            return _finalSeekGeneration;
        }
    }

    public void CompleteFinalSeek(long generation = 0)
    {
        lock (_sync)
        {
            if (generation == 0 || generation == _finalSeekGeneration) _finalSeekPending = false;
        }
    }

    public bool TryTakePreview(out TimeSpan target, out long generation)
    {
        lock (_sync)
        {
            if (_finalSeekPending || _preview is null)
            {
                target = default;
                generation = 0;
                return false;
            }

            target = _preview.Value;
            _preview = null;
            generation = _generation;
            return true;
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_sync) return !_finalSeekPending && generation == _generation;
    }

    internal static TimeSpan Normalize(TimeSpan target) =>
        TimeSpan.FromMilliseconds(Math.Max(0, (long)target.TotalMilliseconds));

    internal static bool ShouldResume(bool previousPlaying, bool seekSucceeded) =>
        previousPlaying && seekSucceeded;
}
