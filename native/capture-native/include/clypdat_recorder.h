#pragma once
#include "clypdat_capture_native.h"

#pragma pack(push, 8)
typedef struct cd_string16 { const uint16_t* data; uint32_t length; uint32_t reserved; } cd_string16;
typedef struct cd_strings16 { const cd_string16* data; uint32_t count; uint32_t reserved; } cd_strings16;
typedef struct cd_audio_application { cd_string16 name; int32_t gain_percent; uint32_t reserved; } cd_audio_application;
typedef struct cd_bytes { uint8_t* data; uint32_t capacity; uint32_t required; } cd_bytes;
typedef struct cd_rect { int32_t x, y, width, height; } cd_rect;
typedef struct cd_session_config {
    cd_struct_header header;
    int64_t qpc_anchor, qpc_frequency, utc_anchor_ticks;
    uint8_t boot_id[16];
    uint64_t target_window, target_monitor;
    int32_t duration_seconds, max_height, frame_rate, capture_x, capture_y, capture_width, capture_height;
    int32_t bitrate_mbps, game_gain_percent, microphone_gain_percent, full_session_quota_gb;
    uint32_t flags;
    double microphone_gate_db;
    cd_string16 chat_device_name, chat_device_id, microphone_device_name;
    cd_strings16 chat_processes, microphone_devices, excluded_processes;
    const cd_audio_application* applications;
    uint32_t application_count, reserved;
    cd_string16 game_name, game_executable, game_window_title, game_window_class;
    cd_string16 video_codec, encoder_mode, encoder_profile, frame_rate_mode, pacing_mode;
    cd_string16 capture_source, monitor_device_name, process_priority, microphone_channel_mode;
    cd_string16 work_directory, ffmpeg_path, rnnoise_model, full_session_path;
    cd_string16 full_session_codec, full_session_container, library_folder;
    cd_string16 file_name_scheme, custom_file_name_template, save_hotkey, full_session_hotkey;
    cd_string16 diagnostic_force_dxgi, diagnostic_disable_direct_blt, diagnostic_pacing_policy;
    cd_string16 diagnostic_nvenc_delay, diagnostic_d3d_debug;
} cd_session_config;
enum cd_session_flags {
    CD_SESSION_CURSOR = 1, CD_SESSION_FULL = 2, CD_SESSION_BACKGROUND_FINALIZE = 4,
    CD_SESSION_NOISE_SUPPRESSION = 8, CD_SESSION_ADAPTIVE_FPS = 16, CD_SESSION_HDR = 32
};
typedef struct cd_session_contract {
    cd_struct_header header;
    uint32_t pointer_size, config_size, save_request_size, save_status_size;
    uint32_t health_size, overlay_size, artwork_size, event_size;
    uint64_t capabilities;
} cd_session_contract;
typedef struct cd_session_health {
    cd_struct_header header;
    uint32_t running, paused, restart_required, active_fps;
    uint32_t queue_depth, queue_capacity;
    uint64_t acquired, encoded, replaced, generation, detector_copies, input_revision;
    cd_bytes details;
} cd_session_health;
typedef struct cd_session_save_request {
    cd_struct_header header;
    uint8_t id[16];
    int64_t start_us, end_us;
    cd_string16 output_path;
} cd_session_save_request;
enum cd_save_state { CD_SAVE_PENDING = 0, CD_SAVE_COMPLETE = 1, CD_SAVE_FAILED = 2, CD_SAVE_CANCELLED = 3 };
typedef struct cd_session_save_status {
    cd_struct_header header;
    uint32_t state, frozen;
    int64_t duration_us;
    uint64_t generation;
    cd_bytes details;
} cd_session_save_status;
typedef struct cd_overlay_update {
    cd_struct_header header;
    uint64_t revision;
    int64_t at_us;
    uint32_t burned, reserved;
    double camera_x, camera_y, camera_width, keyboard_x, keyboard_y, keyboard_width;
    cd_string16 camera_moniker, camera_name, keyboard_layout, settings_json;
} cd_overlay_update;
typedef struct cd_artwork_update {
    cd_struct_header header;
    uint64_t revision;
    int64_t at_us;
    uint32_t width, height, stride, premultiplied;
    const uint8_t* pixels;
    uint32_t length, reserved;
} cd_artwork_update;
typedef struct cd_frame_copy {
    cd_struct_header header;
    uint64_t revision;
    int64_t timestamp_us;
    uint32_t width, height, stride, reserved;
    cd_bytes pixels;
} cd_frame_copy;
typedef struct cd_normalized_rect { double x, y, width, height; } cd_normalized_rect;
typedef struct cd_detector_copy {
    cd_struct_header header;
    int64_t timestamp_us;
    uint32_t widths[4], heights[4];
    cd_bytes images[4];
} cd_detector_copy;
typedef struct cd_event_poll {
    cd_struct_header header;
    uint64_t after_sequence;
    uint32_t timeout_ms, mask;
    uint64_t sequence;
} cd_event_poll;
enum cd_event_mask { CD_EVENT_HEALTH = 1, CD_EVENT_INPUT = 2, CD_EVENT_DETECTOR = 4,
    CD_EVENT_SAVE = 8, CD_EVENT_SESSION = 16, CD_EVENT_SETTINGS = 32 };
#pragma pack(pop)
typedef struct cd_recorder cd_recorder;
CD_API int32_t CD_CALL cd_recorder_get_contract(cd_session_contract* contract);
CD_API int32_t CD_CALL cd_recorder_create(const cd_session_config* config, cd_recorder** recorder);
CD_API int32_t CD_CALL cd_recorder_start(cd_recorder* recorder);
CD_API int32_t CD_CALL cd_recorder_stop(cd_recorder* recorder);
CD_API void CD_CALL cd_recorder_destroy(cd_recorder* recorder);
CD_API int32_t CD_CALL cd_recorder_pause(cd_recorder* recorder, uint32_t paused);
CD_API int32_t CD_CALL cd_recorder_frame_rate(cd_recorder* recorder, uint32_t fps);
CD_API int32_t CD_CALL cd_recorder_health(cd_recorder* recorder, cd_session_health* health);
CD_API int32_t CD_CALL cd_recorder_save_begin(cd_recorder* recorder, const cd_session_save_request* request);
CD_API int32_t CD_CALL cd_recorder_save_status(cd_recorder* recorder, const uint8_t* id, cd_session_save_status* status);
CD_API int32_t CD_CALL cd_recorder_save_cancel(cd_recorder* recorder, const uint8_t* id);
CD_API int32_t CD_CALL cd_recorder_save_release(cd_recorder* recorder, const uint8_t* id);
CD_API int32_t CD_CALL cd_recorder_overlay(cd_recorder* recorder, const cd_overlay_update* update);
CD_API int32_t CD_CALL cd_recorder_artwork(cd_recorder* recorder, const cd_artwork_update* update);
CD_API int32_t CD_CALL cd_recorder_keys(cd_recorder* recorder, cd_bytes* json);
CD_API int32_t CD_CALL cd_recorder_detector_regions(cd_recorder* recorder, const cd_rect* regions,
    uint32_t region_count, const cd_rect* masks, uint32_t mask_count, uint32_t width, uint32_t height);
CD_API int32_t CD_CALL cd_recorder_detector_copy(cd_recorder* recorder, cd_frame_copy* frame);
CD_API int32_t CD_CALL cd_recorder_camera_copy(cd_recorder* recorder, cd_frame_copy* frame);
CD_API int32_t CD_CALL cd_recorder_detector_set(cd_recorder* recorder, const cd_normalized_rect* regions, uint32_t count, uint32_t counter_mask);
CD_API int32_t CD_CALL cd_recorder_detector_read(cd_recorder* recorder, cd_detector_copy* frame);
CD_API int32_t CD_CALL cd_recorder_verify(cd_string16 directory, cd_string16 ffmpeg,
    uint32_t fps, uint32_t variable, cd_bytes* report);
CD_API int32_t CD_CALL cd_recorder_events(cd_recorder* recorder, cd_event_poll* events);
