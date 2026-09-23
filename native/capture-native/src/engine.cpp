#include "clypdat_capture_native.h"

#include <d3d11.h>
#include <dxgi.h>
#include <algorithm>
#include <memory>
#include <mutex>

namespace {
constexpr uint32_t kMinimumFps = 30;
constexpr uint32_t kMaximumFps = 120;
constexpr uint32_t kEncodeQueueCapacity = 8;
constexpr uint32_t kSurfacePoolCapacity = 12;

static_assert(sizeof(cd_struct_header) == 8);
static_assert(sizeof(cd_engine_config) == 56);
static_assert(sizeof(cd_engine_health) == 112);
static_assert(sizeof(cd_save_request) == 24);
static_assert(sizeof(cd_save_result) == 56);
static_assert(sizeof(cd_abi_info) == 32);
static_assert(sizeof(void*) == 8, "The recorder ABI requires x64.");

int32_t validate_header(const cd_struct_header* header, uint32_t required_size) {
    if (header == nullptr || header->struct_size < sizeof(cd_struct_header)) return CD_E_INVALID_ARGUMENT;
    if (header->abi_version != CD_ABI_VERSION) return CD_E_UNSUPPORTED_ABI;
    return header->struct_size >= required_size ? CD_OK : CD_E_INVALID_ARGUMENT;
}

void release_device(ID3D11Device*& device, ID3D11DeviceContext*& context) {
    if (context != nullptr) { context->Release(); context = nullptr; }
    if (device != nullptr) { device->Release(); device = nullptr; }
}
}

struct cd_engine {
    std::mutex mutex;
    cd_engine_config config{};
    cd_engine_state state = CD_ENGINE_CREATED;
    cd_capture_route route = CD_CAPTURE_ROUTE_NONE;
    cd_fatal_error fatal = CD_FATAL_NONE;
    uint32_t active_fps = 0;
    uint32_t adapter_luid_low = 0;
    int32_t adapter_luid_high = 0;
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
};

uint32_t CD_CALL cd_engine_abi_version(void) { return CD_ABI_VERSION; }

int32_t CD_CALL cd_engine_get_abi_info(cd_abi_info* info) {
    const auto result = validate_header(info == nullptr ? nullptr : &info->header, sizeof(cd_abi_info));
    if (result != CD_OK) return result;
    *info = { { sizeof(cd_abi_info), CD_ABI_VERSION }, CD_ENGINE_VERSION, sizeof(void*),
        sizeof(cd_engine_config), sizeof(cd_engine_health), sizeof(cd_save_request), sizeof(cd_save_result) };
    return CD_OK;
}

int32_t CD_CALL cd_engine_create(const cd_engine_config* config, cd_engine** engine) try {
    if (engine == nullptr) return CD_E_INVALID_ARGUMENT;
    *engine = nullptr;
    const auto result = validate_header(config == nullptr ? nullptr : &config->header, sizeof(cd_engine_config));
    if (result != CD_OK) return result;
    if (config->selected_fps < kMinimumFps || config->selected_fps > kMaximumFps || config->width == 0 || config->height == 0) return CD_E_INVALID_ARGUMENT;
    auto created = std::make_unique<cd_engine>();
    created->config = *config;
    created->active_fps = config->selected_fps;
    *engine = created.release();
    return CD_OK;
} catch (...) { return CD_E_INTERNAL; }

int32_t CD_CALL cd_engine_start(cd_engine* engine) try {
    if (engine == nullptr) return CD_E_INVALID_ARGUMENT;
    std::scoped_lock lock(engine->mutex);
    if (engine->state == CD_ENGINE_RUNNING || engine->state == CD_ENGINE_PAUSED) return CD_OK;
    if (engine->state == CD_ENGINE_FAILED) return CD_E_INVALID_STATE;

    constexpr D3D_FEATURE_LEVEL requested_levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    D3D_FEATURE_LEVEL selected_level{};
    const auto hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        requested_levels, static_cast<UINT>(std::size(requested_levels)), D3D11_SDK_VERSION,
        &engine->device, &selected_level, &engine->context);
    if (FAILED(hr)) {
        engine->state = CD_ENGINE_FAILED;
        engine->fatal = CD_FATAL_DEVICE;
        return CD_E_DEVICE_FAILURE;
    }

    IDXGIDevice* dxgi_device = nullptr;
    IDXGIAdapter* adapter = nullptr;
    DXGI_ADAPTER_DESC desc{};
    if (SUCCEEDED(engine->device->QueryInterface(IID_PPV_ARGS(&dxgi_device))) &&
        SUCCEEDED(dxgi_device->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&desc))) {
        engine->adapter_luid_low = desc.AdapterLuid.LowPart;
        engine->adapter_luid_high = desc.AdapterLuid.HighPart;
    }
    if (adapter != nullptr) adapter->Release();
    if (dxgi_device != nullptr) dxgi_device->Release();
    // Device initialization alone does not establish a capture route.
    engine->route = CD_CAPTURE_ROUTE_NONE;
    // This is capture engine's own device only. Do not change process-wide
    // scheduling; that would also reprioritize Avalonia and DWM-facing work.
    if (IDXGIDevice* priority_device = nullptr; SUCCEEDED(engine->device->QueryInterface(IID_PPV_ARGS(&priority_device)))) {
        const auto priority_hr = priority_device->SetGPUThreadPriority(7);
        priority_device->Release();
        (void)priority_hr; // Driver refusal is non-fatal.
    }
    engine->state = CD_ENGINE_RUNNING;
    return CD_OK;
} catch (...) { return CD_E_INTERNAL; }

int32_t CD_CALL cd_engine_stop(cd_engine* engine) try {
    if (engine == nullptr) return CD_E_INVALID_ARGUMENT;
    std::scoped_lock lock(engine->mutex);
    release_device(engine->device, engine->context);
    engine->route = CD_CAPTURE_ROUTE_NONE;
    if (engine->state != CD_ENGINE_FAILED) engine->state = CD_ENGINE_STOPPED;
    return CD_OK;
} catch (...) { return CD_E_INTERNAL; }

void CD_CALL cd_engine_destroy(cd_engine* engine) {
    if (engine == nullptr) return;
    cd_engine_stop(engine);
    delete engine;
}

int32_t CD_CALL cd_engine_set_paused(cd_engine* engine, uint32_t paused) try {
    if (engine == nullptr || paused > 1) return CD_E_INVALID_ARGUMENT;
    std::scoped_lock lock(engine->mutex);
    if (engine->state != CD_ENGINE_RUNNING && engine->state != CD_ENGINE_PAUSED) return CD_E_INVALID_STATE;
    engine->state = paused != 0 ? CD_ENGINE_PAUSED : CD_ENGINE_RUNNING;
    return CD_OK;
} catch (...) { return CD_E_INTERNAL; }

int32_t CD_CALL cd_engine_set_active_fps(cd_engine* engine, uint32_t active_fps) try {
    if (engine == nullptr || active_fps < kMinimumFps || active_fps > engine->config.selected_fps) return CD_E_INVALID_ARGUMENT;
    std::scoped_lock lock(engine->mutex);
    engine->active_fps = active_fps;
    return CD_OK;
} catch (...) { return CD_E_INTERNAL; }

int32_t CD_CALL cd_engine_get_health(const cd_engine* engine, cd_engine_health* health) try {
    if (engine == nullptr) return CD_E_INVALID_ARGUMENT;
    const auto result = validate_header(health == nullptr ? nullptr : &health->header, sizeof(cd_engine_health));
    if (result != CD_OK) return result;
    auto* mutable_engine = const_cast<cd_engine*>(engine);
    std::scoped_lock lock(mutable_engine->mutex);
    health->engine_version = CD_ENGINE_VERSION;
    health->build_version = CD_ENGINE_VERSION;
    health->state = mutable_engine->state;
    health->selected_fps = mutable_engine->config.selected_fps;
    health->active_fps = mutable_engine->active_fps;
    health->capture_route = mutable_engine->route;
    health->fatal_error = mutable_engine->fatal;
    health->queue_depth = 0;
    health->queue_capacity = kEncodeQueueCapacity;
    health->surfaces_in_use = 0;
    health->surface_capacity = kSurfacePoolCapacity;
    health->adapter_luid_low = mutable_engine->adapter_luid_low;
    health->adapter_luid_high = mutable_engine->adapter_luid_high;
    health->encoder_slot_wait_p95_ms = 0;
    health->submission_p95_ms = 0;
    health->queue_age_ms = 0;
    health->input_fps = 0;
    health->output_fps = 0;
    health->fresh_fps = 0;
    return CD_OK;
} catch (...) { return CD_E_INTERNAL; }

int32_t CD_CALL cd_engine_save_window(cd_engine* engine, const cd_save_request* request, cd_save_result* result) {
    if (engine == nullptr) return CD_E_INVALID_ARGUMENT;
    auto validation = validate_header(request == nullptr ? nullptr : &request->header, sizeof(cd_save_request));
    if (validation != CD_OK) return validation;
    validation = validate_header(result == nullptr ? nullptr : &result->header, sizeof(cd_save_result));
    if (validation != CD_OK) return validation;
    if (request->end_qpc <= request->start_qpc) return CD_E_INVALID_ARGUMENT;
    if (result->temporary_video_path == nullptr || result->temporary_video_path_capacity == 0) return CD_E_BUFFER_TOO_SMALL;
    result->temporary_video_path[0] = L'\0';
    result->actual_start_qpc = 0;
    result->actual_end_qpc = 0;
    result->duration_qpc = 0;
    result->packet_count = 0;
    return CD_E_UNAVAILABLE;
}
