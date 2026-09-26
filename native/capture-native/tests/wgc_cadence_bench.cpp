// Manual: Windows Graphics Capture cadence on the real primary display. A
// 48x48 near-black window in the top-left corner presents at controlled
// rates while RecordingCapture records the monitor at 2560x1440 with NVENC,
// its WGC MinUpdateInterval fixed at a number of display ticks (0 = the
// build's policy). One line per target, ticks and source rate.
//
// wgc_cadence_bench <target fps> <ticks,...> <source fps,...> [seconds] [--cfr] [--jitter fraction] [--dwm] [--hdr]
//   source fps "refresh" presents on every display refresh.
#include "recording_capture.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <dxgi1_4.h>
#include <intrin.h>
#include <pdh.h>
#include <pdhmsg.h>
#include <psapi.h>
#include <tlhelp32.h>
#include <wrl/client.h>
extern "C" {
#include <libavutil/log.h>
}
#include <atomic>
#include <chrono>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <random>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace clypdat;
using namespace std::chrono_literals;
using Microsoft::WRL::ComPtr;

namespace {
std::vector<std::string> split(const std::string& text) {
    std::vector<std::string> parts; std::stringstream stream(text); std::string part;
    while (std::getline(stream, part, ',')) if (!part.empty()) parts.push_back(part);
    return parts;
}
// Presents a tiny window at `rate` frames per second (0 = static,
// negative = every refresh), alternating two near-black shades.
class Presenter {
    std::atomic<double> rate_{0}; std::atomic<double> jitter_{0}; std::atomic<bool> stop_{false}; std::thread thread_;
    std::atomic<uint64_t> presents_{0};
public:
    Presenter() {
        thread_ = std::thread([this] {
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
            WNDCLASSW type{}; type.lpfnWndProc = DefWindowProcW; type.hInstance = GetModuleHandleW(nullptr); type.lpszClassName = L"ClypDatCadencePresenter"; RegisterClassW(&type);
            HWND window = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, type.lpszClassName, L"", WS_POPUP, 0, 0, 48, 48, nullptr, nullptr, type.hInstance, nullptr);
            if (!window) return; ShowWindow(window, SW_SHOWNOACTIVATE);
            ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
            D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context);
            ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; ComPtr<IDXGIFactory2> factory;
            device.As(&dxgi); dxgi->GetAdapter(&adapter); adapter->GetParent(IID_PPV_ARGS(&factory));
            DXGI_SWAP_CHAIN_DESC1 desc{}; desc.Width = 48; desc.Height = 48; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
            desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 1; desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD; // Composed by DWM, never a hardware plane.
            ComPtr<IDXGISwapChain1> swap; factory->CreateSwapChainForHwnd(device.Get(), window, &desc, nullptr, nullptr, &swap);
            HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            std::mt19937 random(3); std::uniform_real_distribution<double> unit(-1, 1);
            LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
            auto now = [&] { LARGE_INTEGER c{}; QueryPerformanceCounter(&c); return double(c.QuadPart) / double(frequency.QuadPart); };
            double next = now(); uint64_t frame = 0; double current_rate = -1;
            while (!stop_) {
                MSG message{}; while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
                const double rate = rate_;
                if (rate != current_rate) { current_rate = rate; next = now(); }
                if (rate == 0) { std::this_thread::sleep_for(5ms); continue; }
                if (rate > 0) {
                    next += (1.0 / rate) * (1 + jitter_ * unit(random));
                    const double wait = next - now();
                    if (wait > 0) { LARGE_INTEGER due{}; due.QuadPart = -int64_t(wait * 1e7); SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE); WaitForSingleObject(timer, 100); }
                    else if (wait < -.1) next = now();
                }
                ComPtr<ID3D11Texture2D> back; swap->GetBuffer(0, IID_PPV_ARGS(&back)); ComPtr<ID3D11RenderTargetView> view;
                device->CreateRenderTargetView(back.Get(), nullptr, &view);
                const float shade = (++frame & 1) ? .12f : .125f; const float color[4]{shade, shade, shade, 1};
                context->ClearRenderTargetView(view.Get(), color);
                swap->Present(rate < 0 ? 1 : 0, 0); ++presents_;
            }
            CloseHandle(timer); swap.Reset(); DestroyWindow(window); UnregisterClassW(type.lpszClassName, type.hInstance);
        });
    }
    ~Presenter() { stop_ = true; if (thread_.joinable()) thread_.join(); }
    void rate(double value, double jitter) { jitter_ = jitter; rate_ = value; }
    uint64_t presents() const { return presents_; }
};
// Process GPU engine utilisation (3D and copy) from the GPU Engine counters.
class GpuEngines {
    PDH_HQUERY query_ = nullptr; PDH_HCOUNTER counter_ = nullptr; std::wstring pid_;
public:
    GpuEngines() : pid_(L"pid_" + std::to_wstring(GetCurrentProcessId()) + L"_") {
        if (PdhOpenQueryW(nullptr, 0, &query_) != ERROR_SUCCESS) return;
        if (PdhAddEnglishCounterW(query_, L"\\GPU Engine(*)\\Utilization Percentage", 0, &counter_) != ERROR_SUCCESS) counter_ = nullptr;
        PdhCollectQueryData(query_);
    }
    ~GpuEngines() { if (query_) PdhCloseQuery(query_); }
    void start() { if (query_) PdhCollectQueryData(query_); }
    std::pair<double, double> read() {
        if (!query_ || !counter_ || PdhCollectQueryData(query_) != ERROR_SUCCESS) return {-1, -1};
        DWORD size = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(counter_, PDH_FMT_DOUBLE, &size, &count, nullptr) != PDH_MORE_DATA) return {-1, -1};
        std::vector<uint8_t> buffer(size); auto* items = reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(buffer.data());
        if (PdhGetFormattedCounterArrayW(counter_, PDH_FMT_DOUBLE, &size, &count, items) != ERROR_SUCCESS) return {-1, -1};
        double three_d = 0, copy = 0;
        for (DWORD i = 0; i < count; ++i) {
            const std::wstring name = items[i].szName; if (name.find(pid_) == std::wstring::npos) continue;
            if (name.find(L"engtype_3D") != std::wstring::npos) three_d += items[i].FmtValue.doubleValue;
            else if (name.find(L"engtype_Copy") != std::wstring::npos) copy += items[i].FmtValue.doubleValue;
        }
        return {three_d, copy};
    }
};
uint64_t thread_cycles(const wchar_t* wanted) {
    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0); THREADENTRY32 entry{sizeof(entry)}; uint64_t cycles = 0;
    for (BOOL more = Thread32First(snapshot, &entry); more; more = Thread32Next(snapshot, &entry)) {
        if (entry.th32OwnerProcessID != GetCurrentProcessId()) continue;
        const HANDLE thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ThreadID); if (!thread) continue;
        PWSTR name = nullptr;
        if (SUCCEEDED(GetThreadDescription(thread, &name)) && name) { if (std::wstring(name) == wanted) { ULONG64 v = 0; QueryThreadCycleTime(thread, &v); cycles += v; } LocalFree(name); }
        CloseHandle(thread);
    }
    CloseHandle(snapshot); return cycles;
}
// Cycles of this process's threads started inside NVIDIA driver modules
// (the capture's, the encoder's and the presenter's).
uint64_t driver_cycles() {
    using Query = LONG(NTAPI*)(HANDLE, ULONG, PVOID, ULONG, PULONG);
    static const auto query = reinterpret_cast<Query>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationThread"));
    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0); THREADENTRY32 entry{sizeof(entry)}; uint64_t cycles = 0;
    for (BOOL more = Thread32First(snapshot, &entry); more; more = Thread32Next(snapshot, &entry)) {
        if (entry.th32OwnerProcessID != GetCurrentProcessId()) continue;
        const HANDLE thread = OpenThread(THREAD_QUERY_INFORMATION, FALSE, entry.th32ThreadID); if (!thread) continue;
        PVOID start = nullptr; HMODULE module = nullptr; wchar_t path[MAX_PATH]{};
        if (query && query(thread, 9, &start, sizeof(start), nullptr) == 0 && start &&
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, static_cast<LPCWSTR>(start), &module) &&
            GetModuleFileNameW(module, path, MAX_PATH)) {
            const wchar_t* file = wcsrchr(path, L'\\') ? wcsrchr(path, L'\\') + 1 : path;
            if (_wcsnicmp(file, L"nv", 2) == 0) { ULONG64 value = 0; QueryThreadCycleTime(thread, &value); cycles += value; }
        }
        CloseHandle(thread);
    }
    CloseHandle(snapshot); return cycles;
}
uint64_t process_ticks() { FILETIME c{}, e{}, k{}, u{}; GetProcessTimes(GetCurrentProcess(), &c, &e, &k, &u); return ((uint64_t(k.dwHighDateTime) << 32) | k.dwLowDateTime) + ((uint64_t(u.dwHighDateTime) << 32) | u.dwLowDateTime); }
double tsc_hz() { LARGE_INTEGER f{}, a{}, b{}; QueryPerformanceFrequency(&f); QueryPerformanceCounter(&a); const auto c = __rdtsc(); std::this_thread::sleep_for(200ms); QueryPerformanceCounter(&b); return double(__rdtsc() - c) / (double(b.QuadPart - a.QuadPart) / f.QuadPart); }
}

int main(int argc, char** argv) {
    try {
        av_log_set_level(AV_LOG_ERROR); std::cout << std::unitbuf << std::fixed << std::setprecision(2);
        if (argc < 4) { std::cerr << "wgc_cadence_bench <target fps> <ticks,...> <source fps,...> [seconds] [--cfr] [--jitter fraction] [--dwm] [--hdr]\n"; return 2; }
        const int target = std::atoi(argv[1]); const int seconds = argc > 4 && argv[4][0] != '-' ? std::atoi(argv[4]) : 12;
        bool cfr = false, dwm = false, hdr = false; double jitter = 0;
        for (int i = 4; i < argc; ++i) { const std::string a = argv[i]; if (a == "--cfr") cfr = true; else if (a == "--dwm") dwm = true; else if (a == "--hdr") hdr = true; else if (a == "--jitter" && i + 1 < argc) jitter = std::atof(argv[++i]); }
        const double hz = tsc_hz();
        Presenter presenter; GpuEngines engines;
        for (const auto& ticks_text : split(argv[2])) {
            const int ticks = std::stoi(ticks_text);
            RecordingCaptureConfig config; config.width = 2560; config.height = 1440; config.fps = target; config.bitrate_mbps = 25;
            config.variable_frame_rate = !cfr; config.wgc_update_ticks = ticks; config.wgc_dwm_timing = dwm; config.capture_hdr = hdr;
            RecordingCapture capture(config, {}); capture.start();
            for (const auto& source_text : split(argv[3])) {
                const double rate = source_text == "refresh" ? -1 : std::stod(source_text);
                presenter.rate(rate, jitter); std::this_thread::sleep_for(3s);
                const auto before = capture.health(); const auto process = process_ticks(); const auto acquisition = thread_cycles(L"ClypDat capture acquisition"); const auto driver = driver_cycles();
                const auto presents = presenter.presents(); engines.start(); const auto started = std::chrono::steady_clock::now();
                double output = 0, fresh = 0, duplicates = 0, selection = 0, min_fresh = 1e9;
                for (int second = 0; second < seconds; ++second) {
                    std::this_thread::sleep_for(1s); const auto h = capture.health();
                    output += h.output_fps; fresh += h.unique_fps; duplicates += h.duplicate_fps; selection += h.selection_dropped_fps; min_fresh = std::min(min_fresh, h.unique_fps);
                }
                const double elapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
                const auto [three_d, copy] = engines.read();
                const auto h = capture.health(); const auto& s = h.source_details; const auto& b = before.source_details;
                if (!h.error.empty()) throw std::runtime_error("Capture failed: " + h.error);
                const double copies = double(s.frames_delivered - b.frames_delivered) / elapsed;
                std::cout << "target=" << target << " ticks=" << ticks << " cadence=" << s.cadence_mode << "/" << s.update_ticks << " ceiling=" << s.producer_ceiling_fps << " refresh=" << s.display_refresh_hz << " applied=" << s.applied_interval_100ns / 1e4 << "ms source="
                          << source_text << (jitter > 0 ? "~" + std::to_string(jitter) : "") << " presented=" << double(presenter.presents() - presents) / elapsed
                          << " | callbacks=" << double(s.callbacks - b.callbacks) / elapsed << " copies=" << copies << " copyGB/s=" << copies * 3840 * 2160 * 4 / 1e9
                          << " callbackCpu=" << double(s.callback_us - b.callback_us) / 1000 / elapsed << "ms/s acquisitionCpu=" << double(thread_cycles(L"ClypDat capture acquisition") - acquisition) / hz * 1000 / elapsed
                          << "ms/s driverCpu=" << double(driver_cycles() - driver) / hz * 1000 / elapsed << "ms/s process=" << double(process_ticks() - process) / 1e7 / elapsed * 100 << "% gpu3d=" << three_d << "% gpuCopy=" << copy << "%"
                          << " | output=" << output / seconds << " fresh=" << fresh / seconds << " minFresh=" << min_fresh << " dup=" << duplicates / seconds
                          << " selDrop=" << selection / seconds << " latency p50=" << h.capture_latency_p50_ms << " p95=" << h.capture_latency_p95_ms
                          << " timestampToAcquire p50=" << h.timestamp_to_acquire_p50_ms << " p95=" << h.timestamp_to_acquire_p95_ms
                          << " selectionError p50=" << h.selection_error_p50_ms << " p95=" << h.selection_error_p95_ms
                          << " judder p50=" << h.output_judder_p50_ms << " p95=" << h.output_judder_p95_ms
                          << " submit p50=" << h.submission_p50_ms << " p95=" << h.submission_p95_ms << " completion p50=" << h.completion_p50_ms << " p95=" << h.completion_p95_ms
                          << " drops=" << (h.backpressure_drops - before.backpressure_drops) + (h.replaced - before.replaced)
                          << " ownedPressure=" << s.owned_texture_pressure_drops - b.owned_texture_pressure_drops << " overwritten=" << s.overwritten - b.overwritten << "\n";
                // The delivery chain, and where the timestamp sits against DWM's own timing.
                std::cout << "  chain: sourceLead p50=" << h.source_lead_p50_ms << " p95=" << h.source_lead_p95_ms << " take p50=" << h.callback_take_p50_ms << " p95=" << h.callback_take_p95_ms
                          << " copy p50=" << h.callback_copy_p50_ms << " p95=" << h.callback_copy_p95_ms << " handoff p50=" << h.handoff_p50_ms << " p95=" << h.handoff_p95_ms
                          << " selectionWait p50=" << h.selection_wait_p50_ms << " p95=" << h.selection_wait_p95_ms << " timestamp->selected p50=" << h.capture_latency_p50_ms
                          << " p95=" << h.capture_latency_p95_ms << " ms";
                if (dwm) {
                    const auto timelines = capture.recent_timelines(); const double period = 1e6 / (s.display_refresh_hz > 0 ? s.display_refresh_hz : 240);
                    int on_compose = 0, on_vblank = 0, samples = 0; double lead_periods = 0;
                    for (const auto& t : timelines) {
                        if (!t.timing.dwm_compose_us || !t.timing.callback_us) continue; ++samples;
                        on_compose += std::llabs(t.timestamp_us - t.timing.dwm_compose_us) <= 1;
                        const double grid = double(t.timestamp_us - t.timing.dwm_vblank_us) / period; on_vblank += std::abs(grid - std::round(grid)) < .01;
                        lead_periods += double(t.timestamp_us - t.timing.callback_us) / period;
                    }
                    if (samples) std::cout << " | dwm: timestamp==qpcCompose " << on_compose << "/" << samples << ", on vblank grid " << on_vblank << "/" << samples
                                           << ", arrives " << lead_periods / samples << " refresh periods before its timestamp";
                }
                std::cout << "\n";
            }
            capture.stop();
        }
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
