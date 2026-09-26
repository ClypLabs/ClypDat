// Manual: the Desktop Duplication fallback on the real primary display, the
// per-frame path (--reference) against the pooled copies and GPU cursor. A
// 48x48 near-black window in the display's top-left corner presents every
// refresh so duplication delivers frames; the cursor stays wherever the user
// left it (draws are reported).
//
// dxgi_capture_bench <seconds> <WxH@fps[,...]> <cursor on|off> [--reference]
#include "recording_capture.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <dxgi1_4.h>
#include <intrin.h>
#include <pdh.h>
#include <pdhmsg.h>
#include <tlhelp32.h>
#include <wrl/client.h>
extern "C" {
#include <libavutil/log.h>
}
#include <atomic>
#include <chrono>
#include <iomanip>
#include <iostream>
#include <map>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace clypdat;
using namespace std::chrono_literals;
using Microsoft::WRL::ComPtr;

namespace {
class Presenter {
    std::atomic<bool> stop_{false}; std::thread thread_;
public:
    explicit Presenter(POINT at) {
        thread_ = std::thread([this, at] {
            WNDCLASSW type{}; type.lpfnWndProc = DefWindowProcW; type.hInstance = GetModuleHandleW(nullptr); type.lpszClassName = L"ClypDatDxgiPresenter"; RegisterClassW(&type);
            HWND window = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, type.lpszClassName, L"", WS_POPUP, at.x, at.y, 48, 48, nullptr, nullptr, type.hInstance, nullptr);
            if (!window) return; ShowWindow(window, SW_SHOWNOACTIVATE);
            ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
            D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context);
            ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; ComPtr<IDXGIFactory2> factory; device.As(&dxgi); dxgi->GetAdapter(&adapter); adapter->GetParent(IID_PPV_ARGS(&factory));
            DXGI_SWAP_CHAIN_DESC1 desc{}; desc.Width = 48; desc.Height = 48; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
            desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2; desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
            ComPtr<IDXGISwapChain1> swap; factory->CreateSwapChainForHwnd(device.Get(), window, &desc, nullptr, nullptr, &swap);
            for (uint64_t frame = 0; !stop_ && swap; ++frame) {
                MSG message{}; while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
                ComPtr<ID3D11Texture2D> back; swap->GetBuffer(0, IID_PPV_ARGS(&back)); ComPtr<ID3D11RenderTargetView> view; device->CreateRenderTargetView(back.Get(), nullptr, &view);
                const float shade = (frame & 1) ? .12f : .125f; const float color[4]{shade, shade, shade, 1}; context->ClearRenderTargetView(view.Get(), color); swap->Present(1, 0);
            }
            swap.Reset(); DestroyWindow(window); UnregisterClassW(type.lpszClassName, type.hInstance);
        });
    }
    ~Presenter() { stop_ = true; if (thread_.joinable()) thread_.join(); }
};
class GpuEngines {
    PDH_HQUERY query_ = nullptr; PDH_HCOUNTER counter_ = nullptr; std::wstring pid_;
public:
    GpuEngines() : pid_(L"pid_" + std::to_wstring(GetCurrentProcessId()) + L"_") {
        if (PdhOpenQueryW(nullptr, 0, &query_) == ERROR_SUCCESS && PdhAddEnglishCounterW(query_, L"\\GPU Engine(*)\\Utilization Percentage", 0, &counter_) == ERROR_SUCCESS) PdhCollectQueryData(query_);
    }
    ~GpuEngines() { if (query_) PdhCloseQuery(query_); }
    void start() { if (query_) PdhCollectQueryData(query_); }
    double read_3d() {
        if (!query_ || !counter_ || PdhCollectQueryData(query_) != ERROR_SUCCESS) return -1;
        DWORD size = 0, count = 0; if (PdhGetFormattedCounterArrayW(counter_, PDH_FMT_DOUBLE, &size, &count, nullptr) != PDH_MORE_DATA) return -1;
        std::vector<uint8_t> buffer(size); auto* items = reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(buffer.data());
        if (PdhGetFormattedCounterArrayW(counter_, PDH_FMT_DOUBLE, &size, &count, items) != ERROR_SUCCESS) return -1;
        double total = 0; for (DWORD i = 0; i < count; ++i) { const std::wstring name = items[i].szName; if (name.find(pid_) != std::wstring::npos && name.find(L"engtype_3D") != std::wstring::npos) total += items[i].FmtValue.doubleValue; }
        return total;
    }
};
// Cycles of this process's threads: the named acquisition thread, and threads
// started inside NVIDIA driver modules.
struct Cycles { uint64_t acquisition = 0, driver = 0; };
Cycles thread_cycles() {
    using Query = LONG(NTAPI*)(HANDLE, ULONG, PVOID, ULONG, PULONG);
    static const auto query = reinterpret_cast<Query>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationThread"));
    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0); THREADENTRY32 entry{sizeof(entry)}; Cycles cycles;
    for (BOOL more = Thread32First(snapshot, &entry); more; more = Thread32Next(snapshot, &entry)) {
        if (entry.th32OwnerProcessID != GetCurrentProcessId()) continue;
        const HANDLE thread = OpenThread(THREAD_QUERY_INFORMATION, FALSE, entry.th32ThreadID); if (!thread) continue;
        ULONG64 value = 0; QueryThreadCycleTime(thread, &value);
        PWSTR name = nullptr;
        if (SUCCEEDED(GetThreadDescription(thread, &name)) && name) { if (std::wstring(name) == L"ClypDat capture acquisition") cycles.acquisition += value; LocalFree(name); }
        PVOID start = nullptr; HMODULE module = nullptr; wchar_t path[MAX_PATH]{};
        if (query && query(thread, 9, &start, sizeof(start), nullptr) == 0 && start &&
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, static_cast<LPCWSTR>(start), &module) &&
            GetModuleFileNameW(module, path, MAX_PATH)) {
            const std::wstring file = wcsrchr(path, L'\\') ? wcsrchr(path, L'\\') + 1 : path;
            if (_wcsnicmp(file.c_str(), L"nv", 2) == 0) cycles.driver += value;
        }
        CloseHandle(thread);
    }
    CloseHandle(snapshot); return cycles;
}
uint64_t process_ticks() { FILETIME c{}, e{}, k{}, u{}; GetProcessTimes(GetCurrentProcess(), &c, &e, &k, &u); return ((uint64_t(k.dwHighDateTime) << 32) | k.dwLowDateTime) + ((uint64_t(u.dwHighDateTime) << 32) | u.dwLowDateTime); }
double tsc_hz() { LARGE_INTEGER f{}, a{}, b{}; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&a); const auto c = __rdtsc(); std::this_thread::sleep_for(200ms); QueryPerformanceCounter(&b); return double(__rdtsc() - c) / (double(b.QuadPart - a.QuadPart) / f.QuadPart); }
std::pair<double, double> video_memory() {
    ComPtr<IDXGIFactory1> factory; ComPtr<IDXGIAdapter1> adapter; ComPtr<IDXGIAdapter3> adapter3; DXGI_QUERY_VIDEO_MEMORY_INFO local{}, shared{};
    if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) && SUCCEEDED(factory->EnumAdapters1(0, &adapter)) && SUCCEEDED(adapter.As(&adapter3))) {
        adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local); adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, &shared);
    }
    return {local.CurrentUsage / 1048576.0, shared.CurrentUsage / 1048576.0};
}
std::vector<std::string> split(const std::string& text) { std::vector<std::string> parts; std::stringstream stream(text); std::string part; while (std::getline(stream, part, ',')) if (!part.empty()) parts.push_back(part); return parts; }
}

int main(int argc, char** argv) {
    try {
        av_log_set_level(AV_LOG_ERROR); std::cout << std::unitbuf << std::fixed << std::setprecision(2);
        if (argc < 4) { std::cerr << "dxgi_capture_bench <seconds> <WxH@fps[,...]> <cursor on|off> [--reference]\n"; return 2; }
        const int seconds = std::atoi(argv[1]); const bool cursor = std::string(argv[3]) == "on"; bool reference = false;
        for (int i = 4; i < argc; ++i) if (std::string(argv[i]) == "--reference") reference = true;
        const double hz = tsc_hz();
        Presenter presenter({0, 0}); GpuEngines engines;
        for (const auto& mode : split(argv[2])) {
            RecordingCaptureConfig config; int width = 0, height = 0, fps = 0;
            if (sscanf_s(mode.c_str(), "%dx%d@%d", &width, &height, &fps) != 3) throw std::runtime_error("Bad mode " + mode);
            config.width = width; config.height = height; config.fps = fps; config.bitrate_mbps = 25; config.variable_frame_rate = true;
            config.prefer_dxgi = true; config.capture_cursor = cursor; config.dxgi_reference_path = reference;
            RecordingCapture capture(config, {}); capture.start();
            std::this_thread::sleep_for(4s);
            const auto before = capture.health(); const auto cycles = thread_cycles(); const auto process = process_ticks(); engines.start();
            const auto started = std::chrono::steady_clock::now();
            double output = 0, fresh = 0, duplicates = 0, dedicated = 0, shared = 0;
            for (int second = 0; second < seconds; ++second) {
                std::this_thread::sleep_for(1s); const auto h = capture.health(); output += h.output_fps; fresh += h.unique_fps; duplicates += h.duplicate_fps;
                const auto [d, s] = video_memory(); dedicated += d; shared += s;
            }
            const double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
            const double gpu3d = engines.read_3d(); const auto after_cycles = thread_cycles(); const auto after_process = process_ticks();
            const auto h = capture.health(); const auto& s = h.source_details; const auto& b = before.source_details;
            if (!h.error.empty()) throw std::runtime_error("Capture failed: " + h.error);
            if (h.source != "DXGI Desktop Duplication") throw std::runtime_error("Source is " + h.source);
            capture.stop();
            auto rate = [&](uint64_t now, uint64_t then) { return double(now - then) / elapsed; };
            std::cout << mode << " cursor=" << (cursor ? "on" : "off") << (reference ? " reference" : " pooled") << (s.hdr_display ? " hdr" : " sdr") << ": frames=" << rate(s.frames_delivered, b.frames_delivered)
                      << "/s | cpu acquisition=" << double(after_cycles.acquisition - cycles.acquisition) / hz * 100 / elapsed << "% driver=" << double(after_cycles.driver - cycles.driver) / hz * 100 / elapsed
                      << "% process=" << double(after_process - process) / 1e7 / elapsed * 100 << "% gpu3d=" << gpu3d
                      << "% | textures=" << rate(s.owned_textures_allocated + s.cursor_textures_created, b.owned_textures_allocated + b.cursor_textures_created)
                      << "/s pool " << s.owned_textures_allocated << "/" << s.owned_texture_capacity << " peak " << s.owned_textures_peak << " pressure " << s.owned_texture_pressure_drops - b.owned_texture_pressure_drops
                      << " copy p50=" << s.copy_p50_ms << " p95=" << s.copy_p95_ms
                      << " | cursor gpu=" << rate(s.cursor_gpu_draws, b.cursor_gpu_draws) << "/s cpu=" << rate(s.cursor_cpu_draws, b.cursor_cpu_draws) << "/s fallback="
                      << rate(s.cursor_cpu_fallbacks, b.cursor_cpu_fallbacks) << "/s uploads=" << rate(s.cursor_uploads, b.cursor_uploads) << "/s readback="
                      << rate(s.cursor_readback_bytes, b.cursor_readback_bytes) / 1024 << "KB/s compose p50=" << s.cursor_compose_p50_ms << " p95=" << s.cursor_compose_p95_ms
                      << " lock p95=" << s.cursor_lock_wait_p95_ms
                      << " | submit p50=" << h.submission_p50_ms << " p95=" << h.submission_p95_ms << " completion p50=" << h.completion_p50_ms << " p95=" << h.completion_p95_ms
                      << " output=" << output / seconds << " fresh=" << fresh / seconds << " dup=" << duplicates / seconds
                      << " backpressure=" << h.backpressure_drops - before.backpressure_drops << " latency p50=" << h.capture_latency_p50_ms << " p95=" << h.capture_latency_p95_ms
                      << " | dedicated=" << dedicated / seconds << "MB shared=" << shared / seconds << "MB\n";
        }
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
