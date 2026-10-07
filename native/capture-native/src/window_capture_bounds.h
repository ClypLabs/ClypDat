#pragma once
#include "recording_capture.h"
#include <optional>

namespace clypdat {
// Screen coordinates in physical pixels. The window includes invisible resize
// borders; the frame is DWM's visible bounds; the client excludes decorations.
struct WindowCaptureBounds {
    CaptureRect window, frame, client;
};
std::optional<WindowCaptureBounds> capture_window_bounds(uintptr_t window);
// Match the frame's ContentSize, not its texture allocation. An unmatched size
// during a resize is skipped rather than guessing an origin or including chrome.
std::optional<CaptureRect> capture_window_region(const WindowCaptureBounds& bounds, CaptureRect content);
}
