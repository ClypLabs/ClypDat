#include "clypdat_clip_thumbnail.h"
#include "clip_thumbnail.h"
#include "video_encoder.h"
#include <Windows.h>
#include <algorithm>
#include <cstring>
#include <stdexcept>
#include <string>

namespace {
int32_t report(const std::string& message, uint8_t* error_utf8, uint32_t error_capacity, uint32_t* error_required, int32_t code) {
    if (error_required) *error_required = uint32_t(message.size());
    if (error_utf8 && error_capacity) {
        const auto copied = std::min<size_t>(message.size(), error_capacity - 1);
        std::memcpy(error_utf8, message.data(), copied);
        error_utf8[copied] = 0;
    }
    return code;
}
std::wstring text(const uint16_t* value, uint32_t length) {
    std::wstring result(reinterpret_cast<const wchar_t*>(value), length);
    if (result.find(L'\0') != std::wstring::npos) throw std::invalid_argument("Path contains a null character");
    return result;
}
// Background mode lowers CPU, I/O and memory priority together, which is
// what a game actually contends on. Always ended on the same thread.
struct BackgroundMode {
    bool active = false;
    explicit BackgroundMode(bool wanted) {
        if (wanted) active = SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN) != 0;
    }
    ~BackgroundMode() { if (active) SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END); }
};
}

int32_t CD_CALL cd_clip_thumbnail_write(const cd_clip_thumbnail_config* config,
    uint8_t* error_utf8, uint32_t error_capacity, uint32_t* error_required) {
    if (error_required) *error_required = 0;
    if (!config || config->size != sizeof(*config) || config->abi != CD_ABI_VERSION) return CD_E_UNSUPPORTED_ABI;
    if (!config->path || !config->path_length || config->path_length > 32767) return CD_E_INVALID_ARGUMENT;
    if (!config->output || !config->output_length || config->output_length > 32767) return CD_E_INVALID_ARGUMENT;
    if (!clypdat::runtime_versions_match()) return CD_E_RUNTIME_MISMATCH;
    BackgroundMode background(config->background != 0);
    try {
        clypdat::ClipThumbnailConfig native;
        native.path = text(config->path, config->path_length);
        native.output = text(config->output, config->output_length);
        native.at_us = config->at_us;
        native.width = int(std::clamp<uint32_t>(config->width, 16, 7680));
        native.quality = int(std::clamp<uint32_t>(config->quality, 2, 31));
        clypdat::write_clip_thumbnail(native);
        return CD_OK;
    } catch (const std::invalid_argument& error) { return report(error.what(), error_utf8, error_capacity, error_required, CD_E_INVALID_ARGUMENT); }
    catch (const std::exception& error) { return report(error.what(), error_utf8, error_capacity, error_required, CD_E_INTERNAL); }
    catch (...) { return report("Unknown thumbnail failure", error_utf8, error_capacity, error_required, CD_E_INTERNAL); }
}

int32_t CD_CALL cd_clip_filmstrip_write(cd_clip_filmstrip_config* config,
    uint8_t* error_utf8, uint32_t error_capacity, uint32_t* error_required) {
    if (error_required) *error_required = 0;
    if (!config || config->size != sizeof(*config) || config->abi != CD_ABI_VERSION) return CD_E_UNSUPPORTED_ABI;
    if (!config->path || !config->path_length || config->path_length > 32767) return CD_E_INVALID_ARGUMENT;
    if (!config->output || !config->output_length || config->output_length > 32767) return CD_E_INVALID_ARGUMENT;
    if (!clypdat::runtime_versions_match()) return CD_E_RUNTIME_MISMATCH;
    config->decoded = 0;
    BackgroundMode background(config->background != 0);
    try {
        clypdat::ClipFilmstripConfig native;
        native.path = text(config->path, config->path_length);
        native.output = text(config->output, config->output_length);
        native.duration_us = config->duration_us;
        native.frame_count = int(std::clamp<uint32_t>(config->frame_count, 1, 80));
        native.frame_height = int(std::clamp<uint32_t>(config->frame_height, 16, 1080));
        native.quality = int(std::clamp<uint32_t>(config->quality, 2, 31));
        config->decoded = uint32_t(clypdat::write_clip_filmstrip(native));
        return CD_OK;
    } catch (const std::invalid_argument& error) { return report(error.what(), error_utf8, error_capacity, error_required, CD_E_INVALID_ARGUMENT); }
    catch (const std::exception& error) { return report(error.what(), error_utf8, error_capacity, error_required, CD_E_INTERNAL); }
    catch (...) { return report("Unknown filmstrip failure", error_utf8, error_capacity, error_required, CD_E_INTERNAL); }
}
