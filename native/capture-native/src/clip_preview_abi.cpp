#include "clypdat_clip_preview.h"
#include "clip_preview_decoder.h"
#include "video_encoder.h"
#include <algorithm>
#include <memory>
#include <stdexcept>
#include <string>

// No ABI-wide lock: take blocks by design, and stop must be able to wake it
// from another thread. ClipPreviewDecoder is internally synchronised.
struct cd_clip_preview {
    std::unique_ptr<clypdat::ClipPreviewDecoder> decoder;
};

int32_t CD_CALL cd_clip_preview_open(const cd_clip_preview_config* config, cd_clip_preview** result) {
    if (!result) return CD_E_INVALID_ARGUMENT;
    *result = nullptr;
    if (!config || config->size != sizeof(*config) || config->abi != CD_ABI_VERSION) return CD_E_UNSUPPORTED_ABI;
    if (!config->path || !config->path_length || config->path_length > 32767) return CD_E_INVALID_ARGUMENT;
    if (!clypdat::runtime_versions_match()) return CD_E_RUNTIME_MISMATCH;
    try {
        std::wstring path(reinterpret_cast<const wchar_t*>(config->path), config->path_length);
        if (path.find(L'\0') != std::wstring::npos) return CD_E_INVALID_ARGUMENT;
        clypdat::ClipPreviewConfig native;
        native.path = path;
        native.start_us = config->start_us;
        native.duration_us = config->duration_us;
        native.width = int(config->width);
        native.height = int(config->height);
        native.fps = int(config->fps);
        native.crop_x = config->crop_x;
        native.crop_y = config->crop_y;
        native.crop_width = config->crop_width;
        native.crop_height = config->crop_height;
        native.paced = config->paced != 0;
        auto preview = std::make_unique<cd_clip_preview>();
        preview->decoder = std::make_unique<clypdat::ClipPreviewDecoder>(std::move(native));
        *result = preview.release();
        return CD_OK;
    } catch (const std::invalid_argument&) { return CD_E_INVALID_ARGUMENT; }
    catch (...) { return CD_E_INTERNAL; }
}

int32_t CD_CALL cd_clip_preview_take(cd_clip_preview* preview, cd_clip_preview_frame* frame, uint8_t* rgba, uint32_t capacity) {
    if (!preview || !frame) return CD_E_INVALID_ARGUMENT;
    if (frame->size != sizeof(*frame) || frame->abi != CD_ABI_VERSION) return CD_E_UNSUPPORTED_ABI;
    try {
        auto& decoder = *preview->decoder;
        frame->frames_per_loop = decoder.frames_per_loop();
        if (capacity < decoder.frame_bytes()) return CD_E_BUFFER_TOO_SMALL;
        frame->sequence = decoder.take(frame->sequence, rgba, capacity, std::min<uint32_t>(frame->timeout_ms, 1000));
        frame->source_bytes_read = decoder.source_bytes_read();
        frame->finished = decoder.finished() ? 1u : 0u;
        return CD_OK;
    } catch (const std::invalid_argument&) { return CD_E_INVALID_ARGUMENT; }
    catch (...) { return CD_E_INTERNAL; }
}

int32_t CD_CALL cd_clip_preview_stop(cd_clip_preview* preview) {
    if (!preview) return CD_E_INVALID_ARGUMENT;
    preview->decoder->stop();
    return CD_OK;
}

int32_t CD_CALL cd_clip_preview_error(cd_clip_preview* preview, uint8_t* utf8, uint32_t capacity, uint32_t* required) {
    if (!preview || !required) return CD_E_INVALID_ARGUMENT;
    try {
        const auto text = preview->decoder->error();
        *required = uint32_t(text.size());
        if (text.empty()) return CD_OK;
        if (!utf8 || capacity < text.size()) return CD_E_BUFFER_TOO_SMALL;
        std::copy(text.begin(), text.end(), utf8);
        return CD_OK;
    } catch (...) { return CD_E_INTERNAL; }
}

int32_t CD_CALL cd_clip_preview_release(cd_clip_preview* preview) {
    if (!preview) return CD_OK;
    try { delete preview; return CD_OK; }
    catch (...) { return CD_E_INTERNAL; }
}
