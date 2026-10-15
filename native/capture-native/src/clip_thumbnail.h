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
}
