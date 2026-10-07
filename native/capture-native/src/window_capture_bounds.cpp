#include "window_capture_bounds.h"
#include <Windows.h>
#include <dwmapi.h>

namespace clypdat {
std::optional<WindowCaptureBounds> capture_window_bounds(uintptr_t window) {
    const auto hwnd = reinterpret_cast<HWND>(window);
    if (!hwnd || !IsWindow(hwnd) || IsIconic(hwnd)) return {};
    const auto previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    if (!previous) return {};
    struct Restore {
        DPI_AWARENESS_CONTEXT context;
        ~Restore() { SetThreadDpiAwarenessContext(context); }
    } restore{previous};
    RECT outer{}, frame{}, client{};
    if (!GetWindowRect(hwnd, &outer) || !GetClientRect(hwnd, &client)) return {};
    POINT start{client.left, client.top}, end{client.right, client.bottom};
    if (!ClientToScreen(hwnd, &start) || !ClientToScreen(hwnd, &end)) return {};
    if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &frame, sizeof(frame)))) frame = {};
    RECT current_window{}, current_client{};
    if (!GetWindowRect(hwnd, &current_window) || !GetClientRect(hwnd, &current_client) ||
        outer.left != current_window.left || outer.top != current_window.top ||
        outer.right != current_window.right || outer.bottom != current_window.bottom ||
        client.right != current_client.right || client.bottom != current_client.bottom) return {};
    auto rect = [](RECT r) { return CaptureRect{r.left, r.top, r.right - r.left, r.bottom - r.top}; };
    if (end.x <= start.x || end.y <= start.y) return {};
    return WindowCaptureBounds{rect(outer), rect(frame), {start.x, start.y, end.x - start.x, end.y - start.y}};
}
std::optional<CaptureRect> capture_window_region(const WindowCaptureBounds& bounds, CaptureRect content) {
    if (content.width <= 0 || content.height <= 0) return {};
    const auto matches = [&](CaptureRect rect) {
        return rect.width == content.width && rect.height == content.height;
    };
    // WGC normally starts at the visible DWM frame. Some windows supply the
    // full window rect instead. Size must match in both axes, in physical pixels.
    const auto frame = matches(bounds.frame) ? bounds.frame : bounds.window;
    if (!matches(frame)) return {};
    const CaptureRect crop{bounds.client.x - frame.x, bounds.client.y - frame.y,
        bounds.client.width, bounds.client.height};
    if (crop.width <= 0 || crop.height <= 0 || crop.x < 0 || crop.y < 0 ||
        int64_t(crop.x) + crop.width > content.width || int64_t(crop.y) + crop.height > content.height) return {};
    return crop;
}
}
