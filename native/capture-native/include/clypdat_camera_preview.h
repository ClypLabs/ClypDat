#pragma once
#include "clypdat_capture_native.h"

typedef struct cd_camera_preview cd_camera_preview;
typedef struct cd_camera_preview_config {
    uint32_t size, abi;
    const uint16_t* ffmpeg_path;
    uint32_t ffmpeg_length, reserved0;
    const uint16_t* work_directory;
    uint32_t work_directory_length, reserved1;
    int64_t qpc_anchor, qpc_frequency;
} cd_camera_preview_config;
typedef struct cd_camera_preview_frame {
    uint32_t size, abi;
    uint32_t width, height, stride, required_bytes;
    uint64_t sequence;
    int64_t timestamp_qpc;
    uint32_t running, reserved;
} cd_camera_preview_frame;

CD_API int32_t CD_CALL cd_camera_preview_create(const cd_camera_preview_config* config, cd_camera_preview** result);
CD_API int32_t CD_CALL cd_camera_preview_start(cd_camera_preview* preview, const uint16_t* device, uint32_t device_length);
CD_API int32_t CD_CALL cd_camera_preview_stop(cd_camera_preview* preview);
// Set frame.sequence to the last consumed sequence to coalesce repeat reads.
// Idle/starting cameras and unchanged frames report zero required bytes.
CD_API int32_t CD_CALL cd_camera_preview_copy(cd_camera_preview* preview, cd_camera_preview_frame* frame, uint8_t* pixels, uint32_t capacity);
CD_API int32_t CD_CALL cd_camera_preview_error(cd_camera_preview* preview, uint8_t* utf8, uint32_t capacity, uint32_t* required);
// Releases the external handle even when a timed-out owner must remain alive.
CD_API int32_t CD_CALL cd_camera_preview_release(cd_camera_preview* preview);
