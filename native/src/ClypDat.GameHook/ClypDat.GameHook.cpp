#include <Windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <dxgi1_2.h>
#include <atomic>
#include <cstdint>
#include <string>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

namespace
{
constexpr uint32_t ProtocolVersion = 1, HeaderMagic = 0x48444743, SurfaceCount = 3;
using PresentFunction = HRESULT(__stdcall*)(IDXGISwapChain*, UINT, UINT);
using Present1Function = HRESULT(__stdcall*)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);

// Shared by the recorder and the hook. Sequence is written last, after the
// keyed mutex has handed the texture to the recorder.
struct SharedHeader {
    uint32_t magic = HeaderMagic; uint16_t version = ProtocolVersion; uint16_t size = sizeof(SharedHeader);
    uint32_t generation = 0, width = 0, height = 0, format = DXGI_FORMAT_UNKNOWN; LUID adapter{};
    volatile LONG64 sequence = 0; volatile LONG slot = -1, targetFrameRate = 60;
    volatile LONG64 presents = 0, transported = 0, drops = 0;
};
struct Surface { ID3D11Texture2D* texture = nullptr; IDXGIKeyedMutex* mutex = nullptr; HANDLE handle = nullptr; };

std::atomic<PresentFunction> originalPresent = nullptr; std::atomic<Present1Function> originalPresent1 = nullptr;
void** presentSlot = nullptr; void** present1Slot = nullptr; std::atomic<long> calls = 0; std::atomic<bool> accepting = true;
HANDLE stopEvent = nullptr, frameEvent = nullptr, mapping = nullptr, pipe = INVALID_HANDLE_VALUE;
SharedHeader* header = nullptr; ID3D11Device* device = nullptr; ID3D11DeviceContext* context = nullptr; Surface surfaces[SurfaceCount];
LARGE_INTEGER qpcFrequency{}, lastCopy{};

std::wstring PipeName() { return L"\\\\.\\pipe\\ClypDat-GameHook-" + std::to_wstring(GetCurrentProcessId()); }
std::wstring HeaderName() { return L"Local\\ClypDat-GameHook-Header-" + std::to_wstring(GetCurrentProcessId()); }
std::wstring EventName() { return L"Local\\ClypDat-GameHook-Frame-" + std::to_wstring(GetCurrentProcessId()); }
bool Send(const std::wstring& text) { DWORD written = 0; return pipe != INVALID_HANDLE_VALUE && WriteFile(pipe, text.data(), DWORD(text.size() * sizeof(wchar_t)), &written, nullptr) && written == text.size() * sizeof(wchar_t); }
bool Supported(DXGI_FORMAT f) { return f == DXGI_FORMAT_B8G8R8A8_UNORM || f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB || f == DXGI_FORMAT_R8G8B8A8_UNORM || f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB; }
LONG ClampRate(LONG rate) { return rate < 30 ? 30 : rate > 120 ? 120 : rate; }

void ReleaseTransport() {
    for (auto& surface : surfaces) { if (surface.mutex) surface.mutex->Release(); if (surface.texture) surface.texture->Release(); surface = {}; }
    if (context) context->Release(); if (device) device->Release(); context = nullptr; device = nullptr;
}

bool CreateTransport(IDXGISwapChain* chain, ID3D11Texture2D* backBuffer) {
    D3D11_TEXTURE2D_DESC source{}; backBuffer->GetDesc(&source);
    if (!Supported(source.Format) || !source.Width || !source.Height) { Send(L"failed unsupported-format\n"); return false; }
    ID3D11Device* newDevice = nullptr; IDXGIDevice* dxgiDevice = nullptr; IDXGIAdapter* adapter = nullptr; DXGI_ADAPTER_DESC adapterDesc{};
    if (FAILED(chain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&newDevice))) ||
        FAILED(newDevice->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDevice))) ||
        FAILED(dxgiDevice->GetAdapter(&adapter)) || FAILED(adapter->GetDesc(&adapterDesc))) {
        if (adapter) adapter->Release(); if (dxgiDevice) dxgiDevice->Release(); if (newDevice) newDevice->Release(); return false;
    }
    adapter->Release(); dxgiDevice->Release(); ReleaseTransport(); device = newDevice; device->GetImmediateContext(&context);
    D3D11_TEXTURE2D_DESC transport = source; transport.ArraySize = transport.MipLevels = 1; transport.SampleDesc = { 1, 0 };
    transport.BindFlags = 0; transport.CPUAccessFlags = 0; transport.Usage = D3D11_USAGE_DEFAULT; transport.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    for (auto& surface : surfaces) {
        IDXGIResource* resource = nullptr;
        if (FAILED(device->CreateTexture2D(&transport, nullptr, &surface.texture)) ||
            FAILED(surface.texture->QueryInterface(__uuidof(IDXGIKeyedMutex), reinterpret_cast<void**>(&surface.mutex))) ||
            FAILED(surface.texture->QueryInterface(__uuidof(IDXGIResource), reinterpret_cast<void**>(&resource))) || FAILED(resource->GetSharedHandle(&surface.handle))) {
            if (resource) resource->Release(); ReleaseTransport(); return false;
        }
        resource->Release();
    }
    header->width = source.Width; header->height = source.Height; header->format = uint32_t(source.Format); header->adapter = adapterDesc.AdapterLuid;
    ++header->generation; InterlockedExchange64(&header->sequence, 0); InterlockedExchange(&header->slot, -1);
    return Send(L"surface " + std::to_wstring(header->generation) + L" " + std::to_wstring(source.Width) + L" " + std::to_wstring(source.Height) + L" " +
        std::to_wstring(uint32_t(source.Format)) + L" " + std::to_wstring(adapterDesc.AdapterLuid.HighPart) + L" " + std::to_wstring(adapterDesc.AdapterLuid.LowPart) + L" " +
        std::to_wstring(uintptr_t(surfaces[0].handle)) + L" " + std::to_wstring(uintptr_t(surfaces[1].handle)) + L" " + std::to_wstring(uintptr_t(surfaces[2].handle)) + L"\n");
}

void Capture(IDXGISwapChain* chain) {
    if (!accepting || !header) return; InterlockedIncrement64(&header->presents);
    LARGE_INTEGER now{}; QueryPerformanceCounter(&now); const auto rate = ClampRate(InterlockedCompareExchange(&header->targetFrameRate, 0, 0));
    if (lastCopy.QuadPart && (now.QuadPart - lastCopy.QuadPart) * rate < qpcFrequency.QuadPart) return;
    ID3D11Texture2D* buffer = nullptr; if (FAILED(chain->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&buffer)))) return;
    D3D11_TEXTURE2D_DESC desc{}; buffer->GetDesc(&desc);
    ID3D11Device* chainDevice = nullptr; chain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&chainDevice));
    const bool deviceChanged = chainDevice && chainDevice != device; if (chainDevice) chainDevice->Release();
    if (!device || deviceChanged || header->width != desc.Width || header->height != desc.Height || header->format != desc.Format) if (!CreateTransport(chain, buffer)) { buffer->Release(); InterlockedIncrement64(&header->drops); return; }
    const auto sequence = InterlockedCompareExchange64(&header->sequence, 0, 0) + 1; const auto slot = uint32_t(sequence % SurfaceCount);
    if (FAILED(surfaces[slot].mutex->AcquireSync(0, 0))) { buffer->Release(); InterlockedIncrement64(&header->drops); return; }
    context->CopyResource(surfaces[slot].texture, buffer); surfaces[slot].mutex->ReleaseSync(1); buffer->Release(); lastCopy = now;
    InterlockedExchange(&header->slot, LONG(slot)); MemoryBarrier(); InterlockedExchange64(&header->sequence, sequence); InterlockedIncrement64(&header->transported); SetEvent(frameEvent);
}

HRESULT __stdcall PresentHook(IDXGISwapChain* chain, UINT sync, UINT flags) { calls++; Capture(chain); calls--; auto original = originalPresent.load(); return original ? original(chain, sync, flags) : DXGI_ERROR_INVALID_CALL; }
HRESULT __stdcall Present1Hook(IDXGISwapChain1* chain, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* parameters) { calls++; Capture(chain); calls--; auto original = originalPresent1.load(); return original ? original(chain, sync, flags, parameters) : DXGI_ERROR_INVALID_CALL; }
bool Replace(void** slot, void* replacement, void** old) { DWORD protection = 0; if (!VirtualProtect(slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &protection)) return false; *old = InterlockedExchangePointer(slot, replacement); DWORD ignored = 0; VirtualProtect(slot, sizeof(void*), protection, &ignored); return *old != nullptr; }
void Restore() {
    accepting = false;
    auto restore = [](void** slot, void* old) { DWORD protection = 0; if (slot && old && VirtualProtect(slot, sizeof(void*), PAGE_EXECUTE_READWRITE, &protection)) { InterlockedExchangePointer(slot, old); DWORD ignored = 0; VirtualProtect(slot, sizeof(void*), protection, &ignored); } };
    restore(presentSlot, reinterpret_cast<void*>(originalPresent.load())); restore(present1Slot, reinterpret_cast<void*>(originalPresent1.load())); while (calls) Sleep(1); ReleaseTransport();
}
bool Install() {
    DXGI_SWAP_CHAIN_DESC desc{}; desc.BufferCount = 1; desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.OutputWindow = GetDesktopWindow(); desc.SampleDesc.Count = 1; desc.Windowed = TRUE; desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
    IDXGISwapChain* chain = nullptr; ID3D11Device* createdDevice = nullptr; ID3D11DeviceContext* createdContext = nullptr; D3D_FEATURE_LEVEL level{}; const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    if (FAILED(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, levels, ARRAYSIZE(levels), D3D11_SDK_VERSION, &desc, &chain, &createdDevice, &level, &createdContext))) return false;
    auto table = *reinterpret_cast<void***>(chain); presentSlot = &table[8]; void* old = nullptr; const bool installed = Replace(presentSlot, reinterpret_cast<void*>(&PresentHook), &old); if (installed) originalPresent.store(reinterpret_cast<PresentFunction>(old));
    IDXGISwapChain1* chain1 = nullptr; if (installed && SUCCEEDED(chain->QueryInterface(__uuidof(IDXGISwapChain1), reinterpret_cast<void**>(&chain1)))) { auto table1 = *reinterpret_cast<void***>(chain1); present1Slot = &table1[22]; void* old1 = nullptr; if (Replace(present1Slot, reinterpret_cast<void*>(&Present1Hook), &old1)) originalPresent1.store(reinterpret_cast<Present1Function>(old1)); chain1->Release(); }
    createdContext->Release(); createdDevice->Release(); chain->Release(); return installed;
}
void Control() {
    DWORD available = 0, read = 0; wchar_t text[128]{}; const DWORD capacity = sizeof(text) - sizeof(wchar_t); if (pipe == INVALID_HANDLE_VALUE || !PeekNamedPipe(pipe, nullptr, 0, nullptr, &available, nullptr) || !available || !ReadFile(pipe, text, available < capacity ? available : capacity, &read, nullptr)) return;
    const std::wstring command(text, read / sizeof(wchar_t)); if (command.find(L"shutdown") != std::wstring::npos) { Send(L"shutdown-ack\n"); SetEvent(stopEvent); }
    const auto target = command.find(L"target "); if (target != std::wstring::npos) InterlockedExchange(&header->targetFrameRate, ClampRate(_wtoi(command.c_str() + target + 7)));
}
DWORD WINAPI Worker(void*) {
    QueryPerformanceFrequency(&qpcFrequency); mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(SharedHeader), HeaderName().c_str()); header = mapping ? static_cast<SharedHeader*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(SharedHeader))) : nullptr; frameEvent = CreateEventW(nullptr, FALSE, FALSE, EventName().c_str()); if (!header || !frameEvent) return 0; *header = SharedHeader{};
    for (auto i = 0; i < 120 && WaitForSingleObject(stopEvent, 500) == WAIT_TIMEOUT; ++i) { pipe = CreateFileW(PipeName().c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr); if (pipe != INVALID_HANDLE_VALUE) break; }
    if (pipe == INVALID_HANDLE_VALUE) return 0; const auto installed = Install(); Send(installed ? L"hello 1 attached\n" : L"failed hook-install\n"); while (WaitForSingleObject(stopEvent, 20) == WAIT_TIMEOUT) Control(); Restore(); Send(L"stopped\n"); CloseHandle(pipe); if (header) UnmapViewOfFile(header); if (mapping) CloseHandle(mapping); if (frameEvent) CloseHandle(frameEvent); return 0;
}
}
BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) { DisableThreadLibraryCalls(module); stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr); if (stopEvent) CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr); } else if (reason == DLL_PROCESS_DETACH && stopEvent) { SetEvent(stopEvent); CloseHandle(stopEvent); stopEvent = nullptr; } return TRUE; }
