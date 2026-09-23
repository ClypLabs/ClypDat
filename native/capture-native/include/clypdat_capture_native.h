#pragma once

#include <stdint.h>

#if defined(__cplusplus)
#define CD_EXTERN extern "C"
#else
#define CD_EXTERN extern
#endif

#if defined(_WIN32)
#if defined(CD_BUILDING_DLL)
#define CD_API CD_EXTERN __declspec(dllexport)
#else
#define CD_API CD_EXTERN __declspec(dllimport)
#endif
#define CD_CALL __stdcall
#else
#define CD_API CD_EXTERN
#define CD_CALL
#endif

// ABI remains C-only: no COM, FFmpeg, callbacks, or ownership-bearing pointers
// cross this boundary. Every input and output begins with this header so a newer
// managed host can be rejected safely by an older engine.
enum {
    CD_ABI_VERSION = 3,
    CD_ENGINE_VERSION = 3,
};

enum cd_result {
    CD_OK = 0,
    CD_E_INVALID_ARGUMENT = -1,
    CD_E_UNSUPPORTED_ABI = -2,
    CD_E_INVALID_STATE = -3,
    CD_E_DEVICE_FAILURE = -4,
    CD_E_UNAVAILABLE = -5,
    CD_E_BUFFER_TOO_SMALL = -6,
    CD_E_INTERNAL = -7,
};

enum cd_engine_state {
    CD_ENGINE_CREATED = 0,
    CD_ENGINE_RUNNING = 1,
    CD_ENGINE_PAUSED = 2,
    CD_ENGINE_STOPPED = 3,
    CD_ENGINE_FAILED = 4,
};

enum cd_capture_route {
    CD_CAPTURE_ROUTE_NONE = 0,
    CD_CAPTURE_ROUTE_DXGI = 1,
    CD_CAPTURE_ROUTE_WGC = 2,
};

enum cd_fatal_error {
    CD_FATAL_NONE = 0,
    CD_FATAL_DEVICE = 1,
    CD_FATAL_ENCODER = 2,
    CD_FATAL_CAPTURE = 3,
    CD_FATAL_ABI = 4,
};

#pragma pack(push, 8)

typedef struct cd_struct_header {
    uint32_t struct_size;
    uint32_t abi_version;
} cd_struct_header;

// A layout-independent query lets callers reject incompatible DLLs before
// passing a versioned structure. These sizes are the x64 ABI, not host guesses.
typedef struct cd_abi_info {
    cd_struct_header header;
    uint32_t engine_version;
    uint32_t pointer_size;
    uint32_t config_size;
    uint32_t health_size;
    uint32_t save_request_size;
    uint32_t save_result_size;
} cd_abi_info;

typedef struct cd_engine_config {
    cd_struct_header header;
    uint64_t target_window;
    uint64_t target_monitor;
    uint32_t bitrate_mbps;
    uint32_t selected_fps;
    uint32_t width;
    uint32_t height;
    uint32_t codec;
    uint32_t encoder_mode;
    uint32_t history_seconds;
    uint32_t flags;
} cd_engine_config;

typedef struct cd_engine_health {
    cd_struct_header header;
    uint32_t engine_version;
    uint32_t build_version;
    uint32_t state;
    uint32_t selected_fps;
    uint32_t active_fps;
    uint32_t capture_route;
    uint32_t fatal_error;
    uint32_t queue_depth;
    uint32_t queue_capacity;
    uint32_t surfaces_in_use;
    uint32_t surface_capacity;
    uint32_t adapter_luid_low;
    int32_t adapter_luid_high;
    double encoder_slot_wait_p95_ms;
    double submission_p95_ms;
    double queue_age_ms;
    double input_fps;
    double output_fps;
    double fresh_fps;
} cd_engine_health;

typedef struct cd_save_request {
    cd_struct_header header;
    int64_t start_qpc;
    int64_t end_qpc;
} cd_save_request;

// temporary_video_path is a caller-owned writable buffer. No native object or
// allocation crosses the ABI boundary.
typedef struct cd_save_result {
    cd_struct_header header;
    int64_t actual_start_qpc;
    int64_t actual_end_qpc;
    int64_t duration_qpc;
    uint64_t packet_count;
    uint16_t* temporary_video_path;
    uint32_t temporary_video_path_capacity;
} cd_save_result;

#pragma pack(pop)

typedef struct cd_engine cd_engine;

CD_API uint32_t CD_CALL cd_engine_abi_version(void);
CD_API int32_t CD_CALL cd_engine_get_abi_info(cd_abi_info* info);

CD_API int32_t CD_CALL cd_engine_create(const cd_engine_config* config, cd_engine** engine);
CD_API int32_t CD_CALL cd_engine_start(cd_engine* engine);
CD_API int32_t CD_CALL cd_engine_stop(cd_engine* engine);
CD_API void CD_CALL cd_engine_destroy(cd_engine* engine);
CD_API int32_t CD_CALL cd_engine_set_paused(cd_engine* engine, uint32_t paused);
CD_API int32_t CD_CALL cd_engine_set_active_fps(cd_engine* engine, uint32_t active_fps);
CD_API int32_t CD_CALL cd_engine_get_health(const cd_engine* engine, cd_engine_health* health);
CD_API int32_t CD_CALL cd_engine_save_window(cd_engine* engine, const cd_save_request* request, cd_save_result* result);
