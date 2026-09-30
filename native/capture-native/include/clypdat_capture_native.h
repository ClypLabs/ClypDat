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

// Shared by every ClypDat.Capture.Native ABI (clypdat_recorder.h and the
// camera preview, audio meter and recovery headers). The ABI stays C-only: no
// COM, FFmpeg, callbacks, or ownership-bearing pointers cross it. Every input
// and output begins with cd_struct_header, so a newer managed host is rejected
// safely by an older library.
enum {
    CD_ABI_VERSION = 3,
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
    CD_E_RUNTIME_MISMATCH = -8,
};

// cd_session_contract.capabilities (cd_recorder_get_contract). Advertise only
// completed end-to-end contracts, not isolated building blocks.
enum cd_capability {
    CD_CAP_CAPTURE = 1,
    CD_CAP_REPLAY_SAVE = 2,
    CD_CAP_AUDIO = 4,
    CD_CAP_FULL_SESSION = 8,
    CD_CAP_OVERLAYS = 16,
    CD_CAP_ASYNC_CONTROL = 32,
    CD_CAP_GPU_FAILURE = 64,
};

#pragma pack(push, 8)

typedef struct cd_struct_header {
    uint32_t struct_size;
    uint32_t abi_version;
} cd_struct_header;

#pragma pack(pop)
