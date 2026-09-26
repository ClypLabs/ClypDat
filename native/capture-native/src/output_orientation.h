#pragma once
#include "recording_capture.h"

namespace clypdat {
// Desktop Duplication hands each frame in its output's scanout orientation,
// while DXGI_OUTPUT_DESC::DesktopCoordinates, window rectangles, capture
// regions and the cursor are all in desktop orientation. The output's
// rotation (DXGI_MODE_ROTATION, identical in DXGI_OUTDUPL_DESC) is how the
// desktop is turned onto the scanout; for 90 and 270 degrees the frame
// texture is as wide as the desktop is tall. DXGI_OUTDUPL_DESC::ModeDesc
// carries the desktop-orientation size, not the texture's.
//
// For a desktop W wide and H tall, desktop pixel (x, y), relative to the
// output's top-left, is held by texel:
//   identity: (x, y)
//   90:       (y, W-1-x)
//   180:      (W-1-x, H-1-y)
//   270:      (H-1-y, x)
// (the inverse of the dirty-rectangle rotation in Microsoft's
// DesktopDuplication sample). A portrait display on this project's test
// machine reports 270 and matches that mapping.
enum class OutputRotation { Identity = 0, Rotate90 = 1, Rotate180 = 2, Rotate270 = 3 };
// From DXGI_MODE_ROTATION: UNSPECIFIED and IDENTITY are both identity.
OutputRotation output_rotation(int dxgi_mode_rotation);
inline bool output_rotation_swaps(OutputRotation rotation) { return rotation == OutputRotation::Rotate90 || rotation == OutputRotation::Rotate270; }
struct OutputSize { int width = 0, height = 0; };
struct OutputPoint { int x = 0, y = 0; };
// Texture size for a desktop size and back; a quarter turn swaps them.
OutputSize output_texture_size(OutputSize desktop, OutputRotation rotation);
OutputSize output_desktop_size(OutputSize texture, OutputRotation rotation);
OutputPoint output_desktop_to_texel(OutputPoint desktop, OutputSize desktop_size, OutputRotation rotation);
OutputPoint output_texel_to_desktop(OutputPoint texel, OutputSize desktop_size, OutputRotation rotation);
// The texels holding a desktop rectangle.
CaptureRect output_desktop_rect_to_texture(CaptureRect rect, OutputSize desktop_size, OutputRotation rotation);
// A rectangle in virtual-desktop coordinates relative to an output whose
// top-left lies at (left, top); either may be negative.
inline CaptureRect output_relative(CaptureRect rect, int left, int top) { return {rect.x - left, rect.y - top, rect.width, rect.height}; }
inline bool output_contains(CaptureRect rect, OutputSize size) {
    return rect.width > 0 && rect.height > 0 && rect.x >= 0 && rect.y >= 0 && int64_t(rect.x) + rect.width <= size.width && int64_t(rect.y) + rect.height <= size.height;
}
}
