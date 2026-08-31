namespace ClypDat.App.Services;

internal static class TimelineSeekResumePolicy
{
    public static bool Resolve(bool requestedResume, bool inFlightSeekWantsResume) =>
        requestedResume || inFlightSeekWantsResume;

    public static bool ShouldContinueAfterSeekFailure(bool resumePlayback) => resumePlayback;
}
