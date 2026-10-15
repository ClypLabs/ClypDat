#pragma once
#include <cstdint>
#include <filesystem>

namespace clypdat {
struct ClipThumbnailConfig {
    std::filesystem::path path, output;
    int64_t at_us = 0;
    int width = 960;
    int quality = 4;
};

// Decodes the first frame at or after at_us on the calling thread, with one
// decode thread, and writes it to output as a JPEG. Throws on failure; output
// is replaced only once the whole image has been written.
void write_clip_thumbnail(const ClipThumbnailConfig& config);

struct ClipFilmstripConfig {
    std::filesystem::path path, output;
    int64_t duration_us = 0; // 0: the container's own duration.
    int frame_count = 10;
    int frame_height = 160;
    int quality = 2;
};

// Samples frame_count frames evenly across the clip, (i + 0.5) / frame_count
// of the way through, and writes them side by side as one JPEG. Points that
// fail to decode borrow their nearest decoded neighbour. Returns how many
// points decoded; throws when none did.
int write_clip_filmstrip(const ClipFilmstripConfig& config);
}
