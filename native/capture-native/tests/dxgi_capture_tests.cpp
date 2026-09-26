// Desktop Duplication fallback: GPU cursor composition against the GDI path it
// replaces (byte for byte), the pooled owned-frame copies, and HDR
// tone-mapping through the pool against the per-frame path. WARP by default;
// --gpu adds the hardware device and real duplication frames.
#include "captured_frames.h"
#include "cursor_compositor.h"
#include "recording_capture.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <algorithm>
#include <functional>
#include <cstring>
#include <deque>
#include <iostream>
#include <random>
#include <string>
#include <thread>
#include <vector>

using namespace clypdat;
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
ComPtr<ID3D11Texture2D> texture(const Device& d, int width, int height, DXGI_FORMAT format, const void* data, UINT pitch) {
    D3D11_TEXTURE2D_DESC desc{}; desc.Width = UINT(width); desc.Height = UINT(height); desc.MipLevels = 1; desc.ArraySize = 1; desc.Format = format;
    desc.SampleDesc.Count = 1; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    const D3D11_SUBRESOURCE_DATA initial{data, pitch, 0};
    ComPtr<ID3D11Texture2D> result; CHECK(SUCCEEDED(d.device->CreateTexture2D(&desc, data ? &initial : nullptr, &result))); return result;
}
std::vector<uint8_t> read(ID3D11Texture2D* source) {
    source->AddRef(); CapturePixels pixels; pixels.texture = std::shared_ptr<ID3D11Texture2D>(source, [](auto* p) { p->Release(); });
    D3D11_TEXTURE2D_DESC desc{}; source->GetDesc(&desc); pixels.width = int(desc.Width); pixels.height = int(desc.Height); pixels.stride = pixels.width * 4;
    capture_copy_texture_pixels(pixels); return pixels.bgra;
}
std::vector<uint8_t> random_bgra(int width, int height, uint32_t seed, bool opaque) {
    std::mt19937 random(seed); std::vector<uint8_t> bytes(size_t(width) * height * 4);
    for (size_t i = 0; i < bytes.size(); ++i) bytes[i] = uint8_t(random());
    if (opaque) for (size_t i = 3; i < bytes.size(); i += 4) bytes[i] = 255;
    return bytes;
}
HBITMAP dib(int width, int height, int bits, const void* pixels) {
    BITMAPINFO info{}; info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER); info.bmiHeader.biWidth = width; info.bmiHeader.biHeight = -height;
    info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = WORD(bits); info.bmiHeader.biCompression = BI_RGB;
    void* data = nullptr; HDC screen = GetDC(nullptr); HBITMAP bitmap = CreateDIBSection(screen, &info, DIB_RGB_COLORS, &data, nullptr, 0); ReleaseDC(nullptr, screen);
    CHECK(bitmap); const int stride = ((width * bits + 31) / 32) * 4; std::memcpy(data, pixels, size_t(stride) * height); return bitmap;
}
// Synthetic cursors of every kind DrawIconEx distinguishes.
struct TestCursor { std::string name; HCURSOR handle; bool gpu; };
std::vector<TestCursor> make_cursors() {
    std::vector<TestCursor> cursors; std::mt19937 random(9);
    auto mask_bits = [&](int width, int height, int density) {
        std::vector<uint8_t> bits(size_t((width + 15) / 16 * 2) * height); for (auto& b : bits) b = uint8_t(random() % 100 < density ? random() : 0); return bits;
    };
    auto colour = [&](const std::string& name, int width, int height, auto alpha, bool gpu, int bits = 32) {
        std::vector<uint8_t> pixels(size_t(width) * height * 4);
        for (int i = 0; i < width * height; ++i) { pixels[i * 4] = uint8_t(random()); pixels[i * 4 + 1] = uint8_t(random()); pixels[i * 4 + 2] = uint8_t(random()); pixels[i * 4 + 3] = uint8_t(alpha(i)); }
        HBITMAP color = nullptr;
        if (bits == 24) {
            const int stride = ((width * 24 + 31) / 32) * 4; std::vector<uint8_t> packed(size_t(stride) * height);
            for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) std::memcpy(&packed[size_t(y) * stride + x * 3], &pixels[(size_t(y) * width + x) * 4], 3);
            color = dib(width, height, 24, packed.data());
        } else color = dib(width, height, 32, pixels.data());
        const auto mask = mask_bits(width, height, 40); HBITMAP m = CreateBitmap(width, height, 1, 1, mask.data());
        ICONINFO icon{FALSE, DWORD(width / 3), DWORD(height / 4), m, color};
        cursors.push_back({name, CreateIconIndirect(&icon), gpu}); DeleteObject(color); DeleteObject(m);
    };
    colour("alpha 32x32", 32, 32, [&](int) { const int r = int(random() % 4); return r == 0 ? 0 : r == 1 ? 255 : int(random() % 256); }, true);
    colour("opaque 48x48", 48, 48, [](int) { return 255; }, true);
    colour("alpha 37x23", 37, 23, [&](int i) { return (i * 7) % 256; }, true);
    colour("masked colour 32x32", 32, 32, [](int) { return 0; }, true);
    colour("masked colour from 24bpp (stored 32bpp)", 32, 32, [](int) { return 0; }, true, 24);
    colour("alpha 300x300", 300, 300, [&](int) { return int(random() % 256); }, false);
    {
        const auto both = mask_bits(32, 64, 60); HBITMAP m = CreateBitmap(32, 64, 1, 1, both.data());
        ICONINFO icon{FALSE, 5, 7, m, nullptr}; cursors.push_back({"monochrome 32x32", CreateIconIndirect(&icon), true}); DeleteObject(m);
    }
    for (const auto& c : cursors) CHECK(c.handle);
    return cursors;
}

// Every cursor kind, inside and across every edge of a frame and of a crop
// origin: the GPU path equals the GDI path in every BGRA byte.
void cursor_matches_gdi(const Device& d) {
    const int width = 320, height = 200;
    const auto cursors = make_cursors();
    CursorCompositor compositor(d.device.Get());
    int cases = 0;
    for (const bool opaque : {true, false}) {
        const auto base = random_bgra(width, height, opaque ? 1 : 2, opaque);
        for (const auto& cursor : cursors) {
            const POINT positions[] = {{100, 80}, {0, 0}, {-10, 50}, {50, -12}, {width - 5, 60}, {90, height - 3}, {-20, -20}, {width + 2, height + 2},
                {width - 1, height - 1}, {-500, 40}, {160, 100}};
            for (const auto origin : {POINT{0, 0}, POINT{1000, -300}}) for (const auto position : positions) {
                const CursorState state{cursor.handle, {position.x + origin.x, position.y + origin.y}, true};
                auto cpu = texture(d, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, base.data(), width * 4), gpu = texture(d, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, base.data(), width * 4);
                const bool drawn = cursor_draw_cpu(cpu.Get(), width, height, origin.x, origin.y, state);
                const auto result = compositor.draw(gpu.Get(), width, height, origin.x, origin.y, state);
                const auto wanted = drawn ? (cursor.gpu ? CursorCompositor::Result::Gpu : CursorCompositor::Result::Cpu) : CursorCompositor::Result::Outside;
                if (result != wanted) throw std::runtime_error("Cursor " + cursor.name + " at " + std::to_string(position.x) + "," + std::to_string(position.y) +
                    ": path " + std::to_string(int(result)) + ", expected " + std::to_string(int(wanted)));
                const auto expected = read(cpu.Get()), actual = read(gpu.Get());
                if (expected != actual) {
                    size_t differing = 0; int max = 0;
                    for (size_t i = 0; i < expected.size(); ++i) if (expected[i] != actual[i]) { ++differing; max = std::max(max, std::abs(int(expected[i]) - int(actual[i]))); }
                    throw std::runtime_error("Cursor " + cursor.name + " at " + std::to_string(position.x) + "," + std::to_string(position.y) +
                        (opaque ? " (opaque frame)" : " (random alpha)") + ": " + std::to_string(differing) + " bytes differ, max " + std::to_string(max));
                }
                ++cases;
            }
        }
    }
    const auto stats = compositor.stats();
    CHECK(stats.readback_bytes > 0); // Only the fallback shapes read back.
    std::cout << "cursor: " << cases << " cases byte-identical to DrawIconEx; GPU draws " << stats.gpu_draws << ", CPU fallbacks " << stats.cpu_fallbacks
              << ", shapes uploaded " << stats.uploads << ", cursor textures " << stats.textures_created << "\n";
    // Hidden cursors draw nothing.
    auto frame = texture(d, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, random_bgra(width, height, 3, true).data(), width * 4);
    const auto before = read(frame.Get());
    CHECK(compositor.draw(frame.Get(), width, height, 0, 0, {cursors[0].handle, {50, 50}, false}) == CursorCompositor::Result::Hidden);
    CHECK(read(frame.Get()) == before);
    for (const auto& c : cursors) DestroyCursor(c.handle);
}
// A moving cursor re-uploads nothing; switching among a few cursors uploads
// each once; the handle is re-read every two seconds without re-uploading.
void cursor_caching(const Device& d) {
    const auto cursors = make_cursors();
    CursorCompositor compositor(d.device.Get());
    auto frame = texture(d, 640, 360, DXGI_FORMAT_B8G8R8A8_UNORM, random_bgra(640, 360, 4, true).data(), 640 * 4);
    for (int i = 0; i < 300; ++i) CHECK(compositor.draw(frame.Get(), 640, 360, 0, 0, {cursors[0].handle, {i * 2, i}, true}) == CursorCompositor::Result::Gpu);
    auto stats = compositor.stats();
    CHECK(stats.uploads == 1 && stats.shape_changes == 1 && stats.gpu_draws == 300 && stats.textures_created == 3 && stats.readback_bytes == 0);
    for (int i = 0; i < 300; ++i) {
        const auto& cursor = cursors[std::vector<int>{0, 2, 3, 6}[i % 4]];
        CHECK(compositor.draw(frame.Get(), 640, 360, 0, 0, {cursor.handle, {100 + i % 50, 100}, true}) == CursorCompositor::Result::Gpu);
    }
    stats = compositor.stats();
    CHECK(stats.uploads == 4 && stats.readback_bytes == 0); // Cached per handle.
    std::this_thread::sleep_for(std::chrono::milliseconds(2100));
    CHECK(compositor.draw(frame.Get(), 640, 360, 0, 0, {cursors[0].handle, {10, 10}, true}) == CursorCompositor::Result::Gpu);
    stats = compositor.stats(); CHECK(stats.revalidations == 1 && stats.uploads == 4);
    std::cout << "cursor cache: 300 moving draws, 1 upload; 4 alternating shapes, " << stats.uploads << " uploads; textures " << stats.textures_created << "\n";
    for (const auto& c : cursors) DestroyCursor(c.handle);
}

// The pooled copies: bounded, reused, rebuilt on a crop size change, never
// handing out an old-size texture, dropping (not allocating) under pressure.
void pooled_copies(const Device& d) {
    const int width = 640, height = 360, capacity = capture_source_texture_capacity(2);
    const auto pixels = random_bgra(width, height, 5, true);
    auto input = texture(d, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, pixels.data(), width * 4);
    CapturedFrameStore store(d.device.Get(), capacity, 80);
    // Warm-up, then steady copies allocate nothing.
    for (int i = 0; i < 20; ++i) CHECK(store.copy(input.Get()));
    const auto warm = store.stats();
    for (int i = 0; i < 1000; ++i) { auto frame = store.copy(input.Get(), {i % 7, i % 5, 320, 180}); CHECK(frame); }
    auto stats = store.stats();
    CHECK(stats.allocated == warm.allocated + 1); // One rebuild for the crop size, then reuse.
    // Window crops move without a rebuild; contents are the crop.
    const CaptureRect crop{17, 11, 320, 180};
    const auto copied = read(store.copy(input.Get(), crop).get());
    for (int y = 0; y < crop.height; ++y) CHECK(!std::memcmp(&copied[size_t(y) * crop.width * 4], &pixels[(size_t(crop.y + y) * width + crop.x) * 4], size_t(crop.width) * 4));
    CHECK(store.stats().allocated == stats.allocated);
    // Pressure: every pooled texture held downstream drops the next frame.
    std::vector<std::shared_ptr<ID3D11Texture2D>> held;
    for (int i = 0; i < capacity; ++i) { held.push_back(store.copy(input.Get(), crop)); CHECK(held.back()); }
    CHECK(!store.copy(input.Get(), crop));
    stats = store.stats(); CHECK(stats.pressure_drops == 1 && stats.leased == capacity && stats.allocated <= warm.allocated + 1 + uint64_t(capacity));
    held.pop_back(); CHECK(store.copy(input.Get(), crop)); // A released texture is reused, not reallocated.
    CHECK(store.stats().allocated == stats.allocated);
    // A size change: old-size textures still held are discarded on return.
    auto old = std::move(held); held.clear();
    auto resized = store.copy(input.Get(), {0, 0, 200, 100}); CHECK(resized);
    D3D11_TEXTURE2D_DESC desc{}; resized->GetDesc(&desc); CHECK(desc.Width == 200 && desc.Height == 100);
    old.clear(); resized.reset();
    for (int i = 0; i < 50; ++i) { auto frame = store.copy(input.Get(), {0, 0, 200, 100}); frame->GetDesc(&desc); CHECK(desc.Width == 200 && desc.Height == 100); }
    CHECK(!store.copy(input.Get(), {600, 0, 200, 100})); // Outside the frame.
    stats = store.stats(); CHECK(stats.leased == 0 && stats.peak_leased <= capacity);
    std::cout << "pool: capacity " << capacity << ", allocated " << stats.allocated << " over " << stats.delivered << " copies, pressure drops " << stats.pressure_drops << "\n";
}

// FP16 frames: the pool's crop-then-tone-map result equals the per-frame
// path's FP16 crop copy tone-mapped on its own.
void hdr_matches_per_frame_path(const Device& d) {
    const int width = 320, height = 200;
    std::vector<uint16_t> half(size_t(width) * height * 4); std::mt19937 random(6);
    auto to_half = [](float value) { uint32_t bits; std::memcpy(&bits, &value, 4); const uint32_t sign = (bits >> 16) & 0x8000; int exponent = int((bits >> 23) & 0xFF) - 112;
        uint32_t mantissa = bits & 0x7FFFFF; if (exponent <= 0) return uint16_t(sign); if (exponent >= 31) return uint16_t(sign | 0x7BFF); return uint16_t(sign | (exponent << 10) | (mantissa >> 13)); };
    for (size_t i = 0; i < half.size(); ++i) half[i] = to_half(std::uniform_real_distribution<float>(-.2f, 12.5f)(random));
    auto input = texture(d, width, height, DXGI_FORMAT_R16G16B16A16_FLOAT, half.data(), width * 8);
    for (const float white : {80.f, 203.f, 480.f}) {
        CapturedFrameStore store(d.device.Get(), 4, white);
        for (const CaptureRect crop : {CaptureRect{0, 0, width, height}, CaptureRect{13, 7, 200, 150}, CaptureRect{width - 64, height - 32, 64, 32}}) {
            // Per-frame path: crop into an FP16 texture, tone-map that.
            auto cropped = texture(d, crop.width, crop.height, DXGI_FORMAT_R16G16B16A16_FLOAT, nullptr, 0);
            const D3D11_BOX box{UINT(crop.x), UINT(crop.y), 0, UINT(crop.x + crop.width), UINT(crop.y + crop.height), 1};
            d.context->CopySubresourceRegion(cropped.Get(), 0, 0, 0, 0, input.Get(), 0, &box);
            const auto expected = read(capture_tone_map_texture(cropped.Get(), white).get());
            const auto actual = read(store.copy(input.Get(), crop).get());
            if (expected != actual) {
                size_t differing = 0; for (size_t i = 0; i < expected.size(); ++i) differing += expected[i] != actual[i];
                throw std::runtime_error("HDR crop differs at white " + std::to_string(white) + ": " + std::to_string(differing) + " bytes");
            }
        }
    }
    std::cout << "hdr: pooled crop tone-map identical to the per-frame FP16 path at 80/203/480 nits\n";
}

// Real duplication frames on the display under the cursor: the pooled copy
// plus GPU cursor equals the per-frame copy plus GDI cursor.
void real_duplication(const Device& d) {
    ComPtr<IDXGIDevice> dxgi; d.device.As(&dxgi); ComPtr<IDXGIAdapter> adapter; dxgi->GetAdapter(&adapter);
    // The display under the cursor, else the first unrotated one: the
    // recorder's crops are in desktop coordinates, which a rotated output's
    // duplication texture does not share.
    POINT pointer{}; GetCursorPos(&pointer); const auto monitor = MonitorFromPoint(pointer, MONITOR_DEFAULTTOPRIMARY);
    ComPtr<IDXGIOutputDuplication> duplication; DXGI_OUTPUT_DESC od{};
    for (const bool under_cursor : {true, false}) {
        for (UINT i = 0; !duplication; ++i) {
            ComPtr<IDXGIOutput> output; if (adapter->EnumOutputs(i, &output) == DXGI_ERROR_NOT_FOUND) break;
            output->GetDesc(&od); if (under_cursor && od.Monitor != monitor) continue;
            if (od.Rotation != DXGI_MODE_ROTATION_IDENTITY && od.Rotation != DXGI_MODE_ROTATION_UNSPECIFIED) continue;
            ComPtr<IDXGIOutput1> output1; output.As(&output1); CHECK(SUCCEEDED(output1->DuplicateOutput(d.device.Get(), &duplication)));
        }
        if (duplication) break;
    }
    if (!duplication) { std::cout << "real duplication: no unrotated display on this adapter, skipped\n"; return; }
    CapturedFrameStore store(d.device.Get(), capture_source_texture_capacity(2), 80);
    CursorCompositor compositor(d.device.Get());
    const int desktop_width = od.DesktopCoordinates.right - od.DesktopCoordinates.left, desktop_height = od.DesktopCoordinates.bottom - od.DesktopCoordinates.top;
    int frames = 0, drawn = 0;
    for (int attempt = 0; attempt < 400 && frames < 30; ++attempt) {
        DXGI_OUTDUPL_FRAME_INFO info{}; ComPtr<IDXGIResource> resource;
        const auto hr = duplication->AcquireNextFrame(20, &info, &resource);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) continue; CHECK(SUCCEEDED(hr));
        ComPtr<ID3D11Texture2D> frame; resource.As(&frame);
        D3D11_TEXTURE2D_DESC desc{}; frame->GetDesc(&desc);
        if (desc.Format == DXGI_FORMAT_B8G8R8A8_UNORM) {
            const auto state = capture_cursor_state();
            const int px = state.position.x - od.DesktopCoordinates.left, py = state.position.y - od.DesktopCoordinates.top;
            // Whole display, a window-like crop around the cursor, and a region at the display's corner.
            const CaptureRect crops[] = {{0, 0, desktop_width, desktop_height},
                {std::clamp(px - 200, 0, desktop_width - 400), std::clamp(py - 150, 0, desktop_height - 300), 400, 300}, {desktop_width - 256, desktop_height - 144, 256, 144}};
            for (const auto& crop : crops) {
                auto reference = texture(d, crop.width, crop.height, DXGI_FORMAT_B8G8R8A8_UNORM, nullptr, 0);
                const D3D11_BOX box{UINT(crop.x), UINT(crop.y), 0, UINT(crop.x + crop.width), UINT(crop.y + crop.height), 1};
                d.context->CopySubresourceRegion(reference.Get(), 0, 0, 0, 0, frame.Get(), 0, &box);
                const int ox = od.DesktopCoordinates.left + crop.x, oy = od.DesktopCoordinates.top + crop.y;
                const bool cpu = cursor_draw_cpu(reference.Get(), crop.width, crop.height, ox, oy, state);
                auto pooled = store.copy(frame.Get(), crop); CHECK(pooled);
                const auto result = compositor.draw(pooled.get(), crop.width, crop.height, ox, oy, state);
                drawn += cpu; CHECK(cpu == (result == CursorCompositor::Result::Gpu || result == CursorCompositor::Result::Cpu));
                CHECK(read(reference.Get()) == read(pooled.get()));
            }
            ++frames;
        }
        duplication->ReleaseFrame();
    }
    const auto stats = compositor.stats();
    std::cout << "real duplication: " << frames << " frames x 3 crops identical; cursor drawn in " << drawn << " (GPU " << stats.gpu_draws << ", CPU fallback "
              << stats.cpu_fallbacks << ", shapes " << stats.uploads << ")\n";
}
// Duplication recreation (recover(), as after DXGI_ERROR_ACCESS_LOST) and a
// whole new source (a display change or a window moving monitors), repeated:
// frames keep coming, the pool never grows past its capacity, and GPU memory
// does not creep.
void recreation() {
    RecordingCaptureConfig config; config.prefer_dxgi = true; config.capture_cursor = true;
    LARGE_INTEGER counter{}, frequency{}; QueryPerformanceCounter(&counter); QueryPerformanceFrequency(&frequency);
    config.qpc_anchor = counter.QuadPart; config.qpc_frequency = frequency.QuadPart; config.monotonic_anchor_us = 1;
    auto memory = [] {
        ComPtr<IDXGIFactory1> factory; ComPtr<IDXGIAdapter1> adapter; ComPtr<IDXGIAdapter3> adapter3; DXGI_QUERY_VIDEO_MEMORY_INFO local{};
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) && SUCCEEDED(factory->EnumAdapters1(0, &adapter)) && SUCCEEDED(adapter.As(&adapter3)))
            adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &local);
        return double(local.CurrentUsage) / 1048576;
    };
    const double baseline = memory();
    auto source = create_windows_recording_source(config);
    if (std::string(source->name()) != "DXGI Desktop Duplication") { std::cout << "recreation: Desktop Duplication unavailable, skipped\n"; return; }
    int frames = 0; double settled = 0; int recreated = 0;
    std::deque<CapturePixels> downstream; // What pacing and the encoder would hold.
    for (int round = 0; round < 40; ++round) {
        const auto end = std::chrono::steady_clock::now() + std::chrono::milliseconds(60);
        while (std::chrono::steady_clock::now() < end) {
            CapturePixels pixels;
            if (source->acquire(pixels, std::chrono::milliseconds(20))) { ++frames; downstream.push_back(std::move(pixels)); if (downstream.size() > 4) downstream.pop_front(); }
        }
        const auto health = source->diagnostics();
        CHECK(health.owned_textures_leased <= health.owned_texture_capacity && health.owned_texture_pressure_drops == 0);
        CHECK(health.owned_textures_allocated <= uint64_t(health.owned_texture_capacity) && health.cursor_textures_created <= 32);
        if (round % 10 == 9) { downstream.clear(); CHECK(source->switch_backend(false)); ++recreated; }
        else CHECK(source->recover());
        if (round == 5) settled = memory();
    }
    downstream.clear();
    const double live = memory() - settled;
    source.reset(); std::this_thread::sleep_for(std::chrono::milliseconds(200));
    const double growth = memory() - baseline;
    if (growth > 48) throw std::runtime_error("GPU memory grew " + std::to_string(growth) + " MB after repeated duplication recreation");
    CHECK(frames >= 40);
    std::cout << "recreation: 30 duplication recoveries and " << recreated << " new sources, " << frames << " frames; dedicated while live +" << live
              << " MB (pool warm-up), after release +" << growth << " MB\n";
}
// Manual: per-draw CPU cost of each cursor path on a 4K frame.
int cursor_bench(const Device& d) {
    const auto cursors = make_cursors();
    std::vector<HCURSOR> many; for (int i = 0; i < 12; ++i) many.push_back(cursors[i % 3 == 0 ? 0 : i % 3 == 1 ? 2 : 3].handle);
    std::vector<HCURSOR> distinct; for (int i = 0; i < 12; ++i) { ICONINFO info{}; GetIconInfo(cursors[i % 4 == 3 ? 6 : i % 4].handle, &info); distinct.push_back(CreateIconIndirect(&info)); if (info.hbmColor) DeleteObject(info.hbmColor); if (info.hbmMask) DeleteObject(info.hbmMask); }
    const int width = 3840, height = 2160;
    auto frame = texture(d, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, random_bgra(width, height, 8, true).data(), width * 4);
    struct Scenario { const char* name; std::function<CursorState(int)> state; };
    const Scenario scenarios[] = {
        {"stationary", [&](int) { return CursorState{cursors[0].handle, {1900, 1000}, true}; }},
        {"moving", [&](int i) { return CursorState{cursors[0].handle, {100 + (i * 7) % 3600, 100 + (i * 3) % 2000}, true}; }},
        {"shape change every frame (cached)", [&](int i) { return CursorState{many[size_t(i) % 4], {1900, 1000}, true}; }},
        {"new shape every frame (12 handles, 8 cached)", [&](int i) { return CursorState{distinct[size_t(i) % 12], {1900, 1000}, true}; }},
    };
    LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
    for (const bool gpu : {true, false}) for (const auto& scenario : scenarios) {
        CursorCompositor compositor(d.device.Get()); std::vector<double> times;
        for (int i = 0; i < 1500; ++i) {
            LARGE_INTEGER a{}, b{}; QueryPerformanceCounter(&a);
            if (gpu) compositor.draw(frame.Get(), width, height, 0, 0, scenario.state(i)); else compositor.draw_cpu(frame.Get(), width, height, 0, 0, scenario.state(i));
            QueryPerformanceCounter(&b); if (i >= 50) times.push_back(double(b.QuadPart - a.QuadPart) * 1000 / double(frequency.QuadPart));
        }
        d.context->Flush();
        std::sort(times.begin(), times.end()); const auto stats = compositor.stats();
        std::cout << (gpu ? "gpu " : "cpu ") << scenario.name << ": p50 " << times[times.size() / 2] << " p95 " << times[times.size() * 95 / 100] << " max " << times.back()
                  << " ms, uploads " << stats.uploads << ", cursor textures " << stats.textures_created << ", readback " << stats.readback_bytes / 1024 << " KB over 1500 draws\n";
    }
    for (auto handle : distinct) DestroyCursor(handle);
    for (const auto& c : cursors) DestroyCursor(c.handle);
    return 0;
}
}

int main(int argc, char** argv) {
    try {
        if (argc > 1 && std::string(argv[1]) == "--cursor-bench") return cursor_bench(make_device(true));
        const bool gpu = argc > 1 && std::string(argv[1]) == "--gpu";
        const auto device = make_device(gpu);
        cursor_matches_gdi(device);
        cursor_caching(device);
        pooled_copies(device);
        hdr_matches_per_frame_path(device);
        if (gpu) { real_duplication(device); recreation(); }
        std::cout << "DXGI capture tests passed" << (gpu ? " (hardware device)" : " (WARP)") << "\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
