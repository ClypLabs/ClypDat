#pragma once
#include "video_encoder.h"
#include "encoder_backend.h"
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
struct CapturePixels {
    int width = 0, height = 0, stride = 0;
    int64_t timestamp_us = 0;
    std::vector<uint8_t> bgra;
    std::shared_ptr<ID3D11Texture2D> texture;
};
struct RecordingSourceHealth {
    // overwritten counts delivered frames dropped before acquisition consumed them.
    uint64_t callbacks = 0, frames_delivered = 0, overwritten = 0, resizes = 0;
    int64_t requested_interval_100ns = 0, applied_interval_100ns = 0;
    double display_refresh_hz = 0;
    double cursor_composition_ms = 0;
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
    uint64_t duplicates = 0, submitted = 0, source_recoveries = 0;
    uint64_t unique_frames=0;
    // input_fps counts frames the acquisition thread took from the source;
    // the wgc_* rates come from the capture callback itself.
    double input_fps = 0, unique_fps = 0, output_fps = 0;
    double wgc_callback_fps = 0, wgc_delivered_fps = 0, wgc_overwritten_fps = 0;
    double duplicate_fps = 0, replaced_fps = 0, selection_dropped_fps = 0;
    uint64_t selection_dropped = 0;
    int source_queue_depth = 0, source_queue_peak = 0, source_queue_capacity = 0;
    double capture_latency_p50_ms = 0, capture_latency_p95_ms = 0;
    std::string frame_selection;
    // Bounded encoder backpressure: ticks dropped after a bounded wait because
    // the encoder was busy, owned its whole in-flight budget, or every planned
    // pool surface was in flight. Never counted as GPU conversion fallbacks.
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
    // Active EncoderPlan; encoder_planned is false for candidates still on
    // their legacy resource sizing (AMF, QSV, libx264).
    bool encoder_planned = false, zero_copy_probe_passed = false;
    std::string encoder_vendor, requested_codec, effective_codec, zero_copy_status;
    int encoder_slots = 0, encoder_delay = 0, output_delay_frames = 0, max_in_flight = 0, pool_capacity = 0;
    int surfaces_allocated = 0, surfaces_in_use_peak = 0;
    uint64_t pool_bytes = 0;
    uint32_t capture_adapter_vendor = 0;
    double submission_p50_ms = 0, completion_p50_ms = 0;
    int overload_windows = 0, qualified_windows = 0;
    bool hardware_input = false, hdr = false, frame_rate_protected = false;
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
    std::function<void(AVFrame&)> compose_nv12;
    std::function<bool()> overlay_enabled;
};
// Native construction seams only. The DLL never accepts these overrides.
struct RecordingEncoderCandidate { std::string name; bool low_power=false,d3d11=false; };
struct RecordingCaptureDependencies {
    std::vector<RecordingEncoderCandidate> candidates;
    std::function<std::unique_ptr<VideoEncoder>(const VideoEncoderConfig&,size_t candidate)> open_encoder;
    std::function<int64_t()> monotonic_clock;
    std::optional<uint32_t> adapter_vendor; // Replaces the capture device's DXGI VendorId.
};
// Resource sizing used by candidates that do not consume an EncoderPlan yet.
int legacy_surface_capacity(int fps);
// DXGI VendorId of the device's adapter; 0 when it cannot be read.
uint32_t d3d11_adapter_vendor(ID3D11Device* device);
EncoderPolicy recording_encoder_policy(const RecordingCaptureConfig& config);
// Plan for a recording candidate, sized for the configured maximum frame rate.
// nullopt for candidates not planned yet; throws EncoderPlanInfeasible when
// the candidate cannot run on this adapter or within its limits.
std::optional<EncoderPlan> plan_recording_encoder(const RecordingCaptureConfig& config,
    const RecordingEncoderCandidate& candidate, uint32_t adapter_vendor, bool overlay_stage);

// Pure policy functions are also used by the recording threads and fixtures.
int capture_queue_capacity(int fps);
// Continuous backpressure without an accepted frame for this long means the
// encoder is stuck; it is replaced like a failed encoder.
inline constexpr int64_t kRecordingEncoderStallUs = 2000000;
int64_t capture_final_hold(bool variable, int64_t previous_duration, int64_t hold);
CaptureRect capture_aspect_fit(int source_width, int source_height, int width, int height);
bool capture_variable_deadline(int64_t now, int64_t interval, int64_t& scheduled);
int64_t capture_wgc_interval_100ns(int fps, double refresh_hz);
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
