#include "encoder_backend.h"
#include <algorithm>
#include <stdexcept>

namespace clypdat {
namespace {
void require(bool condition, const char* message) { if (!condition) throw std::invalid_argument(message); }
void validate_policy(const EncoderPolicy& p) {
    require(p.latency_budget_ms > 0 && p.nvenc_min_delay >= 1 && p.nvenc_min_delay <= p.nvenc_max_delay &&
        p.nvenc_surface_slack >= 1 && p.amf_min_depth >= 1 && p.amf_min_depth <= p.amf_max_depth &&
        p.qsv_min_depth >= 1 && p.qsv_min_depth <= p.qsv_max_depth && p.qsv_suggested_slack >= 0 &&
        p.conversion_slots >= 1 && p.readback_conversion_slots >= 1 && p.readback_staging_slots >= 1 && p.ffmpeg_hold >= 0,
        "Invalid encoder resource policy");
}
std::string text(int value) { return std::to_string(value); }
// Fills the shared stage and pool fields once the backend chose its counts.
void finish(EncoderPlan& plan, const EncoderRequest& request, const EncoderPolicy& policy, int encoder_input, int packets) {
    auto& s = plan.stages;
    s.capture_buffers = request.capture_buffers; s.pacing_queue = request.pacing_queue;
    s.encoder_input_surfaces = encoder_input; s.encoder_in_flight = plan.max_in_flight; s.packet_buffers = packets;
    s.input_shares_conversion = plan.zero_copy;
    const int overlay = request.overlay_stage ? 1 : 0;
    s.conversion_surfaces = (plan.zero_copy ? policy.conversion_slots : policy.readback_conversion_slots) + overlay;
    if (plan.needs_cpu_staging) { plan.staging_slots = policy.readback_staging_slots; plan.cpu_frames = 1; }
    const int required = encoder_pool_requirement(s, plan.zero_copy ? policy.ffmpeg_hold : 0);
    plan.pool_clamped = required > plan.max_distinct_textures;
    plan.pool_capacity = std::min(required, plan.max_distinct_textures);
    plan.pool_bytes = uint64_t(plan.pool_capacity) * nv12_frame_bytes(request.width, request.height, plan.surface_alignment);
}
EncoderPlan base(const EncoderBackend& backend, const EncoderRequest& request, bool zero_copy) {
    validate_encoder_request(request);
    EncoderPlan plan;
    plan.vendor = backend.vendor(); plan.codec = request.codec; plan.codec_name = backend.codec_name(request.codec);
    plan.zero_copy = zero_copy; plan.needs_cpu_staging = !zero_copy;
    plan.b_frames = request.b_frames; plan.lookahead = request.lookahead;
    plan.reorder_lookahead_extra = request.b_frames + request.lookahead;
    plan.input = zero_copy ? EncoderInput::D3D11Frames : EncoderInput::SystemNV12;
    plan.max_distinct_textures = kD3D11TextureArrayLimit;
    return plan;
}
bool adapter_matches(const EncoderRequest& request, uint32_t vendor) { return !request.adapter_vendor || request.adapter_vendor == vendor; }

// FFmpeg nvenc.c: default surfaces max(4, 4*(B+1)); lookahead needs L+(B+1)+5
// surfaces and async_depth >= L+(B+1)+4 so rcParams.lookaheadDepth is not
// clipped. Output is withheld until `delay` frames are pending (output_ready).
class NvencBackend final : public EncoderBackend {
public:
    EncoderVendor vendor() const override { return EncoderVendor::Nvidia; }
    std::string codec_name(EncoderCodec codec) const override { return codec == EncoderCodec::AV1 ? "av1_nvenc" : "h264_nvenc"; }
    bool supports_zero_copy(const EncoderRequest& request) const override { return adapter_matches(request, kAdapterVendorNvidia); }
    EncoderPlan plan(const EncoderRequest& request, bool zero_copy, const EncoderPolicy& policy) const override {
        validate_policy(policy);
        auto plan = base(*this, request, zero_copy);
        const int b = request.b_frames, l = request.lookahead, interval = b + 1;
        int structural = std::max(4, interval * 4);
        if (l) structural = std::max(structural, l + interval + 1 + 4);
        int delay = std::clamp(frames_for_latency(request.fps, policy.latency_budget_ms), policy.nvenc_min_delay, policy.nvenc_max_delay);
        if (l) delay = std::max(delay, l + interval + 4);
        // Without zero-copy every surface is also an NVENC system-memory input
        // buffer, so keep the structural minimum there.
        int surfaces = zero_copy ? std::max(structural, delay + policy.nvenc_surface_slack) : structural;
        surfaces = std::min(surfaces, kNvencRegisteredResources);
        delay = std::min(delay, surfaces - 1);
        require(!l || delay >= l + interval + 4, "NVENC lookahead exceeds registered surface limit");
        plan.min_input_surfaces = structural;
        plan.preferred_surfaces = surfaces;
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

// FFmpeg amfenc.c: async_depth caps queued hardware surfaces and rises to
// pa_lookahead_buffer_depth + 1. Output delay is bf plus one frame unless
// AV_CODEC_FLAG_LOW_DELAY. avctx->max_b_frames is ignored; bf must be explicit.
class AmfBackend final : public EncoderBackend {
public:
    EncoderVendor vendor() const override { return EncoderVendor::Amd; }
    std::string codec_name(EncoderCodec codec) const override { return codec == EncoderCodec::AV1 ? "av1_amf" : "h264_amf"; }
    bool supports_zero_copy(const EncoderRequest& request) const override { return adapter_matches(request, kAdapterVendorAmd); }
    EncoderPlan plan(const EncoderRequest& request, bool zero_copy, const EncoderPolicy& policy) const override {
        validate_policy(policy);
        require(request.b_frames <= 3 && request.lookahead <= 41, "AMF supports at most 3 B-frames and 41 lookahead frames");
        auto plan = base(*this, request, zero_copy);
        const int b = request.b_frames, l = request.lookahead;
        int depth = std::clamp(frames_for_latency(request.fps, policy.latency_budget_ms), policy.amf_min_depth, policy.amf_max_depth);
        if (l) depth = std::max(depth, l + 1);
        plan.low_delay_flag = b == 0;
        const int flag_delay = plan.low_delay_flag ? 0 : 1;
        plan.min_input_surfaces = 1 + b + l + flag_delay;
        plan.preferred_surfaces = depth;
        plan.max_in_flight = depth;
        plan.output_delay_frames = b + l + flag_delay;
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
    bool supports_zero_copy(const EncoderRequest& request) const override { return adapter_matches(request, kAdapterVendorIntel); }
    EncoderPlan plan(const EncoderRequest& request, bool zero_copy, const EncoderPolicy& policy) const override {
        validate_policy(policy);
        auto plan = base(*this, request, zero_copy);
        const int b = request.b_frames, l = request.lookahead;
        const int depth = std::clamp(frames_for_latency(request.fps, policy.latency_budget_ms), policy.qsv_min_depth, policy.qsv_max_depth);
        if (zero_copy) plan.input = EncoderInput::QsvFrames;
        plan.surface_alignment = 16;
        plan.right_size_packets = true;
        plan.max_in_flight = depth + b + l;
        plan.min_input_surfaces = plan.max_in_flight + policy.qsv_suggested_slack;
        plan.preferred_surfaces = depth;
        plan.output_delay_frames = depth + b + l;
        plan.frames_from_encoder_ctx = zero_copy;
        plan.fixed_pool = true;
        plan.options = {{"async_depth", text(depth)}};
        if (l) {
            plan.options.push_back(request.codec == EncoderCodec::AV1 ? std::pair<std::string, std::string>{"extbrc", "1"} :
                std::pair<std::string, std::string>{"look_ahead", "1"});
            plan.options.push_back({"look_ahead_depth", text(l)});
        }
        finish(plan, request, policy, plan.min_input_surfaces, depth);
        return plan;
    }
};

// libx264 copies each input picture, so no frame stays encoder-owned. With
// ultrafast/zerolatency the output delay is only B-frames plus lookahead.
class SoftwareBackend final : public EncoderBackend {
public:
    EncoderVendor vendor() const override { return EncoderVendor::Software; }
    std::string codec_name(EncoderCodec) const override { return "libx264"; }
    bool supports_zero_copy(const EncoderRequest&) const override { return false; }
    EncoderPlan plan(const EncoderRequest& request, bool zero_copy, const EncoderPolicy& policy) const override {
        validate_policy(policy);
        require(!zero_copy, "Software encoding requires CPU frames");
        auto plan = base(*this, request, false);
        plan.codec = EncoderCodec::H264;
        plan.min_input_surfaces = 1;
        plan.preferred_surfaces = 1;
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
int frames_for_latency(int fps, int latency_budget_ms) {
    require(fps > 0 && latency_budget_ms > 0, "Invalid latency budget");
    return int((int64_t(fps) * latency_budget_ms + 999) / 1000);
}
uint64_t nv12_frame_bytes(int width, int height, int alignment) {
    require(width > 0 && height > 0 && alignment > 0, "Invalid NV12 dimensions");
    const auto align = [&](int value) { return uint64_t((value + alignment - 1) / alignment * alignment); };
    const auto w = align(width), h = align(height);
    return w * h + w * ((h + 1) / 2);
}
int encoder_pool_requirement(const EncoderStages& s, int ffmpeg_hold) {
    // Readback encoders read system memory; only the conversion ring is D3D11.
    if (!s.input_shares_conversion) return s.conversion_surfaces;
    return std::max(s.encoder_input_surfaces, s.encoder_in_flight) + s.conversion_surfaces + ffmpeg_hold;
}
void validate_encoder_request(const EncoderRequest& r) {
    require(r.width >= 2 && r.height >= 2 && r.width <= 16384 && r.height <= 16384 && !(r.width & 1) && !(r.height & 1) &&
        r.fps >= 30 && r.fps <= 120 && r.bitrate_mbps > 0 && r.b_frames >= 0 && r.b_frames <= 4 &&
        r.lookahead >= 0 && r.lookahead <= 100 && r.capture_buffers >= 0 && r.pacing_queue >= 0,
        "Invalid encoder resource request");
}
}
