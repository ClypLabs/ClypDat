#pragma once
#include <d3d11.h>
#include <dxgi.h>
#include <cstdint>
#include <cstdio>
#include <stdexcept>
#include <string>

namespace clypdat {
inline bool is_device_loss(HRESULT hr) noexcept {
    return hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET ||
        hr == DXGI_ERROR_DEVICE_HUNG || hr == DXGI_ERROR_DRIVER_INTERNAL_ERROR;
}
class GraphicsError : public std::runtime_error {
public:
    const HRESULT result;
    GraphicsError(HRESULT hr, const char* operation) : std::runtime_error(message(hr, operation)), result(hr) { }
private:
    static std::string message(HRESULT hr, const char* operation) {
        char code[16]{}; std::snprintf(code, sizeof(code), "0x%08X", unsigned(hr));
        return std::string(operation) + " (hr=" + code + ")";
    }
};
inline void check_graphics(HRESULT hr, const char* operation) {
    if (FAILED(hr)) throw GraphicsError(hr, operation);
}
}
