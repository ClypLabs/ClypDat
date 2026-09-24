#include "encoder_backend.h"
#include <cstdlib>
#include <iostream>
#include <stdexcept>

#define CHECK(expression) do { if (!(expression)) { \
    std::cerr << __FILE__ << ':' << __LINE__ << ": " #expression " failed\n"; \
    std::exit(EXIT_FAILURE); } } while (false)

using namespace clypdat;

template<class Action> void must_throw(Action action) {
    bool threw = false;
    try { action(); } catch (const std::invalid_argument&) { threw = true; }
    CHECK(threw);
}
EncoderRequest request(int width, int height, int fps, int b_frames = 0, int lookahead = 0) {
    EncoderRequest value; value.width = width; value.height = height; value.fps = fps;
    value.bitrate_mbps = 25; value.b_frames = b_frames; value.lookahead = lookahead; return value;
}
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

void helpers() {
    CHECK(frames_for_latency(60, 60) == 4 && frames_for_latency(90, 60) == 6 && frames_for_latency(120, 60) == 8);
    CHECK(frames_for_latency(60, 50) == 3 && frames_for_latency(30, 1000) == 30);
    CHECK(nv12_frame_bytes(1920, 1080) == 3110400 && nv12_frame_bytes(2560, 1440) == 5529600);
    CHECK(nv12_frame_bytes(3840, 2160) == 12441600 && nv12_frame_bytes(1920, 1080, 16) == 3133440);
    must_throw([] { frames_for_latency(0, 60); });
    must_throw([] { nv12_frame_bytes(0, 1080); });
    must_throw([] { validate_encoder_request(request(1921, 1080, 60)); });
    must_throw([] { validate_encoder_request(request(1920, 1080, 121)); });
    must_throw([] { validate_encoder_request(request(1920, 1080, 29)); });
    must_throw([] { validate_encoder_request(request(1920, 1080, 60, 5)); });
    must_throw([] { validate_encoder_request(request(1920, 1080, 60, 0, 101)); });
    EncoderPolicy broken; broken.nvenc_min_delay = 9;
    must_throw([&] { plan(EncoderVendor::Nvidia, request(1920, 1080, 60), true, broken); });
}

void nvenc_scenarios() {
    // delay, surfaces, in flight, pool for each scenario at the 60 ms default.
    const int expected[][4] = {{4, 6, 5, 7}, {4, 6, 5, 7}, {6, 8, 7, 9}, {8, 10, 9, 11}, {4, 6, 5, 7}};
    for (size_t i = 0; i < std::size(scenarios); ++i) {
        const auto& s = scenarios[i];
        const auto p = plan(EncoderVendor::Nvidia, request(s.width, s.height, s.fps));
        CHECK(p.codec_name == "h264_nvenc" && p.input == EncoderInput::D3D11Frames && p.sw_format == EncoderPixelFormat::NV12);
        CHECK(p.zero_copy && !p.needs_cpu_staging && p.staging_slots == 0 && p.cpu_frames == 0 && p.fixed_pool);
        CHECK(option(p, "delay") == std::to_string(expected[i][0]) && option(p, "surfaces") == std::to_string(expected[i][1]));
        CHECK(option(p, "rc-lookahead").empty());
        CHECK(p.min_input_surfaces == 4 && p.preferred_surfaces == expected[i][1]);
        CHECK(p.max_in_flight == expected[i][2] && p.output_delay_frames == expected[i][0] && p.reorder_lookahead_extra == 0);
        CHECK(p.pool_capacity == expected[i][3] && !p.pool_clamped && p.max_distinct_textures == kNvencRegisteredResources);
        CHECK(p.pool_bytes == uint64_t(p.pool_capacity) * nv12_frame_bytes(s.width, s.height));
        CHECK(p.stages.input_shares_conversion && p.stages.conversion_surfaces == 1);
        CHECK(p.stages.encoder_input_surfaces == p.max_in_flight && p.stages.packet_buffers == expected[i][1]);
    }
    CHECK(plan(EncoderVendor::Nvidia, request(2560, 1440, 90)).pool_bytes == 9ull * 5529600);
    auto av1 = request(2560, 1440, 90); av1.codec = EncoderCodec::AV1;
    CHECK(plan(EncoderVendor::Nvidia, av1).codec_name == "av1_nvenc");
}

void nvenc_reordering_and_lookahead() {
    // FFmpeg requires 4*(B+1) surfaces; reorder adds B frames of delay.
    const auto b = plan(EncoderVendor::Nvidia, request(2560, 1440, 90, 2));
    CHECK(b.min_input_surfaces == 12 && option(b, "surfaces") == "12" && option(b, "delay") == "6");
    CHECK(b.max_in_flight == 9 && b.output_delay_frames == 8 && b.reorder_lookahead_extra == 2 && b.pool_capacity == 11);
    // Lookahead needs L+(B+1)+5 surfaces and delay L+(B+1)+4 to stay unclipped.
    const auto l = plan(EncoderVendor::Nvidia, request(2560, 1440, 90, 0, 20));
    CHECK(l.min_input_surfaces == 26 && option(l, "delay") == "25" && option(l, "surfaces") == "27");
    CHECK(option(l, "rc-lookahead") == "20" && l.max_in_flight == 26 && l.pool_capacity == 28 && l.reorder_lookahead_extra == 20);
    const auto both = plan(EncoderVendor::Nvidia, request(1920, 1080, 60, 1, 8));
    CHECK(both.min_input_surfaces == 15 && option(both, "delay") == "14" && option(both, "surfaces") == "16");
    CHECK(both.max_in_flight == 16 && both.output_delay_frames == 15);
    // Registered-resource table: surfaces never exceed 64; lookahead that
    // cannot fit is rejected instead of silently clipped.
    must_throw([] { plan(EncoderVendor::Nvidia, request(1920, 1080, 60, 0, 60)); });
    auto deep = request(1920, 1080, 60, 0, 57); deep.overlay_stage = true;
    const auto clamped = plan(EncoderVendor::Nvidia, deep);
    CHECK(option(clamped, "surfaces") == "64" && option(clamped, "delay") == "62" && clamped.max_in_flight == 63);
    CHECK(clamped.pool_clamped && clamped.pool_capacity == kNvencRegisteredResources);
}

void nvenc_readback_and_tuning() {
    const auto p = plan(EncoderVendor::Nvidia, request(2560, 1440, 90), false);
    CHECK(p.input == EncoderInput::SystemNV12 && p.needs_cpu_staging && !p.zero_copy);
    CHECK(option(p, "surfaces") == "4" && option(p, "delay") == "3" && p.max_in_flight == 4);
    CHECK(p.staging_slots == 2 && p.cpu_frames == 1 && !p.stages.input_shares_conversion);
    CHECK(p.stages.encoder_input_surfaces == 4 && p.stages.conversion_surfaces == 2 && p.pool_capacity == 2);
    // Budget and bounds are policy, not constants.
    EncoderPolicy wide; wide.latency_budget_ms = 120; wide.nvenc_max_delay = 12;
    const auto tuned = plan(EncoderVendor::Nvidia, request(2560, 1440, 90), true, wide);
    CHECK(option(tuned, "delay") == "11" && option(tuned, "surfaces") == "13" && tuned.pool_capacity == 14);
    EncoderPolicy lean; lean.nvenc_min_delay = 2; lean.latency_budget_ms = 20; lean.nvenc_surface_slack = 1;
    const auto small = plan(EncoderVendor::Nvidia, request(1920, 1080, 60), true, lean);
    CHECK(option(small, "delay") == "2" && option(small, "surfaces") == "4" && small.max_in_flight == 3);
}

void amf_depth() {
    const int expected[][2] = {{4, 6}, {4, 6}, {6, 8}, {8, 10}, {4, 6}};
    for (size_t i = 0; i < std::size(scenarios); ++i) {
        const auto& s = scenarios[i];
        const auto p = plan(EncoderVendor::Amd, request(s.width, s.height, s.fps));
        CHECK(p.codec_name == "h264_amf" && p.input == EncoderInput::D3D11Frames && p.frames_from_encoder_ctx);
        CHECK(option(p, "async_depth") == std::to_string(expected[i][0]) && option(p, "bf") == "0" && option(p, "preanalysis") == "0");
        CHECK(p.low_delay_flag && p.output_delay_frames == 0 && p.max_in_flight == expected[i][0]);
        CHECK(p.pool_capacity == expected[i][1] && !p.right_size_packets && p.max_distinct_textures == kD3D11TextureArrayLimit);
    }
    const auto b = plan(EncoderVendor::Amd, request(2560, 1440, 90, 2));
    CHECK(!b.low_delay_flag && b.output_delay_frames == 3 && option(b, "bf") == "2" && b.min_input_surfaces == 4);
    const auto l = plan(EncoderVendor::Amd, request(2560, 1440, 90, 0, 10));
    CHECK(option(l, "async_depth") == "11" && option(l, "preanalysis") == "1" && option(l, "pa_lookahead_buffer_depth") == "10");
    CHECK(l.output_delay_frames == 10 && l.max_in_flight == 11 && l.pool_capacity == 13);
    must_throw([] { plan(EncoderVendor::Amd, request(1920, 1080, 60, 4)); });
    must_throw([] { plan(EncoderVendor::Amd, request(1920, 1080, 60, 0, 42)); });
    const auto readback = plan(EncoderVendor::Amd, request(2560, 1440, 90), false);
    CHECK(readback.needs_cpu_staging && !readback.frames_from_encoder_ctx && readback.pool_capacity == 2);
    auto av1 = request(2560, 1440, 90); av1.codec = EncoderCodec::AV1;
    CHECK(plan(EncoderVendor::Amd, av1).codec_name == "av1_amf");
}

void qsv_depth() {
    const int expected[][2] = {{4, 7}, {4, 7}, {6, 9}, {6, 9}, {4, 7}};
    for (size_t i = 0; i < std::size(scenarios); ++i) {
        const auto& s = scenarios[i];
        const auto p = plan(EncoderVendor::Intel, request(s.width, s.height, s.fps));
        CHECK(p.codec_name == "h264_qsv" && p.input == EncoderInput::QsvFrames && p.frames_from_encoder_ctx && p.fixed_pool);
        CHECK(option(p, "async_depth") == std::to_string(expected[i][0]) && option(p, "look_ahead").empty());
        CHECK(p.max_in_flight == expected[i][0] && p.output_delay_frames == expected[i][0]);
        CHECK(p.min_input_surfaces == expected[i][0] + 1 && p.pool_capacity == expected[i][1]);
        CHECK(p.right_size_packets && p.surface_alignment == 16);
        CHECK(p.pool_bytes == uint64_t(p.pool_capacity) * nv12_frame_bytes(s.width, s.height, 16));
    }
    const auto l = plan(EncoderVendor::Intel, request(2560, 1440, 90, 1, 10));
    CHECK(l.max_in_flight == 17 && l.output_delay_frames == 17 && l.min_input_surfaces == 18 && l.pool_capacity == 20);
    CHECK(option(l, "look_ahead") == "1" && option(l, "look_ahead_depth") == "10");
    auto av1 = request(2560, 1440, 90, 0, 10); av1.codec = EncoderCodec::AV1;
    const auto a = plan(EncoderVendor::Intel, av1);
    CHECK(a.codec_name == "av1_qsv" && option(a, "extbrc") == "1" && option(a, "look_ahead").empty());
    EncoderPolicy deeper; deeper.qsv_max_depth = 10;
    CHECK(option(plan(EncoderVendor::Intel, request(2560, 1440, 120), true, deeper), "async_depth") == "8");
    const auto readback = plan(EncoderVendor::Intel, request(1920, 1080, 60), false);
    CHECK(readback.input == EncoderInput::SystemNV12 && readback.needs_cpu_staging && readback.right_size_packets);
    CHECK(readback.pool_capacity == 2 && readback.stages.encoder_input_surfaces == 5);
}

void software_fallback() {
    for (const auto& s : scenarios) {
        const auto p = plan(EncoderVendor::Software, request(s.width, s.height, s.fps), false);
        CHECK(p.codec_name == "libx264" && p.input == EncoderInput::SystemNV12 && p.needs_cpu_staging);
        CHECK(p.max_in_flight == 0 && p.output_delay_frames == 0 && p.staging_slots == 2 && p.cpu_frames == 1);
        CHECK(p.pool_capacity == 2 && p.stages.packet_buffers == 0 && p.options.empty());
    }
    must_throw([] { plan(EncoderVendor::Software, request(1920, 1080, 60), true); });
    CHECK(!encoder_backend(EncoderVendor::Software).supports_zero_copy(request(1920, 1080, 60)));
    auto av1 = request(1920, 1080, 60); av1.codec = EncoderCodec::AV1;
    CHECK(plan(EncoderVendor::Software, av1, false).codec == EncoderCodec::H264);
}

void stages_and_pool() {
    // Capture buffering and the pacing queue are reported, never pooled.
    auto shallow = request(2560, 1440, 90); shallow.capture_buffers = 3; shallow.pacing_queue = 0;
    auto deep = shallow; deep.capture_buffers = 5; deep.pacing_queue = 12;
    for (const auto vendor : {EncoderVendor::Nvidia, EncoderVendor::Amd, EncoderVendor::Intel}) {
        const auto a = plan(vendor, shallow), b = plan(vendor, deep);
        CHECK(a.pool_capacity == b.pool_capacity && b.stages.capture_buffers == 5 && b.stages.pacing_queue == 12);
        auto overlay = shallow; overlay.overlay_stage = true;
        const auto o = plan(vendor, overlay);
        CHECK(o.stages.conversion_surfaces == 2 && o.pool_capacity == a.pool_capacity + 1);
    }
    auto overlay = request(1920, 1080, 60); overlay.overlay_stage = true;
    CHECK(plan(EncoderVendor::Software, overlay, false).pool_capacity == 3);
    EncoderStages zero_copy; zero_copy.input_shares_conversion = true;
    zero_copy.encoder_input_surfaces = 5; zero_copy.encoder_in_flight = 4; zero_copy.conversion_surfaces = 1;
    zero_copy.capture_buffers = 3; zero_copy.pacing_queue = 12;
    CHECK(encoder_pool_requirement(zero_copy, 1) == 7 && encoder_pool_requirement(zero_copy, 0) == 6);
    EncoderStages staging = zero_copy; staging.input_shares_conversion = false; staging.conversion_surfaces = 2;
    CHECK(encoder_pool_requirement(staging, 1) == 2);
    EncoderPolicy no_hold; no_hold.ffmpeg_hold = 0;
    CHECK(plan(EncoderVendor::Nvidia, request(2560, 1440, 90), true, no_hold).pool_capacity == 8);
}

void adapter_selection() {
    const auto order = [](uint32_t vendor) { return encoder_vendor_order(vendor); };
    CHECK((order(0) == std::vector{EncoderVendor::Nvidia, EncoderVendor::Amd, EncoderVendor::Intel, EncoderVendor::Software}));
    CHECK((order(kAdapterVendorNvidia) == order(0)));
    CHECK((order(kAdapterVendorAmd) == std::vector{EncoderVendor::Amd, EncoderVendor::Nvidia, EncoderVendor::Intel, EncoderVendor::Software}));
    CHECK((order(kAdapterVendorIntel) == std::vector{EncoderVendor::Intel, EncoderVendor::Nvidia, EncoderVendor::Amd, EncoderVendor::Software}));
    CHECK((encoder_vendor_order(kAdapterVendorAmd, true) == std::vector{EncoderVendor::Software}));
    auto amd = request(1920, 1080, 60); amd.adapter_vendor = kAdapterVendorAmd;
    CHECK(!encoder_backend(EncoderVendor::Nvidia).supports_zero_copy(amd));
    CHECK(encoder_backend(EncoderVendor::Amd).supports_zero_copy(amd));
    CHECK(!encoder_backend(EncoderVendor::Intel).supports_zero_copy(amd));
    const auto unknown = request(1920, 1080, 60);
    CHECK(encoder_backend(EncoderVendor::Nvidia).supports_zero_copy(unknown));
    CHECK(encoder_backend(EncoderVendor::Intel).supports_zero_copy(unknown));
}

int main() {
    helpers();
    nvenc_scenarios();
    nvenc_reordering_and_lookahead();
    nvenc_readback_and_tuning();
    amf_depth();
    qsv_depth();
    software_fallback();
    stages_and_pool();
    adapter_selection();
    std::cout << "Encoder resource policy checks passed\n";
}
