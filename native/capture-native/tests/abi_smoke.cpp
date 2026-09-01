#include "clypdat_capture_native.h"

#include <cassert>

int main() {
    cd_engine_config bad{};
    bad.header.struct_size = sizeof(bad);
    bad.header.abi_version = CD_ABI_VERSION + 1;
    cd_engine* engine = nullptr;
    assert(cd_engine_create(&bad, &engine) == CD_E_INVALID_ARGUMENT);

    cd_engine_config config{};
    config.header.struct_size = sizeof(config);
    config.header.abi_version = CD_ABI_VERSION;
    config.selected_fps = 90;
    config.width = 1920;
    config.height = 1080;
    config.history_seconds = 60;
    assert(cd_engine_create(&config, &engine) == CD_OK);
    assert(engine != nullptr);
    assert(cd_engine_start(engine) == CD_OK);
    assert(cd_engine_set_active_fps(engine, 60) == CD_OK);
    assert(cd_engine_set_paused(engine, 1) == CD_OK);

    cd_engine_health health{};
    health.header.struct_size = sizeof(health);
    health.header.abi_version = CD_ABI_VERSION;
    assert(cd_engine_get_health(engine, &health) == CD_OK);
    assert(health.engine_version == CD_ENGINE_VERSION);
    assert(health.selected_fps == 90);
    assert(health.active_fps == 60);
    assert(health.state == CD_ENGINE_PAUSED);
    assert(health.queue_capacity == 8);
    assert(health.surface_capacity == 12);

    assert(cd_engine_stop(engine) == CD_OK);
    cd_engine_destroy(engine);
    return 0;
}
