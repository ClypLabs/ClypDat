#pragma once
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
extern "C" {
#include <libavutil/hwcontext.h>
}

inline int create_test_ffmpeg_d3d11(AVBufferRef** device) {
    AVDictionary* options=nullptr;
    av_dict_set(&options,"vendor_id","0x10de",0);
    const int result=av_hwdevice_ctx_create(device,AV_HWDEVICE_TYPE_D3D11VA,nullptr,options,0);
    av_dict_free(&options);
    return result;
}

// Generated GPU suites exercise NVENC (including AV1), so adapter 0 is not
// sufficient on hybrid PCs. Explicit adapters, including --qsv's Intel
// adapter, always take precedence. No production device selection changes.
inline HRESULT create_test_d3d11_device(IDXGIAdapter* adapter, D3D_DRIVER_TYPE type,
    HMODULE software, UINT flags, const D3D_FEATURE_LEVEL* levels, UINT level_count,
    UINT sdk, ID3D11Device** device, D3D_FEATURE_LEVEL* level, ID3D11DeviceContext** context) {
    Microsoft::WRL::ComPtr<IDXGIAdapter1> selected;
    if (!adapter && type == D3D_DRIVER_TYPE_HARDWARE) {
        Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) {
            for (UINT i=0;;++i) {
                Microsoft::WRL::ComPtr<IDXGIAdapter1> candidate;
                if (FAILED(factory->EnumAdapters1(i,&candidate))) break;
                DXGI_ADAPTER_DESC1 desc{};
                if (SUCCEEDED(candidate->GetDesc1(&desc)) && desc.VendorId==0x10de && !(desc.Flags&DXGI_ADAPTER_FLAG_SOFTWARE)) {
                    selected=candidate;adapter=selected.Get();type=D3D_DRIVER_TYPE_UNKNOWN;break;
                }
            }
        }
    }
    return D3D11CreateDevice(adapter,type,software,flags,levels,level_count,sdk,device,level,context);
}
