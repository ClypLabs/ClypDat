#include "encoder_backend.h"
#include <algorithm>
#include <limits>

namespace clypdat {
namespace {
void require(bool condition, const char* message) { if (!condition) throw std::invalid_argument(message); }
void feasible(bool condition, const char* message) { if (!condition) throw EncoderPlanInfeasible(message); }
void invariant(bool condition, const char* message) { if (!condition) throw std::logic_error(message); }
void validate_policy(const EncoderPolicy& p) {
    require(p.latency_budget_ms > 0 && p.nvenc_min_delay >= 1 && p.nvenc_min_delay <= p.nvenc_max_delay &&
        p.nvenc_surface_slack >= 1 && p.amf_min_depth >= 1 && p.amf_min_depth <= p.amf_max_depth &&
        p.qsv_min_depth >= 1 && p.qsv_min_depth <= p.qsv_max_depth && p.qsv_suggested_slack >= 0 &&
        p.conversion_slots >= 1 && p.readback_conversion_slots >= 1 && p.readback_staging_slots >= 1 && p.ffmpeg_hold >= 0,
        "Invalid encoder resource policy");
}
std::string text(int value) { return std::to_string(value); }
// Fills the shared stage and pool fields once the backend chose its counts.
// A pool that cannot hold what the stages require is rejected, never shrunk.
void finish(EncoderPlan& plan, const EncoderRequest& request, const EncoderPolicy& policy, int encoder_input, int packets) {
    auto& s = plan.stages;
    s.capture_buffers = request.capture_buffers; s.pacing_queue = request.pacing_queue;
    s.encoder_input_surfaces = encoder_input; s.encoder_in_flight = plan.max_in_flight; s.packet_buffers = packets;
    s.input_shares_conversion = plan.zero_copy;
    const int overlay = request.overlay_stage ? 1 : 0;
    s.conversion_surfaces = (plan.zero_copy ? policy.conversion_slots : policy.readback_conversion_slots) + overlay;
    if (plan.needs_cpu_staging) { plan.staging_slots = policy.readback_staging_slots; plan.cpu_frames = 1; }
    plan.ffmpeg_hold = plan.zero_copy ? policy.ffmpeg_hold : 0;
    plan.pool_capacity = encoder_pool_requirement(s, plan.ffmpeg_hold);
    feasible(plan.pool_capacity <= plan.max_distinct_textures, "Encoder pool exceeds the distinct texture limit");
    plan.pool_bytes = uint64_t(plan.pool_capacity) * frame_bytes(plan.pixel_format, plan.width, plan.height, plan.surface_alignment);
    check_encoder_plan(plan);
}
EncoderPlan base(const EncoderBackend& backend, const EncoderRequest& request, bool zero_copy) {
    validate_encoder_request(request);
    EncoderPlan plan;
    plan.vendor = backend.vendor();
    plan.requested_codec = request.codec;
    plan.effective_codec = backend.effective_codec(request);
    feasible(!plan.codec_fallback() || request.allow_codec_fallback, "Backend cannot encode the requested codec");
    plan.codec_name = backend.codec_name(plan.effective_codec);
    plan.pixel_format = request.pixel_format;
    // 10-bit is only planned for AV1 hardware encoders until capabilities are probed.
    feasible(plan.pixel_format != EncoderPixelFormat::P010 ||
        (plan.effective_codec == EncoderCodec::AV1 && plan.vendor != EncoderVendor::Software), "P010 requires a hardware AV1 encoder");
    plan.width = request.width; plan.height = request.height;
    if (zero_copy) {
        feasible(backend.supports_zero_copy_input(), "Backend has no zero-copy input");
        const auto match = backend.zero_copy_adapter(request);
        feasible(match != AdapterMatch::Mismatch, "Capture adapter cannot feed this encoder without a copy");
        plan.zero_copy_status = match == AdapterMatch::Confirmed ? ZeroCopyStatus::Confirmed : ZeroCopyStatus::Unverified;
    }
    plan.zero_copy = zero_copy; plan.needs_cpu_staging = !zero_copy;
    plan.b_frames = request.b_frames; plan.lookahead = request.lookahead;
    plan.reorder_lookahead_extra = request.b_frames + request.lookahead;
    plan.input = zero_copy ? EncoderInput::D3D11Frames : EncoderInput::SystemFrames;
    plan.max_distinct_textures = kD3D11TextureArrayLimit;
    return plan;
}

// FFmpeg nvenc.c: default surfaces max(4, 4*(B+1)); lookahead needs L+(B+1)+5
// surfaces and async_depth >= L+(B+1)+4 so rcParams.lookaheadDepth is not
// clipped. Output is withheld until `delay` frames are pending (output_ready).
class NvencBackend final : public EncoderBackend {
public:
    EncoderVendor vendor() const override { return EncoderVendor::Nvidia; }
    std::string codec_name(EncoderCodec codec) const override { return codec == EncoderCodec::AV1 ? "av1_nvenc" : "h264_nvenc"; }
    EncoderCodec effective_codec(const EncoderRequest& request) const override { return request.codec; }
    bool supports_zero_copy_input() const override { return true; }
    AdapterMatch zero_copy_adapter(const EncoderRequest& request) const override { return adapter_match(request.adapter_vendor, vendor()); }
    EncoderPlan plan(const EncoderRequest& request, bool zero_copy, const EncoderPolicy& policy) const override {
        validate_policy(policy);
        auto plan = base(*this, request, zero_copy);
        const int b = request.b_frames, l = request.lookahead, interval = b + 1;
        int structural = std::max(4, interval * 4);
        if (l) structural = std::max(structural, l + interval + 1 + 4);
        feasible(structural <= kNvencRegisteredResources, "NVENC surfaces exceed the registered resource limit");
        int delay = std::clamp(frames_for_latency(request.fps, policy.latency_budget_ms), policy.nvenc_min_delay, policy.nvenc_max_delay);
        if (l) delay = std::max(delay, l + interval + 4);
        // Without zero-copy every surface is also an NVENC system-memory input
        // buffer, so keep the structural minimum there.
        const int surfaces = std::min(zero_copy ? std::max(structural, delay + policy.nvenc_surface_slack) : structural,
            kNvencRegisteredResources);
        delay = std::min(delay, surfaces - 1);
        feasible(!l || delay >= l + interval + 4, "NVENC lookahead exceeds registered surface limit");
        plan.min_encoder_slots = structural;
        plan.encoder_slots = surfaces;
        plan.max_encoder_slots = kNvencRegisteredResources;
        plan.max_in_flight = std::min(surfaces, delay + b + 1);
        plan.output_delay_frames = delay + b;
        plan.fixed_pool = true;
        if (zero_copy) plan.max_distinct_textures = std::min(kNvencRegisteredResources, kD3D11TextureArrayLimit);
        plan.options = {{"surfaces", text(surfaces)}, {"delay", text(delay)}};
        if (l) plan.options.push_back({"rc-lookahead", text(l)});
        finish(plan, request, policy, zero_copy ? plan.max_in_flight : surfaces, surfaces);
        return plan;
    }
};

// FFmpeg amfenc.c: once async_depth hardware surfaces are queued, receive
// blocks until AMF returns output. AMF holds B+1 frames for reordering plus
// the pre-analysis window before its first output, so async_depth below
// B+L+1 can never drain. FFmpeg only raises it to L+1, so the plan covers B.
// Output delay adds one poll frame unless AV_CODEC_FLAG_LOW_DELAY.
// avctx->max_b_frames is ignored; bf must be explicit.
class AmfBackend final : public EncoderBackend {
public:
    EncoderVendor vendor() const override { return EncoderVendor::Amd; }
    std::string codec_name(EncoderCodec codec) const override { return codec == EncoderCodec::AV1 ? "av1_amf" : "h264_amf"; }
    EncoderCodec effective_codec(const EncoderRequest& request) const override { return request.codec; }
    bool supports_zero_copy_input() const override { return true; }
    AdapterMatch zero_copy_adapter(const EncoderRequest& request) const override { return adapter_match(request.adapter_vendor, vendor()); }
    EncoderPlan plan(const EncoderRequest& request, bool zero_copy, const EncoderPolicy& policy) const override {
        validate_policy(policy);
        auto plan = base(*this, request, zero_copy);
        const int b = request.b_frames, l = request.lookahead;
        feasible(b <= 3 && l <= kAmfMaxAsyncDepth - 1, "AMF supports at most 3 B-frames and 41 lookahead frames");
        const int structural = b + l + 1;
        feasible(structural <= kAmfMaxAsyncDepth, "AMF reordering and lookahead exceed the async depth limit");
        const int depth = std::max(structural,
            std::clamp(frames_for_latency(request.fps, policy.latency_budget_ms), policy.amf_min_depth, policy.amf_max_depth));
        plan.low_delay_flag = b == 0;
        plan.min_encoder_slots = structural;
        plan.encoder_slots = depth;
        plan.max_encoder_slots = kAmfMaxAsyncDepth;
        plan.max_in_flight = depth;
        plan.output_delay_frames = b + l + (plan.low_delay_flag ? 0 : 1);
        plan.frames_from_encoder_ctx = zero_copy;
        plan.fixed_pool = true;
        plan.options = {{"async_depth", text(depth)}, {"bf", text(b)}, {"preanalysis", l ? "1" : "0"}};
        if (l) plan.options.push_back({"pa_lookahead_buffer_depth", text(l)});
        finish(plan, request, policy, depth, depth);
        return plan;
    }
};

// FFmpeg qsvenc.c: packets are withheld until async_depth tasks are queued and
// inputs stay locked until their task completes. Input must be AV_PIX_FMT_QSV
// frames; each packet buffer is BufferSizeInKB sized and never shrunk.
class QsvBackend final : public EncoderBackend {
public:
    EncoderVendor vendor() const override { return EncoderVendor::Intel; }
    std::string codec_name(EncoderCodec codec) const override { return codec == EncoderCodec::AV1 ? "av1_qsv" : "h264_qsv"; }
    EncoderCodec effective_codec(const EncoderRequest& request) const override { return request.codec; }
    bool supports_zero_copy_input() const override { return true; }
    AdapterMatch zero_copy_adapter(const EncoderRequest& request) const override { return adapter_match(request.adapter_vendor, vendor()); }
    EncoderPlan plan(const EncoderRequest& request, bool zero_copy, const EncoderPolicy& policy) const override {
        validate_policy(policy);
        auto plan = base(*this, request, zero_copy);
        const int b = request.b_frames, l = request.lookahead;
        const int depth = std::clamp(frames_for_latency(request.fps, policy.latency_budget_ms), policy.qsv_min_depth, policy.qsv_max_depth);
        if (zero_copy) plan.input = EncoderInput::QsvFrames;
        plan.surface_alignment = 16;
        plan.right_size_packets = true;
        plan.min_encoder_slots = 1;
        plan.encoder_slots = depth;
        plan.max_encoder_slots = std::numeric_limits<int>::max();
        plan.max_in_flight = depth + b + l;
        plan.output_delay_frames = depth + b + l;
        plan.frames_from_encoder_ctx = zero_copy;
        plan.fixed_pool = true;
        plan.options = {{"async_depth", text(depth)}};
        if (l) {
            plan.options.push_back(request.codec == EncoderCodec::AV1 ? std::pair<std::string, std::string>{"extbrc", "1"} :
                std::pair<std::string, std::string>{"look_ahead", "1"});
            plan.options.push_back({"look_ahead_depth", text(l)});
        }
        // Locked inputs plus one surface for MFX NumFrameSuggested headroom.
        finish(plan, request, policy, plan.max_in_flight + policy.qsv_suggested_slack, depth);
        return plan;
    }
};

// libx264 copies each input picture, so no frame stays encoder-owned. With
// ultrafast/zerolatency the output delay is only B-frames plus lookahead.
// AV1 requests fall back to H.264 when the request allows it.
class SoftwareBackend final : public EncoderBackend {
public:
    EncoderVendor vendor() const override { return EncoderVendor::Software; }
    std::string codec_name(EncoderCodec) const override { return "libx264"; }
    EncoderCodec effective_codec(const EncoderRequest&) const override { return EncoderCodec::H264; }
    bool supports_zero_copy_input() const override { return false; }
    AdapterMatch zero_copy_adapter(const EncoderRequest&) const override { return AdapterMatch::Mismatch; }
    EncoderPlan plan(const EncoderRequest& request, bool zero_copy, const EncoderPolicy& policy) const override {
        validate_policy(policy);
        auto plan = base(*this, request, zero_copy);
        plan.max_in_flight = 0;
        plan.output_delay_frames = request.b_frames + request.lookahead;
        finish(plan, request, policy, 1, 0);
        return plan;
    }
};
}

const EncoderBackend& encoder_backend(EncoderVendor vendor) {
    static const NvencBackend nvenc; static const AmfBackend amf; static const QsvBackend qsv; static const SoftwareBackend software;
    switch (vendor) {
    case EncoderVendor::Nvidia: return nvenc;
    case EncoderVendor::Amd: return amf;
    case EncoderVendor::Intel: return qsv;
    default: return software;
    }
}
std::vector<EncoderVendor> encoder_vendor_order(uint32_t adapter_vendor, bool software_only) {
    if (software_only) return {EncoderVendor::Software};
    std::vector<EncoderVendor> order{EncoderVendor::Nvidia, EncoderVendor::Amd, EncoderVendor::Intel};
    const auto preferred = adapter_vendor == kAdapterVendorAmd ? EncoderVendor::Amd :
        adapter_vendor == kAdapterVendorIntel ? EncoderVendor::Intel : EncoderVendor::Nvidia;
    std::stable_partition(order.begin(), order.end(), [&](EncoderVendor v) { return v == preferred; });
    order.push_back(EncoderVendor::Software);
    return order;
}
AdapterMatch adapter_match(uint32_t adapter_vendor, EncoderVendor vendor) {
    if (vendor == EncoderVendor::Software) return AdapterMatch::Mismatch;
    if (!adapter_vendor) return AdapterMatch::Unknown;
    const uint32_t expected = vendor == EncoderVendor::Nvidia ? kAdapterVendorNvidia :
        vendor == EncoderVendor::Amd ? kAdapterVendorAmd : kAdapterVendorIntel;
    return adapter_vendor == expected ? AdapterMatch::Confirmed : AdapterMatch::Mismatch;
}
int frames_for_latency(int fps, int latency_budget_ms) {
    require(fps > 0 && latency_budget_ms > 0, "Invalid latency budget");
    return int((int64_t(fps) * latency_budget_ms + 999) / 1000);
}
uint64_t frame_bytes(EncoderPixelFormat format, int width, int height, int alignment) {
    require(width > 0 && height > 0 && alignment > 0, "Invalid surface dimensions");
    const auto align = [&](int value) { return uint64_t((value + alignment - 1) / alignment * alignment); };
    const auto w = align(width), h = align(height);
    // 4:2:0: full luma plane plus one interleaved chroma plane at half height.
    const uint64_t samples = w * h + w * ((h + 1) / 2);
    return samples * (format == EncoderPixelFormat::P010 ? 2 : 1);
}
int encoder_pool_requirement(const EncoderStages& s, int ffmpeg_hold) {
    // Readback encoders read system memory; only the conversion ring is GPU.
    if (!s.input_shares_conversion) return s.conversion_surfaces;
    return std::max(s.encoder_input_surfaces, s.encoder_in_flight) + s.conversion_surfaces + ffmpeg_hold;
}
void validate_encoder_request(const EncoderRequest& r) {
    require(r.width >= 2 && r.height >= 2 && r.width <= 16384 && r.height <= 16384 && !(r.width & 1) && !(r.height & 1) &&
        r.fps >= 30 && r.fps <= 120 && r.bitrate_mbps > 0 && r.b_frames >= 0 && r.b_frames <= 4 &&
        r.lookahead >= 0 && r.lookahead <= 100 && r.capture_buffers >= 0 && r.pacing_queue >= 0,
        "Invalid encoder resource request");
}
void check_encoder_plan(const EncoderPlan& p) {
    const auto& s = p.stages;
    invariant(s.capture_buffers >= 0 && s.pacing_queue >= 0 && s.conversion_surfaces >= 1 && s.encoder_input_surfaces >= 0 &&
        s.encoder_in_flight >= 0 && s.packet_buffers >= 0 && p.max_in_flight >= 0 && p.output_delay_frames >= 0 &&
        p.staging_slots >= 0 && p.cpu_frames >= 0 && p.ffmpeg_hold >= 0 && p.width > 0 && p.height > 0,
        "Encoder plan has a negative or empty stage");
    invariant(s.encoder_in_flight == p.max_in_flight, "Encoder in-flight stage differs from the plan cap");
    invariant(p.min_encoder_slots <= p.encoder_slots && p.encoder_slots <= p.max_encoder_slots, "Encoder slots outside backend limits");
    invariant(p.pool_capacity == encoder_pool_requirement(s, p.ffmpeg_hold), "Encoder pool does not match its stages");
    invariant(p.pool_capacity <= p.max_distinct_textures, "Encoder pool exceeds the distinct texture limit");
    invariant(p.pool_bytes == uint64_t(p.pool_capacity) * frame_bytes(p.pixel_format, p.width, p.height, p.surface_alignment),
        "Encoder pool bytes do not match its format");
    invariant(p.pixel_format != EncoderPixelFormat::P010 || p.effective_codec == EncoderCodec::AV1, "P010 plan without AV1");
    invariant(p.codec_name == encoder_backend(p.vendor).codec_name(p.effective_codec), "Encoder name differs from effective codec");
    if (p.zero_copy) {
        invariant(p.vendor != EncoderVendor::Software, "Software plan claims zero-copy");
        invariant(p.input == (p.vendor == EncoderVendor::Intel ? EncoderInput::QsvFrames : EncoderInput::D3D11Frames),
            "Zero-copy plan has the wrong hardware input");
        invariant(!p.needs_cpu_staging && p.staging_slots == 0 && p.cpu_frames == 0 && p.zero_copy_status != ZeroCopyStatus::NotUsed &&
            s.input_shares_conversion && s.encoder_input_surfaces >= p.max_in_flight && p.max_in_flight >= 1,
            "Zero-copy plan is not a shared hardware surface plan");
    } else {
        invariant(p.input == EncoderInput::SystemFrames && p.needs_cpu_staging && p.staging_slots >= 1 && p.cpu_frames >= 1 &&
            p.zero_copy_status == ZeroCopyStatus::NotUsed && !s.input_shares_conversion && !p.frames_from_encoder_ctx,
            "Readback plan lacks staging resources");
    }
}
}
