#pragma once
extern "C" {
#include <libavutil/frame.h>
#include <libavutil/pixfmt.h>
}
#include <cstdint>
#include <memory>
struct ID3D11Device;

namespace clypdat {
struct AVFrameDeleter { void operator()(AVFrame* frame) const { av_frame_free(&frame); } };
using OwnedFrame = std::unique_ptr<AVFrame, AVFrameDeleter>;

struct ReadbackResult {
    OwnedFrame frame;
    double map_wait_ms = 0; // Waiting for the GPU copy to finish.
    double copy_ms = 0;     // Copying the mapped rows into the CPU frame.
    bool stalled = false;   // The copy was still running when first mapped.
};

// System-memory frames for encoders without zero-copy input (libx264 and the
// NVENC, AMF and QSV readback forms), from fixed resources the EncoderPlan
// sizes: reusable CPU frames, plus D3D11 staging textures when frames come
// from the GPU. Everything is allocated in the constructor.
//
// A staged frame is read back only once the next frame has been staged, so
// the GPU copies frame N while the CPU reads frame N-1 instead of stalling on
// Map every frame. Frame properties (pts, duration, colour) travel with the
// staged frame.
//
// A CPU frame is reused only once nothing else references it, so an encoder
// or FFmpeg still holding one is never overwritten. The encoding thread owns
// the stage.
class ReadbackStage {
public:
    // Without a device the stage has no staging textures and serves only
    // CPU-converted frames.
    ReadbackStage(ID3D11Device* device, int width, int height, AVPixelFormat format, int staging_slots, int cpu_frames);
    ~ReadbackStage();
    ReadbackStage(const ReadbackStage&) = delete;
    ReadbackStage& operator=(const ReadbackStage&) = delete;

    ID3D11Device* device() const;
    int width() const;
    int height() const;
    AVPixelFormat format() const;
    int staging_slots() const;
    int cpu_frames() const;
    int pending() const;            // Staged frames not yet read back.
    int cpu_frames_in_use() const;  // CPU frames something still references.
    bool cpu_frame_available() const;
    uint64_t allocations() const;   // Staging textures and CPU frame payloads.

    // Queues a GPU copy of a D3D11 frame's texture slice into a free staging
    // texture and keeps the frame's properties. Throws std::logic_error when
    // every staging texture still holds an unread frame.
    void stage(const AVFrame& d3d11_frame);
    // A free CPU frame sharing its payload with the stage; null when every
    // CPU frame is still referenced.
    OwnedFrame acquire();
    // The oldest staged frame, read into a free CPU frame. The result's frame
    // is null, and the staged frame kept, when no CPU frame is free.
    ReadbackResult read();
    // Discards the oldest staged frame unread.
    void drop_oldest();
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
}
