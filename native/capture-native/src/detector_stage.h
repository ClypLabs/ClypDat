#pragma once
#include "recording_capture.h"
#include <array>
#include <cstdint>
#include <functional>
#include <memory>
struct SwsContext;

namespace clypdat {
// What the auto-clip detector samples: three regions normalized to the output
// canvas, and the counter mask of the third.
struct DetectorRequest {
    int width = 0, height = 0;
    std::array<CaptureNormalizedRect, 3> regions{};
    bool counter_mask = false;
};
// Cumulative. Resources are allocated only when the stage (re)builds for a
// new source size, format, device, canvas or region layout.
struct DetectorStageCounters {
    uint64_t samples = 0, cancelled = 0, fallbacks = 0;
    uint64_t gpu_textures_allocated = 0, cpu_buffers_allocated = 0, staging_rebuilds = 0;
    // Allocations by rebuilds after the first build.
    uint64_t allocations_after_warmup = 0;
    uint64_t readback_bytes = 0;
};
// The last sample: GPU copy until mappable, copy out of the mapped staging
// texture, and conversion plus crop.
struct DetectorStageTiming { double gpu_ms = 0, readback_ms = 0, convert_ms = 0; uint64_t bytes = 0; };

// The auto-clip detector's reusable readback and conversion. It reads back
// only the source pixels the three regions depend on into one reused staging
// texture, and converts only the output rows they cover, with the same
// swscale context and filter the full-canvas conversion uses, so every region
// and mask byte is identical to detector_reference_sample. Source and canvas
// planes are reserved at full size but committed only for those rows.
class DetectorStage {
public:
    DetectorStage();
    ~DetectorStage();
    DetectorStage(const DetectorStage&) = delete;
    DetectorStage& operator=(const DetectorStage&) = delete;
    // Fills snapshot's regions and mask; false when cancelled while the GPU
    // copy was still running.
    bool sample(const CapturePixels& pixels, const DetectorRequest& request, const std::function<bool()>& cancelled,
        RecordingDetectorSnapshot& snapshot);
    // Frees every resource; the next sample rebuilds.
    void release();
    const DetectorStageCounters& counters() const;
    const DetectorStageTiming& timing() const;
    // Runs detector_reference_sample instead (benchmarks and tests).
    bool reference = false;
    // Tests: true holds the readback as if the GPU copy were still running.
    std::function<bool()> readback_pending;
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

// Canvas rects of the three regions, as the detector crops them.
std::array<CaptureRect, 3> detector_region_rects(const DetectorRequest& request);
// Crops the regions, and the third's counter mask when requested, from an
// NV12 canvas of the request's size.
void detector_crop(const uint8_t* luma, size_t luma_pitch, const uint8_t* chroma, size_t chroma_pitch, const DetectorRequest& request,
    RecordingDetectorSnapshot& snapshot);
// The detector before DetectorStage: full-frame readback, full-canvas BGRA to
// NV12 conversion, then crop. The stage's fallback and its tests' reference.
bool detector_reference_sample(CapturePixels& pixels, const DetectorRequest& request, SwsContext*& scaler,
    const std::function<bool()>& cancelled, RecordingDetectorSnapshot& snapshot);
}
