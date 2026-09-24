// Manual: auto-clip detector cost on the real capture pipeline. A generated
// 4K GPU source (a moving patch on a 3840x2160 texture, 144 FPS) records to
// 2560x1440 at 90 FPS with NVENC while the detector samples the Helldivers 2
// layout. Uses only the public capture API, so it builds against earlier
// revisions too.
//
// detector_bench <seconds> <stage|reference|off|idle>
//   stage      the build's detector
//   reference  the full-frame path (builds with DetectorStage only)
//   off        no detector configured
//   idle       detector enabled for 2 s, then disabled: detection thread cost
//              with nothing configured
#include "recording_capture.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <dxgi1_4.h>
#include <intrin.h>
#include <psapi.h>
#include <wrl/client.h>
extern "C" {
#include <libavutil/log.h>
}
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdlib>
#include <iomanip>
#include <iostream>
#include <new>
#include <string>
#include <thread>
#include <vector>

using namespace clypdat;
using namespace std::chrono_literals;
using Microsoft::WRL::ComPtr;

// Allocations made on the detection thread (operator new; FFmpeg's av_malloc
// is not seen).
namespace {
std::atomic<DWORD> detection_thread{0};
std::atomic<uint64_t> detection_allocations{0}, detection_allocated_bytes{0}, detection_large_allocations{0};
void count(size_t size) {
    if (GetCurrentThreadId() != detection_thread.load(std::memory_order_relaxed)) return;
    detection_allocations.fetch_add(1, std::memory_order_relaxed); detection_allocated_bytes.fetch_add(size, std::memory_order_relaxed);
    if (size >= 1 << 20) detection_large_allocations.fetch_add(1, std::memory_order_relaxed);
}
}
void* operator new(size_t size) { count(size); if (void* p = std::malloc(size ? size : 1)) return p; throw std::bad_alloc(); }
void* operator new[](size_t size) { count(size); if (void* p = std::malloc(size ? size : 1)) return p; throw std::bad_alloc(); }
void operator delete(void* p) noexcept { std::free(p); }
void operator delete[](void* p) noexcept { std::free(p); }
void operator delete(void* p, size_t) noexcept { std::free(p); }
void operator delete[](void* p, size_t) noexcept { std::free(p); }

namespace {
class Source final : public RecordingFrameSource {
    ComPtr<ID3D11Device> device_; ComPtr<ID3D11DeviceContext> context_; ComPtr<ID3D11Texture2D> texture_;
    std::chrono::steady_clock::time_point next_ = std::chrono::steady_clock::now(); int index_ = 0;
    std::vector<uint8_t> patch_ = std::vector<uint8_t>(size_t(256) * 256 * 4);
public:
    Source() {
        if (FAILED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &device_, nullptr, &context_)))
            throw std::runtime_error("No D3D11 device");
        ComPtr<ID3D11Multithread> protection; context_.As(&protection); protection->SetMultithreadProtected(TRUE);
        std::vector<uint8_t> initial(size_t(3840) * 2160 * 4);
        for (size_t i = 0; i < initial.size(); i += 4) { initial[i] = uint8_t(i >> 6); initial[i + 1] = uint8_t(i >> 12); initial[i + 2] = uint8_t(i >> 18); initial[i + 3] = 255; }
        D3D11_TEXTURE2D_DESC desc{}; desc.Width = 3840; desc.Height = 2160; desc.MipLevels = 1; desc.ArraySize = 1; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        D3D11_SUBRESOURCE_DATA data{initial.data(), 3840 * 4, 0};
        if (FAILED(device_->CreateTexture2D(&desc, &data, &texture_))) throw std::runtime_error("No source texture");
    }
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
        const auto now = std::chrono::steady_clock::now();
        if (now < next_) { std::this_thread::sleep_for(std::min(timeout, std::chrono::duration_cast<std::chrono::milliseconds>(next_ - now) + 1ms)); return false; }
        next_ += std::chrono::microseconds(1000000 / 144);
        for (size_t i = 0; i < patch_.size(); i += 4) { patch_[i] = uint8_t(index_ * 3); patch_[i + 1] = uint8_t(index_ * 5); patch_[i + 2] = 255; patch_[i + 3] = 255; }
        const UINT left = UINT((index_ * 37) % (3840 - 256)), top = UINT((index_ * 23) % (2160 - 256)); ++index_;
        const D3D11_BOX box{left, top, 0, left + 256, top + 256, 1};
        context_->UpdateSubresource(texture_.Get(), 0, &box, patch_.data(), 256 * 4, 0);
        pixels.width = 3840; pixels.height = 2160; pixels.stride = 3840 * 4; pixels.bgra.clear();
        texture_->AddRef(); pixels.texture = {texture_.Get(), [](auto* p) { p->Release(); }};
        return true;
    }
    bool eligible() const override { return true; }
    const char* name() const override { return "generated 4K"; }
    ID3D11Device* d3d_device() const override { return device_.Get(); }
};
struct Process { uint64_t cpu = 0; size_t working_set = 0, commit = 0; uint64_t dedicated = 0, shared = 0; };
Process sample_process() {
    Process p; FILETIME created{}, exited{}, kernel{}, user{}; GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user);
    auto ticks = [](FILETIME t) { return (uint64_t(t.dwHighDateTime) << 32) | t.dwLowDateTime; };
    p.cpu = ticks(kernel) + ticks(user);
    PROCESS_MEMORY_COUNTERS_EX2 memory{}; GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory));
    p.working_set = memory.PrivateWorkingSetSize; p.commit = memory.PrivateUsage;
    ComPtr<IDXGIFactory1> factory; ComPtr<IDXGIAdapter1> adapter; ComPtr<IDXGIAdapter3> adapter3;
    if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) && SUCCEEDED(factory->EnumAdapters1(0, &adapter)) && SUCCEEDED(adapter.As(&adapter3))) {
        DXGI_QUERY_VIDEO_MEMORY_INFO local{}, nonlocal{};
        adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local); adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &nonlocal);
        p.dedicated = local.CurrentUsage; p.shared = nonlocal.CurrentUsage;
    }
    return p;
}
struct ThreadTime { uint64_t cycles = 0, ticks = 0; };
ThreadTime thread_time(DWORD id) {
    ThreadTime t; if (!id) return t;
    const HANDLE thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, id); if (!thread) return t;
    ULONG64 cycles = 0; QueryThreadCycleTime(thread, &cycles); t.cycles = cycles;
    FILETIME created{}, exited{}, kernel{}, user{}; GetThreadTimes(thread, &created, &exited, &kernel, &user);
    t.ticks = ((uint64_t(kernel.dwHighDateTime) << 32) | kernel.dwLowDateTime) + ((uint64_t(user.dwHighDateTime) << 32) | user.dwLowDateTime);
    CloseHandle(thread); return t;
}
double tsc_hz() {
    LARGE_INTEGER frequency{}, start{}, end{}; QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&start);
    const auto cycles = __rdtsc(); std::this_thread::sleep_for(200ms); QueryPerformanceCounter(&end);
    return double(__rdtsc() - cycles) / (double(end.QuadPart - start.QuadPart) / frequency.QuadPart);
}
}

int main(int argc, char** argv) {
    try {
        av_log_set_level(AV_LOG_ERROR);
        if (argc < 3) { std::cerr << "detector_bench <seconds> <stage|reference|off|idle>\n"; return 2; }
        const int seconds = std::atoi(argv[1]); const std::string mode = argv[2];
        const double hz = tsc_hz();
        RecordingCaptureConfig config; config.width = 2560; config.height = 1440; config.fps = 90; config.bitrate_mbps = 25;
        const std::array<CaptureNormalizedRect, 3> hd2{CaptureNormalizedRect{.34, .445, .32, .065}, {.42, .335, .16, .055}, {1152.0 / 2560, 1036.0 / 1440, 308.0 / 2560, 174.0 / 1440}};
        const bool detecting = mode != "off";
        config.detector_enabled = detecting; config.detector_normalized = hd2; config.detector_counter_mask = true;
        RecordingCaptureDependencies dependencies;
#if __has_include("detector_stage.h")
        dependencies.reference_detector = mode == "reference";
#else
        if (mode == "reference") throw std::runtime_error("This build has only the reference path; use stage");
#endif
        std::atomic<uint64_t> snapshots{0};
        RecordingCaptureCallbacks callbacks;
        callbacks.detector_snapshot = [&](RecordingDetectorSnapshot) { detection_thread = GetCurrentThreadId(); ++snapshots; };
        RecordingCapture capture(config, callbacks, std::make_unique<Source>(), std::move(dependencies));
        capture.start();
        std::this_thread::sleep_for(3s);
        if (mode == "idle") { if (!snapshots) throw std::runtime_error("No detector sample before disabling"); capture.set_detector_regions(hd2, false, false); std::this_thread::sleep_for(1s); }
        const auto warm = capture.health(); const auto process_before = sample_process(); const auto thread_before = thread_time(detection_thread);
        const auto allocations_before = detection_allocations.load(), bytes_before = detection_allocated_bytes.load(), large_before = detection_large_allocations.load();
        const auto started = std::chrono::steady_clock::now();
        double output = 0, fresh = 0; size_t peak_working_set = 0, peak_commit = 0; uint64_t peak_dedicated = 0, peak_shared = 0;
        double working_set = 0, commit = 0, dedicated = 0, shared = 0;
        for (int second = 0; second < seconds; ++second) {
            std::this_thread::sleep_for(1s);
            const auto h = capture.health(); output += h.output_fps; fresh += h.unique_fps;
            const auto p = sample_process();
            working_set += double(p.working_set); commit += double(p.commit); dedicated += double(p.dedicated); shared += double(p.shared);
            peak_working_set = std::max(peak_working_set, p.working_set); peak_commit = std::max(peak_commit, p.commit);
            peak_dedicated = std::max(peak_dedicated, p.dedicated); peak_shared = std::max(peak_shared, p.shared);
        }
        const double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
        const auto h = capture.health(); const auto process_after = sample_process(); const auto thread_after = thread_time(detection_thread);
        if (!capture.stop() || !capture.health().error.empty()) throw std::runtime_error("Capture failed: " + capture.health().error);
        const double mb = 1024.0 * 1024.0, samples = double(h.detector_copies - warm.detector_copies);
        std::cout << std::fixed << std::setprecision(2) << "mode=" << mode << " seconds=" << elapsed
                  << "\n  detection thread: cpu=" << double(thread_after.cycles - thread_before.cycles) / hz * 1000 / elapsed << " ms/s ("
                  << double(thread_after.cycles - thread_before.cycles) / elapsed / 1e6 << " Mcycles/s, GetThreadTimes "
                  << double(thread_after.ticks - thread_before.ticks) / 1e4 / elapsed << " ms/s)"
                  << " samples/s=" << samples / elapsed
                  << " allocations/s=" << double(detection_allocations - allocations_before) / elapsed
                  << " allocated=" << double(detection_allocated_bytes - bytes_before) / mb / elapsed << " MB/s large(>=1MB)/s="
                  << double(detection_large_allocations - large_before) / elapsed;
#if __has_include("detector_stage.h")
        std::cout << "\n  detector: gpu p50=" << h.detector_gpu_p50_ms << " p95=" << h.detector_gpu_p95_ms << " readback p50=" << h.detector_readback_p50_ms
                  << " p95=" << h.detector_readback_p95_ms << " convert p50=" << h.detector_convert_p50_ms << " p95=" << h.detector_convert_p95_ms << " ms"
                  << " bytes/sample=" << h.detector_bytes_per_sample << " readback=" << double(h.detector_readback_bytes - warm.detector_readback_bytes) / mb / elapsed
                  << " MB/s CreateTexture2D/s=" << double(h.detector_textures_allocated - warm.detector_textures_allocated) / elapsed
                  << " builds=" << h.detector_builds << " fallbacks=" << h.detector_fallbacks << " skipped=" << h.detector_skipped
                  << " allocationsAfterWarmup=" << h.detector_allocations_after_warmup;
#endif
        std::cout << "\n  process: cpu=" << double(process_after.cpu - process_before.cpu) / 1e7 / elapsed * 100 << "%"
                  << " privateWS avg=" << working_set / seconds / mb << " peak=" << peak_working_set / mb << " MB"
                  << " commit avg=" << commit / seconds / mb << " peak=" << peak_commit / mb << " MB"
                  << " dedicated avg=" << dedicated / seconds / mb << " peak=" << peak_dedicated / mb << " MB"
                  << " shared avg=" << shared / seconds / mb << " peak=" << peak_shared / mb << " MB"
                  << "\n  capture: output=" << output / seconds << " fresh=" << fresh / seconds << " fps completion p50=" << h.completion_p50_ms
                  << " p95=" << h.completion_p95_ms << " ms drops=" << (h.replaced - warm.replaced) + (h.backpressure_drops - warm.backpressure_drops)
                  << " selectionDropped=" << h.selection_dropped - warm.selection_dropped << " encoder=" << h.encoder << "\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
