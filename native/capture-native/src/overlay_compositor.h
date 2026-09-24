#pragma once
#include "recording_overlays.h"
#include <cstdint>
#include <memory>
struct ID3D11Device;
struct ID3D11RenderTargetView;

namespace clypdat {
// Draws burned overlay layers onto an output-sized BGRA canvas on the GPU,
// for encoders that take GPU surfaces. Placement and nearest-texel mapping
// match compose_overlay_nv12, camera below keyboard; each bitmap's straight
// or premultiplied alpha is blended by fixed-function blend state.
//
// Every layer keeps one texture that is uploaded only when its bitmap
// changes, so static keyboard artwork is uploaded once and each camera frame
// once. Shaders, states, views and the constant buffer are made once.
class OverlayCompositor {
public:
    struct Stats { uint64_t uploads = 0, upload_failures = 0, allocations = 0; };
    // Throws when the shaders or states cannot be created on `device`.
    explicit OverlayCompositor(ID3D11Device* device);
    ~OverlayCompositor();
    OverlayCompositor(const OverlayCompositor&) = delete;
    OverlayCompositor& operator=(const OverlayCompositor&) = delete;
    // Draws the requested layers that have a bitmap onto `canvas`, a
    // width x height render target. A layer whose upload fails is skipped,
    // never drawn from an earlier bitmap. The caller holds the device's
    // multithread lock.
    OverlayCompositionResult draw(ID3D11RenderTargetView* canvas, int width, int height, const OverlayFrame& layers);
    Stats stats() const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
}
