// Burned overlays through the real capture pipeline: GPU composition on
// zero-copy encoders compared pixel by pixel with the CPU reference, the CPU
// paths, lifecycle (recovery, pool pressure, resize, pause, generation and
// transform changes), recorder-session health, and --overlay-bench.
#include "recording_capture.h"
#include "recorder_session.h"
#if __has_include("overlay_compositor.h")
#define CLYPDAT_GPU_OVERLAYS 1
#endif
#include <Windows.h>
#include <d3d11_4.h>
#include <dxgi1_4.h>
#include <psapi.h>
#include <wrl/client.h>
#include <fcntl.h>
#include <io.h>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <functional>
#include <iomanip>
#include <iostream>
#include <map>
#include <sstream>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
}

#define CHECK(x) do { if (!(x)) throw std::runtime_error("Overlay assertion at line " + std::to_string(__LINE__) + ": " #x); } while (false)
using namespace clypdat;
using namespace std::chrono_literals;
using Microsoft::WRL::ComPtr;

namespace {
struct FrameFree { void operator()(AVFrame* frame) const { av_frame_free(&frame); } };
using FramePtr = std::unique_ptr<AVFrame, FrameFree>;
int64_t monotonic_us() {
    LARGE_INTEGER ticks{}, frequency{}; QueryPerformanceCounter(&ticks); QueryPerformanceFrequency(&frequency);
    return int64_t(ticks.QuadPart * (1000000.L / frequency.QuadPart));
}

// The same gradient every frame, from a GPU texture or system memory. Its
// size can change mid-recording, as a resized window's does.
class PatternSource final : public RecordingFrameSource {
    std::atomic<int> width_, height_;
    int fps_, made_width_ = 0, made_height_ = 0;
    bool gpu_;
    std::chrono::steady_clock::time_point next_ = std::chrono::steady_clock::now();
    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11Texture2D> texture_;
    std::vector<uint8_t> pixels_;
    void make() {
        const int width = width_, height = height_;
        pixels_.resize(size_t(width) * height * 4);
        for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
            auto* p = &pixels_[(size_t(y) * width + x) * 4];
            p[0] = uint8_t(x * 255 / (width - 1)); p[1] = uint8_t(y * 255 / (height - 1));
            p[2] = ((x / 64 + y / 64) & 1) ? 200 : 60; p[3] = 255;
        }
        if (device_) {
            texture_.Reset();
            D3D11_TEXTURE2D_DESC desc{}; desc.Width = UINT(width); desc.Height = UINT(height); desc.MipLevels = 1; desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
            D3D11_SUBRESOURCE_DATA data{pixels_.data(), UINT(width * 4), 0};
            CHECK(SUCCEEDED(device_->CreateTexture2D(&desc, &data, &texture_)));
        }
        made_width_ = width; made_height_ = height;
    }
public:
    PatternSource(int width, int height, int fps, bool gpu) : width_(width), height_(height), fps_(fps), gpu_(gpu) {
        if (gpu) {
            ComPtr<ID3D11DeviceContext> context;
            CHECK(SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0,
                D3D11_SDK_VERSION, &device_, nullptr, &context)));
            ComPtr<ID3D11Multithread> protection; CHECK(SUCCEEDED(context.As(&protection))); protection->SetMultithreadProtected(TRUE);
        }
        make();
    }
    void resize(int width, int height) { width_ = width; height_ = height; }
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
        const auto now = std::chrono::steady_clock::now();
        if (now < next_) { std::this_thread::sleep_for(std::min(timeout, std::chrono::duration_cast<std::chrono::milliseconds>(next_ - now) + 1ms)); return false; }
        next_ += std::chrono::microseconds(1000000 / fps_);
        if (width_ != made_width_ || height_ != made_height_) make();
        pixels.width = made_width_; pixels.height = made_height_; pixels.stride = made_width_ * 4;
        if (gpu_) { texture_->AddRef(); pixels.texture = {texture_.Get(), [](auto* p) { p->Release(); }}; pixels.bgra.clear(); }
        else { pixels.texture.reset(); pixels.bgra = pixels_; }
        return true;
    }
    bool eligible() const override { return true; }
    const char* name() const override { return "generated overlay pattern"; }
    void set_frame_rate(int fps) override { fps_ = fps; }
    ID3D11Device* d3d_device() const override { return device_.Get(); }
};

// A test bitmap from straight BGRA; premultiplied bitmaps store colour
// multiplied by alpha, as Skia artwork does.
using Texel = std::array<uint8_t, 4>;
std::shared_ptr<const OverlayBitmap> bitmap(int width, int height, bool premultiplied, uint64_t revision,
    const std::function<Texel(int, int)>& texel) {
    std::vector<uint8_t> pixels(size_t(width) * height * 4);
    for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
        auto value = texel(x, y); auto* p = &pixels[(size_t(y) * width + x) * 4];
        if (premultiplied) for (int c = 0; c < 3; ++c) value[c] = uint8_t((value[c] * value[3] + 127) / 255);
        std::memcpy(p, value.data(), 4);
    }
    return OverlayBitmap::copy(width, height, width * 4, pixels.data(), pixels.size(), revision, monotonic_us(), premultiplied);
}
std::shared_ptr<const OverlayBitmap> camera_a() {
    return bitmap(640, 360, false, 1, [](int x, int y) { return Texel{uint8_t(x * 255 / 639), uint8_t(((x / 40 + y / 40) & 1) ? 200 : 40), uint8_t(y * 255 / 359), 255}; });
}
std::shared_ptr<const OverlayBitmap> camera_c() {
    return bitmap(640, 360, false, 2, [](int x, int y) { return Texel{220, uint8_t(x * 255 / 639), uint8_t(((x / 20) & 1) ? 30 : 160), 255}; });
}
// Alpha ramps 0..255 left to right over a two-tone pattern.
std::shared_ptr<const OverlayBitmap> keyboard_ramp(bool premultiplied, uint64_t revision) {
    return bitmap(400, 150, premultiplied, revision, [](int x, int y) {
        return Texel{uint8_t(((y / 25) & 1) ? 250 : 20), 240, uint8_t(x * 255 / 399), uint8_t(x * 255 / 399)}; });
}
// Transparent, half and opaque bands.
std::shared_ptr<const OverlayBitmap> keyboard_bands(bool premultiplied, uint64_t revision) {
    return bitmap(300, 120, premultiplied, revision, [](int, int y) {
        const int band = (y / 10) % 3; return Texel{30, 90, 250, uint8_t(band == 0 ? 0 : band == 1 ? 128 : 255)}; });
}

#if CLYPDAT_GPU_OVERLAYS
FramePtr copy_frame(const AVFrame& frame) {
    FramePtr copy(av_frame_alloc()); CHECK(copy);
    // Hardware surfaces download as NV12; decoded frames keep their format.
    copy->format = frame.hw_frames_ctx ? AV_PIX_FMT_NV12 : frame.format; copy->width = frame.width; copy->height = frame.height;
    CHECK(av_frame_get_buffer(copy.get(), 0) == 0);
    if (frame.hw_frames_ctx) CHECK(av_hwframe_transfer_data(copy.get(), &frame, 0) == 0);
    else if (const int copied = av_frame_copy(copy.get(), &frame); copied < 0)
        throw std::runtime_error("Copy encoder input: " + std::to_string(copied) + " format=" + std::to_string(frame.format) +
            " size=" + std::to_string(frame.width) + "x" + std::to_string(frame.height));
    copy->pts = frame.pts;
    return copy;
}

// The overlay layers the capture reads, versioned, with the encoder input of
// chosen versions grabbed on its way into the encoder.
struct Scene {
    std::mutex mutex;
    std::condition_variable changed;
    OverlayFrame layers;
    uint64_t version = 0;
    std::map<int64_t, uint64_t> served; // pts -> version its overlays came from
    std::map<int64_t, OverlayCompositionResult> drawn;
    uint64_t grab_version = UINT64_MAX; size_t grab_count = 0;
    std::vector<FramePtr> grabbed;
    // New layers; the first `grab` frames composed from them are kept.
    uint64_t set(OverlayFrame value, size_t grab = 1) {
        std::lock_guard lock(mutex);
        layers = std::move(value); ++version;
        grab_version = version; grab_count = grab; grabbed.clear();
        return version;
    }
    std::vector<FramePtr> take(std::chrono::milliseconds timeout = 5s) {
        std::unique_lock lock(mutex);
        if (!changed.wait_for(lock, timeout, [&] { return grabbed.size() >= grab_count; }))
            throw std::runtime_error("No encoder input composed from overlay version " + std::to_string(grab_version));
        grab_version = UINT64_MAX; return std::move(grabbed);
    }
    void offer(const AVFrame& frame) {
        std::lock_guard lock(mutex);
        const auto version_of = served.find(frame.pts);
        if (version_of == served.end() || version_of->second != grab_version || grabbed.size() >= grab_count) return;
        for (const auto& previous : grabbed) if (previous->pts == frame.pts) return; // Resent after recovery.
        grabbed.push_back(copy_frame(frame)); changed.notify_all();
    }
    OverlayCompositionResult result(int64_t pts) { std::lock_guard lock(mutex); return drawn.at(pts); }
};
OverlayFrame layers(std::shared_ptr<const OverlayBitmap> camera, OverlayTransformNative camera_at,
    std::shared_ptr<const OverlayBitmap> keyboard, OverlayTransformNative keyboard_at, uint64_t generation = 1) {
    OverlayFrame frame; frame.burned = true; frame.camera_generation = generation;
    frame.camera = {camera != nullptr, std::move(camera), camera_at};
    frame.keyboard = {keyboard != nullptr, std::move(keyboard), keyboard_at};
    return frame;
}
OverlayFrame nothing() { OverlayFrame frame; frame.burned = true; return frame; }
constexpr OverlayTransformNative kCameraAt{.7, .05, .25}, kKeyboardAt{.1005, .7, .35}, kMovedAt{.4, .45, .2};
// A fully transparent layer: the composited (two-pass) frame with nothing visible.
OverlayFrame clear_layer() {
    static const auto transparent = bitmap(64, 64, true, 10, [](int, int) { return Texel{255, 255, 255, 0}; });
    return layers(nullptr, {}, transparent, kKeyboardAt);
}
// Differences by zone: inside a layer (2 px in from its edge), on its edge,
// and outside every layer. A chroma sample whose four pixels have different
// layer alpha counts as edge: the CPU reference blends their mean alpha onto
// already subsampled chroma, the GPU blends each pixel and then subsamples,
// so the two differ there where the frame under the layer changes sharply.
struct Zone { int max = 0; double sum = 0; size_t count = 0; double mean() const { return count ? sum / double(count) : 0; }
    void add(int difference) { max = std::max(max, difference); sum += difference; ++count; } };
struct Difference { Zone inside_y, edge_y, outside_y, inside_uv, edge_uv, outside_uv; };
std::vector<OverlayPlacement> placements(const OverlayFrame& frame, int width, int height) {
    std::vector<OverlayPlacement> result;
    for (const auto* layer : {&frame.camera, &frame.keyboard})
        if (layer->requested && layer->bitmap) result.push_back(overlay_placement(*layer->bitmap, layer->transform, width, height));
    return result;
}
// 0 outside, 1 inside, 2 edge: within `band` of any placement's border.
int zone(const std::vector<OverlayPlacement>& list, int x, int y, int band) {
    int result = 0;
    for (const auto& p : list) {
        const bool nearby = x >= p.x - band && y >= p.y - band && x < p.x + p.width + band && y < p.y + p.height + band;
        if (!nearby) continue;
        const bool deep = x >= p.x + band && y >= p.y + band && x < p.x + p.width - band && y < p.y + p.height - band;
        if (!deep) return 2;
        result = 1;
    }
    return result;
}
// A layer's alpha at a frame pixel, by the reference's nearest-texel mapping.
int layer_alpha(const OverlayLayer& layer, const OverlayPlacement& p, int x, int y) {
    if (x < p.x || y < p.y || x >= p.x + p.width || y >= p.y + p.height) return 0;
    const auto& bitmap = *layer.bitmap;
    return bitmap.bgra[size_t((y - p.y) * bitmap.height / p.height) * bitmap.stride + size_t((x - p.x) * bitmap.width / p.width) * 4 + 3];
}
Difference difference(const AVFrame& a, const AVFrame& b, const OverlayFrame& layers) {
    CHECK(a.width == b.width && a.height == b.height);
    const auto list = placements(layers, a.width, a.height);
    std::vector<std::pair<const OverlayLayer*, OverlayPlacement>> drawn;
    for (const auto* layer : {&layers.camera, &layers.keyboard})
        if (layer->requested && layer->bitmap) drawn.push_back({layer, overlay_placement(*layer->bitmap, layer->transform, a.width, a.height)});
    auto alpha_varies = [&](int x, int y) {
        for (const auto& [layer, p] : drawn) {
            const int first = layer_alpha(*layer, p, x, y);
            if (layer_alpha(*layer, p, x + 1, y) != first || layer_alpha(*layer, p, x, y + 1) != first || layer_alpha(*layer, p, x + 1, y + 1) != first) return true;
        }
        return false;
    };
    Difference result;
    for (int y = 0; y < a.height; ++y) for (int x = 0; x < a.width; ++x) {
        const int d = std::abs(int(a.data[0][y * a.linesize[0] + x]) - int(b.data[0][y * b.linesize[0] + x]));
        const int z = zone(list, x, y, 2);
        (z == 0 ? result.outside_y : z == 1 ? result.inside_y : result.edge_y).add(d);
    }
    for (int y = 0; y < a.height / 2; ++y) for (int x = 0; x < a.width / 2; ++x) for (int c = 0; c < 2; ++c) {
        const int d = std::abs(int(a.data[1][y * a.linesize[1] + x * 2 + c]) - int(b.data[1][y * b.linesize[1] + x * 2 + c]));
        const int z = zone(list, x * 2, y * 2, 4);
        (z == 0 ? result.outside_uv : z == 1 && !alpha_varies(x * 2, y * 2) ? result.inside_uv : result.edge_uv).add(d);
    }
    return result;
}
std::string describe(const Difference& d) {
    std::ostringstream text; text << std::fixed << std::setprecision(3);
    auto zone_text = [&](const char* name, const Zone& z) { text << ' ' << name << "=" << z.max << "/" << z.mean(); };
    zone_text("insideY", d.inside_y); zone_text("edgeY", d.edge_y); zone_text("outsideY", d.outside_y);
    zone_text("insideUV", d.inside_uv); zone_text("edgeUV", d.edge_uv); zone_text("outsideUV", d.outside_uv);
    return text.str();
}
// GPU composition against the CPU reference: layer interiors and everything
// outside the layers within rounding of the two conversion passes; layer
// edges also allow for the video processor's chroma siting.
void expect_match(const AVFrame& gpu, const AVFrame& reference, const OverlayFrame& frame, const std::string& label) {
    const auto d = difference(gpu, reference, frame);
    std::cout << "  " << label << ":" << describe(d) << "\n";
    const bool matches = d.inside_y.max <= 3 && d.inside_y.mean() <= 1 && d.outside_y.max <= 2 && d.outside_y.mean() <= .5 &&
        d.edge_y.max <= 3 && d.inside_uv.max <= 3 && d.inside_uv.mean() <= 1 && d.outside_uv.max <= 2 && d.outside_uv.mean() <= .5 &&
        d.edge_uv.max <= 40 && d.edge_uv.mean() <= 6;
    if (!matches) throw std::runtime_error(label + " differs from the CPU reference:" + describe(d));
}
void expect_exact(const AVFrame& a, const AVFrame& b, const std::string& label) {
    const auto d = difference(a, b, OverlayFrame{});
    if (d.outside_y.max || d.outside_uv.max) throw std::runtime_error(label + " is not bit-exact:" + describe(d));
}
FramePtr reference(const AVFrame& base, const OverlayFrame& frame) {
    auto result = copy_frame(base); compose_overlay_nv12(*result, frame); return result;
}

// Wraps every encoder the capture opens so the scene sees its input.
struct EncoderFactory {
    std::shared_ptr<Scene> scene;
    bool stand_in = false;   // AMF and QSV plans run on an NVENC or libx264 context.
    int fail_first_at = 0;   // The first encoder fails on this frame.
    std::function<CodecCalls(CodecCalls)> wrap;
    std::unique_ptr<VideoEncoder> operator()(VideoEncoderConfig value, size_t candidate) const {
        CodecCalls calls;
        if (candidate == 0 && fail_first_at) {
            auto sent = std::make_shared<int>(0);
            calls.send = [sent, at = fail_first_at](AVCodecContext* context, const AVFrame* frame) {
                if (frame && ++*sent == at) return AVERROR_EXTERNAL; return avcodec_send_frame(context, frame); };
        }
        if (wrap) calls = wrap(std::move(calls));
        calls.send = [scene = scene, send = calls.send](AVCodecContext* context, const AVFrame* frame) {
            if (frame) scene->offer(*frame); return send(context, frame); };
        if (stand_in) { value.name = value.hardware_frames ? "h264_nvenc" : "libx264"; value.resource_options.clear(); value.codec_flags = 0; }
        return std::make_unique<VideoEncoder>(value, std::move(calls));
    }
};
struct Rig {
    std::shared_ptr<Scene> scene = std::make_shared<Scene>();
    PatternSource* source = nullptr;
    std::mutex mutex;
    std::vector<Packet> packets;
    std::shared_ptr<const CaptureGeneration> generation;
    std::atomic<uint64_t> packet_count{0};
    std::unique_ptr<RecordingCapture> capture;
    Rig(RecordingCaptureConfig config, RecordingCaptureDependencies dependencies, EncoderFactory factory, int source_width, int source_height, bool gpu = true) {
        factory.scene = scene;
        dependencies.open_encoder = factory;
        RecordingCaptureCallbacks callbacks;
        auto s = scene;
        callbacks.overlay_enabled = [] { return true; };
        callbacks.overlay_frame = [s](int64_t pts) { std::lock_guard lock(s->mutex); s->served[pts] = s->version; return s->layers; };
        callbacks.overlay_composed = [s](int64_t pts, const OverlayFrame&, OverlayCompositionResult drawn) {
            std::lock_guard lock(s->mutex); s->drawn[pts] = drawn; };
        callbacks.generation = [this](auto value) { std::lock_guard lock(mutex); generation = value; packets.clear(); };
        callbacks.packet = [this](auto, Packet packet, int64_t, bool) { std::lock_guard lock(mutex); packets.push_back(std::move(packet)); ++packet_count; };
        scene->set(nothing(), 0);
        auto owned = std::make_unique<PatternSource>(source_width, source_height, config.fps, gpu); source = owned.get();
        capture = std::make_unique<RecordingCapture>(config, std::move(callbacks), std::move(owned), std::move(dependencies));
        capture->start();
    }
    ~Rig() { if (capture) capture->stop(); }
    std::string state() const {
        const auto h = capture->health();
        return " path=" + h.overlay.path + " gpuFailure='" + h.overlay.gpu_failure + "' roundTrips=" + std::to_string(h.overlay.cpu_roundtrips) +
            " processing=" + h.processing_path + " encoder=" + h.encoder + " error=" + h.error;
    }
    // Encoder input composed from `value`.
    FramePtr frame(OverlayFrame value) { scene->set(std::move(value)); return std::move(scene->take().front()); }
};
RecordingCaptureConfig capture_config(int width, int height, int fps) {
    RecordingCaptureConfig config; config.width = width; config.height = height; config.fps = fps; config.bitrate_mbps = 40;
    return config;
}
template<class Predicate> bool eventually(Predicate predicate, std::chrono::milliseconds timeout) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline) { if (predicate()) return true; std::this_thread::sleep_for(10ms); }
    return predicate();
}
// Mean luma and chroma over a placement's interior.
std::array<double, 3> region_mean(const AVFrame& frame, const OverlayPlacement& p) {
    double y = 0, u = 0, v = 0; size_t luma = 0, chroma = 0;
    for (int row = p.y + 4; row < p.y + p.height - 4; ++row) for (int column = p.x + 4; column < p.x + p.width - 4; ++column) {
        y += frame.data[0][row * frame.linesize[0] + column]; ++luma;
        if (row & 1 || column & 1) continue;
        if (frame.format == AV_PIX_FMT_NV12) { u += frame.data[1][(row / 2) * frame.linesize[1] + column]; v += frame.data[1][(row / 2) * frame.linesize[1] + column + 1]; }
        else { u += frame.data[1][(row / 2) * frame.linesize[1] + column / 2]; v += frame.data[2][(row / 2) * frame.linesize[2] + column / 2]; }
        ++chroma;
    }
    return {y / double(luma), u / double(chroma), v / double(chroma)};
}

// GPU composition at one output size: pixels against the CPU reference for
// both alpha kinds, disable, transform change, camera generation replacement,
// a layer that is not ready, then decoded output. Uploads happen per bitmap,
// never per frame, and nothing is read back.
void composition_matches_cpu(int width, int height, int fps, int source_width, int source_height) {
    std::cout << "GPU overlay composition " << width << "x" << height << "@" << fps << " from " << source_width << "x" << source_height << "\n";
    RecordingCaptureDependencies dependencies; dependencies.candidates = {{"h264_nvenc", false, true}};
    Rig rig(capture_config(width, height, fps), std::move(dependencies), {}, source_width, source_height);
    const auto plain = rig.frame(nothing());
    // Composited frames are scaled into the canvas and converted separately.
    // A fully transparent layer shows that two-pass frame alone; it matches
    // the single pass in luma, and in chroma away from sharp colour edges
    // of a scaled source, where the two passes filter chroma differently.
    const auto base = rig.frame(clear_layer());
    {
        const auto passes = difference(*base, *plain, OverlayFrame{});
        std::cout << "  two-pass vs single-pass:" << describe(passes) << "\n";
        if (passes.outside_y.max > 2 || passes.outside_y.mean() > .1 || passes.outside_uv.mean() > 1.5 ||
            (source_width == width && source_height == height && passes.outside_uv.max > 2))
            throw std::runtime_error("Two-pass conversion differs:" + describe(passes));
    }
    // Camera (straight, opaque) under keyboard (premultiplied alpha ramp).
    const auto camera = camera_a(), ramp = keyboard_ramp(true, 11);
    const auto both = layers(camera, kCameraAt, ramp, kKeyboardAt);
    const auto composed = rig.frame(both);
    CHECK(rig.scene->result(composed->pts).camera && rig.scene->result(composed->pts).keyboard);
    expect_match(*composed, *reference(*base, both), both, "camera+premultiplied keyboard");
    // Straight alpha, moved and resized, camera disabled.
    const auto bands = keyboard_bands(false, 12);
    const auto moved = layers(nullptr, {}, bands, kMovedAt);
    const auto moved_frame = rig.frame(moved);
    expect_match(*moved_frame, *reference(*base, moved), moved, "straight keyboard moved");
    // Disabled: exactly the frame without overlays.
    expect_exact(*rig.frame(nothing()), *plain, "disabled overlay");
    // A new camera generation: its very first frames show its own bitmap.
    const auto replacement = camera_c();
    const auto next = layers(replacement, kCameraAt, bands, kMovedAt, 2);
    rig.scene->set(next, 3);
    for (const auto& frame : rig.scene->take()) expect_match(*frame, *reference(*base, next), next, "camera generation 2");
    // Camera requested without a frame yet: skipped explicitly, then drawn.
    auto waiting = next; waiting.camera.bitmap.reset();
    const auto skipped = rig.frame(waiting);
    CHECK(!rig.scene->result(skipped->pts).camera && rig.scene->result(skipped->pts).keyboard);
    expect_match(*skipped, *reference(*base, waiting), waiting, "camera not ready");
    const auto recovered = rig.frame(next);
    CHECK(rig.scene->result(recovered->pts).camera);
    expect_match(*recovered, *reference(*base, next), next, "camera ready again");
    // Premultiplied and straight versions of one ramp compose alike.
    const auto straight = layers(nullptr, {}, keyboard_ramp(false, 13), kKeyboardAt);
    expect_match(*rig.frame(straight), *reference(*base, straight), straight, "straight ramp");
    // One upload per bitmap: the clear layer, camera A, ramp P, bands S,
    // camera C, the straight ramp, then A and P again. Static layers upload
    // nothing more.
    rig.scene->set(both, 0);
    CHECK(eventually([&] { return rig.capture->health().overlay.gpu_uploads >= 8; }, 2s));
    std::this_thread::sleep_for(500ms);
    const auto h = rig.capture->health();
    if (h.overlay.path != "gpu" || h.overlay.cpu_roundtrips || h.frame_allocations || h.overlay.gpu_upload_failures || h.overlay.gpu_uploads != 8 ||
        h.gpu_conversion_fallbacks || h.backpressure_drops)
        throw std::runtime_error("GPU overlay counters: uploads=" + std::to_string(h.overlay.gpu_uploads) +
            " allocations=" + std::to_string(h.frame_allocations) + " drops=" + std::to_string(h.backpressure_drops) + rig.state());
    CHECK(eventually([&] { return rig.capture->health().overlay.gpu_p50_ms > 0; }, 3s));
    CHECK(rig.capture->stop());
    const auto final_health = rig.capture->health();
    if (!final_health.error.empty()) throw std::runtime_error(final_health.error);
    std::cout << "  overlay GPU p50=" << final_health.overlay.gpu_p50_ms << "ms p95=" << final_health.overlay.gpu_p95_ms << "ms\n";
    // The encoded stream carries the layers.
    std::lock_guard lock(rig.mutex);
    CodecContext decoder(avcodec_alloc_context3(avcodec_find_decoder(rig.generation->codec->codec_id)));
    CHECK(decoder && avcodec_parameters_to_context(decoder.get(), rig.generation->codec.get()) == 0 && avcodec_open2(decoder.get(), decoder->codec, nullptr) == 0);
    std::map<int64_t, FramePtr> decoded;
    FramePtr output(av_frame_alloc()); CHECK(output);
    auto receive = [&] { while (avcodec_receive_frame(decoder.get(), output.get()) == 0) {
        auto kept = copy_frame(*output); kept->pts = output->best_effort_timestamp; decoded[kept->pts] = std::move(kept); av_frame_unref(output.get()); } };
    for (const auto& packet : rig.packets) { CHECK(avcodec_send_packet(decoder.get(), packet.get()) == 0); receive(); }
    CHECK(avcodec_send_packet(decoder.get(), nullptr) == 0); receive();
    for (const auto& [frame, expected] : std::vector<std::pair<const AVFrame*, const OverlayFrame*>>{{composed.get(), &both}, {moved_frame.get(), &moved}}) {
        const auto found = decoded.find(frame->pts); CHECK(found != decoded.end());
        const auto ideal = reference(*base, *expected);
        for (const auto& placement : placements(*expected, width, height)) {
            const auto got = region_mean(*found->second, placement), want = region_mean(*ideal, placement);
            for (int c = 0; c < 3; ++c) if (std::abs(got[c] - want[c]) > 3)
                throw std::runtime_error("Decoded overlay differs: plane " + std::to_string(c) + " " + std::to_string(got[c]) + " vs " + std::to_string(want[c]));
        }
    }
}

// Composition that is not GPU-resident. Readback encoders compose on the
// CPU frame they already have; a zero-copy encoder without the compositor
// reports why and takes a CPU round trip only for frames with layers.
void cpu_paths() {
    const auto both = layers(camera_a(), kCameraAt, keyboard_ramp(true, 21), kKeyboardAt);
    {
        RecordingCaptureDependencies dependencies; dependencies.candidates = {{"h264_nvenc", false, false}};
        Rig rig(capture_config(1280, 720, 60), std::move(dependencies), {}, 1280, 720);
        const auto base = rig.frame(nothing());
        expect_exact(*rig.frame(both), *reference(*base, both), "readback CPU composition");
        const auto h = rig.capture->health();
        if (h.overlay.path != "cpu" || h.overlay.cpu_roundtrips || h.hardware_input) throw std::runtime_error("Readback overlays:" + rig.state());
    }
    RecordingCaptureDependencies dependencies; dependencies.candidates = {{"h264_nvenc", false, true}}; dependencies.disable_gpu_overlays = true;
    Rig rig(capture_config(1280, 720, 60), std::move(dependencies), {}, 1280, 720);
    const auto base = rig.frame(nothing());
    std::this_thread::sleep_for(300ms);
    CHECK(rig.capture->health().overlay.cpu_roundtrips == 0); // Nothing to draw, nothing read back.
    expect_exact(*rig.frame(both), *reference(*base, both), "CPU fallback composition");
    const auto h = rig.capture->health();
    if (h.overlay.path != "cpu-fallback" || h.overlay.gpu_failure != "GPU overlay compositor disabled" || !h.overlay.cpu_roundtrips)
        throw std::runtime_error("CPU fallback overlays:" + rig.state());
}

// Overlays through the rest of the lifecycle: encoder recovery, AMF and QSV
// zero-copy plans (on stand-in contexts), pool pressure, a source resize,
// pause and resume.
void lifecycle() {
    const auto both = layers(camera_a(), kCameraAt, keyboard_ramp(true, 31), kKeyboardAt);
    auto check_run = [&](Rig& rig, const char* label) {
        const auto base = rig.frame(clear_layer());
        expect_match(*rig.frame(both), *reference(*base, both), both, label);
        const auto h = rig.capture->health();
        if (h.overlay.path != "gpu" || h.overlay.cpu_roundtrips || !h.overlay.gpu_failure.empty()) throw std::runtime_error(std::string(label) + ":" + rig.state());
    };
    {
        // The first encoder fails mid-stream; its replacement keeps GPU overlays.
        RecordingCaptureDependencies dependencies; dependencies.candidates = {{"h264_nvenc", false, true}, {"h264_nvenc", false, true}};
        EncoderFactory factory; factory.fail_first_at = 30;
        Rig rig(capture_config(1280, 720, 60), std::move(dependencies), factory, 1280, 720);
        CHECK(eventually([&] { return rig.capture->health().generation == 2; }, 5s));
        check_run(rig, "after encoder recovery");
    }
    for (const bool qsv : {false, true}) {
        RecordingCaptureDependencies dependencies; EncoderFactory factory; factory.stand_in = true;
        if (qsv) {
            dependencies.candidates = {{"h264_qsv", true, true}}; dependencies.adapter_vendor = kAdapterVendorIntel;
            dependencies.qsv_frames = [](AVBufferRef* device, int width, int height) {
                AVBufferRef* ref = av_hwframe_ctx_alloc(device); if (!ref) throw std::bad_alloc();
                auto* ctx = reinterpret_cast<AVHWFramesContext*>(ref->data);
                ctx->format = AV_PIX_FMT_D3D11; ctx->sw_format = AV_PIX_FMT_NV12; ctx->width = width; ctx->height = height;
                static_cast<AVD3D11VAFramesContext*>(ctx->hwctx)->BindFlags = D3D11_BIND_RENDER_TARGET;
                if (av_hwframe_ctx_init(ref) < 0) { av_buffer_unref(&ref); throw std::runtime_error("Stand-in QSV frames unavailable"); }
                return ref;
            };
        } else { dependencies.candidates = {{"h264_amf", false, true}}; dependencies.adapter_vendor = kAdapterVendorAmd; }
        Rig rig(capture_config(1280, 720, 60), std::move(dependencies), factory, 1280, 720);
        check_run(rig, qsv ? "QSV zero-copy plan" : "AMF zero-copy plan");
        CHECK(rig.capture->health().processing_path == (qsv ? "d3d11-video-processor-qsv" : "d3d11-video-processor"));
    }
    {
        // The encoder holds every pool surface: counted drops, no fallback,
        // and composition resumes with the pool.
        struct Hold { std::mutex mutex; bool hold = false; std::vector<AVFrame*> held;
            void release() { std::lock_guard lock(mutex); for (auto* frame : held) av_frame_free(&frame); held.clear(); hold = false; }
            ~Hold() { release(); } };
        auto hold = std::make_shared<Hold>();
        RecordingCaptureDependencies dependencies; dependencies.candidates = {{"h264_nvenc", false, true}};
        EncoderFactory factory; factory.wrap = [hold](CodecCalls calls) {
            calls.send = [hold, send = calls.send](AVCodecContext* context, const AVFrame* frame) {
                { std::lock_guard lock(hold->mutex); if (frame && hold->hold) hold->held.push_back(av_frame_clone(frame)); }
                return send(context, frame); };
            return calls; };
        Rig rig(capture_config(1280, 720, 60), std::move(dependencies), factory, 1280, 720);
        rig.scene->set(both, 0);
        { std::lock_guard lock(hold->mutex); hold->hold = true; }
        if (!eventually([&] { return rig.capture->health().pool_pressure_drops >= 3; }, 3s)) throw std::runtime_error("No pool pressure with overlays:" + rig.state());
        hold->release();
        check_run(rig, "after pool pressure");
        CHECK(rig.capture->health().gpu_conversion_fallbacks == 0);
    }
    {
        // A resized source, then pause and resume.
        RecordingCaptureDependencies dependencies; dependencies.candidates = {{"h264_nvenc", false, true}};
        Rig rig(capture_config(1920, 1080, 60), std::move(dependencies), {}, 1920, 1080);
        check_run(rig, "before resize");
        rig.source->resize(3440, 1440);
        std::this_thread::sleep_for(200ms);
        check_run(rig, "after source resize");
        rig.capture->pause(true); std::this_thread::sleep_for(300ms); rig.capture->pause(false);
        check_run(rig, "after pause");
    }
}
#endif

std::filesystem::path self_path() {
    std::wstring path(MAX_PATH, L'\0');
    path.resize(GetModuleFileNameW(nullptr, path.data(), DWORD(path.size())));
    return path;
}
// Stands in for ffmpeg's dshow camera: 640x360 BGRA frames on stdout at
// 30 FPS. "fake-flaky" delivers 20 frames and fails, like an unplugged camera.
int fake_camera(int argc, char** argv) {
    std::string device;
    for (int i = 1; i < argc; ++i) if (std::string_view(argv[i]).starts_with("video=")) device = argv[i] + 6;
    _setmode(_fileno(stdout), _O_BINARY);
    std::vector<uint8_t> frame(640 * 360 * 4);
    for (size_t i = 0; i < frame.size(); i += 4) { frame[i] = 40; frame[i + 1] = 200; frame[i + 2] = device == "fake-flaky" ? 220 : 40; frame[i + 3] = 255; }
    for (int index = 0; index < 30 * 600; ++index) {
        if (device == "fake-flaky" && index == 20) { std::fputs("fake camera disconnected\n", stderr); return 1; }
        if (std::fwrite(frame.data(), 1, frame.size(), stdout) != frame.size() || std::fflush(stdout)) return 0;
        std::this_thread::sleep_for(33ms);
    }
    return 0;
}

#if CLYPDAT_GPU_OVERLAYS
// Recorder-session overlay health through settings, artwork and camera
// changes: explicit states and skip reasons, never a silent absence.
void session_health(bool gpu) {
    const auto root = std::filesystem::current_path() / (L"overlay-session-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64()));
    struct Cleanup { std::filesystem::path path; ~Cleanup() { std::error_code ignored; std::filesystem::remove_all(path, ignored); } } cleanup{root};
    RecorderSessionConfig config;
    config.capture.width = 1280; config.capture.height = 720; config.capture.fps = 30; config.capture.bitrate_mbps = 10;
    config.capture.cpu_encoder = !gpu;
    LARGE_INTEGER counter{}, frequency{}; QueryPerformanceCounter(&counter); QueryPerformanceFrequency(&frequency);
    config.capture.qpc_anchor = counter.QuadPart; config.capture.qpc_frequency = frequency.QuadPart;
    config.capture.monotonic_anchor_us = int64_t(counter.QuadPart * (1000000.L / frequency.QuadPart));
    config.work_directory = root / L"work"; config.ffmpeg = self_path(); config.capture_input = false; config.history_seconds = 10;
    RecorderSession session(config, std::make_unique<PatternSource>(1280, 720, 30, gpu));
    uint64_t revision = 0;
    auto settings = [&](std::wstring camera, std::string keyboard) {
        OverlaySettingsNative value; value.revision = ++revision; value.at_us = monotonic_us(); value.burned = true;
        value.camera_moniker = std::move(camera); value.camera_name = L"Fake"; value.keyboard_layout = std::move(keyboard);
        session.overlay_settings(value);
    };
    auto wait = [&](const char* label, const std::function<bool(const OverlayHealth&)>& predicate, std::chrono::milliseconds timeout = 10s) {
        if (eventually([&] { return predicate(session.health().overlay); }, timeout)) return session.health().overlay;
        const auto o = session.health().overlay;
        throw std::runtime_error(std::string("Overlay session never reached ") + label + ": state=" + o.state + " path=" + o.path +
            " camera=" + std::to_string(o.camera_ready) + "/" + std::to_string(o.camera_generation) + " keyboard=" + std::to_string(o.keyboard_ready) +
            " skip='" + o.last_skip_reason + "' failure='" + o.failure + "' error=" + session.health().error);
    };
    settings(L"fake-steady", "None");
    session.start();
    auto o = wait("camera rendered", [](const auto& o) { return o.state == "rendered" && o.camera_frames > 0; });
    CHECK(o.enabled && o.camera_requested && o.camera_ready && !o.keyboard_requested && o.path == (gpu ? "gpu" : "cpu") && o.settings_revision == revision);
    settings(L"fake-steady", "Full");
    o = wait("keyboard not ready", [](const auto& o) { return o.state == "source-not-ready" && o.last_skip_reason == "Keyboard artwork is not ready."; });
    CHECK(o.keyboard_requested && !o.keyboard_ready && o.camera_ready && o.skipped_frames > 0);
    CHECK(session.artwork(keyboard_ramp(true, 7)));
    o = wait("keyboard rendered", [](const auto& o) { return o.state == "rendered" && o.keyboard_frames > 0; });
    CHECK(o.keyboard_ready && o.keyboard_revision == 7);
    // An unplugged camera: failed with the reason and no frame, then back
    // under a new generation.
    settings(L"fake-flaky", "Full");
    o = wait("camera failed", [](const auto& o) { return o.state == "failed"; });
    CHECK(!o.camera_ready && o.failure.find("fake camera disconnected") != std::string::npos);
    const auto failed_generation = o.camera_generation;
    o = wait("camera reconnected", [&](const auto& o) { return o.state == "rendered" && o.camera_generation > failed_generation; });
    CHECK(o.camera_ready && o.failure.empty());
    settings(L"", "None");
    o = wait("disabled", [](const auto& o) { return o.state == "disabled"; });
    CHECK(!o.enabled);
    CHECK(session.stop());
    if (!session.health().error.empty()) throw std::runtime_error(session.health().error);
    std::cout << "Overlay session health (" << (gpu ? "GPU" : "CPU") << ") passed\n";
}

#endif
#if CLYPDAT_GPU_OVERLAYS
constexpr bool kNewOverlayApi = true;
#else
constexpr bool kNewOverlayApi = false;
#endif
// Manual benchmark: --overlay-bench <width> <height> <fps> <off|gpu|cpu>.
// A 4K generated GPU source through production encoders with a 30 FPS camera
// and static keyboard artwork, measured over 8 s after a 3 s warm-up. "cpu"
// disables the GPU compositor. Builds against earlier trees, where overlays
// always took the CPU round trip.
int overlay_bench(int width, int height, int fps, const std::string& mode) {
    RecordingCaptureConfig config = {}; config.width = width; config.height = height; config.fps = fps; config.bitrate_mbps = 25;
    LARGE_INTEGER counter{}, frequency{}; QueryPerformanceCounter(&counter); QueryPerformanceFrequency(&frequency);
    config.qpc_anchor = counter.QuadPart; config.qpc_frequency = frequency.QuadPart;
    config.monotonic_anchor_us = int64_t(counter.QuadPart * (1000000.L / frequency.QuadPart));
    const bool enabled = mode != "off";
    auto input = std::make_shared<InputHistory>(); auto history = std::make_shared<OverlayHistory>(input);
    history->reset(true);
    OverlaySettingsNative settings; settings.revision = 1; settings.burned = true; settings.camera_moniker = L"bench"; settings.keyboard_layout = "Full";
    history->apply(settings);
    history->set_artwork(keyboard_ramp(true, 1));
    const auto generation = history->replace_camera();
    std::atomic<bool> stop{false};
    std::thread camera([&] {
        for (uint64_t index = 0; !stop; ++index) {
            const auto frame = bitmap(640, 360, false, index, [&](int x, int y) { return Texel{uint8_t(x + index), uint8_t(y), 128, 255}; });
            history->camera_frame(generation, frame);
            std::this_thread::sleep_for(33ms);
        }
    });
    RecordingCaptureCallbacks callbacks;
    callbacks.overlay_enabled = [enabled] { return enabled; };
    std::atomic<uint64_t> packets{0};
    callbacks.packet = [&](auto, Packet, int64_t, bool) { ++packets; };
    RecordingCaptureDependencies dependencies;
#if CLYPDAT_GPU_OVERLAYS
    callbacks.overlay_frame = [history](int64_t pts) { return history->frame(pts); };
    dependencies.disable_gpu_overlays = mode == "cpu";
#else
    callbacks.compose_nv12 = [history](AVFrame& frame) { history->compose(frame); };
#endif
    RecordingCapture capture(config, callbacks, std::make_unique<PatternSource>(3840, 2160, fps, true), std::move(dependencies));
    capture.start();
    std::this_thread::sleep_for(3s);
    struct Sample { uint64_t cpu; size_t private_ws; };
    auto sample = [] {
        FILETIME created{}, exited{}, kernel{}, user{}; GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user);
        auto ticks = [](FILETIME t) { return (uint64_t(t.dwHighDateTime) << 32) | t.dwLowDateTime; };
        PROCESS_MEMORY_COUNTERS_EX2 memory{}; GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory));
        return Sample{ticks(kernel) + ticks(user), memory.PrivateWorkingSetSize};
    };
    const auto warm = capture.health(); const auto before = sample(); const auto started = std::chrono::steady_clock::now();
    double fresh = 0, output = 0, upload = 0, overlay = 0; int windows = 0;
    for (int second = 0; second < 8; ++second) {
        std::this_thread::sleep_for(1s); const auto h = capture.health();
        fresh += h.unique_fps; output += h.output_fps; upload += h.hardware_upload_ms; overlay += h.overlay_compose_ms; ++windows;
    }
    const auto after = sample(); const double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
    const auto h = capture.health();
    ComPtr<IDXGIFactory1> factory; ComPtr<IDXGIAdapter1> adapter; ComPtr<IDXGIAdapter3> adapter3;
    DXGI_QUERY_VIDEO_MEMORY_INFO local{}, shared{};
    if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) && SUCCEEDED(factory->EnumAdapters1(0, &adapter)) && SUCCEEDED(adapter.As(&adapter3))) {
        adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local); adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &shared); }
    CHECK(capture.stop()); stop = true; camera.join();
    if (!capture.health().error.empty()) throw std::runtime_error("Overlay bench: " + capture.health().error);
    const double mb = 1024.0 * 1024.0;
    std::cout << std::fixed << std::setprecision(2) << (kNewOverlayApi ? "current" : "baseline") << " overlays=" << mode << " " << width << "x" << height << "@" << fps
        << ": encoder=" << h.encoder << " path=" << h.processing_path << " fresh=" << fresh / windows << " output=" << output / windows
        << " drops=" << (h.backpressure_drops - warm.backpressure_drops) + (h.replaced - warm.replaced)
        << " frameAllocations/s=" << double(h.frame_allocations - warm.frame_allocations) / elapsed
        << " uploadMs=" << upload / windows << " overlayCpuMs=" << overlay / windows << " videoProcessorMs=" << h.video_processor_ms
        << " cpu=" << double(after.cpu - before.cpu) / 1e7 / elapsed * 100 << "% privateWS=" << double(after.private_ws) / mb
        << "MB dedicated=" << double(local.CurrentUsage) / mb << "MB shared=" << double(shared.CurrentUsage) / mb << "MB\n";
#if CLYPDAT_GPU_OVERLAYS
    std::cout << "  overlay path=" << h.overlay.path << " roundTrips/s=" << double(h.overlay.cpu_roundtrips - warm.overlay.cpu_roundtrips) / elapsed
        << " uploads/s=" << double(h.overlay.gpu_uploads - warm.overlay.gpu_uploads) / elapsed << " gpu p50=" << h.overlay.gpu_p50_ms
        << " p95=" << h.overlay.gpu_p95_ms << "ms gpuFailure='" << h.overlay.gpu_failure << "'\n";
#endif
    return 0;
}
}

int main(int argc, char** argv) {
    try {
        for (int i = 1; i < argc; ++i) if (std::string_view(argv[i]) == "dshow") return fake_camera(argc, argv);
        av_log_set_level(AV_LOG_ERROR);
        if (argc > 4 && std::string_view(argv[1]) == "--overlay-bench")
            return overlay_bench(std::atoi(argv[2]), std::atoi(argv[3]), std::atoi(argv[4]), argc > 5 ? argv[5] : "gpu");
#if CLYPDAT_GPU_OVERLAYS
        const bool gpu = argc > 1 && std::string_view(argv[1]) == "--gpu";
        session_health(false);
        if (gpu) {
            composition_matches_cpu(1920, 1080, 60, 1920, 1080);
            composition_matches_cpu(2560, 1440, 90, 3840, 2160);
            cpu_paths(); lifecycle(); session_health(true);
        }
        std::cout << "Native burned overlay composition, health and lifecycle passed\n";
#endif
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
