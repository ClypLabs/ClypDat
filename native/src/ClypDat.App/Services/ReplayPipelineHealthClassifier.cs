using ClypDat.Capture.Abstractions;

namespace ClypDat.App.Services;

internal static class ReplayPipelineHealthClassifier
{
    internal static ReplayPipelineStage Classify(double sourceFps, double transportFps, double processingP95Ms, double frameBudgetMs, long pacingMisses, int queueDepth, int queueCapacity, double submissionP95Ms, double outputLatencyP95Ms, bool outputBelowTarget)
    {
        if (sourceFps <= 0) return ReplayPipelineStage.SourceAcquisition;
        if (transportFps > 0 && transportFps < sourceFps * .95) return ReplayPipelineStage.CaptureTransport;
        if (processingP95Ms > frameBudgetMs) return ReplayPipelineStage.FrameProcessing;
        if (pacingMisses > 0 && queueDepth < Math.Max(1, queueCapacity / 2) && submissionP95Ms <= frameBudgetMs) return ReplayPipelineStage.Pacing;
        if (submissionP95Ms > frameBudgetMs * 4) return ReplayPipelineStage.EncoderSubmission;
        if (outputBelowTarget && outputLatencyP95Ms > frameBudgetMs * 2) return ReplayPipelineStage.EncoderCompletion;
        if (queueDepth * 2 >= queueCapacity) return ReplayPipelineStage.EncodeQueue;
        return ReplayPipelineStage.None;
    }

    internal static ReplayDegradeReason ToDegradeReason(ReplayPipelineStage stage) => stage switch
    {
        ReplayPipelineStage.SourceAcquisition => ReplayDegradeReason.CaptureStall,
        ReplayPipelineStage.CaptureTransport => ReplayDegradeReason.CaptureTransport,
        ReplayPipelineStage.EncodeQueue or ReplayPipelineStage.EncoderSubmission or ReplayPipelineStage.EncoderCompletion => ReplayDegradeReason.EncoderOverload,
        _ => ReplayDegradeReason.None
    };
}
