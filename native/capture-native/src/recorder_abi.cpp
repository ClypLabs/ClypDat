#include "clypdat_recorder.h"
#include "recorder_session.h"
#include "recording_verification.h"
#include <Windows.h>
#include <algorithm>
#include <cstring>
#include <sstream>
#include <iomanip>
#include <stdexcept>

using namespace clypdat;
namespace {
template<class T> void validate(const T* value) {
    if (!value || value->header.struct_size != sizeof(T)) throw std::invalid_argument("Native recorder structure size mismatch");
    if (value->header.abi_version != CD_ABI_VERSION) throw std::invalid_argument("Native recorder ABI version mismatch");
}
std::wstring copy(cd_string16 value) {
    if (value.length > 1024 * 1024 || (value.length && !value.data)) throw std::invalid_argument("Invalid UTF-16 input");
    std::wstring result = value.length ? std::wstring(reinterpret_cast<const wchar_t*>(value.data), value.length) : std::wstring{};
    for (size_t i = 0; i < result.size(); ++i) {
        const auto ch = uint16_t(result[i]);
        if (!ch) throw std::invalid_argument("Embedded NUL in UTF-16 input");
        if (ch >= 0xD800 && ch <= 0xDBFF) {
            if (++i >= result.size() || uint16_t(result[i]) < 0xDC00 || uint16_t(result[i]) > 0xDFFF)
                throw std::invalid_argument("Unpaired UTF-16 surrogate");
        } else if (ch >= 0xDC00 && ch <= 0xDFFF) throw std::invalid_argument("Unpaired UTF-16 surrogate");
    }
    return result;
}
std::string utf8(const std::wstring& text) {
    if (text.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) throw std::invalid_argument("Invalid UTF-16 input");
    std::string result(size, '\0');
    WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), result.data(), size, nullptr, nullptr);
    return result;
}
std::string text(cd_string16 value) { return utf8(copy(value)); }
std::vector<std::wstring> strings(cd_strings16 input) {
    if (input.count > 4096 || (input.count && !input.data)) throw std::invalid_argument("Invalid string array");
    std::vector<std::wstring> values; values.reserve(input.count);
    for (uint32_t i = 0; i < input.count; ++i) values.push_back(copy(input.data[i]));
    return values;
}
std::string quoted(const std::string& value) {
    std::ostringstream out; out << '"';
    for (auto byte : value) {
        auto c = static_cast<unsigned char>(byte);
        if (c == '"' || c == '\\') out << '\\' << byte;
        else if (c < 32) out << "\\u" << std::hex << std::setw(4) << std::setfill('0') << int(c) << std::dec;
        else out << byte;
    }
    out << '"'; return out.str();
}
int32_t output(cd_bytes& buffer, const void* data, size_t size) {
    if (size > UINT32_MAX) return CD_E_INTERNAL;
    buffer.required = static_cast<uint32_t>(size);
    if (size && (!buffer.data || buffer.capacity < size)) return CD_E_BUFFER_TOO_SMALL;
    if (size) std::memcpy(buffer.data, data, size);
    return CD_OK;
}
int32_t output(cd_bytes& buffer, const std::string& data) { return output(buffer, data.data(), data.size()); }
std::string save_id(const uint8_t* id) {
    if (!id) throw std::invalid_argument("Save identifier is required");
    const char* hex = "0123456789abcdef"; std::string result;
    for (int i = 0; i < 16; ++i) { result += hex[id[i] >> 4]; result += hex[id[i] & 15]; }
    return result;
}
OverlaySettingsNative overlay(const cd_overlay_update& value) {
    OverlaySettingsNative result;
    result.revision = value.revision; result.at_us = value.at_us; result.burned = value.burned != 0;
    result.camera_moniker = copy(value.camera_moniker); result.camera_name = copy(value.camera_name);
    result.keyboard_layout = text(value.keyboard_layout); result.settings_json = text(value.settings_json);
    result.camera_transform = {value.camera_x, value.camera_y, value.camera_width};
    result.keyboard_transform = {value.keyboard_x, value.keyboard_y, value.keyboard_width};
    return result;
}
std::string mappings(const std::vector<SourceMapping>& values) {
    std::ostringstream out; out << '['; bool first = true;
    for (const auto& value : values) {
        if (!first) out << ','; first = false;
        out << "{\"sourceStartUs\":" << value.source_start_us << ",\"durationUs\":" << value.duration_us
            << ",\"outputStartUs\":" << value.output_start_us << '}';
    }
    return out.str() + ']';
}
}
struct cd_recorder {
    std::unique_ptr<RecorderSession> session;
    std::mutex mutex;
    std::string last_error;
    // Retain copied publication/configuration inputs through every accepted save.
    std::vector<std::wstring> copied_strings;
    uint8_t boot_id[16]{};
    int64_t utc_anchor_ticks = 0;
};
namespace {
template<class F> int32_t invoke(cd_recorder* recorder, F action) noexcept {
    if (!recorder) return CD_E_INVALID_ARGUMENT;
    try { return action(); }
    catch (const std::invalid_argument& error) { std::lock_guard lock(recorder->mutex); recorder->last_error = error.what(); return CD_E_INVALID_ARGUMENT; }
    catch (const std::logic_error& error) { std::lock_guard lock(recorder->mutex); recorder->last_error = error.what(); return CD_E_INVALID_STATE; }
    catch (const std::exception& error) { std::lock_guard lock(recorder->mutex); recorder->last_error = error.what(); return CD_E_INTERNAL; }
    catch (...) { return CD_E_INTERNAL; }
}
}
int32_t CD_CALL cd_recorder_get_contract(cd_session_contract* contract) try {
    validate(contract);
    *contract = {{sizeof(cd_session_contract), CD_ABI_VERSION}, sizeof(void*), sizeof(cd_session_config),
        sizeof(cd_session_save_request), sizeof(cd_session_save_status), sizeof(cd_session_health),
        sizeof(cd_overlay_update), sizeof(cd_artwork_update), sizeof(cd_event_poll),
        CD_CAP_CAPTURE | CD_CAP_REPLAY_SAVE | CD_CAP_AUDIO | CD_CAP_FULL_SESSION |
            CD_CAP_OVERLAYS | CD_CAP_ASYNC_CONTROL};
    return CD_OK;
} catch (...) { return CD_E_UNSUPPORTED_ABI; }
int32_t CD_CALL cd_recorder_create(const cd_session_config* config, cd_recorder** recorder) try {
    if (!recorder) return CD_E_INVALID_ARGUMENT; *recorder = nullptr;
    validate(config);
    if (!runtime_versions_match()) return CD_E_RUNTIME_MISMATCH;
    if (config->qpc_frequency <= 0 || config->frame_rate < 30 || config->frame_rate > 120 ||
        config->application_count > 4096 || (config->application_count && !config->applications)) return CD_E_INVALID_ARGUMENT;
    auto result = std::make_unique<cd_recorder>();
    std::memcpy(result->boot_id, config->boot_id, 16); result->utc_anchor_ticks = config->utc_anchor_ticks;
    RecorderSessionConfig native;
    native.capture.qpc_anchor = config->qpc_anchor; native.capture.qpc_frequency = config->qpc_frequency;
    native.capture.monotonic_anchor_us = static_cast<int64_t>(config->qpc_anchor * (1000000.0L / config->qpc_frequency));
    native.capture.window = config->target_window; native.capture.monitor = config->target_monitor;
    native.capture.monitor_device_name = copy(config->monitor_device_name);
    native.capture.max_height = config->max_height;
    native.capture.capture_region = {config->capture_x, config->capture_y, config->capture_width, config->capture_height};
    native.capture.width = std::max(2, config->capture_width & ~1);
    native.capture.height = std::max(2, config->capture_height & ~1);
    native.capture.fps = config->frame_rate; native.capture.bitrate_mbps = config->bitrate_mbps;
    native.capture.cpu_encoder = copy(config->encoder_mode) == L"CPU";
    native.capture.av1 = copy(config->video_codec) == L"AV1";
    native.capture.variable_frame_rate = copy(config->frame_rate_mode) == L"VFR";
    native.capture.capture_cursor = (config->flags & CD_SESSION_CURSOR) != 0;
    native.capture.protect_frame_rate = (config->flags & CD_SESSION_ADAPTIVE_FPS) != 0;
    native.capture.capture_hdr = (config->flags & CD_SESSION_HDR) != 0;
    native.capture.prefer_dxgi = copy(config->diagnostic_force_dxgi) == L"1";
    native.capture.disable_gpu_processing = copy(config->diagnostic_disable_direct_blt) == L"1";
    native.capture.d3d_debug = copy(config->diagnostic_d3d_debug) == L"1";
    native.capture.pacing_policy = text(config->diagnostic_pacing_policy);
    if (native.capture.pacing_policy.empty()) native.capture.pacing_policy = "latest";
    const auto delay = copy(config->diagnostic_nvenc_delay);
    native.capture.nvenc_delay = delay == L"4" ? 4 : delay == L"8" ? 8 : 0;
    if (_wcsicmp(copy(config->capture_source).c_str(), L"Desktop") != 0) {
        native.capture.target_executable = copy(config->game_executable);
        native.capture.target_title = copy(config->game_window_title);
        native.capture.target_class = copy(config->game_window_class);
    }
    native.history_seconds = config->duration_seconds;
    native.work_directory = copy(config->work_directory); native.ffmpeg = copy(config->ffmpeg_path);
    AudioGraphConfig audio;
    audio.history_directory = native.work_directory / L"audio"; audio.ffmpeg = native.ffmpeg;
    audio.rnnoise_model = copy(config->rnnoise_model); audio.retention_us = int64_t(config->duration_seconds) * 1000000;
    audio.qpc_anchor = config->qpc_anchor; audio.qpc_frequency = config->qpc_frequency;
    audio.monotonic_anchor_us = native.capture.monotonic_anchor_us;
    audio.game_executable = copy(config->game_executable); audio.output_device_id = copy(config->chat_device_id);
    audio.excluded_processes = strings(config->excluded_processes); audio.microphone_device_ids = strings(config->microphone_devices);
    for (const auto& name : strings(config->chat_processes)) audio.applications.push_back({name, 1});
    for (uint32_t i = 0; i < config->application_count; ++i)
        audio.applications.push_back({copy(config->applications[i].name), std::clamp(config->applications[i].gain_percent, 0, 150) / 100.f});
    audio.game_gain = std::clamp(config->game_gain_percent, 0, 150) / 100.f;
    audio.microphone_gain = std::clamp(config->microphone_gain_percent, 0, 150) / 100.f;
    audio.microphone_stereo = copy(config->microphone_channel_mode) == L"Stereo";
    audio.noise_suppression = (config->flags & CD_SESSION_NOISE_SUPPRESSION) != 0;
    audio.gate_threshold_db = config->microphone_gate_db;
    native.audio_graph = audio;
    if (config->flags & CD_SESSION_FULL) native.full_session = FullSessionConfig{copy(config->full_session_path)};
    // Every string is copied at the call boundary, including publication-only
    // settings which the managed host uses after completion.
    const cd_string16 values[] = {config->chat_device_name, config->chat_device_id, config->microphone_device_name,
        config->game_name, config->game_executable, config->game_window_title, config->game_window_class,
        config->video_codec, config->encoder_mode, config->encoder_profile, config->frame_rate_mode, config->pacing_mode,
        config->capture_source, config->monitor_device_name, config->process_priority, config->microphone_channel_mode,
        config->work_directory, config->ffmpeg_path, config->rnnoise_model, config->full_session_path,
        config->full_session_codec, config->full_session_container, config->library_folder, config->file_name_scheme,
        config->custom_file_name_template, config->save_hotkey, config->full_session_hotkey,
        config->diagnostic_force_dxgi, config->diagnostic_disable_direct_blt, config->diagnostic_pacing_policy,
        config->diagnostic_nvenc_delay, config->diagnostic_d3d_debug};
    for (auto value : values) result->copied_strings.push_back(copy(value));
    result->session = std::make_unique<RecorderSession>(std::move(native));
    *recorder = result.release(); return CD_OK;
} catch (const std::invalid_argument&) { return CD_E_INVALID_ARGUMENT; }
catch (...) { return CD_E_INTERNAL; }
int32_t CD_CALL cd_recorder_start(cd_recorder* r) { return invoke(r, [&] { r->session->start(); return CD_OK; }); }
int32_t CD_CALL cd_recorder_stop(cd_recorder* r) { return invoke(r, [&] { return r->session->stop() ? CD_OK : CD_E_INVALID_STATE; }); }
void CD_CALL cd_recorder_destroy(cd_recorder* r) { if (!r) return; if (cd_recorder_stop(r) != CD_OK) return; delete r; }
int32_t CD_CALL cd_recorder_pause(cd_recorder* r, uint32_t paused) { return invoke(r, [&] { if (paused > 1) return CD_E_INVALID_ARGUMENT; r->session->pause(paused != 0); return CD_OK; }); }
int32_t CD_CALL cd_recorder_frame_rate(cd_recorder* r, uint32_t fps) { return invoke(r, [&] { if (fps < 30 || fps > 120) return CD_E_INVALID_ARGUMENT; r->session->frame_rate(fps); return CD_OK; }); }
int32_t CD_CALL cd_recorder_health(cd_recorder* r, cd_session_health* health) { return invoke(r, [&]() -> int32_t {
    validate(health); const auto h = r->session->health();
    health->running = h.running; health->paused = h.paused; health->restart_required = h.restart_required;
    health->active_fps = h.active_fps; health->queue_depth = h.queue_depth; health->queue_capacity = h.queue_capacity;
    health->acquired = h.acquired; health->encoded = h.encoded; health->replaced = h.replaced;
    health->generation = h.generation; health->detector_copies = h.detector_copies;
    health->input_revision = r->session->input_revision();
    std::string error; { std::lock_guard lock(r->mutex); error = r->last_error; }
    const auto full = r->session->full_session_status();
    std::ostringstream details; details << "{\"source\":" << quoted(h.source) << ",\"encoder\":" << quoted(h.encoder)
        << ",\"fullSessionPath\":" << quoted(utf8(r->session->full_session_path().native()))
        << ",\"fullSessionDurationUs\":" << full.duration_us
        << ",\"error\":" << quoted(h.error) << ",\"controlError\":" << quoted(error)
        << ",\"fullSessionRunning\":" << (full.running ? "true" : "false")
        << ",\"fullSessionFinished\":" << (full.finished ? "true" : "false") << ",\"fullSessionError\":" << quoted(full.error)
        << ",\"inputFps\":" << h.input_fps << ",\"uniqueFps\":" << h.unique_fps << ",\"outputFps\":" << h.output_fps
        << ",\"outputWidth\":" << h.output_width << ",\"outputHeight\":" << h.output_height
        << ",\"duplicates\":" << h.duplicates << ",\"submitted\":" << h.submitted << ",\"sourceRecoveries\":" << h.source_recoveries
        << ",\"queueAgeMs\":" << h.queue_age_ms << ",\"processingMs\":" << h.processing_ms
        << ",\"processingPath\":" << quoted(h.processing_path)
        << ",\"textureReadbackMs\":" << h.texture_readback_ms << ",\"videoProcessorMs\":" << h.video_processor_ms
        << ",\"softwareConvertMs\":" << h.software_convert_ms << ",\"hardwareUploadMs\":" << h.hardware_upload_ms
        << ",\"overlayComposeMs\":" << h.overlay_compose_ms << ",\"gpuConversionFallbacks\":" << h.gpu_conversion_fallbacks
        << ",\"gpuConversionFallbackError\":" << quoted(h.gpu_conversion_fallback_error)
        << ",\"submissionMs\":" << h.submission_ms << ",\"completionMs\":" << h.completion_ms
        << ",\"submissionP95Ms\":" << h.submission_p95_ms << ",\"completionP95Ms\":" << h.completion_p95_ms
        << ",\"queueAgeMaxMs\":" << h.queue_age_max_ms << ",\"processingMaxMs\":" << h.processing_max_ms
        << ",\"submissionMaxMs\":" << h.submission_max_ms << ",\"completionMaxMs\":" << h.completion_max_ms
        << ",\"surfacesInUse\":" << h.surfaces_in_use << ",\"surfaceCapacity\":" << h.surface_capacity
        << ",\"surfacesInUsePeak\":" << h.surfaces_in_use_peak << ",\"surfacesAllocated\":" << h.surfaces_allocated
        << ",\"encoderPlanned\":" << (h.encoder_planned ? "true" : "false")
        << ",\"encoderVendor\":" << quoted(h.encoder_vendor) << ",\"requestedCodec\":" << quoted(h.requested_codec)
        << ",\"effectiveCodec\":" << quoted(h.effective_codec) << ",\"encoderSlots\":" << h.encoder_slots
        << ",\"encoderDelay\":" << h.encoder_delay << ",\"outputDelayFrames\":" << h.output_delay_frames
        << ",\"maxInFlight\":" << h.max_in_flight << ",\"poolCapacity\":" << h.pool_capacity << ",\"poolBytes\":" << h.pool_bytes
        << ",\"zeroCopyStatus\":" << quoted(h.zero_copy_status) << ",\"zeroCopyProbePassed\":" << (h.zero_copy_probe_passed ? "true" : "false")
        << ",\"captureAdapterVendor\":" << h.capture_adapter_vendor
        << ",\"submissionP50Ms\":" << h.submission_p50_ms << ",\"completionP50Ms\":" << h.completion_p50_ms
        << ",\"packetPayloadBytes\":" << h.packet_payload_bytes << ",\"packetBufferBytes\":" << h.packet_buffer_bytes
        << ",\"readbackStagingSlots\":" << h.readback_staging_slots << ",\"readbackStagingInUse\":" << h.readback_staging_in_use
        << ",\"readbackStagingPeak\":" << h.readback_staging_peak << ",\"readbackCpuFrames\":" << h.readback_cpu_frames
        << ",\"readbackCpuFramesInUse\":" << h.readback_cpu_frames_in_use << ",\"readbackCpuFramesPeak\":" << h.readback_cpu_frames_peak
        << ",\"readbackP50Ms\":" << h.readback_p50_ms << ",\"readbackP95Ms\":" << h.readback_p95_ms
        << ",\"readbackMapWaitP50Ms\":" << h.readback_map_wait_p50_ms << ",\"readbackMapWaitP95Ms\":" << h.readback_map_wait_p95_ms
        << ",\"readbackMapStalls\":" << h.readback_map_stalls << ",\"readbackPressureDrops\":" << h.readback_pressure_drops
        << ",\"frameAllocations\":" << h.frame_allocations
        << ",\"detectorSamples\":" << h.detector_samples << ",\"detectorSampleFps\":" << h.detector_sample_fps
        << ",\"detectorSkipped\":" << h.detector_skipped << ",\"detectorFallbacks\":" << h.detector_fallbacks
        << ",\"detectorGpuP50Ms\":" << h.detector_gpu_p50_ms << ",\"detectorGpuP95Ms\":" << h.detector_gpu_p95_ms
        << ",\"detectorReadbackP50Ms\":" << h.detector_readback_p50_ms << ",\"detectorReadbackP95Ms\":" << h.detector_readback_p95_ms
        << ",\"detectorConvertP50Ms\":" << h.detector_convert_p50_ms << ",\"detectorConvertP95Ms\":" << h.detector_convert_p95_ms
        << ",\"detectorBytesPerSample\":" << h.detector_bytes_per_sample << ",\"detectorReadbackBytes\":" << h.detector_readback_bytes
        << ",\"detectorTexturesAllocated\":" << h.detector_textures_allocated << ",\"detectorBuffersAllocated\":" << h.detector_buffers_allocated
        << ",\"detectorBuilds\":" << h.detector_builds << ",\"detectorAllocationsAfterWarmup\":" << h.detector_allocations_after_warmup
        << ",\"overlayEnabled\":" << (h.overlay.enabled ? "true" : "false") << ",\"overlayState\":" << quoted(h.overlay.state)
        << ",\"overlayPath\":" << quoted(h.overlay.path)
        << ",\"overlayCameraRequested\":" << (h.overlay.camera_requested ? "true" : "false")
        << ",\"overlayCameraReady\":" << (h.overlay.camera_ready ? "true" : "false")
        << ",\"overlayCameraStale\":" << (h.overlay.camera_stale ? "true" : "false")
        << ",\"overlayCameraGeneration\":" << h.overlay.camera_generation
        << ",\"overlayKeyboardRequested\":" << (h.overlay.keyboard_requested ? "true" : "false")
        << ",\"overlayKeyboardReady\":" << (h.overlay.keyboard_ready ? "true" : "false")
        << ",\"overlayKeyboardRevision\":" << h.overlay.keyboard_revision << ",\"overlaySettingsRevision\":" << h.overlay.settings_revision
        << ",\"overlayCameraFrames\":" << h.overlay.camera_frames << ",\"overlayKeyboardFrames\":" << h.overlay.keyboard_frames
        << ",\"overlaySkippedFrames\":" << h.overlay.skipped_frames << ",\"overlayLastSkipReason\":" << quoted(h.overlay.last_skip_reason)
        << ",\"overlayLastRenderedUs\":" << h.overlay.last_rendered_us << ",\"overlayFailure\":" << quoted(h.overlay.failure)
        << ",\"overlayGpuUploads\":" << h.overlay.gpu_uploads << ",\"overlayGpuUploadFailures\":" << h.overlay.gpu_upload_failures
        << ",\"overlayGpuFailure\":" << quoted(h.overlay.gpu_failure) << ",\"overlayCpuRoundTrips\":" << h.overlay.cpu_roundtrips
        << ",\"overlayGpuP50Ms\":" << h.overlay.gpu_p50_ms << ",\"overlayGpuP95Ms\":" << h.overlay.gpu_p95_ms
        << ",\"wgcCallbackFps\":" << h.wgc_callback_fps << ",\"wgcDeliveredFps\":" << h.wgc_delivered_fps
        << ",\"wgcOverwrittenFps\":" << h.wgc_overwritten_fps
        << ",\"acquiredFps\":" << h.input_fps << ",\"duplicateFps\":" << h.duplicate_fps
        << ",\"pacingReplacedFps\":" << h.replaced_fps << ",\"selectionDroppedFps\":" << h.selection_dropped_fps
        << ",\"selectionDropped\":" << h.selection_dropped << ",\"frameSelection\":" << quoted(h.frame_selection)
        << ",\"sourceQueueDepth\":" << h.source_queue_depth << ",\"sourceQueuePeak\":" << h.source_queue_peak
        << ",\"sourceQueueCapacity\":" << h.source_queue_capacity
        << ",\"captureLatencyP50Ms\":" << h.capture_latency_p50_ms << ",\"captureLatencyP95Ms\":" << h.capture_latency_p95_ms
        << ",\"acquireLatencyP50Ms\":" << h.acquire_latency_p50_ms << ",\"acquireLatencyP95Ms\":" << h.acquire_latency_p95_ms
        << ",\"selectionErrorP50Ms\":" << h.selection_error_p50_ms << ",\"selectionErrorP95Ms\":" << h.selection_error_p95_ms
        << ",\"outputJudderP50Ms\":" << h.output_judder_p50_ms << ",\"outputJudderP95Ms\":" << h.output_judder_p95_ms
        << ",\"backpressureDrops\":" << h.backpressure_drops << ",\"encoderBusyDrops\":" << h.encoder_busy_drops
        << ",\"retainedPressureDrops\":" << h.retained_pressure_drops << ",\"poolPressureDrops\":" << h.pool_pressure_drops
        << ",\"backpressureDropFps\":" << h.backpressure_drop_fps << ",\"backpressureWaitMaxMs\":" << h.backpressure_wait_max_ms
        << ",\"encoderStallRecoveries\":" << h.encoder_stall_recoveries
        << ",\"overloadWindows\":" << h.overload_windows << ",\"qualifiedWindows\":" << h.qualified_windows
        << ",\"hardwareInput\":" << (h.hardware_input ? "true" : "false") << ",\"hdr\":" << (h.hdr ? "true" : "false")
        << ",\"frameRateProtected\":" << (h.frame_rate_protected ? "true" : "false")
        << ",\"adapter\":" << quoted(utf8(h.source_details.adapter)) << ",\"adapterLuid\":" << h.source_details.adapter_luid
        << ",\"gpuDevicePriority\":" << h.source_details.gpu_device_priority
        << ",\"gpuDevicePriorityApplied\":" << (h.source_details.gpu_device_priority_applied ? "true" : "false")
        << ",\"displayProfileAvailable\":" << (h.source_details.display_profile_available ? "true" : "false")
        << ",\"hdrDisplay\":" << (h.source_details.hdr_display ? "true" : "false")
        << ",\"hdrConversion\":" << (h.source_details.hdr_conversion ? "true" : "false")
        << ",\"sdrWhiteNits\":" << h.source_details.sdr_white_nits
        << ",\"sourceCallbacks\":" << h.source_details.callbacks << ",\"sourceDelivered\":" << h.source_details.frames_delivered
        << ",\"sourceOverwritten\":" << h.source_details.overwritten << ",\"sourceResizes\":" << h.source_details.resizes
        << ",\"sourceOwnedTextureCapacity\":" << h.source_details.owned_texture_capacity
        << ",\"sourceOwnedTexturesAllocated\":" << h.source_details.owned_textures_allocated
        << ",\"sourceOwnedTexturesLeased\":" << h.source_details.owned_textures_leased
        << ",\"sourceOwnedTexturesPeak\":" << h.source_details.owned_textures_peak
        << ",\"sourceOwnedTexturePressureDrops\":" << h.source_details.owned_texture_pressure_drops
        << ",\"sourceCopyP50Ms\":" << h.source_details.copy_p50_ms << ",\"sourceCopyP95Ms\":" << h.source_details.copy_p95_ms
        << ",\"sourceCursorCompositionMs\":" << h.source_details.cursor_composition_ms
        << ",\"requestedInterval100ns\":" << h.source_details.requested_interval_100ns
        << ",\"appliedInterval100ns\":" << h.source_details.applied_interval_100ns
        << ",\"updateIntervalAvailable\":" << (h.source_details.update_interval_available ? "true" : "false")
        << ",\"wgcCadenceMode\":" << quoted(h.source_details.cadence_mode) << ",\"wgcCadenceFps\":" << h.source_details.cadence_fps
        << ",\"wgcUpdateTicks\":" << h.source_details.update_ticks << ",\"wgcProducerCeilingFps\":" << h.source_details.producer_ceiling_fps
        << ",\"displayRefreshHz\":" << h.source_details.display_refresh_hz << ",\"wgcCallbackUs\":" << h.source_details.callback_us
        << ",\"closedSessions\":[";
    bool first = true;
    for (const auto& closed : r->session->closed_sessions()) {
        if (!first) details << ','; first = false;
        details << "{\"sequence\":" << closed.sequence << ",\"path\":" << quoted(utf8(closed.path.native()))
            << ",\"error\":" << quoted(closed.status.error) << ",\"durationUs\":" << closed.status.duration_us << '}';
    }
    details << "]}";
    return output(health->details, details.str());
}); }
int32_t CD_CALL cd_recorder_save_begin(cd_recorder* r, const cd_session_save_request* request) { return invoke(r, [&] {
    validate(request); r->session->save(save_id(request->id), request->start_us, request->end_us, copy(request->output_path)); return CD_OK;
}); }
int32_t CD_CALL cd_recorder_save_status(cd_recorder* r, const uint8_t* id, cd_session_save_status* status) { return invoke(r, [&]() -> int32_t {
    validate(status); const auto saved = r->session->saved(save_id(id)); if (!saved) return CD_E_INVALID_ARGUMENT;
    status->state = CD_SAVE_PENDING;
    if (saved->completion.wait_for(std::chrono::seconds(0)) != std::future_status::ready) return CD_OK;
    const auto result = saved->completion.get();
    status->state = result.cancelled ? CD_SAVE_CANCELLED : result.error.empty() ? CD_SAVE_COMPLETE : CD_SAVE_FAILED;
    status->duration_us = result.duration_us; status->generation = result.generation; status->frozen = result.frozen;
    std::ostringstream details; details << "{\"output\":" << quoted(utf8(result.output.native())) << ",\"error\":" << quoted(result.error)
        << ",\"audioMappings\":" << mappings(result.audio_mappings) << ",\"overlayMappings\":" << mappings(result.overlay_mappings)
        << ",\"inputStartUs\":" << saved->overlays->start_us << ",\"inputEndUs\":" << saved->overlays->end_us
        << ",\"burnedCamera\":" << (saved->burned_camera ? "true" : "false")
        << ",\"burnedKeyboard\":" << (saved->burned_keyboard ? "true" : "false")
        << ",\"input\":" << saved->overlays->input.json() << ",\"settings\":[";
    bool first = true;
    for (const auto& setting : saved->overlays->settings) {
        if (!first) details << ','; first = false;
        details << "{\"atUs\":" << setting.at_us << ",\"revision\":" << setting.revision
            << ",\"value\":" << (setting.settings_json.empty() ? "null" : setting.settings_json) << '}';
    }
    details << "],\"camera\":["; first = true;
    for (const auto& segment : saved->overlays->camera_segments) {
        if (!first) details << ','; first = false;
        details << "{\"generation\":" << segment.generation << ",\"startUs\":" << segment.start_us
            << ",\"endUs\":" << segment.end_us << ",\"path\":" << quoted(utf8(segment.path.native()))
            << ",\"completed\":" << (segment.completed ? "true" : "false") << '}';
    }
    details << "]}"; return output(status->details, details.str());
}); }
int32_t CD_CALL cd_recorder_save_cancel(cd_recorder* r, const uint8_t* id) { return invoke(r, [&] { r->session->cancel_save(save_id(id)); return CD_OK; }); }
int32_t CD_CALL cd_recorder_save_release(cd_recorder* r, const uint8_t* id) { return invoke(r, [&] { return r->session->release_save(save_id(id)) ? CD_OK : CD_E_INVALID_STATE; }); }
int32_t CD_CALL cd_recorder_overlay(cd_recorder* r, const cd_overlay_update* value) { return invoke(r, [&] { validate(value); r->session->overlay_settings(overlay(*value)); return CD_OK; }); }
int32_t CD_CALL cd_recorder_artwork(cd_recorder* r, const cd_artwork_update* value) { return invoke(r, [&] {
    validate(value); return r->session->artwork(OverlayBitmap::copy(value->width, value->height, value->stride,
        value->pixels, value->length, value->revision, value->at_us, value->premultiplied != 0)) ? CD_OK : CD_E_INVALID_STATE;
}); }
int32_t CD_CALL cd_recorder_keys(cd_recorder* r, cd_bytes* json) { return invoke(r, [&]() -> int32_t {
    if (!json) return CD_E_INVALID_ARGUMENT; std::ostringstream out; out << '['; bool first = true;
    for (const auto& key : r->session->pressed_keys()) { if (!first) out << ','; first = false;
        out << "{\"ScanCode\":" << key.scan_code << ",\"E0\":" << (key.e0 ? "true" : "false")
            << ",\"E1\":" << (key.e1 ? "true" : "false") << ",\"MouseButton\":" << int(key.mouse_button) << '}'; }
    out << ']'; return output(*json, out.str());
}); }
int32_t CD_CALL cd_recorder_detector_regions(cd_recorder* r, const cd_rect* regions, uint32_t count,
    const cd_rect* masks, uint32_t mask_count, uint32_t width, uint32_t height) { return invoke(r, [&] {
    if (count > 1024 || mask_count > 1024 || (count && !regions) || (mask_count && !masks)) return CD_E_INVALID_ARGUMENT;
    std::vector<CaptureRect> rects, exclusions;
    for (uint32_t i = 0; i < count; ++i) rects.push_back({regions[i].x,regions[i].y,regions[i].width,regions[i].height});
    for (uint32_t i = 0; i < mask_count; ++i) exclusions.push_back({masks[i].x,masks[i].y,masks[i].width,masks[i].height});
    r->session->detector_regions(std::move(rects), std::move(exclusions), width, height); return CD_OK;
}); }
int32_t CD_CALL cd_recorder_detector_copy(cd_recorder* r, cd_frame_copy* frame) { return invoke(r, [&]() -> int32_t {
    validate(frame); const auto value = r->session->detector_frame(); if (!value) return CD_E_UNAVAILABLE;
    frame->width = value->width; frame->height = value->height; frame->stride = value->stride;
    frame->timestamp_us = value->timestamp_us; frame->revision = value->timestamp_us;
    return output(frame->pixels, value->bgra.data(), value->bgra.size());
}); }
int32_t CD_CALL cd_recorder_camera_copy(cd_recorder* r, cd_frame_copy* frame) { return invoke(r, [&]() -> int32_t {
    validate(frame); const auto value = r->session->camera_preview(); if (!value) return CD_E_UNAVAILABLE;
    frame->width = value->width; frame->height = value->height; frame->stride = value->stride;
    frame->timestamp_us = value->at_us; frame->revision = value->revision;
    return output(frame->pixels, value->bgra.data(), value->bgra.size());
}); }
int32_t CD_CALL cd_recorder_detector_set(cd_recorder* r, const cd_normalized_rect* regions, uint32_t count, uint32_t counter_mask) {
    return invoke(r, [&] {
        if ((count != 0 && count != 3) || (count && !regions) || counter_mask > 1) return CD_E_INVALID_ARGUMENT;
        std::array<CaptureNormalizedRect, 3> copied{};
        for (uint32_t i = 0; i < count; ++i) copied[i] = {regions[i].x, regions[i].y, regions[i].width, regions[i].height};
        r->session->detector_regions(copied, count != 0, counter_mask != 0); return CD_OK;
    });
}
int32_t CD_CALL cd_recorder_detector_read(cd_recorder* r, cd_detector_copy* frame) { return invoke(r, [&]() -> int32_t {
    validate(frame); const auto value = r->session->detector_snapshot(); if (!value) return CD_E_UNAVAILABLE;
    frame->timestamp_us = value->timestamp_us;
    int32_t result = CD_OK;
    for (size_t i = 0; i < 4; ++i) {
        const auto& image = i < 3 ? value->regions[i] : value->third_mask;
        frame->widths[i] = image.width; frame->heights[i] = image.height;
        const auto copied = output(frame->images[i], image.pixels.data(), image.pixels.size());
        if (copied != CD_OK) result = copied;
    }
    return result;
}); }
int32_t CD_CALL cd_recorder_verify(cd_string16 directory, cd_string16 ffmpeg, uint32_t fps, uint32_t variable, cd_bytes* report) {
    if (!report || variable > 1 || fps < 30 || fps > 120) return CD_E_INVALID_ARGUMENT;
    report->required = 65536;
    if (!report->data || report->capacity < report->required) return CD_E_BUFFER_TOO_SMALL;
    try { return output(*report, verify_recording(copy(directory), copy(ffmpeg), fps, variable != 0)); }
    catch (const std::exception& error) { output(*report, std::string("{\"error\":") + quoted(error.what()) + '}'); return CD_E_INTERNAL; }
    catch (...) { return CD_E_INTERNAL; }
}
int32_t CD_CALL cd_recorder_events(cd_recorder* recorder, cd_event_poll* events) {
    return invoke(recorder, [&] {
        validate(events);
        const auto result = recorder->session->events(events->after_sequence, events->timeout_ms);
        events->mask = result.mask; events->sequence = result.sequence; return CD_OK;
    });
}
