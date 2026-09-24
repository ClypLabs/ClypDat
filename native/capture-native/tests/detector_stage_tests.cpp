// DetectorStage against the full-frame detector it replaces, byte for byte,
// on WARP by default and on the hardware device with --gpu. Also covers the
// capture's event-driven detection thread.
//
// Manual: --approach-a <frames dir> compares a D3D11 video processor
// full-canvas conversion plus region readback with the reference.
#include "detector_stage.h"
#include "recording_capture.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <wincodec.h>
#include <wrl/client.h>
extern "C" {
#include <libavutil/log.h>
#include <libswscale/swscale.h>
}
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <filesystem>
#include <iostream>
#include <mutex>
#include <set>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace clypdat;
using namespace std::chrono_literals;
using Microsoft::WRL::ComPtr;

#define CHECK(x) do { if (!(x)) throw std::runtime_error(std::string("Check failed: ") + #x + " (line " + std::to_string(__LINE__) + ")"); } while (0)

namespace {
struct Device { ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context; };
Device make_device(bool hardware) {
    Device result;
    if (FAILED(D3D11CreateDevice(nullptr, hardware ? D3D_DRIVER_TYPE_HARDWARE : D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, &result.device, nullptr, &result.context))) throw std::runtime_error("No D3D11 device");
    ComPtr<ID3D11Multithread> protection; CHECK(SUCCEEDED(result.context.As(&protection))); protection->SetMultithreadProtected(TRUE);
    return result;
}
struct Image { int width = 0, height = 0; std::vector<uint8_t> bgra; };
// Gradients, noise and HUD-coloured blocks (the counter mask's yellow, pink
// and gold) spread over the frame, so every layout crops varied content.
Image pattern(int width, int height, uint32_t seed) {
    Image image{width, height, std::vector<uint8_t>(size_t(width) * height * 4)};
    uint32_t noise = seed * 2654435761u + 1;
    for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
        noise = noise * 1664525u + 1013904223u;
        auto* p = &image.bgra[(size_t(y) * width + x) * 4];
        p[0] = uint8_t(x * 3 + y * 5 + seed + (noise >> 28)); p[1] = uint8_t((x ^ y) * 7 + seed * 3 + (noise >> 29));
        p[2] = uint8_t(x * y / 7 + seed + (noise >> 27)); p[3] = 255;
        const int cell = ((x / 23) * 7 + (y / 17) * 13 + int(seed)) % 11;
        if (cell == 0) { p[2] = 230; p[1] = 210; p[0] = 20; }
        else if (cell == 1) { p[2] = 220; p[1] = 80; p[0] = 150; }
        else if (cell == 2) { p[2] = 200; p[1] = 170; p[0] = 40; }
    }
    return image;
}
CapturePixels texture_pixels(const Device& device, const Image& image, int64_t timestamp = 1000000) {
    D3D11_TEXTURE2D_DESC desc{}; desc.Width = UINT(image.width); desc.Height = UINT(image.height); desc.MipLevels = 1; desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    D3D11_SUBRESOURCE_DATA data{image.bgra.data(), UINT(image.width * 4), 0};
    ID3D11Texture2D* texture = nullptr; CHECK(SUCCEEDED(device.device->CreateTexture2D(&desc, &data, &texture)));
    CapturePixels pixels; pixels.width = image.width; pixels.height = image.height; pixels.stride = image.width * 4; pixels.timestamp_us = timestamp;
    pixels.texture = std::shared_ptr<ID3D11Texture2D>(texture, [](auto* p) { p->Release(); });
    return pixels;
}
CapturePixels cpu_pixels(const Image& image, int64_t timestamp = 1000000) {
    CapturePixels pixels; pixels.width = image.width; pixels.height = image.height; pixels.stride = image.width * 4; pixels.timestamp_us = timestamp;
    pixels.bgra = image.bgra; return pixels;
}

struct Layout { const char* name; std::array<CaptureNormalizedRect, 3> regions; bool mask; };
const std::vector<Layout>& layouts() {
    static const std::vector<Layout> value{
        {"helldivers2", {CaptureNormalizedRect{.34, .445, .32, .065}, {.42, .335, .16, .055}, {1152.0 / 2560, 1036.0 / 1440, 308.0 / 2560, 174.0 / 1440}}, true},
        {"overwatch", {CaptureNormalizedRect{.015, .02, .27, .80}, {.43, .685, .28, .115}, {.42, .20, .18, .055}}, false},
        {"fortnite", {CaptureNormalizedRect{.01, .49, .33, .12}, {.42, .66, .19, .16}, {.30, .005, .39, .30}}, true},
        // Canvas corners and edges, single rows and columns.
        {"edges", {CaptureNormalizedRect{0, 0, .1, .1}, {.9, .9, .1, .1}, {0, .5, 1, .0005}}, true},
        // Odd pixel origins and sizes; a one-column strip; the last row.
        {"odd", {CaptureNormalizedRect{.3337, .1113, .2011, .0371}, {.7771, .6667, .0007, .3}, {.001, .999, .998, .001}}, true},
        // The whole canvas and overlapping regions.
        {"full", {CaptureNormalizedRect{0, 0, 1, 1}, {.25, .25, .5, .5}, {.2, .3, .35, .4}}, true},
    };
    return value;
}
DetectorRequest request_for(const Layout& layout, int width, int height) { return {width, height, layout.regions, layout.mask}; }

struct Difference { size_t bytes = 0, exact = 0, mask_pixels = 0, mask_different = 0; int max = 0; double sum = 0; bool dimensions = true; };
Difference compare(const RecordingDetectorSnapshot& expected, const RecordingDetectorSnapshot& actual) {
    Difference result;
    for (size_t i = 0; i < 3; ++i) {
        const auto& a = expected.regions[i]; const auto& b = actual.regions[i];
        if (a.width != b.width || a.height != b.height || a.pixels.size() != b.pixels.size()) { result.dimensions = false; continue; }
        for (size_t j = 0; j < a.pixels.size(); ++j) {
            const int delta = std::abs(int(a.pixels[j]) - int(b.pixels[j]));
            ++result.bytes; result.exact += delta == 0; result.max = std::max(result.max, delta); result.sum += delta;
        }
    }
    const auto& a = expected.third_mask; const auto& b = actual.third_mask;
    if (a.width != b.width || a.height != b.height || a.pixels.size() != b.pixels.size()) result.dimensions = false;
    else for (size_t j = 0; j < a.pixels.size(); ++j) { ++result.mask_pixels; result.mask_different += a.pixels[j] != b.pixels[j]; }
    return result;
}
std::string describe(const Difference& d) {
    std::ostringstream text;
    text << "dimensions=" << (d.dimensions ? "same" : "DIFFERENT") << " exact=" << d.exact << "/" << d.bytes << " max=" << d.max
         << " mean=" << (d.bytes ? d.sum / d.bytes : 0) << " maskDifferent=" << d.mask_different << "/" << d.mask_pixels;
    return text.str();
}
RecordingDetectorSnapshot reference(const CapturePixels& pixels, const DetectorRequest& request) {
    SwsContext* scaler = nullptr; auto owned = pixels; RecordingDetectorSnapshot snapshot;
    const bool sampled = detector_reference_sample(owned, request, scaler, [] { return false; }, snapshot);
    sws_freeContext(scaler); CHECK(sampled); return snapshot;
}
void expect_exact(DetectorStage& stage, const CapturePixels& pixels, const DetectorRequest& request, const std::string& label) {
    const auto expected = reference(pixels, request);
    RecordingDetectorSnapshot actual;
    CHECK(stage.sample(pixels, request, [] { return false; }, actual));
    const auto difference = compare(expected, actual);
    if (!difference.dimensions || difference.exact != difference.bytes || difference.mask_different)
        throw std::runtime_error("Detector stage differs from the reference for " + label + ": " + describe(difference));
    CHECK(actual.timestamp_us == pixels.timestamp_us);
    CHECK((actual.third_mask.pixels.size() != 0) == request.counter_mask);
}

// Every layout, source shape and canvas through one stage instance, which
// rebuilds as the source or layout changes: 16:9 at three sizes, letterbox,
// pillarbox, portrait, and odd dimensions.
void exact_against_reference(const Device& device) {
    DetectorStage stage; uint32_t seed = 1; uint64_t cases = 0;
    const std::vector<std::pair<int, int>> sources{{1920, 1080}, {2560, 1440}, {3840, 2160}, {2560, 1080}, {1440, 1080}, {1080, 1920}, {1366, 768}, {1921, 1081}};
    const std::vector<std::pair<int, int>> outputs{{1920, 1080}, {2560, 1440}, {3840, 2160}};
    for (const auto& [sw, sh] : sources) {
        const auto image = pattern(sw, sh, seed++);
        const auto gpu = texture_pixels(device, image, 1000000 + seed);
        const auto cpu = cpu_pixels(image, 2000000 + seed);
        for (const auto& [ow, oh] : outputs) for (const auto& layout : layouts()) {
            const auto request = request_for(layout, ow, oh);
            const auto label = std::string(layout.name) + " " + std::to_string(sw) + "x" + std::to_string(sh) + "->" + std::to_string(ow) + "x" + std::to_string(oh);
            expect_exact(stage, gpu, request, label + " gpu"); ++cases;
            // CPU sources skip the readback; one canvas covers that path.
            if (ow == 2560) { expect_exact(stage, cpu, request, label + " cpu"); ++cases; }
        }
    }
    const auto& counters = stage.counters();
    CHECK(counters.fallbacks == 0 && counters.cancelled == 0 && counters.samples == cases);
    std::cout << "exact: " << cases << " cases identical to the reference; builds=" << counters.staging_rebuilds
              << " textures=" << counters.gpu_textures_allocated << " buffers=" << counters.cpu_buffers_allocated << "\n";
}

size_t private_bytes() { PROCESS_MEMORY_COUNTERS_EX memory{}; GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory)); return memory.PrivateUsage; }
// Steady sampling allocates nothing and reads back region pixels only; the
// reference path reads the whole frame every sample.
void reuse_and_region_readback(const Device& device) {
    const auto image = pattern(3840, 2160, 7);
    const auto request = request_for(layouts()[0], 2560, 1440);
    DetectorStage stage; RecordingDetectorSnapshot snapshot;
    const uint64_t frame_bytes = uint64_t(image.width) * 4 * image.height;
    for (int i = 0; i < 5; ++i) CHECK(stage.sample(texture_pixels(device, image, 1000000 * (i + 1)), request, [] { return false; }, snapshot));
    const auto before = stage.counters(); const auto committed = private_bytes();
    const auto frame = texture_pixels(device, image);
    for (int i = 0; i < 40; ++i) {
        auto pixels = frame; pixels.timestamp_us = 1000000 * (i + 10);
        CHECK(stage.sample(pixels, request, [] { return false; }, snapshot));
        CHECK(stage.timing().bytes > 0 && stage.timing().bytes * 10 < frame_bytes);
    }
    const auto after = stage.counters(); const auto growth = int64_t(private_bytes()) - int64_t(committed);
    CHECK(after.staging_rebuilds == 1 && before.staging_rebuilds == 1);
    CHECK(after.gpu_textures_allocated == 1 && after.cpu_buffers_allocated == 3 && after.allocations_after_warmup == 0);
    CHECK(after.samples == before.samples + 40 && after.fallbacks == 0);
    if (growth > 2 * 1024 * 1024) throw std::runtime_error("Detector stage grew " + std::to_string(growth) + " bytes over 40 samples");
    std::cout << "reuse: 40 samples, bytes/sample=" << stage.timing().bytes << " of " << frame_bytes << " (" << 100.0 * stage.timing().bytes / frame_bytes
              << "%), private growth=" << growth << "\n";
    DetectorStage old; old.reference = true;
    CHECK(old.sample(frame, request, [] { return false; }, snapshot));
    CHECK(old.timing().bytes == frame_bytes && old.counters().gpu_textures_allocated == 1);
    CHECK(old.sample(frame, request, [] { return false; }, snapshot) && old.counters().gpu_textures_allocated == 2);
    // A size change rebuilds; returning to it rebuilds again, never grows.
    const auto lower = texture_pixels(device, pattern(1920, 1080, 8));
    CHECK(stage.sample(lower, request, [] { return false; }, snapshot));
    CHECK(stage.sample(frame, request, [] { return false; }, snapshot));
    CHECK(stage.counters().staging_rebuilds == 3 && stage.counters().gpu_textures_allocated == 3 && stage.counters().allocations_after_warmup == 8);
    stage.release();
    CHECK(stage.sample(frame, request, [] { return false; }, snapshot) && stage.counters().staging_rebuilds == 4);
}
// Stop while the GPU copy is still running: the sample gives up, reports
// cancellation, and the stage samples normally afterwards.
void cancel_pending_readback(const Device& device) {
    const auto image = pattern(2560, 1440, 11);
    const auto pixels = texture_pixels(device, image);
    const auto request = request_for(layouts()[0], 2560, 1440);
    DetectorStage stage; std::atomic<bool> pending{true}, cancelled{false}, entered{false};
    stage.readback_pending = [&] { entered = true; return pending.load(); };
    RecordingDetectorSnapshot snapshot; std::atomic<int> result{-1};
    std::thread worker([&] { result = stage.sample(pixels, request, [&] { return cancelled.load(); }, snapshot) ? 1 : 0; });
    const auto deadline = std::chrono::steady_clock::now() + 2s;
    while (!entered && std::chrono::steady_clock::now() < deadline) std::this_thread::sleep_for(1ms);
    std::this_thread::sleep_for(30ms); CHECK(entered && result == -1);
    const auto cancelled_at = std::chrono::steady_clock::now(); cancelled = true; worker.join();
    CHECK(result == 0 && std::chrono::steady_clock::now() - cancelled_at < 100ms);
    CHECK(stage.counters().cancelled == 1 && stage.counters().samples == 0);
    pending = false; cancelled = false;
    expect_exact(stage, pixels, request, "after cancellation");
    CHECK(stage.counters().staging_rebuilds == 1);
}

// Capture-level: frames with source timestamps 1/fps apart, CPU or texture.
class DetectorSource final : public RecordingFrameSource {
    std::shared_ptr<CapturePixels> frame_; int fps_; int64_t index_ = 0;
    std::chrono::steady_clock::time_point next_ = std::chrono::steady_clock::now();
public:
    DetectorSource(CapturePixels frame, int fps) : frame_(std::make_shared<CapturePixels>(std::move(frame))), fps_(fps) {}
    std::mutex mutex; std::set<int64_t> delivered;
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
        const auto now = std::chrono::steady_clock::now();
        if (now < next_) { std::this_thread::sleep_for(std::min(timeout, std::chrono::duration_cast<std::chrono::milliseconds>(next_ - now) + 1ms)); return false; }
        next_ += std::chrono::microseconds(1000000 / fps_);
        pixels = *frame_; pixels.timestamp_us = 1000000 + ++index_ * (1000000 / fps_);
        { std::lock_guard lock(mutex); delivered.insert(pixels.timestamp_us); }
        return true;
    }
    bool eligible() const override { return true; }
    const char* name() const override { return "detector fixture"; }
    ID3D11Device* d3d_device() const override { ComPtr<ID3D11Device> device; if (frame_->texture) frame_->texture->GetDevice(&device); return device.Get(); }
};
// Cycles the capture's detection thread has run, found by its name.
uint64_t detection_cycles() {
    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0); CHECK(snapshot != INVALID_HANDLE_VALUE);
    THREADENTRY32 entry{sizeof(entry)}; uint64_t cycles = UINT64_MAX;
    for (BOOL more = Thread32First(snapshot, &entry); more; more = Thread32Next(snapshot, &entry)) {
        if (entry.th32OwnerProcessID != GetCurrentProcessId()) continue;
        const HANDLE thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ThreadID); if (!thread) continue;
        PWSTR name = nullptr;
        if (SUCCEEDED(GetThreadDescription(thread, &name)) && name) {
            if (std::wstring(name) == L"ClypDat capture detection") { ULONG64 value = 0; QueryThreadCycleTime(thread, &value); cycles = value; }
            LocalFree(name);
        }
        CloseHandle(thread);
    }
    CloseHandle(snapshot); CHECK(cycles != UINT64_MAX); return cycles;
}
uint64_t idle_cycles(std::chrono::milliseconds window) {
    const auto before = detection_cycles(); std::this_thread::sleep_for(window); return detection_cycles() - before;
}
// No detector: the detection thread sleeps through 120 FPS of acquisitions.
// Enabled at runtime it samples every 500 ms of source time, stamped with a
// delivered frame's timestamp; disabled it sleeps again and releases the
// stage, which rebuilds when re-enabled.
void runtime_configuration() {
    RecordingCaptureConfig config; config.width = 1920; config.height = 1080; config.fps = 30; config.cpu_encoder = true;
    std::mutex mutex; std::condition_variable changed; std::vector<int64_t> stamps;
    RecordingCaptureCallbacks callbacks;
    callbacks.detector_snapshot = [&](RecordingDetectorSnapshot snapshot) {
        CHECK(snapshot.regions[0].width == 614 && snapshot.third_mask.width == 231);
        std::lock_guard lock(mutex); stamps.push_back(snapshot.timestamp_us); changed.notify_all();
    };
    auto owned = std::make_unique<DetectorSource>(cpu_pixels(pattern(1920, 1080, 3)), 120); auto* source = owned.get();
    RecordingCapture capture(config, callbacks, std::move(owned)); capture.start();
    std::this_thread::sleep_for(300ms);
    const auto idle = idle_cycles(1500ms);
    CHECK(capture.health().detector_samples == 0 && capture.health().acquired > 100);
    const auto& hd2 = layouts()[0];
    auto wait_samples = [&](size_t count, std::chrono::milliseconds timeout) {
        std::unique_lock lock(mutex); return changed.wait_for(lock, timeout, [&] { return stamps.size() >= count; });
    };
    capture.set_detector_regions(hd2.regions, true, true);
    CHECK(wait_samples(3, 3s));
    capture.set_detector_regions(hd2.regions, false, false);
    std::this_thread::sleep_for(200ms);
    size_t disabled_at; { std::lock_guard lock(mutex); disabled_at = stamps.size(); }
    const auto disabled_idle = idle_cycles(1500ms);
    { std::lock_guard lock(mutex); CHECK(stamps.size() == disabled_at); }
    const auto builds = capture.health().detector_builds; CHECK(builds == 1);
    capture.set_detector_regions(hd2.regions, true, true);
    CHECK(wait_samples(disabled_at + 2, 3s));
    CHECK(capture.stop());
    const auto health = capture.health();
    CHECK(health.detector_builds == 2 && health.detector_fallbacks == 0 && health.detector_skipped == 0 && health.error.empty());
    std::lock_guard lock(mutex); std::lock_guard delivered(source->mutex);
    for (size_t i = 0; i < stamps.size(); ++i) {
        CHECK(source->delivered.count(stamps[i]));
        if (i) CHECK(stamps[i] - stamps[i - 1] >= 500000);
    }
    // A thread woken by acquisitions or a polling timeout runs millions of
    // cycles in 1.5 s; one asleep runs none.
    if (idle > 200000 || disabled_idle > 200000)
        throw std::runtime_error("Detection thread woke while idle: " + std::to_string(idle) + " / " + std::to_string(disabled_idle) + " cycles");
    std::cout << "runtime: " << stamps.size() << " samples; idle detection cycles " << idle << " (never enabled), " << disabled_idle << " (disabled)\n";
}
// stop() while the detector's GPU copy is pending returns promptly and
// counts the skipped sample.
void stop_during_readback(const Device& device) {
    RecordingCaptureConfig config; config.width = 1920; config.height = 1080; config.fps = 30; config.cpu_encoder = true;
    config.detector_enabled = true; config.detector_normalized = layouts()[0].regions; config.detector_counter_mask = true;
    std::atomic<bool> entered{false};
    RecordingCaptureDependencies dependencies; dependencies.detector_readback_pending = [&] { entered = true; return true; };
    RecordingCaptureCallbacks callbacks; callbacks.detector_snapshot = [](RecordingDetectorSnapshot) {};
    RecordingCapture capture(config, callbacks, std::make_unique<DetectorSource>(texture_pixels(device, pattern(1920, 1080, 5)), 30), std::move(dependencies));
    capture.start();
    const auto deadline = std::chrono::steady_clock::now() + 5s;
    while (!entered && std::chrono::steady_clock::now() < deadline) std::this_thread::sleep_for(5ms);
    CHECK(entered);
    const auto stopping = std::chrono::steady_clock::now();
    CHECK(capture.stop());
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - stopping);
    const auto health = capture.health();
    CHECK(health.detector_skipped == 1 && health.detector_samples == 0 && !health.running);
    if (elapsed > 1500ms) throw std::runtime_error("stop() with a pending detector readback took " + std::to_string(elapsed.count()) + " ms");
    std::cout << "stop during readback: " << elapsed.count() << " ms\n";
}

// Approach A: the video processor converts the whole canvas on the GPU, then
// only regions are read back. Measured against the swscale reference.
struct WicImage { std::string name; Image image; };
std::vector<WicImage> load_frames(const std::filesystem::path& root) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ComPtr<IWICImagingFactory> wic; CHECK(SUCCEEDED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic))));
    std::vector<WicImage> frames;
    for (const auto& entry : std::filesystem::recursive_directory_iterator(root, std::filesystem::directory_options::follow_directory_symlink)) {
        const auto extension = entry.path().extension().string(); if (extension != ".png" && extension != ".jpg") continue;
        ComPtr<IWICBitmapDecoder> decoder; ComPtr<IWICBitmapFrameDecode> frame; ComPtr<IWICFormatConverter> converter;
        if (FAILED(wic->CreateDecoderFromFilename(entry.path().c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder)) ||
            FAILED(decoder->GetFrame(0, &frame)) || FAILED(wic->CreateFormatConverter(&converter)) ||
            FAILED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom))) continue;
        UINT w = 0, h = 0; converter->GetSize(&w, &h); if (w < 1280 || h < 720) continue;
        WicImage item{entry.path().parent_path().filename().string() + "/" + entry.path().filename().string(), {int(w), int(h), std::vector<uint8_t>(size_t(w) * h * 4)}};
        CHECK(SUCCEEDED(converter->CopyPixels(nullptr, w * 4, UINT(item.image.bgra.size()), item.image.bgra.data())));
        for (size_t i = 3; i < item.image.bgra.size(); i += 4) item.image.bgra[i] = 255;
        frames.push_back(std::move(item));
    }
    return frames;
}
int approach_a(const std::filesystem::path& root) {
    const auto device = make_device(true);
    ComPtr<ID3D11VideoDevice> video; ComPtr<ID3D11VideoContext> context;
    CHECK(SUCCEEDED(device.device.As(&video)) && SUCCEEDED(device.context.As(&context)));
    const auto frames = load_frames(root); CHECK(!frames.empty());
    std::cout << frames.size() << " frames\n";
    for (const auto& [ow, oh] : std::vector<std::pair<int, int>>{{1920, 1080}, {2560, 1440}, {3840, 2160}}) {
        D3D11_TEXTURE2D_DESC desc{}; desc.Width = UINT(ow); desc.Height = UINT(oh); desc.MipLevels = 1; desc.ArraySize = 1; desc.Format = DXGI_FORMAT_NV12;
        desc.SampleDesc.Count = 1; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> canvas, staging; CHECK(SUCCEEDED(device.device->CreateTexture2D(&desc, nullptr, &canvas)));
        desc.BindFlags = 0; desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        CHECK(SUCCEEDED(device.device->CreateTexture2D(&desc, nullptr, &staging)));
        for (const auto& layout : layouts()) {
            if (std::string(layout.name) != "helldivers2" && std::string(layout.name) != "overwatch" && std::string(layout.name) != "fortnite") continue;
            Difference total; size_t exact_samples = 0, samples = 0, mask_samples_different = 0; double blt_ms = 0;
            for (const auto& frame : frames) {
                const auto pixels = texture_pixels(device, frame.image);
                const auto request = request_for(layout, ow, oh);
                D3D11_VIDEO_PROCESSOR_CONTENT_DESC content{}; content.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
                content.InputWidth = UINT(frame.image.width); content.InputHeight = UINT(frame.image.height); content.OutputWidth = UINT(ow); content.OutputHeight = UINT(oh);
                content.InputFrameRate = content.OutputFrameRate = {60, 1}; content.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;
                ComPtr<ID3D11VideoProcessorEnumerator> enumerator; ComPtr<ID3D11VideoProcessor> processor;
                CHECK(SUCCEEDED(video->CreateVideoProcessorEnumerator(&content, &enumerator)) && SUCCEEDED(video->CreateVideoProcessor(enumerator.Get(), 0, &processor)));
                ComPtr<ID3D11VideoProcessorInputView> input; ComPtr<ID3D11VideoProcessorOutputView> output;
                D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC id{}; id.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
                D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC od{}; od.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
                CHECK(SUCCEEDED(video->CreateVideoProcessorInputView(pixels.texture.get(), enumerator.Get(), &id, &input)));
                CHECK(SUCCEEDED(video->CreateVideoProcessorOutputView(canvas.Get(), enumerator.Get(), &od, &output)));
                const auto fit = capture_aspect_fit(frame.image.width, frame.image.height, ow, oh);
                RECT source{0, 0, frame.image.width, frame.image.height}, destination{fit.x, fit.y, fit.x + fit.width, fit.y + fit.height}, target{0, 0, ow, oh};
                context->VideoProcessorSetStreamSourceRect(processor.Get(), 0, TRUE, &source);
                context->VideoProcessorSetStreamDestRect(processor.Get(), 0, TRUE, &destination);
                context->VideoProcessorSetOutputTargetRect(processor.Get(), TRUE, &target);
                context->VideoProcessorSetStreamAutoProcessingMode(processor.Get(), 0, FALSE);
                context->VideoProcessorSetStreamFrameFormat(processor.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
                D3D11_VIDEO_PROCESSOR_COLOR_SPACE in{}, out{}; in.YCbCr_Matrix = out.YCbCr_Matrix = 1;
                in.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255; out.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
                context->VideoProcessorSetStreamColorSpace(processor.Get(), 0, &in); context->VideoProcessorSetOutputColorSpace(processor.Get(), &out);
                D3D11_VIDEO_COLOR background{}; background.YCbCr = {16.f / 255.f, 128.f / 255.f, 128.f / 255.f, 1};
                context->VideoProcessorSetOutputBackgroundColor(processor.Get(), TRUE, &background);
                D3D11_VIDEO_PROCESSOR_STREAM stream{}; stream.Enable = TRUE; stream.pInputSurface = input.Get();
                const auto started = std::chrono::steady_clock::now();
                CHECK(SUCCEEDED(context->VideoProcessorBlt(processor.Get(), output.Get(), 0, 1, &stream)));
                device.context->CopyResource(staging.Get(), canvas.Get());
                D3D11_MAPPED_SUBRESOURCE mapped{}; CHECK(SUCCEEDED(device.context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)));
                blt_ms += std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count();
                RecordingDetectorSnapshot actual; actual.timestamp_us = pixels.timestamp_us;
                const auto* luma = static_cast<const uint8_t*>(mapped.pData);
                detector_crop(luma, mapped.RowPitch, luma + size_t(mapped.RowPitch) * oh, mapped.RowPitch, request, actual);
                device.context->Unmap(staging.Get(), 0);
                const auto difference = compare(reference(pixels, request), actual);
                ++samples; exact_samples += difference.exact == difference.bytes && !difference.mask_different; mask_samples_different += difference.mask_different != 0;
                total.bytes += difference.bytes; total.exact += difference.exact; total.max = std::max(total.max, difference.max); total.sum += difference.sum;
                total.mask_pixels += difference.mask_pixels; total.mask_different += difference.mask_different;
            }
            std::cout << "A " << layout.name << " " << ow << "x" << oh << ": exact samples " << exact_samples << "/" << samples << "; luma " << describe(total)
                      << "; samples with mask differences " << mask_samples_different << "; blt+full readback " << blt_ms / samples << " ms/sample\n";
        }
    }
    return 0;
}
}

int main(int argc, char** argv) {
    try {
        av_log_set_level(AV_LOG_ERROR);
        if (argc > 2 && std::string(argv[1]) == "--approach-a") return approach_a(argv[2]);
        const bool gpu = argc > 1 && std::string(argv[1]) == "--gpu";
        const auto device = make_device(gpu);
        exact_against_reference(device);
        reuse_and_region_readback(device);
        cancel_pending_readback(device);
        runtime_configuration();
        stop_during_readback(device);
        std::cout << "Detector stage tests passed" << (gpu ? " (hardware device)" : " (WARP)") << "\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
