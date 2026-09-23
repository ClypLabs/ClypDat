#include "clypdat_capture_native.h"

#include <cstdlib>
#include <iostream>
#include <string_view>
#include <limits>

// assert() suppresses even the calls under test in Release builds.
#define CHECK(expression) do { if (!(expression)) { \
    std::cerr << __FILE__ << ':' << __LINE__ << ": " #expression " failed\n"; \
    std::exit(EXIT_FAILURE); } } while (false)

int main(int argc, char** argv) {
    const bool gpu = argc == 2 && std::string_view(argv[1]) == "--gpu";
    CHECK(cd_engine_abi_version() == CD_ABI_VERSION);
    cd_abi_info info{ { sizeof(info), CD_ABI_VERSION } };
    CHECK(cd_engine_get_abi_info(&info) == CD_OK);
    CHECK(info.pointer_size == 8);
    CHECK(info.config_size == sizeof(cd_engine_config));
    CHECK(info.health_size == sizeof(cd_engine_health));
    CHECK(info.save_request_size == sizeof(cd_save_request));
    CHECK(info.save_result_size == sizeof(cd_save_result));
    info.header.abi_version = 2;
    CHECK(cd_engine_get_abi_info(&info) == CD_E_UNSUPPORTED_ABI);

    cd_engine_config config{};
    config.header = { sizeof(config), CD_ABI_VERSION };
    config.selected_fps = 90;
    config.bitrate_mbps = 20;
    config.width = 1920;
    config.height = 1080;
    config.history_seconds = 60;
    cd_engine* engine = reinterpret_cast<cd_engine*>(1);
    CHECK(cd_engine_create(nullptr, &engine) == CD_E_INVALID_ARGUMENT);
    CHECK(engine == nullptr);
    CHECK(cd_engine_create(&config, nullptr) == CD_E_INVALID_ARGUMENT);
    config.header.abi_version = 2;
    CHECK(cd_engine_create(&config, &engine) == CD_E_UNSUPPORTED_ABI);
    CHECK(engine == nullptr);
    config.header.abi_version = CD_ABI_VERSION;
    config.header.struct_size = sizeof(cd_struct_header);
    CHECK(cd_engine_create(&config, &engine) == CD_E_INVALID_ARGUMENT);
    config.header.struct_size = sizeof(config);
    config.selected_fps = 0;
    CHECK(cd_engine_create(&config, &engine) == CD_E_INVALID_ARGUMENT);
    config.selected_fps = 90;
    CHECK(cd_engine_create(&config, &engine) == CD_OK);
    CHECK(engine != nullptr);
    config.selected_fps = 30; // Native engine must retain its own copy.
    CHECK(cd_engine_set_active_fps(engine, 60) == CD_OK);
    CHECK(cd_engine_set_active_fps(engine, 121) == CD_E_INVALID_ARGUMENT);
    CHECK(cd_engine_set_paused(engine, 1) == CD_E_INVALID_STATE);
    CHECK(cd_engine_set_paused(engine, 2) == CD_E_INVALID_ARGUMENT);

    cd_engine_health health{};
    health.header = { sizeof(health), CD_ABI_VERSION };
    CHECK(cd_engine_get_health(engine, &health) == CD_OK);
    CHECK(health.engine_version == CD_ENGINE_VERSION);
    CHECK(health.selected_fps == 90);
    CHECK(health.active_fps == 60);
    CHECK(health.state == CD_ENGINE_CREATED);
    CHECK(health.capture_route == CD_CAPTURE_ROUTE_NONE);
    health.header.abi_version = 2;
    CHECK(cd_engine_get_health(engine, &health) == CD_E_UNSUPPORTED_ABI);
    health.header.abi_version = CD_ABI_VERSION;

    uint16_t path[2]{ 123, 456 };
    cd_save_request request{ { sizeof(request), CD_ABI_VERSION },
        std::numeric_limits<int64_t>::min(), std::numeric_limits<int64_t>::max() };
    cd_save_result result{};
    result.header = { sizeof(result), CD_ABI_VERSION };
    CHECK(cd_engine_save_window(engine, &request, &result) == CD_E_BUFFER_TOO_SMALL);
    result.temporary_video_path = path;
    result.temporary_video_path_capacity = 1;
    CHECK(cd_engine_save_window(engine, &request, &result) == CD_E_UNAVAILABLE);
    CHECK(path[0] == 0 && path[1] == 456);
    CHECK(result.duration_qpc == 0 && result.packet_count == 0);
    request.header.abi_version = 2;
    CHECK(cd_engine_save_window(engine, &request, &result) == CD_E_UNSUPPORTED_ABI);

    for (int iteration = 0; iteration < 20; ++iteration) {
        if (gpu) {
            CHECK(cd_engine_start(engine) == CD_OK);
            CHECK(cd_engine_start(engine) == CD_OK);
            CHECK(cd_engine_set_paused(engine, 1) == CD_OK);
            CHECK(cd_engine_get_health(engine, &health) == CD_OK);
            CHECK(health.state == CD_ENGINE_PAUSED);
        }
        CHECK(cd_engine_stop(engine) == CD_OK);
        CHECK(cd_engine_stop(engine) == CD_OK);
        CHECK(cd_engine_get_health(engine, &health) == CD_OK);
        CHECK(health.state == CD_ENGINE_STOPPED);
    }
    cd_engine_destroy(engine);
    cd_engine_destroy(nullptr);
    std::cout << (gpu ? "Device lifecycle" : "ABI") << " checks passed\n";
}
