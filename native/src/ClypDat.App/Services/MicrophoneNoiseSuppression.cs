using System.Runtime.Versioning;
using NAudio.Wave;

namespace ClypDat.App.Services;

// Shared limits and the single "should this microphone be filtered?" decision,
// so the capture path, the settings view model and the mic test meter cannot
// drift apart on what the slider range means or when the feature is available.
[SupportedOSPlatform("windows")]
internal static class MicrophoneNoiseSuppression
{
    // At the floor the gate is off, not merely very quiet: BuildFilterChain
    // leaves the agate stage out of the graph entirely rather than adding one
    // whose threshold can never be crossed.
    public const double MinimumGateThresholdDb = -100;
    public const double MaximumGateThresholdDb = -25;
    public const double DefaultGateThresholdDb = MinimumGateThresholdDb;

    public static double ClampGateThresholdDb(double value)
    {
        if (!double.IsFinite(value)) return DefaultGateThresholdDb;
        return Math.Clamp(value, MinimumGateThresholdDb, MaximumGateThresholdDb);
    }

    /// <summary>
    /// Wraps a microphone capture in the denoising stage when the user asked
    /// for it and it can actually run. Returns the capture unchanged otherwise -
    /// every failure mode here degrades to a clean, unfiltered microphone
    /// rather than to no microphone at all.
    /// </summary>
    public static IWaveIn Wrap(IWaveIn capture, bool enabled, double gateThresholdDb, string deviceName)
    {
        if (!enabled) return capture;

        if (FfmpegPathResolver.RnnoiseModelPath.Length == 0)
        {
            AppLog.Info($"Mic noise suppression skipped for '{deviceName}': RNNoise model not bundled.");
            return capture;
        }

        if (DenoisingWaveIn.IsDisabledForSession)
        {
            return capture;
        }

        if (!DenoisingWaveIn.CanWrap(capture.WaveFormat))
        {
            AppLog.Info(
                $"Mic noise suppression skipped for '{deviceName}': unsupported capture format " +
                $"({capture.WaveFormat.Encoding}, {capture.WaveFormat.BitsPerSample}-bit, {capture.WaveFormat.Channels}ch).");
            return capture;
        }

        try
        {
            return new DenoisingWaveIn(capture, ClampGateThresholdDb(gateThresholdDb));
        }
        catch (Exception error)
        {
            AppLog.Error($"Mic noise suppression could not start for '{deviceName}'; recording unfiltered.", error);
            return capture;
        }
    }
}
