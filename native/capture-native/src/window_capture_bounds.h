#pragma once
#include "recording_capture.h"
#include <optional>

namespace clypdat {
// Screen coordinates in physical pixels. The window includes invisible resize
// borders; the frame is DWM's visible bounds; the client excludes decorations.
struct WindowCaptureBounds {
    CaptureRect window, frame, client;
};
// Retries a move/resize that races the queries; null when minimised or gone.
std::optional<WindowCaptureBounds> capture_window_bounds(uintptr_t window);
// Match the frame's ContentSize, not its texture allocation. An unmatched size
// during a resize is skipped rather than guessing an origin or including chrome.
std::optional<CaptureRect> capture_window_region(const WindowCaptureBounds& bounds, CaptureRect content);
// Maps the client by the frame-to-content scale, for content that is the
// window at another uniform scale (a DPI-virtualised window composed at its
// logical size). Null when the axes scale differently.
std::optional<CaptureRect> capture_window_scaled_region(const WindowCaptureBounds& bounds, CaptureRect content);

// Picks each WGC window frame's crop. A size matching neither rect is a
// resize race and is skipped; once the same mismatch persists it is not a
// race, so the client is mapped by scale rather than recording nothing.
class WindowCropSelector {
public:
    static constexpr int persistent_frames = 8;
    std::optional<CaptureRect> select(const std::optional<WindowCaptureBounds>& bounds, CaptureRect content);
    uint64_t skipped() const { return skipped_; }
    uint64_t scaled() const { return scaled_; }
private:
    CaptureRect miss_content_, miss_frame_;
    int streak_ = 0;
    uint64_t skipped_ = 0, scaled_ = 0;
};
}
