#include "readback_stage.h"
#include <Windows.h>
#include <d3d11_4.h>
#include <wrl/client.h>
extern "C" {
#include <libavutil/imgutils.h>
}
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <deque>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace clypdat {
namespace {
DXGI_FORMAT dxgi_format(AVPixelFormat format) {
    switch (format) {
    case AV_PIX_FMT_NV12: return DXGI_FORMAT_NV12;
    case AV_PIX_FMT_P010: return DXGI_FORMAT_P010;
    default: throw std::invalid_argument("Recording readback supports NV12 and P010 frames");
    }
}
double elapsed_ms(std::chrono::steady_clock::time_point started) {
    return std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count();
}
std::string hresult(const char* what, HRESULT value) {
    char code[16]{}; std::snprintf(code, sizeof(code), "0x%08X", unsigned(value));
    return std::string(what) + " (hr=" + code + ")";
}
}

struct ReadbackStage::Impl {
    Microsoft::WRL::ComPtr<ID3D11Device> device;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    Microsoft::WRL::ComPtr<ID3D11Multithread> multithread;
    int width = 0, height = 0;
    AVPixelFormat format = AV_PIX_FMT_NONE;
    struct Slot { Microsoft::WRL::ComPtr<ID3D11Texture2D> texture; OwnedFrame properties; };
    std::vector<Slot> slots;
    std::deque<size_t> staged; // Slot indexes, oldest first.
    // Payload owners. One is free when this is its only reference.
    std::vector<OwnedFrame> frames;
    uint64_t allocations = 0;
    HANDLE timer = nullptr;
    ~Impl() { if (timer) CloseHandle(timer); }
    struct Lock {
        ID3D11Multithread* value;
        explicit Lock(ID3D11Multithread* v) : value(v) { if (value) value->Enter(); }
        ~Lock() { if (value) value->Leave(); }
    };
    AVFrame* free_frame() const {
        for (const auto& frame : frames) if (av_frame_is_writable(frame.get())) return frame.get();
        return nullptr;
    }
    void pause(int64_t microseconds) const {
        LARGE_INTEGER due{}; due.QuadPart = -microseconds * 10;
        if (timer && SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE)) WaitForSingleObject(timer, 50);
        else std::this_thread::sleep_for(std::chrono::microseconds(microseconds));
    }
};

ReadbackStage::ReadbackStage(ID3D11Device* device, int width, int height, AVPixelFormat format, int staging_slots, int cpu_frames)
    : impl_(std::make_unique<Impl>()) {
    auto& s = *impl_;
    if (width < 2 || height < 2 || (width & 1) || (height & 1) || width > 16384 || height > 16384 ||
        staging_slots < 0 || staging_slots > 8 || cpu_frames < 1 || cpu_frames > 8 || (staging_slots && !device))
        throw std::invalid_argument("Invalid recording readback stage");
    const auto texture_format = dxgi_format(format);
    s.width = width; s.height = height; s.format = format;
    if (device) {
        s.device = device; device->GetImmediateContext(&s.context); s.context.As(&s.multithread);
        s.timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    }
    for (int i = 0; i < staging_slots; ++i) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = UINT(width); desc.Height = UINT(height); desc.MipLevels = 1; desc.ArraySize = 1;
        desc.Format = texture_format; desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_STAGING; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        Impl::Slot slot;
        const auto created = device->CreateTexture2D(&desc, nullptr, &slot.texture);
        if (FAILED(created)) throw std::runtime_error(hresult("Create recording readback staging texture", created));
        slot.properties.reset(av_frame_alloc()); if (!slot.properties) throw std::bad_alloc();
        s.slots.push_back(std::move(slot)); ++s.allocations;
    }
    for (int i = 0; i < cpu_frames; ++i) {
        OwnedFrame frame(av_frame_alloc()); if (!frame) throw std::bad_alloc();
        frame->format = format; frame->width = width; frame->height = height;
        if (av_frame_get_buffer(frame.get(), 0) < 0) throw std::bad_alloc();
        s.frames.push_back(std::move(frame)); ++s.allocations;
    }
}
ReadbackStage::~ReadbackStage() = default;

ID3D11Device* ReadbackStage::device() const { return impl_->device.Get(); }
int ReadbackStage::width() const { return impl_->width; }
int ReadbackStage::height() const { return impl_->height; }
AVPixelFormat ReadbackStage::format() const { return impl_->format; }
int ReadbackStage::staging_slots() const { return int(impl_->slots.size()); }
int ReadbackStage::cpu_frames() const { return int(impl_->frames.size()); }
int ReadbackStage::pending() const { return int(impl_->staged.size()); }
int ReadbackStage::cpu_frames_in_use() const {
    int used = 0;
    for (const auto& frame : impl_->frames) if (!av_frame_is_writable(frame.get())) ++used;
    return used;
}
bool ReadbackStage::cpu_frame_available() const { return impl_->free_frame() != nullptr; }
uint64_t ReadbackStage::allocations() const { return impl_->allocations; }

void ReadbackStage::stage(const AVFrame& frame) {
    auto& s = *impl_;
    if (s.staged.size() >= s.slots.size()) throw std::logic_error("No free recording readback staging texture");
    if (frame.format != AV_PIX_FMT_D3D11 || !frame.data[0]) throw std::invalid_argument("Recording readback stages only D3D11 frames");
    auto* texture = reinterpret_cast<ID3D11Texture2D*>(frame.data[0]);
    const UINT slice = UINT(reinterpret_cast<uintptr_t>(frame.data[1]));
    D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
    Microsoft::WRL::ComPtr<ID3D11Device> owner; texture->GetDevice(&owner);
    if (owner.Get() != s.device.Get() || desc.Format != dxgi_format(s.format) || desc.Width != UINT(s.width) ||
        desc.Height != UINT(s.height) || slice >= desc.ArraySize)
        throw std::invalid_argument("Recording readback frame does not match its staging textures");
    size_t index = 0;
    while (std::find(s.staged.begin(), s.staged.end(), index) != s.staged.end()) ++index;
    {
        Impl::Lock lock(s.multithread.Get());
        s.context->CopySubresourceRegion(s.slots[index].texture.Get(), 0, 0, 0, 0, texture,
            D3D11CalcSubresource(0, slice, desc.MipLevels), nullptr);
        // Submit now: the copy runs while the CPU reads the previous frame.
        s.context->Flush();
    }
    auto* properties = s.slots[index].properties.get();
    av_frame_unref(properties);
    if (av_frame_copy_props(properties, &frame) < 0) throw std::bad_alloc();
    s.staged.push_back(index);
}

OwnedFrame ReadbackStage::acquire() {
    auto* free = impl_->free_frame();
    if (!free) return {};
    OwnedFrame frame(av_frame_alloc());
    if (!frame || av_frame_ref(frame.get(), free) < 0) throw std::bad_alloc();
    return frame;
}

ReadbackResult ReadbackStage::read() {
    auto& s = *impl_;
    if (s.staged.empty()) throw std::logic_error("No staged recording frame to read back");
    ReadbackResult result;
    result.frame = acquire();
    if (!result.frame) return result;
    auto& slot = s.slots[s.staged.front()];
    D3D11_MAPPED_SUBRESOURCE mapped{};
    const auto started = std::chrono::steady_clock::now();
    HRESULT hr = S_OK;
    for (;;) {
        { Impl::Lock lock(s.multithread.Get()); hr = s.context->Map(slot.texture.Get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped); }
        if (hr != DXGI_ERROR_WAS_STILL_DRAWING) break;
        // Polling leaves the device lock free for capture while the copy runs.
        result.stalled = true;
        if (elapsed_ms(started) > 2000) throw std::runtime_error("Recording readback copy did not finish");
        s.pause(200);
    }
    result.map_wait_ms = elapsed_ms(started);
    if (FAILED(hr)) throw std::runtime_error(hresult("Map recording readback staging texture", hr));
    const auto copy_started = std::chrono::steady_clock::now();
    uint8_t* planes[4]{}; int pitches[4]{};
    for (auto& pitch : pitches) pitch = int(mapped.RowPitch);
    // The chroma plane follows the luma rows of the staging texture.
    const int filled = av_image_fill_pointers(planes, s.format, s.height, static_cast<uint8_t*>(mapped.pData), pitches);
    if (filled >= 0) {
        const uint8_t* source[4]{planes[0], planes[1], planes[2], planes[3]};
        av_image_copy(result.frame->data, result.frame->linesize, source, pitches, s.format, s.width, s.height);
    }
    { Impl::Lock lock(s.multithread.Get()); s.context->Unmap(slot.texture.Get(), 0); }
    if (filled < 0) throw std::runtime_error("Recording readback layout unavailable");
    result.copy_ms = elapsed_ms(copy_started);
    const int copied = av_frame_copy_props(result.frame.get(), slot.properties.get());
    av_frame_unref(slot.properties.get()); s.staged.pop_front();
    if (copied < 0) throw std::bad_alloc();
    return result;
}

void ReadbackStage::drop_oldest() {
    auto& s = *impl_;
    if (s.staged.empty()) return;
    av_frame_unref(s.slots[s.staged.front()].properties.get());
    s.staged.pop_front();
}
}
