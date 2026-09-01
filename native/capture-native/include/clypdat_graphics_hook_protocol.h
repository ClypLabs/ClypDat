#pragma once

#include <stdint.h>
#include <windows.h>

// Shared-memory ABI used by the x64 host, injector and hook. Names are
// nonce-scoped; the fixed per-PID locator contains only ControlMappingName.
enum : uint32_t {
    CLYPDAT_HOOK_MAGIC = 0x48444C43, // "CLDH"
    CLYPDAT_HOOK_ABI_VERSION = 1,
    CLYPDAT_HOOK_SLOT_COUNT = 3,
    CLYPDAT_HOOK_NAME_CAPACITY = 128,
};

enum clypdat_hook_state : uint32_t {
    CLYPDAT_HOOK_STARTING = 1,
    CLYPDAT_HOOK_WAITING_FOR_SWAPCHAIN = 2,
    CLYPDAT_HOOK_READY = 3,
    CLYPDAT_HOOK_FAILED = 4,
    CLYPDAT_HOOK_STOP_REQUESTED = 5,
    CLYPDAT_HOOK_STOPPED = 6,
};

enum clypdat_hook_failure : uint32_t {
    CLYPDAT_HOOK_FAILURE_NONE = 0,
    CLYPDAT_HOOK_FAILURE_PROTOCOL = 1,
    CLYPDAT_HOOK_FAILURE_UNSUPPORTED_FORMAT = 2,
    CLYPDAT_HOOK_FAILURE_NON_D3D11 = 3,
    CLYPDAT_HOOK_FAILURE_RESOURCE = 4,
    CLYPDAT_HOOK_FAILURE_DEVICE = 5,
    CLYPDAT_HOOK_FAILURE_TARGET_EXITED = 6,
    CLYPDAT_HOOK_FAILURE_INCOMPATIBLE_RESIDENT = 7,
};

#pragma pack(push, 8)
struct clypdat_hook_locator {
    uint32_t magic;
    uint32_t abi_size;
    uint32_t abi_version;
    uint32_t target_pid;
    wchar_t control_mapping_name[CLYPDAT_HOOK_NAME_CAPACITY];
};

struct clypdat_hook_control {
    uint32_t magic;
    uint32_t abi_size;
    uint32_t abi_version;
    volatile uint32_t state;
    volatile uint32_t failure;
    uint32_t host_pid;
    uint32_t target_pid;
    uint64_t target_hwnd;
    volatile uint32_t requested_fps;
    uint32_t renderer;
    uint32_t format;
    uint32_t width;
    uint32_t height;
    uint32_t adapter_luid_low;
    int32_t adapter_luid_high;
    volatile uint64_t generation;
    volatile uint64_t presents;
    volatile uint64_t transported_frames;
    volatile uint64_t transport_drops;
    volatile uint64_t slot_sequences[CLYPDAT_HOOK_SLOT_COUNT];
    volatile int64_t slot_qpc[CLYPDAT_HOOK_SLOT_COUNT];
    wchar_t slot_resource_names[CLYPDAT_HOOK_SLOT_COUNT][CLYPDAT_HOOK_NAME_CAPACITY];
};
#pragma pack(pop)

static_assert(sizeof(clypdat_hook_locator) == 272);
static_assert(alignof(clypdat_hook_control) == 8);
