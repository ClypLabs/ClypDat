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

bool valid_header(const cd_struct_header* header, uint32_t required_size) {
    return header != nullptr && header->abi_version == CD_ABI_VERSION && header->struct_size >= required_size;
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

int32_t CD_CALL cd_engine_create(const cd_engine_config* config, cd_engine** engine) {
    if (engine == nullptr || !valid_header(config == nullptr ? nullptr : &config->header, sizeof(cd_engine_config))) return CD_E_INVALID_ARGUMENT;
    if (config->selected_fps < kMinimumFps || config->selected_fps > kMaximumFps || config->width == 0 || config->height == 0) return CD_E_INVALID_ARGUMENT;
    auto created = std::make_unique<cd_engine>();
    created->config = *config;
    created->active_fps = config->selected_fps;
    *engine = created.release();
    return CD_OK;
}

int32_t CD_CALL cd_engine_start(cd_engine* engine) {
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
    engine->route = CD_CAPTURE_ROUTE_DXGI;
    // This is capture engine's own device only. Do not change process-wide
    // scheduling; that would also reprioritize Avalonia and DWM-facing work.
    if (IDXGIDevice* priority_device = nullptr; SUCCEEDED(engine->device->QueryInterface(IID_PPV_ARGS(&priority_device)))) {
        const auto priority_hr = priority_device->SetGPUThreadPriority(7);
        priority_device->Release();
        (void)priority_hr; // Driver refusal is non-fatal.
    }
    engine->state = CD_ENGINE_RUNNING;
    return CD_OK;
}

int32_t CD_CALL cd_engine_stop(cd_engine* engine) {
    if (engine == nullptr) return CD_E_INVALID_ARGUMENT;
    std::scoped_lock lock(engine->mutex);
    release_device(engine->device, engine->context);
    if (engine->state != CD_ENGINE_FAILED) engine->state = CD_ENGINE_STOPPED;
    return CD_OK;
}

void CD_CALL cd_engine_destroy(cd_engine* engine) {
    if (engine == nullptr) return;
    cd_engine_stop(engine);
    delete engine;
}

int32_t CD_CALL cd_engine_set_paused(cd_engine* engine, uint32_t paused) {
    if (engine == nullptr) return CD_E_INVALID_ARGUMENT;
    std::scoped_lock lock(engine->mutex);
    if (engine->state != CD_ENGINE_RUNNING && engine->state != CD_ENGINE_PAUSED) return CD_E_INVALID_STATE;
    engine->state = paused != 0 ? CD_ENGINE_PAUSED : CD_ENGINE_RUNNING;
    return CD_OK;
}

int32_t CD_CALL cd_engine_set_active_fps(cd_engine* engine, uint32_t active_fps) {
    if (engine == nullptr || active_fps < kMinimumFps || active_fps > engine->config.selected_fps) return CD_E_INVALID_ARGUMENT;
    std::scoped_lock lock(engine->mutex);
    engine->active_fps = active_fps;
    return CD_OK;
}

int32_t CD_CALL cd_engine_get_health(const cd_engine* engine, cd_engine_health* health) {
    if (engine == nullptr || !valid_header(health == nullptr ? nullptr : &health->header, sizeof(cd_engine_health))) return CD_E_INVALID_ARGUMENT;
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
}

int32_t CD_CALL cd_engine_save_window(cd_engine* engine, const cd_save_request* request, cd_save_result* result) {
    if (engine == nullptr || !valid_header(request == nullptr ? nullptr : &request->header, sizeof(cd_save_request)) ||
        !valid_header(result == nullptr ? nullptr : &result->header, sizeof(cd_save_result))) return CD_E_INVALID_ARGUMENT;
    if (request->end_qpc <= request->start_qpc) return CD_E_INVALID_ARGUMENT;
    if (result->temporary_video_path == nullptr || result->temporary_video_path_capacity == 0) return CD_E_BUFFER_TOO_SMALL;
    result->temporary_video_path[0] = L'\0';
    result->actual_start_qpc = request->start_qpc;
    result->actual_end_qpc = request->end_qpc;
    result->duration_qpc = request->end_qpc - request->start_qpc;
    result->packet_count = 0;
    return CD_E_UNAVAILABLE;
}
