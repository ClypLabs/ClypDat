#pragma once
#include "clypdat_capture_native.h"

// Library card thumbnails: decodes one frame of a clip in-process and writes it
// as a JPEG. Replaces an ffmpeg.exe process (and its full decode thread pool)
// per thumbnail, so a clip saved mid-game can get its card image straight away.
typedef struct cd_clip_thumbnail_config {
    uint32_t size, abi;
    const uint16_t* path;
    uint32_t path_length;
    const uint16_t* output;
    uint32_t output_length;
    int64_t at_us;       // Source time of the frame; 0 is the clip's first frame.
    uint32_t width;      // Output width; height follows the aspect, rounded to even.
    uint32_t quality;    // JPEG qscale, 2 (best) to 31.
    uint32_t background; // 1: run at background CPU, I/O and memory priority.
    uint32_t reserved0;
} cd_clip_thumbnail_config;

// Blocks the calling thread until the JPEG is written. On failure, the reason
// is copied to error_utf8 when it fits; error_required reports its length.
CD_API int32_t CD_CALL cd_clip_thumbnail_write(const cd_clip_thumbnail_config* config,
    uint8_t* error_utf8, uint32_t error_capacity, uint32_t* error_required);

// Editor timeline filmstrips: frame_count frames sampled evenly across the
// clip, side by side in one JPEG (each frame_height tall, aspect kept). One
// open and mostly one keyframe decode per frame, instead of one ffmpeg.exe
// seeking a full GOP for each.
typedef struct cd_clip_filmstrip_config {
    uint32_t size, abi;
    const uint16_t* path;
    uint32_t path_length;
    const uint16_t* output;
    uint32_t output_length;
    int64_t duration_us;   // 0: the container's own duration.
    uint32_t frame_count;  // 1 to 80.
    uint32_t frame_height;
    uint32_t quality;      // JPEG qscale, 2 (best) to 31.
    uint32_t background;   // 1: run at background CPU, I/O and memory priority.
    uint32_t decoded;      // Out: frames that decoded; the rest borrowed a neighbour.
    uint32_t reserved0;
} cd_clip_filmstrip_config;

CD_API int32_t CD_CALL cd_clip_filmstrip_write(cd_clip_filmstrip_config* config,
    uint8_t* error_utf8, uint32_t error_capacity, uint32_t* error_required);
