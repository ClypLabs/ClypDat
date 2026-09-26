// Desktop Duplication fallback: GPU cursor composition against the GDI path it
// replaces (byte for byte), the pooled owned-frame copies, HDR tone-mapping
// through the pool against the per-frame path, and rotated outputs turned
// upright. WARP by default; --gpu adds the hardware device and real
// duplication frames on every output.
#include "captured_frames.h"
#include "cursor_compositor.h"
#include "recording_capture.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <dxgi1_4.h>
#include <wrl/client.h>
#include <algorithm>
#include <climits>
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

// The orientation contract (output_orientation.h): for every rotation and
// odd sizes, each desktop pixel has exactly one texel inside the texture and
// maps back; a rectangle maps to exactly its pixels' texels; offsets from
// negative desktop origins; and the portrait display measured on the
// development machine (DISPLAY2, 1080x1920, DXGI rotation 270): a probe
// window's top-right quadrant at desktop (276, 274) 60x40 was found centred
// on texel (1625.5, 305.5) of the 1920x1080 frame.
void orientation_contract() {
    const OutputRotation rotations[]{OutputRotation::Identity, OutputRotation::Rotate90, OutputRotation::Rotate180, OutputRotation::Rotate270};
    CHECK(output_rotation(DXGI_MODE_ROTATION_UNSPECIFIED) == OutputRotation::Identity && output_rotation(DXGI_MODE_ROTATION_IDENTITY) == OutputRotation::Identity);
    CHECK(output_rotation(DXGI_MODE_ROTATION_ROTATE90) == OutputRotation::Rotate90 && output_rotation(DXGI_MODE_ROTATION_ROTATE180) == OutputRotation::Rotate180);
    CHECK(output_rotation(DXGI_MODE_ROTATION_ROTATE270) == OutputRotation::Rotate270);
    auto same = [](CaptureRect a, CaptureRect b) { return a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height; };
    int rects = 0;
    for (const auto rotation : rotations) for (const OutputSize size : {OutputSize{1, 1}, OutputSize{7, 5}, OutputSize{5, 7}, OutputSize{13, 1}, OutputSize{1, 9}, OutputSize{16, 16}}) {
        const auto texture = output_texture_size(size, rotation); const auto back = output_desktop_size(texture, rotation);
        CHECK(back.width == size.width && back.height == size.height);
        CHECK(output_rotation_swaps(rotation) ? texture.width == size.height && texture.height == size.width : texture.width == size.width && texture.height == size.height);
        std::vector<int> hits(size_t(texture.width) * texture.height);
        for (int y = 0; y < size.height; ++y) for (int x = 0; x < size.width; ++x) {
            const auto t = output_desktop_to_texel({x, y}, size, rotation);
            CHECK(t.x >= 0 && t.y >= 0 && t.x < texture.width && t.y < texture.height); ++hits[size_t(t.y) * texture.width + t.x];
            const auto d = output_texel_to_desktop(t, size, rotation); CHECK(d.x == x && d.y == y);
        }
        for (const int h : hits) CHECK(h == 1);
        // Every rectangle of the desktop.
        for (int y = 0; y < size.height; ++y) for (int x = 0; x < size.width; ++x) for (int h = 1; y + h <= size.height; ++h) for (int w = 1; x + w <= size.width; ++w) {
            const CaptureRect r{x, y, w, h}; const auto mapped = output_desktop_rect_to_texture(r, size, rotation);
            CHECK(output_contains(mapped, texture));
            int left = INT_MAX, top = INT_MAX, right = INT_MIN, bottom = INT_MIN;
            for (const auto corner : {OutputPoint{x, y}, OutputPoint{x + w - 1, y}, OutputPoint{x, y + h - 1}, OutputPoint{x + w - 1, y + h - 1}}) {
                const auto t = output_desktop_to_texel(corner, size, rotation);
                left = std::min(left, t.x); top = std::min(top, t.y); right = std::max(right, t.x + 1); bottom = std::max(bottom, t.y + 1);
            }
            CHECK(same(mapped, {left, top, right - left, bottom - top})); ++rects;
        }
    }
    // Negative and offset desktop origins.
    CHECK(same(output_relative({-1000, -250, 100, 50}, -1080, -300), {80, 50, 100, 50}));
    CHECK(same(output_relative({3840 + 216, 118 + 274, 120, 80}, 3840, 118), {216, 274, 120, 80}));
    CHECK(!output_contains({-1, 0, 4, 4}, {8, 8}) && !output_contains({5, 0, 4, 4}, {8, 8}) && output_contains({4, 4, 4, 4}, {8, 8}) && !output_contains({0, 0, 0, 4}, {8, 8}));
    // The measured portrait display.
    const auto quadrant = output_desktop_rect_to_texture({276, 274, 60, 40}, {1080, 1920}, OutputRotation::Rotate270);
    CHECK(same(quadrant, {1606, 276, 40, 60}));
    CHECK(quadrant.x + (quadrant.width - 1) / 2.0 == 1625.5 && quadrant.y + (quadrant.height - 1) / 2.0 == 305.5);
    std::cout << "orientation: 4 rotations x 6 sizes bijective and invertible, " << rects << " rectangles exact, measured portrait mapping reproduced\n";
}
// A scanout texture whose every texel is unique: B, G the low bytes of x and
// y, R their high bits, A varied.
std::vector<uint8_t> unique_texels(OutputSize size) {
    std::vector<uint8_t> bytes(size_t(size.width) * size.height * 4);
    for (int y = 0; y < size.height; ++y) for (int x = 0; x < size.width; ++x) {
        auto* p = &bytes[(size_t(y) * size.width + x) * 4];
        p[0] = uint8_t(x); p[1] = uint8_t(y); p[2] = uint8_t((x >> 8) | ((y >> 8) << 4)); p[3] = uint8_t(x * 7 + y * 13);
    }
    return bytes;
}
// `crop` of the desktop an output shows, read on the CPU from its scanout
// texels.
std::vector<uint8_t> upright(const std::vector<uint8_t>& texels, OutputSize desktop, OutputRotation rotation, CaptureRect crop) {
    const auto size = output_texture_size(desktop, rotation);
    std::vector<uint8_t> bytes(size_t(crop.width) * crop.height * 4);
    for (int y = 0; y < crop.height; ++y) for (int x = 0; x < crop.width; ++x) {
        const auto t = output_desktop_to_texel({crop.x + x, crop.y + y}, desktop, rotation);
        std::memcpy(&bytes[(size_t(y) * crop.width + x) * 4], &texels[(size_t(t.y) * size.width + t.x) * 4], 4);
    }
    return bytes;
}
// Rotated outputs through the pooled copy: whole frames, window crops,
// regions at every edge and corner, one-pixel and odd crops come out upright
// and exact (alpha included); crops are bounded by the desktop size, not the
// texture's; the pool stays bounded, rebuilding only when a rotation change
// changes the crop size; FP16 frames tone-map exactly as the upright path;
// the GPU cursor lands where the GDI cursor does, from negative origins too.
void rotated_copies(const Device& d) {
    const OutputSize desktop{321, 187};
    const OutputRotation rotations[]{OutputRotation::Identity, OutputRotation::Rotate90, OutputRotation::Rotate180, OutputRotation::Rotate270};
    const int capacity = capture_source_texture_capacity(2);
    CapturedFrameStore store(d.device.Get(), capacity, 80);
    const int W = desktop.width, H = desktop.height; int checked = 0;
    const CaptureRect crops[]{{0, 0, W, H}, {37, 21, 120, 90}, {0, 0, 64, 32}, {W - 64, 0, 64, 32}, {0, H - 32, 64, 32}, {W - 64, H - 32, 64, 32},
                              {0, 0, 1, 1}, {W - 1, H - 1, 1, 1}, {W - 1, 0, 1, H}, {0, H - 1, W, 1}, {11, 13, 33, 17}};
    for (const auto rotation : rotations) {
        const auto size = output_texture_size(desktop, rotation); const auto texels = unique_texels(size);
        auto input = texture(d, size.width, size.height, DXGI_FORMAT_B8G8R8A8_UNORM, texels.data(), size.width * 4);
        for (const auto& crop : crops) {
            const auto copied = store.copy(input.Get(), crop, rotation); CHECK(copied);
            D3D11_TEXTURE2D_DESC desc{}; copied->GetDesc(&desc); CHECK(int(desc.Width) == crop.width && int(desc.Height) == crop.height);
            if (read(copied.get()) != upright(texels, desktop, rotation, crop))
                throw std::runtime_error("Rotation " + std::to_string(int(rotation) * 90) + " crop " + std::to_string(crop.x) + "," + std::to_string(crop.y) + " " +
                                         std::to_string(crop.width) + "x" + std::to_string(crop.height) + " is not upright");
            ++checked;
        }
        // One pixel past any edge is refused; so is the texture's own size when it differs from the desktop's.
        for (const CaptureRect outside : {CaptureRect{W - 63, 0, 64, 32}, CaptureRect{0, H - 31, 64, 32}, CaptureRect{-1, 0, 64, 32}, CaptureRect{0, -1, 64, 32}})
            CHECK(!store.copy(input.Get(), outside, rotation));
        if (output_rotation_swaps(rotation)) CHECK(!store.copy(input.Get(), {0, 0, size.width, size.height}, rotation));
    }
    auto stats = store.stats(); CHECK(stats.pressure_drops == 0 && stats.leased == 0 && stats.peak_leased <= capacity);
    // Steady copies at any rotation allocate nothing; a square crop keeps its
    // pool across rotation changes. Turning a display keeps its scanout
    // (panel) size and swaps its desktop size, so a whole-frame copy
    // rebuilds the pool once per turn between landscape and portrait.
    std::vector<ComPtr<ID3D11Texture2D>> inputs;
    for (const auto rotation : rotations) { const auto size = output_texture_size(desktop, rotation); inputs.push_back(texture(d, size.width, size.height, DXGI_FORMAT_B8G8R8A8_UNORM, unique_texels(size).data(), size.width * 4)); }
    for (int i = 0; i < 8; ++i) CHECK(store.copy(inputs[size_t(i % 4)].Get(), {40, 30, 96, 96}, rotations[i % 4]));
    const auto square = store.stats().allocated;
    for (int i = 0; i < 400; ++i) CHECK(store.copy(inputs[size_t(i % 4)].Get(), {40 + i % 9, 30 + i % 5, 96, 96}, rotations[i % 4]));
    CHECK(store.stats().allocated == square);
    const OutputSize panel{W, H}; const auto scanout = unique_texels(panel);
    auto turned = texture(d, W, H, DXGI_FORMAT_B8G8R8A8_UNORM, scanout.data(), W * 4);
    for (int change = 0; change < 8; ++change) {
        const auto rotation = rotations[change % 4]; const auto upright_size = output_desktop_size(panel, rotation); const auto before = store.stats().allocated;
        for (int i = 0; i < 50; ++i) { const auto frame = store.copy(turned.Get(), {}, rotation); CHECK(frame);
            D3D11_TEXTURE2D_DESC desc{}; frame->GetDesc(&desc); CHECK(int(desc.Width) == upright_size.width && int(desc.Height) == upright_size.height);
            if (i == 0) CHECK(read(frame.get()) == upright(scanout, upright_size, rotation, {0, 0, upright_size.width, upright_size.height})); }
        CHECK(store.stats().allocated == before + 1); // Every quarter turn swaps the size.
    }
    stats = store.stats(); CHECK(stats.pressure_drops == 0 && stats.leased == 0 && stats.peak_leased <= capacity);
    // FP16: rotated tone-mapping equals the upright path's texel for texel.
    std::mt19937 random(12); std::vector<uint16_t> half;
    auto to_half = [](float value) { uint32_t bits; std::memcpy(&bits, &value, 4); const uint32_t sign = (bits >> 16) & 0x8000; int exponent = int((bits >> 23) & 0xFF) - 112;
        uint32_t mantissa = bits & 0x7FFFFF; if (exponent <= 0) return uint16_t(sign); if (exponent >= 31) return uint16_t(sign | 0x7BFF); return uint16_t(sign | (exponent << 10) | (mantissa >> 13)); };
    for (const float white : {80.f, 203.f, 480.f}) {
        CapturedFrameStore hdr(d.device.Get(), 4, white);
        for (const auto rotation : rotations) {
            const auto size = output_texture_size(desktop, rotation);
            half.assign(size_t(size.width) * size.height * 4, 0); for (auto& h : half) h = to_half(std::uniform_real_distribution<float>(-.2f, 12.5f)(random));
            auto input = texture(d, size.width, size.height, DXGI_FORMAT_R16G16B16A16_FLOAT, half.data(), size.width * 8);
            const auto mapped = read(hdr.copy(input.Get()).get()); // Tone-mapped in scanout orientation.
            for (const auto& crop : {crops[0], crops[1], crops[5], crops[8]})
                if (read(hdr.copy(input.Get(), crop, rotation).get()) != upright(mapped, desktop, rotation, crop))
                    throw std::runtime_error("HDR rotation " + std::to_string(int(rotation) * 90) + " at white " + std::to_string(white) + " differs from the upright tone map");
        }
        CHECK(hdr.stats().pressure_drops == 0);
    }
    // Cursor on rotated output, including a desktop at a negative origin.
    const auto cursors = make_cursors(); CursorCompositor compositor(d.device.Get()); int cursor_cases = 0;
    for (const auto rotation : rotations) {
        const auto size = output_texture_size(desktop, rotation); const auto texels = unique_texels(size);
        std::vector<uint8_t> opaque = texels; for (size_t i = 3; i < opaque.size(); i += 4) opaque[i] = 255;
        auto input = texture(d, size.width, size.height, DXGI_FORMAT_B8G8R8A8_UNORM, opaque.data(), size.width * 4);
        for (const POINT origin : {POINT{0, 0}, POINT{-1080, -300}, POINT{3840, 118}}) for (const auto& crop : {crops[0], crops[1], crops[5]})
            for (const POINT at : {POINT{crop.x + 10, crop.y + 12}, POINT{crop.x - 6, crop.y - 4}, POINT{crop.x + crop.width - 3, crop.y + crop.height - 2}}) {
                const auto& cursor = cursors[size_t(cursor_cases) % cursors.size()];
                const CursorState state{cursor.handle, {origin.x + at.x, origin.y + at.y}, true};
                const auto expected_bytes = upright(opaque, desktop, rotation, crop);
                auto expected = texture(d, crop.width, crop.height, DXGI_FORMAT_B8G8R8A8_UNORM, expected_bytes.data(), crop.width * 4);
                const bool drawn = cursor_draw_cpu(expected.Get(), crop.width, crop.height, origin.x + crop.x, origin.y + crop.y, state);
                const auto actual = store.copy(input.Get(), crop, rotation); CHECK(actual);
                const auto result = compositor.draw(actual.get(), crop.width, crop.height, origin.x + crop.x, origin.y + crop.y, state);
                CHECK(drawn == (result == CursorCompositor::Result::Gpu || result == CursorCompositor::Result::Cpu));
                if (read(expected.Get()) != read(actual.get())) throw std::runtime_error("Cursor " + cursor.name + " on rotation " + std::to_string(int(rotation) * 90) + " differs from GDI");
                ++cursor_cases;
            }
    }
    for (const auto& c : cursors) DestroyCursor(c.handle);
    stats = store.stats();
    std::cout << "rotated copies: " << checked << " crops upright and exact over 4 rotations; pool capacity " << capacity << ", allocated " << stats.allocated << ", pressure "
              << stats.pressure_drops << "; FP16 identical to the upright tone map at 80/203/480; " << cursor_cases << " cursor cases identical to GDI\n";
}

// Losing duplication access (a mode or rotation change) retries the reopen
// on later acquisitions; only a failure lasting past the window is an error,
// and a success ends the loss.
void reopen_policy() {
    using namespace std::chrono;
    CaptureReopener reopener(seconds(5)); const auto t0 = steady_clock::now(); int opens = 0; bool fails = true;
    const auto open = [&] { ++opens; if (fails) throw std::runtime_error("DuplicateOutput E_ACCESSDENIED"); };
    CHECK(!reopener.pending()); reopener.lost(t0); CHECK(reopener.pending());
    CHECK(!reopener.attempt(open, t0)); CHECK(!reopener.attempt(open, t0 + milliseconds(4900)));
    reopener.lost(t0 + seconds(3)); // A second loss while pending keeps the first's start.
    bool thrown = false; try { reopener.attempt(open, t0 + milliseconds(5100)); } catch (const std::runtime_error&) { thrown = true; }
    CHECK(thrown && !reopener.pending() && reopener.failures == 3 && reopener.reopens == 0);
    // Two changes in quick succession, each reopened after a few failures.
    for (int change = 0; change < 2; ++change) {
        const auto at = t0 + seconds(10 + change); reopener.lost(at); fails = true;
        CHECK(!reopener.attempt(open, at)); CHECK(!reopener.attempt(open, at + milliseconds(100)));
        fails = false; CHECK(reopener.attempt(open, at + milliseconds(200))); CHECK(!reopener.pending());
    }
    CHECK(reopener.reopens == 2 && reopener.failures == 7 && opens == 9);
    std::cout << "reopen: lost access retried within 5 s, rethrown after, repeated changes reopened\n";
}
// A window of four solid quadrants (red, green / blue, yellow) at a desktop
// position, painted with GDI so its pixels are exact.
class Quadrants {
    HWND window_ = nullptr;
    static LRESULT CALLBACK procedure(HWND hwnd, UINT message, WPARAM w, LPARAM l) {
        if (message != WM_PAINT) return DefWindowProcW(hwnd, message, w, l);
        PAINTSTRUCT paint; HDC dc = BeginPaint(hwnd, &paint); RECT r{}; GetClientRect(hwnd, &r); const LONG hw = r.right / 2, hh = r.bottom / 2;
        const RECT parts[4]{{0, 0, hw, hh}, {hw, 0, r.right, hh}, {0, hh, hw, r.bottom}, {hw, hh, r.right, r.bottom}};
        for (int i = 0; i < 4; ++i) { HBRUSH brush = CreateSolidBrush(colour(i)); FillRect(dc, &parts[i], brush); DeleteObject(brush); }
        EndPaint(hwnd, &paint); return 0;
    }
public:
    static COLORREF colour(int quadrant) { const COLORREF c[4]{RGB(255, 0, 0), RGB(0, 255, 0), RGB(0, 0, 255), RGB(255, 255, 0)}; return c[quadrant]; }
    Quadrants(int x, int y, int width, int height) {
        WNDCLASSW type{}; type.lpfnWndProc = procedure; type.hInstance = GetModuleHandleW(nullptr); type.lpszClassName = L"ClypDatQuadrants"; RegisterClassW(&type);
        window_ = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, type.lpszClassName, L"", WS_POPUP, x, y, width, height, nullptr, nullptr, type.hInstance, nullptr);
        CHECK(window_); ShowWindow(window_, SW_SHOWNOACTIVATE); UpdateWindow(window_);
    }
    ~Quadrants() { DestroyWindow(window_); }
    void pump() { MSG message; while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) DispatchMessageW(&message); }
    // Every pixel of an upright `width` x `height` copy has its quadrant's colour.
    static bool matches(const std::vector<uint8_t>& bgra, int width, int height) {
        for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
            const auto c = colour((y >= height / 2) * 2 + (x >= width / 2)); const auto* p = &bgra[(size_t(y) * width + x) * 4];
            if (p[0] != GetBValue(c) || p[1] != GetGValue(c) || p[2] != GetRValue(c)) return false;
        }
        return true;
    }
};

// Real duplication frames on every output of the adapter, rotated ones
// included: the texture has the scanout size for the output's rotation; the
// pooled copy plus GPU cursor equals the frame read back, turned upright on
// the CPU, plus the GDI cursor; and a window of four coloured quadrants on
// the output comes out with every quadrant in place.
void real_duplication(const Device& d) {
    ComPtr<IDXGIDevice> dxgi; d.device.As(&dxgi); ComPtr<IDXGIAdapter> adapter; dxgi->GetAdapter(&adapter);
    int outputs = 0;
    for (UINT i = 0;; ++i) {
        ComPtr<IDXGIOutput> output; if (adapter->EnumOutputs(i, &output) == DXGI_ERROR_NOT_FOUND) break;
        DXGI_OUTPUT_DESC od{}; output->GetDesc(&od); if (!od.AttachedToDesktop) continue;
        ComPtr<IDXGIOutput1> output1; output.As(&output1); ComPtr<IDXGIOutputDuplication> duplication;
        CHECK(SUCCEEDED(output1->DuplicateOutput(d.device.Get(), &duplication)));
        DXGI_OUTDUPL_DESC dd{}; duplication->GetDesc(&dd);
        const auto rotation = output_rotation(int(dd.Rotation)); CHECK(output_rotation(int(od.Rotation)) == rotation);
        const OutputSize desktop{od.DesktopCoordinates.right - od.DesktopCoordinates.left, od.DesktopCoordinates.bottom - od.DesktopCoordinates.top};
        const auto scanout = output_texture_size(desktop, rotation);
        CapturedFrameStore store(d.device.Get(), capture_source_texture_capacity(2), 80);
        CursorCompositor compositor(d.device.Get());
        const CaptureRect window{desktop.width / 5, desktop.height / 7, 120, 80};
        Quadrants quadrants(od.DesktopCoordinates.left + window.x, od.DesktopCoordinates.top + window.y, window.width, window.height);
        int frames = 0, drawn = 0; bool upright_window = false, bgra = true;
        for (int attempt = 0; attempt < 600 && (frames < 20 || !upright_window) && bgra; ++attempt) {
            quadrants.pump();
            DXGI_OUTDUPL_FRAME_INFO info{}; ComPtr<IDXGIResource> resource;
            const auto hr = duplication->AcquireNextFrame(20, &info, &resource);
            if (hr == DXGI_ERROR_WAIT_TIMEOUT) continue; CHECK(SUCCEEDED(hr));
            struct Release { IDXGIOutputDuplication* p; ~Release() { p->ReleaseFrame(); } } release{duplication.Get()};
            ComPtr<ID3D11Texture2D> frame; resource.As(&frame);
            D3D11_TEXTURE2D_DESC desc{}; frame->GetDesc(&desc);
            CHECK(int(desc.Width) == scanout.width && int(desc.Height) == scanout.height);
            if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) { bgra = false; break; }
            if (!upright_window) upright_window = Quadrants::matches(read(store.copy(frame.Get(), window, rotation).get()), window.width, window.height);
            const auto texels = read(frame.Get());
            const auto state = capture_cursor_state();
            const int px = state.position.x - od.DesktopCoordinates.left, py = state.position.y - od.DesktopCoordinates.top;
            // Whole display, a window-like crop around the cursor, and a region at the display's corner.
            const CaptureRect crops[] = {{0, 0, desktop.width, desktop.height},
                {std::clamp(px - 200, 0, desktop.width - 400), std::clamp(py - 150, 0, desktop.height - 300), 400, 300}, {desktop.width - 256, desktop.height - 144, 256, 144}};
            for (const auto& crop : crops) {
                const auto expected_bytes = upright(texels, desktop, rotation, crop);
                auto reference = texture(d, crop.width, crop.height, DXGI_FORMAT_B8G8R8A8_UNORM, expected_bytes.data(), crop.width * 4);
                const int ox = od.DesktopCoordinates.left + crop.x, oy = od.DesktopCoordinates.top + crop.y;
                const bool cpu = cursor_draw_cpu(reference.Get(), crop.width, crop.height, ox, oy, state);
                auto pooled = store.copy(frame.Get(), crop, rotation); CHECK(pooled);
                const auto result = compositor.draw(pooled.get(), crop.width, crop.height, ox, oy, state);
                drawn += cpu; CHECK(cpu == (result == CursorCompositor::Result::Gpu || result == CursorCompositor::Result::Cpu));
                CHECK(read(reference.Get()) == read(pooled.get()));
            }
            ++frames;
        }
        std::wcout << L"real duplication " << od.DeviceName;
        if (!bgra) { std::cout << ": not BGRA (HDR desktop), skipped\n"; continue; }
        if (!upright_window) throw std::runtime_error("Output rotation " + std::to_string(int(rotation) * 90) + ": the quadrant window did not come out upright");
        const auto stats = compositor.stats(); const auto pool = store.stats();
        std::cout << " (" << desktop.width << "x" << desktop.height << ", rotation " << int(rotation) * 90 << ", texture " << scanout.width << "x" << scanout.height << "): "
                  << frames << " frames x 3 crops identical to the CPU-upright reference, quadrant window upright; cursor drawn in " << drawn << " (GPU " << stats.gpu_draws
                  << ", CPU fallback " << stats.cpu_fallbacks << "); pool allocated " << pool.allocated << ", pressure " << pool.pressure_drops << "\n";
        CHECK(pool.pressure_drops == 0 && frames >= 20); ++outputs;
    }
    CHECK(outputs > 0);
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
        // Physical pixels, as the recorder (PerMonitorV2 manifest) sees them.
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (argc > 1 && std::string(argv[1]) == "--cursor-bench") return cursor_bench(make_device(true));
        const bool gpu = argc > 1 && std::string(argv[1]) == "--gpu";
        const auto device = make_device(gpu);
        cursor_matches_gdi(device);
        cursor_caching(device);
        pooled_copies(device);
        hdr_matches_per_frame_path(device);
        orientation_contract();
        rotated_copies(device);
        reopen_policy();
        if (gpu) { real_duplication(device); recreation(); }
        std::cout << "DXGI capture tests passed" << (gpu ? " (hardware device)" : " (WARP)") << "\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
