#include "encoder_backend.h"
#include <cstdlib>
#include <iostream>
#include <stdexcept>

#define CHECK(expression) do { if (!(expression)) { \
    std::cerr << __FILE__ << ':' << __LINE__ << ": " #expression " failed\n"; \
    std::exit(EXIT_FAILURE); } } while (false)

using namespace clypdat;

template<class Error, class Action> void must_throw(Action action) {
    bool threw = false;
    try { action(); } catch (const Error&) { threw = true; }
    CHECK(threw);
}
template<class Action> void invalid(Action action) { must_throw<std::invalid_argument>(action); }
template<class Action> void infeasible(Action action) { must_throw<EncoderPlanInfeasible>(action); }
template<class Action> void broken(Action action) { must_throw<std::logic_error>(action); }

uint32_t adapter_of(EncoderVendor vendor) {
    return vendor == EncoderVendor::Nvidia ? kAdapterVendorNvidia : vendor == EncoderVendor::Amd ? kAdapterVendorAmd :
        vendor == EncoderVendor::Intel ? kAdapterVendorIntel : 0;
}
EncoderRequest request(int width, int height, int fps, int b_frames = 0, int lookahead = 0) {
    EncoderRequest value; value.width = width; value.height = height; value.fps = fps;
    value.bitrate_mbps = 25; value.b_frames = b_frames; value.lookahead = lookahead; return value;
}
// Capture device confirmed on the backend's own adapter.
EncoderRequest on(EncoderVendor vendor, EncoderRequest value) { value.adapter_vendor = adapter_of(vendor); return value; }
EncoderPlan plan(EncoderVendor vendor, const EncoderRequest& value, bool zero_copy = true, const EncoderPolicy& policy = {}) {
    return encoder_backend(vendor).plan(value, zero_copy, policy);
}
std::string option(const EncoderPlan& value, const std::string& key) {
    for (const auto& [name, setting] : value.options) if (name == key) return setting;
    return {};
}
struct Scenario { const char* name; int width, height, fps; };
constexpr Scenario scenarios[] = {
    {"1080p60", 1920, 1080, 60}, {"1440p60", 2560, 1440, 60}, {"1440p90", 2560, 1440, 90},
    {"1440p120", 2560, 1440, 120}, {"4K60", 3840, 2160, 60}};
constexpr EncoderVendor hardware[] = {EncoderVendor::Nvidia, EncoderVendor::Amd, EncoderVendor::Intel};

void helpers() {
    CHECK(frames_for_latency(60, 60) == 4 && frames_for_latency(90, 60) == 6 && frames_for_latency(120, 60) == 8);
    CHECK(frames_for_latency(60, 50) == 3 && frames_for_latency(30, 1000) == 30);
    invalid([] { frames_for_latency(0, 60); });
    invalid([] { validate_encoder_request(request(1921, 1080, 60)); });
    invalid([] { validate_encoder_request(request(1920, 1080, 121)); });
    invalid([] { validate_encoder_request(request(1920, 1080, 29)); });
    invalid([] { validate_encoder_request(request(1920, 1080, 60, 5)); });
    invalid([] { validate_encoder_request(request(1920, 1080, 60, 0, 101)); });
    EncoderPolicy bad; bad.nvenc_min_delay = 9;
    invalid([&] { plan(EncoderVendor::Nvidia, request(1920, 1080, 60), true, bad); });
}

void surface_bytes() {
    using F = EncoderPixelFormat;
    CHECK(frame_bytes(F::NV12, 1920, 1080) == 3110400);
    CHECK(frame_bytes(F::NV12, 2560, 1440) == 5529600);
    CHECK(frame_bytes(F::NV12, 3840, 2160) == 12441600);
    CHECK(frame_bytes(F::NV12, 1920, 1080, 16) == 3133440);   // 1920x1088
    CHECK(frame_bytes(F::NV12, 2560, 1440, 16) == 5529600);   // already aligned
    CHECK(frame_bytes(F::NV12, 3840, 2160, 64) == 12533760);  // 3840x2176
    CHECK(frame_bytes(F::P010, 1920, 1080) == 6220800);
    CHECK(frame_bytes(F::P010, 2560, 1440) == 11059200);
    CHECK(frame_bytes(F::P010, 3840, 2160) == 24883200);
    CHECK(frame_bytes(F::P010, 1920, 1080, 16) == 6266880);
    CHECK(frame_bytes(F::P010, 3840, 2160, 64) == 25067520);
    invalid([] { frame_bytes(EncoderPixelFormat::NV12, 0, 1080); });
    invalid([] { frame_bytes(EncoderPixelFormat::NV12, 1920, 1080, 0); });
    // pool_bytes follows the plan's format and alignment.
    auto ten = on(EncoderVendor::Nvidia, request(2560, 1440, 90)); ten.codec = EncoderCodec::AV1; ten.pixel_format = F::P010;
    const auto p = plan(EncoderVendor::Nvidia, ten);
    CHECK(p.pixel_format == F::P010 && p.pool_capacity == 9 && p.pool_bytes == 9ull * 11059200);
    auto qsv = on(EncoderVendor::Intel, request(1920, 1080, 60)); qsv.codec = EncoderCodec::AV1; qsv.pixel_format = F::P010;
    const auto q = plan(EncoderVendor::Intel, qsv);
    CHECK(q.pool_bytes == uint64_t(q.pool_capacity) * 6266880);
    // P010 is not planned for H.264 or software until capabilities are probed.
    auto h264 = on(EncoderVendor::Nvidia, request(1920, 1080, 60)); h264.pixel_format = F::P010;
    infeasible([&] { plan(EncoderVendor::Nvidia, h264); });
    auto software = request(1920, 1080, 60); software.codec = EncoderCodec::AV1; software.pixel_format = F::P010;
    infeasible([&] { plan(EncoderVendor::Software, software, false); });
}

void nvenc_scenarios() {
    // delay, surfaces, in flight, pool for each scenario at the 60 ms default.
    const int expected[][4] = {{4, 6, 5, 7}, {4, 6, 5, 7}, {6, 8, 7, 9}, {8, 10, 9, 11}, {4, 6, 5, 7}};
    for (size_t i = 0; i < std::size(scenarios); ++i) {
        const auto& s = scenarios[i];
        const auto p = plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(s.width, s.height, s.fps)));
        CHECK(p.codec_name == "h264_nvenc" && p.input == EncoderInput::D3D11Frames && p.pixel_format == EncoderPixelFormat::NV12);
        CHECK(p.zero_copy && p.zero_copy_status == ZeroCopyStatus::Confirmed && p.confirmed());
        CHECK(!p.needs_cpu_staging && p.staging_slots == 0 && p.cpu_frames == 0 && p.fixed_pool);
        CHECK(option(p, "delay") == std::to_string(expected[i][0]) && option(p, "surfaces") == std::to_string(expected[i][1]));
        CHECK(option(p, "rc-lookahead").empty());
        CHECK(p.min_encoder_slots == 4 && p.encoder_slots == expected[i][1] && p.max_encoder_slots == kNvencRegisteredResources);
        CHECK(p.max_in_flight == expected[i][2] && p.output_delay_frames == expected[i][0] && p.reorder_lookahead_extra == 0);
        CHECK(p.pool_capacity == expected[i][3] && p.max_distinct_textures == kNvencRegisteredResources);
        CHECK(p.pool_bytes == uint64_t(p.pool_capacity) * frame_bytes(EncoderPixelFormat::NV12, s.width, s.height));
        CHECK(p.stages.input_shares_conversion && p.stages.conversion_surfaces == 1 && p.ffmpeg_hold == 1);
        CHECK(p.stages.encoder_input_surfaces == p.max_in_flight && p.stages.packet_buffers == expected[i][1]);
    }
    auto av1 = on(EncoderVendor::Nvidia, request(2560, 1440, 90)); av1.codec = EncoderCodec::AV1;
    const auto a = plan(EncoderVendor::Nvidia, av1);
    CHECK(a.codec_name == "av1_nvenc" && a.effective_codec == EncoderCodec::AV1 && !a.codec_fallback());
}

void nvenc_reordering_and_lookahead() {
    // FFmpeg requires 4*(B+1) surfaces; reorder adds B frames of delay.
    const auto b = plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(2560, 1440, 90, 2)));
    CHECK(b.min_encoder_slots == 12 && option(b, "surfaces") == "12" && option(b, "delay") == "6");
    CHECK(b.max_in_flight == 9 && b.output_delay_frames == 8 && b.reorder_lookahead_extra == 2 && b.pool_capacity == 11);
    // Lookahead needs L+(B+1)+5 surfaces and delay L+(B+1)+4 to stay unclipped.
    const auto l = plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(2560, 1440, 90, 0, 20)));
    CHECK(l.min_encoder_slots == 26 && option(l, "delay") == "25" && option(l, "surfaces") == "27");
    CHECK(option(l, "rc-lookahead") == "20" && l.max_in_flight == 26 && l.pool_capacity == 28 && l.reorder_lookahead_extra == 20);
    const auto both = plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(1920, 1080, 60, 1, 8)));
    CHECK(both.min_encoder_slots == 15 && option(both, "delay") == "14" && option(both, "surfaces") == "16");
    CHECK(both.max_in_flight == 16 && both.output_delay_frames == 15);
}

void nvenc_resource_limits() {
    // Largest lookahead whose pool fits the 64-entry registration table and
    // array limit: 61 in flight + 2 conversion (overlay) + 1 FFmpeg hold.
    auto edge = on(EncoderVendor::Nvidia, request(1920, 1080, 60, 0, 55)); edge.overlay_stage = true;
    const auto fits = plan(EncoderVendor::Nvidia, edge);
    CHECK(option(fits, "surfaces") == "62" && option(fits, "delay") == "60" && fits.max_in_flight == 61);
    CHECK(fits.pool_capacity == kNvencRegisteredResources);
    // One more frame needs 65 pool surfaces: rejected, not clamped.
    auto over = edge; over.lookahead = 56;
    infeasible([&] { plan(EncoderVendor::Nvidia, over); });
    infeasible([] { plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(1920, 1080, 60, 0, 57))); });
    // Structural surfaces above 64 are rejected before any pool is sized.
    infeasible([] { plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(1920, 1080, 60, 0, 60))); });
    infeasible([] { plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(1920, 1080, 60, 4, 55))); });
    // A tuned delay near the limit still fits; one that would need 66 pool
    // surfaces is rejected.
    EncoderPolicy wide; wide.latency_budget_ms = 1000; wide.nvenc_max_delay = 100;
    const auto capped = plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(1920, 1080, 60)), true, wide);
    CHECK(option(capped, "surfaces") == "62" && option(capped, "delay") == "60" && capped.pool_capacity == 63);
    wide.latency_budget_ms = 1100;
    infeasible([&] { plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(1920, 1080, 60)), true, wide); });
}

void nvenc_readback_and_tuning() {
    const auto p = plan(EncoderVendor::Nvidia, request(2560, 1440, 90), false);
    CHECK(p.input == EncoderInput::SystemFrames && p.needs_cpu_staging && !p.zero_copy && p.zero_copy_status == ZeroCopyStatus::NotUsed);
    CHECK(option(p, "surfaces") == "4" && option(p, "delay") == "3" && p.max_in_flight == 4);
    CHECK(p.staging_slots == 2 && p.cpu_frames == 1 && !p.stages.input_shares_conversion && p.ffmpeg_hold == 0);
    CHECK(p.stages.encoder_input_surfaces == 4 && p.stages.conversion_surfaces == 2 && p.pool_capacity == 2);
    // Budget and bounds are policy, not constants.
    EncoderPolicy wide; wide.latency_budget_ms = 120; wide.nvenc_max_delay = 12;
    const auto tuned = plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(2560, 1440, 90)), true, wide);
    CHECK(option(tuned, "delay") == "11" && option(tuned, "surfaces") == "13" && tuned.pool_capacity == 14);
    EncoderPolicy lean; lean.nvenc_min_delay = 2; lean.latency_budget_ms = 20; lean.nvenc_surface_slack = 1;
    const auto small = plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(1920, 1080, 60)), true, lean);
    CHECK(option(small, "delay") == "2" && option(small, "surfaces") == "4" && small.max_in_flight == 3);
}

void amf_depth() {
    const int expected[][2] = {{4, 6}, {4, 6}, {6, 8}, {8, 10}, {4, 6}};
    for (size_t i = 0; i < std::size(scenarios); ++i) {
        const auto& s = scenarios[i];
        const auto p = plan(EncoderVendor::Amd, on(EncoderVendor::Amd, request(s.width, s.height, s.fps)));
        CHECK(p.codec_name == "h264_amf" && p.input == EncoderInput::D3D11Frames && p.frames_from_encoder_ctx);
        CHECK(option(p, "async_depth") == std::to_string(expected[i][0]) && option(p, "bf") == "0" && option(p, "preanalysis") == "0");
        CHECK(p.low_delay_flag && p.output_delay_frames == 0 && p.max_in_flight == expected[i][0]);
        CHECK(p.pool_capacity == expected[i][1] && !p.right_size_packets && p.max_distinct_textures == kD3D11TextureArrayLimit);
    }
    const auto readback = plan(EncoderVendor::Amd, request(2560, 1440, 90), false);
    CHECK(readback.needs_cpu_staging && !readback.frames_from_encoder_ctx && readback.pool_capacity == 2);
    auto av1 = on(EncoderVendor::Amd, request(2560, 1440, 90)); av1.codec = EncoderCodec::AV1;
    CHECK(plan(EncoderVendor::Amd, av1).codec_name == "av1_amf");
}

// amfenc blocks in receive once async_depth hardware surfaces are queued and
// AMF emits nothing until it holds B+1 reorder frames plus the pre-analysis
// window. Depth must therefore cover B+L+1 physical surfaces.
void amf_invariants() {
    struct Case { int b, l, slots, depth, delay, pool; bool low_delay; };
    const Case cases[] = {
        {0, 0, 1, 6, 0, 8, true},      // latency budget dominates
        {2, 0, 3, 6, 3, 8, false},     // reorder fits inside the budget depth
        {0, 10, 11, 11, 10, 13, true}, // FFmpeg's own L+1 rule
        {2, 10, 13, 13, 13, 15, false}, // B raises it past FFmpeg's L+1
    };
    for (const auto& c : cases) {
        const auto p = plan(EncoderVendor::Amd, on(EncoderVendor::Amd, request(2560, 1440, 90, c.b, c.l)));
        CHECK(p.min_encoder_slots == c.slots && p.encoder_slots == c.depth && option(p, "async_depth") == std::to_string(c.depth));
        CHECK(p.encoder_slots >= p.min_encoder_slots && p.max_in_flight >= c.b + c.l + 1);
        CHECK(p.max_in_flight == c.depth && p.stages.encoder_input_surfaces == c.depth);
        CHECK(p.output_delay_frames == c.delay && p.low_delay_flag == c.low_delay && p.pool_capacity == c.pool);
        CHECK(p.pool_capacity >= p.stages.encoder_input_surfaces + p.stages.conversion_surfaces + p.ffmpeg_hold);
        CHECK(option(p, "bf") == std::to_string(c.b) && option(p, "preanalysis") == (c.l ? "1" : "0"));
        CHECK(option(p, "pa_lookahead_buffer_depth") == (c.l ? std::to_string(c.l) : std::string{}));
    }
    // Lower frame rates keep the structural floor.
    CHECK(plan(EncoderVendor::Amd, on(EncoderVendor::Amd, request(1920, 1080, 60, 2, 10))).encoder_slots == 13);
    // FFmpeg caps async_depth at MAX_LOOKAHEAD_DEPTH + 1.
    CHECK(plan(EncoderVendor::Amd, on(EncoderVendor::Amd, request(1920, 1080, 60, 3, 38))).encoder_slots == kAmfMaxAsyncDepth);
    infeasible([] { plan(EncoderVendor::Amd, on(EncoderVendor::Amd, request(1920, 1080, 60, 3, 39))); });
    infeasible([] { plan(EncoderVendor::Amd, on(EncoderVendor::Amd, request(1920, 1080, 60, 4))); });
    infeasible([] { plan(EncoderVendor::Amd, on(EncoderVendor::Amd, request(1920, 1080, 60, 0, 42))); });
}

void qsv_depth() {
    const int expected[][2] = {{4, 7}, {4, 7}, {6, 9}, {6, 9}, {4, 7}};
    for (size_t i = 0; i < std::size(scenarios); ++i) {
        const auto& s = scenarios[i];
        const auto p = plan(EncoderVendor::Intel, on(EncoderVendor::Intel, request(s.width, s.height, s.fps)));
        CHECK(p.codec_name == "h264_qsv" && p.input == EncoderInput::QsvFrames && p.frames_from_encoder_ctx && p.fixed_pool);
        CHECK(option(p, "async_depth") == std::to_string(expected[i][0]) && option(p, "look_ahead").empty());
        CHECK(p.max_in_flight == expected[i][0] && p.output_delay_frames == expected[i][0]);
        CHECK(p.stages.encoder_input_surfaces == expected[i][0] + 1 && p.pool_capacity == expected[i][1]);
        CHECK(p.right_size_packets && p.surface_alignment == 16);
        CHECK(p.pool_bytes == uint64_t(p.pool_capacity) * frame_bytes(EncoderPixelFormat::NV12, s.width, s.height, 16));
    }
    const auto l = plan(EncoderVendor::Intel, on(EncoderVendor::Intel, request(2560, 1440, 90, 1, 10)));
    CHECK(l.max_in_flight == 17 && l.output_delay_frames == 17 && l.stages.encoder_input_surfaces == 18 && l.pool_capacity == 20);
    CHECK(option(l, "look_ahead") == "1" && option(l, "look_ahead_depth") == "10");
    auto av1 = on(EncoderVendor::Intel, request(2560, 1440, 90, 0, 10)); av1.codec = EncoderCodec::AV1;
    const auto a = plan(EncoderVendor::Intel, av1);
    CHECK(a.codec_name == "av1_qsv" && option(a, "extbrc") == "1" && option(a, "look_ahead").empty());
    EncoderPolicy deeper; deeper.qsv_max_depth = 10;
    CHECK(option(plan(EncoderVendor::Intel, on(EncoderVendor::Intel, request(2560, 1440, 120)), true, deeper), "async_depth") == "8");
    const auto readback = plan(EncoderVendor::Intel, request(1920, 1080, 60), false);
    CHECK(readback.input == EncoderInput::SystemFrames && readback.needs_cpu_staging && readback.right_size_packets);
    CHECK(readback.pool_capacity == 2 && readback.stages.encoder_input_surfaces == 5);
    // Locked inputs beyond the texture-array limit cannot be pooled.
    infeasible([] { plan(EncoderVendor::Intel, on(EncoderVendor::Intel, request(1920, 1080, 60, 4, 60))); });
}

void software_fallback() {
    for (const auto& s : scenarios) {
        const auto p = plan(EncoderVendor::Software, request(s.width, s.height, s.fps), false);
        CHECK(p.codec_name == "libx264" && p.input == EncoderInput::SystemFrames && p.needs_cpu_staging);
        CHECK(p.max_in_flight == 0 && p.output_delay_frames == 0 && p.staging_slots == 2 && p.cpu_frames == 1);
        CHECK(p.pool_capacity == 2 && p.stages.packet_buffers == 0 && p.options.empty());
    }
    infeasible([] { plan(EncoderVendor::Software, request(1920, 1080, 60), true); });
    CHECK(!encoder_backend(EncoderVendor::Software).supports_zero_copy_input());
}

void codec_fallback() {
    auto av1 = request(1920, 1080, 60); av1.codec = EncoderCodec::AV1;
    const auto p = plan(EncoderVendor::Software, av1, false);
    CHECK(p.requested_codec == EncoderCodec::AV1 && p.effective_codec == EncoderCodec::H264 && p.codec_fallback());
    CHECK(p.codec_name == "libx264");
    av1.allow_codec_fallback = false;
    infeasible([&] { plan(EncoderVendor::Software, av1, false); });
    for (const auto vendor : hardware) {
        const auto h = plan(vendor, on(vendor, av1));
        CHECK(h.requested_codec == EncoderCodec::AV1 && h.effective_codec == EncoderCodec::AV1 && !h.codec_fallback());
    }
    const auto h264 = plan(EncoderVendor::Software, request(1920, 1080, 60), false);
    CHECK(!h264.codec_fallback());
}

void adapter_compatibility() {
    CHECK(adapter_match(0, EncoderVendor::Nvidia) == AdapterMatch::Unknown);
    CHECK(adapter_match(kAdapterVendorNvidia, EncoderVendor::Nvidia) == AdapterMatch::Confirmed);
    CHECK(adapter_match(kAdapterVendorAmd, EncoderVendor::Nvidia) == AdapterMatch::Mismatch);
    CHECK(adapter_match(kAdapterVendorIntel, EncoderVendor::Intel) == AdapterMatch::Confirmed);
    CHECK(adapter_match(kAdapterVendorIntel, EncoderVendor::Software) == AdapterMatch::Mismatch);
    for (const auto vendor : hardware) {
        const auto& backend = encoder_backend(vendor);
        CHECK(backend.supports_zero_copy_input());
        // Unknown adapter: mechanism exists, compatibility is not claimed.
        const auto unknown = request(2560, 1440, 90);
        CHECK(backend.zero_copy_adapter(unknown) == AdapterMatch::Unknown);
        const auto unverified = plan(vendor, unknown);
        CHECK(unverified.zero_copy && unverified.zero_copy_status == ZeroCopyStatus::Unverified && !unverified.confirmed());
        const auto confirmed = plan(vendor, on(vendor, request(2560, 1440, 90)));
        CHECK(confirmed.zero_copy_status == ZeroCopyStatus::Confirmed && confirmed.confirmed());
        CHECK(unverified.pool_capacity == confirmed.pool_capacity);
        // Other vendor's adapter: zero-copy impossible, readback still plannable.
        for (const auto other : hardware) if (other != vendor) {
            const auto foreign = on(other, request(2560, 1440, 90));
            CHECK(backend.zero_copy_adapter(foreign) == AdapterMatch::Mismatch);
            infeasible([&] { plan(vendor, foreign); });
            const auto readback = plan(vendor, foreign, false);
            CHECK(readback.zero_copy_status == ZeroCopyStatus::NotUsed && readback.confirmed());
        }
    }
    const auto order = [](uint32_t vendor) { return encoder_vendor_order(vendor); };
    CHECK((order(0) == std::vector{EncoderVendor::Nvidia, EncoderVendor::Amd, EncoderVendor::Intel, EncoderVendor::Software}));
    CHECK((order(kAdapterVendorNvidia) == order(0)));
    CHECK((order(kAdapterVendorAmd) == std::vector{EncoderVendor::Amd, EncoderVendor::Nvidia, EncoderVendor::Intel, EncoderVendor::Software}));
    CHECK((order(kAdapterVendorIntel) == std::vector{EncoderVendor::Intel, EncoderVendor::Nvidia, EncoderVendor::Amd, EncoderVendor::Software}));
    CHECK((encoder_vendor_order(kAdapterVendorAmd, true) == std::vector{EncoderVendor::Software}));
}

void stages_and_pool() {
    // Capture buffering and the pacing queue are reported, never pooled.
    for (const auto vendor : hardware) {
        auto shallow = on(vendor, request(2560, 1440, 90)); shallow.capture_buffers = 3; shallow.pacing_queue = 0;
        auto deep = shallow; deep.capture_buffers = 5; deep.pacing_queue = 12;
        const auto a = plan(vendor, shallow), b = plan(vendor, deep);
        CHECK(a.pool_capacity == b.pool_capacity && b.stages.capture_buffers == 5 && b.stages.pacing_queue == 12);
        auto overlay = shallow; overlay.overlay_stage = true;
        const auto o = plan(vendor, overlay);
        CHECK(o.stages.conversion_surfaces == 2 && o.pool_capacity == a.pool_capacity + 1);
    }
    auto overlay = request(1920, 1080, 60); overlay.overlay_stage = true;
    CHECK(plan(EncoderVendor::Software, overlay, false).pool_capacity == 3);
    EncoderStages shared; shared.input_shares_conversion = true;
    shared.encoder_input_surfaces = 5; shared.encoder_in_flight = 4; shared.conversion_surfaces = 1;
    shared.capture_buffers = 3; shared.pacing_queue = 12;
    CHECK(encoder_pool_requirement(shared, 1) == 7 && encoder_pool_requirement(shared, 0) == 6);
    EncoderStages staging = shared; staging.input_shares_conversion = false; staging.conversion_surfaces = 2;
    CHECK(encoder_pool_requirement(staging, 1) == 2);
    EncoderPolicy no_hold; no_hold.ffmpeg_hold = 0;
    CHECK(plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(2560, 1440, 90)), true, no_hold).pool_capacity == 8);
}

// Every plan the policy returns must satisfy the resource invariants; every
// request it cannot satisfy must be rejected rather than returned undersized.
void executable_plan_invariants() {
    struct Shape { int b, l; };
    constexpr Shape shapes[] = {{0, 0}, {2, 0}, {0, 10}, {2, 10}, {1, 8}, {3, 38}, {4, 55}, {0, 60}};
    int executable = 0, rejected = 0;
    for (const auto vendor : {EncoderVendor::Nvidia, EncoderVendor::Amd, EncoderVendor::Intel, EncoderVendor::Software})
    for (const auto& s : scenarios) for (const auto& shape : shapes)
    for (const bool zero_copy : {true, false}) for (const bool overlay : {false, true})
    for (const uint32_t adapter : {0u, kAdapterVendorNvidia, kAdapterVendorAmd, kAdapterVendorIntel})
    for (const auto codec : {EncoderCodec::H264, EncoderCodec::AV1})
    for (const auto format : {EncoderPixelFormat::NV12, EncoderPixelFormat::P010}) {
        auto value = request(s.width, s.height, s.fps, shape.b, shape.l);
        value.overlay_stage = overlay; value.adapter_vendor = adapter; value.codec = codec; value.pixel_format = format;
        EncoderPlan p;
        try { p = plan(vendor, value, zero_copy); }
        catch (const EncoderPlanInfeasible&) { ++rejected; continue; }
        ++executable;
        check_encoder_plan(p);
        const auto& st = p.stages;
        CHECK(st.capture_buffers >= 0 && st.pacing_queue >= 0 && st.conversion_surfaces >= 1 && st.encoder_input_surfaces >= 0 &&
            st.encoder_in_flight >= 0 && st.packet_buffers >= 0);
        CHECK(p.pool_capacity >= encoder_pool_requirement(st, p.ffmpeg_hold));
        CHECK(p.pool_capacity <= p.max_distinct_textures && p.max_distinct_textures <= kD3D11TextureArrayLimit);
        CHECK(p.min_encoder_slots <= p.encoder_slots && p.encoder_slots <= p.max_encoder_slots);
        CHECK(p.requested_codec == codec && (p.effective_codec == codec || vendor == EncoderVendor::Software));
        if (p.zero_copy) {
            CHECK(vendor != EncoderVendor::Software);
            CHECK(p.input == (vendor == EncoderVendor::Intel ? EncoderInput::QsvFrames : EncoderInput::D3D11Frames));
            CHECK(st.input_shares_conversion && p.staging_slots == 0 && p.zero_copy_status != ZeroCopyStatus::NotUsed);
            CHECK(p.pool_capacity >= st.encoder_input_surfaces + st.conversion_surfaces + p.ffmpeg_hold);
            CHECK((p.zero_copy_status == ZeroCopyStatus::Confirmed) == (adapter == adapter_of(vendor)));
        } else {
            CHECK(p.input == EncoderInput::SystemFrames && p.needs_cpu_staging && p.staging_slots >= 1 && p.cpu_frames >= 1);
        }
        if (vendor == EncoderVendor::Nvidia) CHECK(p.encoder_slots <= kNvencRegisteredResources);
        if (vendor == EncoderVendor::Amd) CHECK(p.encoder_slots <= kAmfMaxAsyncDepth && p.encoder_slots >= shape.b + shape.l + 1);
    }
    CHECK(executable > 0 && rejected > 0);
}

// check_encoder_plan rejects hand-built plans that break an invariant.
void invariant_violations() {
    const auto good = plan(EncoderVendor::Nvidia, on(EncoderVendor::Nvidia, request(2560, 1440, 90)));
    check_encoder_plan(good);
    const auto readback = plan(EncoderVendor::Software, request(2560, 1440, 90), false);
    check_encoder_plan(readback);
    auto p = good; p.pool_capacity -= 1; broken([&] { check_encoder_plan(p); });
    p = good; p.pool_capacity = 65; broken([&] { check_encoder_plan(p); });
    p = good; p.stages.encoder_in_flight = -1; broken([&] { check_encoder_plan(p); });
    p = good; p.stages.conversion_surfaces = 0; broken([&] { check_encoder_plan(p); });
    p = good; p.encoder_slots = 65; broken([&] { check_encoder_plan(p); });
    p = good; p.min_encoder_slots = p.encoder_slots + 1; broken([&] { check_encoder_plan(p); });
    p = good; p.input = EncoderInput::SystemFrames; broken([&] { check_encoder_plan(p); });
    p = good; p.input = EncoderInput::QsvFrames; broken([&] { check_encoder_plan(p); });
    p = good; p.zero_copy_status = ZeroCopyStatus::NotUsed; broken([&] { check_encoder_plan(p); });
    p = good; p.pixel_format = EncoderPixelFormat::P010; broken([&] { check_encoder_plan(p); });
    p = good; p.pool_bytes += 1; broken([&] { check_encoder_plan(p); });
    p = good; p.effective_codec = EncoderCodec::AV1; broken([&] { check_encoder_plan(p); });
    p = readback; p.zero_copy = true; p.needs_cpu_staging = false; p.staging_slots = 0; p.cpu_frames = 0;
    p.zero_copy_status = ZeroCopyStatus::Confirmed; p.stages.input_shares_conversion = true;
    broken([&] { check_encoder_plan(p); });
    p = readback; p.staging_slots = 0; broken([&] { check_encoder_plan(p); });
    p = readback; p.cpu_frames = 0; broken([&] { check_encoder_plan(p); });
    p = readback; p.zero_copy_status = ZeroCopyStatus::Confirmed; broken([&] { check_encoder_plan(p); });
}

int main() {
    helpers();
    surface_bytes();
    nvenc_scenarios();
    nvenc_reordering_and_lookahead();
    nvenc_resource_limits();
    nvenc_readback_and_tuning();
    amf_depth();
    amf_invariants();
    qsv_depth();
    software_fallback();
    codec_fallback();
    adapter_compatibility();
    stages_and_pool();
    executable_plan_invariants();
    invariant_violations();
    std::cout << "Encoder resource policy checks passed\n";
}
