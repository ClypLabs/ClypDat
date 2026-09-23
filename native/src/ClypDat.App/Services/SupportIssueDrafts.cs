using ClypDat.Core.Settings;

namespace ClypDat.App.Services;

internal static class SupportIssueDrafts
{
    internal static string Bug(string version, string build, string os, AppSettings? settings) => $"""
        ## Summary
        Describe the bug and how it affects your recording or clips.

        ## Steps to reproduce
        1. Open ClypDat and ...
        2. ...
        3. ...

        **How often?** Every time / Sometimes / Once
        **When did it start?** Include the last working version, if known.

        ## Expected result
        What should have happened?

        ## Actual result
        What happened instead? Include the exact error message, if any.

        ## Recording context
        - Game or application:
        - Capture mode: Game Capture / Desktop Capture
        - Recording type: Replay clip / Full Session
        - GPU and driver version:
        - Monitor resolution, refresh rate, and Windows HDR setting:

        ## Evidence and diagnostics
        - Screenshot or short example showing the problem:
        - Diagnostic report ID from **Help & support > Send diagnostics**:
        - Workarounds or troubleshooting already tried:

        Keep diagnostic ZIPs private; share the report ID here instead.

        ## App details
        | Setting | Value |
        | --- | --- |
        | ClypDat version | {version} |
        | Build | {build} |
        | Operating system | {os} |
        | Global recording quality | {(settings is null ? "Unknown" : $"{settings.ReplayMaxHeight}p / {settings.ReplayFrameRate} FPS / {settings.ReplayBitrateMbps} Mbps")} |
        | Video codec / encoder | {(settings is null ? "Unknown" : $"{settings.ReplayVideoCodec} / {settings.ReplayEncoderMode}")} |

        <!-- Per-game settings may override the global values above. Remove prompts that do not apply. -->
        """;

    internal static string Feature(string version, string build) => $"""
        ## Problem or missing capability
        What are you trying to do, and what gets in the way today?

        ## Proposed feature
        Describe the change and where it should appear in ClypDat.

        ## Example workflow
        1. The user starts by ...
        2. They ...
        3. ClypDat should ...

        ## Who would benefit?
        Describe the use case and how often you would use this.

        ## Alternatives considered
        What do you use now? Why does it not solve the problem?

        ## What would count as complete?
        - [ ] Describe an observable outcome.
        - [ ] Describe another outcome, if needed.

        ## Examples or mockups
        Add sketches, screenshots, or links that explain the idea. Mention any relevant limits or edge cases.

        ## App details
        | Setting | Value |
        | --- | --- |
        | ClypDat version | {version} |
        | Build | {build} |
        """;
}
