#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <MinHook.h>
#include <algorithm>
#include <string>
#include "clypdat_graphics_hook_protocol.h"

namespace {
using present_fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using present1_fn = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
present_fn original_present = nullptr;
present1_fn original_present1 = nullptr;
clypdat_hook_control* control = nullptr;
HANDLE control_mapping = nullptr;
HANDLE rebuild_event = nullptr;
IDXGISwapChain* candidate = nullptr;
ID3D11Texture2D* slots[CLYPDAT_HOOK_SLOT_COUNT]{};
IDXGIKeyedMutex* mutexes[CLYPDAT_HOOK_SLOT_COUNT]{};
ID3D11Texture2D* resolve_texture = nullptr;
UINT width = 0, height = 0;
DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
volatile LONG next_slot = 0;
volatile LONG64 last_capture_ms = 0;

bool supported(DXGI_FORMAT value) {
    return value == DXGI_FORMAT_R8G8B8A8_UNORM || value == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
        value == DXGI_FORMAT_B8G8R8A8_UNORM || value == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
}
void fail(clypdat_hook_failure value) {
    if (!control) return;
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&control->failure), value);
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&control->state), CLYPDAT_HOOK_FAILED);
}
void release_transport() {
    for (UINT i = 0; i != CLYPDAT_HOOK_SLOT_COUNT; ++i) {
        if (mutexes[i]) { mutexes[i]->Release(); mutexes[i] = nullptr; }
        if (slots[i]) { slots[i]->Release(); slots[i] = nullptr; }
    }
    if (resolve_texture) { resolve_texture->Release(); resolve_texture = nullptr; }
    width = height = 0; format = DXGI_FORMAT_UNKNOWN;
}
void schedule_rebuild(IDXGISwapChain* chain) {
    if (InterlockedCompareExchangePointer(reinterpret_cast<PVOID volatile*>(&candidate), chain, nullptr) != nullptr) return;
    chain->AddRef();
    SetEvent(rebuild_event);
}
bool create_transport(IDXGISwapChain* chain) {
    release_transport();
    DXGI_SWAP_CHAIN_DESC swap_desc{};
    if (FAILED(chain->GetDesc(&swap_desc)) || !supported(swap_desc.BufferDesc.Format)) { fail(CLYPDAT_HOOK_FAILURE_UNSUPPORTED_FORMAT); return false; }
    ID3D11Device* device = nullptr;
    ID3D11Texture2D* backbuffer = nullptr;
    if (FAILED(chain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&device))) ||
        FAILED(chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backbuffer)))) {
        if (device) device->Release(); fail(CLYPDAT_HOOK_FAILURE_NON_D3D11); return false;
    }
    D3D11_TEXTURE2D_DESC source{}; backbuffer->GetDesc(&source); backbuffer->Release();
    if (!supported(source.Format)) { device->Release(); fail(CLYPDAT_HOOK_FAILURE_UNSUPPORTED_FORMAT); return false; }
    if (source.SampleDesc.Count > 1) {
        auto resolve = source; resolve.SampleDesc = { 1, 0 }; resolve.BindFlags = 0; resolve.MiscFlags = 0;
        if (FAILED(device->CreateTexture2D(&resolve, nullptr, &resolve_texture))) { device->Release(); fail(CLYPDAT_HOOK_FAILURE_RESOURCE); return false; }
    }
    for (UINT i = 0; i != CLYPDAT_HOOK_SLOT_COUNT; ++i) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = source.Width; desc.Height = source.Height; desc.MipLevels = 1; desc.ArraySize = 1; desc.Format = source.Format;
        desc.SampleDesc = { 1, 0 }; desc.Usage = D3D11_USAGE_DEFAULT;
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        if (FAILED(device->CreateTexture2D(&desc, nullptr, &slots[i])) ||
            FAILED(slots[i]->QueryInterface(__uuidof(IDXGIKeyedMutex), reinterpret_cast<void**>(&mutexes[i])))) {
            device->Release(); fail(CLYPDAT_HOOK_FAILURE_RESOURCE); return false;
        }
        IDXGIResource1* resource = nullptr; HANDLE handle = nullptr;
        const HRESULT named = SUCCEEDED(slots[i]->QueryInterface(__uuidof(IDXGIResource1), reinterpret_cast<void**>(&resource)))
            ? resource->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, control->slot_resource_names[i], &handle)
            : E_NOINTERFACE;
        if (resource) resource->Release();
        if (FAILED(named)) { device->Release(); fail(CLYPDAT_HOOK_FAILURE_RESOURCE); return false; }
        CloseHandle(handle);
    }
    IDXGIDevice* dxgi_device = nullptr; IDXGIAdapter* adapter = nullptr; DXGI_ADAPTER_DESC adapter_desc{};
    if (SUCCEEDED(device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgi_device))) &&
        SUCCEEDED(dxgi_device->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&adapter_desc))) {
        control->adapter_luid_low = adapter_desc.AdapterLuid.LowPart;
        control->adapter_luid_high = adapter_desc.AdapterLuid.HighPart;
    }
    if (adapter) adapter->Release(); if (dxgi_device) dxgi_device->Release(); device->Release();
    width = source.Width; height = source.Height; format = source.Format;
    control->width = width; control->height = height; control->format = static_cast<uint32_t>(format);
    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&control->generation));
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&control->failure), CLYPDAT_HOOK_FAILURE_NONE);
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&control->state), CLYPDAT_HOOK_READY);
    return true;
}
void capture(IDXGISwapChain* chain) {
    if (!control || control->state != CLYPDAT_HOOK_READY) return;
    DXGI_SWAP_CHAIN_DESC desc{};
    if (FAILED(chain->GetDesc(&desc)) || reinterpret_cast<uint64_t>(desc.OutputWindow) != control->target_hwnd) return;
    InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&control->presents));
    const auto now_ms = static_cast<LONG64>(GetTickCount64());
    const uint32_t requested_fps = control->requested_fps;
    const auto interval_ms = std::max<LONG64>(1, 1000 / std::max<uint32_t>(1u, requested_fps));
    const auto previous_ms = InterlockedCompareExchange64(&last_capture_ms, 0, 0);
    if (now_ms - previous_ms < interval_ms || InterlockedCompareExchange64(&last_capture_ms, now_ms, previous_ms) != previous_ms) return;
    ID3D11Texture2D* backbuffer = nullptr;
    if (FAILED(chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&backbuffer)))) { schedule_rebuild(chain); return; }
    D3D11_TEXTURE2D_DESC source{}; backbuffer->GetDesc(&source);
    if (source.Width != width || source.Height != height || source.Format != format) { backbuffer->Release(); schedule_rebuild(chain); return; }
    const UINT slot = static_cast<UINT>(InterlockedIncrement(&next_slot) - 1) % CLYPDAT_HOOK_SLOT_COUNT;
    if (FAILED(mutexes[slot]->AcquireSync(0, 0))) { backbuffer->Release(); InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&control->transport_drops)); return; }
    ID3D11Device* device = nullptr; ID3D11DeviceContext* context = nullptr;
    if (SUCCEEDED(chain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&device)))) device->GetImmediateContext(&context);
    if (context) {
        if (source.SampleDesc.Count > 1) { context->ResolveSubresource(resolve_texture, 0, backbuffer, 0, source.Format); context->CopyResource(slots[slot], resolve_texture); }
        else context->CopyResource(slots[slot], backbuffer);
        context->Release();
        LARGE_INTEGER now{}; QueryPerformanceCounter(&now);
        control->slot_qpc[slot] = now.QuadPart;
        InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&control->slot_sequences[slot]));
        InterlockedIncrement64(reinterpret_cast<volatile LONG64*>(&control->transported_frames));
    }
    if (device) device->Release();
    backbuffer->Release();
    mutexes[slot]->ReleaseSync(1);
}
HRESULT STDMETHODCALLTYPE present_hook(IDXGISwapChain* chain, UINT interval, UINT flags) { capture(chain); return original_present(chain, interval, flags); }
HRESULT STDMETHODCALLTYPE present1_hook(IDXGISwapChain1* chain, UINT interval, UINT flags, const DXGI_PRESENT_PARAMETERS* parameters) { capture(chain); return original_present1(chain, interval, flags, parameters); }
bool install_hooks(HINSTANCE instance) {
    WNDCLASSW klass{}; klass.lpfnWndProc = DefWindowProcW; klass.hInstance = instance; klass.lpszClassName = L"ClypDatGraphicsHookProbe";
    RegisterClassW(&klass);
    const HWND window = CreateWindowExW(0, klass.lpszClassName, L"", WS_OVERLAPPED, 0, 0, 1, 1, nullptr, nullptr, instance, nullptr);
    DXGI_SWAP_CHAIN_DESC desc{}; desc.BufferCount = 1; desc.BufferDesc.Width = desc.BufferDesc.Height = 1; desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.OutputWindow = window; desc.SampleDesc.Count = 1; desc.Windowed = TRUE;
    ID3D11Device* device = nullptr; ID3D11DeviceContext* context = nullptr; IDXGISwapChain* chain = nullptr; D3D_FEATURE_LEVEL level{};
    if (FAILED(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &desc, &chain, &device, &level, &context))) { DestroyWindow(window); return false; }
    void** table = *reinterpret_cast<void***>(chain);
    bool ok = MH_Initialize() == MH_OK && MH_CreateHook(table[8], present_hook, reinterpret_cast<void**>(&original_present)) == MH_OK && MH_EnableHook(table[8]) == MH_OK;
    IDXGISwapChain1* chain1 = nullptr;
    if (ok && SUCCEEDED(chain->QueryInterface(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&chain1)))) {
        void* present1 = (*reinterpret_cast<void***>(chain1))[22];
        ok = MH_CreateHook(present1, present1_hook, reinterpret_cast<void**>(&original_present1)) == MH_OK && MH_EnableHook(present1) == MH_OK;
        chain1->Release();
    }
    chain->Release(); context->Release(); device->Release(); DestroyWindow(window);
    return ok;
}
DWORD WINAPI worker(void* parameter) {
    const auto instance = static_cast<HINSTANCE>(parameter);
    const std::wstring locator_name = L"Local\\ClypDat.GraphicsHook.Locator." + std::to_wstring(GetCurrentProcessId());
    HANDLE locator_mapping = OpenFileMappingW(FILE_MAP_READ, FALSE, locator_name.c_str());
    if (!locator_mapping) return 0;
    auto* locator = static_cast<clypdat_hook_locator*>(MapViewOfFile(locator_mapping, FILE_MAP_READ, 0, 0, sizeof(clypdat_hook_locator)));
    if (!locator || locator->magic != CLYPDAT_HOOK_MAGIC || locator->abi_size != sizeof(clypdat_hook_locator) || locator->abi_version != CLYPDAT_HOOK_ABI_VERSION) { if (locator) UnmapViewOfFile(locator); CloseHandle(locator_mapping); return 0; }
    control_mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, locator->control_mapping_name);
    UnmapViewOfFile(locator); CloseHandle(locator_mapping);
    if (!control_mapping) return 0;
    control = static_cast<clypdat_hook_control*>(MapViewOfFile(control_mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(clypdat_hook_control)));
    if (!control || control->magic != CLYPDAT_HOOK_MAGIC || control->abi_size != sizeof(clypdat_hook_control) || control->abi_version != CLYPDAT_HOOK_ABI_VERSION || control->target_pid != GetCurrentProcessId()) { fail(CLYPDAT_HOOK_FAILURE_PROTOCOL); return 0; }
    rebuild_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&control->state), CLYPDAT_HOOK_WAITING_FOR_SWAPCHAIN);
    if (!install_hooks(instance)) { fail(CLYPDAT_HOOK_FAILURE_DEVICE); return 0; }
    while (control->state != CLYPDAT_HOOK_STOP_REQUESTED) {
        WaitForSingleObject(rebuild_event, 100);
        auto* swap = static_cast<IDXGISwapChain*>(InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(&candidate), nullptr));
        if (swap) { create_transport(swap); swap->Release(); }
    }
    MH_DisableHook(MH_ALL_HOOKS); MH_Uninitialize(); release_transport();
    InterlockedExchange(reinterpret_cast<volatile LONG*>(&control->state), CLYPDAT_HOOK_STOPPED);
    return 0;
}
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { DisableThreadLibraryCalls(instance); HANDLE thread = CreateThread(nullptr, 0, worker, instance, 0, nullptr); if (thread) CloseHandle(thread); }
    return TRUE;
}
