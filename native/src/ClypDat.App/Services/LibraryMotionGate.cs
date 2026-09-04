namespace ClypDat.App.Services;

internal sealed class LibraryMotionGate
{
    private int _resumedGeneration = -1;

    internal int Generation { get; private set; }
    internal bool IsActive { get; private set; }

    internal int Begin()
    {
        IsActive = true;
        return ++Generation;
    }

    internal bool TrySettle(int generation, bool candidateCanResume)
    {
        if (!IsActive || generation != Generation) return false;
        IsActive = false;
        if (!candidateCanResume || _resumedGeneration == generation) return false;
        _resumedGeneration = generation;
        return true;
    }
}
