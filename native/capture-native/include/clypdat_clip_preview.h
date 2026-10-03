#pragma once
#include "clypdat_capture_native.h"

// Library hover previews: decodes one clip range in-process to card-sized
// RGBA frames. The decoder loops over the range until stopped.
typedef struct cd_clip_preview cd_clip_preview;
typedef struct cd_clip_preview_config {
    uint32_t size, abi;
    const uint16_t* path;
    uint32_t path_length;
    uint32_t paced; // 1: wall-clock pacing, newest frame only. 0: every frame waits to be taken.
    int64_t start_us, duration_us;
    uint32_t width, height, fps, reserved0;
    // Saved crop in source pixels; zero width/height covers the canvas instead.
    int32_t crop_x, crop_y, crop_width, crop_height;
} cd_clip_preview_config;
typedef struct cd_clip_preview_frame {
    uint32_t size, abi;
    // In: the last sequence consumed. Out: the copied frame's sequence, or 0
    // when none arrived within timeout_ms.
    uint64_t sequence;
    uint64_t frames_per_loop, source_bytes_read;
    uint32_t timeout_ms;
    uint32_t finished; // The decoder ended (stopped, or failed - see error).
} cd_clip_preview_frame;

CD_API int32_t CD_CALL cd_clip_preview_open(const cd_clip_preview_config* config, cd_clip_preview** result);
// Blocks up to frame->timeout_ms. Safe to call while another thread stops the preview.
CD_API int32_t CD_CALL cd_clip_preview_take(cd_clip_preview* preview, cd_clip_preview_frame* frame, uint8_t* rgba, uint32_t capacity);
// Ends decoding and wakes any waiting take. Release still owns the memory.
CD_API int32_t CD_CALL cd_clip_preview_stop(cd_clip_preview* preview);
CD_API int32_t CD_CALL cd_clip_preview_error(cd_clip_preview* preview, uint8_t* utf8, uint32_t capacity, uint32_t* required);
CD_API int32_t CD_CALL cd_clip_preview_release(cd_clip_preview* preview);
