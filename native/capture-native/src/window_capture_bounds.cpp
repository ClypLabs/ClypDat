#include "window_capture_bounds.h"
#include <Windows.h>
#include <dwmapi.h>
#include <algorithm>
#include <cmath>

namespace clypdat {
namespace {
std::optional<WindowCaptureBounds> query_window_bounds(HWND hwnd) {
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
// WGC normally starts at the visible DWM frame; without one, the window rect.
CaptureRect reference_rect(const WindowCaptureBounds& bounds) {
    return bounds.frame.width > 0 && bounds.frame.height > 0 ? bounds.frame : bounds.window;
}
bool within(CaptureRect crop, CaptureRect content) {
    return crop.width > 0 && crop.height > 0 && crop.x >= 0 && crop.y >= 0 &&
        int64_t(crop.x) + crop.width <= content.width && int64_t(crop.y) + crop.height <= content.height;
}
}
std::optional<WindowCaptureBounds> capture_window_bounds(uintptr_t window) {
    const auto hwnd = reinterpret_cast<HWND>(window);
    if (!hwnd || !IsWindow(hwnd) || IsIconic(hwnd)) return {};
    const auto previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    if (!previous) return {};
    struct Restore {
        DPI_AWARENESS_CONTEXT context;
        ~Restore() { SetThreadDpiAwarenessContext(context); }
    } restore{previous};
    // A drag moves the window between queries; a few retries settle it.
    for (int attempt = 0; attempt < 3; ++attempt)
        if (const auto bounds = query_window_bounds(hwnd)) return bounds;
    return {};
}
std::optional<CaptureRect> capture_window_region(const WindowCaptureBounds& bounds, CaptureRect content) {
    if (content.width <= 0 || content.height <= 0) return {};
    const auto matches = [&](CaptureRect rect) {
        return rect.width == content.width && rect.height == content.height;
    };
    // Some windows supply the full window rect even with a DWM frame. Size
    // must match in both axes, in physical pixels.
    const auto frame = matches(bounds.frame) ? bounds.frame : bounds.window;
    if (!matches(frame)) return {};
    const CaptureRect crop{bounds.client.x - frame.x, bounds.client.y - frame.y,
        bounds.client.width, bounds.client.height};
    if (!within(crop, content)) return {};
    return crop;
}
std::optional<CaptureRect> capture_window_scaled_region(const WindowCaptureBounds& bounds, CaptureRect content) {
    const auto frame = reference_rect(bounds);
    if (content.width <= 0 || content.height <= 0 || frame.width <= 0 || frame.height <= 0) return {};
    const double sx = double(content.width) / frame.width, sy = double(content.height) / frame.height;
    // Rounding of a scaled window differs by a pixel at most; a larger gap
    // is a different shape, not a scale.
    if (std::abs(sx - sy) > 0.01 * std::max(sx, sy)) return {};
    CaptureRect crop{int(std::lround((bounds.client.x - frame.x) * sx)), int(std::lround((bounds.client.y - frame.y) * sy)),
        int(std::lround(bounds.client.width * sx)), int(std::lround(bounds.client.height * sy))};
    crop.width = std::min(crop.width, content.width - crop.x);
    crop.height = std::min(crop.height, content.height - crop.y);
    if (!within(crop, content)) return {};
    return crop;
}
std::optional<CaptureRect> WindowCropSelector::select(const std::optional<WindowCaptureBounds>& bounds, CaptureRect content) {
    if (bounds) if (const auto crop = capture_window_region(*bounds, content)) { streak_ = 0; return crop; }
    const auto frame = bounds ? reference_rect(*bounds) : CaptureRect{};
    if (bounds && content == miss_content_ && frame.width == miss_frame_.width && frame.height == miss_frame_.height) ++streak_;
    else { miss_content_ = content; miss_frame_ = frame; streak_ = bounds ? 1 : 0; }
    if (bounds && streak_ >= persistent_frames)
        if (const auto crop = capture_window_scaled_region(*bounds, content)) { ++scaled_; return crop; }
    ++skipped_;
    return {};
}
}
