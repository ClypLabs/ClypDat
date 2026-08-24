#include <Windows.h>
#include <d3d11.h>
#include <dxgi.h>

#include <atomic>
#include <cstdint>
#include <string>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

namespace
{
    using PresentFunction = HRESULT(__stdcall*)(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags);

    std::atomic<PresentFunction> originalPresent = nullptr;
    std::atomic_uint64_t presentCount = 0;
    HANDLE stopEvent = nullptr;

    bool WritePipeMessage(HANDLE pipe, const std::wstring& message)
    {
        const auto byteCount = static_cast<DWORD>(message.size() * sizeof(wchar_t));
        DWORD written = 0;
        return WriteFile(pipe, message.data(), byteCount, &written, nullptr) != FALSE && written == byteCount;
    }

    HRESULT __stdcall PresentHook(IDXGISwapChain* swapChain, UINT syncInterval, UINT flags)
    {
        presentCount.fetch_add(1, std::memory_order_relaxed);
        const auto original = originalPresent.load(std::memory_order_acquire);
        return original == nullptr ? DXGI_ERROR_INVALID_CALL : original(swapChain, syncInterval, flags);
    }

    bool InstallPresentHook()
    {
        DXGI_SWAP_CHAIN_DESC description{};
        description.BufferCount = 1;
        description.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        description.OutputWindow = GetDesktopWindow();
        description.SampleDesc.Count = 1;
        description.Windowed = TRUE;
        description.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        IDXGISwapChain* swapChain = nullptr;
        ID3D11Device* device = nullptr;
        ID3D11DeviceContext* context = nullptr;
        D3D_FEATURE_LEVEL featureLevel{};
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        const auto result = D3D11CreateDeviceAndSwapChain(
            nullptr,
            D3D_DRIVER_TYPE_HARDWARE,
            nullptr,
            0,
            levels,
            ARRAYSIZE(levels),
            D3D11_SDK_VERSION,
            &description,
            &swapChain,
            &device,
            &featureLevel,
            &context);

        if (FAILED(result)) return false;

        auto virtualTable = *reinterpret_cast<void***>(swapChain);
        constexpr size_t PresentSlot = 8;
        auto current = reinterpret_cast<PresentFunction>(virtualTable[PresentSlot]);
        if (current == nullptr)
        {
            context->Release();
            device->Release();
            swapChain->Release();
            return false;
        }

        DWORD oldProtection = 0;
        const auto writable = VirtualProtect(&virtualTable[PresentSlot], sizeof(void*), PAGE_EXECUTE_READWRITE, &oldProtection) != FALSE;
        if (writable)
        {
            originalPresent.store(current, std::memory_order_release);
            InterlockedExchangePointer(&virtualTable[PresentSlot], reinterpret_cast<void*>(&PresentHook));
            DWORD ignored = 0;
            VirtualProtect(&virtualTable[PresentSlot], sizeof(void*), oldProtection, &ignored);
        }

        context->Release();
        device->Release();
        swapChain->Release();
        return writable;
    }

    DWORD WINAPI HookWorker(void*)
    {
        const auto pipeName = L"\\\\.\\pipe\\ClypDat-GameHook-" + std::to_wstring(GetCurrentProcessId());
        HANDLE pipe = INVALID_HANDLE_VALUE;
        for (auto attempt = 0; attempt < 120 && WaitForSingleObject(stopEvent, 500) == WAIT_TIMEOUT; ++attempt)
        {
            pipe = CreateFileW(pipeName.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            if (pipe != INVALID_HANDLE_VALUE) break;
        }
        if (pipe == INVALID_HANDLE_VALUE) return 0;

        const auto installed = InstallPresentHook();
        WritePipeMessage(pipe, installed ? L"attached\n" : L"hook-failed\n");

        while (WaitForSingleObject(stopEvent, 1000) == WAIT_TIMEOUT)
        {
            if (!WritePipeMessage(pipe, L"present=" + std::to_wstring(presentCount.load(std::memory_order_relaxed)) + L"\n")) break;
        }

        CloseHandle(pipe);
        return 0;
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (stopEvent != nullptr) CreateThread(nullptr, 0, HookWorker, nullptr, 0, nullptr);
    }
    else if (reason == DLL_PROCESS_DETACH && stopEvent != nullptr)
    {
        SetEvent(stopEvent);
        CloseHandle(stopEvent);
        stopEvent = nullptr;
    }
    return TRUE;
}
