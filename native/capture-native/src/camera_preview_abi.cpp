#include "clypdat_camera_preview.h"
#include "recording_overlays.h"
#include "video_encoder.h"
#include <Windows.h>
#include <algorithm>
#include <mutex>
#include <stdexcept>

struct cd_camera_preview {
    std::mutex mutex;
    std::filesystem::path ffmpeg, root;
    int64_t qpc_anchor=0, qpc_frequency=0;
    std::shared_ptr<clypdat::InputHistory> input=std::make_shared<clypdat::InputHistory>();
    std::shared_ptr<clypdat::OverlayHistory> history=std::make_shared<clypdat::OverlayHistory>(input);
    std::unique_ptr<clypdat::RecordingCamera> camera;
    std::string error;
};
namespace {
std::wstring copied(const uint16_t* value,uint32_t length) {
    if(!value || !length || length>32767) throw std::invalid_argument("Invalid camera preview UTF-16 input");
    std::wstring text(reinterpret_cast<const wchar_t*>(value),length);
    if(text.find(L'\0')!=std::wstring::npos) throw std::invalid_argument("Embedded null in camera preview input");
    return text;
}
template<class Operation> int32_t guarded(cd_camera_preview* preview,Operation operation) noexcept {
    if(!preview) return CD_E_INVALID_ARGUMENT;
    std::lock_guard lock(preview->mutex);
    try { return operation(); }
    catch(const std::invalid_argument& error) { preview->error=error.what(); return CD_E_INVALID_ARGUMENT; }
    catch(const std::exception& error) { preview->error=error.what(); return CD_E_DEVICE_FAILURE; }
    catch(...) { preview->error="Native camera preview failed"; return CD_E_INTERNAL; }
}
}
int32_t CD_CALL cd_camera_preview_create(const cd_camera_preview_config* config,cd_camera_preview** result) {
    if(!result) return CD_E_INVALID_ARGUMENT; *result=nullptr;
    if(!config || config->size!=sizeof(*config) || config->abi!=CD_ABI_VERSION) return CD_E_UNSUPPORTED_ABI;
    if(config->qpc_frequency<=0) return CD_E_INVALID_ARGUMENT;
    if(!clypdat::runtime_versions_match()) return CD_E_RUNTIME_MISMATCH;
    try {
        auto preview=std::make_unique<cd_camera_preview>();
        preview->ffmpeg=copied(config->ffmpeg_path,config->ffmpeg_length);
        preview->root=copied(config->work_directory,config->work_directory_length);
        if(!preview->ffmpeg.is_absolute() || !preview->root.is_absolute()) return CD_E_INVALID_ARGUMENT;
        preview->qpc_anchor=config->qpc_anchor; preview->qpc_frequency=config->qpc_frequency;
        const auto anchor=preview->qpc_anchor,frequency=preview->qpc_frequency;
        preview->camera=std::make_unique<clypdat::RecordingCamera>(preview->history,[anchor,frequency] {
            LARGE_INTEGER tick{}; QueryPerformanceCounter(&tick);
            return static_cast<int64_t>((tick.QuadPart-anchor)*(1000000.0L/frequency));
        });
        *result=preview.release(); return CD_OK;
    } catch(const std::invalid_argument&) { return CD_E_INVALID_ARGUMENT; }
    catch(...) { return CD_E_INTERNAL; }
}
int32_t CD_CALL cd_camera_preview_start(cd_camera_preview* preview,const uint16_t* device,uint32_t length) {
    return guarded(preview,[&]() -> int32_t {
        preview->error.clear();
        if(!preview->camera->start(preview->ffmpeg,preview->root,copied(device,length),false,true)) {
            preview->error="Camera did not stop within the deadline. Restart ClypDat."; return CD_E_INVALID_STATE;
        }
        return CD_OK;
    });
}
int32_t CD_CALL cd_camera_preview_stop(cd_camera_preview* preview) {
    return guarded(preview,[&]() -> int32_t {
        if(preview->camera->stop()) return CD_OK;
        preview->error="Camera did not stop within the deadline. Restart ClypDat."; return CD_E_INVALID_STATE;
    });
}
int32_t CD_CALL cd_camera_preview_copy(cd_camera_preview* preview,cd_camera_preview_frame* output,uint8_t* pixels,uint32_t capacity) {
    if(!output || output->size!=sizeof(*output) || output->abi!=CD_ABI_VERSION) return CD_E_UNSUPPORTED_ABI;
    return guarded(preview,[&]() -> int32_t {
        const auto previous_sequence=output->sequence;
        const auto frame=preview->history->preview();
        output->running=preview->camera->running()?1u:0u;
        output->width=output->height=output->stride=output->required_bytes=0; output->sequence=0; output->timestamp_qpc=0;
        if(!frame) return CD_OK;
        output->width=frame->width; output->height=frame->height; output->stride=frame->stride;
        output->required_bytes=static_cast<uint32_t>(frame->bgra.size()); output->sequence=frame->revision;
        output->timestamp_qpc=preview->qpc_anchor+static_cast<int64_t>(frame->at_us*(preview->qpc_frequency/1000000.0L));
        if(previous_sequence==frame->revision) { output->required_bytes=0; return CD_OK; }
        if(capacity<frame->bgra.size() || !pixels) return CD_E_BUFFER_TOO_SMALL;
        std::copy(frame->bgra.begin(),frame->bgra.end(),pixels); return CD_OK;
    });
}
int32_t CD_CALL cd_camera_preview_error(cd_camera_preview* preview,uint8_t* utf8,uint32_t capacity,uint32_t* required) {
    if(!required) return CD_E_INVALID_ARGUMENT;
    return guarded(preview,[&]() -> int32_t {
        auto text=preview->error.empty()?preview->camera->error():preview->error;
        *required=static_cast<uint32_t>(text.size());
        if(text.empty()) return CD_OK;
        if(!utf8 || capacity<text.size()) return CD_E_BUFFER_TOO_SMALL;
        std::copy(text.begin(),text.end(),utf8); return CD_OK;
    });
}
int32_t CD_CALL cd_camera_preview_release(cd_camera_preview* preview) {
    if(!preview) return CD_OK;
    try {
        const bool stopped=preview->camera->stop(); delete preview;
        return stopped?CD_OK:CD_E_INVALID_STATE;
    } catch(...) { return CD_E_INTERNAL; }
}
