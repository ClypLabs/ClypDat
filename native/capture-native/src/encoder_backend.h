#pragma once
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

// Backend-independent encoder resource policy. Pure arithmetic: no FFmpeg,
// D3D11 or encoder state. The runtime does not consume these plans yet.
namespace clypdat {
enum class EncoderVendor { Nvidia, Amd, Intel, Software };
enum class EncoderCodec { H264, AV1 };
// How frames reach the encoder: D3D11 NV12 frames context, QSV frames context
// with a D3D11 child, or system-memory NV12 after a GPU readback.
enum class EncoderInput { D3D11Frames, QsvFrames, SystemNV12 };
enum class EncoderPixelFormat { NV12, P010 };

inline constexpr uint32_t kAdapterVendorNvidia = 0x10DE, kAdapterVendorAmd = 0x1002, kAdapterVendorIntel = 0x8086;
// FFmpeg nvenc.h MAX_REGISTERED_FRAMES; more distinct inputs thrash registration.
inline constexpr int kNvencRegisteredResources = 64;
// FFmpeg hwcontext_d3d11va.c MAX_ARRAY_SIZE; fixed pools are clamped to this.
inline constexpr int kD3D11TextureArrayLimit = 64;

struct EncoderRequest {
    EncoderCodec codec = EncoderCodec::H264;
    int width = 1920, height = 1080, fps = 60, bitrate_mbps = 20;
    int b_frames = 0, lookahead = 0;
    uint32_t adapter_vendor = 0; // DXGI VendorId of the capture device; 0 when unknown.
    bool overlay_stage = false;  // Burned overlays upload one more NV12 surface.
    // Reported so every stage stays visible; never folded into encoder pools.
    int capture_buffers = 3, pacing_queue = 0;
};

// Tunable defaults. The latency-budget formulas are starting points to be
// replaced by benchmark results, not encoder requirements.
struct EncoderPolicy {
    int latency_budget_ms = 60;
    int nvenc_min_delay = 4, nvenc_max_delay = 8, nvenc_surface_slack = 2;
    int amf_min_depth = 3, amf_max_depth = 8;
    int qsv_min_depth = 3, qsv_max_depth = 6, qsv_suggested_slack = 1;
    int conversion_slots = 1, readback_conversion_slots = 2, readback_staging_slots = 2;
    int ffmpeg_hold = 1; // avcodec buffer_frame / NVENC ctx->frame on EAGAIN.
};

// The six resource stages, counted in frames or buffers. Only conversion,
// encoder input and encoder in-flight surfaces size the NV12 pool.
struct EncoderStages {
    int capture_buffers = 0;        // WGC frame pool, source resolution.
    int pacing_queue = 0;           // Source-resolution refs awaiting conversion.
    int conversion_surfaces = 0;    // NV12 targets written by the video processor.
    int encoder_input_surfaces = 0; // Surfaces the encoder reads; system memory when staging.
    int encoder_in_flight = 0;      // Frames the encoder may still own.
    int packet_buffers = 0;         // Encoder-side output buffers.
    bool input_shares_conversion = false; // Zero-copy: input is the conversion surface.
};

struct EncoderPlan {
    EncoderVendor vendor = EncoderVendor::Software;
    EncoderCodec codec = EncoderCodec::H264;
    std::string codec_name;
    EncoderInput input = EncoderInput::SystemNV12;
    EncoderPixelFormat sw_format = EncoderPixelFormat::NV12;
    bool zero_copy = false, needs_cpu_staging = false;
    bool frames_from_encoder_ctx = false; // AMF asserts frame ctx == encoder ctx.
    bool fixed_pool = false, right_size_packets = false, low_delay_flag = false;
    int b_frames = 0, lookahead = 0;
    int min_input_surfaces = 0;      // Structural FFmpeg/driver rule.
    int preferred_surfaces = 0;      // Encoder-side slots (NVENC surfaces, AMF/QSV depth).
    int max_in_flight = 0;           // Hard cap for frames the encoder owns.
    int output_delay_frames = 0;     // Frames submitted before the first packet.
    int reorder_lookahead_extra = 0; // Frames added by B-frames and lookahead.
    int max_distinct_textures = 0;
    int surface_alignment = 1;       // QSV child textures align width/height to 16.
    int staging_slots = 0, cpu_frames = 0;
    int pool_capacity = 0;           // D3D11/QSV NV12 surfaces owned by the pipeline.
    bool pool_clamped = false;       // Requirement exceeded max_distinct_textures.
    uint64_t pool_bytes = 0;
    EncoderStages stages;
    std::vector<std::pair<std::string, std::string>> options; // Resource options only.
};

class EncoderBackend {
public:
    virtual ~EncoderBackend() = default;
    virtual EncoderVendor vendor() const = 0;
    virtual std::string codec_name(EncoderCodec codec) const = 0;
    // Zero-copy needs the capture device on this vendor's adapter.
    virtual bool supports_zero_copy(const EncoderRequest& request) const = 0;
    virtual EncoderPlan plan(const EncoderRequest& request, bool zero_copy, const EncoderPolicy& policy = {}) const = 0;
};

const EncoderBackend& encoder_backend(EncoderVendor vendor);
// Capture adapter's vendor first, then the remaining hardware, then software.
std::vector<EncoderVendor> encoder_vendor_order(uint32_t adapter_vendor, bool software_only = false);
int frames_for_latency(int fps, int latency_budget_ms);
uint64_t nv12_frame_bytes(int width, int height, int alignment = 1);
// Sum of the stages that hold pool surfaces; capture and pacing never count.
int encoder_pool_requirement(const EncoderStages& stages, int ffmpeg_hold);
void validate_encoder_request(const EncoderRequest& request);
}
