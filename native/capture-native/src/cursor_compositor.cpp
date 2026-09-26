#include "cursor_compositor.h"
#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <deque>
#include <map>
#include <stdexcept>
#include <string>
#include <vector>

namespace clypdat {
namespace {
using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;
void checked(HRESULT hr, const char* operation) {
    if (FAILED(hr)) throw std::runtime_error(std::string(operation) + " HRESULT=" + std::to_string(uint32_t(hr)));
}
double since_ms(Clock::time_point started) { return std::chrono::duration<double, std::milli>(Clock::now() - started).count(); }
double percentile(const std::deque<double>& values, double rank) {
    if (values.empty()) return 0;
    std::vector<double> sorted(values.begin(), values.end()); std::sort(sorted.begin(), sorted.end());
    return sorted[std::max<size_t>(1, size_t(std::ceil(sorted.size() * rank))) - 1];
}
void record(std::deque<double>& values, double value) { values.push_back(value); if (values.size() > 240) values.pop_front(); }
struct IconBitmaps {
    ICONINFO info{};
    bool valid = false;
    explicit IconBitmaps(HCURSOR cursor) { valid = GetIconInfo(cursor, &info) != FALSE; }
    ~IconBitmaps() { if (info.hbmColor) DeleteObject(info.hbmColor); if (info.hbmMask) DeleteObject(info.hbmMask); }
};

// The pre-GPU cursor path: read the cursor rectangle back, DrawIconEx over it
// on a DIB section, upload it again.
class CpuCursor {
    ComPtr<ID3D11Texture2D> staging_;
public:
    bool draw(ID3D11Texture2D* texture, int width, int height, int origin_x, int origin_y, const CursorState& cursor, uint64_t* readback_bytes) {
        if (!cursor.showing || !cursor.handle) return false;
        IconBitmaps icon(cursor.handle); if (!icon.valid) return false;
        const int x = cursor.position.x - origin_x - int(icon.info.xHotspot);
        const int y = cursor.position.y - origin_y - int(icon.info.yHotspot);
        BITMAP shape_info{};
        const auto shape = icon.info.hbmColor ? icon.info.hbmColor : icon.info.hbmMask;
        if (!shape || GetObjectW(shape, sizeof(shape_info), &shape_info) != sizeof(shape_info)) return false;
        const int cursor_width = shape_info.bmWidth;
        const int cursor_height = icon.info.hbmColor ? shape_info.bmHeight : shape_info.bmHeight / 2;
        const int left = std::max(0, x), top = std::max(0, y);
        const int right = std::min(width, x + cursor_width), bottom = std::min(height, y + cursor_height);
        if (right <= left || bottom <= top) return false;
        const int w = right - left, h = bottom - top, stride = w * 4;
        std::vector<uint8_t> pixels(size_t(stride) * h);
        ComPtr<ID3D11Device> device; texture->GetDevice(&device);
        ComPtr<ID3D11DeviceContext> context; device->GetImmediateContext(&context);
        D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
        if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) throw std::runtime_error("Unsupported capture texture format");
        D3D11_TEXTURE2D_DESC previous{}; if (staging_) staging_->GetDesc(&previous);
        if (!staging_ || previous.Width != UINT(w) || previous.Height != UINT(h)) {
            staging_.Reset(); desc.Width = UINT(w); desc.Height = UINT(h); desc.MipLevels = 1; desc.ArraySize = 1;
            desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ; desc.MiscFlags = 0;
            checked(device->CreateTexture2D(&desc, nullptr, &staging_), "Create recording readback");
        }
        const D3D11_BOX box{UINT(left), UINT(top), 0, UINT(right), UINT(bottom), 1};
        context->CopySubresourceRegion(staging_.Get(), 0, 0, 0, 0, texture, 0, &box);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        checked(context->Map(staging_.Get(), 0, D3D11_MAP_READ, 0, &mapped), "Map recording readback");
        for (int row = 0; row < h; ++row) std::memcpy(pixels.data() + size_t(row) * stride, static_cast<const uint8_t*>(mapped.pData) + size_t(row) * mapped.RowPitch, stride);
        context->Unmap(staging_.Get(), 0);
        if (readback_bytes) *readback_bytes += uint64_t(stride) * h;
        BITMAPINFO info{}; info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = w; info.bmiHeader.biHeight = -h;
        info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32; info.bmiHeader.biCompression = BI_RGB;
        void* data = nullptr; HDC dc = CreateCompatibleDC(nullptr); if (!dc) return false;
        HBITMAP dib = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &data, nullptr, 0);
        if (!dib) { DeleteDC(dc); return false; }
        auto old = SelectObject(dc, dib);
        std::memcpy(data, pixels.data(), pixels.size());
        DrawIconEx(dc, x - left, y - top, cursor.handle, cursor_width, cursor_height, 0, nullptr, DI_NORMAL);
        GdiFlush();
        std::memcpy(pixels.data(), data, pixels.size());
        SelectObject(dc, old); DeleteObject(dib); DeleteDC(dc);
        context->UpdateSubresource(texture, 0, &box, pixels.data(), UINT(stride), 0);
        return true;
    }
};

// A cursor's pixels as DrawIconEx uses them. mode 0 blends colour by its
// alpha; mode 1 ANDs the mask then XORs the colour.
struct Shape {
    bool valid = false, supported = false;
    int width = 0, height = 0, mode = 0;
    POINT hotspot{};
    std::vector<uint8_t> color, mask; // BGRA; one byte per pixel, 0 or 255.
    uint64_t hash = 0;
};
Shape read_shape(HCURSOR cursor) {
    Shape shape; IconBitmaps icon(cursor); if (!icon.valid) return shape;
    BITMAP info{};
    const auto main = icon.info.hbmColor ? icon.info.hbmColor : icon.info.hbmMask;
    if (!main || GetObjectW(main, sizeof(info), &info) != sizeof(info)) return shape;
    shape.valid = true; shape.hotspot = {LONG(icon.info.xHotspot), LONG(icon.info.yHotspot)};
    shape.width = info.bmWidth; shape.height = icon.info.hbmColor ? info.bmHeight : info.bmHeight / 2;
    if (shape.width <= 0 || shape.height <= 0) { shape.valid = false; return shape; }
    // DrawIconEx converts colour bitmaps below 32 bpp through GDI; those,
    // and oversized cursors, stay on the CPU path.
    shape.supported = shape.width <= 256 && shape.height <= 256 && (!icon.info.hbmColor || info.bmBitsPixel == 32);
    if (!shape.supported) return shape;
    HDC dc = CreateCompatibleDC(nullptr); if (!dc) { shape.supported = false; return shape; }
    auto bits = [&](HBITMAP bitmap, int w, int h, std::vector<uint8_t>& out) {
        BITMAPINFO bi{}; bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER); bi.bmiHeader.biWidth = w; bi.bmiHeader.biHeight = -h;
        bi.bmiHeader.biPlanes = 1; bi.bmiHeader.biBitCount = 32; bi.bmiHeader.biCompression = BI_RGB;
        out.resize(size_t(w) * h * 4);
        return GetDIBits(dc, bitmap, 0, UINT(h), out.data(), &bi, DIB_RGB_COLORS) == h;
    };
    const size_t pixels = size_t(shape.width) * shape.height;
    shape.mask.resize(pixels);
    std::vector<uint8_t> mask;
    bool read = false;
    if (icon.info.hbmColor) {
        read = bits(icon.info.hbmColor, shape.width, shape.height, shape.color) && bits(icon.info.hbmMask, shape.width, shape.height, mask);
        bool alpha = false; for (size_t i = 0; read && i < pixels && !alpha; ++i) alpha = shape.color[i * 4 + 3] != 0;
        shape.mode = alpha ? 0 : 1;
        for (size_t i = 0; read && i < pixels; ++i) shape.mask[i] = mask[i * 4] ? 255 : 0;
    } else {
        read = bits(icon.info.hbmMask, shape.width, shape.height * 2, mask);
        shape.mode = 1; shape.color.assign(pixels * 4, 0);
        for (size_t i = 0; read && i < pixels; ++i) {
            shape.mask[i] = mask[i * 4] ? 255 : 0;
            if (mask[(pixels + i) * 4]) shape.color[i * 4] = shape.color[i * 4 + 1] = shape.color[i * 4 + 2] = 255;
        }
    }
    DeleteDC(dc);
    if (!read) { shape.supported = false; return shape; }
    uint64_t hash = 1469598103934665603ull;
    auto mix = [&](const void* data, size_t size) { for (size_t i = 0; i < size; ++i) { hash ^= static_cast<const uint8_t*>(data)[i]; hash *= 1099511628211ull; } };
    const int header[] = {shape.width, shape.height, shape.mode, int(shape.hotspot.x), int(shape.hotspot.y)};
    mix(header, sizeof(header)); mix(shape.color.data(), shape.color.size()); mix(shape.mask.data(), shape.mask.size());
    shape.hash = hash;
    return shape;
}
const char* kShader = R"(
cbuffer Cursor : register(b0) { int2 origin; int mode; int unused; };
Texture2D<float4> Shape : register(t0);
Texture2D<float> Mask : register(t1);
Texture2D<float4> Dest : register(t2);
struct V { float4 p : SV_Position; };
V VS(uint id : SV_VertexID) { V o; o.p = float4(id == 2 ? 3 : -1, id == 1 ? 3 : -1, 0, 1); return o; }
uint4 Bytes(float4 v) { return uint4(round(saturate(v) * 255)); }
float4 PS(V i) : SV_Target {
    int3 q = int3(int2(i.p.xy) - origin, 0);
    uint4 d = Bytes(Dest.Load(q)), s = Bytes(Shape.Load(q));
    uint4 result;
    if (mode == 0) {
        uint a = s.a;
        result.rgb = s.rgb * a / 255 + (d.rgb * (255 - a) + 127) / 255;
        result.a = a + (d.a * (255 - a) + 127) / 255;
    } else {
        uint m = uint(round(saturate(Mask.Load(q)) * 255));
        result.rgb = (d.rgb & m) ^ s.rgb;
        result.a = 0;
    }
    return float4(result) / 255.0;
}
)";
}

CursorState capture_cursor_state() {
    CURSORINFO info{sizeof(CURSORINFO)};
    CursorState state;
    if (!GetCursorInfo(&info)) return state;
    state.handle = info.hCursor; state.position = info.ptScreenPos; state.showing = (info.flags & CURSOR_SHOWING) != 0;
    return state;
}
bool cursor_draw_cpu(ID3D11Texture2D* target, int width, int height, int origin_x, int origin_y, const CursorState& cursor, uint64_t* readback_bytes) {
    CpuCursor cpu; return cpu.draw(target, width, height, origin_x, origin_y, cursor, readback_bytes);
}

struct CursorCompositor::Impl {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Multithread> multithread;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11Buffer> constants;
    // Recently used shapes, most recent last; each owns its textures.
    struct Entry {
        HCURSOR handle = nullptr; Shape shape; Clock::time_point loaded;
        ComPtr<ID3D11ShaderResourceView> color, mask;
    };
    std::vector<Entry> entries;
    // The frame region under the cursor, copied before each draw.
    ComPtr<ID3D11Texture2D> dest; ComPtr<ID3D11ShaderResourceView> dest_view; int dest_size = 0;
    std::map<ID3D11Texture2D*, ComPtr<ID3D11RenderTargetView>> targets; UINT target_width = 0, target_height = 0;
    CpuCursor cpu;
    CursorCompositorStats counters;
    std::deque<double> compose_times, lock_waits;

    ComPtr<ID3D11ShaderResourceView> texture(int width, int height, DXGI_FORMAT format, const void* data, UINT pitch) {
        D3D11_TEXTURE2D_DESC desc{}; desc.Width = UINT(width); desc.Height = UINT(height); desc.MipLevels = 1; desc.ArraySize = 1;
        desc.Format = format; desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_IMMUTABLE; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        const D3D11_SUBRESOURCE_DATA initial{data, pitch, 0};
        ComPtr<ID3D11Texture2D> created; checked(device->CreateTexture2D(&desc, &initial, &created), "Create cursor shape texture");
        ComPtr<ID3D11ShaderResourceView> view; checked(device->CreateShaderResourceView(created.Get(), nullptr, &view), "Create cursor shape view");
        ++counters.textures_created; return view;
    }
    // The cached entry for `handle`, loading or re-reading its shape.
    Entry* entry(HCURSOR handle) {
        const auto now = Clock::now();
        auto found = std::find_if(entries.begin(), entries.end(), [&](const Entry& e) { return e.handle == handle; });
        if (found != entries.end() && now - found->loaded < std::chrono::seconds(2)) {
            if (found + 1 != entries.end()) std::rotate(found, found + 1, entries.end());
            return &entries.back();
        }
        auto shape = read_shape(handle);
        if (found != entries.end()) {
            ++counters.revalidations; found->loaded = now;
            if (shape.valid && shape.supported && found->shape.supported && shape.hash == found->shape.hash) {
                if (found + 1 != entries.end()) std::rotate(found, found + 1, entries.end());
                return &entries.back();
            }
            entries.erase(found);
        }
        ++counters.shape_changes;
        Entry fresh; fresh.handle = handle; fresh.loaded = now; fresh.shape = std::move(shape);
        if (fresh.shape.valid && fresh.shape.supported) {
            fresh.color = texture(fresh.shape.width, fresh.shape.height, DXGI_FORMAT_B8G8R8A8_UNORM, fresh.shape.color.data(), UINT(fresh.shape.width * 4));
            fresh.mask = texture(fresh.shape.width, fresh.shape.height, DXGI_FORMAT_R8_UNORM, fresh.shape.mask.data(), UINT(fresh.shape.width));
            ++counters.uploads;
        }
        if (entries.size() >= 8) entries.erase(entries.begin());
        entries.push_back(std::move(fresh));
        return &entries.back();
    }
    ID3D11RenderTargetView* target_view(ID3D11Texture2D* target) {
        D3D11_TEXTURE2D_DESC desc{}; target->GetDesc(&desc);
        // Pool textures of an earlier size are discarded; so are their views.
        if (desc.Width != target_width || desc.Height != target_height || targets.size() >= 32) { targets.clear(); target_width = desc.Width; target_height = desc.Height; }
        auto& view = targets[target];
        if (!view) checked(device->CreateRenderTargetView(target, nullptr, &view), "Create cursor target view");
        return view.Get();
    }
};

CursorCompositor::CursorCompositor(ID3D11Device* device) : impl_(std::make_unique<Impl>()) {
    auto& s = *impl_;
    s.device = device; device->GetImmediateContext(&s.context); s.context.As(&s.multithread);
    ComPtr<ID3DBlob> vs, ps, error;
    checked(D3DCompile(kShader, std::strlen(kShader), nullptr, nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vs, &error), "Compile cursor vertex shader");
    checked(D3DCompile(kShader, std::strlen(kShader), nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &ps, &error), "Compile cursor pixel shader");
    checked(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &s.vertex), "Create cursor vertex shader");
    checked(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &s.pixel), "Create cursor pixel shader");
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth = 16; desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    checked(device->CreateBuffer(&desc, nullptr, &s.constants), "Create cursor constants");
}
CursorCompositor::~CursorCompositor() = default;
void CursorCompositor::reset() { impl_->entries.clear(); impl_->targets.clear(); }
CursorCompositorStats CursorCompositor::stats() const {
    auto result = impl_->counters;
    result.compose_p50_ms = percentile(impl_->compose_times, .5); result.compose_p95_ms = percentile(impl_->compose_times, .95);
    result.lock_wait_p95_ms = percentile(impl_->lock_waits, .95);
    return result;
}

CursorCompositor::Result CursorCompositor::draw_cpu(ID3D11Texture2D* target, int width, int height, int origin_x, int origin_y, const CursorState& cursor) {
    auto& s = *impl_;
    const auto started = Clock::now();
    if (!cursor.showing || !cursor.handle) { ++s.counters.hidden; return Result::Hidden; }
    if (!s.cpu.draw(target, width, height, origin_x, origin_y, cursor, &s.counters.readback_bytes)) { ++s.counters.outside; return Result::Outside; }
    ++s.counters.cpu_draws; record(s.compose_times, since_ms(started));
    return Result::Cpu;
}
CursorCompositor::Result CursorCompositor::draw(ID3D11Texture2D* target, int width, int height, int origin_x, int origin_y, const CursorState& cursor) {
    auto& s = *impl_;
    const auto started = Clock::now();
    if (!cursor.showing || !cursor.handle) { ++s.counters.hidden; return Result::Hidden; }
    const auto* entry = s.entry(cursor.handle);
    if (!entry->shape.valid) { ++s.counters.failed; return Result::Failed; }
    if (!entry->shape.supported) {
        const bool drawn = s.cpu.draw(target, width, height, origin_x, origin_y, cursor, &s.counters.readback_bytes);
        if (!drawn) { ++s.counters.outside; return Result::Outside; }
        ++s.counters.cpu_fallbacks; ++s.counters.cpu_draws; record(s.compose_times, since_ms(started)); return Result::Cpu;
    }
    const auto& shape = entry->shape;
    const int x = cursor.position.x - origin_x - int(shape.hotspot.x), y = cursor.position.y - origin_y - int(shape.hotspot.y);
    const int left = std::max(0, x), top = std::max(0, y), right = std::min(width, x + shape.width), bottom = std::min(height, y + shape.height);
    if (right <= left || bottom <= top) { ++s.counters.outside; return Result::Outside; }
    D3D11_TEXTURE2D_DESC desc{}; target->GetDesc(&desc);
    if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) throw std::runtime_error("Unsupported capture texture format");
    const int size = std::max(shape.width, shape.height);
    if (!s.dest || s.dest_size < size) {
        s.dest.Reset(); s.dest_view.Reset();
        D3D11_TEXTURE2D_DESC scratch{}; scratch.Width = scratch.Height = UINT(size); scratch.MipLevels = 1; scratch.ArraySize = 1;
        scratch.Format = DXGI_FORMAT_B8G8R8A8_UNORM; scratch.SampleDesc.Count = 1; scratch.Usage = D3D11_USAGE_DEFAULT; scratch.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        checked(s.device->CreateTexture2D(&scratch, nullptr, &s.dest), "Create cursor destination copy");
        checked(s.device->CreateShaderResourceView(s.dest.Get(), nullptr, &s.dest_view), "Create cursor destination view");
        s.dest_size = size; ++s.counters.textures_created;
    }
    auto* view = s.target_view(target);
    const int constants[4] = {x, y, shape.mode, 0};
    const D3D11_BOX box{UINT(left), UINT(top), 0, UINT(right), UINT(bottom), 1};
    const D3D11_VIEWPORT viewport{float(left), float(top), float(right - left), float(bottom - top), 0, 1};
    const auto locking = Clock::now();
    // The draw's pipeline state must not interleave with the encoder
    // thread's overlay draws on the same immediate context.
    struct Enter { ID3D11Multithread* p; explicit Enter(ID3D11Multithread* v) : p(v) { if (p) p->Enter(); } ~Enter() { if (p) p->Leave(); } } lock(s.multithread.Get());
    record(s.lock_waits, since_ms(locking));
    s.context->CopySubresourceRegion(s.dest.Get(), 0, UINT(left - x), UINT(top - y), 0, target, 0, &box);
    s.context->UpdateSubresource(s.constants.Get(), 0, nullptr, constants, 0, 0);
    ID3D11ShaderResourceView* views[] = {entry->color.Get(), entry->mask.Get(), s.dest_view.Get()};
    auto* buffer = s.constants.Get();
    s.context->IASetInputLayout(nullptr); s.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    s.context->VSSetShader(s.vertex.Get(), nullptr, 0); s.context->PSSetShader(s.pixel.Get(), nullptr, 0);
    s.context->PSSetConstantBuffers(0, 1, &buffer); s.context->PSSetShaderResources(0, 3, views);
    s.context->OMSetRenderTargets(1, &view, nullptr); s.context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
    s.context->OMSetDepthStencilState(nullptr, 0); s.context->RSSetState(nullptr); s.context->RSSetViewports(1, &viewport);
    s.context->Draw(3, 0);
    ID3D11RenderTargetView* no_target = nullptr; ID3D11ShaderResourceView* no_views[3] = {};
    s.context->OMSetRenderTargets(1, &no_target, nullptr); s.context->PSSetShaderResources(0, 3, no_views);
    ++s.counters.gpu_draws; record(s.compose_times, since_ms(started));
    return Result::Gpu;
}
}
