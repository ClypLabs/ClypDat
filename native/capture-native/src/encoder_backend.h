#pragma once
#include <cstdint>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

// Backend-independent encoder resource policy. Pure arithmetic: no FFmpeg,
// D3D11 or encoder state. The recorder applies NVENC, AMF and QSV plans;
// software plans are not consumed yet.
namespace clypdat {
enum class EncoderVendor { Nvidia, Amd, Intel, Software };
enum class EncoderCodec { H264, AV1 };
// How frames reach the encoder: D3D11 frames context, QSV frames context with
// a D3D11 child, or system-memory frames after a GPU readback.
enum class EncoderInput { D3D11Frames, QsvFrames, SystemFrames };
enum class EncoderPixelFormat { NV12, P010 };
// Capture adapter against the backend's vendor. Unknown is not a match.
enum class AdapterMatch { Confirmed, Unknown, Mismatch };
// NotUsed for readback plans. Unverified plans need a runtime probe and a
// readback fallback before they may be trusted.
enum class ZeroCopyStatus { NotUsed, Confirmed, Unverified };

inline constexpr uint32_t kAdapterVendorNvidia = 0x10DE, kAdapterVendorAmd = 0x1002, kAdapterVendorIntel = 0x8086;
// FFmpeg nvenc.h MAX_REGISTERED_FRAMES; more distinct inputs thrash registration.
inline constexpr int kNvencRegisteredResources = 64;
// FFmpeg hwcontext_d3d11va.c MAX_ARRAY_SIZE. FFmpeg silently shrinks larger
// fixed pools, so plans that need more surfaces are rejected as infeasible.
inline constexpr int kD3D11TextureArrayLimit = 64;
// FFmpeg amfenc async_depth range: MAX_LOOKAHEAD_DEPTH + 1.
inline constexpr int kAmfMaxAsyncDepth = 42;

// The request cannot be met within backend or API limits. Distinct from
// std::invalid_argument, which marks a malformed request.
class EncoderPlanInfeasible : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

struct EncoderRequest {
    EncoderCodec codec = EncoderCodec::H264;
    EncoderPixelFormat pixel_format = EncoderPixelFormat::NV12;
    bool allow_codec_fallback = true; // Today AV1 may fall back to H.264 libx264.
    int width = 1920, height = 1080, fps = 60, bitrate_mbps = 20;
    int b_frames = 0, lookahead = 0;
    uint32_t adapter_vendor = 0; // DXGI VendorId of the capture device; 0 when unknown.
    bool overlay_stage = false;  // Burned overlays upload one more surface.
    // Input surfaces the driver suggests (MFX NumFrameSuggested); 0 when
    // unknown. Only QSV reads it: a larger value grows the pool up to the
    // distinct texture limit, and beyond it the plan is infeasible.
    int suggested_input_surfaces = 0;
    // Reported so every stage stays visible; never folded into encoder pools.
    int capture_buffers = 3, pacing_queue = 0;
};

// Tunable defaults. The latency-budget formulas are starting points to be
// replaced by benchmark results, not encoder requirements.
struct EncoderPolicy {
    int latency_budget_ms = 60;
    int nvenc_min_delay = 4, nvenc_max_delay = 8, nvenc_surface_slack = 2;
    // Explicit NVENC `delay` replacing the latency-budget value; 0 keeps the
    // budget. Plans that cannot honour it exactly are infeasible.
    int nvenc_delay_override = 0;
    int amf_min_depth = 3, amf_max_depth = 8;
    int qsv_min_depth = 3, qsv_max_depth = 6, qsv_suggested_slack = 1;
    int conversion_slots = 1, readback_conversion_slots = 2, readback_staging_slots = 2;
    int ffmpeg_hold = 1; // avcodec buffer_frame / NVENC ctx->frame on EAGAIN.
};

// The six resource stages. Counts are physical surfaces or buffers. Only
// conversion, encoder input and encoder in-flight surfaces size the pool.
struct EncoderStages {
    int capture_buffers = 0;        // WGC frame pool, source resolution.
    int pacing_queue = 0;           // Source-resolution refs awaiting conversion.
    int conversion_surfaces = 0;    // Targets written by the video processor.
    int encoder_input_surfaces = 0; // Physical surfaces the encoder may read at once.
    int encoder_in_flight = 0;      // Frames the encoder may still own.
    int packet_buffers = 0;         // Encoder-side output buffers.
    bool input_shares_conversion = false; // Zero-copy: input is the conversion surface.
};

struct EncoderPlan {
    EncoderVendor vendor = EncoderVendor::Software;
    EncoderCodec requested_codec = EncoderCodec::H264;
    EncoderCodec effective_codec = EncoderCodec::H264;
    std::string codec_name;
    EncoderInput input = EncoderInput::SystemFrames;
    EncoderPixelFormat pixel_format = EncoderPixelFormat::NV12;
    int width = 0, height = 0;
    bool zero_copy = false, needs_cpu_staging = false;
    ZeroCopyStatus zero_copy_status = ZeroCopyStatus::NotUsed;
    bool frames_from_encoder_ctx = false; // AMF asserts frame ctx == encoder ctx.
    bool fixed_pool = false, right_size_packets = false, low_delay_flag = false;
    int b_frames = 0, lookahead = 0;
    // Encoder queue parameter (NVENC `surfaces`, AMF/QSV `async_depth`). A slot
    // count, not a number of physical input surfaces; see stages for those.
    int encoder_slots = 0;
    int min_encoder_slots = 0;       // Structural minimum; below it the encoder stalls or FFmpeg raises it.
    int max_encoder_slots = 0;       // Backend/API limit for encoder_slots.
    int max_in_flight = 0;           // Hard cap for frames the encoder owns.
    int output_delay_frames = 0;     // Frames submitted before the first packet.
    int reorder_lookahead_extra = 0; // Frames added by B-frames and lookahead.
    int max_distinct_textures = 0;
    int surface_alignment = 1;       // QSV child textures align width/height to 16.
    int staging_slots = 0, cpu_frames = 0;
    int ffmpeg_hold = 0;             // Pool surface FFmpeg may hold between calls.
    int pool_capacity = 0;           // Exactly the surfaces the stages require.
    uint64_t pool_bytes = 0;
    EncoderStages stages;
    std::vector<std::pair<std::string, std::string>> options; // Resource options only.
    bool codec_fallback() const { return requested_codec != effective_codec; }
    // Executable without a runtime probe of the adapter.
    bool confirmed() const { return zero_copy_status != ZeroCopyStatus::Unverified; }
};

class EncoderBackend {
public:
    virtual ~EncoderBackend() = default;
    virtual EncoderVendor vendor() const = 0;
    virtual std::string codec_name(EncoderCodec codec) const = 0;
    // Codec this backend encodes for a request; may differ when fallback is allowed.
    virtual EncoderCodec effective_codec(const EncoderRequest& request) const = 0;
    // Whether the backend has any zero-copy input mechanism at all.
    virtual bool supports_zero_copy_input() const = 0;
    // Whether that mechanism can apply to the request's capture adapter.
    virtual AdapterMatch zero_copy_adapter(const EncoderRequest& request) const = 0;
    // Throws std::invalid_argument for malformed requests and
    // EncoderPlanInfeasible when limits cannot be met. Returned plans satisfy
    // check_encoder_plan.
    virtual EncoderPlan plan(const EncoderRequest& request, bool zero_copy, const EncoderPolicy& policy = {}) const = 0;
};

const EncoderBackend& encoder_backend(EncoderVendor vendor);
// Capture adapter's vendor first, then the remaining hardware, then software.
std::vector<EncoderVendor> encoder_vendor_order(uint32_t adapter_vendor, bool software_only = false);
AdapterMatch adapter_match(uint32_t adapter_vendor, EncoderVendor vendor);
int frames_for_latency(int fps, int latency_budget_ms);
// Bytes of one 4:2:0 surface: NV12 8-bit, P010 16-bit samples.
uint64_t frame_bytes(EncoderPixelFormat format, int width, int height, int alignment = 1);
// Sum of the stages that hold pool surfaces; capture and pacing never count.
int encoder_pool_requirement(const EncoderStages& stages, int ffmpeg_hold);
void validate_encoder_request(const EncoderRequest& request);
const char* encoder_vendor_name(EncoderVendor vendor);
const char* encoder_codec_name(EncoderCodec codec);
const char* zero_copy_status_name(ZeroCopyStatus status);
// Throws std::logic_error when a plan breaks its resource invariants.
void check_encoder_plan(const EncoderPlan& plan);
}
