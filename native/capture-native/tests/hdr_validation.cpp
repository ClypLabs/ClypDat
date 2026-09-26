// Manual: HDR capture on a real display. A pattern window drawn from a known
// DIB (grey steps, primaries at full and half level, a ramp, dark detail,
// near-white steps, text) sits on the display, and what capture makes of it
// is compared with the DIB: exact in SDR, after tone-mapping in HDR. Display
// state changes only through DisplayConfig, and every mode puts back what it
// found.
//
// hdr_validation state
// hdr_validation pattern <DISPLAYn> <on|off|keep>          both backends, fresh sources
// hdr_validation white <DISPLAYn> <nits,...>                SDR brightness changes under a live source
// hdr_validation transition <DISPLAYn> <work dir> <ffmpeg> [dxgi] [overlay]  replay armed through SDR, HDR, SDR; a saved clip each
// hdr_validation move <HDR DISPLAYn> <SDR DISPLAYn>         window capture moving between the displays
// hdr_validation toggle <DISPLAYn> <count> <work dir> <ffmpeg> [dxgi]  HDR flipped under an armed replay
// hdr_validation fixture <DISPLAYn> <file>                  raw FP16 rows of the pattern (HDR on)
// hdr_validation suspend <DISPLAYn> <work dir> <ffmpeg>     displays powered off for 10 s under an armed replay
#include "recorder_session.h"
#include "recording_capture.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <dxgi1_6.h>
#include <psapi.h>
#include <wrl/client.h>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/log.h>
}
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <optional>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace clypdat;
using namespace std::chrono_literals;
using Microsoft::WRL::ComPtr;

namespace {
void check(bool value, const std::string& message) { if (!value) throw std::runtime_error(message); }
int64_t monotonic_us() { return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count(); }
std::wstring wide(const std::string& text) { return {text.begin(), text.end()}; }
std::string narrow(const std::wstring& text) { std::string s; for (auto c : text) s += char(c); return s; }

// ---- Display state -------------------------------------------------------
struct Target { LUID adapter{}; UINT32 id = 0; std::wstring gdi; HMONITOR monitor = nullptr; RECT bounds{}; };
struct Colour { bool supported = false, enabled = false, wide = false; float white = 80; };
// Undocumented counterpart of DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
// the SDR content brightness slider.
struct SetWhite { DISPLAYCONFIG_DEVICE_INFO_HEADER header; ULONG level; BYTE final_value; };
constexpr auto SET_SDR_WHITE_LEVEL = DISPLAYCONFIG_DEVICE_INFO_TYPE(0xFFFFFFEE);
Target target(const std::string& name) {
    Target t; t.gdi = wide(name.find('\\') == std::string::npos ? "\\\\.\\" + name : name);
    UINT32 paths = 0, modes = 0; GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, &paths, &modes);
    std::vector<DISPLAYCONFIG_PATH_INFO> path(paths); std::vector<DISPLAYCONFIG_MODE_INFO> mode(modes);
    check(QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, &paths, path.data(), &modes, mode.data(), nullptr) == ERROR_SUCCESS, "QueryDisplayConfig");
    for (UINT32 i = 0; i < paths; ++i) {
        DISPLAYCONFIG_SOURCE_DEVICE_NAME source{}; source.header = {DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, sizeof(source), path[i].sourceInfo.adapterId, path[i].sourceInfo.id};
        if (DisplayConfigGetDeviceInfo(&source.header) == ERROR_SUCCESS && _wcsicmp(source.viewGdiDeviceName, t.gdi.c_str()) == 0) { t.adapter = path[i].targetInfo.adapterId; t.id = path[i].targetInfo.id; }
    }
    check(t.id || t.adapter.LowPart || t.adapter.HighPart, "No display " + name);
    struct Search { Target& t; } search{t};
    EnumDisplayMonitors(nullptr, nullptr, [](HMONITOR monitor, HDC, LPRECT, LPARAM data) -> BOOL {
        auto& s = *reinterpret_cast<Search*>(data); MONITORINFOEXW info{}; info.cbSize = sizeof(info);
        if (GetMonitorInfoW(monitor, &info) && _wcsicmp(info.szDevice, s.t.gdi.c_str()) == 0) { s.t.monitor = monitor; s.t.bounds = info.rcMonitor; return FALSE; }
        return TRUE;
    }, reinterpret_cast<LPARAM>(&search));
    check(t.monitor != nullptr, "No monitor " + name);
    return t;
}
Colour colour(const Target& t) {
    Colour c; DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO info{}; info.header = {DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, sizeof(info), t.adapter, t.id};
    if (DisplayConfigGetDeviceInfo(&info.header) == ERROR_SUCCESS) { c.supported = info.advancedColorSupported; c.enabled = info.advancedColorEnabled; c.wide = info.wideColorEnforced; }
    DISPLAYCONFIG_SDR_WHITE_LEVEL white{}; white.header = {DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL, sizeof(white), t.adapter, t.id};
    if (DisplayConfigGetDeviceInfo(&white.header) == ERROR_SUCCESS) c.white = 80.f * white.SDRWhiteLevel / 1000.f;
    return c;
}
bool hdr(const Colour& c) { return c.enabled && !c.wide; }
std::string describe(const Colour& c) {
    std::ostringstream s; s << (hdr(c) ? "HDR" : c.wide ? "SDR (wide colour enforced)" : "SDR") << ", SDR white " << c.white << " nits"; return s.str();
}
void set_hdr(const Target& t, bool on) {
    DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE set{}; set.header = {DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE, sizeof(set), t.adapter, t.id}; set.enableAdvancedColor = on;
    check(DisplayConfigSetDeviceInfo(&set.header) == ERROR_SUCCESS, "Set advanced colour");
    const auto end = std::chrono::steady_clock::now() + 10s;
    while (colour(t).enabled != on && std::chrono::steady_clock::now() < end) std::this_thread::sleep_for(100ms);
    check(colour(t).enabled == on, "Advanced colour did not change");
    std::this_thread::sleep_for(2s); // The mode change settles.
}
void set_white(const Target& t, float nits) {
    SetWhite set{}; set.header = {SET_SDR_WHITE_LEVEL, sizeof(set), t.adapter, t.id}; set.level = ULONG(std::lround(nits * 1000 / 80)); set.final_value = 1;
    check(DisplayConfigSetDeviceInfo(&set.header) == ERROR_SUCCESS, "Set SDR white level");
    const auto end = std::chrono::steady_clock::now() + 5s;
    while (std::abs(colour(t).white - nits) > .6f && std::chrono::steady_clock::now() < end) std::this_thread::sleep_for(50ms);
    check(std::abs(colour(t).white - nits) <= .6f, "SDR white level did not change");
    std::this_thread::sleep_for(500ms);
}
// Puts a display's HDR state and SDR white level back as found.
class Restore {
    Target t_; Colour original_;
public:
    explicit Restore(Target t) : t_(std::move(t)), original_(colour(t_)) { std::cout << narrow(t_.gdi) << " found " << describe(original_) << "\n"; }
    ~Restore() {
        try {
            if (colour(t_).enabled != original_.enabled) set_hdr(t_, original_.enabled);
            if (original_.enabled && std::abs(colour(t_).white - original_.white) > .6f) set_white(t_, original_.white);
            std::cout << narrow(t_.gdi) << " restored: " << describe(colour(t_)) << "\n";
        } catch (const std::exception& error) { std::cerr << "RESTORE FAILED for " << narrow(t_.gdi) << ": " << error.what() << "\n"; }
    }
};

// ---- Pattern -------------------------------------------------------------
constexpr int PW = 512, PH = 320;
struct Band { const char* name; int top, bottom; };
const Band bands[]{{"grey steps", 0, 48}, {"primaries", 48, 96}, {"half primaries", 96, 144}, {"ramp", 144, 176}, {"dark detail", 176, 224}, {"near white", 224, 256}, {"text", 256, 320}};
class Pattern {
    HWND window_ = nullptr; HDC memory_ = nullptr; HBITMAP bitmap_ = nullptr; HGDIOBJ previous_ = nullptr; uint8_t* bits_ = nullptr;
    static LRESULT CALLBACK procedure(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
        if (message != WM_PAINT) return DefWindowProcW(hwnd, message, w, l);
        auto* self = reinterpret_cast<Pattern*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        PAINTSTRUCT paint; HDC dc = BeginPaint(hwnd, &paint); if (self) BitBlt(dc, 0, 0, PW, PH, self->memory_, 0, 0, SRCCOPY); EndPaint(hwnd, &paint); return 0;
    }
public:
    std::vector<uint8_t> expected; // BGRA, what the window shows.
    Pattern(int x, int y) {
        BITMAPINFO info{}; info.bmiHeader = {sizeof(BITMAPINFOHEADER), PW, -PH, 1, 32, BI_RGB};
        HDC screen = GetDC(nullptr); memory_ = CreateCompatibleDC(screen); ReleaseDC(nullptr, screen);
        bitmap_ = CreateDIBSection(memory_, &info, DIB_RGB_COLORS, reinterpret_cast<void**>(&bits_), nullptr, 0); check(bitmap_ && bits_, "Pattern DIB");
        previous_ = SelectObject(memory_, bitmap_);
        auto put = [&](int x0, int y0, int x1, int y1, int r, int g, int b) {
            for (int y = y0; y < y1; ++y) for (int x = x0; x < x1; ++x) { auto* p = bits_ + (size_t(y) * PW + x) * 4; p[0] = uint8_t(b); p[1] = uint8_t(g); p[2] = uint8_t(r); p[3] = 0; }
        };
        for (int i = 0; i < 16; ++i) put(i * 32, 0, i * 32 + 32, 48, i * 17, i * 17, i * 17);
        const int primaries[8][3]{{1, 0, 0}, {0, 1, 0}, {0, 0, 1}, {0, 1, 1}, {1, 0, 1}, {1, 1, 0}, {1, 1, 1}, {0, 0, 0}};
        for (int i = 0; i < 8; ++i) { put(i * 64, 48, i * 64 + 64, 96, primaries[i][0] * 255, primaries[i][1] * 255, primaries[i][2] * 255);
                                      put(i * 64, 96, i * 64 + 64, 144, primaries[i][0] * 128, primaries[i][1] * 128, primaries[i][2] * 128); }
        for (int x = 0; x < PW; ++x) put(x, 144, x + 1, 176, x * 255 / (PW - 1), x * 255 / (PW - 1), x * 255 / (PW - 1));
        for (int i = 0; i < 16; ++i) put(i * 32, 176, i * 32 + 32, 224, i, i, i);
        for (int i = 0; i < 16; ++i) put(i * 32, 224, i * 32 + 32, 256, 240 + i, 240 + i, 240 + i);
        put(0, 256, PW / 2, PH, 255, 255, 255); put(PW / 2, 256, PW, PH, 0, 0, 0);
        GdiFlush();
        HFONT font = CreateFontW(-26, 0, 0, 0, FW_NORMAL, 0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI"); HGDIOBJ old = SelectObject(memory_, font);
        SetBkMode(memory_, TRANSPARENT);
        RECT left{8, 260, PW / 2, PH}, right{PW / 2 + 8, 260, PW, PH};
        SetTextColor(memory_, RGB(0, 0, 0)); DrawTextW(memory_, L"ClypDat HDR 0123456789 Ag", -1, &left, DT_WORDBREAK);
        SetTextColor(memory_, RGB(255, 255, 255)); DrawTextW(memory_, L"ClypDat HDR 0123456789 Ag", -1, &right, DT_WORDBREAK);
        SelectObject(memory_, old); DeleteObject(font); GdiFlush();
        expected.assign(bits_, bits_ + size_t(PW) * PH * 4); for (size_t i = 3; i < expected.size(); i += 4) expected[i] = 255;
        WNDCLASSW type{}; type.lpfnWndProc = procedure; type.hInstance = GetModuleHandleW(nullptr); type.lpszClassName = L"ClypDatHdrPattern"; RegisterClassW(&type);
        window_ = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, type.lpszClassName, L"ClypDat HDR pattern", WS_POPUP, x, y, PW, PH, nullptr, nullptr, type.hInstance, nullptr);
        check(window_ != nullptr, "Pattern window"); SetWindowLongPtrW(window_, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(this));
        ShowWindow(window_, SW_SHOWNOACTIVATE); UpdateWindow(window_); settle(400ms);
    }
    ~Pattern() { DestroyWindow(window_); SelectObject(memory_, previous_); DeleteObject(bitmap_); DeleteDC(memory_); }
    HWND window() const { return window_; }
    RECT rect() const { RECT r{}; GetWindowRect(window_, &r); return r; }
    void move(int x, int y) { SetWindowPos(window_, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE); settle(400ms); }
    void settle(std::chrono::milliseconds time) {
        const auto end = std::chrono::steady_clock::now() + time;
        while (std::chrono::steady_clock::now() < end) { MSG m; while (PeekMessageW(&m, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&m); std::this_thread::sleep_for(5ms); }
    }
};
struct Errors { int max = 0; double mean = 0, over1 = 0, over3 = 0; int grey_tint = 0; bool monotonic = true; int dark_levels = 0; std::vector<int> steps; };
// Per band: largest and mean channel error, share of channels off by more
// than 1 and 3 codes; for grey bands the largest channel spread (tint); the
// ramp, dark detail and near-white steps must keep their order; how many of
// the 16 dark steps stay distinct.
std::vector<Errors> compare(const std::vector<uint8_t>& got, const std::vector<uint8_t>& want) {
    std::vector<Errors> result;
    for (const auto& band : bands) {
        Errors e; double sum = 0; size_t n = 0, a = 0, b = 0;
        for (int y = band.top; y < band.bottom; ++y) for (int x = 0; x < PW; ++x) for (int c = 0; c < 3; ++c) {
            const size_t i = (size_t(y) * PW + x) * 4 + c; const int d = std::abs(int(got[i]) - int(want[i]));
            e.max = std::max(e.max, d); sum += d; ++n; a += d > 1; b += d > 3;
        }
        e.mean = sum / double(n); e.over1 = 100.0 * double(a) / double(n); e.over3 = 100.0 * double(b) / double(n);
        if (std::string(band.name) != "primaries" && std::string(band.name) != "half primaries" && std::string(band.name) != "text")
            for (int y = band.top; y < band.bottom; ++y) for (int x = 0; x < PW; ++x) {
                const auto* p = &got[(size_t(y) * PW + x) * 4]; e.grey_tint = std::max({e.grey_tint, std::abs(p[0] - p[1]), std::abs(p[1] - p[2]), std::abs(p[0] - p[2])});
            }
        const int mid = (band.top + band.bottom) / 2;
        auto at = [&](int x) { return int(got[(size_t(mid) * PW + x) * 4 + 1]); };
        if (std::string(band.name) == "ramp") for (int x = 1; x < PW; ++x) e.monotonic &= at(x) >= at(x - 1);
        if (std::string(band.name) == "dark detail" || std::string(band.name) == "near white" || std::string(band.name) == "grey steps") {
            std::vector<int> levels; for (int i = 0; i < 16; ++i) levels.push_back(at(i * 32 + 16)); e.steps = levels;
            for (int i = 1; i < 16; ++i) e.monotonic &= levels[size_t(i)] >= levels[size_t(i) - 1];
            std::sort(levels.begin(), levels.end()); e.dark_levels = int(std::unique(levels.begin(), levels.end()) - levels.begin());
        }
        result.push_back(e);
    }
    return result;
}
void report(const std::string& label, const std::vector<Errors>& errors) {
    std::cout << "  " << label << ":\n";
    for (size_t i = 0; i < errors.size(); ++i) {
        const auto& e = errors[i];
        std::cout << "    " << std::left << std::setw(15) << bands[i].name << std::right << " max " << std::setw(3) << e.max << " mean " << std::setw(5) << e.mean << " >1 " << std::setw(6) << e.over1
                  << "% >3 " << std::setw(6) << e.over3 << "%";
        if (e.grey_tint || std::string(bands[i].name) == "grey steps") std::cout << " tint " << e.grey_tint;
        if (std::string(bands[i].name) == "ramp" || e.dark_levels) std::cout << (e.monotonic ? " ordered" : " OUT OF ORDER");
        if (e.dark_levels) std::cout << " distinct " << e.dark_levels << "/16";
        if (e.max > 3 && !e.steps.empty()) { std::cout << " got"; for (int v : e.steps) std::cout << " " << v; }
        std::cout << "\n";
    }
}
RecordingCaptureConfig capture_config(const Target& t, bool dxgi) {
    RecordingCaptureConfig c; c.width = 2560; c.height = 1440; c.fps = 60; c.bitrate_mbps = 25; c.variable_frame_rate = true;
    c.monitor = reinterpret_cast<uintptr_t>(t.monitor); c.capture_hdr = true; c.prefer_dxgi = dxgi; c.capture_cursor = false;
    LARGE_INTEGER counter{}, frequency{}; QueryPerformanceCounter(&counter); QueryPerformanceFrequency(&frequency);
    c.qpc_anchor = counter.QuadPart; c.qpc_frequency = frequency.QuadPart; c.monotonic_anchor_us = monotonic_us();
    return c;
}
// The pattern's pixels in the newest frames `source` delivers within `time`.
std::optional<std::vector<uint8_t>> grab(RecordingFrameSource& source, Pattern& pattern, const RECT& origin, std::chrono::milliseconds time = 1500ms) {
    std::optional<std::vector<uint8_t>> best; const auto end = std::chrono::steady_clock::now() + time; const auto r = pattern.rect();
    while (std::chrono::steady_clock::now() < end) {
        pattern.settle(1ms); CapturePixels pixels;
        if (!source.acquire(pixels, 30ms)) continue;
        capture_copy_texture_pixels(pixels);
        const int x0 = r.left - origin.left, y0 = r.top - origin.top;
        if (x0 < 0 || y0 < 0 || x0 + PW > pixels.width || y0 + PH > pixels.height) continue;
        std::vector<uint8_t> crop(size_t(PW) * PH * 4);
        for (int y = 0; y < PH; ++y) std::memcpy(&crop[size_t(y) * PW * 4], &pixels.bgra[(size_t(y0 + y) * pixels.stride) + size_t(x0) * 4], size_t(PW) * 4);
        best = std::move(crop);
    }
    return best;
}
double mean_error(const std::vector<Errors>& e) { double s = 0; for (const auto& b : e) s += b.mean; return s / double(e.size()); }
void describe_source(RecordingFrameSource& source) {
    const auto d = source.diagnostics();
    std::cout << "  source " << source.name() << ": hdrDisplay=" << d.hdr_display << " fp16Conversion=" << d.hdr_conversion << " sdrWhite=" << d.sdr_white_nits
              << " nits, pool " << d.owned_textures_allocated << "/" << d.owned_texture_capacity << " pressure " << d.owned_texture_pressure_drops << "\n";
}
double process_vram_mb() {
    ComPtr<IDXGIFactory1> factory; ComPtr<IDXGIAdapter1> adapter; ComPtr<IDXGIAdapter3> adapter3; DXGI_QUERY_VIDEO_MEMORY_INFO local{};
    if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) && SUCCEEDED(factory->EnumAdapters1(0, &adapter)) && SUCCEEDED(adapter.As(&adapter3)))
        adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local);
    return local.CurrentUsage / 1048576.0;
}
double private_mb() { PROCESS_MEMORY_COUNTERS_EX m{}; m.cb = sizeof(m); GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&m), sizeof(m)); return m.PrivateUsage / 1048576.0; }

void pattern_mode(const Target& t, const std::string& want) {
    Restore restore(t);
    if (want == "on" || want == "off") { if (hdr(colour(t)) != (want == "on")) set_hdr(t, want == "on"); }
    std::cout << "display now " << describe(colour(t)) << "\n";
    Pattern pattern(t.bounds.left + 96, t.bounds.top + 96);
    for (const bool dxgi : {false, true}) {
        auto source = create_windows_recording_source(capture_config(t, dxgi)); describe_source(*source);
        const auto got = grab(*source, pattern, t.bounds); check(got.has_value(), "No frame of the pattern");
        report(std::string(dxgi ? "DXGI" : "WGC") + " against the DIB", compare(*got, pattern.expected));
        source->stop();
    }
}
void white_mode(const Target& t, const std::vector<float>& levels) {
    Restore restore(t);
    if (!hdr(colour(t))) set_hdr(t, true);
    Pattern pattern(t.bounds.left + 96, t.bounds.top + 96);
    for (const bool dxgi : {false, true}) {
        auto source = create_windows_recording_source(capture_config(t, dxgi));
        for (const float nits : levels) {
            set_white(t, nits);
            // Recording re-reads the display profile every second through set_frame_rate.
            const auto started = std::chrono::steady_clock::now(); source->set_frame_rate(60);
            const double recreate_ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count();
            const auto d = source->diagnostics();
            const auto got = grab(*source, pattern, t.bounds); check(got.has_value(), "No frame after the white change");
            std::ostringstream label; label << (dxgi ? "DXGI" : "WGC") << " at SDR white " << nits << " (source uses " << d.sdr_white_nits << ", reprofiled in " << recreate_ms << " ms)";
            report(label.str(), compare(*got, pattern.expected));
            check(std::abs(d.sdr_white_nits - nits) < 1, "Source kept the old SDR white level");
        }
        source->stop();
    }
}
// Every frame of a clip decodes; the pattern's grey steps and primaries in
// its last frame, as BT.709 limited-range luma, against the DIB's.
struct ClipCheck { int frames = 0; bool keyframe = false; double luma_max_error = 0, luma_mean_error = 0; std::vector<double> steps, overlay; };
// Burned keyboard artwork: three 100x100 patches, opaque red, white at half
// alpha (straight), opaque mid grey, drawn at the keyboard transform.
constexpr int AW = 300, AH = 100; constexpr double AX = .55, AY = .05, AWIDTH = .3;
std::shared_ptr<const OverlayBitmap> artwork() {
    std::vector<uint8_t> bgra(size_t(AW) * AH * 4);
    for (int y = 0; y < AH; ++y) for (int x = 0; x < AW; ++x) {
        auto* p = &bgra[(size_t(y) * AW + x) * 4]; const int patch = x / 100;
        if (patch == 0) { p[0] = 0; p[1] = 0; p[2] = 255; p[3] = 255; } else if (patch == 1) { p[0] = p[1] = p[2] = 255; p[3] = 128; } else { p[0] = p[1] = p[2] = 128; p[3] = 255; }
    }
    return OverlayBitmap::copy(AW, AH, AW * 4, bgra.data(), bgra.size(), 1, monotonic_us(), false);
}
ClipCheck check_clip(const std::filesystem::path& path, const RECT& window, const RECT& monitor, int out_w, int out_h) {
    ClipCheck result; AVFormatContext* input = nullptr; const auto name = path.string();
    check(avformat_open_input(&input, name.c_str(), nullptr, nullptr) >= 0, "Open clip"); check(avformat_find_stream_info(input, nullptr) >= 0, "Clip info");
    int stream = -1; for (unsigned i = 0; i < input->nb_streams; ++i) if (input->streams[i]->codecpar->codec_type == AVMEDIA_TYPE_VIDEO) stream = int(i);
    check(stream >= 0, "No video");
    auto* codec = avcodec_find_decoder(input->streams[stream]->codecpar->codec_id); auto* decoder = avcodec_alloc_context3(codec);
    avcodec_parameters_to_context(decoder, input->streams[stream]->codecpar); check(avcodec_open2(decoder, codec, nullptr) >= 0, "Open decoder");
    AVPacket* packet = av_packet_alloc(); AVFrame* frame = av_frame_alloc(); AVFrame* last = av_frame_alloc(); bool first = true;
    auto drain = [&] { while (avcodec_receive_frame(decoder, frame) == 0) { ++result.frames; av_frame_unref(last); av_frame_move_ref(last, frame); } };
    while (av_read_frame(input, packet) >= 0) {
        if (packet->stream_index == stream) { if (first) { result.keyframe = packet->flags & AV_PKT_FLAG_KEY; first = false; } check(avcodec_send_packet(decoder, packet) >= 0, "Decode"); drain(); }
        av_packet_unref(packet);
    }
    avcodec_send_packet(decoder, nullptr); drain();
    check(last->format == AV_PIX_FMT_YUV420P || last->format == AV_PIX_FMT_NV12 || last->format == AV_PIX_FMT_YUVJ420P, "Decoded format");
    const auto fit = capture_aspect_fit(monitor.right - monitor.left, monitor.bottom - monitor.top, out_w, out_h);
    const double sx = double(fit.width) / (monitor.right - monitor.left), sy = double(fit.height) / (monitor.bottom - monitor.top);
    auto luma = [&](int px, int py, int pw, int ph) { // Mean luma over the pattern rectangle's inner half.
        const int x0 = fit.x + int((window.left - monitor.left + px + pw / 4) * sx), x1 = fit.x + int((window.left - monitor.left + px + pw * 3 / 4) * sx);
        const int y0 = fit.y + int((window.top - monitor.top + py + ph / 4) * sy), y1 = fit.y + int((window.top - monitor.top + py + ph * 3 / 4) * sy);
        double s = 0; int n = 0; for (int y = y0; y < y1; ++y) for (int x = x0; x < x1; ++x) { s += last->data[0][size_t(y) * last->linesize[0] + x]; ++n; } return s / std::max(n, 1);
    };
    auto expected = [](int r, int g, int b) { return 16 + 219 * (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255; };
    double sum = 0; int n = 0;
    for (int i = 0; i < 16; ++i) { const double got = luma(i * 32, 0, 32, 48); result.steps.push_back(got); const double d = std::abs(got - expected(i * 17, i * 17, i * 17));
        result.luma_max_error = std::max(result.luma_max_error, d); sum += d; ++n; }
    const int primaries[8][3]{{1, 0, 0}, {0, 1, 0}, {0, 0, 1}, {0, 1, 1}, {1, 0, 1}, {1, 1, 0}, {1, 1, 1}, {0, 0, 0}};
    for (int i = 0; i < 8; ++i) { const double d = std::abs(luma(i * 64, 48, 64, 48) - expected(primaries[i][0] * 255, primaries[i][1] * 255, primaries[i][2] * 255));
        result.luma_max_error = std::max(result.luma_max_error, d); sum += d; ++n; }
    result.luma_mean_error = sum / n;
    // Overlay patches, in output pixels.
    const double scale = AWIDTH * out_w / AW;
    for (int patch = 0; patch < 3; ++patch) {
        const int x0 = int(AX * out_w + (patch * 100 + 25) * scale), x1 = int(AX * out_w + (patch * 100 + 75) * scale), y0 = int(AY * out_h + 25 * scale), y1 = int(AY * out_h + 75 * scale);
        double s = 0; int count = 0; for (int y = y0; y < y1; ++y) for (int x = x0; x < x1; ++x) { s += last->data[0][size_t(y) * last->linesize[0] + x]; ++count; }
        result.overlay.push_back(s / std::max(count, 1));
    }
    av_frame_free(&frame); av_frame_free(&last); av_packet_free(&packet); avcodec_free_context(&decoder); avformat_close_input(&input);
    return result;
}
void transition_mode(const Target& t, const std::filesystem::path& directory, const std::filesystem::path& ffmpeg, bool dxgi, bool overlay) {
    Restore restore(t);
    if (hdr(colour(t))) set_hdr(t, false);
    std::filesystem::create_directories(directory);
    Pattern pattern(t.bounds.left + 96, t.bounds.top + 96);
    RecorderSessionConfig config; config.capture = capture_config(t, dxgi); config.history_seconds = 40;
    config.work_directory = directory / L"work"; config.ffmpeg = ffmpeg; config.capture_input = false;
    const double vram0 = process_vram_mb(), private0 = private_mb();
    if (overlay) { config.overlays.burned = true; config.overlays.keyboard_layout = "Compact"; config.overlays.keyboard_transform = {AX, AY, AWIDTH}; }
    RecorderSession recorder(config); recorder.start();
    if (overlay) check(recorder.artwork(artwork()), "Artwork refused");
    recorder.detector_regions({CaptureNormalizedRect{double(pattern.rect().left - t.bounds.left) / (t.bounds.right - t.bounds.left), double(pattern.rect().top - t.bounds.top) / (t.bounds.bottom - t.bounds.top),
                               double(PW) / (t.bounds.right - t.bounds.left), 48.0 / (t.bounds.bottom - t.bounds.top)}, CaptureNormalizedRect{}, CaptureNormalizedRect{}}, true, false);
    auto phase = [&](const std::string& name, int seconds) {
        std::vector<double> output, fresh; RecordingCaptureHealth h;
        for (int s = 0; s < seconds; ++s) { pattern.settle(1s); h = recorder.health(); check(h.error.empty(), "Capture failed: " + h.error); if (s >= 2) { output.push_back(h.output_fps); fresh.push_back(h.unique_fps); } }
        const auto& d = h.source_details; double o = 0, f = 0; for (double v : output) o += v; for (double v : fresh) f += v;
        std::cout << "  " << name << ": source=" << h.source << " hdrDisplay=" << d.hdr_display << " fp16=" << d.hdr_conversion << " white=" << d.sdr_white_nits
                  << " output=" << o / std::max<size_t>(output.size(), 1) << " fresh=" << f / std::max<size_t>(fresh.size(), 1) << " recoveries=" << h.source_recoveries
                  << " switchRetries=" << d.profile_switch_failures << " reopens=" << d.duplication_reopens
                  << (h.source_recovery_error.empty() ? "" : " (last: " + h.source_recovery_error + ")") << " encoder=" << h.encoder << " backpressure=" << h.backpressure_drops
                  << " pool " << d.owned_textures_leased << "/" << d.owned_texture_capacity << " pressure " << d.owned_texture_pressure_drops
                  << " detector samples=" << h.detector_samples << " fallbacks=" << h.detector_fallbacks << " allocAfterWarmup=" << h.detector_allocations_after_warmup
                  << " | vram +" << process_vram_mb() - vram0 << " MB private +" << private_mb() - private0 << " MB\n";
        if (overlay) std::cout << "    overlay: state=" << h.overlay.state << " path=" << h.overlay.path << " keyboardFrames=" << h.overlay.keyboard_frames << " gpuUploads=" << h.overlay.gpu_uploads
                               << " cpuRoundtrips=" << h.overlay.cpu_roundtrips << " gpu p95=" << h.overlay.gpu_p95_ms << " ms skipped=" << h.overlay.skipped_frames << "\n";
        const auto snapshot = recorder.detector_snapshot();
        return snapshot ? snapshot->regions[0].pixels : std::vector<uint8_t>{};
    };
    auto save = [&](const std::string& name, int seconds) {
        const auto end = monotonic_us() - 200000, start = end - int64_t(seconds) * 1000000; const auto path = directory / (name + ".mp4"); std::filesystem::remove(path);
        recorder.save(name, start, end, path); auto saved = recorder.saved(name); check(saved.has_value(), "Save not accepted");
        check(saved->completion.wait_for(120s) == std::future_status::ready, "Save timed out");
        const auto result = saved->completion.get(); check(result.error.empty(), "Save failed: " + result.error);
        const auto clip = check_clip(path, pattern.rect(), t.bounds, config.capture.width, config.capture.height);
        std::cout << "    clip " << name << " (" << seconds << " s): " << clip.frames << " frames, keyframe first " << clip.keyframe << ", pattern luma vs DIB max "
                  << clip.luma_max_error << " mean " << clip.luma_mean_error << "; grey steps";
        for (double v : clip.steps) std::cout << " " << std::lround(v);
        if (overlay) { std::cout << "; overlay patches"; for (double v : clip.overlay) std::cout << " " << v; }
        std::cout << "\n";
        return clip;
    };
    std::cout << "SDR: " << describe(colour(t)) << "\n";
    const auto sdr_detector = phase("SDR", 8); const auto a = save("1-sdr", 4);
    set_hdr(t, true); std::cout << "HDR on: " << describe(colour(t)) << "\n";
    const auto hdr_detector = phase("HDR", 8); const auto b = save("2-hdr", 4);
    set_hdr(t, false); std::cout << "HDR off: " << describe(colour(t)) << "\n";
    phase("SDR again", 8); const auto c = save("3-sdr", 4);
    const auto span = save("4-across", 30);
    double diff = 0; size_t n = std::min(sdr_detector.size(), hdr_detector.size());
    for (size_t i = 0; i < n; ++i) diff += std::abs(int(sdr_detector[i]) - int(hdr_detector[i]));
    std::cout << "  detector region SDR vs HDR: " << n << " bytes, mean difference " << (n ? diff / double(n) : -1) << "\n";
    double between = 0; for (size_t i = 0; i < a.steps.size(); ++i) between = std::max({between, std::abs(a.steps[i] - b.steps[i]), std::abs(c.steps[i] - b.steps[i])});
    std::cout << "  grey-step luma, SDR clips vs HDR clip: max difference " << between << "\n";
    if (overlay) { double o = 0; for (size_t i = 0; i < a.overlay.size(); ++i) o = std::max({o, std::abs(a.overlay[i] - b.overlay[i]), std::abs(c.overlay[i] - b.overlay[i])});
        // Red 255 over anything: Y 16+219*.2126 = 62.6; grey 128: 16+219*128/255 = 125.9.
        std::cout << "  burned overlay luma, SDR clips vs HDR clip: max difference " << o << " (expected red 62.6, grey 125.9)\n"; }
    check(span.frames > 0 && a.keyframe && b.keyframe && c.keyframe && span.keyframe, "Clip without a leading keyframe");
    check(recorder.stop(), "Recorder stop");
}
// HDR switched on and off `count` times under an armed replay: capture
// follows every change without failing, and GPU and process memory return to
// where they were.
void toggle_mode(const Target& t, int count, bool dxgi, const std::filesystem::path& directory, const std::filesystem::path& ffmpeg) {
    Restore restore(t);
    Pattern pattern(t.bounds.left + 96, t.bounds.top + 96);
    RecorderSessionConfig config; config.capture = capture_config(t, dxgi); config.history_seconds = 10;
    config.work_directory = directory / L"work"; config.ffmpeg = ffmpeg; config.capture_input = false;
    RecorderSession recorder(config); recorder.start(); pattern.settle(12s); // History full.
    const double vram0 = process_vram_mb(), private0 = private_mb(); const bool start = hdr(colour(t));
    for (int i = 0; i < count; ++i) {
        set_hdr(t, !hdr(colour(t))); pattern.settle(3s);
        const auto h = recorder.health(); check(h.error.empty(), "Capture failed: " + h.error); const auto& d = h.source_details;
        std::cout << "  toggle " << i + 1 << " -> " << (hdr(colour(t)) ? "HDR" : "SDR") << ": source=" << h.source << " fp16=" << d.hdr_conversion << " output=" << h.output_fps << " fresh=" << h.unique_fps
                  << " recoveries=" << h.source_recoveries << " switchRetries=" << d.profile_switch_failures << " reopens=" << d.duplication_reopens << " reopenFailures=" << d.duplication_reopen_failures
                  << (h.source_recovery_error.empty() ? "" : " (last recovery: " + h.source_recovery_error + ")") << " pool " << d.owned_textures_leased << "/" << d.owned_texture_capacity
                  << " pressure " << d.owned_texture_pressure_drops << " | vram " << std::showpos << process_vram_mb() - vram0 << " MB private " << private_mb() - private0 << std::noshowpos << " MB\n";
    }
    if (hdr(colour(t)) != start) set_hdr(t, start);
    pattern.settle(4s);
    std::cout << "  back to the start state: vram " << std::showpos << process_vram_mb() - vram0 << " MB private " << private_mb() - private0 << std::noshowpos << " MB\n";
    check(recorder.stop(), "Recorder stop");
}
// The displays powered off (SC_MONITORPOWER) for 10 s and back on under an
// armed replay, as the app records it: capture pauses or idles without
// failing, resumes, and a clip across the gap saves and decodes.
void suspend_mode(const Target& t, const std::filesystem::path& directory, const std::filesystem::path& ffmpeg) {
    Pattern pattern(t.bounds.left + 96, t.bounds.top + 96);
    RecorderSessionConfig config; config.capture = capture_config(t, false); config.history_seconds = 40;
    config.work_directory = directory / L"work"; config.ffmpeg = ffmpeg; config.capture_input = false;
    RecorderSession recorder(config); recorder.start();
    auto log = [&](const std::string& label) {
        const auto h = recorder.health(); check(h.error.empty(), "Capture failed: " + h.error);
        std::cout << "  " << label << ": source=" << h.source << " fp16=" << h.source_details.hdr_conversion << " output=" << h.output_fps << " fresh=" << h.unique_fps << " paused=" << h.paused
                  << " recoveries=" << h.source_recoveries << (h.source_recovery_error.empty() ? "" : " (last: " + h.source_recovery_error + ")") << " backpressure=" << h.backpressure_drops << "\n";
    };
    for (int i = 0; i < 6; ++i) { pattern.settle(1s); if (i >= 4) log("before"); }
    const auto off_at = monotonic_us();
    DefWindowProcW(pattern.window(), WM_SYSCOMMAND, SC_MONITORPOWER, 2);
    for (int i = 0; i < 10; ++i) { pattern.settle(1s); log("displays off " + std::to_string(i + 1) + " s"); }
    DefWindowProcW(pattern.window(), WM_SYSCOMMAND, SC_MONITORPOWER, -1); SetThreadExecutionState(ES_DISPLAY_REQUIRED);
    for (int i = 0; i < 8; ++i) { pattern.settle(1s); log("displays on " + std::to_string(i + 1) + " s"); }
    const auto end = monotonic_us() - 200000, start = off_at - 5000000; const auto path = directory / "suspend.mp4"; std::filesystem::remove(path);
    recorder.save("suspend", start, end, path); auto saved = recorder.saved("suspend"); check(saved.has_value(), "Save not accepted");
    check(saved->completion.wait_for(120s) == std::future_status::ready, "Save timed out");
    const auto result = saved->completion.get(); check(result.error.empty(), "Save failed: " + result.error);
    const auto clip = check_clip(path, pattern.rect(), t.bounds, config.capture.width, config.capture.height);
    std::cout << "  clip across the gap (" << double(end - start) / 1e6 << " s): " << clip.frames << " frames, keyframe first " << clip.keyframe << ", duration "
              << double(result.duration_us) / 1e6 << " s, pattern luma vs DIB max " << clip.luma_max_error << "\n";
    check(clip.keyframe && clip.frames > 0, "Clip across the gap is broken");
    check(recorder.stop(), "Recorder stop");
}
void move_mode(const Target& from, const Target& to) {
    Restore r1(from), r2(to);
    if (!hdr(colour(from))) set_hdr(from, true);
    Pattern pattern(from.bounds.left + 96, from.bounds.top + 96);
    RecordingCaptureConfig c = capture_config(from, false); c.monitor = 0; c.window = reinterpret_cast<uintptr_t>(pattern.window());
    auto source = create_windows_recording_source(c);
    const RECT none{};
    auto window_origin = [&] { return pattern.rect(); };
    for (int hop = 0; hop < 4; ++hop) {
        const auto& at = hop % 2 ? to : from;
        if (hop) { pattern.move(at.bounds.left + 96, at.bounds.top + 96); const auto started = std::chrono::steady_clock::now(); source->set_frame_rate(60);
                   std::cout << "  moved to " << narrow(at.gdi) << " (" << describe(colour(at)) << "), reprofiled in "
                             << std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count() << " ms\n"; }
        describe_source(*source);
        const auto got = grab(*source, pattern, window_origin()); check(got.has_value(), "No window frame");
        report("window on " + narrow(at.gdi), compare(*got, pattern.expected));
        check(source->diagnostics().hdr_display == hdr(colour(at)), "Source HDR profile does not follow the window's display");
    }
    (void)none; source->stop();
}
// Raw FP16 rows through the middle of each band, as DXGI duplicates them.
void fixture_mode(const Target& t, const std::filesystem::path& file) {
    Restore restore(t);
    if (!hdr(colour(t))) set_hdr(t, true);
    Pattern pattern(t.bounds.left + 96, t.bounds.top + 96);
    ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
    check(SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context)), "Device");
    ComPtr<IDXGIDevice> dxgi; device.As(&dxgi); ComPtr<IDXGIAdapter> adapter; dxgi->GetAdapter(&adapter); ComPtr<IDXGIOutputDuplication> duplication;
    for (UINT i = 0; !duplication; ++i) {
        ComPtr<IDXGIOutput> output; check(adapter->EnumOutputs(i, &output) != DXGI_ERROR_NOT_FOUND, "Output"); DXGI_OUTPUT_DESC od{}; output->GetDesc(&od);
        if (od.Monitor != t.monitor) continue; ComPtr<IDXGIOutput5> output5; check(SUCCEEDED(output.As(&output5)), "Output5");
        // The recorder's format list.
        const DXGI_FORMAT formats[]{DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_B8G8R8A8_UNORM}; check(SUCCEEDED(output5->DuplicateOutput1(device.Get(), 0, 2, formats, &duplication)), "DuplicateOutput1");
    }
    std::vector<uint16_t> rows; const auto r = pattern.rect();
    for (int attempt = 0; attempt < 100 && rows.empty(); ++attempt) {
        pattern.settle(1ms); DXGI_OUTDUPL_FRAME_INFO info{}; ComPtr<IDXGIResource> resource;
        if (duplication->AcquireNextFrame(50, &info, &resource) != S_OK) continue;
        ComPtr<ID3D11Texture2D> frame; resource.As(&frame); D3D11_TEXTURE2D_DESC desc{}; frame->GetDesc(&desc); check(desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT, "Duplication format " + std::to_string(int(desc.Format)) + ", not FP16");
        auto staging = desc; staging.Usage = D3D11_USAGE_STAGING; staging.BindFlags = 0; staging.CPUAccessFlags = D3D11_CPU_ACCESS_READ; staging.MiscFlags = 0;
        ComPtr<ID3D11Texture2D> copy; device->CreateTexture2D(&staging, nullptr, &copy); context->CopyResource(copy.Get(), frame.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{}; context->Map(copy.Get(), 0, D3D11_MAP_READ, 0, &mapped);
        for (const auto& band : bands) { const int y = r.top - t.bounds.top + (band.top + band.bottom) / 2;
            const auto* row = reinterpret_cast<const uint16_t*>(static_cast<const uint8_t*>(mapped.pData) + size_t(y) * mapped.RowPitch) + size_t(r.left - t.bounds.left) * 4;
            rows.insert(rows.end(), row, row + size_t(PW) * 4); }
        context->Unmap(copy.Get(), 0); duplication->ReleaseFrame();
    }
    check(!rows.empty(), "No FP16 frame");
    std::vector<uint8_t> expected; for (const auto& band : bands) { const int y = (band.top + band.bottom) / 2; expected.insert(expected.end(), pattern.expected.begin() + ptrdiff_t(size_t(y) * PW * 4), pattern.expected.begin() + ptrdiff_t(size_t(y + 1) * PW * 4)); }
    std::ofstream out(file, std::ios::binary); const float white = colour(t).white; const uint32_t header[4]{0x36315046u /* FP16 */, uint32_t(PW), uint32_t(std::size(bands)), 0};
    out.write(reinterpret_cast<const char*>(header), sizeof(header)); out.write(reinterpret_cast<const char*>(&white), 4);
    out.write(reinterpret_cast<const char*>(rows.data()), std::streamsize(rows.size() * 2)); out.write(reinterpret_cast<const char*>(expected.data()), std::streamsize(expected.size()));
    std::cout << "fixture: " << std::size(bands) << " rows of " << PW << " FP16 pixels at SDR white " << white << " nits, with the DIB rows, in " << file.string() << "\n";
}
}

int main(int argc, char** argv) {
    try {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        av_log_set_level(AV_LOG_ERROR); std::cout << std::unitbuf << std::fixed << std::setprecision(2);
        const std::string mode = argc > 1 ? argv[1] : "state";
        if (mode == "state") { for (const char* name : {"DISPLAY1", "DISPLAY2", "DISPLAY3"}) try { const auto t = target(name); std::cout << name << ": " << describe(colour(t)) << (colour(t).supported ? "" : " (HDR unsupported)") << "\n"; } catch (...) {} return 0; }
        if (mode == "pattern" && argc > 3) { pattern_mode(target(argv[2]), argv[3]); return 0; }
        if (mode == "white" && argc > 3) { std::vector<float> levels; std::stringstream s(argv[3]); std::string part; while (std::getline(s, part, ',')) levels.push_back(std::stof(part)); white_mode(target(argv[2]), levels); return 0; }
        if (mode == "transition" && argc > 4) {
            bool dxgi = false, overlay = false; for (int i = 5; i < argc; ++i) { dxgi |= std::string(argv[i]) == "dxgi"; overlay |= std::string(argv[i]) == "overlay"; }
            transition_mode(target(argv[2]), argv[3], argv[4], dxgi, overlay); return 0;
        }
        if (mode == "move" && argc > 3) { move_mode(target(argv[2]), target(argv[3])); return 0; }
        if (mode == "toggle" && argc > 5) { toggle_mode(target(argv[2]), std::atoi(argv[3]), argc > 6 && std::string(argv[6]) == "dxgi", argv[4], argv[5]); return 0; }
        if (mode == "fixture" && argc > 3) { fixture_mode(target(argv[2]), argv[3]); return 0; }
        if (mode == "suspend" && argc > 4) { suspend_mode(target(argv[2]), argv[3], argv[4]); return 0; }
        std::cerr << "hdr_validation state | pattern <DISPLAYn> <on|off|keep> | white <DISPLAYn> <nits,...> | transition <DISPLAYn> <dir> <ffmpeg> | move <HDR DISPLAYn> <SDR DISPLAYn> | fixture <DISPLAYn> <file>\n";
        return 2;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
