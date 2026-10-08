// Generated window pixels only. No screen/window acquisition or input.
#include "window_capture_bounds.h"
#include "captured_frames.h"
#include "gpu_test_device.h"
#include "readback_stage.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <wrl/client.h>
#include <iostream>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <stdexcept>
#include <vector>

using namespace clypdat;
using Microsoft::WRL::ComPtr;
#define CHECK(x) do { if (!(x)) throw std::runtime_error(std::string("Check failed: ") + #x + " (line " + std::to_string(__LINE__) + ")"); } while (0)

void geometry_cases() {
    int cases = 0;
    for (const int dpi : {96, 120, 144, 192}) for (const int x : {-1920, 100}) {
        const int border = dpi / 96, caption = 28 * dpi / 96;
        WindowCaptureBounds bounds{{x - 8, -308, 1936, 1096 + caption}, {x, -300, 1920, 1080 + caption},
            {x + border, -300 + caption, 1920 - 2 * border, 1080 - border}};
        CHECK(capture_window_region(bounds, {0, 0, bounds.frame.width, bounds.frame.height}) ==
            (CaptureRect{border, caption, bounds.client.width, bounds.client.height}));
        CHECK(capture_window_region(bounds, {0, 0, bounds.window.width, bounds.window.height}) ==
            (CaptureRect{8 + border, 8 + caption, bounds.client.width, bounds.client.height}));
        // Match both dimensions; a stale frame during resize must be dropped.
        CHECK(!capture_window_region(bounds, {0, 0, bounds.frame.width, bounds.frame.height - 1}));
        CHECK(!capture_window_region(bounds, {0, 0, bounds.frame.width - 1, bounds.frame.height}));
        bounds.frame = {}; // DWM unavailable: use the matching physical window.
        CHECK(capture_window_region(bounds, {0, 0, bounds.window.width, bounds.window.height}));
        bounds.client.width = 5000; CHECK(!capture_window_region(bounds, {0, 0, bounds.window.width, bounds.window.height}));
        ++cases;
    }
    const WindowCaptureBounds borderless{{-1080, 0, 1080, 1920}, {-1080, 0, 1080, 1920}, {-1080, 0, 1080, 1920}};
    CHECK(capture_window_region(borderless, {0, 0, 1080, 1920}) == (CaptureRect{0, 0, 1080, 1920}));
    const WindowCaptureBounds maximized{{-12, -12, 3864, 2184}, {0, 0, 3840, 2160}, {0, 40, 3840, 2120}};
    CHECK(capture_window_region(maximized, {0, 0, 3840, 2160}) == (CaptureRect{0, 40, 3840, 2120}));
    CHECK(!capture_window_region(borderless, {}));
    std::cout << "Geometry: " << cases << " DPI/monitor cases; borderless, maximized, DWM fallback, resize races\n";
}
void persistent_mismatch() {
    // A DPI-unaware window at 150%: DWM bounds are physical, but its content
    // arrives at the logical 640 x 428 with a 28-pixel caption.
    const WindowCaptureBounds unaware{{952, 142, 976, 658}, {960, 150, 960, 642}, {960, 192, 960, 600}};
    const CaptureRect logical{0, 0, 640, 428};
    CHECK(!capture_window_region(unaware, logical));
    CHECK(capture_window_scaled_region(unaware, logical) == (CaptureRect{0, 28, 640, 400}));
    // A different shape is a resize, not a scale.
    CHECK(!capture_window_scaled_region(unaware, {0, 0, 640, 500}));
    WindowCropSelector selector;
    for (int frame = 1; frame < WindowCropSelector::persistent_frames; ++frame) CHECK(!selector.select(unaware, logical));
    CHECK(selector.select(unaware, logical) == (CaptureRect{0, 28, 640, 400}));
    CHECK(selector.skipped() == WindowCropSelector::persistent_frames - 1 && selector.scaled() == 1);
    // Exact matches win and reset the streak; changing sizes during a drag and
    // missing bounds (minimised) never reach the scaled fallback.
    CHECK(selector.select(unaware, {0, 0, 960, 642}) == (CaptureRect{0, 42, 960, 600}));
    for (int frame = 0; frame < 2 * WindowCropSelector::persistent_frames; ++frame) {
        CHECK(!selector.select(unaware, {0, 0, 700 + frame, 450 + frame}));
        CHECK(!selector.select(std::nullopt, logical));
    }
    CHECK(selector.scaled() == 1);
    std::cout << "Persistent mismatch: DPI-virtualised content scaled after " << WindowCropSelector::persistent_frames
              << " frames; resize races and minimised windows skipped and counted\n";
}
void physical_bounds() {
    // Hidden owned windows exercise Win32 coordinates across caller/target DPI
    // contexts. Nothing is shown, captured or brought to the foreground.
    const auto original = GetThreadDpiAwarenessContext();
    struct Restore { DPI_AWARENESS_CONTEXT context; ~Restore() { SetThreadDpiAwarenessContext(context); } } restore{original};
    std::vector<RECT> monitors;
    EnumDisplayMonitors(nullptr, nullptr, [](HMONITOR, HDC, LPRECT rect, LPARAM data) -> BOOL {
        reinterpret_cast<std::vector<RECT>*>(data)->push_back(*rect); return TRUE;
    }, reinterpret_cast<LPARAM>(&monitors));
    int cases = 0;
    for (auto monitor : monitors) for (const auto target : {DPI_AWARENESS_CONTEXT_UNAWARE, DPI_AWARENESS_CONTEXT_SYSTEM_AWARE, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2}) {
        CHECK(SetThreadDpiAwarenessContext(target));
        const auto hwnd = CreateWindowExW(0, L"STATIC", L"ClypDat bounds test", WS_OVERLAPPEDWINDOW,
            monitor.left + 100, monitor.top + 100, 640, 400, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        CHECK(hwnd);
        struct Destroy { HWND hwnd; ~Destroy() { DestroyWindow(hwnd); } } destroy{hwnd};
        CHECK(SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2));
        RECT client{}; CHECK(GetClientRect(hwnd, &client));
        POINT start{}, end{client.right, client.bottom}; CHECK(ClientToScreen(hwnd, &start)); CHECK(ClientToScreen(hwnd, &end));
        const CaptureRect expected{start.x, start.y, end.x - start.x, end.y - start.y};
        for (const auto caller : {DPI_AWARENESS_CONTEXT_UNAWARE, DPI_AWARENESS_CONTEXT_SYSTEM_AWARE, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2}) {
            CHECK(SetThreadDpiAwarenessContext(caller));
            const auto before = GetThreadDpiAwarenessContext();
            const auto bounds = capture_window_bounds(reinterpret_cast<uintptr_t>(hwnd));
            CHECK(bounds && bounds->client == expected);
            // Not minimised: the restored shape has the physical window's
            // aspect ratio, whatever DPI space the placement is in.
            const auto shape = capture_window_restored_shape(reinterpret_cast<uintptr_t>(hwnd));
            CHECK(shape && std::abs(double(shape->width) / shape->height - double(bounds->window.width) / bounds->window.height) < 0.01);
            CHECK(AreDpiAwarenessContextsEqual(before, GetThreadDpiAwarenessContext()));
            ++cases;
        }
    }
    CHECK(!capture_window_bounds(0)); CHECK(!capture_window_bounds(1));
    CHECK(!capture_window_restored_shape(0)); CHECK(!capture_window_restored_shape(1));
    std::cout << "Win32 physical bounds: " << cases << " hidden-window DPI cases; caller context restored\n";
}
void check_content(CapturePixels& output, uint32_t color) {
    if (output.deferred) { output.texture = output.deferred->materialize(); output.deferred.reset(); CHECK(output.texture); }
    capture_copy_texture_pixels(output);
    for (size_t i = 0; i < output.bgra.size(); i += 4)
        if (output.bgra[i] != uint8_t(color) || output.bgra[i + 1] != uint8_t(color >> 8) || output.bgra[i + 2] != uint8_t(color >> 16))
            throw std::runtime_error("Window capture includes title bar or green right border");
}
void generated_window(bool hardware) {
    // Physical bounds of a decorated 150% window, including an invisible
    // resize margin, a 40-pixel caption and a 6-pixel right border.
    const WindowCaptureBounds bounds{{92, 192, 656, 416}, {100, 200, 640, 400}, {101, 240, 633, 359}};
    const auto crop = capture_window_region(bounds, {0, 0, 640, 400}); CHECK(crop);
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
    CHECK(SUCCEEDED(create_test_d3d11_device(nullptr, hardware ? D3D_DRIVER_TYPE_HARDWARE : D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context)));
    ComPtr<ID3D11Multithread> protection; CHECK(SUCCEEDED(context.As(&protection))); protection->SetMultithreadProtected(TRUE);
    std::vector<uint32_t> pixels(640 * 400, 0xff00ff00); // Green frame/padding.
    for (int y = 40; y < 399; ++y) for (int x = 1; x < 634; ++x) pixels[y * 640 + x] = 0xff204080;
    D3D11_TEXTURE2D_DESC desc{}; desc.Width = 640; desc.Height = 400; desc.MipLevels = 1; desc.ArraySize = 1;
    desc.SampleDesc.Count = 1; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA data{pixels.data(), 640 * 4, 0}; ComPtr<ID3D11Texture2D> texture;
    CHECK(SUCCEEDED(device->CreateTexture2D(&desc, &data, &texture)));
    CapturedFrameStore store(device.Get(), 4, 80);
    for (const bool borrowed : {false, true}) {
        int released = 0;
        if (borrowed) CHECK(store.publish(texture.Get(), [&] { ++released; }, 123, {}, crop));
        else CHECK(store.deliver(texture.Get(), 123, {}, crop));
        CapturePixels output; int64_t stamp = 0;
        CHECK(store.take(output, stamp, std::chrono::milliseconds(0))); check_content(output, 0xff204080);
        CHECK(output.width == 633 && output.height == 359 && stamp == 123);
        CHECK(released == (borrowed ? 1 : 0));
    }
    // An older deferred frame retains its own crop after newer geometry arrives.
    int released = 0; CapturePixels first, second; int64_t stamp = 0;
    CHECK(store.publish(texture.Get(), [&] { ++released; }, 124, {}, crop));
    CHECK(store.take(first, stamp, std::chrono::milliseconds(0)));
    CHECK(store.publish(texture.Get(), [&] { ++released; }, 125, {}, CaptureRect{0, 0, 16, 16}));
    CHECK(store.take(second, stamp, std::chrono::milliseconds(0)));
    check_content(second, 0xff00ff00); check_content(first, 0xff204080);
    CHECK(first.width == 633 && first.height == 359 && released == 2);
    first = {}; second = {};
    CHECK(!store.publish(texture.Get(), [&] { ++released; }, 126, {}, CaptureRect{1, 40, 640, 400}));
    CHECK(released == 3);
    CHECK(!store.deliver(texture.Get(), 127, {}, CaptureRect{}));
    // Monitor/region captures retain their existing default crop.
    CapturedFrameStore region_store(device.Get(), 4, 80, *crop);
    CHECK(region_store.deliver(texture.Get(), 128)); CapturePixels region;
    CHECK(region_store.take(region, stamp, std::chrono::milliseconds(0))); check_content(region, 0xff204080);
    std::cout << "Generated window: title/green border excluded; arrival/deferred copies, retained crops, invalid crops, monitor regions\n";

    class Source final : public RecordingFrameSource {
        ComPtr<ID3D11Device> device_;
        ComPtr<ID3D11Texture2D> texture_;
        CapturedFrameStore frames_;
        CaptureRect crop_;
        bool delivered_ = false;
    public:
        Source(ID3D11Device* device, ID3D11Texture2D* texture, CaptureRect crop)
            : device_(device), texture_(texture), frames_(device, 4, 80), crop_(crop) {}
        bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
            if (delivered_) { std::this_thread::sleep_for(timeout); return false; }
            delivered_ = true;
            CHECK(frames_.publish(texture_.Get(), [] {}, 0, {}, crop_));
            int64_t stamp = 0; return frames_.take(pixels, stamp, timeout);
        }
        bool eligible() const override { return true; }
        const char* name() const override { return "generated client capture"; }
        ID3D11Device* d3d_device() const override { return device_.Get(); }
        CaptureRect content_bounds() const override { return {0, 0, crop_.width, crop_.height}; }
    };
    RecordingCaptureConfig config; config.max_height = 480; config.fps = 30; config.cpu_encoder = !hardware;
    std::mutex mutex; std::condition_variable changed;
    std::vector<Packet> packets; std::shared_ptr<const CaptureGeneration> generation;
    RecordingCaptureCallbacks callbacks;
    callbacks.generation = [&](auto value) { std::lock_guard lock(mutex); generation = std::move(value); };
    callbacks.packet = [&](auto, Packet packet, int64_t, bool) {
        std::lock_guard lock(mutex); packets.push_back(std::move(packet)); changed.notify_all();
    };
    RecordingCapture capture(config, callbacks, std::make_unique<Source>(device.Get(), texture.Get(), *crop));
    capture.start();
    { std::unique_lock lock(mutex); CHECK(changed.wait_for(lock, std::chrono::seconds(5), [&] { return packets.size() >= 3; })); }
    CHECK(capture.stop()); const auto health = capture.health(); CHECK(health.error.empty()); CHECK(generation);
    CHECK(health.output_width == 846 && health.output_height == 480);
    // Any vendor's encoder: GPU conversion must run without a CPU fallback,
    // with D3D11 input unless the selected encoder reads frames back.
    if (hardware) {
        CHECK(health.processing_path.rfind("d3d11-video-processor", 0) == 0);
        CHECK(health.hardware_input == (health.processing_path != "d3d11-video-processor-readback"));
    }
    CodecContext decoder(avcodec_alloc_context3(avcodec_find_decoder(generation->codec->codec_id)));
    CHECK(decoder); CHECK(avcodec_parameters_to_context(decoder.get(), generation->codec.get()) == 0);
    CHECK(avcodec_open2(decoder.get(), decoder->codec, nullptr) == 0);
    OwnedFrame frame(av_frame_alloc()); CHECK(frame); int decoded = 0;
    const auto fit = capture_aspect_fit(633, 359, 846, 480);
    auto receive = [&] {
        while (true) {
            const auto result = avcodec_receive_frame(decoder.get(), frame.get());
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) break;
            CHECK(result == 0 && frame->width == 846 && frame->height == 480);
            CHECK(frame->format == AV_PIX_FMT_YUV420P);
            for (int plane = 0; plane < 3; ++plane) {
                const int divisor = plane ? 2 : 1, black = plane ? 128 : 16;
                const int center = frame->data[plane][(frame->height / (2 * divisor)) * frame->linesize[plane] + frame->width / (2 * divisor)];
                for (int y = 0; y < frame->height / divisor; ++y) for (int x = 0; x < frame->width / divisor; ++x) {
                    const bool inside = x >= fit.x / divisor && x < (fit.x + fit.width) / divisor && y >= fit.y / divisor && y < (fit.y + fit.height) / divisor;
                    CHECK(std::abs(int(frame->data[plane][y * frame->linesize[plane] + x]) - (inside ? center : black)) <= 3);
                }
            }
            ++decoded; av_frame_unref(frame.get());
        }
    };
    for (const auto& packet : packets) { CHECK(avcodec_send_packet(decoder.get(), packet.get()) == 0); receive(); }
    CHECK(avcodec_send_packet(decoder.get(), nullptr) == 0); receive(); CHECK(decoded >= 3);
    std::cout << "Client-aspect video: " << decoded << " frames decoded, all edge pixels checked (" << health.processing_path << ", " << health.encoder << ")\n";
}
int main(int argc, char** argv) {
    try { av_log_set_level(AV_LOG_ERROR); geometry_cases(); persistent_mismatch(); physical_bounds(); generated_window(argc > 1 && std::string(argv[1]) == "--gpu"); return 0; }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}
