#include "recording_capture.h"
#include <Windows.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <d3dcompiler.h>
#include <dxgi1_5.h>
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
class Device {
public:
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Texture2D> staging;
    std::mutex mutex;
    ComPtr<ID3D11VertexShader> hdr_vertex;
    ComPtr<ID3D11PixelShader> hdr_pixel;
    RecordingSourceHealth diagnostics;
    explicit Device(ID3D11Device* existing=nullptr,bool debug=false) : device(existing) {
        if(existing)existing->GetImmediateContext(&context);
        else checked(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT|(debug?D3D11_CREATE_DEVICE_DEBUG:0),
            nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context), "Create recording D3D11 device");
        ComPtr<ID3D11Multithread> multithread;
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
    ComPtr<ID3D11Texture2D> copy(ID3D11Texture2D* input, float white) {
        std::lock_guard lock(mutex);
        D3D11_TEXTURE2D_DESC desc{}; input->GetDesc(&desc);
        const bool hdr = desc.Format == DXGI_FORMAT_R16G16B16A16_FLOAT;
        desc.Usage = D3D11_USAGE_DEFAULT; desc.CPUAccessFlags = 0; desc.MiscFlags = 0;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | (hdr ? D3D11_BIND_RENDER_TARGET : 0);
        if (hdr) desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        ComPtr<ID3D11Texture2D> owned;
        checked(device->CreateTexture2D(&desc, nullptr, &owned), "Allocate owned recording texture");
        if (!hdr) { context->CopyResource(owned.Get(), input); return owned; }
        if (!hdr_vertex) {
            const std::string shader =
                "struct V{float4 p:SV_Position;}; V VS(uint id:SV_VertexID){V o;o.p=float4(id==2?3:-1,id==1?3:-1,0,1);return o;}"
                "Texture2D<float4> Source:register(t0);float Srgb(float v){return v<=0.0031308?v*12.92:1.055*pow(v,1.0/2.4)-0.055;}"
                "float4 PS(V i):SV_Target{float3 rgb=max(Source.Load(int3(i.p.xy,0)).rgb,0)*" +
                std::to_string(80.f / (white > 0 && white < 10000 ? white : 80.f)) +
                ";float m=max(rgb.r,max(rgb.g,rgb.b));if(m>1)rgb/=m;return float4(saturate(Srgb(rgb.r)),saturate(Srgb(rgb.g)),saturate(Srgb(rgb.b)),1);}";
            ComPtr<ID3DBlob> vs, ps, error;
            checked(D3DCompile(shader.data(), shader.size(), nullptr, nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vs, &error), "Compile recording HDR vertex shader");
            checked(D3DCompile(shader.data(), shader.size(), nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &ps, &error), "Compile recording HDR pixel shader");
            checked(device->CreateVertexShader(vs->GetBufferPointer(), vs->GetBufferSize(), nullptr, &hdr_vertex), "Create recording HDR vertex shader");
            checked(device->CreatePixelShader(ps->GetBufferPointer(), ps->GetBufferSize(), nullptr, &hdr_pixel), "Create recording HDR pixel shader");
        }
        ComPtr<ID3D11ShaderResourceView> source;
        ComPtr<ID3D11RenderTargetView> target;
        checked(device->CreateShaderResourceView(input, nullptr, &source), "Create recording HDR input view");
        checked(device->CreateRenderTargetView(owned.Get(), nullptr, &target), "Create recording HDR output view");
        D3D11_VIEWPORT viewport{0,0,float(desc.Width),float(desc.Height),0,1};
        auto raw_target = target.Get(); auto raw_source = source.Get();
        context->OMSetRenderTargets(1, &raw_target, nullptr); context->RSSetViewports(1, &viewport);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->VSSetShader(hdr_vertex.Get(), nullptr, 0); context->PSSetShader(hdr_pixel.Get(), nullptr, 0);
        context->PSSetShaderResources(0, 1, &raw_source); context->Draw(3, 0);
        raw_target = nullptr; raw_source = nullptr;
        context->OMSetRenderTargets(1, &raw_target, nullptr); context->PSSetShaderResources(0, 1, &raw_source);
        return owned;
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
void draw_cursor(CapturePixels& pixels, int origin_x, int origin_y) {
    CURSORINFO cursor{sizeof(CURSORINFO)};
    if (!GetCursorInfo(&cursor) || !(cursor.flags & CURSOR_SHOWING)) return;
    ICONINFO icon{}; if (!GetIconInfo(cursor.hCursor, &icon)) return;
    struct IconCleanup { ICONINFO& i; ~IconCleanup(){ if(i.hbmColor)DeleteObject(i.hbmColor);if(i.hbmMask)DeleteObject(i.hbmMask); } } cleanup{icon};
    const int x = cursor.ptScreenPos.x - origin_x - int(icon.xHotspot);
    const int y = cursor.ptScreenPos.y - origin_y - int(icon.yHotspot);
    if (x > pixels.width || y > pixels.height) return;
    BITMAPINFO info{}; info.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    info.bmiHeader.biWidth = pixels.width; info.bmiHeader.biHeight = -pixels.height;
    info.bmiHeader.biPlanes = 1; info.bmiHeader.biBitCount = 32; info.bmiHeader.biCompression = BI_RGB;
    void* data = nullptr; HDC dc = CreateCompatibleDC(nullptr); if (!dc) return;
    HBITMAP bitmap = CreateDIBSection(dc, &info, DIB_RGB_COLORS, &data, nullptr, 0);
    if (!bitmap) { DeleteDC(dc); return; }
    auto old = SelectObject(dc, bitmap);
    for (int row = 0; row < pixels.height; ++row) std::memcpy(static_cast<uint8_t*>(data) + size_t(row) * pixels.width * 4,
        pixels.bgra.data() + size_t(row) * pixels.stride, size_t(pixels.width) * 4);
    DrawIconEx(dc, x, y, cursor.hCursor, 0, 0, 0, nullptr, DI_NORMAL);
    GdiFlush();
    for (int row = 0; row < pixels.height; ++row) std::memcpy(pixels.bgra.data() + size_t(row) * pixels.stride,
        static_cast<uint8_t*>(data) + size_t(row) * pixels.width * 4, size_t(pixels.width) * 4);
    SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc);
}

class WgcSource final : public RecordingFrameSource {
    struct Shared {
        Device gpu;
        explicit Shared(ID3D11Device* device,bool debug):gpu(device,debug){}
        std::mutex mutex;
        std::condition_variable changed;
        ComPtr<ID3D11Texture2D> latest;
        int64_t stamp = 0;
        bool closed = false;
        std::string error;
        RecordingSourceHealth diagnostics;
    };
    RecordingCaptureConfig config_;
    std::shared_ptr<Shared> shared_;
    IDirect3DDevice direct_device_{nullptr};
    GraphicsCaptureItem item_{nullptr};
    Direct3D11CaptureFramePool pool_{nullptr};
    GraphicsCaptureSession session_{nullptr};
    winrt::event_token arrived_{}, closed_{};
    RECT desktop_{};
public:
    explicit WgcSource(const RecordingCaptureConfig& config,ID3D11Device* existing=nullptr) : config_(config),shared_(std::make_shared<Shared>(existing,config.d3d_debug)) {
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
            MONITORINFO monitor_info{sizeof(MONITORINFO)};if(GetMonitorInfoW(monitor,&monitor_info))desktop_=monitor_info.rcMonitor;
            checked(interop->CreateForMonitor(monitor, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item_)), "Create monitor capture item");
        }
        const auto size = item_.Size();
        if (size.Width <= 0 || size.Height <= 0) throw std::runtime_error("Capture target has no pixels");
        const auto format = config.capture_hdr ? DirectXPixelFormat::R16G16B16A16Float : DirectXPixelFormat::B8G8R8A8UIntNormalized;
        pool_ = Direct3D11CaptureFramePool::CreateFreeThreaded(direct_device_, format, 3, size);
        auto shared = shared_; auto direct = direct_device_; const auto white = config.sdr_white_nits;
        arrived_ = pool_.FrameArrived([shared, direct, format, white](const Direct3D11CaptureFramePool& pool, auto&&) {
            try {
                {std::lock_guard lock(shared->mutex);++shared->diagnostics.callbacks;}
                auto frame = pool.TryGetNextFrame();
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
                    auto owned = shared->gpu.copy(texture.Get(), white);
                    const auto timestamp = frame.SystemRelativeTime().count() / 10;
                    frame.Close();
                    { std::lock_guard lock(shared->mutex); ++shared->diagnostics.frames_delivered;
                      if(shared->latest)++shared->diagnostics.overwritten;
                      shared->latest = std::move(owned); shared->stamp = timestamp; }
                    shared->changed.notify_all(); frame = pool.TryGetNextFrame();
                }
            } catch (const std::exception& e) {
                std::lock_guard lock(shared->mutex); shared->error = e.what(); shared->changed.notify_all();
            } catch (...) {
                std::lock_guard lock(shared->mutex); shared->error = "Windows Graphics Capture frame callback failed"; shared->changed.notify_all();
            }
        });
        closed_ = item_.Closed([shared](auto&&, auto&&) { std::lock_guard lock(shared->mutex); shared->closed = true; shared->latest.Reset(); shared->changed.notify_all(); });
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
        std::lock_guard lock(shared_->mutex);
        return !shared_->closed && window_eligible(reinterpret_cast<HWND>(config_.window), true);
    }
    bool foreground() const override {return window_eligible(reinterpret_cast<HWND>(config_.window),false);}
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
        ComPtr<ID3D11Texture2D> owned; int64_t stamp;
        { std::unique_lock lock(shared_->mutex);
          shared_->changed.wait_for(lock, timeout, [&] { return shared_->latest || shared_->closed || !shared_->error.empty(); });
          if (!shared_->error.empty()) throw std::runtime_error(shared_->error);
          if (!shared_->latest) return false; owned = std::move(shared_->latest); stamp = shared_->stamp; }
        if (!eligible()) return false;
        pixels.timestamp_us = config_.monotonic_anchor_us + stamp - config_.qpc_anchor / config_.qpc_frequency * 1000000 -
            config_.qpc_anchor % config_.qpc_frequency * 1000000 / config_.qpc_frequency;
        D3D11_TEXTURE2D_DESC desc{}; owned->GetDesc(&desc);
        if(!config_.window&&config_.capture_region.width>0&&config_.capture_region.height>0){
            const auto& region=config_.capture_region;
            const int x=region.x-desktop_.left,y=region.y-desktop_.top;
            if(x<0||y<0||int64_t(x)+region.width>desc.Width||int64_t(y)+region.height>desc.Height)return false;
            auto cropped_desc=desc;cropped_desc.Width=region.width;cropped_desc.Height=region.height;
            ComPtr<ID3D11Texture2D> cropped;checked(shared_->gpu.device->CreateTexture2D(&cropped_desc,nullptr,&cropped),"Create recording region texture");
            D3D11_BOX box{UINT(x),UINT(y),0,UINT(x+region.width),UINT(y+region.height),1};
            shared_->gpu.context->CopySubresourceRegion(cropped.Get(),0,0,0,0,owned.Get(),0,&box);owned=std::move(cropped);desc=cropped_desc;
        }
        pixels.width = int(desc.Width); pixels.height = int(desc.Height); pixels.stride = pixels.width * 4;
        pixels.texture = std::shared_ptr<ID3D11Texture2D>(owned.Detach(), [](auto* p) { p->Release(); });
        return true;
    }
    void set_frame_rate(int fps) override {
        if (!session_) return;
        auto monitor = reinterpret_cast<HMONITOR>(config_.monitor);
        if (config_.window) monitor=MonitorFromWindow(reinterpret_cast<HWND>(config_.window),MONITOR_DEFAULTTONEAREST);
        if (!monitor) monitor=MonitorFromPoint(POINT{},MONITOR_DEFAULTTOPRIMARY);
        MONITORINFOEXW info{};info.cbSize=sizeof(info); DEVMODEW mode{};mode.dmSize=sizeof(mode);double refresh=0;
        if(GetMonitorInfoW(monitor,&info)&&EnumDisplaySettingsW(info.szDevice,ENUM_CURRENT_SETTINGS,&mode))refresh=mode.dmDisplayFrequency;
        // This interface is optional on older supported Windows builds.
        const auto interval=capture_wgc_interval_100ns(fps,refresh);
        {std::lock_guard lock(shared_->mutex);shared_->diagnostics.requested_interval_100ns=interval;shared_->diagnostics.display_refresh_hz=refresh;}
        try { session_.MinUpdateInterval(std::chrono::duration<int64_t, std::ratio<1, 10000000>>(interval));
            const auto applied=session_.MinUpdateInterval().count();
            std::lock_guard lock(shared_->mutex);shared_->diagnostics.applied_interval_100ns=applied;shared_->diagnostics.update_interval_available=true;
        } catch (...) {}
    }
    const char* name() const override { return "Windows Graphics Capture"; }
    ID3D11Device* d3d_device() const override { return shared_->gpu.device.Get(); }
    CaptureRect content_bounds() const override {
        if(!config_.window&&config_.capture_region.width>0&&config_.capture_region.height>0)return{0,0,config_.capture_region.width,config_.capture_region.height};
        const auto size=item_.Size();return{0,0,size.Width,size.Height};
    }
    RecordingSourceHealth diagnostics() const override {
        std::lock_guard lock(shared_->mutex);auto result=shared_->diagnostics;
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
            std::lock_guard lock(shared_->mutex);shared_->error.clear();shared_->latest.Reset();return true;
        }catch(...){return false;}
    }
};

class DxgiSource final : public RecordingFrameSource {
    RecordingCaptureConfig config_;
    Device gpu_;
    ComPtr<IDXGIOutputDuplication> duplication_;
    RECT desktop_{};
    CaptureRect stable_{}, candidate_{};
    int crop_samples_ = 0;
public:
    explicit DxgiSource(const RecordingCaptureConfig& config,ID3D11Device* existing=nullptr) : config_(config),gpu_(existing,config.d3d_debug) { open(); }
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
            desktop_ = desc.DesktopCoordinates; return;
        }
        throw std::runtime_error("Capture display unavailable on recording adapter");
    }
    bool eligible() const override { return window_eligible(reinterpret_cast<HWND>(config_.window), false); }
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
        if (!eligible()) return false;
        DXGI_OUTDUPL_FRAME_INFO info{}; ComPtr<IDXGIResource> resource;
        const auto result = duplication_->AcquireNextFrame(DWORD(timeout.count()), &info, &resource);
        if (result == DXGI_ERROR_WAIT_TIMEOUT) return false;
        if (result == DXGI_ERROR_ACCESS_LOST) { open(); return false; }
        checked(result, "Acquire desktop recording frame");
        struct Release { IDXGIOutputDuplication* p; ~Release() { p->ReleaseFrame(); } } release{duplication_.Get()};
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
        const auto delta = stamp - config_.qpc_anchor;
        pixels.timestamp_us = config_.monotonic_anchor_us + delta / config_.qpc_frequency * 1000000 +
            delta % config_.qpc_frequency * 1000000 / config_.qpc_frequency;
        D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
        if (crop.width <= 0 || crop.height <= 0) crop = {0,0,int(desc.Width),int(desc.Height)};
        if (crop.x < 0 || crop.y < 0 || int64_t(crop.x) + crop.width > desc.Width || int64_t(crop.y) + crop.height > desc.Height) return false;
        desc.Width = crop.width; desc.Height = crop.height; desc.Usage = D3D11_USAGE_DEFAULT;
        desc.CPUAccessFlags = 0; desc.MiscFlags = 0; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        ComPtr<ID3D11Texture2D> owned;
        checked(gpu_.device->CreateTexture2D(&desc, nullptr, &owned), "Allocate owned desktop frame");
        D3D11_BOX box{UINT(crop.x),UINT(crop.y),0,UINT(crop.x+crop.width),UINT(crop.y+crop.height),1};
        gpu_.context->CopySubresourceRegion(owned.Get(),0,0,0,0,texture.Get(),0,&box);
        if(desc.Format==DXGI_FORMAT_R16G16B16A16_FLOAT)owned=gpu_.copy(owned.Get(),config_.sdr_white_nits);
        pixels.width = crop.width; pixels.height = crop.height; pixels.stride = crop.width * 4;
        pixels.texture = std::shared_ptr<ID3D11Texture2D>(owned.Detach(), [](auto* p){p->Release();});
        if (config_.capture_cursor) {
            if (!gpu_.read(pixels.texture.get(), pixels)) return false;
            draw_cursor(pixels, desktop_.left + crop.x, desktop_.top + crop.y); pixels.texture.reset();
        }
        return true;
    }
    const char* name() const override { return "DXGI Desktop Duplication"; }
    ID3D11Device* d3d_device() const override { return gpu_.device.Get(); }
    void stop() override { duplication_.Reset(); }
    RecordingSourceHealth diagnostics() const override {
        auto result=gpu_.diagnostics;result.display_profile_available=config_.display_profile_available;
        result.hdr_display=config_.display_hdr;result.hdr_conversion=config_.capture_hdr;result.sdr_white_nits=config_.sdr_white_nits;return result;
    }
    bool recover() override { try{open();return true;}catch(...){return false;} }
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
            if(!switch_backend(wgc)){config_=std::move(previous);throw std::runtime_error("Recording display changed and capture recreation failed; restart worker");}
            monitored_=monitor;
        }
        active_->set_frame_rate(fps);
    }
    void stop()override{active_->stop();}
    RecordingSourceHealth diagnostics()const override{return active_->diagnostics();}
    CaptureRect content_bounds()const override{return active_->content_bounds();}
    bool recover()override{
        if(active_->recover())return true;
        return switch_backend(std::string_view(active_->name())!="Windows Graphics Capture");
    }
    bool switch_backend(bool wgc)override{
        if(!wgc&&!active_->foreground())return false;
        try{
            auto* device=active_->d3d_device();std::unique_ptr<RecordingFrameSource> replacement;
            if(wgc)replacement=std::make_unique<WgcSource>(config_,device);
            else replacement=std::make_unique<DxgiSource>(config_,device);
            active_->stop();active_=std::move(replacement);return true;
        }catch(...){return false;}
    }
};
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
