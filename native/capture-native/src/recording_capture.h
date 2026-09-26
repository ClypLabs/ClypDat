#pragma once
#include "video_encoder.h"
#include "encoder_backend.h"
#include "recording_overlays.h"
#include <chrono>
#include <array>
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>
struct ID3D11Device;
struct ID3D11Texture2D;

namespace clypdat {
struct CaptureRect { int x = 0, y = 0, width = 0, height = 0; bool operator==(const CaptureRect&) const = default; };
struct CaptureNormalizedRect { double x=0,y=0,width=0,height=0; };
struct RecordingDetectorImage { int width=0,height=0;std::vector<uint8_t> pixels; };
struct RecordingDetectorSnapshot {
    int64_t timestamp_us=0;
    std::array<RecordingDetectorImage,3> regions;
    RecordingDetectorImage third_mask;
};
// A frame's path to the recorder, on the same monotonic microsecond timeline
// as timestamp_us; 0 where a source has no such stage. WGC fills the
// callback stages: FrameArrived entry, TryGetNextFrame returned, the pooled
// copy issued and the frame published. RecordingCapture adds acquisition.
// dwm_vblank_us and dwm_compose_us are DwmGetCompositionTimingInfo at the
// callback, recorded only for benchmarks (wgc_dwm_timing).
struct CaptureFrameTiming {
    int64_t callback_us = 0, taken_us = 0, published_us = 0, acquired_us = 0;
    int64_t dwm_vblank_us = 0, dwm_compose_us = 0;
};
struct CapturePixels {
    int width = 0, height = 0, stride = 0;
    // WGC: Direct3D11CaptureFrame.SystemRelativeTime, the QPC time of the
    // vblank the composed frame is shown at, so it follows the frame's
    // arrival. DXGI: LastPresentTime (or LastMouseUpdateTime).
    int64_t timestamp_us = 0;
    std::vector<uint8_t> bgra;
    std::shared_ptr<ID3D11Texture2D> texture;
    CaptureFrameTiming timing;
};
// One selected frame's timeline (benchmarks).
struct CaptureFrameTimeline { int64_t timestamp_us = 0; CaptureFrameTiming timing; int64_t selected_us = 0; };
struct RecordingSourceHealth {
    // overwritten counts delivered frames dropped before acquisition consumed them.
    uint64_t callbacks = 0, frames_delivered = 0, overwritten = 0, resizes = 0;
    // Owned WGC frame copies from a bounded texture pool (CapturedFrameStore):
    // capacity, textures created, leased now and at most, frames dropped
    // because every owned texture was held, and CPU time to issue each copy.
    int owned_texture_capacity = 0, owned_textures_leased = 0, owned_textures_peak = 0;
    uint64_t owned_textures_allocated = 0, owned_texture_pressure_drops = 0;
    double copy_p50_ms = 0, copy_p95_ms = 0;
    int64_t requested_interval_100ns = 0, applied_interval_100ns = 0;
    double display_refresh_hz = 0;
    // WGC producer cadence: "safe" (capture_wgc_update_ticks), "fixed"
    // (wgc_update_ticks override) or "unavailable" (no MinUpdateInterval on
    // this Windows build: WGC delivers every composition). update_ticks is
    // 0 when the refresh rate is unknown.
    std::string cadence_mode;
    int cadence_fps = 0, update_ticks = 0;
    double producer_ceiling_fps = 0;
    double cursor_composition_ms = 0;
    // Desktop Duplication cursor: draws composed on the GPU and on the CPU
    // (fallback shapes, or the reference path), shape changes and uploads,
    // cursor textures created, bytes read back for CPU draws, compose time
    // and the wait for the device lock around a GPU draw.
    uint64_t cursor_gpu_draws = 0, cursor_cpu_draws = 0, cursor_cpu_fallbacks = 0, cursor_shape_changes = 0, cursor_uploads = 0;
    uint64_t cursor_textures_created = 0, cursor_readback_bytes = 0;
    double cursor_compose_p50_ms = 0, cursor_compose_p95_ms = 0, cursor_lock_wait_p95_ms = 0;
    uint64_t callback_us = 0; // Time spent in WGC FrameArrived callbacks.
    // Desktop Duplication reopened after losing access (a mode, rotation or
    // desktop change), and reopen attempts that failed and were retried.
    uint64_t duplication_reopens = 0, duplication_reopen_failures = 0;
    uint64_t adapter_luid = 0;
    std::wstring adapter;
    bool update_interval_available = false;
    int gpu_device_priority=0;
    bool gpu_device_priority_applied=false;
    bool display_profile_available=false,hdr_display=false,hdr_conversion=false;
    float sdr_white_nits=80;
};
// Sources return owned pixels. This seam is native-only; production never calls
// managed code for frames. Generated fixtures use the identical pipeline.
class RecordingFrameSource {
public:
    virtual ~RecordingFrameSource() = default;
    virtual bool acquire(CapturePixels&, std::chrono::milliseconds timeout) = 0;
    virtual bool eligible() const = 0;
    virtual void set_frame_rate(int) {}
    virtual const char* name() const = 0;
    virtual ID3D11Device* d3d_device() const { return nullptr; }
    virtual void stop() {}
    virtual RecordingSourceHealth diagnostics() const { return {}; }
    virtual bool recover() { return false; }
    virtual CaptureRect content_bounds() const { return {}; }
    virtual bool foreground() const { return eligible(); }
    virtual bool switch_backend(bool) { return false; }
};
struct RecordingCaptureConfig {
    uintptr_t window = 0, monitor = 0;
    std::wstring monitor_device_name;
    CaptureRect capture_region;
    int max_height = 0;
    int width = 1920, height = 1080, fps = 60, bitrate_mbps = 20;
    bool variable_frame_rate = false, capture_cursor = true, prefer_dxgi = false;
    bool cpu_encoder = false, av1 = false, protect_frame_rate = false;
    bool capture_hdr = false;
    bool display_hdr=false,display_profile_available=false;
    bool disable_gpu_processing = false;
    bool d3d_debug=false;
    int nvenc_delay=0;
    std::string pacing_policy="latest";
    // "timestamp" gives each output tick the queued source frame nearest one
    // output interval back, so rate-matched sources keep distinct frames;
    // "newest" keeps single-slot newest-frame sampling for comparison.
    std::string frame_selection="timestamp";
    int source_queue_depth=2;
    // Benchmarks: fixes the WGC MinUpdateInterval at this many display ticks.
    int wgc_update_ticks=0;
    // Benchmarks: records DWM composition timing at every WGC callback.
    bool wgc_dwm_timing=false;
    // Benchmarks: the Desktop Duplication path before pooling, a new owned
    // texture per frame and the CPU cursor.
    bool dxgi_reference_path=false;
    std::wstring target_executable,target_title,target_class;
    float sdr_white_nits = 80;
    int64_t qpc_anchor = 0, qpc_frequency = 0, monotonic_anchor_us = 0;
    std::vector<CaptureRect> detector_regions;
    std::vector<CaptureRect> detector_masks;
    int detector_width = 0, detector_height = 0;
    std::array<CaptureNormalizedRect,3> detector_normalized{};
    bool detector_enabled=false,detector_counter_mask=false;
};
struct RecordingCaptureHealth {
    uint64_t acquired = 0, encoded = 0, replaced = 0, generation = 0;
    uint64_t detector_copies = 0;
    int queue_depth = 0, queue_capacity = 0, active_fps = 0;
    int output_width=0,output_height=0;
    bool running = false, paused = false, restart_required = false;
    std::string source, encoder, error;
    // The failure behind the last source recovery or backend switch.
    std::string source_recovery_error;
    uint64_t duplicates = 0, submitted = 0, source_recoveries = 0;
    uint64_t unique_frames=0;
    // input_fps counts frames the acquisition thread took from the source;
    // the wgc_* rates come from the capture callback itself.
    double input_fps = 0, unique_fps = 0, output_fps = 0;
    double wgc_callback_fps = 0, wgc_delivered_fps = 0, wgc_overwritten_fps = 0;
    double duplicate_fps = 0, replaced_fps = 0, selection_dropped_fps = 0;
    uint64_t selection_dropped = 0;
    int source_queue_depth = 0, source_queue_peak = 0, source_queue_capacity = 0;
    // capture_latency: source timestamp to the output tick that selected it.
    double capture_latency_p50_ms = 0, capture_latency_p95_ms = 0;
    // Acquisition time minus source timestamp. WGC stamps a frame with the
    // vblank it is displayed at, which follows its arrival, so this is
    // negative there: it is not a delivery delay.
    double timestamp_to_acquire_p50_ms = 0, timestamp_to_acquire_p95_ms = 0;
    // The delivery chain of selected frames: source timestamp minus
    // FrameArrived entry (WGC: how far ahead of its display the frame
    // arrives), TryGetNextFrame, the pooled copy, publication to acquisition,
    // and acquisition to the output tick that selected it.
    double source_lead_p50_ms = 0, source_lead_p95_ms = 0, callback_take_p50_ms = 0, callback_take_p95_ms = 0;
    double callback_copy_p50_ms = 0, callback_copy_p95_ms = 0, handoff_p50_ms = 0, handoff_p95_ms = 0;
    double selection_wait_p50_ms = 0, selection_wait_p95_ms = 0;
    // Output timing accuracy: selected frame against the sampled instant, and
    // the source-time step between consecutive fresh outputs against one
    // output interval (judder).
    double selection_error_p50_ms = 0, selection_error_p95_ms = 0, output_judder_p50_ms = 0, output_judder_p95_ms = 0;
    std::string frame_selection;
    // Bounded encoder backpressure: ticks dropped after a bounded wait because
    // the encoder was busy, owned its whole in-flight budget, every planned
    // pool surface was in flight, or every readback CPU frame was still held
    // (readback_pressure_drops). Never counted as GPU conversion fallbacks.
    uint64_t backpressure_drops = 0, encoder_busy_drops = 0, retained_pressure_drops = 0, pool_pressure_drops = 0;
    uint64_t encoder_stall_recoveries = 0;
    double backpressure_drop_fps = 0, backpressure_wait_max_ms = 0;
    double queue_age_ms = 0, processing_ms = 0, submission_ms = 0, completion_ms = 0;
    double texture_readback_ms=0,video_processor_ms=0,software_convert_ms=0,hardware_upload_ms=0,overlay_compose_ms=0;
    uint64_t gpu_conversion_fallbacks=0;
    std::string processing_path,gpu_conversion_fallback_error;
    double submission_p95_ms = 0, completion_p95_ms = 0;
    double queue_age_max_ms=0,processing_max_ms=0,submission_max_ms=0,completion_max_ms=0;
    int surfaces_in_use = 0, surface_capacity = 0;
    // Active EncoderPlan; encoder_planned is false only for an encoder no
    // backend plans.
    bool encoder_planned = false, zero_copy_probe_passed = false;
    std::string encoder_vendor, requested_codec, effective_codec, zero_copy_status;
    int encoder_slots = 0, encoder_delay = 0, output_delay_frames = 0, max_in_flight = 0, pool_capacity = 0;
    int surfaces_allocated = 0, surfaces_in_use_peak = 0;
    uint64_t pool_bytes = 0;
    uint32_t capture_adapter_vendor = 0;
    double submission_p50_ms = 0, completion_p50_ms = 0;
    // Encoded bytes of every emitted packet and the buffer bytes backing them.
    uint64_t packet_payload_bytes = 0, packet_buffer_bytes = 0;
    // System-memory readback for libx264 and the hardware readback forms; zero
    // on zero-copy paths. Staging textures hold GPU frames awaiting readback;
    // a CPU frame is in use while filled, submitted or held by the encoder.
    int readback_staging_slots = 0, readback_staging_in_use = 0, readback_staging_peak = 0;
    int readback_cpu_frames = 0, readback_cpu_frames_in_use = 0, readback_cpu_frames_peak = 0;
    double readback_p50_ms = 0, readback_p95_ms = 0, readback_map_wait_p50_ms = 0, readback_map_wait_p95_ms = 0;
    uint64_t readback_map_stalls = 0, readback_pressure_drops = 0;
    // Large frame allocations by the encoding thread: readback resources when
    // they are created, plus any CPU frame or download a path allocates per frame.
    uint64_t frame_allocations = 0;
    // Auto-clip detector (DetectorStage): samples delivered, due samples
    // skipped by stop, samples that fell back to the full-frame path, and per
    // sample GPU copy, readback and conversion times and bytes read back.
    // Its textures and CPU buffers are allocated only when it builds for a
    // new source, canvas or layout; allocations_after_warmup counts rebuilds.
    uint64_t detector_samples = 0, detector_skipped = 0, detector_fallbacks = 0;
    double detector_sample_fps = 0, detector_bytes_per_sample = 0;
    double detector_gpu_p50_ms = 0, detector_gpu_p95_ms = 0, detector_readback_p50_ms = 0, detector_readback_p95_ms = 0;
    double detector_convert_p50_ms = 0, detector_convert_p95_ms = 0;
    uint64_t detector_textures_allocated = 0, detector_buffers_allocated = 0, detector_builds = 0, detector_allocations_after_warmup = 0;
    uint64_t detector_readback_bytes = 0;
    int overload_windows = 0, qualified_windows = 0;
    bool hardware_input = false, hdr = false, frame_rate_protected = false;
    // Burned overlays: the capture fills the composition path, CPU round trips
    // and GPU figures; the recorder session fills sources and render states.
    OverlayHealth overlay;
    RecordingSourceHealth source_details;
};
struct CaptureCodecParametersDeleter { void operator()(AVCodecParameters* p) const { avcodec_parameters_free(&p); } };
struct CaptureGeneration {
    uint64_t id = 0;
    int64_t start_us = 0;
    std::shared_ptr<const AVCodecParameters> codec;
    AVRational time_base{1, 1000000};
    std::string encoder;
};
struct RecordingCaptureCallbacks {
    std::function<void(std::shared_ptr<const CaptureGeneration>)> generation;
    std::function<void(std::shared_ptr<const CaptureGeneration>, Packet, int64_t acquired_us, bool fresh)> packet;
    std::function<void(CapturePixels)> detector;
    std::function<void(RecordingDetectorSnapshot)> detector_snapshot;
    std::function<void(const std::string&)> failure;
    // Native compositor only; invoked before conversion/encode on owned BGRA.
    std::function<void(CapturePixels&)> compose;
    // CPU overlay composition on system-memory frames (and the zero-copy
    // fallback when the GPU compositor is unavailable).
    std::function<void(AVFrame&)> compose_nv12;
    std::function<bool()> overlay_enabled;
    // The burned layers for the frame at `pts`, read once per frame and
    // composed on the GPU on zero-copy paths, on the CPU otherwise;
    // overlay_composed reports what was drawn. Without overlay_frame the
    // capture falls back to compose_nv12.
    std::function<OverlayFrame(int64_t pts)> overlay_frame;
    std::function<void(int64_t pts, const OverlayFrame& layers, OverlayCompositionResult drawn)> overlay_composed;
};
// Native construction seams only. The DLL never accepts these overrides.
struct RecordingEncoderCandidate { std::string name; bool low_power=false,d3d11=false; };
struct RecordingCaptureDependencies {
    std::vector<RecordingEncoderCandidate> candidates;
    std::function<std::unique_ptr<VideoEncoder>(const VideoEncoderConfig&,size_t candidate)> open_encoder;
    std::function<int64_t()> monotonic_clock;
    std::optional<uint32_t> adapter_vendor; // Replaces the capture device's DXGI VendorId.
    // Replaces capture_create_qsv_frames for QSV zero-copy candidates. Tests
    // on non-Intel machines return D3D11 frames to drive the same path.
    std::function<AVBufferRef*(AVBufferRef* d3d11_device, int width, int height)> qsv_frames;
    // Reports the GPU overlay compositor unavailable, as a driver without
    // BGRA video-processor support would.
    bool disable_gpu_overlays = false;
    // Runs the detector's full-frame path instead of DetectorStage's region
    // readback (benchmarks and tests).
    bool reference_detector = false;
    // Tests: true holds the detector's readback as if the GPU copy were
    // still running.
    std::function<bool()> detector_readback_pending;
};
// Resource sizing used by candidates that do not consume an EncoderPlan yet.
int legacy_surface_capacity(int fps);
// Production candidate order: NVENC, then AMF, then QSV, then libx264. Each
// hardware vendor tries zero-copy before its readback form.
std::vector<RecordingEncoderCandidate> recording_encoder_candidates(bool cpu, bool av1);
// D3D11 texture and array slice behind a D3D11 hardware frame. Individual
// textures report slice 0.
struct CaptureSurfaceTarget {
    ID3D11Texture2D* texture = nullptr;
    unsigned slice = 0;
    bool operator==(const CaptureSurfaceTarget&) const = default;
};
CaptureSurfaceTarget capture_surface_target(const AVFrame& d3d11_frame);
// Every pool surface must be its own NV12 render target covering width x
// height. Throws std::runtime_error at the first surface that is not.
void capture_check_render_targets(const std::vector<CaptureSurfaceTarget>& targets, int width, int height);
// QSV NV12 frames whose D3D11 children live on the given D3D11VA device.
// Throws when no QSV runtime serves that device's adapter. Caller owns the
// returned reference.
AVBufferRef* capture_create_qsv_frames(AVBufferRef* d3d11_device, int width, int height);
// DXGI VendorId of the device's adapter; 0 when it cannot be read.
uint32_t d3d11_adapter_vendor(ID3D11Device* device);
EncoderPolicy recording_encoder_policy(const RecordingCaptureConfig& config);
// Plan for a recording candidate, sized for the configured maximum frame rate.
// nullopt for an encoder no backend plans; throws EncoderPlanInfeasible when
// the candidate cannot run on this adapter or within its limits.
std::optional<EncoderPlan> plan_recording_encoder(const RecordingCaptureConfig& config,
    const RecordingEncoderCandidate& candidate, uint32_t adapter_vendor, bool overlay_stage);

// Pure policy functions are also used by the recording threads and fixtures.
int capture_queue_capacity(int fps);
// Owned WGC frame copies alive at once in steady state, sized from measured
// holders rather than the pacing queue (see the definition).
int capture_source_texture_capacity(int source_queue_depth);
// Continuous backpressure without an accepted frame for this long means the
// encoder is stuck; it is replaced like a failed encoder.
inline constexpr int64_t kRecordingEncoderStallUs = 2000000;
int64_t capture_final_hold(bool variable, int64_t previous_duration, int64_t hold);
CaptureRect capture_aspect_fit(int source_width, int source_height, int width, int height);
bool capture_variable_deadline(int64_t now, int64_t interval, int64_t& scheduled);
// Display ticks between WGC frames for a recording rate (0: refresh unknown),
// and the MinUpdateInterval that asks for it.
int capture_wgc_update_ticks(int fps, double refresh_hz);
// Reopens a capture resource that lost access (Desktop Duplication's
// DXGI_ERROR_ACCESS_LOST on a mode or rotation change): while reopening
// fails, later attempts retry, and the failure is rethrown only once access
// has been lost for longer than `window`. A display change is routine, not a
// source failure for the recorder's recovery budget.
class CaptureReopener {
    std::chrono::milliseconds window_;
    std::chrono::steady_clock::time_point since_{};
    bool pending_ = false;
public:
    explicit CaptureReopener(std::chrono::milliseconds window) : window_(window) {}
    uint64_t reopens = 0, failures = 0;
    void lost(std::chrono::steady_clock::time_point at) { if (!pending_) since_ = at; pending_ = true; }
    bool pending() const { return pending_; }
    void reset() { pending_ = false; }
    // Runs `open`; true once it succeeded. Rethrows its failure after the window.
    bool attempt(const std::function<void()>& open, std::chrono::steady_clock::time_point now);
};
int64_t capture_wgc_interval_100ns(int fps, double refresh_hz);
// The recorder's monotonic timeline in microseconds. A QPC reading maps to
// monotonic_anchor_us plus its distance from qpc_anchor; a QPC-based time in
// microseconds (WGC SystemRelativeTime / 10) maps the same way, within 1 us.
int64_t capture_qpc_to_us(int64_t qpc, int64_t qpc_anchor, int64_t qpc_frequency, int64_t monotonic_anchor_us);
int64_t capture_qpc_us_to_us(int64_t qpc_us, int64_t qpc_anchor, int64_t qpc_frequency, int64_t monotonic_anchor_us);
class RecordingFramePacer {
    int fps_;
    bool variable_;
    double next_constant_ = 0;
    int64_t last_ = -1;
public:
    RecordingFramePacer(int fps,bool variable);
    void set_frame_rate(int fps);
    int64_t next(int64_t elapsed_us,bool source_advanced,int64_t constant_intervals=1);
};
class RecordingRecoveryTimeline {
    struct Outage { int64_t start; std::optional<int64_t> end; };
    std::vector<Outage> outages_;
    int healthy_windows_=0;
public:
    void observe(bool unhealthy,bool paused,int64_t now);
    bool safe_start(int64_t requested_start,int64_t requested_end,int64_t& safe_start)const;
};
bool capture_transport_shortfall(bool captured,bool wgc,double target,double sampled);
struct CaptureFrameStamp { uint64_t sequence = 0; int64_t timestamp_us = 0; };
// Queued frame nearest target_us among frames newer than consumed; ties
// prefer the older frame. nullopt when nothing newer is queued.
std::optional<size_t> capture_select_frame(const std::vector<CaptureFrameStamp>& queued, uint64_t consumed, int64_t target_us);
// Diagnostic overrides: CLYPDAT_FRAME_SELECTION and CLYPDAT_SOURCE_QUEUE_DEPTH.
// Out-of-range values are ignored.
void apply_capture_environment(RecordingCaptureConfig& config);

class RecordingCapture {
public:
    explicit RecordingCapture(RecordingCaptureConfig config, RecordingCaptureCallbacks callbacks,
        std::unique_ptr<RecordingFrameSource> source = {},RecordingCaptureDependencies dependencies={});
    ~RecordingCapture();
    RecordingCapture(const RecordingCapture&) = delete;
    RecordingCapture& operator=(const RecordingCapture&) = delete;
    void start();
    // A timeout keeps the graph alive, including source/encoder/writers; callers
    // must restart the worker. Never destroy resources still used by a thread.
    bool stop(std::chrono::milliseconds timeout = std::chrono::seconds(10));
    void pause(bool paused);
    void request_frame_rate(int fps);
    void set_save_in_progress(bool saving);
    void set_detector_regions(std::vector<CaptureRect> regions, std::vector<CaptureRect> masks,
        int output_width, int output_height);
    void set_detector_regions(const std::array<CaptureNormalizedRect,3>& regions,bool enabled,bool counter_mask);
    RecordingCaptureHealth health() const;
    // The last 64 selected frames' timelines (benchmarks).
    std::vector<CaptureFrameTimeline> recent_timelines() const;
    bool safe_save_start(int64_t requested_start,int64_t requested_end,int64_t& safe_start)const;
private:
    struct State;
    std::shared_ptr<State> state_;
};
std::unique_ptr<RecordingFrameSource> create_windows_recording_source(const RecordingCaptureConfig& config);
void capture_copy_texture_pixels(CapturePixels& pixels);
bool capture_copy_texture_pixels_nonblocking(CapturePixels& pixels, const std::function<bool()>& cancelled);
std::shared_ptr<ID3D11Texture2D> capture_tone_map_texture(ID3D11Texture2D* texture,float sdr_white_nits);
}
