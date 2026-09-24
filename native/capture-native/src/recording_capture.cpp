#include "recording_capture.h"
#include <Windows.h>
#include <avrt.h>
#include <d3d11_4.h>
#include <wrl/client.h>
extern "C" {
#include <libavutil/imgutils.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libswscale/swscale.h>
}
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cctype>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <map>
#include <set>
#include <stdexcept>
#include <thread>

namespace clypdat {
int capture_queue_capacity(int fps) { return std::clamp((std::clamp(fps, 30, 120) + 7) / 8, 4, 15); }
int legacy_surface_capacity(int fps) { return std::clamp((fps + 1) / 2, 16, 60) + capture_queue_capacity(fps) + 5; }
std::vector<RecordingEncoderCandidate> recording_encoder_candidates(bool cpu, bool av1) {
    if (cpu) return {{"libx264"}};
    std::vector<RecordingEncoderCandidate> result;
    // Cross-codec NVENC precedence is intentional and matches production.
    if (av1) result.push_back({"av1_nvenc", false, true});
    result.push_back({"h264_nvenc", false, true});
    if (av1) result.push_back({"av1_nvenc"});
    result.push_back({"h264_nvenc"});
    // AMF zero-copy is planned only on an AMD capture adapter; elsewhere the
    // plan is infeasible and the readback AMF candidate follows.
    if (av1) {
        result.push_back({"av1_amf", false, true}); result.push_back({"av1_amf"});
        result.push_back({"av1_qsv", true}); result.push_back({"av1_qsv"});
    }
    result.push_back({"h264_amf", false, true}); result.push_back({"h264_amf"});
    result.push_back({"h264_qsv", true}); result.push_back({"h264_qsv"});
    result.push_back({"libx264"}); return result;
}
uint32_t d3d11_adapter_vendor(ID3D11Device* device) {
    if (!device) return 0;
    Microsoft::WRL::ComPtr<IDXGIDevice> dxgi; Microsoft::WRL::ComPtr<IDXGIAdapter> adapter; DXGI_ADAPTER_DESC desc{};
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgi))) || FAILED(dxgi->GetAdapter(&adapter)) || FAILED(adapter->GetDesc(&desc))) return 0;
    return desc.VendorId;
}
EncoderPolicy recording_encoder_policy(const RecordingCaptureConfig& config) {
    // The configured diagnostic delay wins; the environment is the fallback.
    // Both feed the plan, which owns the one delay the encoder receives.
    EncoderPolicy policy;
    int delay = config.nvenc_delay == 4 || config.nvenc_delay == 8 ? config.nvenc_delay : 0;
    char* value = nullptr; size_t length = 0;
    if (!delay && _dupenv_s(&value, &length, "CLYPDAT_NVENC_DELAY") == 0 && value) {
        const std::unique_ptr<char, decltype(&std::free)> owned(value, &std::free);
        const std::string text(value); delay = text == "4" ? 4 : text == "8" ? 8 : 0;
    }
    policy.nvenc_delay_override = delay;
    return policy;
}
std::optional<EncoderPlan> plan_recording_encoder(const RecordingCaptureConfig& config,
    const RecordingEncoderCandidate& candidate, uint32_t adapter_vendor, bool overlay_stage) {
    // QSV and libx264 keep their legacy resource sizing for now.
    const bool nvenc = candidate.name.ends_with("_nvenc"), amf = candidate.name.ends_with("_amf");
    if (!nvenc && !amf) return std::nullopt;
    EncoderRequest request;
    request.codec = candidate.name.starts_with("av1") ? EncoderCodec::AV1 : EncoderCodec::H264;
    request.pixel_format = EncoderPixelFormat::NV12;
    request.allow_codec_fallback = false;
    request.width = config.width; request.height = config.height;
    // Size for the configured ceiling; adaptive reductions fit, and a later
    // return to the configured rate cannot exceed the pool.
    request.fps = config.fps;
    request.bitrate_mbps = std::clamp(config.bitrate_mbps, 5, 100);
    request.adapter_vendor = adapter_vendor;
    request.overlay_stage = overlay_stage;
    request.capture_buffers = 3; request.pacing_queue = capture_queue_capacity(config.fps);
    return encoder_backend(nvenc ? EncoderVendor::Nvidia : EncoderVendor::Amd).plan(request, candidate.d3d11, recording_encoder_policy(config));
}
int64_t capture_final_hold(bool variable, int64_t previous, int64_t hold) {
    const auto cadence = std::max<int64_t>(1, previous);
    return variable ? std::clamp<int64_t>(hold, 1, cadence * 2) : cadence;
}
CaptureRect capture_aspect_fit(int sw, int sh, int w, int h) {
    if (sw <= 0 || sh <= 0 || w < 2 || h < 2) throw std::invalid_argument("Invalid recording canvas");
    w &= ~1; h &= ~1;
    const double scale = std::min(double(w) / sw, double(h) / sh);
    const int fw = std::clamp(int(sw * scale) & ~1, 2, w & ~1);
    const int fh = std::clamp(int(sh * scale) & ~1, 2, h & ~1);
    return { ((w - fw) / 2) & ~1, ((h - fh) / 2) & ~1, fw, fh };
}
bool capture_variable_deadline(int64_t now, int64_t interval, int64_t& scheduled) {
    if (interval <= 0 || now - scheduled + 750 < interval) return false;
    scheduled += std::max<int64_t>(1, (now - scheduled + 750) / interval) * interval;
    return true;
}
int64_t capture_wgc_interval_100ns(int fps, double refresh_hz) {
    const auto target = std::clamp(fps,30,120);
    if (!std::isfinite(refresh_hz) || refresh_hz<=0) return int64_t(std::llround(10000000.0/target/2));
    const int periods = std::max(1,int(std::floor(refresh_hz/(target*1.5)+.000001)));
    return int64_t(std::llround(10000000.0/refresh_hz*(periods-.5)));
}
RecordingFramePacer::RecordingFramePacer(int fps,bool variable):fps_(std::clamp(fps,30,120)),variable_(variable){}
void RecordingFramePacer::set_frame_rate(int fps){fps_=std::clamp(fps,30,120);}
int64_t RecordingFramePacer::next(int64_t elapsed,bool advanced,int64_t intervals){
    const double interval=1000000.0/fps_;int64_t pts;
    intervals=std::max<int64_t>(1,intervals);
    if(variable_)pts=advanced?elapsed:(last_<0?elapsed:last_+int64_t(std::nearbyint(interval)));
    else{pts=int64_t(std::nearbyint(next_constant_+interval*(intervals-1)));next_constant_+=interval*intervals;}
    last_=std::max(last_+1,pts);return last_;
}
bool capture_transport_shortfall(bool captured,bool wgc,double target,double sampled){return captured&&!wgc&&target>0&&sampled<target*.99;}
std::optional<size_t> capture_select_frame(const std::vector<CaptureFrameStamp>& queued, uint64_t consumed, int64_t target_us) {
    std::optional<size_t> best;
    uint64_t best_distance = 0;
    for (size_t i = 0; i < queued.size(); ++i) {
        if (queued[i].sequence <= consumed) continue;
        const auto distance = uint64_t(std::llabs(queued[i].timestamp_us - target_us));
        if (!best || distance < best_distance) { best = i; best_distance = distance; }
    }
    return best;
}
void apply_capture_environment(RecordingCaptureConfig& config) {
    auto read = [](const char* name) {
        char* value = nullptr; size_t length = 0;
        if (_dupenv_s(&value, &length, name) != 0 || !value) return std::string{};
        const std::unique_ptr<char, decltype(&std::free)> owned(value, &std::free);
        return std::string(value);
    };
    const auto selection = read("CLYPDAT_FRAME_SELECTION");
    if (selection == "timestamp" || selection == "newest") config.frame_selection = selection;
    auto depth = [&](const char* name, int& target) {
        const auto text = read(name);
        if (text.size() == 1 && text[0] >= '1' && text[0] <= '8') target = text[0] - '0';
    };
    depth("CLYPDAT_SOURCE_QUEUE_DEPTH", config.source_queue_depth);
}
void RecordingRecoveryTimeline::observe(bool unhealthy,bool paused,int64_t now){
    if(paused){healthy_windows_=0;return;}
    if(unhealthy){healthy_windows_=0;if(outages_.empty()||outages_.back().end)outages_.push_back({now,{}});return;}
    if(outages_.empty()||outages_.back().end)return;
    if(++healthy_windows_>=2)outages_.back().end=now;
}
bool RecordingRecoveryTimeline::safe_start(int64_t begin,int64_t end,int64_t& result)const{
    result=begin;
    for(const auto& outage:outages_){
        if(outage.start>=end||(outage.end&&*outage.end<=begin))continue;
        if(!outage.end||*outage.end>=end)return false;
        result=std::max(result,*outage.end);
    }
    return result<end;
}
namespace {
struct FrameDeleter { void operator()(AVFrame* p) const { av_frame_free(&p); } };
class CaptureThreadScheduling{
    int previous_=THREAD_PRIORITY_NORMAL;HANDLE mmcss_=nullptr;
public:
    explicit CaptureThreadScheduling(bool enabled){
        previous_=GetThreadPriority(GetCurrentThread());
        if(enabled){SetThreadPriority(GetCurrentThread(),THREAD_PRIORITY_ABOVE_NORMAL);DWORD index=0;
            mmcss_=AvSetMmThreadCharacteristicsW(L"Capture",&index);if(mmcss_)AvSetMmThreadPriority(mmcss_,AVRT_PRIORITY_CRITICAL);}
    }
    ~CaptureThreadScheduling(){if(mmcss_)AvRevertMmThreadCharacteristics(mmcss_);if(previous_!=THREAD_PRIORITY_ERROR_RETURN)SetThreadPriority(GetCurrentThread(),previous_);}
};
using Frame = std::unique_ptr<AVFrame, FrameDeleter>;
void check(int result, const char* what) {
    if (result >= 0) return;
    char text[AV_ERROR_MAX_STRING_SIZE]{}; av_strerror(result, text, sizeof(text));
    throw std::runtime_error(std::string(what) + ": " + text);
}
using Candidate=RecordingEncoderCandidate;
bool valid_pixels(const CapturePixels& p) {
    return p.width > 0 && p.height > 0 && p.width <= 16384 && p.height <= 16384 &&
        p.stride >= int64_t(p.width) * 4 && (p.texture || uint64_t(p.stride) * p.height <= p.bgra.size());
}
bool contains(const CaptureRect& r, int x, int y) {
    return x >= r.x && y >= r.y && int64_t(x) < int64_t(r.x) + r.width && int64_t(y) < int64_t(r.y) + r.height;
}
struct BufferDeleter { void operator()(AVBufferRef* p) const { av_buffer_unref(&p); } };
// Every planned surface is in flight. This is encoder backpressure, not a
// GPU conversion failure, so it never triggers the CPU conversion fallback.
struct SurfacePoolExhausted : std::runtime_error { using std::runtime_error::runtime_error; };
using Buffer = std::unique_ptr<AVBufferRef, BufferDeleter>;
class GpuProcessor {
    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11VideoDevice> video_;
    Microsoft::WRL::ComPtr<ID3D11VideoContext> context_;
    Microsoft::WRL::ComPtr<ID3D11Multithread> multithread_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> immediate_;
    Microsoft::WRL::ComPtr<ID3D11VideoProcessorEnumerator> enumerator_;
    Microsoft::WRL::ComPtr<ID3D11VideoProcessor> processor_;
    int source_width_ = 0, source_height_ = 0;
    int width_, height_, fps_, capacity_;
    // Distinct pool surfaces handed out. A dynamic pool grows only when every
    // surface is outstanding, so a new surface past capacity is refused.
    std::set<std::pair<void*, intptr_t>> surfaces_;
    void acquire(AVFrame* frame, const char* what) {
        check(av_hwframe_get_buffer(frames.get(), frame, 0), what);
        const std::pair<void*, intptr_t> key{frame->data[0], reinterpret_cast<intptr_t>(frame->data[1])};
        if (surfaces_.contains(key)) return;
        if (int(surfaces_.size()) >= capacity_) {
            av_frame_unref(frame);
            throw SurfacePoolExhausted("Recording hardware surface pool exceeded its planned capacity of " + std::to_string(capacity_));
        }
        surfaces_.insert(key);
    }
public:
    Buffer device, frames;
    int capacity() const { return capacity_; }
    int allocated() const { return int(surfaces_.size()); }
    GpuProcessor(ID3D11Device* input, int width, int height, int fps, int capacity) : device_(input), width_(width), height_(height), fps_(fps), capacity_(capacity) {
        if (capacity < 1) throw std::invalid_argument("Recording surface pool capacity must be positive");
        if (!input) throw std::runtime_error("D3D11 recording source unavailable");
        if (FAILED(input->QueryInterface(IID_PPV_ARGS(&video_)))) throw std::runtime_error("D3D11 video processing unavailable");
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> immediate; input->GetImmediateContext(&immediate);
        if (FAILED(immediate.As(&context_))) throw std::runtime_error("D3D11 video context unavailable");
        immediate.As(&multithread_); immediate_ = immediate;
        device.reset(av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA));
        if (!device) throw std::bad_alloc();
        auto* hw = reinterpret_cast<AVHWDeviceContext*>(device->data);
        auto* d3d = static_cast<AVD3D11VADeviceContext*>(hw->hwctx);
        d3d->device = input; input->AddRef();
        check(av_hwdevice_ctx_init(device.get()), "Initialize recording hardware device");
        // Dynamic individual textures first, fixed array pool only when dynamic
        // allocation is unavailable. Prewarming the bounded working set
        // qualifies the format and bind flags before the encoder opens.
        for (const int pool_size : {0, capacity}) {
            frames.reset(av_hwframe_ctx_alloc(device.get())); if (!frames) throw std::bad_alloc();
            auto* fc = reinterpret_cast<AVHWFramesContext*>(frames->data);
            fc->format = AV_PIX_FMT_D3D11; fc->sw_format = AV_PIX_FMT_NV12;
            fc->width = width; fc->height = height; fc->initial_pool_size = pool_size;
            auto* df = static_cast<AVD3D11VAFramesContext*>(fc->hwctx); df->BindFlags = D3D11_BIND_RENDER_TARGET;
            if (av_hwframe_ctx_init(frames.get()) < 0) continue;
            std::vector<Frame> probes;bool usable=true;surfaces_.clear();
            for(int i=0;i<capacity;++i){Frame probe(av_frame_alloc());if(!probe)throw std::bad_alloc();
                if(av_hwframe_get_buffer(frames.get(),probe.get(),0)<0){usable=false;break;}
                surfaces_.insert({probe->data[0],reinterpret_cast<intptr_t>(probe->data[1])});probes.push_back(std::move(probe));}
            if(usable)return;
        }
        throw std::runtime_error("Recording D3D11 surface pools unavailable");
    }
    Frame convert(const CapturePixels& pixels, int64_t pts) {
        if (!pixels.texture) throw std::runtime_error("Recording frame has no GPU texture");
        struct Lock { ID3D11Multithread* p; Lock(ID3D11Multithread* v):p(v){if(p)p->Enter();} ~Lock(){if(p)p->Leave();} } lock(multithread_.Get());
        auto hr = [](HRESULT value, const char* text) { if (FAILED(value)) { char code[16]{};std::snprintf(code,sizeof(code),"0x%08X",unsigned(value));throw std::runtime_error(std::string(text)+" (hr="+code+")"); } };
        if (!processor_ || pixels.width != source_width_ || pixels.height != source_height_) {
            enumerator_.Reset(); processor_.Reset();
            D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{}; desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
            desc.InputWidth = pixels.width; desc.InputHeight = pixels.height;
            desc.OutputWidth = width_; desc.OutputHeight = height_;
            desc.InputFrameRate = {UINT(fps_), 1}; desc.OutputFrameRate = {UINT(fps_), 1};
            desc.Usage = D3D11_VIDEO_USAGE_OPTIMAL_SPEED;
            if(FAILED(video_->CreateVideoProcessorEnumerator(&desc,&enumerator_))){desc.Usage=D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
                hr(video_->CreateVideoProcessorEnumerator(&desc, &enumerator_), "Create recording video processor enumerator");}
            hr(video_->CreateVideoProcessor(enumerator_.Get(), 0, &processor_), "Create recording video processor");
            source_width_ = pixels.width; source_height_ = pixels.height;
        }
        Frame frame(av_frame_alloc()); if (!frame) throw std::bad_alloc();
        acquire(frame.get(), "Allocate recording hardware surface");
        Microsoft::WRL::ComPtr<ID3D11VideoProcessorInputView> input;
        Microsoft::WRL::ComPtr<ID3D11VideoProcessorOutputView> output;
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC id{}; id.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        const auto input_view_result=video_->CreateVideoProcessorInputView(pixels.texture.get(), enumerator_.Get(), &id, &input);
        if(FAILED(input_view_result)){
            D3D11_TEXTURE2D_DESC source_desc{};pixels.texture->GetDesc(&source_desc);UINT format_support=0;
            const auto support_result=enumerator_->CheckVideoProcessorFormat(source_desc.Format,&format_support);
            char error[192]{};std::snprintf(error,sizeof(error),"Create recording video input view (hr=0x%08X, format=0x%X support=0x%X supportHr=0x%08X source=%ux%u bind=0x%X)",
                unsigned(input_view_result),unsigned(source_desc.Format),unsigned(format_support),unsigned(support_result),source_desc.Width,source_desc.Height,source_desc.BindFlags);
            throw std::runtime_error(error);
        }
        auto* texture = reinterpret_cast<ID3D11Texture2D*>(frame->data[0]); D3D11_TEXTURE2D_DESC td{}; texture->GetDesc(&td);
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC od{};
        if (td.ArraySize > 1) { od.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2DARRAY;
            od.Texture2DArray.FirstArraySlice = UINT(reinterpret_cast<uintptr_t>(frame->data[1])); od.Texture2DArray.ArraySize = 1; }
        else od.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        hr(video_->CreateVideoProcessorOutputView(texture, enumerator_.Get(), &od, &output), "Create recording video output view");
        const auto fit = capture_aspect_fit(pixels.width, pixels.height, width_, height_);
        RECT source{0, 0, pixels.width, pixels.height}, destination{fit.x, fit.y, fit.x + fit.width, fit.y + fit.height}, canvas{0,0,width_,height_};
        context_->VideoProcessorSetStreamSourceRect(processor_.Get(), 0, TRUE, &source);
        context_->VideoProcessorSetStreamDestRect(processor_.Get(), 0, TRUE, &destination);
        context_->VideoProcessorSetOutputTargetRect(processor_.Get(), TRUE, &canvas);
        context_->VideoProcessorSetStreamAutoProcessingMode(processor_.Get(), 0, FALSE);
        context_->VideoProcessorSetStreamFrameFormat(processor_.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE input_space{}, output_space{};
        input_space.YCbCr_Matrix = 1; input_space.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_0_255;
        output_space.YCbCr_Matrix = 1; output_space.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
        context_->VideoProcessorSetStreamColorSpace(processor_.Get(), 0, &input_space);
        context_->VideoProcessorSetOutputColorSpace(processor_.Get(), &output_space);
        D3D11_VIDEO_COLOR background{}; background.YCbCr = {16.f / 255.f, 128.f / 255.f, 128.f / 255.f, 1};
        context_->VideoProcessorSetOutputBackgroundColor(processor_.Get(), TRUE, &background);
        D3D11_VIDEO_PROCESSOR_STREAM stream{}; stream.Enable = TRUE; stream.pInputSurface = input.Get();
        hr(context_->VideoProcessorBlt(processor_.Get(), output.Get(), 0, 1, &stream), "Process recording GPU frame");
        // Submit the conversion now. NVENC waits for this surface in its
        // blocking bitstream lock; left batched, the write can sit behind a
        // capture-thread call that needs the device lock NVENC holds.
        immediate_->Flush();
        frame->pts = pts; frame->duration = 1000000 / fps_;
        frame->color_range = AVCOL_RANGE_MPEG; frame->colorspace = AVCOL_SPC_BT709;
        frame->color_primaries = AVCOL_PRI_BT709; frame->color_trc = AVCOL_TRC_BT709;
        return frame;
    }
    Frame upload(const AVFrame& software) {
        Frame frame(av_frame_alloc()); if (!frame) throw std::bad_alloc();
        acquire(frame.get(), "Allocate recording upload surface");
        check(av_hwframe_transfer_data(frame.get(), &software, 0), "Upload recording processed frame");
        check(av_frame_copy_props(frame.get(), &software), "Copy recording frame metadata"); return frame;
    }
};
}

struct RecordingCapture::State : std::enable_shared_from_this<RecordingCapture::State> {
    RecordingCaptureConfig config;
    RecordingCaptureCallbacks callbacks;
    RecordingCaptureDependencies dependencies;
    std::unique_ptr<RecordingFrameSource> source;
    bool injected_source = false;
    mutable std::mutex mutex;
    std::mutex source_mutex;
    std::condition_variable changed;
    RecordingCaptureHealth status;
    std::shared_ptr<CapturePixels> latest;
    uint64_t sequence = 0;
    // Recent acquisitions for timestamp selection, oldest first. Bounded by
    // config.source_queue_depth; frames at or below consumed are spent.
    struct Acquired { std::shared_ptr<CapturePixels> pixels; uint64_t sequence; };
    std::deque<Acquired> recent;
    uint64_t consumed = 0;
    // acquired_us is the source time the tick samples; scheduled_us is when
    // pacing issued it, used for queue age.
    struct Work { std::shared_ptr<CapturePixels> pixels; int64_t pts; bool fresh;int64_t acquired_us;uint64_t source_sequence;int64_t scheduled_us; };
    std::deque<Work> queue;
    std::atomic<bool> stopping{false}, user_paused{false};
    std::atomic<int> fps{60};
    std::atomic<bool> switch_requested{false};
    std::atomic<bool> saving{false};
    bool pacing_finished = false;
    int threads = 0;
    int64_t last_pts = -1, last_detector = INT64_MIN / 2;
    std::unique_ptr<VideoEncoder> encoder;
    std::unique_ptr<GpuProcessor> gpu;
    std::shared_ptr<CaptureGeneration> generation;
    size_t active_candidate = 0;
    std::vector<Candidate> encoder_candidates;
    std::vector<bool> failed_candidates;
    std::map<int64_t, std::pair<int64_t, bool>> submitted;
    std::map<int64_t, int64_t> submitted_at;
    std::map<int64_t, Frame> retained_surfaces;
    size_t retained_limit = 0; // Frames the active encoder may own at once.
    int64_t pressure_since = 0; // First backpressure drop since the last accepted frame.
    std::deque<double> submission_times, completion_times, capture_latencies;
    double readback_ms_sum=0,video_processor_ms_sum=0,software_convert_ms_sum=0,hardware_upload_ms_sum=0,overlay_compose_ms_sum=0;
    uint64_t processing_stage_samples=0;
    int64_t health_window = 0;
    int64_t session_started = 0, last_tuning_decision = 0, clean_since = 0;
    std::deque<bool> severe_windows;
    uint64_t previous_acquired = 0, previous_encoded = 0, previous_dropped = 0;
    uint64_t previous_recoveries=0;
    uint64_t previous_unique=0,previous_source_delivered=0;
    uint64_t previous_duplicates=0,previous_replaced=0,previous_selection_dropped=0,previous_backpressure_drops=0;
    uint64_t previous_wgc_callbacks=0,previous_wgc_delivered=0,previous_wgc_overwritten=0;
    int transport_shortfall_windows=0;
    RecordingRecoveryTimeline recovery;
    SwsContext* scaler = nullptr;
    SwsContext* detector_scaler = nullptr;
    std::string gpu_initialization_error;

    State(RecordingCaptureConfig c, RecordingCaptureCallbacks cb, std::unique_ptr<RecordingFrameSource> s,RecordingCaptureDependencies deps={})
        : config(std::move(c)), callbacks(std::move(cb)),dependencies(std::move(deps)), source(std::move(s)) {
        injected_source = bool(source);
        std::transform(config.pacing_policy.begin(),config.pacing_policy.end(),config.pacing_policy.begin(),[](unsigned char c){return char(std::tolower(c));});
        apply_capture_environment(config);
        config.source_queue_depth=std::clamp(config.source_queue_depth,1,8);
        if(config.frame_selection!="newest")config.frame_selection="timestamp";
        status.frame_selection=config.frame_selection;status.source_queue_capacity=config.frame_selection=="newest"?1:config.source_queue_depth;
        if (config.width <= 0 || config.height <= 0 || config.width > 16384 || config.height > 16384 ||
            (config.width & 1) || (config.height & 1) || config.fps < 30 || config.fps > 120)
            throw std::invalid_argument("Invalid recording capture configuration");
        LARGE_INTEGER frequency{}, counter{}; QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&counter);
        if (!config.qpc_frequency) config.qpc_frequency = frequency.QuadPart;
        if (!config.qpc_anchor) config.qpc_anchor = counter.QuadPart;
        if (config.qpc_frequency <= 0) throw std::invalid_argument("Invalid recording clock frequency");
        fps = config.fps; status.active_fps = config.fps; status.queue_capacity = capture_queue_capacity(config.fps);
        encoder_candidates = dependencies.candidates.empty()?recording_encoder_candidates(config.cpu_encoder, config.av1):dependencies.candidates;
        failed_candidates.resize(encoder_candidates.size());
    }
    ~State() { sws_freeContext(scaler);sws_freeContext(detector_scaler); }
    int64_t now() const {
        if(dependencies.monotonic_clock)return dependencies.monotonic_clock();
        LARGE_INTEGER ticks{}; QueryPerformanceCounter(&ticks);
        const int64_t delta = ticks.QuadPart - config.qpc_anchor;
        return config.monotonic_anchor_us + delta / config.qpc_frequency * 1000000 +
            delta % config.qpc_frequency * 1000000 / config.qpc_frequency;
    }
    void fail(const std::string& message) noexcept {
        { std::lock_guard lock(mutex); status.error = message; status.restart_required = true; }
        stopping = true; changed.notify_all();
        try { if (callbacks.failure) callbacks.failure(message); } catch (...) {}
    }
    template<class F> void launch(F action) {
        ++threads;
        try {
            std::thread([self = shared_from_this(), action] {
                {
                CaptureThreadScheduling scheduling(action!=&State::detection);
                try { (self.get()->*action)(); }
                catch (const std::exception& e) { self->fail(e.what()); }
                catch (...) { self->fail("Unhandled native recording failure"); }
                }
                { std::lock_guard lock(self->mutex); --self->threads; }
                self->changed.notify_all();
            }).detach();
        } catch (...) { --threads; throw; }
    }
    void emit(std::vector<Packet> packets) {
        for (auto& packet : packets) {
            const auto mapping = submitted.find(packet->pts);
            if (mapping == submitted.end()) throw std::runtime_error("Encoder output has no source timestamp mapping");
            const auto [acquired, fresh] = mapping->second;
            const auto completed_at = now();
            const auto issued = submitted_at.find(packet->pts);
            const double latency = issued == submitted_at.end() ? 0 : double(completed_at - issued->second) / 1000;
            if (issued != submitted_at.end()) submitted_at.erase(issued);
            submitted.erase(mapping);
            retained_surfaces.erase(packet->pts);
            if (callbacks.packet) callbacks.packet(generation, std::move(packet), acquired, fresh);
            std::lock_guard lock(mutex); ++status.encoded;
            if(fresh)++status.unique_frames;
            status.completion_ms = latency; completion_times.push_back(latency); if (completion_times.size() > 240) completion_times.pop_front();
            status.completion_max_ms=std::max(status.completion_max_ms,latency);
            status.surfaces_in_use = int(submitted.size());
        }
    }
    void open_encoder(bool recovering) {
        const auto old_codec = encoder ? encoder->context().codec_id : AV_CODEC_ID_NONE;
        const bool old_hardware = encoder && active_candidate < encoder_candidates.size() &&
            encoder_candidates[active_candidate].name != "libx264";
        const bool old_d3d11 = encoder && encoder_candidates[active_candidate].d3d11;
        const uint32_t adapter_vendor = dependencies.adapter_vendor ? *dependencies.adapter_vendor : d3d11_adapter_vendor(source->d3d_device());
        const bool overlay_stage = callbacks.compose_nv12 && (!callbacks.overlay_enabled || callbacks.overlay_enabled());
        std::string failures;
        for (size_t i = recovering ? active_candidate + 1 : 0; i < encoder_candidates.size(); ++i) {
            if (failed_candidates[i]) continue;
            const auto& candidate = encoder_candidates[i];
            const auto codec = candidate.name.starts_with("av1") ? AV_CODEC_ID_AV1 : AV_CODEC_ID_H264;
            if (recovering && (codec != old_codec || candidate.d3d11 != old_d3d11 || (old_hardware && candidate.name == "libx264"))) continue;
            try {
                // An infeasible plan, such as zero-copy on a non-NVIDIA capture
                // adapter, only skips this candidate.
                const auto plan = plan_recording_encoder(config, candidate, adapter_vendor, overlay_stage);
                const int capacity = plan ? plan->pool_capacity : legacy_surface_capacity(config.fps);
                VideoEncoderConfig ec; ec.width = config.width; ec.height = config.height; ec.fps = fps;
                ec.bitrate_mbps = config.bitrate_mbps; ec.name = candidate.name; ec.low_power = candidate.low_power;
                if (plan) {
                    ec.resource_options = plan->options;
                    if (plan->low_delay_flag) ec.codec_flags |= AV_CODEC_FLAG_LOW_DELAY;
                    ec.require_encoder_frames = plan->frames_from_encoder_ctx;
                }
                if (candidate.d3d11) {
                    // A recovering encoder may still own surfaces of the current pool.
                    if (gpu && !recovering && gpu->capacity() != capacity) gpu.reset();
                    if (!gpu) gpu = std::make_unique<GpuProcessor>(source->d3d_device(), config.width, config.height, fps, capacity);
                    ec.hardware_frames = gpu->frames.get();
                } else if (!recovering) ensure_conversion(capacity);
                auto replacement = dependencies.open_encoder?dependencies.open_encoder(ec,i):std::make_unique<VideoEncoder>(ec);
                if(!replacement)throw std::runtime_error("Recording encoder factory returned no encoder");
                if (encoder) { try { emit(encoder->finish()); } catch (...) {} }
                encoder = std::move(replacement); active_candidate = i;
                // Any unreturned frames belonged to a failed outgoing context.
                // Destroy that context before dropping their retained surfaces.
                submitted.clear();submitted_at.clear();retained_surfaces.clear();
                auto next = std::make_shared<CaptureGeneration>();
                next->id = generation ? generation->id + 1 : 1; next->start_us = now(); next->encoder = candidate.name;
                AVCodecParameters* parameters = avcodec_parameters_alloc();
                if (!parameters) throw std::bad_alloc();
                next->codec = std::shared_ptr<const AVCodecParameters>(parameters, CaptureCodecParametersDeleter{});
                check(avcodec_parameters_from_context(parameters, &encoder->context()), "Copy recording codec parameters");
                generation = std::move(next);
                retained_limit = size_t(plan ? plan->max_in_flight : legacy_surface_capacity(config.fps));
                { std::lock_guard lock(mutex); status.generation = generation->id; status.encoder = candidate.name;
                  status.hardware_input = candidate.d3d11; status.surface_capacity = candidate.d3d11 ? (gpu ? gpu->capacity() : capacity) : 0;
                  status.hdr = status.source_details.hdr_display;
                  status.capture_adapter_vendor = adapter_vendor; status.encoder_planned = bool(plan);
                  status.pool_capacity = plan ? plan->pool_capacity : 0;
                  status.surfaces_allocated = gpu ? gpu->allocated() : 0; status.surfaces_in_use = 0; status.surfaces_in_use_peak = 0;
                  status.encoder_vendor.clear(); status.requested_codec.clear(); status.effective_codec.clear(); status.zero_copy_status.clear();
                  status.zero_copy_probe_passed = false; status.encoder_slots = status.encoder_delay = status.output_delay_frames = status.max_in_flight = 0;
                  status.pool_bytes = 0;
                  if (plan) {
                      status.encoder_vendor = encoder_vendor_name(plan->vendor);
                      status.requested_codec = encoder_codec_name(plan->requested_codec); status.effective_codec = encoder_codec_name(plan->effective_codec);
                      status.zero_copy_status = zero_copy_status_name(plan->zero_copy_status);
                      // The opened encoder is the probe for an unverified adapter;
                      // the plan itself stays unverified.
                      status.zero_copy_probe_passed = plan->zero_copy_status == ZeroCopyStatus::Unverified;
                      status.encoder_slots = plan->encoder_slots; status.output_delay_frames = plan->output_delay_frames;
                      for (const auto& [key, value] : plan->options) if (key == "delay") status.encoder_delay = std::stoi(value);
                      status.max_in_flight = plan->max_in_flight; status.pool_bytes = plan->pool_bytes;
                  } }
                if (callbacks.generation) callbacks.generation(generation);
                return;
            } catch (const std::exception& e) { failed_candidates[i] = true; failures += candidate.name + ": " + e.what() + "\n"; }
        }
        throw std::runtime_error("No compatible recording encoder available: " + failures);
    }
    // Readback candidates keep a GPU converter when the source has a device;
    // without one they use CPU conversion, as before.
    void ensure_conversion(int capacity) {
        auto* device = source->d3d_device();
        if (!device || (gpu && gpu->capacity() == capacity)) return;
        gpu.reset();
        try { gpu = std::make_unique<GpuProcessor>(device, config.width, config.height, fps, capacity); gpu_initialization_error.clear(); }
        catch (const std::exception& error) { gpu_initialization_error = error.what(); std::lock_guard lock(mutex); ++status.gpu_conversion_fallbacks; status.gpu_conversion_fallback_error = gpu_initialization_error; }
        catch (...) { gpu_initialization_error = "Unknown GPU processor initialization error"; std::lock_guard lock(mutex); ++status.gpu_conversion_fallbacks; status.gpu_conversion_fallback_error = gpu_initialization_error; }
    }
    void acquisition() {
        struct StopSource { RecordingFrameSource* source; ~StopSource(){try{source->stop();}catch(...){}} } source_owner{source.get()};
        int applied_fps = 0;
        int64_t refresh_checked = 0;
        int64_t last_recovery = 0;
        bool backend_fallback_attempted = false;
        while (!stopping) {
            const int requested = fps;
            if (applied_fps != requested || now()-refresh_checked>=1000000) {
                source->set_frame_rate(requested); applied_fps = requested; refresh_checked=now();
                auto details=source->diagnostics();std::lock_guard lock(mutex);status.hdr=details.hdr_display;status.source_details=std::move(details);
            }
            observe_health();
            if (user_paused || !source->eligible()) {
                { std::lock_guard lock(mutex); latest.reset(); recent.clear(); status.source_queue_depth = 0; status.paused = true; }
                std::unique_lock lock(mutex); changed.wait_for(lock, std::chrono::milliseconds(20), [&] { return stopping.load(); });
                continue;
            }
            CapturePixels pixels;
            try {
                if (!source->acquire(pixels, std::chrono::milliseconds(20))) continue;
            }catch(const std::exception&){
                const auto current=now();
                // A fresh source recreation gets one recovery opportunity. A
                // repeated failure inside the original 30s window cannot loop
                // indefinitely while saves appear healthy.
                if((!last_recovery||current-last_recovery>30000000)&&source->recover()){
                    last_recovery=current;std::lock_guard lock(mutex);++status.source_recoveries;latest.reset();recent.clear();
                    status.source=source->name();status.source_details=source->diagnostics();
                    previous_source_delivered=0;recovery.observe(true,false,current);continue;
                }
                if(!backend_fallback_attempted){
                    const bool switch_to_wgc=std::string(source->name())!="Windows Graphics Capture";
                    if(source->switch_backend(switch_to_wgc)){
                        backend_fallback_attempted=true;last_recovery=current;
                        std::lock_guard lock(mutex);++status.source_recoveries;latest.reset();recent.clear();
                        status.source=source->name();status.source_details=source->diagnostics();
                        previous_source_delivered=0;recovery.observe(true,false,current);continue;
                    }
                }
                throw;
            }
            // DXGI eligibility must be checked after acquisition, before pixels
            // reach processing; foreground can change while AcquireNextFrame waits.
            if (user_paused || !source->eligible()) continue;
            if (!valid_pixels(pixels)) throw std::runtime_error("Capture source returned invalid frame storage");
            if (!pixels.timestamp_us) pixels.timestamp_us = now();
            { std::lock_guard lock(mutex); latest = std::make_shared<CapturePixels>(std::move(pixels));
              ++sequence; ++status.acquired; status.paused = false;
              if (config.frame_selection != "newest") {
                  recent.push_back({latest, sequence});
                  // Overflow drops the oldest; it counts only if never output.
                  while (recent.size() > size_t(config.source_queue_depth)) {
                      if (recent.front().sequence > consumed) ++status.selection_dropped;
                      recent.pop_front();
                  }
                  status.source_queue_depth = int(recent.size());
                  status.source_queue_peak = std::max(status.source_queue_peak, status.source_queue_depth);
              } }
            changed.notify_all();
        }
    }
    void observe_health() {
        const auto current = now();
        std::lock_guard lock(mutex);
        if (!health_window) { health_window = current; return; }
        const auto elapsed = current - health_window;
        if (elapsed < 1000000) return;
        status.input_fps = double(status.acquired - previous_acquired) * 1000000 / elapsed;
        status.unique_fps = double(status.unique_frames-previous_unique)*1000000/elapsed;previous_unique=status.unique_frames;
        status.output_fps = double(status.encoded - previous_encoded) * 1000000 / elapsed;
        auto rate = [&](uint64_t value, uint64_t& previous) {
            const double result = value >= previous ? double(value - previous) * 1000000 / elapsed : 0;
            previous = value; return result;
        };
        status.duplicate_fps = rate(status.duplicates, previous_duplicates);
        status.replaced_fps = rate(status.replaced, previous_replaced);
        status.selection_dropped_fps = rate(status.selection_dropped, previous_selection_dropped);
        status.backpressure_drop_fps = rate(status.backpressure_drops, previous_backpressure_drops);
        // Source counters restart with a recreated source; rate() reads 0 then.
        status.wgc_callback_fps = rate(status.source_details.callbacks, previous_wgc_callbacks);
        status.wgc_delivered_fps = rate(status.source_details.frames_delivered, previous_wgc_delivered);
        status.wgc_overwritten_fps = rate(status.source_details.overwritten, previous_wgc_overwritten);
        auto percentile = [](const std::deque<double>& values,double rank) {
            if (values.empty()) return 0.0;
            std::vector<double> sorted(values.begin(),values.end()); std::sort(sorted.begin(),sorted.end());
            return sorted[std::max<size_t>(1,size_t(std::ceil(sorted.size()*rank)))-1];
        };
        status.submission_p95_ms = percentile(submission_times,.95); status.completion_p95_ms = percentile(completion_times,.95);
        status.submission_p50_ms = percentile(submission_times,.5); status.completion_p50_ms = percentile(completion_times,.5);
        status.capture_latency_p50_ms = percentile(capture_latencies,.5); status.capture_latency_p95_ms = percentile(capture_latencies,.95);
        if(processing_stage_samples){
            const double samples=double(processing_stage_samples);
            status.texture_readback_ms=readback_ms_sum/samples;status.video_processor_ms=video_processor_ms_sum/samples;
            status.software_convert_ms=software_convert_ms_sum/samples;status.hardware_upload_ms=hardware_upload_ms_sum/samples;
            status.overlay_compose_ms=overlay_compose_ms_sum/samples;
            readback_ms_sum=video_processor_ms_sum=software_convert_ms_sum=hardware_upload_ms_sum=overlay_compose_ms_sum=0;
            processing_stage_samples=0;
        }
        const bool pressured = status.queue_depth * 4 >= status.queue_capacity * 3;
        const bool overloaded = !status.paused && !saving && ((pressured && status.replaced > previous_dropped) || status.backpressure_drop_fps > 0) &&
            status.output_fps < fps.load() * .99;
        status.overload_windows = overloaded ? status.overload_windows + 1 : 0;
        status.qualified_windows = !status.paused && !pressured && status.output_fps >= fps.load() * .99 ? status.qualified_windows + 1 : 0;
        if (status.overload_windows >= 3) { switch_requested = true; status.overload_windows = 0; }
        const auto delivered=status.source_details.frames_delivered;
        const double producer_rate=delivered>=previous_source_delivered?double(delivered-previous_source_delivered)*1000000/elapsed:0;
        previous_source_delivered=delivered;
        const bool transport_shortfall=status.source=="DXGI Desktop Duplication"&&capture_transport_shortfall(status.acquired>0,false,std::min(double(fps.load()),producer_rate),status.input_fps);
        transport_shortfall_windows=transport_shortfall&&!saving?transport_shortfall_windows+1:0;
        const bool source_recovered=status.source_recoveries!=previous_recoveries;
        const bool stalled=status.acquired>0&&status.input_fps<=0&&status.output_fps<1;
        recovery.observe(source_recovered||stalled||transport_shortfall_windows>=3,status.paused,current);
        previous_recoveries=status.source_recoveries;
        // Preserve the longer tuning policy independently from the encoder's
        // three-window failover qualification. No startup benchmarking.
        if(config.protect_frame_rate&&!status.paused&&!saving&&current-session_started>=30000000){
            const double fraction=status.queue_capacity?double(status.queue_depth)/status.queue_capacity:0;
            const bool severe=status.output_fps<fps.load()*.9&&fraction>=.5;
            const bool clean=status.output_fps>=fps.load()*.95&&fraction<.25;
            severe_windows.push_back(severe);if(severe_windows.size()>15)severe_windows.pop_front();
            if(clean){if(!clean_since)clean_since=current;}else clean_since=0;
            if(!last_tuning_decision||current-last_tuning_decision>=60000000){
                const int active=fps.load();int replacement=active;
                if(severe&&severe_windows.size()>=15&&std::count(severe_windows.begin(),severe_windows.end(),true)>=8)
                    replacement=active>90?90:active>60?60:30;
                else if(active<config.fps&&clean_since&&current-clean_since>=120000000)
                    replacement=std::min(config.fps,active<60?60:active<90?90:120);
                if(replacement!=active){fps=replacement;status.active_fps=replacement;status.frame_rate_protected=replacement<config.fps;
                    last_tuning_decision=current;clean_since=0;severe_windows.clear();}
            }
        }else clean_since=0;
        previous_acquired = status.acquired; previous_encoded = status.encoded; previous_dropped = status.replaced; health_window = current;
    }
    void pacing() {
        struct Timer{HANDLE handle=CreateWaitableTimerExW(nullptr,nullptr,0x2,TIMER_ALL_ACCESS);~Timer(){if(handle)CloseHandle(handle);}}timer;
        auto wait_deadline=[&](std::unique_lock<std::mutex>& lock,int64_t microseconds){
            microseconds=std::clamp<int64_t>(microseconds,100,40000);
            if(timer.handle){LARGE_INTEGER due{};due.QuadPart=-microseconds*10;
                if(SetWaitableTimer(timer.handle,&due,0,nullptr,nullptr,FALSE)){
                    lock.unlock();WaitForSingleObject(timer.handle,50);lock.lock();return;
                }
            }
            changed.wait_for(lock,std::chrono::microseconds(microseconds));
        };
        std::shared_ptr<CapturePixels> held;
        uint64_t held_sequence = 0;
        std::vector<CaptureFrameStamp> stamps;
        { std::lock_guard lock(mutex); consumed = 0; }
        int64_t scheduled = now();
        const int64_t origin=scheduled;
        double constant_deadline=double(scheduled);
        int legacy_catchup_remaining=std::clamp(fps.load()/4,4,60);
        RecordingFramePacer pacer(fps,config.variable_frame_rate);
        while (!stopping) {
            const int64_t interval = 1000000 / fps.load();
            const int64_t current = now();
            std::unique_lock lock(mutex);
            if (!latest || user_paused || status.paused) {
                held.reset();
                scheduled = current;constant_deadline=double(current); changed.wait_for(lock, std::chrono::milliseconds(5)); continue;
            }
            int64_t intervals=1;
            if(config.variable_frame_rate){
                if (!capture_variable_deadline(current, interval, scheduled)) {
                    wait_deadline(lock,scheduled+interval-current-750);
                    continue;
                }
            }else if(config.pacing_policy!="legacy"){
                const double exact_interval=1000000.0/fps.load();
                const double elapsed=double(current)-constant_deadline+1000;
                if(elapsed<exact_interval){wait_deadline(lock,int64_t(std::ceil(exact_interval-elapsed)));continue;}
                intervals=std::max<int64_t>(1,int64_t(std::floor(elapsed/exact_interval)));
                constant_deadline+=exact_interval*intervals;
            }else{
                const double next=constant_deadline+1000000.0/fps.load();
                if(double(current)<next){legacy_catchup_remaining=std::clamp(fps.load()/4,4,60);wait_deadline(lock,int64_t(std::ceil(next-current)));continue;}
                if(legacy_catchup_remaining--<=0){constant_deadline=double(current);legacy_catchup_remaining=std::clamp(fps.load()/4,4,60);continue;}
                constant_deadline=next;
            }
            bool fresh = false;
            int64_t sample_us = current, elapsed = current - origin;
            if (config.frame_selection == "newest") {
                fresh = sequence != consumed;
                held = latest; held_sequence = sequence; consumed = sequence;
            } else {
                // Sample one output interval back so a frame that lands just
                // after its tick still gets that tick instead of being
                // overwritten by its successor.
                const int64_t delay = int64_t(std::llround(1000000.0 / fps.load()));
                sample_us = current - delay;
                stamps.clear();
                for (const auto& item : recent) stamps.push_back({item.sequence, item.pixels->timestamp_us});
                if (const auto pick = capture_select_frame(stamps, consumed, sample_us)) {
                    for (size_t i = 0; i < *pick; ++i) if (recent[i].sequence > consumed) ++status.selection_dropped;
                    held = recent[*pick].pixels; held_sequence = recent[*pick].sequence; consumed = held_sequence;
                    recent.erase(recent.begin(), recent.begin() + std::ptrdiff_t(*pick) + 1);
                    status.source_queue_depth = int(recent.size());
                    fresh = true;
                    // VFR stamps the frame's own capture time, bounded to this tick.
                    if (config.variable_frame_rate)
                        elapsed = std::clamp(held->timestamp_us, sample_us - delay, current) - origin;
                }
            }
            if (!held) continue;
            if (!fresh) ++status.duplicates;
            else {
                capture_latencies.push_back(double(current - held->timestamp_us) / 1000);
                if (capture_latencies.size() > 240) capture_latencies.pop_front();
            }
            pacer.set_frame_rate(fps);
            const auto pts = origin+pacer.next(elapsed,fresh,intervals); last_pts = pts;
            const size_t capacity = static_cast<size_t>(capture_queue_capacity(fps));
            while (queue.size() >= capacity) { queue.pop_front(); ++status.replaced; }
            queue.push_back({held, pts, fresh, sample_us, held_sequence, current}); status.queue_depth = int(queue.size()); status.queue_capacity = int(capacity);
            lock.unlock(); changed.notify_all();
        }
        { std::lock_guard lock(mutex); pacing_finished = true; }
        changed.notify_all();
    }
    void detector(CapturePixels& pixels) {
        std::vector<CaptureRect> regions, masks; int width, height;
        bool normalized=false,counter_mask=false;
        std::array<CaptureNormalizedRect,3> normalized_regions{};
        {
            std::lock_guard lock(mutex);
            normalized=config.detector_enabled&&bool(callbacks.detector_snapshot);
            if ((!normalized&&(config.detector_regions.empty() || !callbacks.detector)) || pixels.timestamp_us - last_detector < 500000) return;
            if (config.height < 1000 || std::abs(double(config.width) / config.height - 16.0 / 9.0) > .01) return;
            regions = config.detector_regions; masks = config.detector_masks;
            width = config.detector_width; height = config.detector_height;
            normalized_regions=config.detector_normalized;counter_mask=config.detector_counter_mask;
        }
        if(!capture_copy_texture_pixels_nonblocking(pixels,[&]{return stopping.load();}))return;
        if(normalized){
            auto canvas=convert(pixels,pixels.timestamp_us,detector_scaler);
            RecordingDetectorSnapshot snapshot;snapshot.timestamp_us=pixels.timestamp_us;
            for(size_t i=0;i<3;++i){
                const auto& region=normalized_regions[i];
                CaptureRect rect;
                rect.x=std::clamp(int(std::nearbyint(region.x*config.width)),0,config.width-1);
                rect.y=std::clamp(int(std::nearbyint(region.y*config.height)),0,config.height-1);
                rect.width=std::clamp(int(std::nearbyint(region.width*config.width)),1,config.width-rect.x);
                rect.height=std::clamp(int(std::nearbyint(region.height*config.height)),1,config.height-rect.y);
                auto& image=snapshot.regions[i];image.width=rect.width;image.height=rect.height;image.pixels.resize(size_t(rect.width)*rect.height);
                for(int y=0;y<rect.height;++y)std::copy_n(canvas->data[0]+size_t(rect.y+y)*canvas->linesize[0]+rect.x,
                    rect.width,image.pixels.data()+size_t(y)*rect.width);
                if(i==2&&counter_mask){
                    auto& mask=snapshot.third_mask;mask.width=rect.width;mask.height=rect.height;mask.pixels.resize(image.pixels.size());
                    for(int y=0;y<rect.height;++y)for(int x=0;x<rect.width;++x){
                        const auto uv=canvas->data[1]+size_t((rect.y+y)/2)*canvas->linesize[1]+((rect.x+x)/2)*2;
                        const double luma=(image.pixels[size_t(y)*rect.width+x]-16)*(255.0/219),u=uv[0]-128,v=uv[1]-128;
                        const double r=std::clamp(luma+1.792741*v,0.0,255.0),g=std::clamp(luma-.213249*u-.532909*v,0.0,255.0),b=std::clamp(luma+2.112402*u,0.0,255.0);
                        const bool skull=int64_t(x)*308<int64_t(rect.width)*120;
                        const bool pink=skull&&r>70&&r>1.6*g&&r>b+35&&b>.15*r;
                        const bool gold=skull&&r>140&&g>130&&b<.45*std::min(r,g)&&std::abs(r-g)<80;
                        const bool yellow=r>140&&g>130&&b<.45*std::min(r,g)&&std::abs(r-g)<35;
                        mask.pixels[size_t(y)*rect.width+x]=(pink||gold||yellow)?255:0;
                    }
                }
            }
            last_detector=pixels.timestamp_us;{std::lock_guard lock(mutex);++status.detector_copies;}
            callbacks.detector_snapshot(std::move(snapshot));return;
        }
        if (width <= 0 || height <= 0) { width = pixels.width; height = pixels.height; }
        CapturePixels output; output.width = width; output.height = height; output.stride = width * 4;
        output.timestamp_us = pixels.timestamp_us; output.bgra.resize(size_t(output.stride) * height);
        for (int y = 0; y < height; ++y) for (int x = 0; x < width; ++x) {
            const int sx = int(int64_t(x) * pixels.width / width), sy = int(int64_t(y) * pixels.height / height);
            if (!std::any_of(regions.begin(), regions.end(), [&](const auto& r) { return contains(r, sx, sy); }) ||
                std::any_of(masks.begin(), masks.end(), [&](const auto& r) { return contains(r, sx, sy); })) continue;
            std::copy_n(pixels.bgra.data() + size_t(sy) * pixels.stride + sx * 4, 4,
                output.bgra.data() + size_t(y) * output.stride + x * 4);
        }
        last_detector = pixels.timestamp_us;
        { std::lock_guard lock(mutex); ++status.detector_copies; }
        callbacks.detector(std::move(output));
    }
    void detection(){
        while(!stopping){
            std::shared_ptr<CapturePixels> pixels;
            {std::unique_lock lock(mutex);
                changed.wait_for(lock,std::chrono::milliseconds(25),[&]{return stopping.load();});
                if(stopping)break;
                if(((config.detector_enabled&&callbacks.detector_snapshot)||(!config.detector_regions.empty()&&callbacks.detector))&&!status.paused)pixels=latest;
            }
            if(pixels){auto owned=*pixels;detector(owned);}
        }
    }
    Frame convert(const CapturePixels& pixels, int64_t pts,SwsContext*& conversion) {
        Frame frame(av_frame_alloc()); if (!frame) throw std::bad_alloc();
        frame->format = AV_PIX_FMT_NV12; frame->width = config.width; frame->height = config.height;
        frame->pts = pts; frame->duration = 1000000 / fps.load();
        frame->color_range = AVCOL_RANGE_MPEG; frame->colorspace = AVCOL_SPC_BT709;
        frame->color_primaries = AVCOL_PRI_BT709; frame->color_trc = AVCOL_TRC_BT709;
        check(av_frame_get_buffer(frame.get(), 32), "Allocate recording frame");
        for (int y = 0; y < config.height; ++y) memset(frame->data[0] + size_t(y) * frame->linesize[0], 16, config.width);
        for (int y = 0; y < config.height / 2; ++y) memset(frame->data[1] + size_t(y) * frame->linesize[1], 128, config.width);
        const auto fit = capture_aspect_fit(pixels.width, pixels.height, config.width, config.height);
        conversion = sws_getCachedContext(conversion, pixels.width, pixels.height, AV_PIX_FMT_BGRA,
            fit.width, fit.height, AV_PIX_FMT_NV12, SWS_BILINEAR, nullptr, nullptr, nullptr);
        if (!conversion) throw std::runtime_error("Initialize recording frame conversion");
        const auto coefficients = sws_getCoefficients(SWS_CS_ITU709);
        check(sws_setColorspaceDetails(conversion, coefficients, 1, coefficients, 0, 0, 1 << 16, 1 << 16), "Set BT.709 conversion");
        const uint8_t* input[] = {pixels.bgra.data()}; const int strides[] = {pixels.stride};
        uint8_t* output[] = {frame->data[0] + size_t(fit.y) * frame->linesize[0] + fit.x,
            frame->data[1] + size_t(fit.y / 2) * frame->linesize[1] + fit.x};
        check(sws_scale(conversion, input, strides, 0, pixels.height, output, frame->linesize), "Convert recording frame");
        // swscale's SIMD stores may extend past the fitted row width. Restore
        // studio black after scaling, including row padding and chroma edges.
        for(int plane=0;plane<2;++plane){
            const int divisor=plane?2:1;const int black=plane?128:16;
            for(int row=0;row<config.height/divisor;++row){
                auto* destination=frame->data[plane]+size_t(row)*frame->linesize[plane];
                if(row<fit.y/divisor||row>=(fit.y+fit.height)/divisor)std::memset(destination,black,frame->linesize[plane]);
                else{std::memset(destination,black,fit.x);std::memset(destination+fit.x+fit.width,black,frame->linesize[plane]-fit.x-fit.width);}
            }
        }
        return frame;
    }
    Frame convert(const CapturePixels& pixels,int64_t pts){return convert(pixels,pts,scaler);}
    enum class Pressure { EncoderBusy, Retained, Pool };
    struct PressureTimer {
        HANDLE handle = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        ~PressureTimer() { if (handle) CloseHandle(handle); }
        void pause(int64_t microseconds) const {
            LARGE_INTEGER due{}; due.QuadPart = -std::max<int64_t>(microseconds, 100) * 10;
            if (handle && SetWaitableTimer(handle, &due, 0, nullptr, nullptr, FALSE)) WaitForSingleObject(handle, 50);
            else std::this_thread::sleep_for(std::chrono::microseconds(microseconds));
        }
    };
    // A failed encoder is replaced by the next compatible candidate; with none
    // left open_encoder throws and the worker restarts.
    void replace_encoder(bool& first) { failed_candidates[active_candidate] = true; open_encoder(true); first = true; }
    // Drains ready packets for at most one output interval until ready()
    // holds. Never allocates; waited_us reports the time spent.
    template<class Ready> bool relieve(Ready ready, const PressureTimer& timer, int64_t& waited_us, bool& first) {
        const auto started = now();
        const int64_t budget = 1000000 / fps.load();
        for (;;) {
            try { emit(encoder->drain_ready()); }
            catch (...) { replace_encoder(first); waited_us = now() - started; return true; }
            if (ready()) { waited_us = now() - started; return true; }
            const auto elapsed = now() - started;
            if (elapsed >= budget) { waited_us = elapsed; return false; }
            timer.pause(std::min<int64_t>(1000, budget - elapsed));
        }
    }
    // Counts a dropped tick. Pressure outliving the stall bound without an
    // accepted frame replaces the encoder instead of dropping forever.
    void record_pressure(Pressure kind, int64_t waited_us, bool& first) {
        const auto current = now();
        { std::lock_guard lock(mutex); ++status.backpressure_drops;
          if (kind == Pressure::EncoderBusy) ++status.encoder_busy_drops;
          else if (kind == Pressure::Retained) ++status.retained_pressure_drops;
          else ++status.pool_pressure_drops;
          status.backpressure_wait_max_ms = std::max(status.backpressure_wait_max_ms, double(waited_us) / 1000); }
        if (!pressure_since) { pressure_since = current; return; }
        if (current - pressure_since < kRecordingEncoderStallUs) return;
        pressure_since = 0;
        replace_encoder(first);
        std::lock_guard lock(mutex); ++status.encoder_stall_recoveries;
    }
    void forget(int64_t pts) { retained_surfaces.erase(pts); submitted.erase(pts); submitted_at.erase(pts); }
    void encoding() {
        bool first = true;
        uint64_t last_encoded_sequence=0;
        const PressureTimer timer;
        while (true) {
            Work work;
            { std::unique_lock lock(mutex); changed.wait(lock, [&] { return !queue.empty() || pacing_finished || (stopping && threads <= 1); });
              if (queue.empty()) { if (pacing_finished || stopping) break; continue; }
              work = std::move(queue.front()); queue.pop_front(); status.queue_depth = int(queue.size()); }
            if (switch_requested.exchange(false)) {
                try { open_encoder(true); first = true; }
                catch (const std::exception&) {
                    if (config.protect_frame_rate) {
                        const auto old = fps.load(); const int reduced = old > 90 ? 90 : old > 60 ? 60 : 30;
                        fps = reduced; std::lock_guard lock(mutex); status.active_fps = reduced; status.frame_rate_protected = reduced < config.fps;
                    }
                }
            }
            const auto process_started = now();
            { std::lock_guard lock(mutex); status.queue_age_ms = std::max(0.0, double(process_started-work.scheduled_us)/1000);
                status.queue_age_max_ms=std::max(status.queue_age_max_ms,status.queue_age_ms); }
            // The encoder owns its whole in-flight budget: wait a bounded time
            // for it to release a frame before spending a pool surface.
            if (retained_surfaces.size() >= retained_limit) {
                int64_t waited = 0;
                if (!relieve([&] { return retained_surfaces.size() < retained_limit; }, timer, waited, first)) {
                    record_pressure(Pressure::Retained, waited, first); continue;
                }
            }
            CapturePixels source_copy;
            CapturePixels composed;
            CapturePixels* pixels = work.pixels.get();
            // Immutable CPU frames can be converted directly. GPU readback
            // fills only a private shell, never the shared acquisition frame.
            if(pixels->texture){source_copy=*pixels;pixels=&source_copy;}
            if (callbacks.compose) { capture_copy_texture_pixels(*pixels); composed = *pixels; composed.timestamp_us = work.pts;
                callbacks.compose(composed); composed.texture.reset(); pixels = &composed; }
            double readback_ms=0,video_processor_ms=0,software_convert_ms=0,hardware_upload_ms=0,overlay_ms=0;
            std::string processing_path;
            auto elapsed_ms=[](auto started){return std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-started).count();};
            // Pool exhaustion escapes as SurfacePoolExhausted from every path,
            // including the fallback upload and the overlay upload.
            auto produce = [&]() -> Frame {
                Frame frame;
                if (gpu && pixels->texture && !config.disable_gpu_processing) {
                    try {
                        const auto started=std::chrono::steady_clock::now();
                        frame = gpu->convert(*pixels, work.pts);
                        video_processor_ms=elapsed_ms(started);
                        processing_path=encoder_candidates[active_candidate].d3d11?"d3d11-video-processor":"d3d11-video-processor-readback";
                        if(!encoder_candidates[active_candidate].d3d11){
                            const auto readback_started=std::chrono::steady_clock::now();
                            Frame software(av_frame_alloc());if(!software)throw std::bad_alloc();
                            check(av_hwframe_transfer_data(software.get(),frame.get(),0),"Read processed recording NV12");
                            check(av_frame_copy_props(software.get(),frame.get()),"Copy processed recording timestamps");frame=std::move(software);
                            readback_ms+=elapsed_ms(readback_started);
                        }
                    } catch (const SurfacePoolExhausted&) {
                        throw;
                    } catch (const std::exception& error) {
                        { std::lock_guard lock(mutex); ++status.gpu_conversion_fallbacks;status.gpu_conversion_fallback_error=error.what(); }
                        const auto readback_started=std::chrono::steady_clock::now();capture_copy_texture_pixels(*pixels);readback_ms+=elapsed_ms(readback_started);
                        const auto convert_started=std::chrono::steady_clock::now();frame=convert(*pixels,work.pts);software_convert_ms=elapsed_ms(convert_started);
                        processing_path=encoder_candidates[active_candidate].d3d11?"gpu-fallback-cpu-convert-d3d11-upload":"gpu-fallback-cpu-convert";
                        if(encoder_candidates[active_candidate].d3d11){const auto upload_started=std::chrono::steady_clock::now();frame=gpu->upload(*frame);hardware_upload_ms=elapsed_ms(upload_started);}
                    } catch (...) {
                        { std::lock_guard lock(mutex); ++status.gpu_conversion_fallbacks;status.gpu_conversion_fallback_error="Unknown GPU conversion error"; }
                        const auto readback_started=std::chrono::steady_clock::now();capture_copy_texture_pixels(*pixels);readback_ms+=elapsed_ms(readback_started);
                        const auto convert_started=std::chrono::steady_clock::now();frame=convert(*pixels,work.pts);software_convert_ms=elapsed_ms(convert_started);
                        processing_path=encoder_candidates[active_candidate].d3d11?"gpu-fallback-cpu-convert-d3d11-upload":"gpu-fallback-cpu-convert";
                        if(encoder_candidates[active_candidate].d3d11){const auto upload_started=std::chrono::steady_clock::now();frame=gpu->upload(*frame);hardware_upload_ms=elapsed_ms(upload_started);}
                    }
                } else {
                    if(pixels->texture){const auto readback_started=std::chrono::steady_clock::now();capture_copy_texture_pixels(*pixels);readback_ms=elapsed_ms(readback_started);}
                    const auto convert_started=std::chrono::steady_clock::now();frame = convert(*pixels, work.pts);software_convert_ms=elapsed_ms(convert_started);
                    processing_path=encoder_candidates[active_candidate].d3d11?"cpu-convert-d3d11-upload":"cpu-convert";
                    if (encoder_candidates[active_candidate].d3d11) {const auto upload_started=std::chrono::steady_clock::now();frame = gpu->upload(*frame);hardware_upload_ms=elapsed_ms(upload_started);}
                }
                if (callbacks.compose_nv12&&(!callbacks.overlay_enabled||callbacks.overlay_enabled())) {
                    const auto overlay_started=std::chrono::steady_clock::now();
                    if (frame->format == AV_PIX_FMT_D3D11) {
                        Frame software(av_frame_alloc()); if (!software) throw std::bad_alloc();
                        check(av_hwframe_transfer_data(software.get(), frame.get(), 0), "Download recording overlay canvas");
                        check(av_frame_copy_props(software.get(), frame.get()), "Copy recording overlay timing");
                        callbacks.compose_nv12(*software); const auto upload_started=std::chrono::steady_clock::now();frame = gpu->upload(*software);hardware_upload_ms+=elapsed_ms(upload_started);
                    } else callbacks.compose_nv12(*frame);
                    overlay_ms=elapsed_ms(overlay_started);
                }
                return frame;
            };
            Frame frame;
            bool pool_dropped = false;
            for (int attempt = 0; !frame; ++attempt) {
                try { frame = produce(); }
                catch (const SurfacePoolExhausted&) {
                    // One bounded wait for the encoder to release a surface,
                    // then drop this tick. No CPU fallback, no extra surface.
                    const auto held = retained_surfaces.size();
                    int64_t waited = 0;
                    if (attempt == 0 && relieve([&] { return retained_surfaces.size() < held; }, timer, waited, first)) continue;
                    record_pressure(Pressure::Pool, waited, first); pool_dropped = true; break;
                }
            }
            if (pool_dropped) continue;
            if (first) frame->pict_type = AV_PICTURE_TYPE_I;
            const bool fresh = work.source_sequence != last_encoded_sequence;
            auto track = [&] {
                Frame retained(av_frame_clone(frame.get()));if(!retained)throw std::bad_alloc();
                retained_surfaces[work.pts]=std::move(retained);
                submitted[work.pts] = {work.acquired_us, fresh}; submitted_at[work.pts] = now();
            };
            track();
            const auto submit_started = now();
            { std::lock_guard lock(mutex); status.processing_ms = double(submit_started-process_started)/1000;
                status.processing_max_ms=std::max(status.processing_max_ms,status.processing_ms);
                readback_ms_sum+=readback_ms;video_processor_ms_sum+=video_processor_ms;software_convert_ms_sum+=software_convert_ms;
                hardware_upload_ms_sum+=hardware_upload_ms;overlay_compose_ms_sum+=overlay_ms;++processing_stage_samples;
                status.processing_path=processing_path;
                status.surfaces_in_use_peak=std::max(status.surfaces_in_use_peak,int(submitted.size()));
                if(gpu)status.surfaces_allocated=gpu->allocated(); }
            SubmitResult result;
            try {
                result = encoder->try_submit(*frame); emit(std::move(result.packets));
                // Busy: the encoder refused this frame and holds no reference.
                // Retry within one output interval, then drop the tick.
                const int64_t budget = 1000000 / fps.load();
                while (result.status == SubmitStatus::Busy && now() - submit_started < budget) {
                    timer.pause(1000);
                    result = encoder->try_submit(*frame); emit(std::move(result.packets));
                }
            }
            catch (...) {
                replace_encoder(first); frame->pict_type = AV_PICTURE_TYPE_I;
                track();
                result = encoder->try_submit(*frame); emit(std::move(result.packets));
            }
            const auto submitted_done = now();
            if (result.status == SubmitStatus::Busy) {
                forget(work.pts);
                record_pressure(Pressure::EncoderBusy, submitted_done - submit_started, first);
                continue;
            }
            first = false; last_encoded_sequence = work.source_sequence; pressure_since = 0;
            const double duration = double(submitted_done-submit_started)/1000;
            { std::lock_guard lock(mutex); ++status.submitted; status.submission_ms = duration;status.submission_max_ms=std::max(status.submission_max_ms,duration);
                submission_times.push_back(duration); if(submission_times.size()>240)submission_times.pop_front(); }
        }
        emit(encoder->finish());
    }
};

RecordingCapture::RecordingCapture(RecordingCaptureConfig c, RecordingCaptureCallbacks cb, std::unique_ptr<RecordingFrameSource> source,RecordingCaptureDependencies dependencies)
    : state_(std::make_shared<State>(std::move(c), std::move(cb), std::move(source),std::move(dependencies))) {}
RecordingCapture::~RecordingCapture() { if (state_) stop(); }
void RecordingCapture::start() {
    auto s = state_;
    { std::lock_guard lock(s->mutex);
      if(s->status.restart_required)throw std::logic_error("Recording capture requires worker restart");
      if (s->status.running || s->threads) throw std::logic_error("Recording capture already running"); }
    if (!s->injected_source) { s->source = create_windows_recording_source(s->config); s->gpu.reset(); }
    if(s->config.max_height>0){
        const auto bounds=s->source->content_bounds();
        if(bounds.width<=0||bounds.height<=0)throw std::runtime_error("Recording source has no initial canvas dimensions");
        const int height=std::clamp(s->config.max_height,480,2160);
        const int width=std::max(2,int(std::nearbyint(height*double(bounds.width)/bounds.height)));
        s->config.width=width+(width&1);s->config.height=height+(height&1);
    }
    s->fps=s->config.fps;s->user_paused=false;
    std::fill(s->failed_candidates.begin(),s->failed_candidates.end(),false);
    s->open_encoder(false);
    std::lock_guard lock(s->mutex);
    s->stopping = false; s->pacing_finished = false; s->queue.clear(); s->latest.reset(); s->recent.clear(); s->consumed = 0; s->sequence = 0;
    s->status.source_queue_depth = 0; s->status.source_queue_peak = 0;
    s->session_started=s->now();s->health_window=0;s->last_tuning_decision=0;s->clean_since=0;s->severe_windows.clear();
    s->status.running = true; s->status.source = s->source->name(); s->status.error.clear(); s->status.restart_required = false;
    s->status.output_width=s->config.width;s->status.output_height=s->config.height;
    try { s->launch(&State::acquisition); s->launch(&State::pacing); s->launch(&State::encoding); s->launch(&State::detection); }
    catch (...) { s->stopping = true; s->pacing_finished = true; s->changed.notify_all(); throw; }
}
bool RecordingCapture::stop(std::chrono::milliseconds timeout) {
    auto s = state_; s->stopping = true; s->changed.notify_all();
    std::unique_lock lock(s->mutex);
    if (!s->changed.wait_for(lock, timeout, [&] { return s->threads == 0; })) {
        s->status.restart_required = true; s->status.error = "Native recording shutdown timed out; restart worker"; return false;
    }
    s->status.running = false; return true;
}
void RecordingCapture::pause(bool paused) { state_->user_paused = paused; state_->changed.notify_all(); }
void RecordingCapture::set_save_in_progress(bool saving) { state_->saving=saving; }
void RecordingCapture::request_frame_rate(int requested) {
    const int ceiling = std::min(requested, state_->config.fps);
    int selected = 30; for (const int rate : {120, 90, 60, 30}) if (rate <= ceiling) { selected = rate; break; }
    state_->fps = selected;
    { std::lock_guard lock(state_->mutex); state_->status.active_fps = selected; }
    state_->changed.notify_all();
}
void RecordingCapture::set_detector_regions(std::vector<CaptureRect> regions, std::vector<CaptureRect> masks, int w, int h) {
    if (w < 0 || h < 0 || w > 16384 || h > 16384) throw std::invalid_argument("Invalid detector dimensions");
    std::lock_guard lock(state_->mutex); state_->config.detector_regions = std::move(regions);
    state_->config.detector_masks = std::move(masks); state_->config.detector_width = w; state_->config.detector_height = h;
}
void RecordingCapture::set_detector_regions(const std::array<CaptureNormalizedRect,3>& regions,bool enabled,bool counter_mask){
    for(const auto& region:regions)if(!std::isfinite(region.x)||!std::isfinite(region.y)||!std::isfinite(region.width)||!std::isfinite(region.height)||
        region.x<0||region.y<0||region.width<0||region.height<0||region.x>1||region.y>1||region.width>1||region.height>1)
        throw std::invalid_argument("Invalid normalized detector region");
    std::lock_guard lock(state_->mutex);state_->config.detector_normalized=regions;state_->config.detector_enabled=enabled;state_->config.detector_counter_mask=counter_mask;
}
RecordingCaptureHealth RecordingCapture::health() const { std::lock_guard lock(state_->mutex); return state_->status; }
bool RecordingCapture::safe_save_start(int64_t begin,int64_t end,int64_t& result)const{
    std::lock_guard lock(state_->mutex);return state_->recovery.safe_start(begin,end,result);
}
}
