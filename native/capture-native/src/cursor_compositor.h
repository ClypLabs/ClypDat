#pragma once
#include <Windows.h>
#include <cstdint>
#include <memory>
struct ID3D11Device;
struct ID3D11Texture2D;

namespace clypdat {
// The system cursor as GetCursorInfo reports it: handle, hotspot position in
// screen pixels, and whether it is showing.
struct CursorState { HCURSOR handle = nullptr; POINT position{}; bool showing = false; };
CursorState capture_cursor_state();

// Draws `cursor` into BGRA `target` (width x height), whose pixel (0,0) is
// screen pixel (origin_x, origin_y), the way Desktop Duplication recording
// always has: the cursor's rectangle is read back to the CPU, drawn with GDI
// DrawIconEx and uploaded again. The reference for CursorCompositor and its
// fallback. False when nothing was drawn. Adds the bytes read back.
bool cursor_draw_cpu(ID3D11Texture2D* target, int width, int height, int origin_x, int origin_y, const CursorState& cursor,
    uint64_t* readback_bytes = nullptr);

struct CursorCompositorStats {
    // Shapes uploaded after a handle or bitmap change, and re-reads of an
    // unchanged handle's bitmaps; draws by path; cursor textures created.
    uint64_t shape_changes = 0, uploads = 0, revalidations = 0;
    // cpu_fallbacks: CPU draws of shapes the GPU path does not reproduce;
    // cpu_draws: every CPU draw, fallbacks and the reference path.
    uint64_t gpu_draws = 0, cpu_fallbacks = 0, cpu_draws = 0, hidden = 0, outside = 0, failed = 0;
    uint64_t textures_created = 0, readback_bytes = 0;
    double compose_p50_ms = 0, compose_p95_ms = 0, lock_wait_p95_ms = 0;
};
// The system cursor composed on the GPU with DrawIconEx's exact integer
// arithmetic, so frames match cursor_draw_cpu byte for byte: colour cursors
// with alpha blend as floor(s*a/255) + round(d*(255-a)/255) (alpha
// a + round(d*(255-a)/255)); masked-colour and monochrome cursors become
// (d & mask) ^ colour with the box's alpha cleared. The shape is uploaded
// once per cursor handle and re-read every two seconds in case the handle's
// bitmaps change; a moving cursor only moves the draw. Colour bitmaps below
// 32 bpp and cursors over 256 pixels use cursor_draw_cpu.
class CursorCompositor {
public:
    explicit CursorCompositor(ID3D11Device* device);
    ~CursorCompositor();
    CursorCompositor(const CursorCompositor&) = delete;
    CursorCompositor& operator=(const CursorCompositor&) = delete;
    enum class Result { Hidden, Outside, Gpu, Cpu, Failed };
    Result draw(ID3D11Texture2D* target, int width, int height, int origin_x, int origin_y, const CursorState& cursor);
    // cursor_draw_cpu with a reused readback texture (the reference path).
    Result draw_cpu(ID3D11Texture2D* target, int width, int height, int origin_x, int origin_y, const CursorState& cursor);
    void reset(); // Forgets the shape and cached views.
    CursorCompositorStats stats() const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
}
