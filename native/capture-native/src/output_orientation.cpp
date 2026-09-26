#include "output_orientation.h"

namespace clypdat {
OutputRotation output_rotation(int dxgi_mode_rotation) {
    // DXGI_MODE_ROTATION_ROTATE90 = 2, ROTATE180 = 3, ROTATE270 = 4.
    switch (dxgi_mode_rotation) {
    case 2: return OutputRotation::Rotate90;
    case 3: return OutputRotation::Rotate180;
    case 4: return OutputRotation::Rotate270;
    default: return OutputRotation::Identity;
    }
}
OutputSize output_texture_size(OutputSize desktop, OutputRotation rotation) {
    return output_rotation_swaps(rotation) ? OutputSize{desktop.height, desktop.width} : desktop;
}
OutputSize output_desktop_size(OutputSize texture, OutputRotation rotation) {
    return output_rotation_swaps(rotation) ? OutputSize{texture.height, texture.width} : texture;
}
OutputPoint output_desktop_to_texel(OutputPoint p, OutputSize size, OutputRotation rotation) {
    switch (rotation) {
    case OutputRotation::Rotate90: return {p.y, size.width - 1 - p.x};
    case OutputRotation::Rotate180: return {size.width - 1 - p.x, size.height - 1 - p.y};
    case OutputRotation::Rotate270: return {size.height - 1 - p.y, p.x};
    default: return p;
    }
}
OutputPoint output_texel_to_desktop(OutputPoint t, OutputSize size, OutputRotation rotation) {
    switch (rotation) {
    case OutputRotation::Rotate90: return {size.width - 1 - t.y, t.x};
    case OutputRotation::Rotate180: return {size.width - 1 - t.x, size.height - 1 - t.y};
    case OutputRotation::Rotate270: return {t.y, size.height - 1 - t.x};
    default: return t;
    }
}
CaptureRect output_desktop_rect_to_texture(CaptureRect r, OutputSize size, OutputRotation rotation) {
    switch (rotation) {
    case OutputRotation::Rotate90: return {r.y, size.width - r.x - r.width, r.height, r.width};
    case OutputRotation::Rotate180: return {size.width - r.x - r.width, size.height - r.y - r.height, r.width, r.height};
    case OutputRotation::Rotate270: return {size.height - r.y - r.height, r.x, r.height, r.width};
    default: return r;
    }
}
}
