#pragma once

#include <stdint.h>

#if defined(_WIN32)
#define CD_API extern "C" __declspec(dllexport)
#define CD_CALL __stdcall
#else
#define CD_API extern "C"
#define CD_CALL
#endif

// ABI remains C-only: no COM, FFmpeg, callbacks, or ownership-bearing pointers
// cross this boundary. Every input and output begins with this header so a newer
// managed host can be rejected safely by an older engine.
enum : uint32_t {
    CD_ABI_VERSION = 1,
    CD_ENGINE_VERSION = 1,
};

enum cd_result : int32_t {
    CD_OK = 0,
    CD_E_INVALID_ARGUMENT = -1,
    CD_E_UNSUPPORTED_ABI = -2,
    CD_E_INVALID_STATE = -3,
    CD_E_DEVICE_FAILURE = -4,
    CD_E_UNAVAILABLE = -5,
    CD_E_BUFFER_TOO_SMALL = -6,
};

enum cd_engine_state : uint32_t {
    CD_ENGINE_CREATED = 0,
    CD_ENGINE_RUNNING = 1,
    CD_ENGINE_PAUSED = 2,
    CD_ENGINE_STOPPED = 3,
    CD_ENGINE_FAILED = 4,
};

enum cd_capture_route : uint32_t {
    CD_CAPTURE_ROUTE_NONE = 0,
    CD_CAPTURE_ROUTE_DXGI = 1,
    CD_CAPTURE_ROUTE_WGC = 2,
};

enum cd_fatal_error : uint32_t {
    CD_FATAL_NONE = 0,
    CD_FATAL_DEVICE = 1,
    CD_FATAL_ENCODER = 2,
    CD_FATAL_CAPTURE = 3,
    CD_FATAL_ABI = 4,
};

struct cd_struct_header {
    uint32_t struct_size;
    uint32_t abi_version;
};

struct cd_engine_config {
    cd_struct_header header;
    uint64_t target_window;
    uint32_t selected_fps;
    uint32_t width;
    uint32_t height;
    uint32_t codec;
    uint32_t encoder_mode;
    uint32_t history_seconds;
    uint32_t flags;
};

struct cd_engine_health {
    cd_struct_header header;
    uint32_t engine_version;
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
};

struct cd_save_window {
    cd_struct_header header;
    int64_t start_unix_milliseconds;
    int64_t end_unix_milliseconds;
};

struct cd_engine;

CD_API int32_t CD_CALL cd_engine_create(const cd_engine_config* config, cd_engine** engine);
CD_API int32_t CD_CALL cd_engine_start(cd_engine* engine);
CD_API int32_t CD_CALL cd_engine_stop(cd_engine* engine);
CD_API void CD_CALL cd_engine_destroy(cd_engine* engine);
CD_API int32_t CD_CALL cd_engine_set_paused(cd_engine* engine, uint32_t paused);
CD_API int32_t CD_CALL cd_engine_set_active_fps(cd_engine* engine, uint32_t active_fps);
CD_API int32_t CD_CALL cd_engine_get_health(const cd_engine* engine, cd_engine_health* health);
CD_API int32_t CD_CALL cd_engine_save_window(cd_engine* engine, const cd_save_window* window, wchar_t* output_path, uint32_t output_path_capacity);
