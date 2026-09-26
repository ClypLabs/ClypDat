#include "recording_capture.h"
#include "captured_frames.h"
#include "cursor_compositor.h"
#include "output_orientation.h"
#include <Windows.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <dxgi1_5.h>
#include <dwmapi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <wrl/client.h>
#include <algorithm>
#include <condition_variable>
#include <cmath>
#include <cstring>
#include <deque>
#include <map>
#include <vector>
#include <mutex>
#include <stdexcept>
#include <thread>

namespace clypdat {
namespace {
using Microsoft::WRL::ComPtr;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
void checked(HRESULT hr, const char* operation) {
    if (FAILED(hr)) throw std::runtime_error(std::string(operation) + " HRESULT=" + std::to_string(uint32_t(hr)));
}
// The full-screen triangle and the SDR mapping of scRGB shared by the
// tone-mapping shaders; the scale for `white` is compiled in.
std::string tone_map_hlsl(float white) {
    return "struct V{float4 p:SV_Position;}; V VS(uint id:SV_VertexID){V o;o.p=float4(id==2?3:-1,id==1?3:-1,0,1);return o;}"
           "Texture2D<float4> Source:register(t0);float Srgb(float v){return v<=0.0031308?v*12.92:1.055*pow(v,1.0/2.4)-0.055;}"
           "float4 ToneMap(float3 c){float3 rgb=max(c,0)*" + std::to_string(80.f / (white > 0 && white < 10000 ? white : 80.f)) +
           ";float m=max(rgb.r,max(rgb.g,rgb.b));if(m>1)rgb/=m;return float4(saturate(Srgb(rgb.r)),saturate(Srgb(rgb.g)),saturate(Srgb(rgb.b)),1);}";
}
class Device {
public:
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Texture2D> staging;
    std::mutex mutex;
    ComPtr<ID3D11VertexShader> hdr_vertex, orient_vertex;
    ComPtr<ID3D11PixelShader> hdr_pixel, orient_pixel;
    ComPtr<ID3D11Buffer> orient_constants;
    ComPtr<ID3D11Multithread> multithread;
    RecordingSourceHealth diagnostics;
    explicit Device(ID3D11Device* existing=nullptr,bool debug=false) : device(existing) {
        if(existing)existing->GetImmediateContext(&context);
        else checked(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT|(debug?D3D11_CREATE_DEVICE_DEBUG:0),
            nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context), "Create recording D3D11 device");
        if (SUCCEEDED(context.As(&multithread))) multithread->SetMultithreadProtected(TRUE);
        ComPtr<IDXGIDevice> dxgi;ComPtr<IDXGIAdapter> adapter;
        if(SUCCEEDED(device.As(&dxgi))&&SUCCEEDED(dxgi->GetAdapter(&adapter))){DXGI_ADAPTER_DESC desc{};
            if(SUCCEEDED(adapter->GetDesc(&desc))){diagnostics.adapter=desc.Description;
                diagnostics.adapter_luid=(uint64_t(uint32_t(desc.AdapterLuid.HighPart))<<32)|desc.AdapterLuid.LowPart;}
            if(!existing){int priority=1;wchar_t value[16]{};
                const auto count=GetEnvironmentVariableW(L"CLYPDAT_GPU_DEVICE_PRIORITY",value,16);
                if(count>0&&count<16){wchar_t* end=nullptr;const auto parsed=wcstol(value,&end,10);if(end&&!*end&&parsed>=-7&&parsed<=7)priority=int(parsed);}
                if(SUCCEEDED(dxgi->SetGPUThreadPriority(priority))){diagnostics.gpu_device_priority_applied=true;diagnostics.gpu_device_priority=priority;
                    dxgi->GetGPUThreadPriority(&diagnostics.gpu_device_priority);}
            }
        }
    }
    // A new owned BGRA copy of `input`, tone-mapped when it is FP16.
    ComPtr<ID3D11Texture2D> copy(ID3D11Texture2D* input, float white) {
        D3D11_TEXTURE2D_DESC desc{}; input->GetDesc(&desc);
        const bool hdr = desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT;
        desc.Usage = D3D11_USAGE_DEFAULT; desc.CPUAccessFlags = 0; desc.MiscFlags = 0;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        if (hdr) desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        ComPtr<ID3D11Texture2D> owned;
        checked(device->CreateTexture2D(&desc, nullptr, &owned), "Allocate owned recording texture");
        if (!hdr) copy_into(owned.Get(), input, nullptr);
        else tone_map(input, owned.Get(), white);
        return owned;
    }
    // Copies `input`, or the `box` of it, to the origin of `output`.
    void copy_into(ID3D11Texture2D* output, ID3D11Texture2D* input, const D3D11_BOX* box) {
        std::lock_guard lock(mutex);
        if (box) context->CopySubresourceRegion(output, 0, 0, 0, 0, input, 0, box);
        else context->CopyResource(output, input);
    }
    // Tone-maps FP16 `input` into the same-size BGRA `output`. `view` may be a
    // cached render-target view of `output`; the input view is made per call
    // so no reference to the capture API's buffer outlives it.
    void tone_map(ID3D11Texture2D* input, ID3D11Texture2D* output, float white, ID3D11RenderTargetView* view = nullptr) {
        std::lock_guard lock(mutex);
        // The draw's pipeline state must not interleave with the encoder
        // thread's overlay draws on the same immediate context.
        struct Enter { ID3D11Multithread* p; explicit Enter(ID3D11Multithread* v) : p(v) { if (p) p->Enter(); } ~Enter() { if (p) p->Leave(); } } device_lock(multithread.Get());
        D3D11_TEXTURE2D_DESC desc{}; output->GetDesc(&desc);
        if (!hdr_vertex) {
            const std::string shader = tone_map_hlsl(white) + "float4 PS(V i):SV_Target{return ToneMap(Source.Load(int3(i.p.xy,0)).rgb);}";
            ComPtr<ID3DBlob> vs, ps, error;
            checked(D3DCompile(shader.data(), shader.size(), nullptr, nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vs, &error), "Compile recording HDR vertex shader");
            checked(D3DCompile(shader.data(), shader.size(), nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &ps, &error), "Compile recording HDR pixel shader");
            checked(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &hdr_vertex), "Create recording HDR vertex shader");
            checked(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &hdr_pixel), "Create recording HDR pixel shader");
        }
        ComPtr<ID3D11ShaderResourceView> source;
        ComPtr<ID3D11RenderTargetView> created;
        checked(device->CreateShaderResourceView(input, nullptr, &source), "Create recording HDR input view");
        if (!view) { checked(device->CreateRenderTargetView(output, nullptr, &created), "Create recording HDR output view"); view = created.Get(); }
        D3D11_VIEWPORT viewport{0,0,float(desc.Width),float(desc.Height),0,1};
        auto raw_target = view; auto raw_source = source.Get();
        context->OMSetRenderTargets(1, &raw_target, nullptr); context->RSSetViewports(1, &viewport);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(hdr_vertex.Get(), nullptr, 0); context->PSSetShader(hdr_pixel.Get(), nullptr, 0);
        context->PSSetShaderResources(0, 1, &raw_source); context->Draw(3, 0);
        raw_target = nullptr; raw_source = nullptr;
        context->OMSetRenderTargets(1, &raw_target, nullptr); context->PSSetShaderResources(0, 1, &raw_source);
    }
    // Draws `crop` of a rotated output's desktop (`desktop` in size) into the
    // crop-sized BGRA `output`, from `input` in the output's scanout
    // orientation: each output pixel loads the texel output_orientation.h
    // maps it to. BGRA texels are copied exactly; FP16 is tone-mapped as
    // tone_map() does. `view` may be a cached render-target view of `output`.
    void orient(ID3D11Texture2D* input, ID3D11Texture2D* output, OutputRotation rotation, OutputSize desktop, CaptureRect crop, float white,
                ID3D11RenderTargetView* view = nullptr) {
        std::lock_guard lock(mutex);
        struct Enter { ID3D11Multithread* p; explicit Enter(ID3D11Multithread* v) : p(v) { if (p) p->Enter(); } ~Enter() { if (p) p->Leave(); } } device_lock(multithread.Get());
        D3D11_TEXTURE2D_DESC desc{}; input->GetDesc(&desc);
        if (!orient_pixel) {
            const std::string shader = tone_map_hlsl(white) +
                "cbuffer Orientation:register(b0){int4 Crop;int4 Desktop;};"
                "float4 PS(V i):SV_Target{int2 d=int2(i.p.xy)+Crop.xy;int2 t=d;"
                "if(Crop.z==1)t=int2(d.y,Desktop.x-1-d.x);else if(Crop.z==2)t=int2(Desktop.x-1-d.x,Desktop.y-1-d.y);else if(Crop.z==3)t=int2(Desktop.y-1-d.y,d.x);"
                "float4 c=Source.Load(int3(t,0));return Desktop.z?ToneMap(c.rgb):c;}";
            ComPtr<ID3DBlob> vs, ps, error;
            checked(D3DCompile(shader.data(), shader.size(), nullptr, nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vs, &error), "Compile recording orientation vertex shader");
            checked(D3DCompile(shader.data(), shader.size(), nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &ps, &error), "Compile recording orientation pixel shader");
            checked(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &orient_vertex), "Create recording orientation vertex shader");
            checked(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &orient_pixel), "Create recording orientation pixel shader");
            const D3D11_BUFFER_DESC constants{32, D3D11_USAGE_DEFAULT, D3D11_BIND_CONSTANT_BUFFER, 0, 0, 0};
            checked(device->CreateBuffer(&constants, nullptr, &orient_constants), "Create recording orientation constants");
        }
        const int32_t values[8]{crop.x, crop.y, int32_t(rotation), 0, desktop.width, desktop.height, desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT, 0};
        context->UpdateSubresource(orient_constants.Get(), 0, nullptr, values, 0, 0);
        ComPtr<ID3D11ShaderResourceView> source;
        ComPtr<ID3D11RenderTargetView> created;
        checked(device->CreateShaderResourceView(input, nullptr, &source), "Create recording orientation input view");
        if (!view) { checked(device->CreateRenderTargetView(output, nullptr, &created), "Create recording orientation output view"); view = created.Get(); }
        const D3D11_VIEWPORT viewport{0, 0, float(crop.width), float(crop.height), 0, 1};
        auto raw_target = view; auto raw_source = source.Get(); auto raw_constants = orient_constants.Get();
        context->IASetInputLayout(nullptr); context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->OMSetRenderTargets(1, &raw_target, nullptr); context->OMSetBlendState(nullptr, nullptr, 0xffffffff);
        context->OMSetDepthStencilState(nullptr, 0); context->RSSetState(nullptr); context->RSSetViewports(1, &viewport);
        context->VSSetShader(orient_vertex.Get(), nullptr, 0); context->PSSetShader(orient_pixel.Get(), nullptr, 0);
        context->PSSetShaderResources(0, 1, &raw_source); context->PSSetConstantBuffers(0, 1, &raw_constants); context->Draw(3, 0);
        raw_target = nullptr; raw_source = nullptr; raw_constants = nullptr;
        context->OMSetRenderTargets(1, &raw_target, nullptr); context->PSSetShaderResources(0, 1, &raw_source); context->PSSetConstantBuffers(0, 1, &raw_constants);
    }
    bool read(ID3D11Texture2D* texture, CapturePixels& output, CaptureRect crop = {}) {
        std::lock_guard lock(mutex);
        D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
        if (desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) throw std::runtime_error("Unsupported capture texture format");
        if (crop.width <= 0 || crop.height <= 0) crop = {0, 0, int(desc.Width), int(desc.Height)};
        if (crop.x < 0 || crop.y < 0 || crop.x + crop.width > int(desc.Width) || crop.y + crop.height > int(desc.Height)) return false;
        D3D11_TEXTURE2D_DESC previous{}; if (staging) staging->GetDesc(&previous);
        if (!staging || previous.Width != UINT(crop.width) || previous.Height != UINT(crop.height)) {
            staging.Reset(); desc.Width = crop.width; desc.Height = crop.height; desc.MipLevels = 1; desc.ArraySize = 1;
            desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ; desc.MiscFlags = 0;
            checked(device->CreateTexture2D(&desc, nullptr, &staging), "Create recording readback");
        }
        D3D11_BOX box{UINT(crop.x), UINT(crop.y), 0, UINT(crop.x + crop.width), UINT(crop.y + crop.height), 1};
        context->CopySubresourceRegion(staging.Get(), 0, 0, 0, 0, texture, 0, &box);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        checked(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped), "Map recording readback");
        try {
            output.width = crop.width; output.height = crop.height; output.stride = crop.width * 4;
            output.bgra.resize(size_t(output.stride) * output.height);
            for (int row = 0; row < output.height; ++row)
                std::memcpy(output.bgra.data() + size_t(row) * output.stride, static_cast<const uint8_t*>(mapped.pData) + size_t(row) * mapped.RowPitch, output.stride);
        } catch (...) { context->Unmap(staging.Get(), 0); throw; }
        context->Unmap(staging.Get(), 0); return true;
    }
    void write(ID3D11Texture2D* texture, const CapturePixels& input, CaptureRect destination) {
        std::lock_guard lock(mutex);
        if (input.width != destination.width || input.height != destination.height ||
            input.stride < input.width * 4 || input.bgra.size() < size_t(input.stride) * input.height)
            throw std::runtime_error("Invalid cursor update pixels");
        D3D11_BOX box{UINT(destination.x), UINT(destination.y), 0,
            UINT(destination.x + destination.width), UINT(destination.y + destination.height), 1};
        context->UpdateSubresource(texture, 0, &box, input.bgra.data(), UINT(input.stride), 0);
    }
};
bool window_eligible(HWND hwnd, bool background_allowed) {
    if (!hwnd) return true;
    if (!IsWindow(hwnd) || IsIconic(hwnd) || !IsWindowVisible(hwnd)) return false;
    if (background_allowed) return true;
    return GetAncestor(GetForegroundWindow(), GA_ROOT) == GetAncestor(hwnd, GA_ROOT);
}
struct DisplayProfile{bool available=false,hdr=false;float white=80;};
DisplayProfile display_profile(HMONITOR monitor){
    MONITORINFOEXW info{};info.cbSize=sizeof(info);if(!GetMonitorInfoW(monitor,&info))return{};
    for(int attempt=0;attempt<3;++attempt){
        UINT32 paths_count=0,modes_count=0;if(GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS,&paths_count,&modes_count)!=ERROR_SUCCESS)return{};
        std::vector<DISPLAYCONFIG_PATH_INFO> paths(paths_count);std::vector<DISPLAYCONFIG_MODE_INFO> modes(modes_count);
        const auto result=QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS,&paths_count,paths.data(),&modes_count,modes.data(),nullptr);
        if(result==ERROR_INSUFFICIENT_BUFFER)continue;if(result!=ERROR_SUCCESS)return{};
        for(UINT32 i=0;i<paths_count;++i){
            DISPLAYCONFIG_SOURCE_DEVICE_NAME source{};source.header.type=DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;source.header.size=sizeof(source);
            source.header.adapterId=paths[i].sourceInfo.adapterId;source.header.id=paths[i].sourceInfo.id;
            if(DisplayConfigGetDeviceInfo(&source.header)!=ERROR_SUCCESS||_wcsicmp(source.viewGdiDeviceName,info.szDevice))continue;
            DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO color{};color.header.type=DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO;color.header.size=sizeof(color);
            color.header.adapterId=paths[i].targetInfo.adapterId;color.header.id=paths[i].targetInfo.id;
            if(DisplayConfigGetDeviceInfo(&color.header)!=ERROR_SUCCESS)return{};
            DisplayProfile profile;profile.available=true;profile.hdr=color.advancedColorEnabled!=0&&(color.value&4)==0;
            DISPLAYCONFIG_SDR_WHITE_LEVEL white{};white.header.type=DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL;white.header.size=sizeof(white);
            white.header.adapterId=paths[i].targetInfo.adapterId;white.header.id=paths[i].targetInfo.id;
            if(DisplayConfigGetDeviceInfo(&white.header)==ERROR_SUCCESS&&white.SDRWhiteLevel>0)profile.white=80.f*white.SDRWhiteLevel/1000.f;
            return profile;
        }
        return{};
    }
    return{};
}
// The monitor-relative capture region WGC frames are cropped to; empty for
// windows and whole monitors.
CaptureRect wgc_region(const RecordingCaptureConfig& config) {
    if (config.window || config.capture_region.width <= 0 || config.capture_region.height <= 0) return {};
    auto monitor = reinterpret_cast<HMONITOR>(config.monitor);
    if (!monitor) monitor = MonitorFromPoint(POINT{}, MONITOR_DEFAULTTOPRIMARY);
    MONITORINFO info{sizeof(MONITORINFO)};
    RECT desktop{}; if (GetMonitorInfoW(monitor, &info)) desktop = info.rcMonitor;
    return {config.capture_region.x - desktop.left, config.capture_region.y - desktop.top, config.capture_region.width, config.capture_region.height};
}
class WgcSource final : public RecordingFrameSource {
    struct Shared {
        Device gpu;
        // Pooled owned copies; the WGC buffer is released as soon as its copy is queued.
        CapturedFrameStore frames;
        Shared(ID3D11Device* device, bool debug, int capacity, float white, CaptureRect region)
            : gpu(device, debug), frames(gpu.device.Get(), capacity, white, region) {}
        std::mutex mutex;
        RecordingSourceHealth diagnostics;
    };
    RecordingCaptureConfig config_;
    std::shared_ptr<Shared> shared_;
    IDirect3DDevice direct_device_{nullptr};
    GraphicsCaptureItem item_{nullptr};
    Direct3D11CaptureFramePool pool_{nullptr};
    GraphicsCaptureSession session_{nullptr};
    winrt::event_token arrived_{}, closed_{};
public:
    explicit WgcSource(const RecordingCaptureConfig& config,ID3D11Device* existing=nullptr) : config_(config),
        shared_(std::make_shared<Shared>(existing, config.d3d_debug, capture_source_texture_capacity(config.source_queue_depth), config.sdr_white_nits, wgc_region(config))) {
        // WinRT capture is agile; initialization may already belong to the
        // native worker. RPC_E_CHANGED_MODE is harmless for this API.
        const HRESULT init = RoInitialize(RO_INIT_MULTITHREADED);
        if (FAILED(init) && init != RPC_E_CHANGED_MODE) checked(init, "Initialize recording WinRT");
        if (!GraphicsCaptureSession::IsSupported()) throw std::runtime_error("Windows Graphics Capture unavailable");
        ComPtr<IDXGIDevice> dxgi; checked(shared_->gpu.device.As(&dxgi), "Query capture DXGI device");
        winrt::com_ptr<IInspectable> inspectable;
        checked(CreateDirect3D11DeviceFromDXGIDevice(dxgi.Get(), inspectable.put()), "Create capture WinRT device");
        direct_device_ = inspectable.as<IDirect3DDevice>();
        auto interop = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        if (config.window) checked(interop->CreateForWindow(reinterpret_cast<HWND>(config.window),
            winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item_)), "Create window capture item");
        else {
            auto monitor = reinterpret_cast<HMONITOR>(config.monitor);
            if (!monitor) monitor = MonitorFromPoint(POINT{}, MONITOR_DEFAULTTOPRIMARY);
            checked(interop->CreateForMonitor(monitor, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item_)), "Create monitor capture item");
        }
        const auto size = item_.Size();
        if (size.Width <= 0 || size.Height <= 0) throw std::runtime_error("Capture target has no pixels");
        const auto format = config.capture_hdr ? DirectXPixelFormat::R16G16B16A16Float : DirectXPixelFormat::B8G8R8A8UIntNormalized;
        pool_ = Direct3D11CaptureFramePool::CreateFreeThreaded(direct_device_, format, 3, size);
        auto shared = shared_; auto direct = direct_device_; const bool dwm_timing = config.wgc_dwm_timing;
        arrived_ = pool_.FrameArrived([shared, direct, format, dwm_timing](const Direct3D11CaptureFramePool& pool, auto&&) {
            const auto entered = std::chrono::steady_clock::now();
            CapturedFrameStore::Timing timing; LARGE_INTEGER qpc{}; QueryPerformanceCounter(&qpc); timing.callback_qpc = qpc.QuadPart;
            if (dwm_timing) { DWM_TIMING_INFO dwm{}; dwm.cbSize = sizeof(dwm);
                if (SUCCEEDED(DwmGetCompositionTimingInfo(nullptr, &dwm))) { timing.dwm_vblank_qpc = int64_t(dwm.qpcVBlank); timing.dwm_compose_qpc = int64_t(dwm.qpcCompose); } }
            struct Timed { Shared& shared; std::chrono::steady_clock::time_point entered;
                ~Timed() { const auto us = uint64_t(std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now() - entered).count());
                    std::lock_guard lock(shared.mutex); shared.diagnostics.callback_us += us; } } timed{*shared, entered};
            try {
                {std::lock_guard lock(shared->mutex);++shared->diagnostics.callbacks;}
                auto frame = pool.TryGetNextFrame();
                QueryPerformanceCounter(&qpc); timing.taken_qpc = qpc.QuadPart;
                while (frame) {
                    const auto content = frame.ContentSize();
                    const auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
                    ComPtr<ID3D11Texture2D> texture;
                    checked(access->GetInterface(IID_PPV_ARGS(&texture)), "Read WGC texture");
                    D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
                    if (content.Width != int(desc.Width) || content.Height != int(desc.Height)) {
                        {std::lock_guard lock(shared->mutex);++shared->diagnostics.resizes;}
                        frame.Close();
                        if (content.Width > 0 && content.Height > 0)
                            pool.Recreate(direct, format, 3, content);
                        break;
                    }
                    // The copy goes to a pooled texture (none free: counted
                    // and dropped), so the WGC buffer is released at once.
                    // SystemRelativeTime: 100 ns QPC time of the vblank the
                    // composition is shown at (after this callback runs).
                    const auto timestamp = frame.SystemRelativeTime().count() / 10;
                    shared->frames.deliver(texture.Get(), timestamp, timing);
                    texture.Reset(); frame.Close();
                    frame = pool.TryGetNextFrame();
                }
            } catch (const std::exception& e) {
                shared->frames.fail(e.what());
            } catch (...) {
                shared->frames.fail("Windows Graphics Capture frame callback failed");
            }
        });
        closed_ = item_.Closed([shared](auto&&, auto&&) { shared->frames.close(); });
        session_ = pool_.CreateCaptureSession(item_);
        try { session_.IsCursorCaptureEnabled(config.capture_cursor); } catch (...) {}
        try { session_.IsBorderRequired(false); } catch (...) {}
        set_frame_rate(config.fps); session_.StartCapture();
    }
    ~WgcSource() override { stop(); }
    void stop() override {
        try { if (pool_) pool_.FrameArrived(arrived_); if (item_) item_.Closed(closed_);
            if (session_) session_.Close(); if (pool_) pool_.Close();
            session_=nullptr;pool_=nullptr;item_=nullptr; } catch (...) {}
    }
    bool eligible() const override {
        return !shared_->frames.closed() && window_eligible(reinterpret_cast<HWND>(config_.window), true);
    }
    bool foreground() const override {return window_eligible(reinterpret_cast<HWND>(config_.window),false);}
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
        // The store already cropped a capture region; the pooled copy returns
        // to the pool when the last CapturePixels owner releases it.
        int64_t stamp = 0; CapturedFrameStore::Timing timing;
        if (!shared_->frames.take(pixels, stamp, timeout, &timing)) return false;
        if (!eligible()) { pixels.texture.reset(); return false; }
        const auto a = config_.qpc_anchor, f = config_.qpc_frequency, m = config_.monotonic_anchor_us;
        pixels.timestamp_us = capture_qpc_us_to_us(stamp, a, f, m);
        auto at = [&](int64_t qpc) { return qpc ? capture_qpc_to_us(qpc, a, f, m) : 0; };
        pixels.timing.callback_us = at(timing.callback_qpc); pixels.timing.taken_us = at(timing.taken_qpc); pixels.timing.published_us = at(timing.published_qpc);
        pixels.timing.dwm_vblank_us = at(timing.dwm_vblank_qpc); pixels.timing.dwm_compose_us = at(timing.dwm_compose_qpc);
        return true;
    }
    void set_frame_rate(int fps) override {
        if (!session_) return;
        auto monitor = reinterpret_cast<HMONITOR>(config_.monitor);
        if (config_.window) monitor=MonitorFromWindow(reinterpret_cast<HWND>(config_.window),MONITOR_DEFAULTTONEAREST);
        if (!monitor) monitor=MonitorFromPoint(POINT{},MONITOR_DEFAULTTOPRIMARY);
        MONITORINFOEXW info{};info.cbSize=sizeof(info); DEVMODEW mode{};mode.dmSize=sizeof(mode);double refresh=0;
        if(GetMonitorInfoW(monitor,&info)&&EnumDisplaySettingsW(info.szDevice,ENUM_CURRENT_SETTINGS,&mode))refresh=mode.dmDisplayFrequency;
        // Follows the active rate on every call; fixed only for benchmarks.
        const bool fixed=config_.wgc_update_ticks>0&&refresh>0;
        const int ticks=fixed?config_.wgc_update_ticks:capture_wgc_update_ticks(fps,refresh);
        const auto interval=fixed?int64_t(std::llround(10000000.0/refresh*(ticks-.5))):capture_wgc_interval_100ns(fps,refresh);
        {std::lock_guard lock(shared_->mutex);auto& d=shared_->diagnostics;d.requested_interval_100ns=interval;d.display_refresh_hz=refresh;
            d.cadence_fps=fps;d.update_ticks=ticks;d.producer_ceiling_fps=ticks?refresh/ticks:1e7/double(interval);}
        // This interface is optional on older supported Windows builds.
        try { session_.MinUpdateInterval(std::chrono::duration<int64_t, std::ratio<1, 10000000>>(interval));
            const auto applied=session_.MinUpdateInterval().count();
            std::lock_guard lock(shared_->mutex);auto& d=shared_->diagnostics;d.applied_interval_100ns=applied;d.update_interval_available=true;d.cadence_mode=fixed?"fixed":"safe";
        } catch (...) {
            std::lock_guard lock(shared_->mutex);auto& d=shared_->diagnostics;d.applied_interval_100ns=0;d.update_interval_available=false;
            d.cadence_mode="unavailable";d.producer_ceiling_fps=refresh;
        }
    }
    const char* name() const override { return "Windows Graphics Capture"; }
    ID3D11Device* d3d_device() const override { return shared_->gpu.device.Get(); }
    CaptureRect content_bounds() const override {
        if(!config_.window&&config_.capture_region.width>0&&config_.capture_region.height>0)return{0,0,config_.capture_region.width,config_.capture_region.height};
        const auto size=item_.Size();return{0,0,size.Width,size.Height};
    }
    RecordingSourceHealth diagnostics() const override {
        const auto frames=shared_->frames.stats();
        std::lock_guard lock(shared_->mutex);auto result=shared_->diagnostics;
        result.frames_delivered=frames.delivered;result.overwritten=frames.superseded;
        result.owned_texture_capacity=frames.capacity;result.owned_textures_allocated=frames.allocated;result.owned_textures_leased=frames.leased;
        result.owned_textures_peak=frames.peak_leased;result.owned_texture_pressure_drops=frames.pressure_drops;
        result.copy_p50_ms=frames.copy_p50_ms;result.copy_p95_ms=frames.copy_p95_ms;
        result.adapter=shared_->gpu.diagnostics.adapter;result.adapter_luid=shared_->gpu.diagnostics.adapter_luid;
        result.gpu_device_priority=shared_->gpu.diagnostics.gpu_device_priority;result.gpu_device_priority_applied=shared_->gpu.diagnostics.gpu_device_priority_applied;
        result.display_profile_available=config_.display_profile_available;result.hdr_display=config_.display_hdr;
        result.hdr_conversion=config_.capture_hdr;result.sdr_white_nits=config_.sdr_white_nits;return result;
    }
    bool recover() override {
        try {
            if(!eligible()||!pool_||!item_)return false;
            const auto size=item_.Size();if(size.Width<=0||size.Height<=0)return false;
            pool_.Recreate(direct_device_,config_.capture_hdr?DirectXPixelFormat::R16G16B16A16Float:DirectXPixelFormat::B8G8R8A8UIntNormalized,3,size);
            shared_->frames.reset();return true;
        }catch(...){return false;}
    }
};

class DxgiSource final : public RecordingFrameSource {
    RecordingCaptureConfig config_;
    Device gpu_;
    ComPtr<IDXGIOutputDuplication> duplication_;
    RECT desktop_{};
    // Frames come in the output's scanout orientation; crops, the cursor
    // and everything downstream are in desktop orientation.
    OutputRotation rotation_ = OutputRotation::Identity;
    CaptureRect stable_{}, candidate_{};
    int crop_samples_ = 0;
    // Access lost to a mode, rotation or desktop change: reopened on later
    // acquisitions, an error only after 5 s.
    CaptureReopener reopener_{std::chrono::seconds(5)};
    double cursor_composition_ms_ = 0;
    uint64_t cursor_composition_samples_ = 0;
    // Owned copies come from a bounded pool, as for WGC; the reference path
    // allocates each one.
    CapturedFrameStore frames_;
    std::unique_ptr<CursorCompositor> cursor_;
    uint64_t reference_textures_ = 0;
    // Frame polling: AcquireNextFrame is only ever called with a zero timeout.
    HANDLE timer_ = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
    std::chrono::steady_clock::time_point last_frame_{};
    std::chrono::microseconds refresh_period_{4167};
    // A blocking AcquireNextFrame holds the device's multithread lock while
    // it waits, stalling the encoder thread for milliseconds per frame.
    // Poll instead: first just before the next refresh is due, then every
    // millisecond, and every 2 ms once the display has been quiet for 50 ms.
    HRESULT next_frame(std::chrono::milliseconds timeout, DXGI_OUTDUPL_FRAME_INFO& info, ComPtr<IDXGIResource>& resource) {
        if (config_.dxgi_reference_path) return duplication_->AcquireNextFrame(DWORD(timeout.count()), &info, &resource);
        using namespace std::chrono;
        const auto deadline = steady_clock::now() + timeout;
        for (;;) {
            const auto result = duplication_->AcquireNextFrame(0, &info, &resource);
            const auto now = steady_clock::now();
            if (result != DXGI_ERROR_WAIT_TIMEOUT) { if (SUCCEEDED(result)) last_frame_ = now; return result; }
            if (now >= deadline) return result;
            auto next = now + (now - last_frame_ > milliseconds(50) ? milliseconds(2) : milliseconds(1));
            next = std::min(std::max(next, last_frame_ + refresh_period_ * 3 / 4), deadline);
            const auto wait = duration_cast<microseconds>(next - now).count();
            LARGE_INTEGER due{}; due.QuadPart = -std::max<int64_t>(wait, 100) * 10;
            if (timer_ && SetWaitableTimer(timer_, &due, 0, nullptr, nullptr, FALSE)) WaitForSingleObject(timer_, 50);
            else std::this_thread::sleep_for(microseconds(wait));
        }
    }
public:
    explicit DxgiSource(const RecordingCaptureConfig& config,ID3D11Device* existing=nullptr) : config_(config),gpu_(existing,config.d3d_debug),
        frames_(gpu_.device.Get(),capture_source_texture_capacity(config.source_queue_depth),config.sdr_white_nits) {
        if(config_.capture_cursor)cursor_=std::make_unique<CursorCompositor>(gpu_.device.Get());
        open();
    }
    void open() {
        ComPtr<IDXGIDevice> device; checked(gpu_.device.As(&device), "Query duplication device");
        ComPtr<IDXGIAdapter> adapter; checked(device->GetAdapter(&adapter), "Query duplication adapter");
        auto target = reinterpret_cast<HMONITOR>(config_.monitor);
        if (config_.window) target = MonitorFromWindow(reinterpret_cast<HWND>(config_.window), MONITOR_DEFAULTTONEAREST);
        if (!target) target = MonitorFromPoint(POINT{}, MONITOR_DEFAULTTOPRIMARY);
        for (UINT i = 0;; ++i) {
            ComPtr<IDXGIOutput> output;
            if (adapter->EnumOutputs(i, &output) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_OUTPUT_DESC desc{}; checked(output->GetDesc(&desc), "Read duplication output");
            if (desc.Monitor != target) continue;
            ComPtr<IDXGIOutput1> output1; checked(output.As(&output1), "Query desktop duplication");
            duplication_.Reset();
            ComPtr<IDXGIOutput5> output5;
            if(config_.capture_hdr&&SUCCEEDED(output.As(&output5))){
                const DXGI_FORMAT formats[]{DXGI_FORMAT_R16G16B16A16_FLOAT,DXGI_FORMAT_B8G8R8A8_UNORM};
                checked(output5->DuplicateOutput1(gpu_.device.Get(),0,2,formats,&duplication_),"Create HDR desktop duplication");
            }else checked(output1->DuplicateOutput(gpu_.device.Get(), &duplication_), "Create desktop duplication");
            desktop_ = desc.DesktopCoordinates;
            DXGI_OUTDUPL_DESC duplicated{}; duplication_->GetDesc(&duplicated); rotation_ = output_rotation(int(duplicated.Rotation));
            MONITORINFOEXW monitor{}; monitor.cbSize = sizeof(monitor); DEVMODEW mode{}; mode.dmSize = sizeof(mode);
            if (GetMonitorInfoW(desc.Monitor, &monitor) && EnumDisplaySettingsW(monitor.szDevice, ENUM_CURRENT_SETTINGS, &mode) && mode.dmDisplayFrequency > 1)
                refresh_period_ = std::chrono::microseconds(1000000 / mode.dmDisplayFrequency);
            return;
        }
        throw std::runtime_error("Capture display unavailable on recording adapter");
    }
    void lose() { duplication_.Reset(); reopener_.lost(std::chrono::steady_clock::now()); }
    bool eligible() const override { return window_eligible(reinterpret_cast<HWND>(config_.window), false); }
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
        if (!eligible()) return false;
        const auto reopen = [&] {
            if (reopener_.attempt([this] { open(); }, std::chrono::steady_clock::now())) return true;
            std::this_thread::sleep_for(std::min(timeout, std::chrono::milliseconds(20))); return false;
        };
        // No duplication (a failed replacement's recover() could not reopen
        // it): reopened like a lost one.
        if (!duplication_ && !reopener_.pending()) reopener_.lost(std::chrono::steady_clock::now());
        if (reopener_.pending() && !reopen()) return false;
        DXGI_OUTDUPL_FRAME_INFO info{}; ComPtr<IDXGIResource> resource;
        const auto result = next_frame(timeout, info, resource);
        if (result == DXGI_ERROR_WAIT_TIMEOUT) return false;
        // Turning a display, AcquireNextFrame was measured failing with
        // INVALID_CALL rather than ACCESS_LOST: the loss can land on the held
        // frame's ReleaseFrame instead. Either way the duplication is gone and
        // is reopened.
        if (result == DXGI_ERROR_ACCESS_LOST || result == DXGI_ERROR_INVALID_CALL) { lose(); reopen(); return false; }
        checked(result, "Acquire desktop recording frame");
        struct Release { DxgiSource& s; ~Release() { if (s.duplication_ && s.duplication_->ReleaseFrame() == DXGI_ERROR_ACCESS_LOST) s.lose(); } } release{*this};
        if (!eligible()) return false;
        if(!info.LastPresentTime.QuadPart&&!config_.capture_cursor)return false;
        ++gpu_.diagnostics.frames_delivered;
        ComPtr<ID3D11Texture2D> texture; checked(resource.As(&texture), "Query desktop frame texture");
        CaptureRect crop;
        if (config_.window) {
            RECT rect{}; const auto hwnd = reinterpret_cast<HWND>(config_.window);
            if (!GetClientRect(hwnd, &rect)) return false;
            POINT origin{}; if (!ClientToScreen(hwnd, &origin)) return false;
            crop = {origin.x - desktop_.left, origin.y - desktop_.top, rect.right, rect.bottom};
            if (crop.width!=candidate_.width||crop.height!=candidate_.height) { candidate_ = crop; crop_samples_ = 1; }
            else ++crop_samples_;
            if(stable_.width==0)stable_=crop;
            if(crop.width!=stable_.width||crop.height!=stable_.height){
                if(crop_samples_<3)return false;
                stable_=crop;crop_samples_=0;
            }
        }else if(config_.capture_region.width>0&&config_.capture_region.height>0)crop={config_.capture_region.x-desktop_.left,config_.capture_region.y-desktop_.top,config_.capture_region.width,config_.capture_region.height};
        if (!eligible()) return false;
        const auto stamp = info.LastPresentTime.QuadPart ? info.LastPresentTime.QuadPart : info.LastMouseUpdateTime.QuadPart;
        pixels.timestamp_us = capture_qpc_to_us(stamp, config_.qpc_anchor, config_.qpc_frequency, config_.monotonic_anchor_us);
        D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
        // The crop is in desktop orientation; so is the size it must fit.
        const auto desktop = output_desktop_size({int(desc.Width), int(desc.Height)}, rotation_);
        if (crop.width <= 0 || crop.height <= 0) crop = {0, 0, desktop.width, desktop.height};
        if (!output_contains(crop, desktop)) return false;
        if (config_.dxgi_reference_path) {
            const bool hdr = desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT;
            desc.Width = crop.width; desc.Height = crop.height; desc.Usage = D3D11_USAGE_DEFAULT;
            desc.CPUAccessFlags = 0; desc.MiscFlags = 0; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
            if (rotation_ != OutputRotation::Identity) desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            ComPtr<ID3D11Texture2D> owned;
            checked(gpu_.device->CreateTexture2D(&desc, nullptr, &owned), "Allocate owned desktop frame"); ++reference_textures_;
            if (rotation_ != OutputRotation::Identity) gpu_.orient(texture.Get(), owned.Get(), rotation_, desktop, crop, config_.sdr_white_nits);
            else {
                D3D11_BOX box{UINT(crop.x),UINT(crop.y),0,UINT(crop.x+crop.width),UINT(crop.y+crop.height),1};
                gpu_.context->CopySubresourceRegion(owned.Get(),0,0,0,0,texture.Get(),0,&box);
                if(hdr){owned=gpu_.copy(owned.Get(),config_.sdr_white_nits);++reference_textures_;}
            }
            pixels.texture = std::shared_ptr<ID3D11Texture2D>(owned.Detach(), [](auto* p){p->Release();});
        } else {
            // Copied (and turned upright) into a pooled texture before
            // ReleaseFrame; with every pooled texture still held downstream
            // the frame is dropped and counted rather than allocating another.
            pixels.texture = frames_.copy(texture.Get(), crop, rotation_);
            if (!pixels.texture) return false;
        }
        pixels.width = crop.width; pixels.height = crop.height; pixels.stride = crop.width * 4;
        if (cursor_) {
            const auto cursor_started = std::chrono::steady_clock::now();
            const auto state = capture_cursor_state();
            if (config_.dxgi_reference_path) cursor_->draw_cpu(pixels.texture.get(), pixels.width, pixels.height, desktop_.left + crop.x, desktop_.top + crop.y, state);
            else cursor_->draw(pixels.texture.get(), pixels.width, pixels.height, desktop_.left + crop.x, desktop_.top + crop.y, state);
            cursor_composition_ms_ += std::chrono::duration<double, std::milli>(
                std::chrono::steady_clock::now() - cursor_started).count();
            ++cursor_composition_samples_;
        }
        return true;
    }
    const char* name() const override { return "DXGI Desktop Duplication"; }
    ID3D11Device* d3d_device() const override { return gpu_.device.Get(); }
    ~DxgiSource() override { if (timer_) CloseHandle(timer_); }
    void stop() override { duplication_.Reset(); }
    RecordingSourceHealth diagnostics() const override {
        auto result=gpu_.diagnostics;result.display_profile_available=config_.display_profile_available;
        result.duplication_reopens = reopener_.reopens; result.duplication_reopen_failures = reopener_.failures;
        result.cursor_composition_ms = cursor_composition_samples_
            ? cursor_composition_ms_ / double(cursor_composition_samples_) : 0;
        const auto frames=frames_.stats();
        result.owned_texture_capacity=frames.capacity;result.owned_textures_allocated=frames.allocated+reference_textures_;result.owned_textures_leased=frames.leased;
        result.owned_textures_peak=frames.peak_leased;result.owned_texture_pressure_drops=frames.pressure_drops;
        result.copy_p50_ms=frames.copy_p50_ms;result.copy_p95_ms=frames.copy_p95_ms;
        if(cursor_){const auto c=cursor_->stats();
            result.cursor_gpu_draws=c.gpu_draws;result.cursor_cpu_draws=c.cpu_draws;result.cursor_cpu_fallbacks=c.cpu_fallbacks;
            result.cursor_shape_changes=c.shape_changes;result.cursor_uploads=c.uploads;result.cursor_textures_created=c.textures_created;
            result.cursor_readback_bytes=c.readback_bytes;result.cursor_compose_p50_ms=c.compose_p50_ms;result.cursor_compose_p95_ms=c.compose_p95_ms;
            result.cursor_lock_wait_p95_ms=c.lock_wait_p95_ms;}
        result.hdr_display=config_.display_hdr;result.hdr_conversion=config_.capture_hdr;result.sdr_white_nits=config_.sdr_white_nits;return result;
    }
    bool recover() override { try{open();reopener_.reset();return true;}catch(...){return false;} }
    CaptureRect content_bounds() const override {
        if(config_.window){RECT rect{};if(GetClientRect(reinterpret_cast<HWND>(config_.window),&rect))return{0,0,rect.right,rect.bottom};}
        if(config_.capture_region.width>0&&config_.capture_region.height>0)return{0,0,config_.capture_region.width,config_.capture_region.height};
        return{0,0,desktop_.right-desktop_.left,desktop_.bottom-desktop_.top};
    }
};
class AdaptiveSource final:public RecordingFrameSource{
    RecordingCaptureConfig config_;
    std::unique_ptr<RecordingFrameSource> active_;
    bool hdr_requested_=false;
    HMONITOR monitored_=nullptr;
    CaptureReopener reprofile_{std::chrono::seconds(10)};
    HMONITOR current_monitor()const{
        if(config_.window)return MonitorFromWindow(reinterpret_cast<HWND>(config_.window),MONITOR_DEFAULTTONEAREST);
        if(config_.monitor)return reinterpret_cast<HMONITOR>(config_.monitor);
        return MonitorFromPoint(POINT{},MONITOR_DEFAULTTOPRIMARY);
    }
public:
    AdaptiveSource(RecordingCaptureConfig config,std::unique_ptr<RecordingFrameSource> active,bool hdr_requested):
        config_(std::move(config)),active_(std::move(active)),hdr_requested_(hdr_requested){monitored_=current_monitor();}
    bool acquire(CapturePixels& frame,std::chrono::milliseconds timeout)override{return active_->acquire(frame,timeout);}
    bool eligible()const override{return active_->eligible();}
    bool foreground()const override{return active_->foreground();}
    const char* name()const override{return active_->name();}
    ID3D11Device* d3d_device()const override{return active_->d3d_device();}
    void set_frame_rate(int fps)override{
        config_.fps=fps;
        const auto monitor=current_monitor();const auto profile=display_profile(monitor);
        if(profile.available&&(profile.hdr!=config_.display_hdr||std::abs(profile.white-config_.sdr_white_nits)>.01f||monitor!=monitored_)){
            const bool wgc=std::string_view(active_->name())=="Windows Graphics Capture";
            auto previous=config_;config_.display_hdr=profile.hdr;config_.capture_hdr=hdr_requested_&&profile.hdr;
            config_.display_profile_available=true;config_.sdr_white_nits=profile.white;
            // A display mid mode change (HDR switched on or off) can refuse
            // a new duplication for a moment. The profile is re-read every
            // second, so a failed switch is tried again then; only a change
            // that cannot be followed for 10 s ends capture. An ineligible
            // Desktop Duplication window waits without a deadline.
            if(!wgc&&!active_->foreground()){config_=std::move(previous);reprofile_.reset();active_->set_frame_rate(fps);return;}
            if(!reprofile_.pending())reprofile_.lost(std::chrono::steady_clock::now());
            bool switched=false;
            try{
                switched=reprofile_.attempt([&]{
                    if(!switch_backend(wgc))throw std::runtime_error("Recording display changed and capture recreation failed; restart worker");
                },std::chrono::steady_clock::now());
            }catch(...){config_=std::move(previous);throw;}
            if(!switched){config_=std::move(previous);active_->set_frame_rate(fps);return;}
            monitored_=monitor;
        }
        active_->set_frame_rate(fps);
    }
    void stop()override{active_->stop();}
    RecordingSourceHealth diagnostics()const override{auto result=active_->diagnostics();result.profile_switch_failures=reprofile_.failures;return result;}
    CaptureRect content_bounds()const override{return active_->content_bounds();}
    bool recover()override{
        if(active_->recover())return true;
        return switch_backend(std::string_view(active_->name())!="Windows Graphics Capture");
    }
    bool switch_backend(bool wgc)override{
        if(!wgc&&!active_->foreground())return false;
        // A process may duplicate an output only once, so a Desktop
        // Duplication source is released before its replacement opens, and
        // reopened if the replacement fails.
        const bool dxgi_to_dxgi=!wgc&&std::string_view(active_->name())=="DXGI Desktop Duplication";
        if(dxgi_to_dxgi)active_->stop();
        try{
            auto* device=active_->d3d_device();std::unique_ptr<RecordingFrameSource> replacement;
            if(wgc)replacement=std::make_unique<WgcSource>(config_,device);
            else replacement=std::make_unique<DxgiSource>(config_,device);
            active_->stop();active_=std::move(replacement);return true;
        }catch(...){if(dxgi_to_dxgi)active_->recover();return false;}
    }
};
}

struct CapturedFrameStore::Impl : std::enable_shared_from_this<CapturedFrameStore::Impl> {
    Device gpu;
    const int capacity;
    const float white;
    const CaptureRect region;
    Impl(ID3D11Device* device, int size, float nits, CaptureRect crop) : gpu(device), capacity(size), white(nits), region(crop) {}
    // Pool and newest slot.
    mutable std::mutex mutex;
    std::condition_variable changed;
    UINT width = 0, height = 0;
    uint64_t generation = 0;
    int live = 0, leased = 0, peak = 0; // live: textures of the current size.
    std::vector<ComPtr<ID3D11Texture2D>> free;
    uint64_t allocated = 0, delivered = 0, superseded = 0, pressure = 0;
    std::shared_ptr<ID3D11Texture2D> newest;
    int64_t stamp = 0;
    CapturedFrameStore::Timing newest_timing;
    bool closed = false;
    std::string error;
    std::deque<double> copy_times;
    // Delivery-only state, serialized by `delivering`.
    std::mutex delivering;
    ComPtr<ID3D11Texture2D> scratch; // Full-size BGRA target for cropped HDR frames.
    std::map<ID3D11Texture2D*, ComPtr<ID3D11RenderTargetView>> views; // Of pooled textures and the scratch.

    // A texture of the current size, or null at capacity. `mutex` held. The
    // lease hands the texture back when its last owner releases it.
    std::shared_ptr<ID3D11Texture2D> lease() {
        ComPtr<ID3D11Texture2D> texture;
        if (!free.empty()) { texture = std::move(free.back()); free.pop_back(); }
        else if (live < capacity) {
            D3D11_TEXTURE2D_DESC desc{};
            desc.Width = width; desc.Height = height; desc.MipLevels = 1; desc.ArraySize = 1;
            desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_DEFAULT;
            desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
            checked(gpu.device->CreateTexture2D(&desc, nullptr, &texture), "Allocate owned recording texture");
            ++live; ++allocated;
        } else return {};
        ++leased; peak = std::max(peak, leased);
        const std::weak_ptr<Impl> owner = weak_from_this();
        const auto from = generation;
        return std::shared_ptr<ID3D11Texture2D>(texture.Detach(), [owner, from](ID3D11Texture2D* returned) {
            if (const auto impl = owner.lock()) impl->give_back(returned, from); else returned->Release();
        });
    }
    void give_back(ID3D11Texture2D* returned, uint64_t from) {
        ComPtr<ID3D11Texture2D> texture; texture.Attach(returned);
        std::lock_guard lock(mutex);
        --leased;
        // Textures of an earlier size are discarded.
        if (from == generation) free.push_back(std::move(texture));
    }
    ID3D11RenderTargetView* view(ID3D11Texture2D* texture) {
        auto& cached = views[texture];
        if (!cached) checked(gpu.device->CreateRenderTargetView(texture, nullptr, &cached), "Create recording HDR output view");
        return cached.Get();
    }
    // Resizes the pool for `crop` when needed. `mutex` held; returns the
    // superseded newest frame, released by the caller outside the lock.
    std::shared_ptr<ID3D11Texture2D> resize(const CaptureRect& crop) {
        if (UINT(crop.width) == width && UINT(crop.height) == height) return {};
        width = UINT(crop.width); height = UINT(crop.height); ++generation;
        free.clear(); live = 0; views.clear();
        return std::move(newest);
    }
    // Copies, or tone-maps from FP16, `crop` of `input` into `target`.
    // `delivering` held. Returns the CPU time spent issuing it.
    double fill(ID3D11Texture2D* target, ID3D11Texture2D* input, const D3D11_TEXTURE2D_DESC& desc, const CaptureRect& crop) {
        const bool hdr = desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT;
        const bool whole = crop.width == int(desc.Width) && crop.height == int(desc.Height);
        const auto started = std::chrono::steady_clock::now();
        const D3D11_BOX box{UINT(crop.x), UINT(crop.y), 0, UINT(crop.x + crop.width), UINT(crop.y + crop.height), 1};
        if (!hdr) gpu.copy_into(target, input, whole ? nullptr : &box);
        else if (whole) gpu.tone_map(input, target, white, view(target));
        else {
            D3D11_TEXTURE2D_DESC current{}; if (scratch) scratch->GetDesc(&current);
            if (!scratch || current.Width != desc.Width || current.Height != desc.Height) {
                if (scratch) views.erase(scratch.Get());
                scratch.Reset();
                auto size = desc; size.Format = DXGI_FORMAT_B8G8R8A8_UNORM; size.MipLevels = 1; size.ArraySize = 1;
                size.Usage = D3D11_USAGE_DEFAULT; size.CPUAccessFlags = 0; size.MiscFlags = 0;
                size.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
                checked(gpu.device->CreateTexture2D(&size, nullptr, &scratch), "Allocate recording HDR crop texture");
                std::lock_guard lock(mutex); ++allocated;
            }
            // Tone mapping is per pixel, so mapping the whole frame and
            // copying the crop equals mapping a cropped FP16 copy.
            gpu.tone_map(input, scratch.Get(), white, view(scratch.Get()));
            gpu.copy_into(target, scratch.Get(), &box);
        }
        return record(started);
    }
    // Draws `crop` of a rotated output's desktop from scanout-oriented
    // `input` into `target`. `delivering` held.
    double fill_rotated(ID3D11Texture2D* target, ID3D11Texture2D* input, OutputRotation rotation, OutputSize desktop, const CaptureRect& crop) {
        const auto started = std::chrono::steady_clock::now();
        gpu.orient(input, target, rotation, desktop, crop, white, view(target));
        return record(started);
    }
    double record(std::chrono::steady_clock::time_point started) {
        const double elapsed = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count();
        std::lock_guard lock(mutex);
        copy_times.push_back(elapsed); if (copy_times.size() > 240) copy_times.pop_front();
        return elapsed;
    }
};

CapturedFrameStore::CapturedFrameStore(ID3D11Device* device, int capacity, float sdr_white_nits, CaptureRect region) {
    if (!device || capacity < 1 || capacity > 64 || region.width < 0 || region.height < 0 || (region.width > 0) != (region.height > 0))
        throw std::invalid_argument("Invalid captured frame store");
    impl_ = std::make_shared<Impl>(device, capacity, sdr_white_nits, region);
}
CapturedFrameStore::~CapturedFrameStore() { close(); }

bool CapturedFrameStore::deliver(ID3D11Texture2D* input, int64_t timestamp, const Timing& timing) {
    auto& s = *impl_;
    if (!input) throw std::invalid_argument("Missing captured frame");
    std::lock_guard serial(s.delivering);
    D3D11_TEXTURE2D_DESC desc{}; input->GetDesc(&desc);
    const bool hdr = desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT;
    if (!hdr && desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) throw std::runtime_error("Unsupported capture texture format");
    const auto crop = s.region.width > 0 ? s.region : CaptureRect{0, 0, int(desc.Width), int(desc.Height)};
    if (crop.x < 0 || crop.y < 0 || int64_t(crop.x) + crop.width > desc.Width || int64_t(crop.y) + crop.height > desc.Height) return false;
    // Released only outside the lock: a lease's return takes it.
    std::shared_ptr<ID3D11Texture2D> target, stale;
    {
        std::lock_guard lock(s.mutex);
        stale = s.resize(crop);
        // An untaken frame is superseded in place: nothing else holds it.
        if (s.newest) { target = std::move(s.newest); ++s.superseded; }
        else target = s.lease();
        if (!target) { ++s.pressure; return false; }
    }
    s.fill(target.get(), input, desc, crop);
    auto published = timing; LARGE_INTEGER now{}; QueryPerformanceCounter(&now); published.published_qpc = now.QuadPart;
    std::shared_ptr<ID3D11Texture2D> replaced;
    {
        std::lock_guard lock(s.mutex);
        replaced = std::move(s.newest);
        s.newest = std::move(target); s.stamp = timestamp; s.newest_timing = published; ++s.delivered;
    }
    s.changed.notify_all();
    return true;
}
std::shared_ptr<ID3D11Texture2D> CapturedFrameStore::copy(ID3D11Texture2D* input, CaptureRect crop, OutputRotation rotation) {
    auto& s = *impl_;
    if (!input) throw std::invalid_argument("Missing captured frame");
    std::lock_guard serial(s.delivering);
    D3D11_TEXTURE2D_DESC desc{}; input->GetDesc(&desc);
    if (desc.Format != DXGI_FORMAT_R16G16B16A16_FLOAT && desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM) throw std::runtime_error("Unsupported capture texture format");
    const auto desktop = output_desktop_size({int(desc.Width), int(desc.Height)}, rotation);
    if (crop.width <= 0 || crop.height <= 0) crop = {0, 0, desktop.width, desktop.height};
    if (!output_contains(crop, desktop)) return {};
    std::shared_ptr<ID3D11Texture2D> target, stale;
    {
        std::lock_guard lock(s.mutex);
        stale = s.resize(crop);
        target = s.lease();
        if (!target) { ++s.pressure; return {}; }
    }
    if (rotation == OutputRotation::Identity) s.fill(target.get(), input, desc, crop);
    else s.fill_rotated(target.get(), input, rotation, desktop, crop);
    { std::lock_guard lock(s.mutex); ++s.delivered; }
    return target;
}
bool CapturedFrameStore::take(CapturePixels& pixels, int64_t& timestamp, std::chrono::milliseconds timeout, Timing* timing) {
    auto& s = *impl_;
    std::shared_ptr<ID3D11Texture2D> texture;
    {
        std::unique_lock lock(s.mutex);
        s.changed.wait_for(lock, timeout, [&] { return s.newest || s.closed || !s.error.empty(); });
        if (!s.error.empty()) throw std::runtime_error(s.error);
        if (!s.newest || s.closed) return false;
        texture = std::move(s.newest); timestamp = s.stamp; if (timing) *timing = s.newest_timing;
    }
    D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
    pixels.width = int(desc.Width); pixels.height = int(desc.Height); pixels.stride = pixels.width * 4;
    pixels.texture = std::move(texture);
    return true;
}
void CapturedFrameStore::fail(const std::string& error) {
    { std::lock_guard lock(impl_->mutex); impl_->error = error; }
    impl_->changed.notify_all();
}
void CapturedFrameStore::close() {
    std::shared_ptr<ID3D11Texture2D> dropped;
    { std::lock_guard lock(impl_->mutex); impl_->closed = true; dropped = std::move(impl_->newest); }
    impl_->changed.notify_all();
}
void CapturedFrameStore::reset() {
    std::shared_ptr<ID3D11Texture2D> dropped;
    { std::lock_guard lock(impl_->mutex); impl_->error.clear(); dropped = std::move(impl_->newest); }
}
bool CapturedFrameStore::closed() const { std::lock_guard lock(impl_->mutex); return impl_->closed; }
CapturedFrameStore::Stats CapturedFrameStore::stats() const {
    const auto& s = *impl_;
    std::vector<double> times;
    Stats result;
    {
        std::lock_guard lock(s.mutex);
        result.capacity = s.capacity; result.leased = s.leased; result.peak_leased = s.peak;
        result.allocated = s.allocated; result.delivered = s.delivered; result.superseded = s.superseded; result.pressure_drops = s.pressure;
        times.assign(s.copy_times.begin(), s.copy_times.end());
    }
    if (!times.empty()) {
        std::sort(times.begin(), times.end());
        auto rank = [&](double p) { return times[std::max<size_t>(1, size_t(std::ceil(times.size() * p))) - 1]; };
        result.copy_p50_ms = rank(.5); result.copy_p95_ms = rank(.95);
    }
    return result;
}
void capture_copy_texture_pixels(CapturePixels& pixels) {
    if (!pixels.texture || !pixels.bgra.empty()) return;
    ComPtr<ID3D11Device> device; pixels.texture->GetDevice(&device);
    Device reader(device.Get());
    if (!reader.read(pixels.texture.get(), pixels)) throw std::runtime_error("Recording texture readback failed");
}
std::shared_ptr<ID3D11Texture2D> capture_tone_map_texture(ID3D11Texture2D* texture,float white){
    if(!texture)throw std::invalid_argument("Missing HDR texture");
    D3D11_TEXTURE2D_DESC desc{};texture->GetDesc(&desc);
    if(desc.Format!=DXGI_FORMAT_R16G16B16A16_FLOAT)throw std::invalid_argument("HDR conversion requires R16G16B16A16_FLOAT");
    ComPtr<ID3D11Device> device;texture->GetDevice(&device);Device processor(device.Get());
    auto output=processor.copy(texture,white);return{output.Detach(),[](auto*p){p->Release();}};
}
bool capture_copy_texture_pixels_nonblocking(CapturePixels& pixels,const std::function<bool()>& cancelled){
    if(!pixels.texture||!pixels.bgra.empty())return true;
    ComPtr<ID3D11Device> device;pixels.texture->GetDevice(&device);
    ComPtr<ID3D11DeviceContext> context;device->GetImmediateContext(&context);
    D3D11_TEXTURE2D_DESC desc{};pixels.texture->GetDesc(&desc);
    if(desc.Format!=DXGI_FORMAT_B8G8R8A8_UNORM)throw std::runtime_error("Detector readback requires BGRA");
    desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;desc.MiscFlags=0;
    ComPtr<ID3D11Texture2D> staging;checked(device->CreateTexture2D(&desc,nullptr,&staging),"Allocate detector readback");
    context->CopyResource(staging.Get(),pixels.texture.get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    for(;;){
        if(cancelled())return false;
        const auto result=context->Map(staging.Get(),0,D3D11_MAP_READ,D3D11_MAP_FLAG_DO_NOT_WAIT,&mapped);
        if(result==DXGI_ERROR_WAS_STILL_DRAWING){std::this_thread::sleep_for(std::chrono::milliseconds(1));continue;}
        checked(result,"Map detector readback");break;
    }
    try{
        pixels.bgra.resize(size_t(pixels.stride)*pixels.height);
        for(int row=0;row<pixels.height;++row)std::memcpy(pixels.bgra.data()+size_t(row)*pixels.stride,
            static_cast<const uint8_t*>(mapped.pData)+size_t(row)*mapped.RowPitch,size_t(pixels.width)*4);
    }catch(...){context->Unmap(staging.Get(),0);throw;}
    context->Unmap(staging.Get(),0);return true;
}
std::unique_ptr<RecordingFrameSource> create_windows_recording_source(const RecordingCaptureConfig& config) {
    auto resolved=config;
    if(!resolved.window&&(!resolved.target_executable.empty()||!resolved.target_title.empty()||!resolved.target_class.empty())){
        struct SearchWindow{RecordingCaptureConfig& config;HWND found=nullptr;}search{resolved};
        EnumWindows([](HWND hwnd,LPARAM parameter)->BOOL{
            auto& state=*reinterpret_cast<SearchWindow*>(parameter);const auto& desired=state.config;
            if(!IsWindowVisible(hwnd)||GetWindow(hwnd,GW_OWNER))return TRUE;
            wchar_t title[1024]{},class_name[256]{};GetWindowTextW(hwnd,title,1024);GetClassNameW(hwnd,class_name,256);
            if(!desired.target_title.empty()&&_wcsicmp(title,desired.target_title.c_str()))return TRUE;
            if(!desired.target_class.empty()&&_wcsicmp(class_name,desired.target_class.c_str()))return TRUE;
            if(!desired.target_executable.empty()){
                DWORD pid=0;GetWindowThreadProcessId(hwnd,&pid);HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);
                if(!process)return TRUE;wchar_t path[32768]{};DWORD length=32768;const bool read=QueryFullProcessImageNameW(process,0,path,&length)!=FALSE;CloseHandle(process);
                if(!read)return TRUE;const wchar_t* name=wcsrchr(path,L'\\');name=name?name+1:path;
                std::wstring expected=desired.target_executable;const auto slash=expected.find_last_of(L"/\\");if(slash!=std::wstring::npos)expected.erase(0,slash+1);
                if(_wcsicmp(name,expected.c_str()))return TRUE;
            }
            state.found=hwnd;return FALSE;
        },reinterpret_cast<LPARAM>(&search));
        if(!search.found)throw std::runtime_error("Selected recording window is unavailable");resolved.window=reinterpret_cast<uintptr_t>(search.found);
    }
    if(!resolved.window&&!resolved.monitor&&!resolved.monitor_device_name.empty()){
        struct Search{const std::wstring& name;uintptr_t result=0;}search{resolved.monitor_device_name};
        EnumDisplayMonitors(nullptr,nullptr,[](HMONITOR monitor,HDC,LPRECT,LPARAM parameter)->BOOL{
            auto& state=*reinterpret_cast<Search*>(parameter);MONITORINFOEXW info{};info.cbSize=sizeof(info);
            if(GetMonitorInfoW(monitor,&info)&&_wcsicmp(info.szDevice,state.name.c_str())==0){state.result=reinterpret_cast<uintptr_t>(monitor);return FALSE;}return TRUE;
        },reinterpret_cast<LPARAM>(&search));
        if(!search.result)throw std::runtime_error("Selected recording monitor is unavailable");resolved.monitor=search.result;
    }
    if(!resolved.window&&!resolved.monitor&&resolved.capture_region.width>0&&resolved.capture_region.height>0)
        resolved.monitor=reinterpret_cast<uintptr_t>(MonitorFromPoint(POINT{resolved.capture_region.x+resolved.capture_region.width/2,resolved.capture_region.y+resolved.capture_region.height/2},MONITOR_DEFAULTTONEAREST));
    auto target_monitor=reinterpret_cast<HMONITOR>(resolved.monitor);
    if(resolved.window)target_monitor=MonitorFromWindow(reinterpret_cast<HWND>(resolved.window),MONITOR_DEFAULTTONEAREST);
    if(!target_monitor)target_monitor=MonitorFromPoint(POINT{},MONITOR_DEFAULTTOPRIMARY);
    const auto profile=display_profile(target_monitor);resolved.display_hdr=profile.hdr;resolved.display_profile_available=profile.available;
    resolved.capture_hdr=resolved.capture_hdr&&profile.hdr;resolved.sdr_white_nits=profile.white;
    std::unique_ptr<RecordingFrameSource> source;
    if (resolved.prefer_dxgi) {
        try { source=std::make_unique<DxgiSource>(resolved); } catch (...) { source=std::make_unique<WgcSource>(resolved); }
    }else{
        try { source=std::make_unique<WgcSource>(resolved); } catch (...) { source=std::make_unique<DxgiSource>(resolved); }
    }
    return std::make_unique<AdaptiveSource>(resolved,std::move(source),config.capture_hdr);
}
}
