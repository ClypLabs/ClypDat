#pragma once
#include <cstdint>
#include <filesystem>
#include <memory>
#include <string>

namespace clypdat {
// One hovered library card: decodes a clip range in-process and keeps only the
// newest card-sized RGBA frame. Replaces an ffmpeg.exe process per hover whose
// raw frames crossed a pipe.
struct ClipPreviewConfig {
    std::filesystem::path path;
    int64_t start_us = 0, duration_us = 0;
    int width = 0, height = 0, fps = 60;
    // Saved crop edit in source pixels. Empty: cover the canvas (scale up,
    // centre crop). Set: crop, then fit inside the canvas with black bars.
    int crop_x = 0, crop_y = 0, crop_width = 0, crop_height = 0;
    // Paced: frames follow wall-clock time and unread ones are replaced.
    // Unpaced: every frame waits to be taken (tests and reference decodes).
    bool paced = true;
};

class ClipPreviewDecoder {
public:
    explicit ClipPreviewDecoder(ClipPreviewConfig config);
    ~ClipPreviewDecoder();
    ClipPreviewDecoder(const ClipPreviewDecoder&) = delete;
    ClipPreviewDecoder& operator=(const ClipPreviewDecoder&) = delete;

    // Copies the newest frame whose sequence differs from `after`, waiting up
    // to timeout_ms for one. Returns its sequence, or 0 when none arrived.
    // Sequence 1 is the range's first frame; it keeps counting across loops,
    // so (sequence - 1) % frames_per_loop() is the position in the range.
    uint64_t take(uint64_t after, uint8_t* rgba, size_t capacity, uint32_t timeout_ms);
    void stop();
    bool finished() const;
    std::string error() const;
    uint64_t source_bytes_read() const;
    uint64_t frames_per_loop() const;
    size_t frame_bytes() const;

private:
    struct State;
    std::shared_ptr<State> state_;
};

// Output frames per pass over the range: ceil(duration * fps).
uint64_t clip_preview_frames_per_loop(int64_t duration_us, int fps);
}
