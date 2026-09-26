#pragma once
#include "output_orientation.h"
#include "recording_capture.h"
#include <chrono>
#include <cstdint>
#include <memory>
#include <string>
struct ID3D11Device;
struct ID3D11Texture2D;

namespace clypdat {
// Owned copies of captured frames for a capture API whose own buffers must be
// released promptly (Windows Graphics Capture). Each frame is copied, or
// tone-mapped from FP16, into a BGRA texture from a bounded pool of reusable
// textures, so the API buffer goes back at once. A copy returns to the pool
// when its last CapturePixels owner lets it go: no texture is rewritten while
// acquisition, source selection, pacing, the detector or the encoder still
// holds it.
//
// The newest copy waits in one slot until taken, and a newer frame
// supersedes it in place. When every pooled texture is still held
// downstream, a new frame is dropped and counted; nothing is allocated past
// the capacity. A size change rebuilds the pool; textures of the old size
// are discarded as they come back.
class CapturedFrameStore {
public:
    struct Stats {
        int capacity = 0, leased = 0, peak_leased = 0;
        // Pool textures created (plus the HDR crop scratch), frames published,
        // untaken frames superseded in place, frames dropped for pool pressure.
        uint64_t allocated = 0, delivered = 0, superseded = 0, pressure_drops = 0;
        double copy_p50_ms = 0, copy_p95_ms = 0; // CPU time to issue each copy or tone-map.
    };
    // `region` crops every frame; an empty region keeps the whole frame. FP16
    // frames are tone-mapped for `sdr_white_nits`.
    CapturedFrameStore(ID3D11Device* device, int capacity, float sdr_white_nits, CaptureRect region = {});
    ~CapturedFrameStore();
    CapturedFrameStore(const CapturedFrameStore&) = delete;
    CapturedFrameStore& operator=(const CapturedFrameStore&) = delete;

    // When a frame reached the capture API's callback, in QPC ticks; the
    // store adds when it was published (copy issued).
    struct Timing { int64_t callback_qpc = 0, taken_qpc = 0, published_qpc = 0, dwm_vblank_qpc = 0, dwm_compose_qpc = 0; };
    // Copies `input` and publishes it as the newest frame, stamped
    // `timestamp`. Returns false when the frame was dropped: the region lies
    // outside it, or every pooled texture is held. Keeps no reference to
    // `input`, so the caller may release its buffer on return.
    bool deliver(ID3D11Texture2D* input, int64_t timestamp, const Timing& timing = {});
    // Copies `crop` of `input` (all of it when empty) into a pooled texture
    // and returns it without publishing (Desktop Duplication hands it
    // downstream itself). Null when the crop lies outside `input` or every
    // pooled texture is held (counted as a pressure drop). Keeps no
    // reference to `input`. Crops of a new size rebuild the pool as
    // deliver() does for a new frame size.
    //
    // `input` in a rotated output's scanout orientation is turned upright:
    // `crop` is then in the output's desktop orientation (see
    // output_orientation.h), and so is the copy.
    std::shared_ptr<ID3D11Texture2D> copy(ID3D11Texture2D* input, CaptureRect crop = {}, OutputRotation rotation = OutputRotation::Identity);
    // Takes the newest frame, waiting up to `timeout`. False when none
    // arrived or the store is closed; throws the error set by fail().
    bool take(CapturePixels& pixels, int64_t& timestamp, std::chrono::milliseconds timeout, Timing* timing = nullptr);
    void fail(const std::string& error); // Wakes take() with an error.
    void close();                        // Drops the newest frame; take() returns false.
    void reset();                        // Clears the error and the newest frame.
    bool closed() const;
    Stats stats() const;
private:
    struct Impl;
    std::shared_ptr<Impl> impl_;
};
}
