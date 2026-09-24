#include "recording_capture.h"
#include "readback_stage.h"
#include "captured_frames.h"
#include "recording_save.h"
#include <atomic>
#include <cstdlib>
#include <cstring>
#include <chrono>
#include <condition_variable>
#include <iostream>
#include <mutex>
#include <random>
#include <set>
#include <stdexcept>
#include <thread>
#include <d3d11_4.h>
#include <wrl/client.h>
#include <DirectXPackedVector.h>
#include <dxgi1_4.h>
#include <iomanip>
#include <psapi.h>
extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
}

#define CHECK(x) do { if (!(x)) throw std::runtime_error("Capture assertion: " #x); } while (false)
using namespace clypdat;
using namespace std::chrono_literals;
class GeneratedSource final : public RecordingFrameSource {
    int index_ = 0, fps_;
    int width_=128,height_=72;
    std::chrono::steady_clock::time_point next_ = std::chrono::steady_clock::now();
    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture_;
public:
    explicit GeneratedSource(int fps,bool gpu=false,int width=256,int height=144,IDXGIAdapter* adapter=nullptr) : fps_(fps) {
        if(gpu){width_=width;height_=height;
            CHECK(SUCCEEDED(D3D11CreateDevice(adapter,adapter?D3D_DRIVER_TYPE_UNKNOWN:D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device_,nullptr,&context_)));
            Microsoft::WRL::ComPtr<ID3D11Multithread> protection;CHECK(SUCCEEDED(context_.As(&protection)));protection->SetMultithreadProtected(TRUE);
            std::vector<uint8_t> initial(size_t(width_)*height_*4,96);
            for(size_t i=3;i<initial.size();i+=4)initial[i]=255;
            D3D11_TEXTURE2D_DESC desc{};desc.Width=width_;desc.Height=height_;desc.MipLevels=1;desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;
            D3D11_SUBRESOURCE_DATA data{initial.data(),UINT(width_*4),0};CHECK(SUCCEEDED(device_->CreateTexture2D(&desc,&data,&texture_)));}
    }
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
        const auto now = std::chrono::steady_clock::now();
        if (now < next_) { std::this_thread::sleep_for(std::min(timeout, std::chrono::duration_cast<std::chrono::milliseconds>(next_-now)+1ms)); return false; }
        next_ += std::chrono::microseconds(1000000/fps_);
        pixels.width = width_; pixels.height = height_; pixels.stride = width_*4;
        if(device_){
            constexpr int patch=64;std::vector<uint8_t> moving(size_t(patch)*patch*4);
            for(size_t i=0;i<moving.size();i+=4){moving[i]=uint8_t(index_);moving[i+1]=uint8_t(index_*3);moving[i+2]=255;moving[i+3]=255;}
            const int left=(index_*17)%(width_-patch),top=(index_*11)%(height_-patch);
            D3D11_BOX box{UINT(left),UINT(top),0,UINT(left+patch),UINT(top+patch),1};
            context_->UpdateSubresource(texture_.Get(),0,&box,moving.data(),patch*4,0);
            texture_->AddRef();pixels.texture={texture_.Get(),[](auto*p){p->Release();}};pixels.bgra.clear();
        }else{
            pixels.bgra.resize(size_t(width_)*height_*4);
            for(int y=0;y<height_;++y)for(int x=0;x<width_;++x){auto* p=&pixels.bgra[(y*width_+x)*4];p[0]=uint8_t(x+index_);p[1]=uint8_t(y+index_);p[2]=uint8_t(x+y+index_);p[3]=255;}
        }
        ++index_; return true;
    }
    bool eligible() const override { return true; }
    const char* name() const override { return "generated recording fixture"; }
    void set_frame_rate(int fps) override { fps_=fps; }
    ID3D11Device* d3d_device() const override {return device_.Get();}
};
class StartupDipSource final : public RecordingFrameSource {
    std::chrono::steady_clock::time_point started_ = std::chrono::steady_clock::now();
    std::chrono::steady_clock::time_point next_ = started_;
    int index_ = 0;
public:
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
        const auto current = std::chrono::steady_clock::now();
        if (current < next_) {
            std::this_thread::sleep_for(std::min(timeout,
                std::chrono::duration_cast<std::chrono::milliseconds>(next_ - current) + 1ms));
            return false;
        }
        const bool warmup = current - started_ < 4200ms;
        next_ += std::chrono::microseconds(warmup ? 33333 : 4167);
        pixels.width = 128; pixels.height = 72; pixels.stride = 128 * 4;
        pixels.bgra.resize(size_t(pixels.stride) * pixels.height);
        for (int y = 0; y < pixels.height; ++y) for (int x = 0; x < pixels.width; ++x) {
            auto* pixel = pixels.bgra.data() + size_t(y) * pixels.stride + x * 4;
            pixel[0] = uint8_t(x + index_); pixel[1] = uint8_t(y + index_ * 3);
            pixel[2] = uint8_t(x + y + index_); pixel[3] = 255;
        }
        ++index_;
        return true;
    }
    bool eligible() const override { return true; }
    const char* name() const override { return "generated startup dip fixture"; }
    int switch_attempts = 0;
    bool switch_backend(bool) override { ++switch_attempts; return false; }
};
class EligibilitySource final : public RecordingFrameSource {
    bool dxgi_;
    std::atomic<bool> foreground_{false};
    std::atomic<bool> capturable_{true};
    std::chrono::steady_clock::time_point next_ = std::chrono::steady_clock::now();
    int index_ = 0;
public:
    explicit EligibilitySource(bool dxgi) : dxgi_(dxgi) {}
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
        const auto current = std::chrono::steady_clock::now();
        if (current < next_) {
            std::this_thread::sleep_for(std::min(timeout,
                std::chrono::duration_cast<std::chrono::milliseconds>(next_ - current) + 1ms));
            return false;
        }
        next_ = current + 33333us;
        pixels.width = 128; pixels.height = 72; pixels.stride = 512;
        pixels.bgra.resize(size_t(pixels.stride) * pixels.height);
        for (size_t i = 0; i < pixels.bgra.size(); i += 4) {
            pixels.bgra[i] = uint8_t(index_); pixels.bgra[i + 1] = 96;
            pixels.bgra[i + 2] = 180; pixels.bgra[i + 3] = 255;
        }
        ++index_;
        return true;
    }
    bool eligible() const override { return capturable_ && (!dxgi_ || foreground_); }
    bool foreground() const override { return foreground_; }
    const char* name() const override { return dxgi_ ? "generated DXGI eligibility fixture" : "generated WGC eligibility fixture"; }
    void set_foreground(bool value) { foreground_ = value; }
    void set_capturable(bool value) { capturable_ = value; }
};
void source_eligibility_controls_capture_pause() {
    RecordingCaptureConfig config; config.width = 128; config.height = 72; config.fps = 30; config.cpu_encoder = true;
    std::mutex mutex; std::condition_variable changed; std::atomic<uint64_t> packets = 0;
    RecordingCaptureCallbacks callbacks;
    callbacks.packet = [&](auto, Packet, int64_t, bool) {
        ++packets; changed.notify_all();
    };
    auto wgc = std::make_unique<EligibilitySource>(false);
    auto* wgc_state = wgc.get();
    RecordingCapture capture(config, callbacks, std::move(wgc)); capture.start();
    auto wait_for_packets = [&](uint64_t target) {
        std::unique_lock lock(mutex);
        CHECK(changed.wait_for(lock, 4s, [&] { return packets >= target; }));
    };
    wait_for_packets(12);
    auto health = capture.health();
    const auto metrics_deadline = std::chrono::steady_clock::now() + 3s;
    while ((health.input_fps <= 0 || health.output_fps <= 0) && std::chrono::steady_clock::now() < metrics_deadline) {
        std::this_thread::sleep_for(20ms); health = capture.health();
    }
    CHECK(!health.paused); CHECK(health.input_fps > 0); CHECK(health.output_fps > 0);
    wgc_state->set_foreground(false); // WGC must keep delivering while backgrounded.
    wait_for_packets(24);
    CHECK(!capture.health().paused);
    capture.pause(true);
    const auto paused_deadline = std::chrono::steady_clock::now() + 2s;
    while (!capture.health().paused && std::chrono::steady_clock::now() < paused_deadline) std::this_thread::sleep_for(10ms);
    CHECK(capture.health().paused);
    capture.pause(false);
    wait_for_packets(30);
    wgc_state->set_capturable(false);
    const auto ineligible_deadline = std::chrono::steady_clock::now() + 2s;
    while (!capture.health().paused && std::chrono::steady_clock::now() < ineligible_deadline) std::this_thread::sleep_for(10ms);
    CHECK(capture.health().paused);
    const auto paused_packets = packets.load();
    wgc_state->set_capturable(true);
    wait_for_packets(paused_packets + 6);
    CHECK(!capture.health().paused);
    CHECK(capture.stop());

    auto dxgi = std::make_unique<EligibilitySource>(true);
    auto* dxgi_state = dxgi.get(); packets = 0;
    RecordingCapture dxgi_capture(config, callbacks, std::move(dxgi)); dxgi_capture.start();
    const auto background_deadline = std::chrono::steady_clock::now() + 2s;
    while (!dxgi_capture.health().paused && std::chrono::steady_clock::now() < background_deadline) std::this_thread::sleep_for(10ms);
    CHECK(dxgi_capture.health().paused);
    dxgi_state->set_foreground(true);
    wait_for_packets(6);
    CHECK(!dxgi_capture.health().paused);
    CHECK(dxgi_capture.stop());
}
void startup_source_dip_does_not_switch_backend() {
    RecordingCaptureConfig config; config.width = 128; config.height = 72; config.fps = 90; config.cpu_encoder = true;
    std::mutex mutex; std::condition_variable changed; uint64_t packets = 0;
    RecordingCaptureCallbacks callbacks;
    callbacks.packet = [&](auto, Packet, int64_t, bool) {
        std::lock_guard lock(mutex); ++packets; changed.notify_all();
    };
    auto source = std::make_unique<StartupDipSource>();
    auto* source_state = source.get();
    RecordingCapture capture(config, callbacks, std::move(source)); capture.start();
    {
        std::unique_lock lock(mutex);
        CHECK(changed.wait_for(lock, 7s, [&] { return packets >= 450; }));
    }
    const auto recovered_deadline = std::chrono::steady_clock::now() + 2s;
    while (capture.health().input_fps <= 180 && std::chrono::steady_clock::now() < recovered_deadline)
        std::this_thread::sleep_for(20ms);
    CHECK(capture.stop());
    const auto health = capture.health();
    CHECK(source_state->switch_attempts == 0);
    if (health.input_fps <= 180) throw std::runtime_error("Startup dip did not recover: input=" +
        std::to_string(health.input_fps) + " fresh=" + std::to_string(health.unique_fps) +
        " output=" + std::to_string(health.output_fps) + " switchAttempts=" + std::to_string(source_state->switch_attempts));
    CHECK(health.output_fps >= 85);
    CHECK(health.source == "generated startup dip fixture");
    CHECK(health.error.empty());
}
void roundtrip(int fps, bool variable,bool gpu=false,bool av1=false) {
    RecordingCaptureConfig config; config.width=128; config.height=72; config.fps=fps; config.cpu_encoder=true;
    config.variable_frame_rate=variable; config.monotonic_anchor_us=4000000;config.cpu_encoder=!gpu;config.av1=av1;
    if(gpu){config.width=256;config.height=144;}
    std::mutex mutex; std::condition_variable ready;
    std::vector<Packet> packets;
    std::shared_ptr<const CaptureGeneration> generation;
    RecordingCaptureCallbacks callbacks;
    callbacks.generation=[&](auto g){generation=g;};
    callbacks.packet=[&](auto g, Packet p, int64_t source, bool){
        CHECK(g==generation); CHECK(source>=config.monotonic_anchor_us);
        std::lock_guard lock(mutex); packets.push_back(std::move(p)); ready.notify_all();
    };
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(fps,gpu));
    capture.start();
    {std::unique_lock lock(mutex);if(!ready.wait_for(lock,5s,[&]{return packets.size()>=size_t(fps/3);})){const auto h=capture.health();
        throw std::runtime_error("Roundtrip stalled fps="+std::to_string(fps)+" variable="+std::to_string(variable)+" gpu="+std::to_string(gpu)+" av1="+std::to_string(av1)+
            " packets="+std::to_string(packets.size())+" acquired="+std::to_string(h.acquired)+" submitted="+std::to_string(h.submitted)+" encoded="+std::to_string(h.encoded)+
            " duplicates="+std::to_string(h.duplicates)+" queue="+std::to_string(h.queue_depth)+" sourceQueue="+std::to_string(h.source_queue_depth)+" encoder="+h.encoder+" error="+h.error);}}
    capture.pause(true); capture.request_frame_rate(30);
    CHECK(capture.stop()); CHECK(!capture.health().running); CHECK(capture.health().detector_copies==0);
    if(!capture.health().error.empty())throw std::runtime_error("Roundtrip fps="+std::to_string(fps)+" gpu="+std::to_string(gpu)+" av1="+std::to_string(av1)+": "+capture.health().error);
    CHECK(generation); CHECK(!packets.empty()); CHECK(packets.front()->flags&AV_PKT_FLAG_KEY);
    if(gpu){CHECK(capture.health().hardware_input);CHECK(generation->codec->codec_id==(av1?AV_CODEC_ID_AV1:AV_CODEC_ID_H264));}
    CodecContext decoder(avcodec_alloc_context3(avcodec_find_decoder(generation->codec->codec_id)));
    CHECK(decoder); CHECK(avcodec_parameters_to_context(decoder.get(),generation->codec.get())==0);
    CHECK(avcodec_open2(decoder.get(),decoder->codec,nullptr)==0);
    AVFrame* frame=av_frame_alloc(); CHECK(frame); int decoded=0; int64_t previous=-1;
    auto receive=[&]{for(;;){const int result=avcodec_receive_frame(decoder.get(),frame);if(result==AVERROR(EAGAIN)||result==AVERROR_EOF)break;CHECK(result==0);CHECK(frame->width==config.width&&frame->height==config.height);
        uint64_t luma=0;for(int y=0;y<config.height;++y)for(int x=0;x<config.width;++x)luma+=frame->data[0][y*frame->linesize[0]+x];
        CHECK(luma>uint64_t(config.width*config.height*20));CHECK(frame->color_range==AVCOL_RANGE_MPEG);CHECK(frame->colorspace==AVCOL_SPC_BT709);
        ++decoded;av_frame_unref(frame);}};
    for(const auto& packet:packets){CHECK(packet->pts>previous);previous=packet->pts;CHECK(avcodec_send_packet(decoder.get(),packet.get())==0);receive();}
    CHECK(avcodec_send_packet(decoder.get(),nullptr)==0);receive();av_frame_free(&frame);
    CHECK(decoded==int(packets.size()));
}
void blocked_writer(){
    RecordingCaptureConfig config;config.width=128;config.height=72;config.fps=120;config.cpu_encoder=true;
    std::mutex mutex;std::condition_variable changed;bool entered=false,released=false;
    RecordingCaptureCallbacks callbacks;
    callbacks.packet=[&](auto,Packet,int64_t,bool){std::unique_lock lock(mutex);entered=true;changed.notify_all();changed.wait(lock,[&]{return released;});};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(120));capture.start();
    {std::unique_lock lock(mutex);CHECK(changed.wait_for(lock,3s,[&]{return entered;}));}
    const auto deadline=std::chrono::steady_clock::now()+2s;
    while(!capture.health().replaced&&std::chrono::steady_clock::now()<deadline)std::this_thread::sleep_for(10ms);
    CHECK(capture.health().queue_depth<=15);CHECK(capture.health().replaced>0);
    CHECK(!capture.stop(10ms));CHECK(capture.health().restart_required);
    {std::lock_guard lock(mutex);released=true;}changed.notify_all();CHECK(capture.stop(3s));
}
std::string resource_option(const VideoEncoderConfig& config,const std::string& key){
    for(const auto& [name,value]:config.resource_options)if(name==key)return value;
    return {};
}
void gpu_4k_to_1440p(int fps){
    RecordingCaptureConfig config;config.width=2560;config.height=1440;config.fps=fps;
    std::mutex mutex;std::condition_variable changed;uint64_t packets=0;
    RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet,int64_t,bool){std::lock_guard lock(mutex);++packets;changed.notify_all();};
    std::vector<VideoEncoderConfig> opened;RecordingCaptureDependencies dependencies;
    dependencies.open_encoder=[&](const VideoEncoderConfig& value,size_t){opened.push_back(value);return std::make_unique<VideoEncoder>(value);};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(fps,true,3840,2160),std::move(dependencies));capture.start();
    bool reached=false;{std::unique_lock lock(mutex);reached=changed.wait_for(lock,5s,[&]{return packets>=size_t(fps*2);});}
    if(!reached){capture.stop();const auto stalled=capture.health();std::lock_guard lock(mutex);
        throw std::runtime_error("4K-to-1440p "+std::to_string(fps)+" FPS GPU capture timed out: packets="+std::to_string(packets)+" acquired="+std::to_string(stalled.acquired)+" encoded="+std::to_string(stalled.encoded)+" input="+std::to_string(stalled.input_fps)+" output="+std::to_string(stalled.output_fps)+" queue="+std::to_string(stalled.queue_depth)+" drops="+std::to_string(stalled.replaced)+" path="+stalled.processing_path+" fallback="+stalled.gpu_conversion_fallback_error+" error="+stalled.error);}
    CHECK(capture.stop());const auto health=capture.health();
    if(!health.error.empty())throw std::runtime_error("4K-to-1440p "+std::to_string(fps)+" FPS GPU capture failed: "+health.error);
    CHECK(health.hardware_input);CHECK(health.output_width==2560&&health.output_height==1440);
    if(health.processing_path!="d3d11-video-processor")throw std::runtime_error("4K-to-1440p path="+health.processing_path+" fallbacks="+std::to_string(health.gpu_conversion_fallbacks)+" fallbackError="+health.gpu_conversion_fallback_error+" videoProcessor="+std::to_string(health.video_processor_ms)+" softwareConvert="+std::to_string(health.software_convert_ms));
    CHECK(health.gpu_conversion_fallbacks==0);
    CHECK(health.output_fps>=fps*.9);CHECK(health.queue_depth<health.queue_capacity);
    CHECK(health.processing_ms<1000.0/fps);
    // The encoder and pool follow the plan, never the old (fps+1)/2 formula.
    const auto expected=plan_recording_encoder(config,{"h264_nvenc",false,true},kAdapterVendorNvidia,false);CHECK(expected);
    CHECK(!opened.empty()&&opened.front().name=="h264_nvenc"&&opened.front().resource_options==expected->options);
    CHECK(resource_option(opened.front(),"surfaces")==std::to_string(expected->encoder_slots));
    CHECK(expected->encoder_slots<std::clamp((fps+1)/2,16,60));
    CHECK(health.encoder_planned&&health.zero_copy_status=="confirmed"&&health.capture_adapter_vendor==kAdapterVendorNvidia);
    CHECK(health.surface_capacity==expected->pool_capacity&&health.pool_capacity==expected->pool_capacity);
    CHECK(health.surface_capacity<legacy_surface_capacity(fps));
    CHECK(health.surfaces_allocated<=health.surface_capacity&&health.surfaces_in_use_peak<=expected->max_in_flight);
    // Zero-copy keeps no staging textures or CPU frames and allocates none per frame.
    CHECK(health.readback_staging_slots==0&&health.readback_cpu_frames==0&&health.frame_allocations==0);
    CHECK(health.encoder_slots==expected->encoder_slots&&health.max_in_flight==expected->max_in_flight);
    if(fps==90)CHECK(health.encoder_slots==8&&health.encoder_delay==6&&health.max_in_flight==7&&health.surface_capacity==9);
    std::cout<<"4K-to-1440p@"<<fps<<" plan: slots="<<health.encoder_slots<<" delay="<<health.encoder_delay<<" pool="<<health.surface_capacity
        <<" allocated="<<health.surfaces_allocated<<" peak="<<health.surfaces_in_use_peak<<" completionP50="<<health.completion_p50_ms<<"ms\n";
    std::cout<<"4K-to-1440p@"<<fps<<" GPU capture: input="<<health.input_fps<<" output="<<health.output_fps
        <<" processing="<<health.processing_ms<<"ms videoProcessor="<<health.video_processor_ms<<"ms dropped="<<health.replaced<<"\n";
}
void detector(){
    RecordingCaptureConfig config;config.width=1920;config.height=1080;config.fps=30;config.cpu_encoder=true;
    config.detector_enabled=true;config.detector_counter_mask=true;
    config.detector_normalized={CaptureNormalizedRect{.34,.445,.32,.065},CaptureNormalizedRect{.42,.335,.16,.055},CaptureNormalizedRect{1152.0/2560,1036.0/1440,308.0/2560,174.0/1440}};
    std::mutex mutex;std::condition_variable changed;bool delivered=false;
    RecordingCaptureCallbacks callbacks;callbacks.detector_snapshot=[&](RecordingDetectorSnapshot snapshot){
        CHECK(snapshot.regions[0].width==614);CHECK(snapshot.regions[0].height==70);
        CHECK(snapshot.third_mask.pixels.size()==snapshot.regions[2].pixels.size());
        std::lock_guard lock(mutex);delivered=true;changed.notify_all();};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(30));capture.start();
    {std::unique_lock lock(mutex);CHECK(changed.wait_for(lock,5s,[&]{return delivered;}));}CHECK(capture.stop());
    CHECK(capture.health().detector_copies>0);if(!capture.health().error.empty())throw std::runtime_error("Detector: "+capture.health().error);
}
class PatternSource final:public RecordingFrameSource{
    int width_,height_;bool delivered_=false;Microsoft::WRL::ComPtr<ID3D11Device> device_;
public:
    PatternSource(int width,int height,bool gpu):width_(width),height_(height){
        if(gpu){Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;CHECK(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device_,nullptr,&context)));
            Microsoft::WRL::ComPtr<ID3D11Multithread> protection;CHECK(SUCCEEDED(context.As(&protection)));protection->SetMultithreadProtected(TRUE);}
    }
    bool acquire(CapturePixels& pixels,std::chrono::milliseconds wait)override{
        if(delivered_){std::this_thread::sleep_for(wait);return false;}delivered_=true;
        pixels.width=width_;pixels.height=height_;pixels.stride=width_*4;pixels.bgra.resize(size_t(pixels.stride)*height_);
        const int side=std::min(width_,height_)/3;
        for(int y=0;y<height_;++y)for(int x=0;x<width_;++x){const bool square=std::abs(x-width_/2)<side/2&&std::abs(y-height_/2)<side/2;
            const uint8_t value=square?255:(x%64<2||y%64<2?0:80);auto*p=pixels.bgra.data()+size_t(y)*pixels.stride+x*4;p[0]=p[1]=p[2]=value;p[3]=255;}
        if(device_){D3D11_TEXTURE2D_DESC desc{};desc.Width=width_;desc.Height=height_;desc.MipLevels=1;desc.ArraySize=1;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            D3D11_SUBRESOURCE_DATA data{pixels.bgra.data(),UINT(pixels.stride),0};ID3D11Texture2D* texture=nullptr;CHECK(SUCCEEDED(device_->CreateTexture2D(&desc,&data,&texture)));
            pixels.texture={texture,[](auto*p){p->Release();}};pixels.bgra.clear();}
        return true;
    }
    bool eligible()const override{return true;}const char* name()const override{return "generated square grid";}
    ID3D11Device* d3d_device()const override{return device_.Get();}
};
void aspect_fit_processing(bool gpu){
    for(const auto [width,height]:{std::pair{858,527},std::pair{3840,2160},std::pair{1080,1920},std::pair{3440,1440},std::pair{857,529}}){
        RecordingCaptureConfig config;config.width=1758;config.height=1080;config.fps=30;config.cpu_encoder=true;
        std::mutex mutex;std::condition_variable changed;bool checked=false;
        const auto fit=capture_aspect_fit(width,height,1758,1080);
        RecordingCaptureCallbacks callbacks;callbacks.compose_nv12=[&](AVFrame& frame){
            CHECK(frame.format==AV_PIX_FMT_NV12);int horizontal=0,vertical=0;
            for(int x=fit.x;x<fit.x+fit.width;++x)if(frame.data[0][(fit.y+fit.height/2)*frame.linesize[0]+x]>210)++horizontal;
            for(int y=fit.y;y<fit.y+fit.height;++y)if(frame.data[0][y*frame.linesize[0]+fit.x+fit.width/2]>210)++vertical;
            CHECK(horizontal>50);CHECK(std::abs(horizontal-vertical)<=3);
            for(int y=0;y<1080;++y)for(int x=0;x<1758;++x){if(x>=fit.x&&x<fit.x+fit.width&&y>=fit.y&&y<fit.y+fit.height)continue;
                CHECK(frame.data[0][y*frame.linesize[0]+x]==16);CHECK(frame.data[1][(y/2)*frame.linesize[1]+x]==128);}
            std::lock_guard lock(mutex);checked=true;changed.notify_all();};
        RecordingCapture capture(config,callbacks,std::make_unique<PatternSource>(width,height,gpu));capture.start();
        {std::unique_lock lock(mutex);if(!changed.wait_for(lock,5s,[&]{return checked;}))throw std::runtime_error("Aspect processing: "+capture.health().error);}
        CHECK(capture.stop());if(!capture.health().error.empty())throw std::runtime_error("Aspect: "+capture.health().error);
    }
}
void hdr_shader(){
    Microsoft::WRL::ComPtr<ID3D11Device> device;CHECK(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,nullptr)));
    for(int width:{4,6})for(bool changed:{false,true}){
        std::vector<uint16_t> data(size_t(width)*4*4);
        for(int y=0;y<4;++y)for(int x=0;x<width;++x){float r=0,g=0,b=0;
            if(x==1&&y==0)r=g=b=.18f;else if(x==2&&y==0)r=g=b=4;
            else if(x==3&&y==0){r=changed?.1f:4;g=changed?4:.1f;b=.1f;}
            const size_t i=(y*width+x)*4;data[i]=DirectX::PackedVector::XMConvertFloatToHalf(r);data[i+1]=DirectX::PackedVector::XMConvertFloatToHalf(g);data[i+2]=DirectX::PackedVector::XMConvertFloatToHalf(b);data[i+3]=DirectX::PackedVector::XMConvertFloatToHalf(1);}
        D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=4;desc.MipLevels=1;desc.ArraySize=1;desc.Format=DXGI_FORMAT_R16G16B16A16_FLOAT;desc.SampleDesc.Count=1;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
        D3D11_SUBRESOURCE_DATA initial{data.data(),UINT(width*8),0};Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;CHECK(SUCCEEDED(device->CreateTexture2D(&desc,&initial,&texture)));
        CapturePixels output;output.texture=capture_tone_map_texture(texture.Get(),80);output.width=width;output.height=4;output.stride=width*4;capture_copy_texture_pixels(output);
        CHECK(output.bgra[6]>=115&&output.bgra[6]<=121);CHECK(output.bgra[10]>=252);
        const auto*r=output.bgra.data()+12;if(changed)CHECK(r[1]>r[2]+12&&r[1]>r[0]+12);else CHECK(r[2]>r[1]+12&&r[2]>r[0]+12);CHECK(r[3]==255);
    }
}
void decode_saved_session(const std::filesystem::path& path){
    AVFormatContext* input=nullptr;const auto encoded=path.u8string();const std::string name(encoded.begin(),encoded.end());
    CHECK(avformat_open_input(&input,name.c_str(),nullptr,nullptr)>=0);
    struct Close{AVFormatContext* input;~Close(){avformat_close_input(&input);}}close{input};
    CHECK(avformat_find_stream_info(input,nullptr)>=0);CHECK(input->nb_streams==1);CHECK(input->duration>0);
    auto* stream=input->streams[0];CHECK(stream->codecpar->codec_id==AV_CODEC_ID_H264);CHECK(stream->codecpar->width==256&&stream->codecpar->height==144);
    auto* raw=avcodec_alloc_context3(avcodec_find_decoder(AV_CODEC_ID_H264));CHECK(raw);CodecContext decoder(raw);
    CHECK(avcodec_parameters_to_context(raw,stream->codecpar)>=0);CHECK(avcodec_open2(raw,raw->codec,nullptr)>=0);
    Packet packet(av_packet_alloc());AVFrame* frame=av_frame_alloc();CHECK(packet&&frame);int count=0;int64_t prior=AV_NOPTS_VALUE;
    auto receive=[&]{for(;;){const int result=avcodec_receive_frame(raw,frame);if(result==AVERROR(EAGAIN)||result==AVERROR_EOF)break;CHECK(result==0);++count;av_frame_unref(frame);}};
    while(av_read_frame(input,packet.get())>=0){CHECK(packet->pts!=AV_NOPTS_VALUE);if(prior!=AV_NOPTS_VALUE)CHECK(packet->pts>prior);prior=packet->pts;
        if(!count)CHECK(packet->flags&AV_PKT_FLAG_KEY);CHECK(avcodec_send_packet(raw,packet.get())>=0);receive();av_packet_unref(packet.get());}
    CHECK(avcodec_send_packet(raw,nullptr)>=0);receive();av_frame_free(&frame);CHECK(count>=15);
    CHECK(av_seek_frame(input,-1,input->duration/2,AVSEEK_FLAG_BACKWARD)>=0);
}

// Plans follow the recording configuration; pacing depth, adaptive rate and
// candidate order never size the NV12 pool.
void recording_encoder_plans(){
    _putenv_s("CLYPDAT_NVENC_DELAY","");
    RecordingCaptureConfig config;config.width=2560;config.height=1440;config.fps=90;config.bitrate_mbps=25;
    const RecordingEncoderCandidate zero_copy{"h264_nvenc",false,true},readback{"h264_nvenc",false,false};
    const auto plan=plan_recording_encoder(config,zero_copy,kAdapterVendorNvidia,false);CHECK(plan);
    CHECK(plan->encoder_slots==8&&plan->output_delay_frames==6&&plan->max_in_flight==7&&plan->pool_capacity==9);
    CHECK(plan->zero_copy_status==ZeroCopyStatus::Confirmed&&plan->requested_codec==EncoderCodec::H264&&!plan->codec_fallback());
    CHECK(plan->pool_bytes==9ull*2560*1440*3/2&&plan->stages.pacing_queue==capture_queue_capacity(90));
    CHECK((plan->options==std::vector<std::pair<std::string,std::string>>{{"surfaces","8"},{"delay","6"}}));
    CHECK(legacy_surface_capacity(90)==62&&plan->pool_capacity<legacy_surface_capacity(90));
    // The pacing queue is reported, never pooled.
    EncoderRequest shallow;shallow.width=2560;shallow.height=1440;shallow.fps=90;shallow.bitrate_mbps=25;
    shallow.adapter_vendor=kAdapterVendorNvidia;shallow.pacing_queue=0;
    CHECK(encoder_backend(EncoderVendor::Nvidia).plan(shallow,true).pool_capacity==plan->pool_capacity);
    // Burned overlays reserve one conversion surface.
    CHECK(plan_recording_encoder(config,zero_copy,kAdapterVendorNvidia,true)->pool_capacity==10);
    // A foreign adapter cannot feed D3D11 NVENC; the readback candidate still plans.
    bool skipped=false;try{plan_recording_encoder(config,zero_copy,kAdapterVendorAmd,false);}catch(const EncoderPlanInfeasible&){skipped=true;}CHECK(skipped);
    const auto copy=plan_recording_encoder(config,readback,kAdapterVendorAmd,false);CHECK(copy);
    CHECK(!copy->zero_copy&&copy->zero_copy_status==ZeroCopyStatus::NotUsed&&copy->needs_cpu_staging);
    // Unknown adapter: plannable, but only unverified.
    const auto unknown=plan_recording_encoder(config,zero_copy,0,false);CHECK(unknown);
    CHECK(unknown->zero_copy_status==ZeroCopyStatus::Unverified&&!unknown->confirmed()&&unknown->pool_capacity==9);
    // libx264 plans the software readback pipeline: two conversion targets,
    // two staging textures and two CPU frames at every rate.
    const auto software=plan_recording_encoder(config,{"libx264",false,false},kAdapterVendorNvidia,false);
    CHECK(software&&software->vendor==EncoderVendor::Software&&software->needs_cpu_staging&&!software->zero_copy);
    CHECK(software->pool_capacity==2&&software->staging_slots==2&&software->cpu_frames==2&&software->max_in_flight==0);
    const auto av1=plan_recording_encoder(config,{"av1_nvenc",false,true},kAdapterVendorNvidia,false);
    CHECK(av1&&av1->requested_codec==EncoderCodec::AV1&&av1->effective_codec==EncoderCodec::AV1&&av1->codec_name=="av1_nvenc");
    // Delay override: configured value first, environment second, one final delay.
    config.nvenc_delay=4;const auto four=plan_recording_encoder(config,zero_copy,kAdapterVendorNvidia,false);
    CHECK((four->options==std::vector<std::pair<std::string,std::string>>{{"surfaces","6"},{"delay","4"}})&&four->pool_capacity==7);
    config.nvenc_delay=8;const auto eight=plan_recording_encoder(config,zero_copy,kAdapterVendorNvidia,false);
    CHECK((eight->options==std::vector<std::pair<std::string,std::string>>{{"surfaces","10"},{"delay","8"}})&&eight->pool_capacity==11);
    config.nvenc_delay=0;_putenv_s("CLYPDAT_NVENC_DELAY","8");
    CHECK(recording_encoder_policy(config).nvenc_delay_override==8);
    config.nvenc_delay=4;CHECK(recording_encoder_policy(config).nvenc_delay_override==4);
    config.nvenc_delay=0;_putenv_s("CLYPDAT_NVENC_DELAY","5");CHECK(recording_encoder_policy(config).nvenc_delay_override==0);
    _putenv_s("CLYPDAT_NVENC_DELAY","");
    // The configured ceiling sizes every supported rate.
    const int pools[]={7,7,9,11};int index=0;
    for(int fps:{30,60,90,120}){config.fps=fps;const auto rate=plan_recording_encoder(config,zero_copy,kAdapterVendorNvidia,false);
        CHECK(rate&&rate->pool_capacity==pools[index++]&&rate->pool_capacity<=kNvencRegisteredResources);check_encoder_plan(*rate);}
    CHECK(d3d11_adapter_vendor(nullptr)==0);
}

// AMF plans come from the recording configuration and capture adapter; the
// NVENC plan is untouched by them.
void amf_recording_plans(){
    _putenv_s("CLYPDAT_NVENC_DELAY","");
    RecordingCaptureConfig config;config.width=2560;config.height=1440;config.bitrate_mbps=25;
    const RecordingEncoderCandidate zero_copy{"h264_amf",false,true},readback{"h264_amf",false,false};
    const int depths[]={3,4,6,8};int index=0;
    for(int fps:{30,60,90,120}){
        config.fps=fps;const int depth=depths[index++];
        const auto p=plan_recording_encoder(config,zero_copy,kAdapterVendorAmd,false);CHECK(p);
        CHECK(p->vendor==EncoderVendor::Amd&&p->codec_name=="h264_amf"&&p->input==EncoderInput::D3D11Frames);
        CHECK(p->zero_copy&&p->zero_copy_status==ZeroCopyStatus::Confirmed&&p->frames_from_encoder_ctx&&!p->needs_cpu_staging);
        CHECK(p->low_delay_flag&&p->output_delay_frames==0&&p->encoder_slots==depth&&p->max_in_flight==depth&&p->pool_capacity==depth+2);
        CHECK((p->options==std::vector<std::pair<std::string,std::string>>{{"async_depth",std::to_string(depth)},{"bf","0"},{"preanalysis","0"}}));
        check_encoder_plan(*p);
    }
    config.fps=90;
    CHECK(plan_recording_encoder(config,zero_copy,kAdapterVendorAmd,true)->pool_capacity==9);
    // Foreign adapter: zero-copy is infeasible; the readback plan still sizes AMF.
    bool skipped=false;try{plan_recording_encoder(config,zero_copy,kAdapterVendorNvidia,false);}catch(const EncoderPlanInfeasible&){skipped=true;}CHECK(skipped);
    skipped=false;try{plan_recording_encoder(config,zero_copy,kAdapterVendorIntel,false);}catch(const EncoderPlanInfeasible&){skipped=true;}CHECK(skipped);
    const auto copy=plan_recording_encoder(config,readback,kAdapterVendorNvidia,false);CHECK(copy);
    CHECK(!copy->zero_copy&&copy->zero_copy_status==ZeroCopyStatus::NotUsed&&copy->needs_cpu_staging&&!copy->frames_from_encoder_ctx);
    CHECK(copy->input==EncoderInput::SystemFrames&&copy->pool_capacity==2&&copy->max_in_flight==6&&copy->low_delay_flag);
    // Unknown adapter: probe allowed, never confirmed by being unknown.
    const auto unknown=plan_recording_encoder(config,zero_copy,0,false);CHECK(unknown);
    CHECK(unknown->zero_copy_status==ZeroCopyStatus::Unverified&&!unknown->confirmed()&&unknown->pool_capacity==8);
    const auto av1=plan_recording_encoder(config,{"av1_amf",false,true},kAdapterVendorAmd,false);
    CHECK(av1&&av1->codec_name=="av1_amf"&&av1->requested_codec==EncoderCodec::AV1&&av1->effective_codec==EncoderCodec::AV1);
    // NVENC resource values are unchanged.
    const auto nvenc=plan_recording_encoder(config,{"h264_nvenc",false,true},kAdapterVendorNvidia,false);
    CHECK(nvenc->encoder_slots==8&&nvenc->max_in_flight==7&&nvenc->pool_capacity==9&&!nvenc->low_delay_flag&&!nvenc->frames_from_encoder_ctx);
    // Candidate order: each vendor tries zero-copy before readback.
    auto names=[](const std::vector<RecordingEncoderCandidate>& list){std::vector<std::string> result;
        for(const auto& c:list)result.push_back(c.name+(c.d3d11?"+d3d11":"")+(c.low_power?"+lp":""));return result;};
    CHECK((names(recording_encoder_candidates(false,false))==std::vector<std::string>{"h264_nvenc+d3d11","h264_nvenc","h264_amf+d3d11","h264_amf",
        "h264_qsv+d3d11+lp","h264_qsv+d3d11","h264_qsv+lp","h264_qsv","libx264"}));
    CHECK((names(recording_encoder_candidates(false,true))==std::vector<std::string>{"av1_nvenc+d3d11","h264_nvenc+d3d11","av1_nvenc","h264_nvenc",
        "av1_amf+d3d11","av1_amf","av1_qsv+d3d11+lp","av1_qsv+d3d11","av1_qsv+lp","av1_qsv",
        "h264_amf+d3d11","h264_amf","h264_qsv+d3d11+lp","h264_qsv+d3d11","h264_qsv+lp","h264_qsv","libx264"}));
    CHECK((names(recording_encoder_candidates(true,false))==std::vector<std::string>{"libx264"}));
}
// QSV plans come from the recording configuration and capture adapter; the
// NVENC and AMF plans are untouched by them.
void qsv_recording_plans(){
    _putenv_s("CLYPDAT_NVENC_DELAY","");
    RecordingCaptureConfig config;config.width=2560;config.height=1440;config.bitrate_mbps=25;
    const RecordingEncoderCandidate zero_copy{"h264_qsv",true,true},readback{"h264_qsv",true,false};
    const int depths[]={3,4,6,6},pools[]={6,7,9,9};int index=0;
    for(int fps:{30,60,90,120}){
        config.fps=fps;const int depth=depths[index],pool=pools[index++];
        const auto p=plan_recording_encoder(config,zero_copy,kAdapterVendorIntel,false);CHECK(p);
        CHECK(p->vendor==EncoderVendor::Intel&&p->codec_name=="h264_qsv"&&p->input==EncoderInput::QsvFrames);
        CHECK(p->zero_copy&&p->zero_copy_status==ZeroCopyStatus::Confirmed&&p->frames_from_encoder_ctx&&p->right_size_packets&&!p->needs_cpu_staging);
        CHECK(p->encoder_slots==depth&&p->max_in_flight==depth&&p->output_delay_frames==depth&&p->pool_capacity==pool&&!p->low_delay_flag);
        CHECK(p->stages.encoder_input_surfaces==depth+1&&p->pool_bytes==uint64_t(pool)*2560*1440*3/2);
        CHECK((p->options==std::vector<std::pair<std::string,std::string>>{{"async_depth",std::to_string(depth)}}));
        check_encoder_plan(*p);
    }
    config.fps=90;
    CHECK(plan_recording_encoder(config,zero_copy,kAdapterVendorIntel,true)->pool_capacity==10);
    // QSV children are 16-row aligned: 1080p surfaces are 1920x1088.
    config.width=1920;config.height=1080;
    CHECK(plan_recording_encoder(config,zero_copy,kAdapterVendorIntel,false)->pool_bytes==9ull*1920*1088*3/2);
    config.width=2560;config.height=1440;
    // Another vendor's adapter cannot feed QSV surfaces; the readback plan still sizes QSV.
    for(const uint32_t foreign:{kAdapterVendorNvidia,kAdapterVendorAmd}){
        bool skipped=false;try{plan_recording_encoder(config,zero_copy,foreign,false);}catch(const EncoderPlanInfeasible&){skipped=true;}CHECK(skipped);}
    const auto copy=plan_recording_encoder(config,readback,kAdapterVendorNvidia,false);CHECK(copy);
    CHECK(!copy->zero_copy&&copy->zero_copy_status==ZeroCopyStatus::NotUsed&&copy->needs_cpu_staging&&!copy->frames_from_encoder_ctx);
    CHECK(copy->input==EncoderInput::SystemFrames&&copy->pool_capacity==2&&copy->max_in_flight==6&&copy->right_size_packets);
    CHECK((copy->options==std::vector<std::pair<std::string,std::string>>{{"async_depth","6"}}));
    // Unknown adapter: probe allowed, never confirmed by being unknown.
    const auto unknown=plan_recording_encoder(config,zero_copy,0,false);CHECK(unknown);
    CHECK(unknown->zero_copy_status==ZeroCopyStatus::Unverified&&!unknown->confirmed()&&unknown->pool_capacity==9);
    const auto av1=plan_recording_encoder(config,{"av1_qsv",true,true},kAdapterVendorIntel,false);
    CHECK(av1&&av1->codec_name=="av1_qsv"&&av1->requested_codec==EncoderCodec::AV1&&av1->effective_codec==EncoderCodec::AV1);
    CHECK(av1->input==EncoderInput::QsvFrames&&av1->pool_capacity==9&&av1->right_size_packets);
    // NVENC and AMF resource values are unchanged.
    const auto nvenc=plan_recording_encoder(config,{"h264_nvenc",false,true},kAdapterVendorNvidia,false);
    CHECK(nvenc->encoder_slots==8&&nvenc->max_in_flight==7&&nvenc->pool_capacity==9&&!nvenc->right_size_packets);
    const auto amf=plan_recording_encoder(config,{"h264_amf",false,true},kAdapterVendorAmd,false);
    CHECK(amf->encoder_slots==6&&amf->pool_capacity==8&&amf->low_delay_flag&&!amf->right_size_packets);
}
struct PlannedRun{RecordingCaptureHealth health;std::vector<size_t> attempted;std::vector<VideoEncoderConfig> opened;uint64_t packets=0;};
PlannedRun planned_run(std::optional<uint32_t> adapter,std::vector<RecordingEncoderCandidate> candidates){
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;
    RecordingCaptureDependencies dependencies;dependencies.candidates=std::move(candidates);dependencies.adapter_vendor=adapter;
    PlannedRun run;std::mutex mutex;std::condition_variable changed;
    dependencies.open_encoder=[&](const VideoEncoderConfig& value,size_t candidate){run.attempted.push_back(candidate);run.opened.push_back(value);return std::make_unique<VideoEncoder>(value);};
    RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet,int64_t,bool){std::lock_guard lock(mutex);++run.packets;changed.notify_all();};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    {std::unique_lock lock(mutex);if(!changed.wait_for(lock,5s,[&]{return run.packets>=60;}))throw std::runtime_error("Planned run stalled: "+capture.health().error);}
    CHECK(capture.stop());run.health=capture.health();if(!run.health.error.empty())throw std::runtime_error(run.health.error);
    return run;
}
void planned_adapter_selection(){
    const RecordingEncoderCandidate zero_copy{"h264_nvenc",false,true},readback{"h264_nvenc",false,false};
    // Mismatch: D3D11 NVENC is skipped before opening; readback NVENC records.
    auto mismatch=planned_run(kAdapterVendorAmd,{zero_copy,readback});
    CHECK(mismatch.attempted==std::vector<size_t>({1}));CHECK(!mismatch.health.hardware_input&&mismatch.health.encoder_planned);
    CHECK(mismatch.health.zero_copy_status=="not-used"&&mismatch.health.capture_adapter_vendor==kAdapterVendorAmd);
    CHECK(mismatch.opened.front().hardware_frames==nullptr&&resource_option(mismatch.opened.front(),"surfaces")=="4");
    // Unknown: the real open is the probe; the plan stays unverified.
    auto unknown=planned_run(0u,{zero_copy,readback});
    CHECK(unknown.attempted==std::vector<size_t>({0}));CHECK(unknown.health.hardware_input);
    CHECK(unknown.health.zero_copy_status=="unverified"&&unknown.health.zero_copy_probe_passed);
    // Confirmed from the real device.
    auto confirmed=planned_run(std::nullopt,{zero_copy,readback});
    CHECK(confirmed.health.zero_copy_status=="confirmed"&&!confirmed.health.zero_copy_probe_passed);
    CHECK(confirmed.health.capture_adapter_vendor==kAdapterVendorNvidia&&confirmed.health.surface_capacity==7);
}
// Encoder doubles on a real GPU pipeline so pool surfaces are genuine. The
// probe can hold every frame it accepts (pinning pool surfaces), refuse input
// (EAGAIN), or accept input without ever producing output.
struct PressureProbe {
    std::mutex mutex;
    std::deque<int64_t> pending;
    std::vector<AVFrame*> held;
    bool hold = false, busy = false, stuck = false, flushing = false;
    void release() { std::lock_guard lock(mutex); for (auto* frame : held) av_frame_free(&frame); held.clear(); hold = false; }
    ~PressureProbe() { for (auto* frame : held) av_frame_free(&frame); }
};
CodecCalls probe_calls(std::shared_ptr<PressureProbe> probe) {
    CodecCalls calls;
    calls.send = [probe](AVCodecContext*, const AVFrame* frame) {
        std::lock_guard lock(probe->mutex);
        if (!frame) { probe->flushing = true; return 0; }
        if (probe->busy) return AVERROR(EAGAIN);
        if (probe->hold) probe->held.push_back(av_frame_clone(frame));
        if (!probe->stuck) probe->pending.push_back(frame->pts);
        return 0;
    };
    calls.receive = [probe](AVCodecContext*, AVPacket* packet) {
        std::lock_guard lock(probe->mutex);
        if (probe->pending.empty()) return probe->flushing ? AVERROR_EOF : AVERROR(EAGAIN);
        packet->pts = packet->dts = probe->pending.front(); packet->flags = AV_PKT_FLAG_KEY; probe->pending.pop_front();
        return 0;
    };
    return calls;
}
template<class Predicate> bool wait_health(RecordingCapture& capture, Predicate predicate, std::chrono::milliseconds timeout) {
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline) { if (predicate(capture.health())) return true; std::this_thread::sleep_for(10ms); }
    return predicate(capture.health());
}
std::string pressure_state(const RecordingCaptureHealth& h) {
    return "drops=" + std::to_string(h.backpressure_drops) + " busy=" + std::to_string(h.encoder_busy_drops) + " retained=" + std::to_string(h.retained_pressure_drops) +
        " pool=" + std::to_string(h.pool_pressure_drops) + " stalls=" + std::to_string(h.encoder_stall_recoveries) + " fallbacks=" + std::to_string(h.gpu_conversion_fallbacks) +
        " generation=" + std::to_string(h.generation) + " allocated=" + std::to_string(h.surfaces_allocated) + "/" + std::to_string(h.surface_capacity) + " error=" + h.error;
}
// Every planned surface held by the encoder is pool backpressure: counted
// drops, no CPU conversion fallback, no growth, no restart.
void pool_backpressure(){
    auto probe = std::make_shared<PressureProbe>(); probe->hold = true;
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_nvenc",false,true}};
    dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t){return std::make_unique<VideoEncoder>(value,probe_calls(probe));};
    std::atomic<uint64_t> packets{0};RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet,int64_t,bool){++packets;};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    if(!wait_health(capture,[](const auto& h){return h.pool_pressure_drops>=3;},3s))throw std::runtime_error("No pool backpressure: "+pressure_state(capture.health()));
    auto pressured=capture.health();
    if(pressured.gpu_conversion_fallbacks||pressured.restart_required||!pressured.error.empty()||pressured.surfaces_allocated>pressured.surface_capacity)
        throw std::runtime_error("Pool pressure misclassified: "+pressure_state(pressured));
    CHECK(pressured.surface_capacity==7&&pressured.backpressure_wait_max_ms<40);
    probe->release();const auto resumed=packets.load();
    if(!wait_health(capture,[&](const auto&){return packets.load()>=resumed+30;},3s))throw std::runtime_error("Pool pressure did not clear: "+pressure_state(capture.health()));
    CHECK(capture.stop());const auto health=capture.health();
    if(!health.error.empty()||health.encoder_stall_recoveries||health.gpu_conversion_fallbacks||health.generation!=1)throw std::runtime_error("Pool pressure recovery: "+pressure_state(health));
}
// EAGAIN the encoder cannot drain is a bounded wait and a counted drop.
void busy_backpressure(){
    auto probe = std::make_shared<PressureProbe>();
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_nvenc",false,true}};
    dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t){return std::make_unique<VideoEncoder>(value,probe_calls(probe));};
    std::atomic<uint64_t> packets{0};RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet,int64_t,bool){++packets;};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    CHECK(wait_health(capture,[&](const auto&){return packets.load()>=30;},5s));
    { std::lock_guard lock(probe->mutex); probe->busy=true; }
    if(!wait_health(capture,[](const auto& h){return h.encoder_busy_drops>=5;},3s))throw std::runtime_error("No busy backpressure: "+pressure_state(capture.health()));
    auto pressured=capture.health();
    if(pressured.restart_required||!pressured.error.empty()||pressured.generation!=1)throw std::runtime_error("Busy pressure escalated: "+pressure_state(pressured));
    CHECK(pressured.backpressure_wait_max_ms>=10&&pressured.backpressure_wait_max_ms<60);
    { std::lock_guard lock(probe->mutex); probe->busy=false; }
    const auto resumed=packets.load();
    if(!wait_health(capture,[&](const auto&){return packets.load()>=resumed+30;},3s))throw std::runtime_error("Busy pressure did not clear: "+pressure_state(capture.health()));
    CHECK(capture.stop());const auto health=capture.health();
    if(!health.error.empty()||health.encoder_stall_recoveries||health.generation!=1)throw std::runtime_error("Busy pressure recovery: "+pressure_state(health));
}
// An encoder that accepts frames but never outputs: retained-budget drops,
// then after the stall bound it is replaced by the next candidate.
void stuck_encoder_recovery(){
    auto probe = std::make_shared<PressureProbe>(); probe->stuck = true;
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;config.nvenc_delay=4;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_nvenc",false,true},{"h264_nvenc",false,true}};
    dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t candidate){
        return candidate==0?std::make_unique<VideoEncoder>(value,probe_calls(probe)):std::make_unique<VideoEncoder>(value);};
    std::atomic<uint64_t> second{0};RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto generation,Packet,int64_t,bool){if(generation->id==2)++second;};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    CHECK(wait_health(capture,[](const auto& h){return h.retained_pressure_drops>=5;},3s));
    auto pressured=capture.health();
    if(pressured.restart_required||pressured.generation!=1||pressured.submitted!=5||pressured.max_in_flight!=5)throw std::runtime_error("Retained pressure escalated early: "+pressure_state(pressured));
    if(!wait_health(capture,[&](const auto&){return second.load()>=30;},6s))throw std::runtime_error("Stuck encoder not replaced: "+pressure_state(capture.health()));
    CHECK(capture.stop());const auto health=capture.health();
    if(!health.error.empty()||health.encoder_stall_recoveries!=1||health.generation!=2)throw std::runtime_error("Stuck encoder recovery: "+pressure_state(health));
}
// With no candidate left, a genuinely stuck encoder still ends in a restart,
// but only after the stall bound, never on the first exhausted frame.
void stuck_encoder_without_fallback(){
    auto probe = std::make_shared<PressureProbe>(); probe->stuck = true;
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;config.nvenc_delay=4;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_nvenc",false,true}};
    dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t){return std::make_unique<VideoEncoder>(value,probe_calls(probe));};
    RecordingCaptureCallbacks callbacks;
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    CHECK(wait_health(capture,[](const auto& h){return h.retained_pressure_drops>=5;},3s));
    const auto early=capture.health();CHECK(!early.restart_required&&early.submitted==5&&early.surfaces_allocated<=early.surface_capacity);
    if(!wait_health(capture,[](const auto& h){return h.restart_required;},5s))throw std::runtime_error("Stuck encoder never escalated: "+pressure_state(capture.health()));
    const auto health=capture.health();
    CHECK(health.error.find("No compatible recording encoder available")!=std::string::npos);
    capture.stop();
}

// AMF and QSV cannot open on this NVIDIA machine. An NVENC (or libx264)
// context stands in for the encoder while RecordingCapture runs the real AMF
// or QSV plan: options, flags, frame-context ownership, pool and in-flight
// limits, packet right-sizing and backpressure.
std::unique_ptr<VideoEncoder> stand_in_encoder(VideoEncoderConfig value,CodecCalls calls={}){
    const bool hardware=value.hardware_frames!=nullptr;
    value.name=hardware?"h264_nvenc":"libx264";value.resource_options.clear();value.codec_flags=0;
    return std::make_unique<VideoEncoder>(value,std::move(calls));
}
struct AmfRun{RecordingCaptureHealth health;std::vector<size_t> attempted;std::vector<VideoEncoderConfig> opened;};
AmfRun amf_run(std::optional<uint32_t> adapter){
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_amf",false,true},{"h264_amf",false,false}};dependencies.adapter_vendor=adapter;
    AmfRun run;std::atomic<uint64_t> packets{0};
    dependencies.open_encoder=[&](const VideoEncoderConfig& value,size_t candidate){run.attempted.push_back(candidate);run.opened.push_back(value);return stand_in_encoder(value);};
    RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet,int64_t,bool){++packets;};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    if(!wait_health(capture,[&](const auto&){return packets.load()>=60;},5s))throw std::runtime_error("AMF plan run stalled: "+pressure_state(capture.health()));
    CHECK(capture.stop());run.health=capture.health();if(!run.health.error.empty())throw std::runtime_error(run.health.error);
    return run;
}
void amf_zero_copy_plan(){
    const auto confirmed=amf_run(kAdapterVendorAmd);
    CHECK(confirmed.attempted==std::vector<size_t>({0}));
    const auto& opened=confirmed.opened.front();
    CHECK(opened.name=="h264_amf"&&opened.hardware_frames&&opened.require_encoder_frames&&(opened.codec_flags&AV_CODEC_FLAG_LOW_DELAY));
    CHECK((opened.resource_options==std::vector<std::pair<std::string,std::string>>{{"async_depth","4"},{"bf","0"},{"preanalysis","0"}}));
    const auto& h=confirmed.health;
    CHECK(h.encoder_planned&&h.encoder_vendor=="amd"&&h.zero_copy_status=="confirmed"&&h.hardware_input&&h.processing_path=="d3d11-video-processor");
    CHECK(h.encoder_slots==4&&h.max_in_flight==4&&h.surface_capacity==6&&h.pool_capacity==6);
    CHECK(h.surfaces_allocated<=6&&h.surfaces_in_use_peak<=4&&h.gpu_conversion_fallbacks==0);
    CHECK(h.readback_staging_slots==0&&h.readback_cpu_frames==0&&h.frame_allocations==0);
    // Unknown adapter: the real open is the probe; the plan stays unverified.
    const auto unknown=amf_run(0u);
    CHECK(unknown.attempted==std::vector<size_t>({0})&&unknown.health.zero_copy_status=="unverified"&&unknown.health.zero_copy_probe_passed);
    // Foreign adapter: zero-copy is skipped before opening; readback AMF records.
    const auto foreign=amf_run(kAdapterVendorNvidia);
    CHECK(foreign.attempted==std::vector<size_t>({1}));
    const auto& readback=foreign.opened.front();
    CHECK(readback.name=="h264_amf"&&!readback.hardware_frames&&!readback.require_encoder_frames&&(readback.codec_flags&AV_CODEC_FLAG_LOW_DELAY));
    CHECK((readback.resource_options==std::vector<std::pair<std::string,std::string>>{{"async_depth","4"},{"bf","0"},{"preanalysis","0"}}));
    CHECK(!foreign.health.hardware_input&&foreign.health.zero_copy_status=="not-used"&&foreign.health.pool_capacity==2&&foreign.health.surface_capacity==0);
    CHECK(foreign.health.processing_path=="d3d11-video-processor-readback"&&foreign.health.max_in_flight==4);
    CHECK(foreign.health.readback_staging_slots==2&&foreign.health.readback_cpu_frames==2&&foreign.health.frame_allocations==4);
}
// A frame from any other frames context is rejected before FFmpeg sees it,
// instead of reaching amfenc's av_assert0. The check is opt-in.
void amf_frame_context_ownership(){
    AVBufferRef* raw=nullptr;CHECK(av_hwdevice_ctx_create(&raw,AV_HWDEVICE_TYPE_D3D11VA,nullptr,nullptr,0)>=0);
    struct Unref{AVBufferRef* p;~Unref(){av_buffer_unref(&p);}} device{raw};
    auto frames=[&]{AVBufferRef* ref=av_hwframe_ctx_alloc(device.p);CHECK(ref);auto* ctx=reinterpret_cast<AVHWFramesContext*>(ref->data);
        ctx->format=AV_PIX_FMT_D3D11;ctx->sw_format=AV_PIX_FMT_NV12;ctx->width=256;ctx->height=144;
        static_cast<AVD3D11VAFramesContext*>(ctx->hwctx)->BindFlags=D3D11_BIND_RENDER_TARGET;CHECK(av_hwframe_ctx_init(ref)>=0);return ref;};
    Unref owned{frames()},foreign{frames()};
    auto frame_from=[](AVBufferRef* ctx){AVFrame* frame=av_frame_alloc();CHECK(frame&&av_hwframe_get_buffer(ctx,frame,0)>=0);return frame;};
    int sends=0;CodecCalls calls;calls.send=[&](AVCodecContext*,const AVFrame* frame){if(frame)++sends;return 0;};
    calls.receive=[](AVCodecContext*,AVPacket*){return AVERROR(EAGAIN);};
    for(const bool require:{true,false}){
        VideoEncoderConfig config;config.width=256;config.height=144;config.fps=60;config.bitrate_mbps=5;config.name="h264_nvenc";
        config.hardware_frames=owned.p;config.require_encoder_frames=require;sends=0;
        VideoEncoder encoder(config,calls);AVFrame* wrong=frame_from(foreign.p);AVFrame* right=frame_from(owned.p);
        bool rejected=false;try{encoder.try_submit(*wrong);}catch(const std::invalid_argument&){rejected=true;}
        CHECK(rejected==require&&sends==(require?0:1));
        CHECK(encoder.try_submit(*right).status==SubmitStatus::Accepted&&sends==(require?1:2));
        av_frame_free(&wrong);av_frame_free(&right);
    }
}
// Bounded backpressure uses the AMF plan's budgets: in-flight cap 4 and pool
// 6 at 60 fps, then stuck-encoder replacement by the next AMF candidate.
void amf_backpressure(){
    {
        auto probe=std::make_shared<PressureProbe>();probe->hold=true;
        RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;
        RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_amf",false,true}};dependencies.adapter_vendor=kAdapterVendorAmd;
        dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t){return stand_in_encoder(value,probe_calls(probe));};
        std::atomic<uint64_t> packets{0};RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet,int64_t,bool){++packets;};
        RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
        if(!wait_health(capture,[](const auto& h){return h.pool_pressure_drops>=3;},3s))throw std::runtime_error("No AMF pool backpressure: "+pressure_state(capture.health()));
        const auto pressured=capture.health();
        if(pressured.gpu_conversion_fallbacks||pressured.restart_required||pressured.surfaces_allocated>6||pressured.surface_capacity!=6)
            throw std::runtime_error("AMF pool pressure misclassified: "+pressure_state(pressured));
        probe->release();const auto resumed=packets.load();
        if(!wait_health(capture,[&](const auto&){return packets.load()>=resumed+30;},3s))throw std::runtime_error("AMF pool pressure did not clear: "+pressure_state(capture.health()));
        CHECK(capture.stop());CHECK(capture.health().error.empty()&&capture.health().encoder_stall_recoveries==0);
    }
    auto probe=std::make_shared<PressureProbe>();probe->stuck=true;
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_amf",false,true},{"h264_amf",false,true}};dependencies.adapter_vendor=kAdapterVendorAmd;
    dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t candidate){return candidate==0?stand_in_encoder(value,probe_calls(probe)):stand_in_encoder(value);};
    std::atomic<uint64_t> second{0};RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto generation,Packet,int64_t,bool){if(generation->id==2)++second;};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    CHECK(wait_health(capture,[](const auto& h){return h.retained_pressure_drops>=5;},3s));
    const auto pressured=capture.health();
    if(pressured.restart_required||pressured.submitted!=4||pressured.max_in_flight!=4)throw std::runtime_error("AMF retained pressure: "+pressure_state(pressured));
    if(!wait_health(capture,[&](const auto&){return second.load()>=30;},6s))throw std::runtime_error("Stuck AMF encoder not replaced: "+pressure_state(capture.health()));
    CHECK(capture.stop());const auto health=capture.health();
    if(!health.error.empty()||health.encoder_stall_recoveries!=1||health.generation!=2||health.encoder_vendor!="amd")throw std::runtime_error("AMF recovery: "+pressure_state(health));
}
uint32_t test_adapter_vendor(){
    Microsoft::WRL::ComPtr<ID3D11Device> device;
    CHECK(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,nullptr)));
    return d3d11_adapter_vendor(device.Get());
}
struct BufferRef{AVBufferRef* p=nullptr;~BufferRef(){av_buffer_unref(&p);}};
// D3D11 frames on the recording device stand in for derived QSV frames: the
// same individual render-target children, without an Intel runtime.
AVBufferRef* qsv_stand_in_frames(AVBufferRef* device,int width,int height){
    AVBufferRef* ref=av_hwframe_ctx_alloc(device);if(!ref)throw std::bad_alloc();
    auto* ctx=reinterpret_cast<AVHWFramesContext*>(ref->data);
    ctx->format=AV_PIX_FMT_D3D11;ctx->sw_format=AV_PIX_FMT_NV12;ctx->width=width;ctx->height=height;
    static_cast<AVD3D11VAFramesContext*>(ctx->hwctx)->BindFlags=D3D11_BIND_RENDER_TARGET;
    if(av_hwframe_ctx_init(ref)<0){av_buffer_unref(&ref);throw std::runtime_error("Stand-in QSV frames unavailable");}
    return ref;
}
// Receives every packet into a VBV-sized allocation with only `size` shrunk,
// as qsvenc does (3.1 MB at 25 Mbps).
CodecCalls vbv_packets(CodecCalls calls={}){
    calls.receive=[receive=calls.receive](AVCodecContext* context,AVPacket* packet){
        const int result=receive(context,packet);if(result<0)return result;
        AVPacket* inflated=av_packet_alloc();
        if(!inflated||av_new_packet(inflated,3125000)<0){av_packet_free(&inflated);return AVERROR(ENOMEM);}
        if(packet->size)std::memcpy(inflated->data,packet->data,size_t(packet->size));
        inflated->size=packet->size;const int copied=av_packet_copy_props(inflated,packet);
        av_packet_unref(packet);av_packet_move_ref(packet,inflated);av_packet_free(&inflated);
        return copied;
    };
    return calls;
}
bool exact_size(const AVPacket& packet){return packet.buf&&packet.buf->size==size_t(packet.size)+AV_INPUT_BUFFER_PADDING_SIZE;}
// Pool surfaces must be distinct NV12 render targets: individual textures
// report slice 0 and array slices their index. Aliased surfaces (FFmpeg's
// fixed QSV pool presents every array slice as slice 0), non-render-target,
// undersized and out-of-range surfaces are refused. NVIDIA refuses NV12
// render-target arrays, so slice indices come from a decoder array.
void qsv_surface_mapping(){
    BufferRef device;CHECK(av_hwdevice_ctx_create(&device.p,AV_HWDEVICE_TYPE_D3D11VA,nullptr,nullptr,0)>=0);
    auto frames=[&](int pool,UINT bind){AVBufferRef* ref=av_hwframe_ctx_alloc(device.p);CHECK(ref);auto* ctx=reinterpret_cast<AVHWFramesContext*>(ref->data);
        ctx->format=AV_PIX_FMT_D3D11;ctx->sw_format=AV_PIX_FMT_NV12;ctx->width=256;ctx->height=144;ctx->initial_pool_size=pool;
        static_cast<AVD3D11VAFramesContext*>(ctx->hwctx)->BindFlags=bind;CHECK(av_hwframe_ctx_init(ref)>=0);return ref;};
    std::vector<AVFrame*> held;
    auto targets_of=[&](AVBufferRef* ctx,int count){std::vector<CaptureSurfaceTarget> result;
        for(int i=0;i<count;++i){AVFrame* frame=av_frame_alloc();CHECK(frame&&av_hwframe_get_buffer(ctx,frame,0)>=0);held.push_back(frame);result.push_back(capture_surface_target(*frame));}
        return result;};
    auto refused=[](const std::vector<CaptureSurfaceTarget>& targets,int width,int height){
        try{capture_check_render_targets(targets,width,height);}catch(const std::runtime_error&){return true;}return false;};
    BufferRef singles{frames(0,D3D11_BIND_RENDER_TARGET)},array{frames(3,D3D11_BIND_DECODER)},sampled{frames(0,D3D11_BIND_SHADER_RESOURCE)};
    const auto individual=targets_of(singles.p,3);
    CHECK(individual[0].texture!=individual[1].texture&&individual[1].texture!=individual[2].texture&&individual[0].texture!=individual[2].texture);
    CHECK(individual[0].slice==0&&individual[1].slice==0&&individual[2].slice==0);
    capture_check_render_targets(individual,256,144);
    const auto slices=targets_of(array.p,3);
    // The preallocated pool hands slices back in LIFO order.
    CHECK(slices[0].texture==slices[1].texture&&slices[1].texture==slices[2].texture&&
        (std::set<unsigned>{slices[0].slice,slices[1].slice,slices[2].slice}==std::set<unsigned>{0,1,2}));
    CHECK(refused(slices,256,144));
    CHECK(refused({individual[0],individual[1],individual[0]},256,144));
    CHECK(refused(targets_of(sampled.p,1),256,144));
    CHECK(refused(individual,256,160)&&refused(individual,272,144));
    auto outside=individual;outside[2].slice=1;CHECK(refused(outside,256,144));
    CHECK(refused({CaptureSurfaceTarget{}},256,144));
    AVFrame software{};software.format=AV_PIX_FMT_NV12;
    bool rejected=false;try{capture_surface_target(software);}catch(const std::invalid_argument&){rejected=true;}CHECK(rejected);
    for(auto* frame:held)av_frame_free(&frame);
    // qsvenc takes only QSV frames: D3D11 frames are refused before FFmpeg opens it.
    for(const auto name:{"h264_qsv","av1_qsv"}){
        VideoEncoderConfig config;config.width=256;config.height=144;config.fps=60;config.bitrate_mbps=5;config.name=name;config.hardware_frames=singles.p;
        bool wrong=false;try{VideoEncoder encoder(config);}catch(const std::invalid_argument& error){wrong=std::string(error.what()).find("QSV")!=std::string::npos;}
        CHECK(wrong);
    }
}
// The real derivation from a recording D3D11VA device. Without an Intel QSV
// runtime behind the device's adapter it fails cleanly; on Intel it yields
// QSV frames whose D3D11 children are distinct, 16-row aligned render targets
// on the capture device itself. Returns whether derivation succeeded.
bool derived_qsv_frames(ID3D11Device* d3d){
    CHECK(av_hwdevice_find_type_by_name("qsv")==AV_HWDEVICE_TYPE_QSV);
    CHECK(avcodec_find_encoder_by_name("h264_qsv")&&avcodec_find_encoder_by_name("av1_qsv"));
    BufferRef device{av_hwdevice_ctx_alloc(AV_HWDEVICE_TYPE_D3D11VA)};CHECK(device.p);
    auto* hw=static_cast<AVD3D11VADeviceContext*>(reinterpret_cast<AVHWDeviceContext*>(device.p->data)->hwctx);hw->device=d3d;d3d->AddRef();
    CHECK(av_hwdevice_ctx_init(device.p)>=0);
    const bool intel=d3d11_adapter_vendor(d3d)==kAdapterVendorIntel;
    BufferRef frames;
    try{frames.p=capture_create_qsv_frames(device.p,1920,1080);}
    catch(const std::runtime_error& error){
        if(intel)throw;
        std::cout<<"QSV derivation refused on a non-Intel adapter: "<<error.what()<<"\n";return false;
    }
    CHECK(intel);
    const auto* ctx=reinterpret_cast<const AVHWFramesContext*>(frames.p->data);CHECK(ctx->format==AV_PIX_FMT_QSV&&ctx->sw_format==AV_PIX_FMT_NV12);
    std::vector<AVFrame*> held;std::vector<CaptureSurfaceTarget> targets;
    for(int i=0;i<9;++i){
        AVFrame* frame=av_frame_alloc();CHECK(frame&&av_hwframe_get_buffer(frames.p,frame,0)>=0&&frame->format==AV_PIX_FMT_QSV&&frame->data[3]);held.push_back(frame);
        AVFrame* mapped=av_frame_alloc();CHECK(mapped);mapped->format=AV_PIX_FMT_D3D11;
        CHECK(av_hwframe_map(mapped,frame,AV_HWFRAME_MAP_WRITE|AV_HWFRAME_MAP_OVERWRITE)>=0);
        targets.push_back(capture_surface_target(*mapped));av_frame_free(&mapped);
    }
    capture_check_render_targets(targets,1920,1080);
    D3D11_TEXTURE2D_DESC desc{};targets[0].texture->GetDesc(&desc);CHECK(desc.Width==1920&&desc.Height==1088);
    Microsoft::WRL::ComPtr<ID3D11Device> owner;targets[0].texture->GetDevice(&owner);CHECK(owner.Get()==d3d);
    for(auto* frame:held)av_frame_free(&frame);
    return true;
}
void qsv_derivation(){
    Microsoft::WRL::ComPtr<ID3D11Device> device;
    CHECK(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,nullptr)));
    CHECK(derived_qsv_frames(device.Get())==(d3d11_adapter_vendor(device.Get())==kAdapterVendorIntel));
}
struct QsvRun{RecordingCaptureHealth health;std::vector<size_t> attempted;std::vector<VideoEncoderConfig> opened;uint64_t packets=0,oversized=0;};
// A QSV zero-copy candidate then its readback form. stand_in_frames swaps
// the derived QSV frames for D3D11 frames; without it the real derivation runs.
QsvRun qsv_run(std::optional<uint32_t> adapter,bool stand_in_frames=true){
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_qsv",true,true},{"h264_qsv",true,false}};dependencies.adapter_vendor=adapter;
    if(stand_in_frames)dependencies.qsv_frames=qsv_stand_in_frames;
    QsvRun run;std::mutex mutex;
    dependencies.open_encoder=[&](const VideoEncoderConfig& value,size_t candidate){run.attempted.push_back(candidate);run.opened.push_back(value);return stand_in_encoder(value,vbv_packets());};
    RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet packet,int64_t,bool){std::lock_guard lock(mutex);++run.packets;if(!exact_size(*packet))++run.oversized;};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    if(!wait_health(capture,[&](const auto&){std::lock_guard lock(mutex);return run.packets>=60;},5s))throw std::runtime_error("QSV plan run stalled: "+pressure_state(capture.health()));
    CHECK(capture.stop());run.health=capture.health();if(!run.health.error.empty())throw std::runtime_error(run.health.error);
    return run;
}
void qsv_zero_copy_plan(){
    const auto confirmed=qsv_run(kAdapterVendorIntel);
    CHECK(confirmed.attempted==std::vector<size_t>({0}));
    const auto& opened=confirmed.opened.front();
    CHECK(opened.name=="h264_qsv"&&opened.low_power&&opened.hardware_frames&&opened.require_encoder_frames&&opened.right_size_packets&&opened.codec_flags==0);
    CHECK((opened.resource_options==std::vector<std::pair<std::string,std::string>>{{"async_depth","4"}}));
    const auto& h=confirmed.health;
    CHECK(h.encoder_planned&&h.encoder_vendor=="intel"&&h.zero_copy_status=="confirmed"&&h.hardware_input&&h.processing_path=="d3d11-video-processor-qsv");
    CHECK(h.encoder_slots==4&&h.max_in_flight==4&&h.output_delay_frames==4&&h.surface_capacity==7&&h.pool_capacity==7);
    CHECK(h.surfaces_allocated<=7&&h.surfaces_in_use_peak<=4&&h.gpu_conversion_fallbacks==0);
    CHECK(h.readback_staging_slots==0&&h.readback_cpu_frames==0&&h.frame_allocations==0);
    // Only exact-size packets reach history: payload plus FFmpeg's padding.
    CHECK(confirmed.oversized==0&&h.packet_buffer_bytes==h.packet_payload_bytes+h.encoded*AV_INPUT_BUFFER_PADDING_SIZE);
    // Unknown adapter: the real open is the probe; the plan stays unverified.
    const auto unknown=qsv_run(0u);
    CHECK(unknown.attempted==std::vector<size_t>({0})&&unknown.health.zero_copy_status=="unverified"&&unknown.health.zero_copy_probe_passed);
    // Foreign adapter: zero-copy is skipped before initialisation and readback QSV records.
    const auto foreign=qsv_run(kAdapterVendorNvidia);
    CHECK(foreign.attempted==std::vector<size_t>({1}));
    const auto& readback=foreign.opened.front();
    CHECK(readback.name=="h264_qsv"&&!readback.hardware_frames&&!readback.require_encoder_frames&&readback.right_size_packets);
    CHECK((readback.resource_options==std::vector<std::pair<std::string,std::string>>{{"async_depth","4"}}));
    CHECK(!foreign.health.hardware_input&&foreign.health.zero_copy_status=="not-used"&&foreign.health.pool_capacity==2&&foreign.health.surface_capacity==0);
    CHECK(foreign.health.processing_path=="d3d11-video-processor-readback"&&foreign.health.max_in_flight==4&&foreign.oversized==0);
    CHECK(foreign.health.readback_staging_slots==2&&foreign.health.readback_cpu_frames==2&&foreign.health.frame_allocations==4);
    // The real derivation on a non-Intel capture device fails during
    // initialisation, before any encoder opens, and readback QSV follows,
    // whether the adapter was unknown or wrongly reported as Intel.
    if(test_adapter_vendor()!=kAdapterVendorIntel)for(const uint32_t adapter:{0u,kAdapterVendorIntel}){
        const auto failed=qsv_run(adapter,false);
        CHECK(failed.attempted==std::vector<size_t>({1})&&!failed.health.hardware_input&&failed.health.zero_copy_status=="not-used");
        CHECK(failed.health.gpu_conversion_fallbacks==0&&failed.oversized==0);
    }
}
// Bounded backpressure uses the QSV plan's budgets: pool 7 and in-flight cap
// 4 at 60 fps, then stuck-encoder replacement by the next QSV zero-copy candidate.
void qsv_backpressure(){
    {
        auto probe=std::make_shared<PressureProbe>();probe->hold=true;
        RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;
        RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_qsv",true,true}};dependencies.adapter_vendor=kAdapterVendorIntel;
        dependencies.qsv_frames=qsv_stand_in_frames;
        dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t){return stand_in_encoder(value,probe_calls(probe));};
        std::atomic<uint64_t> packets{0};RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet,int64_t,bool){++packets;};
        RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
        if(!wait_health(capture,[](const auto& h){return h.pool_pressure_drops>=3;},3s))throw std::runtime_error("No QSV pool backpressure: "+pressure_state(capture.health()));
        const auto pressured=capture.health();
        if(pressured.gpu_conversion_fallbacks||pressured.restart_required||pressured.surfaces_allocated>7||pressured.surface_capacity!=7)
            throw std::runtime_error("QSV pool pressure misclassified: "+pressure_state(pressured));
        probe->release();const auto resumed=packets.load();
        if(!wait_health(capture,[&](const auto&){return packets.load()>=resumed+30;},3s))throw std::runtime_error("QSV pool pressure did not clear: "+pressure_state(capture.health()));
        CHECK(capture.stop());CHECK(capture.health().error.empty()&&capture.health().encoder_stall_recoveries==0);
    }
    auto probe=std::make_shared<PressureProbe>();probe->stuck=true;
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_qsv",true,true},{"h264_qsv",false,true}};dependencies.adapter_vendor=kAdapterVendorIntel;
    dependencies.qsv_frames=qsv_stand_in_frames;
    dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t candidate){
        return candidate==0?stand_in_encoder(value,probe_calls(probe)):stand_in_encoder(value,vbv_packets());};
    std::atomic<uint64_t> second{0},oversized{0};RecordingCaptureCallbacks callbacks;
    callbacks.packet=[&](auto generation,Packet packet,int64_t,bool){if(generation->id==2){++second;if(!exact_size(*packet))++oversized;}};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    CHECK(wait_health(capture,[](const auto& h){return h.retained_pressure_drops>=5;},3s));
    const auto pressured=capture.health();
    if(pressured.restart_required||pressured.submitted!=4||pressured.max_in_flight!=4)throw std::runtime_error("QSV retained pressure: "+pressure_state(pressured));
    if(!wait_health(capture,[&](const auto&){return second.load()>=30;},6s))throw std::runtime_error("Stuck QSV encoder not replaced: "+pressure_state(capture.health()));
    CHECK(capture.stop());const auto health=capture.health();
    if(!health.error.empty()||health.encoder_stall_recoveries!=1||health.generation!=2||health.encoder_vendor!="intel"||
        health.processing_path!="d3d11-video-processor-qsv"||oversized.load())throw std::runtime_error("QSV recovery: "+pressure_state(health));
}
size_t decode_all(const CaptureGeneration& generation,const std::vector<Packet>& packets,int width,int height){
    CodecContext decoder(avcodec_alloc_context3(avcodec_find_decoder(generation.codec->codec_id)));
    CHECK(decoder&&avcodec_parameters_to_context(decoder.get(),generation.codec.get())==0&&avcodec_open2(decoder.get(),decoder->codec,nullptr)==0);
    AVFrame* frame=av_frame_alloc();CHECK(frame);size_t decoded=0;
    auto receive=[&]{while(avcodec_receive_frame(decoder.get(),frame)==0){CHECK(frame->width==width&&frame->height==height);++decoded;av_frame_unref(frame);}};
    for(const auto& packet:packets){CHECK(avcodec_send_packet(decoder.get(),packet.get())==0);receive();}
    CHECK(avcodec_send_packet(decoder.get(),nullptr)==0);receive();av_frame_free(&frame);
    return decoded;
}
// The readback stage's fixed resources: a staged frame comes back one call
// later with its own pixels and properties, always through the same two CPU
// frames and two staging textures. A held CPU frame is never overwritten, and
// a full stage refuses rather than grows.
void readback_stage_reuse(){
    BufferRef device;CHECK(av_hwdevice_ctx_create(&device.p,AV_HWDEVICE_TYPE_D3D11VA,nullptr,nullptr,0)>=0);
    auto* d3d=static_cast<AVD3D11VADeviceContext*>(reinterpret_cast<AVHWDeviceContext*>(device.p->data)->hwctx)->device;
    for(const auto format:{AV_PIX_FMT_NV12,AV_PIX_FMT_P010}){
        const bool deep=format==AV_PIX_FMT_P010;
        BufferRef frames{av_hwframe_ctx_alloc(device.p)};CHECK(frames.p);
        auto* ctx=reinterpret_cast<AVHWFramesContext*>(frames.p->data);ctx->format=AV_PIX_FMT_D3D11;ctx->sw_format=format;ctx->width=256;ctx->height=144;
        static_cast<AVD3D11VAFramesContext*>(ctx->hwctx)->BindFlags=D3D11_BIND_SHADER_RESOURCE;CHECK(av_hwframe_ctx_init(frames.p)>=0);
        // Luma and chroma encode the frame index, in 8 or 16 bits.
        auto luma=[&](int index){return deep?(64+index)<<6:16+index;};
        auto chroma=[&](int index){return deep?(512+index)<<6:100+index;};
        auto sample=[&](const AVFrame& frame,int plane,int x,int y){const auto* row=frame.data[plane]+size_t(y)*frame.linesize[plane];
            return deep?int(reinterpret_cast<const uint16_t*>(row)[x]):int(row[x]);};
        auto gpu_frame=[&](int index){
            OwnedFrame cpu(av_frame_alloc());CHECK(cpu);cpu->format=format;cpu->width=256;cpu->height=144;CHECK(av_frame_get_buffer(cpu.get(),0)>=0);
            for(int plane=0;plane<2;++plane)for(int y=0;y<(plane?72:144);++y){auto* row=cpu->data[plane]+size_t(y)*cpu->linesize[plane];
                for(int x=0;x<256;++x){const int value=plane?chroma(index):luma(index);if(deep)reinterpret_cast<uint16_t*>(row)[x]=uint16_t(value);else row[x]=uint8_t(value);}}
            OwnedFrame gpu(av_frame_alloc());CHECK(gpu&&av_hwframe_get_buffer(frames.p,gpu.get(),0)>=0&&av_hwframe_transfer_data(gpu.get(),cpu.get(),0)>=0);
            gpu->pts=1000*index;gpu->duration=16667;gpu->color_range=AVCOL_RANGE_MPEG;gpu->colorspace=AVCOL_SPC_BT709;
            CHECK(av_frame_new_side_data(gpu.get(),AV_FRAME_DATA_SEI_UNREGISTERED,20));
            return gpu;};
        auto matches=[&](const AVFrame& frame,int index){
            return frame.pts==1000*index&&frame.duration==16667&&frame.color_range==AVCOL_RANGE_MPEG&&frame.colorspace==AVCOL_SPC_BT709&&
                av_frame_get_side_data(&frame,AV_FRAME_DATA_SEI_UNREGISTERED)&&frame.format==format&&frame.width==256&&frame.height==144&&
                sample(frame,0,0,0)==luma(index)&&sample(frame,0,255,143)==luma(index)&&sample(frame,1,0,0)==chroma(index)&&sample(frame,1,255,71)==chroma(index);};
        ReadbackStage stage(d3d,256,144,format,2,2);
        CHECK(stage.allocations()==4&&stage.staging_slots()==2&&stage.cpu_frames()==2&&stage.pending()==0&&stage.device()==d3d);
        std::set<uint8_t*> payloads;
        for(int index=0;index<12;++index){
            {auto source=gpu_frame(index);stage.stage(*source);}
            if(index==0){CHECK(stage.pending()==1);continue;}
            CHECK(stage.pending()==2);
            // Frame N-1 comes back while frame N is still staged.
            auto result=stage.read();CHECK(result.frame&&stage.pending()==1&&matches(*result.frame,index-1));
            payloads.insert(result.frame->data[0]);
        }
        CHECK(payloads.size()<=2&&stage.allocations()==4);
        // Both CPU frames held: nothing is overwritten and the staged frame waits.
        auto held=stage.acquire(),other=stage.acquire();
        CHECK(held&&other&&!stage.acquire()&&!stage.cpu_frame_available()&&stage.cpu_frames_in_use()==2);
        CHECK(!stage.read().frame&&stage.pending()==1);
        {auto next=gpu_frame(12);stage.stage(*next);}CHECK(stage.pending()==2);
        bool full=false;{auto extra=gpu_frame(13);try{stage.stage(*extra);}catch(const std::logic_error&){full=true;}}CHECK(full);
        stage.drop_oldest();CHECK(stage.pending()==1);
        held.reset();CHECK(stage.cpu_frames_in_use()==1);
        auto last=stage.read();CHECK(last.frame&&matches(*last.frame,12)&&stage.pending()==0&&stage.allocations()==4);
    }
    // Without a device the stage serves CPU frames only.
    ReadbackStage cpu(nullptr,256,144,AV_PIX_FMT_NV12,0,2);
    CHECK(cpu.staging_slots()==0&&cpu.cpu_frames()==2&&cpu.allocations()==2&&cpu.acquire());
    bool refused=false;try{ReadbackStage invalid(nullptr,256,144,AV_PIX_FMT_NV12,2,2);}catch(const std::invalid_argument&){refused=true;}CHECK(refused);
}
struct ReadbackRun{RecordingCaptureHealth warm,paused,health;std::vector<Packet> packets;std::shared_ptr<const CaptureGeneration> generation;std::vector<VideoEncoderConfig> opened;uint64_t oversized=0;};
// A readback candidate through the real capture, pacing and encoding threads:
// warm up, run, pause (the idle flush reads back the last staged frame), stop.
ReadbackRun readback_run(std::vector<RecordingEncoderCandidate> candidates,std::optional<uint32_t> adapter,bool gpu,
    std::function<std::unique_ptr<VideoEncoder>(const VideoEncoderConfig&)> factory={},bool variable=false){
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;config.variable_frame_rate=variable;
    RecordingCaptureDependencies dependencies;dependencies.candidates=std::move(candidates);dependencies.adapter_vendor=adapter;
    ReadbackRun run;std::mutex mutex;
    dependencies.open_encoder=[&](const VideoEncoderConfig& value,size_t){run.opened.push_back(value);return factory?factory(value):std::make_unique<VideoEncoder>(value);};
    RecordingCaptureCallbacks callbacks;callbacks.generation=[&](auto generation){std::lock_guard lock(mutex);run.generation=generation;};
    callbacks.packet=[&](auto,Packet packet,int64_t,bool){std::lock_guard lock(mutex);if(!exact_size(*packet))++run.oversized;run.packets.push_back(std::move(packet));};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,gpu),std::move(dependencies));capture.start();
    std::this_thread::sleep_for(1500ms);run.warm=capture.health();
    std::this_thread::sleep_for(1500ms);capture.pause(true);
    // Encoders with an output delay may still hold packets; nothing may stay staged.
    if(!wait_health(capture,[](const auto& h){return h.paused&&h.readback_staging_in_use==0;},1s))
        throw std::runtime_error("Readback not flushed on pause: "+pressure_state(capture.health()));
    run.paused=capture.health();
    CHECK(capture.stop());run.health=capture.health();if(!run.health.error.empty())throw std::runtime_error(run.health.error);
    return run;
}
// Every packet belongs to a submitted frame, in pts order, and after warm-up
// the pipeline allocates nothing.
void check_readback_run(const ReadbackRun& run,int staging,const char* path){
    const auto& h=run.health;
    if(h.processing_path!=path||h.readback_staging_slots!=staging||h.readback_cpu_frames!=2||h.readback_staging_peak>staging||h.readback_cpu_frames_peak>2||
        h.frame_allocations!=uint64_t(staging+2)||run.warm.frame_allocations!=h.frame_allocations||h.readback_pressure_drops||h.gpu_conversion_fallbacks||h.hardware_input)
        throw std::runtime_error(std::string("Readback run ")+path+": staging="+std::to_string(h.readback_staging_slots)+" peak="+std::to_string(h.readback_staging_peak)+
            " cpu="+std::to_string(h.readback_cpu_frames)+" peak="+std::to_string(h.readback_cpu_frames_peak)+" allocations="+std::to_string(run.warm.frame_allocations)+
            "->"+std::to_string(h.frame_allocations)+" path="+h.processing_path+" "+pressure_state(h));
    CHECK(run.packets.size()==h.encoded&&h.encoded==h.submitted&&h.encoded>=120&&h.output_fps>0);
    for(size_t i=1;i<run.packets.size();++i)CHECK(run.packets[i]->pts>run.packets[i-1]->pts);
    CHECK(run.packets.front()->flags&AV_PKT_FLAG_KEY);
    if(staging)CHECK(h.readback_p50_ms>0&&h.readback_p95_ms>=h.readback_p50_ms);
}
void readback_pipeline(){
    // NVENC system-memory readback, CFR and VFR, decoded.
    for(const bool variable:{false,true}){
        const auto run=readback_run({{"h264_nvenc",false,false}},std::nullopt,true,{},variable);
        check_readback_run(run,2,"d3d11-video-processor-readback");
        CHECK(!run.opened.front().hardware_frames&&run.health.encoder_vendor=="nvidia"&&run.health.zero_copy_status=="not-used");
        CHECK(decode_all(*run.generation,run.packets,256,144)==run.packets.size());
        std::cout<<"NVENC readback "<<(variable?"VFR":"CFR")<<": output="<<run.health.output_fps<<" readbackP50="<<run.health.readback_p50_ms<<"ms p95="<<run.health.readback_p95_ms
            <<"ms mapWaitP95="<<run.health.readback_map_wait_p95_ms<<"ms stalls="<<run.health.readback_map_stalls<<" allocations="<<run.health.frame_allocations<<"\n";
    }
    // libx264 from GPU frames and from CPU frames (no staging textures).
    const auto x264=readback_run({{"libx264"}},std::nullopt,true);
    check_readback_run(x264,2,"d3d11-video-processor-readback");
    CHECK(x264.health.encoder_planned&&x264.health.encoder_vendor=="software"&&x264.health.surface_capacity==0&&x264.health.pool_capacity==2);
    CHECK(decode_all(*x264.generation,x264.packets,256,144)==x264.packets.size());
    check_readback_run(readback_run({{"libx264"}},std::nullopt,false),0,"cpu-convert");
    // AMF readback keeps its plan's options and flags on the shared pipeline.
    const auto amf=readback_run({{"h264_amf",false,false}},kAdapterVendorNvidia,true,[](const VideoEncoderConfig& value){return stand_in_encoder(value);});
    check_readback_run(amf,2,"d3d11-video-processor-readback");
    const auto& amf_config=amf.opened.front();
    CHECK(amf_config.name=="h264_amf"&&!amf_config.hardware_frames&&(amf_config.codec_flags&AV_CODEC_FLAG_LOW_DELAY)&&amf.health.encoder_vendor=="amd");
    CHECK((amf_config.resource_options==std::vector<std::pair<std::string,std::string>>{{"async_depth","4"},{"bf","0"},{"preanalysis","0"}}));
    // QSV readback still right-sizes VBV-sized packets.
    const auto qsv=readback_run({{"h264_qsv",true,false}},kAdapterVendorNvidia,true,[](const VideoEncoderConfig& value){return stand_in_encoder(value,vbv_packets());});
    check_readback_run(qsv,2,"d3d11-video-processor-readback");
    CHECK(qsv.opened.front().right_size_packets&&qsv.oversized==0&&qsv.health.encoder_vendor=="intel");
}
// An encoder holding every CPU frame: counted readback drops after a bounded
// wait, no new staging textures or CPU frames, no GPU fallback, no restart.
void readback_pressure(){
    auto probe=std::make_shared<PressureProbe>();probe->hold=true;
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{"libx264"}};
    dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t){return std::make_unique<VideoEncoder>(value,probe_calls(probe));};
    std::atomic<uint64_t> packets{0};RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet,int64_t,bool){++packets;};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    if(!wait_health(capture,[](const auto& h){return h.readback_pressure_drops>=3;},3s))throw std::runtime_error("No readback backpressure: "+pressure_state(capture.health()));
    const auto pressured=capture.health();
    if(pressured.gpu_conversion_fallbacks||pressured.restart_required||pressured.pool_pressure_drops||pressured.frame_allocations!=4||
        pressured.readback_cpu_frames_peak!=2||pressured.readback_staging_peak>2||pressured.backpressure_wait_max_ms>=40)
        throw std::runtime_error("Readback pressure misclassified: "+pressure_state(pressured)+" allocations="+std::to_string(pressured.frame_allocations));
    probe->release();const auto resumed=packets.load();
    if(!wait_health(capture,[&](const auto&){return packets.load()>=resumed+30;},3s))throw std::runtime_error("Readback pressure did not clear: "+pressure_state(capture.health()));
    CHECK(capture.stop());const auto health=capture.health();
    if(!health.error.empty()||health.encoder_stall_recoveries||health.generation!=1||health.frame_allocations!=4)throw std::runtime_error("Readback pressure recovery: "+pressure_state(health));
}
// Intel hardware only, run by hand with --qsv: real QSV zero-copy and
// readback encodes on every Intel adapter. Other machines report a skip.
int qsv_hardware(){
    Microsoft::WRL::ComPtr<IDXGIFactory1> factory;CHECK(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))));
    std::vector<Microsoft::WRL::ComPtr<IDXGIAdapter1>> adapters;
    for(UINT index=0;;++index){
        Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;if(factory->EnumAdapters1(index,&adapter)==DXGI_ERROR_NOT_FOUND)break;
        DXGI_ADAPTER_DESC1 desc{};adapter->GetDesc1(&desc);
        if(desc.VendorId==kAdapterVendorIntel&&!(desc.Flags&DXGI_ADAPTER_FLAG_SOFTWARE))adapters.push_back(adapter);
    }
    if(adapters.empty()){std::cout<<"No Intel adapter: QSV hardware checks skipped\n";return 0;}
    for(const auto& adapter:adapters){
        DXGI_ADAPTER_DESC1 desc{};adapter->GetDesc1(&desc);std::wcout<<L"Intel adapter: "<<desc.Description<<L"\n";
        {GeneratedSource probe(60,true,1920,1080,adapter.Get());CHECK(derived_qsv_frames(probe.d3d_device()));}
        for(const bool av1:{false,true})for(const bool zero_copy:{true,false}){
            const std::string name=av1?"av1_qsv":"h264_qsv",label=name+(zero_copy?" zero-copy":" readback");
            RecordingCaptureConfig config;config.width=1920;config.height=1080;config.fps=60;config.bitrate_mbps=25;
            RecordingCaptureDependencies dependencies;dependencies.candidates={{name,true,zero_copy},{name,false,zero_copy}};
            std::mutex mutex;std::vector<Packet> packets;std::shared_ptr<const CaptureGeneration> generation;uint64_t oversized=0;
            RecordingCaptureCallbacks callbacks;callbacks.generation=[&](auto value){std::lock_guard lock(mutex);generation=value;};
            callbacks.packet=[&](auto,Packet packet,int64_t,bool){std::lock_guard lock(mutex);if(!exact_size(*packet))++oversized;packets.push_back(std::move(packet));};
            RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true,1920,1080,adapter.Get()),std::move(dependencies));
            try{capture.start();}catch(const std::exception& error){if(!av1)throw;std::cout<<label<<" unavailable: "<<error.what()<<"\n";continue;}
            std::this_thread::sleep_for(3s);CHECK(capture.stop());const auto h=capture.health();
            if(!h.error.empty())throw std::runtime_error(label+": "+h.error);
            CHECK(h.encoder_vendor=="intel"&&h.hardware_input==zero_copy&&h.zero_copy_status==(zero_copy?"confirmed":"not-used"));
            CHECK(h.processing_path==(zero_copy?"d3d11-video-processor-qsv":"d3d11-video-processor-readback"));
            CHECK(oversized==0&&!packets.empty()&&(packets.front()->flags&AV_PKT_FLAG_KEY)&&h.gpu_conversion_fallbacks==0);
            const auto decoded=decode_all(*generation,packets,1920,1080);CHECK(decoded==packets.size());
            std::cout<<label<<": output="<<h.output_fps<<" fresh="<<h.unique_fps<<" completionP50="<<h.completion_p50_ms<<"ms p95="<<h.completion_p95_ms
                <<"ms pool="<<h.surface_capacity<<" allocated="<<h.surfaces_allocated<<" peak="<<h.surfaces_in_use_peak<<" maxInFlight="<<h.max_in_flight
                <<" drops="<<h.backpressure_drops<<" payload="<<h.packet_payload_bytes<<" buffer="<<h.packet_buffer_bytes<<" decoded="<<decoded<<"\n";
        }
    }
    return 0;
}
// WGC-like delivery: the game finishes frames at game_fps (jittered); each
// display refresh with a new game frame yields one frame stamped at that
// refresh and delivered after a callback delay. Microseconds.
struct Delivery { int64_t stamp, arrival; };
std::vector<Delivery> wgc_schedule(double game_fps,double refresh,double seconds,uint32_t seed){
    std::mt19937 rng(seed);std::uniform_real_distribution<double> render(-.15,.15),callback(200,1500);
    std::vector<double> completes;for(double t=0;t<seconds;t+=(1/game_fps)*(1+render(rng)))completes.push_back(t);
    std::vector<Delivery> frames;size_t next=0;int64_t shown=-1;
    for(int64_t v=0;double(v)/refresh<seconds;++v){
        const double vsync=double(v)/refresh;while(next<completes.size()&&completes[next]<=vsync)++next;
        if(int64_t(next)-1>shown){shown=int64_t(next)-1;const auto stamp=int64_t(vsync*1e6);frames.push_back({stamp,stamp+int64_t(callback(rng))});}
    }
    return frames;
}
// Replays the pacing selection offline, exactly as RecordingCapture does.
double simulated_fresh(const std::vector<Delivery>& frames,int target,double seconds,bool timestamp,int depth,uint32_t seed){
    std::mt19937 rng(seed);std::uniform_int_distribution<int> wake(-300,300);
    const double interval=1e6/target;std::deque<CaptureFrameStamp> queue;uint64_t consumed=0;size_t next=0;int fresh=0;
    for(int64_t k=1;double(k)*interval<seconds*1e6;++k){
        const int64_t tick=int64_t(double(k)*interval)+wake(rng);
        while(next<frames.size()&&frames[next].arrival<=tick){queue.push_back({next+1,frames[next].stamp});++next;
            while(queue.size()>size_t(depth))queue.pop_front();}
        if(!timestamp){if(!queue.empty()&&queue.back().sequence>consumed){consumed=queue.back().sequence;++fresh;}queue.clear();continue;}
        const std::vector<CaptureFrameStamp> view(queue.begin(),queue.end());
        if(const auto pick=capture_select_frame(view,consumed,tick-int64_t(std::llround(interval)))){
            consumed=view[*pick].sequence;++fresh;queue.erase(queue.begin(),queue.begin()+std::ptrdiff_t(*pick)+1);}
    }
    return fresh/seconds;
}
void frame_selection_policy(){
    // Nearest to target among unconsumed frames; ties prefer the older one.
    const std::vector<CaptureFrameStamp> queued{{3,1000},{4,2000},{5,3000}};
    CHECK(capture_select_frame(queued,2,2100)==std::optional<size_t>(1));
    CHECK(capture_select_frame(queued,2,2500)==std::optional<size_t>(1));
    CHECK(capture_select_frame(queued,4,1000)==std::optional<size_t>(2));
    CHECK(!capture_select_frame(queued,5,2000));CHECK(!capture_select_frame({},0,0));
    // Refresh relationships: target, display Hz, game fps.
    struct Case{int target;double refresh,game;};
    const Case cases[]={{60,60,60},{60,144,60},{60,144,90},{90,120,150},{90,144,150},{90,165,150},{90,240,150},{90,240,95},{90,240,90},
        {90,240,240},{120,144,200},{120,165,200},{120,240,200},{120,240,125}};
    for(const auto& c:cases){
        const double seconds=20;const auto frames=wgc_schedule(c.game,c.refresh,seconds,7);
        const double delivered=frames.size()/seconds,expected=std::min(delivered,double(c.target));
        const double newest=simulated_fresh(frames,c.target,seconds,false,1,11),selected=simulated_fresh(frames,c.target,seconds,true,2,11);
        std::cout<<"selection "<<c.target<<"fps@"<<c.refresh<<"Hz game="<<c.game<<": delivered="<<delivered<<" newest="<<newest<<" timestamp="<<selected<<"\n";
        if(selected<expected*.985-.5||selected+.2<newest)throw std::runtime_error("Timestamp selection lost fresh frames at "+std::to_string(c.target)+"fps/"+std::to_string(int(c.refresh))+"Hz");
    }
}
// Emulated WGC delivery through the real capture, pacing and encoder threads.
// A high-resolution timer keeps callback timing close to the schedule;
// sleep_for would batch deliveries on the default 15.6 ms Windows tick.
class DeliveredSource final : public RecordingFrameSource {
    std::vector<Delivery> frames_;size_t next_=0;int64_t base_;std::function<int64_t()> clock_;
    HANDLE timer_=CreateWaitableTimerExW(nullptr,nullptr,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS);
    void pause(int64_t microseconds){LARGE_INTEGER due{};due.QuadPart=-microseconds*10;
        if(timer_&&SetWaitableTimer(timer_,&due,0,nullptr,nullptr,FALSE))WaitForSingleObject(timer_,INFINITE);
        else std::this_thread::sleep_for(std::chrono::microseconds(microseconds));}
public:
    DeliveredSource(std::vector<Delivery> frames,int64_t base,std::function<int64_t()> clock):frames_(std::move(frames)),base_(base),clock_(std::move(clock)){}
    ~DeliveredSource()override{if(timer_)CloseHandle(timer_);}
    bool acquire(CapturePixels& pixels,std::chrono::milliseconds timeout)override{
        if(next_>=frames_.size()){pause(timeout.count()*1000);return false;}
        const auto wait=base_+frames_[next_].arrival-clock_();
        if(wait>0){if(wait>timeout.count()*1000){pause(timeout.count()*1000);return false;}pause(wait);}
        pixels.width=128;pixels.height=72;pixels.stride=512;pixels.bgra.assign(size_t(512)*72,uint8_t(next_));
        for(size_t i=3;i<pixels.bgra.size();i+=4)pixels.bgra[i]=255;
        pixels.timestamp_us=base_+frames_[next_].stamp;++next_;return true;
    }
    bool eligible()const override{return true;}
    const char* name()const override{return "emulated WGC delivery fixture";}
};
RecordingCaptureHealth delivered_run(const std::vector<Delivery>& frames,int fps,bool variable,const std::string& selection){
    const auto started=std::chrono::steady_clock::now();
    auto clock=[started]{return int64_t(std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now()-started).count())+10000000;};
    RecordingCaptureConfig config;config.width=128;config.height=72;config.fps=fps;config.cpu_encoder=true;config.variable_frame_rate=variable;
    config.frame_selection=selection;config.monotonic_anchor_us=10000000;
    RecordingCaptureDependencies dependencies;dependencies.monotonic_clock=clock;
    std::mutex mutex;int64_t previous=-1;bool ordered=true;uint64_t packets=0;
    RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet packet,int64_t,bool){std::lock_guard lock(mutex);ordered&=packet->pts>previous;previous=packet->pts;++packets;};
    RecordingCapture capture(config,callbacks,std::make_unique<DeliveredSource>(frames,clock()+100000,clock),std::move(dependencies));capture.start();
    // Average the two steady-state health windows after warm-up.
    RecordingCaptureHealth best;double fresh=0,duplicates=0,dropped=0;
    for(int second=0;second<4;++second){std::this_thread::sleep_for(1s);if(second>=2){best=capture.health();fresh+=best.unique_fps/2;duplicates+=best.duplicate_fps/2;dropped+=best.selection_dropped_fps/2;}}
    best.unique_fps=fresh;best.duplicate_fps=duplicates;best.selection_dropped_fps=dropped;
    CHECK(capture.stop());const auto health=capture.health();if(!health.error.empty())throw std::runtime_error(health.error);
    CHECK(ordered&&packets>0);best.frame_selection=health.frame_selection;best.source_queue_capacity=health.source_queue_capacity;
    return best;
}
void fresh_frame_delivery(){
    // 90 fps target, 240 Hz display, game slightly above target: every output
    // tick has a distinct source frame available.
    const auto frames=wgc_schedule(95,240,6,23);
    for(const bool variable:{false,true}){
        const auto selected=delivered_run(frames,90,variable,"timestamp");
        const auto newest=delivered_run(frames,90,variable,"newest");
        std::cout<<(variable?"VFR":"CFR")<<" 90fps@240Hz game=95: timestamp acquired="<<selected.input_fps<<" fresh="<<selected.unique_fps
            <<" duplicates="<<selected.duplicate_fps<<" dropped="<<selected.selection_dropped_fps<<" latencyP95="<<selected.capture_latency_p95_ms
            <<"ms; newest fresh="<<newest.unique_fps<<" duplicates="<<newest.duplicate_fps<<"\n";
        CHECK(selected.frame_selection=="timestamp"&&selected.source_queue_capacity==2&&newest.frame_selection=="newest");
        if(selected.unique_fps<86)throw std::runtime_error("Timestamp selection fresh FPS "+std::to_string(selected.unique_fps));
        CHECK(selected.source_queue_peak<=2);CHECK(selected.capture_latency_p95_ms<40);
    }
    // Other refresh relationships, CFR: 60 fps on 144 Hz and 120 fps on 240 Hz.
    struct Case{int target;double refresh,game;};
    for(const auto& c:{Case{60,144,70},Case{120,240,130}}){
        const auto selected=delivered_run(wgc_schedule(c.game,c.refresh,6,31),c.target,false,"timestamp");
        std::cout<<"CFR "<<c.target<<"fps@"<<c.refresh<<"Hz game="<<c.game<<": acquired="<<selected.input_fps<<" fresh="<<selected.unique_fps
            <<" duplicates="<<selected.duplicate_fps<<"\n";
        if(selected.unique_fps<c.target*.95)throw std::runtime_error("Timestamp selection fresh FPS "+std::to_string(selected.unique_fps)+" at "+std::to_string(c.target));
    }
}
// A BGRA (or FP16, every channel `level`) test texture; BGRA pixels hold
// x in blue, y in green and `value` in red.
Microsoft::WRL::ComPtr<ID3D11Texture2D> capture_texture(ID3D11Device* device,int width,int height,uint8_t value,bool fp16=false,float level=0){
    D3D11_TEXTURE2D_DESC desc{};desc.Width=width;desc.Height=height;desc.MipLevels=1;desc.ArraySize=1;desc.SampleDesc.Count=1;
    desc.Format=fp16?DXGI_FORMAT_R16G16B16A16_FLOAT:DXGI_FORMAT_B8G8R8A8_UNORM;desc.BindFlags=D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET;
    std::vector<uint16_t> half;std::vector<uint8_t> bgra;D3D11_SUBRESOURCE_DATA data{};
    if(fp16){half.assign(size_t(width)*height*4,DirectX::PackedVector::XMConvertFloatToHalf(level));
        for(size_t i=3;i<half.size();i+=4)half[i]=DirectX::PackedVector::XMConvertFloatToHalf(1);data={half.data(),UINT(width*8),0};}
    else{bgra.resize(size_t(width)*height*4);for(int y=0;y<height;++y)for(int x=0;x<width;++x){auto* p=&bgra[(size_t(y)*width+x)*4];p[0]=uint8_t(x);p[1]=uint8_t(y);p[2]=value;p[3]=255;}
        data={bgra.data(),UINT(width*4),0};}
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;CHECK(SUCCEEDED(device->CreateTexture2D(&desc,&data,&texture)));return texture;
}
std::vector<uint8_t> texture_bytes(const std::shared_ptr<ID3D11Texture2D>& texture){
    D3D11_TEXTURE2D_DESC desc{};texture->GetDesc(&desc);CapturePixels pixels;pixels.texture=texture;
    pixels.width=int(desc.Width);pixels.height=int(desc.Height);pixels.stride=pixels.width*4;capture_copy_texture_pixels(pixels);return pixels.bgra;
}
ULONG references(IUnknown* object){object->AddRef();return object->Release();}
// The owned-copy store behind WGC: one reused texture for frames taken at
// once, held copies never rewritten, untaken frames superseded in place,
// bounded and counted exhaustion, resize and HDR/SDR handling, region crops,
// and no reference kept to the capture API's buffer.
void captured_frame_store(){
    Microsoft::WRL::ComPtr<ID3D11Device> device;
    CHECK(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,nullptr)));
    const auto ten=capture_texture(device.Get(),256,144,10),twenty=capture_texture(device.Get(),256,144,20);
    const auto input_references=references(ten.Get());
    CapturedFrameStore store(device.Get(),3,80);
    auto take=[&](CapturedFrameStore& from,int64_t expected){CapturePixels pixels;int64_t stamp=-1;CHECK(from.take(pixels,stamp,0ms)&&stamp==expected);return pixels;};
    // Frames taken and released at once reuse a single texture: no
    // allocation after warm-up, and the WGC buffer is never retained.
    std::set<ID3D11Texture2D*> seen;
    for(int i=0;i<200;++i){CHECK(store.deliver(ten.Get(),i));seen.insert(take(store,i).texture.get());}
    auto stats=store.stats();
    CHECK(seen.size()==1&&stats.allocated==1&&stats.delivered==200&&stats.leased==0&&stats.peak_leased==1&&stats.capacity==3);
    CHECK(references(ten.Get())==input_references&&stats.copy_p95_ms>=stats.copy_p50_ms);
    // A held copy is never handed out again or rewritten while referenced.
    CHECK(store.deliver(ten.Get(),500));auto held=take(store,500);
    for(int i=0;i<50;++i){CHECK(store.deliver(twenty.Get(),600+i));const auto other=take(store,600+i);CHECK(other.texture.get()!=held.texture.get());}
    const auto held_bytes=texture_bytes(held.texture);CHECK(held_bytes[2]==10&&held_bytes[(size_t(143)*256+255)*4+2]==10);
    CHECK(store.stats().allocated==2&&store.stats().leased==1);
    // An untaken frame is superseded in place, in the same texture.
    const auto before=store.stats();
    CHECK(store.deliver(twenty.Get(),700)&&store.deliver(ten.Get(),701));
    auto latest=take(store,701);CHECK(texture_bytes(latest.texture)[2]==10);
    CHECK(store.stats().superseded==before.superseded+1&&store.stats().allocated==before.allocated);
    // Every texture held: the frame is dropped and counted, nothing allocated.
    CHECK(store.deliver(twenty.Get(),800));auto third=take(store,800);
    CHECK(store.stats().leased==3&&!store.deliver(ten.Get(),801));
    CHECK(store.stats().pressure_drops==1&&store.stats().allocated==3&&store.stats().leased==3);
    third.texture.reset();CHECK(store.deliver(ten.Get(),802)&&take(store,802).texture);
    // A new size rebuilds the pool; held old-size copies stay intact and are
    // discarded, not reused, when released.
    const auto large=capture_texture(device.Get(),320,180,30);
    CHECK(store.deliver(large.Get(),900));auto resized=take(store,900);CHECK(resized.width==320&&resized.height==180);
    CHECK(texture_bytes(held.texture)[2]==10);
    // An extra reference keeps the old address unique; the pool must drop its own.
    const Microsoft::WRL::ComPtr<ID3D11Texture2D> old_texture(held.texture.get());held=CapturePixels{};latest=CapturePixels{};
    CHECK(references(old_texture.Get())==1);
    for(int i=0;i<10;++i){CHECK(store.deliver(large.Get(),910+i));const auto next=take(store,910+i);CHECK(next.texture.get()!=old_texture.Get()&&next.width==320);}
    CHECK(store.stats().leased==1);resized=CapturePixels{};CHECK(store.stats().leased==0);
    // FP16 frames are tone-mapped exactly as before, BGRA frames copied, in
    // the same pooled textures and without retaining either input.
    const auto hdr=capture_texture(device.Get(),256,144,0,true,.5f);const auto hdr_references=references(hdr.Get());
    CapturedFrameStore mixed(device.Get(),3,80);
    const auto expected=texture_bytes(capture_tone_map_texture(hdr.Get(),80));
    CHECK(mixed.deliver(hdr.Get(),1));CHECK(texture_bytes(take(mixed,1).texture)==expected);
    CHECK(mixed.deliver(ten.Get(),2));CHECK(texture_bytes(take(mixed,2).texture)[2]==10);
    for(int i=3;i<20;++i){CHECK(mixed.deliver(i%2?hdr.Get():ten.Get(),i));take(mixed,i);}
    CHECK(mixed.stats().allocated==1&&references(hdr.Get())==hdr_references);
    // A capture region is cropped on delivery, SDR and HDR, with the HDR
    // scratch made once; a region outside the frame drops it.
    CapturedFrameStore region(device.Get(),3,80,{16,8,128,72});
    CHECK(region.deliver(ten.Get(),1));const auto crop=take(region,1);CHECK(crop.width==128&&crop.height==72);
    const auto crop_bytes=texture_bytes(crop.texture);CHECK(crop_bytes[0]==16&&crop_bytes[1]==8&&crop_bytes[2]==10);
    for(int i=2;i<22;++i){CHECK(region.deliver(hdr.Get(),i));const auto frame=take(region,i);CHECK(frame.width==128);}
    // The held first crop, one reused pool texture and the HDR scratch.
    CHECK(region.stats().allocated==3);
    CHECK(region.deliver(hdr.Get(),30));const auto hdr_crop=texture_bytes(take(region,30).texture);
    CHECK(hdr_crop.size()==size_t(128)*72*4&&std::equal(hdr_crop.begin(),hdr_crop.begin()+4,expected.begin()));
    CapturedFrameStore outside(device.Get(),3,80,{200,100,128,72});
    CHECK(!outside.deliver(ten.Get(),1)&&outside.stats().delivered==0&&outside.stats().allocated==0);
    // Errors wake take(); reset clears them; close ends it.
    store.fail("capture failed");bool threw=false;try{CapturePixels pixels;int64_t stamp=0;store.take(pixels,stamp,0ms);}catch(const std::runtime_error&){threw=true;}
    CHECK(threw);store.reset();{CapturePixels pixels;int64_t stamp=0;CHECK(!store.take(pixels,stamp,0ms));}
    CHECK(store.deliver(ten.Get(),1000));store.close();{CapturePixels pixels;int64_t stamp=0;CHECK(store.closed()&&!store.take(pixels,stamp,0ms)&&store.stats().leased==0);}
    bool invalid=false;try{CapturedFrameStore bad(device.Get(),0,80);}catch(const std::invalid_argument&){invalid=true;}CHECK(invalid);
}
// WGC-style delivery through the production CapturedFrameStore: a producer
// thread renders into one of three buffers (like WGC's frame pool) on the
// schedule and hands it to the store; acquire() takes frames exactly as
// WgcSource does.
class PooledWgcSource final:public RecordingFrameSource{
    Microsoft::WRL::ComPtr<ID3D11Device> device_;Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    std::vector<Microsoft::WRL::ComPtr<ID3D11Texture2D>> buffers_;std::vector<Microsoft::WRL::ComPtr<ID3D11RenderTargetView>> views_;
    std::unique_ptr<CapturedFrameStore> store_;std::atomic<bool> stop_{false},started_{false};std::thread producer_;
public:
    // The schedule starts at the first acquire(), once the encoder is open.
    PooledWgcSource(std::vector<Delivery> frames,std::function<int64_t()> clock,int capacity,int width=1280,int height=720){
        CHECK(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device_,nullptr,&context_)));
        store_=std::make_unique<CapturedFrameStore>(device_.Get(),capacity,80.f);
        for(int i=0;i<3;++i){buffers_.push_back(capture_texture(device_.Get(),width,height,0));views_.emplace_back();
            CHECK(SUCCEEDED(device_->CreateRenderTargetView(buffers_.back().Get(),nullptr,&views_.back())));}
        producer_=std::thread([this,frames=std::move(frames),clock=std::move(clock)]{
            while(!started_&&!stop_)std::this_thread::sleep_for(1ms);
            const int64_t base=clock()+50000;
            HANDLE timer=CreateWaitableTimerExW(nullptr,nullptr,CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,TIMER_ALL_ACCESS);
            for(size_t i=0;i<frames.size()&&!stop_;++i){
                const auto wait=base+frames[i].arrival-clock();
                if(wait>0){LARGE_INTEGER due{};due.QuadPart=-wait*10;if(timer&&SetWaitableTimer(timer,&due,0,nullptr,nullptr,FALSE))WaitForSingleObject(timer,INFINITE);}
                const float shade=float(i%200)/200;const float color[4]{shade,1-shade,.5f,1};
                context_->ClearRenderTargetView(views_[i%3].Get(),color);
                store_->deliver(buffers_[i%3].Get(),base+frames[i].stamp);
            }
            if(timer)CloseHandle(timer);});
    }
    ~PooledWgcSource()override{stop_=true;if(producer_.joinable())producer_.join();}
    bool acquire(CapturePixels& pixels,std::chrono::milliseconds timeout)override{
        started_=true;int64_t stamp=0;if(!store_->take(pixels,stamp,timeout))return false;pixels.timestamp_us=stamp;return true;}
    bool eligible()const override{return true;}
    const char* name()const override{return "emulated pooled WGC delivery";}
    ID3D11Device* d3d_device()const override{return device_.Get();}
    RecordingSourceHealth diagnostics()const override{
        const auto s=store_->stats();RecordingSourceHealth health;health.frames_delivered=s.delivered;health.overwritten=s.superseded;
        health.owned_texture_capacity=s.capacity;health.owned_textures_allocated=s.allocated;health.owned_textures_leased=s.leased;
        health.owned_textures_peak=s.peak_leased;health.owned_texture_pressure_drops=s.pressure_drops;health.copy_p50_ms=s.copy_p50_ms;health.copy_p95_ms=s.copy_p95_ms;
        return health;}
};
struct PooledRun{RecordingCaptureHealth warm,health;};
// One emulated WGC run through the real capture, pacing and encoding threads
// (NVENC zero-copy unless `candidates` says otherwise), averaged over the two
// steady-state health windows after warm-up.
PooledRun pooled_run(const std::vector<Delivery>& frames,int fps,bool variable,int capacity,RecordingCaptureDependencies dependencies={},double seconds=4){
    const auto started=std::chrono::steady_clock::now();
    auto clock=[started]{return int64_t(std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now()-started).count())+10000000;};
    RecordingCaptureConfig config;config.width=640;config.height=360;config.fps=fps;config.variable_frame_rate=variable;config.monotonic_anchor_us=10000000;
    dependencies.monotonic_clock=clock;
    RecordingCaptureCallbacks callbacks;
    RecordingCapture capture(config,callbacks,std::make_unique<PooledWgcSource>(frames,clock,capacity),std::move(dependencies));capture.start();
    PooledRun run;double fresh=0,duplicates=0,dropped=0,overwritten=0;int windows=0;
    for(int second=0;second<int(seconds);++second){std::this_thread::sleep_for(1s);
        if(second==1)run.warm=capture.health();
        if(second>=2){const auto h=capture.health();fresh+=h.unique_fps;duplicates+=h.duplicate_fps;dropped+=h.selection_dropped_fps;overwritten+=h.wgc_overwritten_fps;++windows;}}
    run.health=capture.health();capture.stop();
    if(!run.health.error.empty())throw std::runtime_error(run.health.error);
    if(windows){run.health.unique_fps=fresh/windows;run.health.duplicate_fps=duplicates/windows;run.health.selection_dropped_fps=dropped/windows;run.health.wgc_overwritten_fps=overwritten/windows;}
    return run;
}
std::string pooled_state(const RecordingCaptureHealth& h){
    const auto& s=h.source_details;
    return "fresh="+std::to_string(h.unique_fps)+" output="+std::to_string(h.output_fps)+" duplicates="+std::to_string(h.duplicate_fps)+
        " selectionDropped="+std::to_string(h.selection_dropped_fps)+" overwritten="+std::to_string(h.wgc_overwritten_fps)+" sourcePeak="+std::to_string(h.source_queue_peak)+
        " owned capacity="+std::to_string(s.owned_texture_capacity)+" allocated="+std::to_string(s.owned_textures_allocated)+" peak="+std::to_string(s.owned_textures_peak)+
        " pressureDrops="+std::to_string(s.owned_texture_pressure_drops)+" "+pressure_state(h);
}
// Measures how many owned copies are held at once (large pool, so nothing
// is refused): steady capture at 60/90/120 FPS, a stuck encoder until it is
// replaced, and every pool surface pinned by the encoder.
void measure_source_texture_holders(){
    struct Case{int target;double refresh,game;};
    for(const auto& c:{Case{60,144,70},Case{90,240,95},Case{120,240,130},Case{90,240,240}}){
        const auto run=pooled_run(wgc_schedule(c.game,c.refresh,6,41),c.target,false,32);
        std::cout<<"holders steady "<<c.target<<"fps@"<<c.refresh<<"Hz game="<<c.game<<": "<<pooled_state(run.health)<<"\n";
    }
    {
        auto probe=std::make_shared<PressureProbe>();probe->stuck=true;
        RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_nvenc",false,true},{"h264_nvenc",false,true}};
        dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t candidate){return candidate==0?std::make_unique<VideoEncoder>(value,probe_calls(probe)):std::make_unique<VideoEncoder>(value);};
        const auto run=pooled_run(wgc_schedule(95,240,7,43),90,false,32,std::move(dependencies),5);
        std::cout<<"holders stuck encoder 90fps: "<<pooled_state(run.health)<<"\n";
    }
    {
        auto probe=std::make_shared<PressureProbe>();probe->hold=true;
        RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_nvenc",false,true},{"h264_nvenc",false,true}};
        // The replacement releases what the probe pinned, as a destroyed encoder would.
        dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t candidate){
            if(candidate==0)return std::make_unique<VideoEncoder>(value,probe_calls(probe));probe->release();return std::make_unique<VideoEncoder>(value);};
        const auto run=pooled_run(wgc_schedule(95,240,7,47),90,false,32,std::move(dependencies),5);
        std::cout<<"holders pool backpressure 90fps: "<<pooled_state(run.health)<<"\n";
    }
}
// The pooled WGC path through the real pipeline at the production capacity:
// fresh frames at the target without duplicates or pool drops, source
// selection still two deep, and no per-frame allocation: a texture is made
// only when concurrent holders reach a new high, so allocations equal the
// peak lease count (at most two lazy top-ups after warm-up). A stuck encoder
// or a blocked writer is absorbed by counted drops, never by growth or a
// restart.
void pooled_wgc_capture(){
    const int capacity=capture_source_texture_capacity(2);
    auto regressed=[&](const PooledRun& run,int target){const auto& h=run.health;const auto& s=h.source_details;
        return h.unique_fps<target*.955||h.duplicate_fps>2||h.source_queue_peak>2||s.owned_texture_pressure_drops||s.owned_texture_capacity!=capacity||
            s.owned_textures_allocated!=uint64_t(s.owned_textures_peak)||s.owned_textures_allocated>run.warm.source_details.owned_textures_allocated+2||
            s.owned_textures_peak>capacity;};
    for(const bool variable:{false,true}){
        const auto run=pooled_run(wgc_schedule(95,240,7,53),90,variable,capacity);
        std::cout<<"pooled WGC "<<(variable?"VFR":"CFR")<<" 90fps@240Hz game=95: "<<pooled_state(run.health)<<"\n";
        if(regressed(run,90))throw std::runtime_error("Pooled WGC delivery regressed: "+pooled_state(run.health));
    }
    struct Case{int target;double refresh,game;};
    for(const auto& c:{Case{60,144,70},Case{120,240,130}}){
        const auto run=pooled_run(wgc_schedule(c.game,c.refresh,7,59),c.target,false,capacity);
        std::cout<<"pooled WGC CFR "<<c.target<<"fps@"<<c.refresh<<"Hz game="<<c.game<<": "<<pooled_state(run.health)<<"\n";
        if(regressed(run,c.target))throw std::runtime_error("Pooled WGC delivery regressed at "+std::to_string(c.target)+": "+pooled_state(run.health));
    }
    {
        auto probe=std::make_shared<PressureProbe>();probe->stuck=true;
        RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_nvenc",false,true},{"h264_nvenc",false,true}};
        dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t candidate){return candidate==0?std::make_unique<VideoEncoder>(value,probe_calls(probe)):std::make_unique<VideoEncoder>(value);};
        const auto run=pooled_run(wgc_schedule(95,240,7,61),90,false,capacity,std::move(dependencies),5);const auto& h=run.health;
        std::cout<<"pooled WGC stuck encoder: "<<pooled_state(h)<<"\n";
        if(h.encoder_stall_recoveries!=1||h.generation!=2||h.restart_required||h.source_details.owned_textures_allocated>uint64_t(capacity))
            throw std::runtime_error("Pooled WGC stuck encoder: "+pooled_state(h));
    }
    // A blocked packet writer backs pacing up with distinct frames until the
    // pool is exhausted: new WGC frames are dropped and counted, not
    // allocated, and capture resumes once the writer returns.
    const auto started=std::chrono::steady_clock::now();
    auto clock=[started]{return int64_t(std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now()-started).count())+10000000;};
    RecordingCaptureConfig config;config.width=640;config.height=360;config.fps=90;config.monotonic_anchor_us=10000000;
    RecordingCaptureDependencies dependencies;dependencies.monotonic_clock=clock;
    std::mutex mutex;std::condition_variable changed;bool blocked=true,entered=false;uint64_t packets=0;
    RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet,int64_t,bool){std::unique_lock lock(mutex);++packets;
        if(packets==20){entered=true;changed.notify_all();changed.wait(lock,[&]{return !blocked;});}};
    RecordingCapture capture(config,callbacks,std::make_unique<PooledWgcSource>(wgc_schedule(95,240,8,67),clock,capacity),std::move(dependencies));capture.start();
    {std::unique_lock lock(mutex);CHECK(changed.wait_for(lock,5s,[&]{return entered;}));}
    if(!wait_health(capture,[](const auto& h){return h.source_details.owned_texture_pressure_drops>=10;},3s))
        {{std::lock_guard lock(mutex);blocked=false;}changed.notify_all();throw std::runtime_error("No owned-texture pressure: "+pooled_state(capture.health()));}
    const auto pressured=capture.health();
    {std::lock_guard lock(mutex);blocked=false;}changed.notify_all();
    const auto resumed=[&]{std::lock_guard lock(mutex);return packets;}();
    if(!wait_health(capture,[&](const auto&){std::lock_guard lock(mutex);return packets>=resumed+60;},3s))throw std::runtime_error("Capture did not resume: "+pooled_state(capture.health()));
    CHECK(capture.stop());const auto health=capture.health();
    std::cout<<"pooled WGC blocked writer: "<<pooled_state(pressured)<<"\n";
    if(pressured.restart_required||!health.error.empty()||pressured.source_details.owned_textures_allocated>uint64_t(capacity)||pressured.source_details.owned_textures_peak>capacity)
        throw std::runtime_error("Owned-texture pressure escalated: "+pooled_state(health));
}
void gpu_generation_failover(){
    const auto base=std::filesystem::current_path();const auto root=base/(L"capture-generation-fixture-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64()));
    CHECK(std::filesystem::create_directory(root));
    struct Cleanup{std::filesystem::path base,path;~Cleanup(){if(path.parent_path()==base){std::error_code ignored;std::filesystem::remove_all(path,ignored);}}}cleanup{base,root};
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;config.nvenc_delay=4;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_nvenc",false,true},{"h264_nvenc",false,true}};
    std::vector<size_t> attempted;
    dependencies.open_encoder=[&](const VideoEncoderConfig& value,size_t candidate){
        attempted.push_back(candidate);CodecCalls calls;
        if(candidate==0){auto sent=std::make_shared<int>(0);calls.send=[sent](AVCodecContext* context,const AVFrame* frame){
            if(frame&&++*sent==45)return AVERROR_EXTERNAL;return avcodec_send_frame(context,frame);};}
        return std::make_unique<VideoEncoder>(value,std::move(calls));
    };
    std::mutex mutex;std::condition_variable changed;size_t second_packets=0;
    std::vector<std::shared_ptr<const CaptureGeneration>> generations;
    std::vector<std::filesystem::path> files;std::unique_ptr<FullSessionWriter> writer;
    RecordingCaptureCallbacks callbacks;
    callbacks.generation=[&](auto generation){
        if(writer){CHECK(writer->stop());CHECK(writer->status().error.empty());}
        const auto file=root/(L"generation-"+std::to_wstring(generation->id)+L".mkv");
        writer=std::make_unique<FullSessionWriter>(FullSessionConfig{file,{}},generation);
        generations.push_back(std::move(generation));files.push_back(file);
    };
    uint64_t packet_generation=0;
    callbacks.packet=[&](auto generation,Packet packet,int64_t,bool){
        if(packet_generation!=generation->id){CHECK(packet->flags&AV_PKT_FLAG_KEY);packet_generation=generation->id;}
        CHECK(writer->video(generation,*packet));
        if(generation->id==2){std::lock_guard lock(mutex);++second_packets;changed.notify_all();}
    };
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    {std::unique_lock lock(mutex);if(!changed.wait_for(lock,10s,[&]{return second_packets>=30;}))throw std::runtime_error("GPU failover did not deliver second generation: "+capture.health().error);}
    CHECK(capture.stop());if(!capture.health().error.empty())throw std::runtime_error(capture.health().error);
    CHECK(writer->stop());CHECK(writer->status().error.empty());CHECK(attempted==std::vector<size_t>({0,1}));CHECK(generations.size()==2);CHECK(files.size()==2);
    CHECK(generations[0]->codec.get()!=generations[1]->codec.get());
    for(const auto& generation:generations)CHECK(generation->codec->codec_id==AV_CODEC_ID_H264&&generation->codec->extradata_size>0);
    writer.reset();for(const auto& file:files)decode_saved_session(file);
}
// Manual benchmark: --readback-bench <encoder> <width> <height> <fps>. Forced
// readback from a generated 4K GPU source; one line of steady-state metrics
// measured over 8 s after a 3 s warm-up.
int readback_bench(const std::string& encoder,int width,int height,int fps){
    RecordingCaptureConfig config;config.width=width;config.height=height;config.fps=fps;config.bitrate_mbps=25;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{encoder,false,false}};
    std::atomic<uint64_t> packets{0};RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet,int64_t,bool){++packets;};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(fps,true,3840,2160),std::move(dependencies));capture.start();
    std::this_thread::sleep_for(3s);
    struct Sample{uint64_t cpu;DWORD faults;size_t private_ws,commit;};
    auto sample=[]{FILETIME created{},exited{},kernel{},user{};GetProcessTimes(GetCurrentProcess(),&created,&exited,&kernel,&user);
        auto ticks=[](FILETIME t){return (uint64_t(t.dwHighDateTime)<<32)|t.dwLowDateTime;};
        PROCESS_MEMORY_COUNTERS_EX2 memory{};GetProcessMemoryInfo(GetCurrentProcess(),reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory),sizeof(memory));
        return Sample{ticks(kernel)+ticks(user),memory.PageFaultCount,memory.PrivateWorkingSetSize,memory.PrivateUsage};};
    const auto warm=capture.health();const auto before=sample();const auto started=std::chrono::steady_clock::now();
    std::this_thread::sleep_for(8s);
    const auto after=sample();const double seconds=std::chrono::duration<double>(std::chrono::steady_clock::now()-started).count();
    const auto h=capture.health();
    Microsoft::WRL::ComPtr<IDXGIFactory1> factory;Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;Microsoft::WRL::ComPtr<IDXGIAdapter3> adapter3;
    DXGI_QUERY_VIDEO_MEMORY_INFO local{},shared{};
    if(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))&&SUCCEEDED(factory->EnumAdapters1(0,&adapter))&&SUCCEEDED(adapter.As(&adapter3))){
        adapter3->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_LOCAL,&local);adapter3->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL,&shared);}
    CHECK(capture.stop());
    if(!capture.health().error.empty())throw std::runtime_error("Readback bench: "+capture.health().error);
    const double mb=1024.0*1024.0;
    std::cout<<std::fixed<<std::setprecision(2)<<encoder<<" "<<width<<"x"<<height<<"@"<<fps<<": path="<<h.processing_path
        <<" cpu="<<double(after.cpu-before.cpu)/1e7/seconds*100<<"% faults/s="<<double(after.faults-before.faults)/seconds
        <<" privateWS="<<double(after.private_ws)/mb<<"MB commit="<<double(after.commit)/mb<<"MB dedicated="<<double(local.CurrentUsage)/mb
        <<"MB shared="<<double(shared.CurrentUsage)/mb<<"MB output="<<h.output_fps<<" fresh="<<h.unique_fps<<" readbackAvg="<<h.texture_readback_ms
        <<"ms submission p50="<<h.submission_p50_ms<<" p95="<<h.submission_p95_ms<<"ms completion p50="<<h.completion_p50_ms<<" p95="<<h.completion_p95_ms
        <<"ms drops="<<(h.backpressure_drops-warm.backpressure_drops)+(h.replaced-warm.replaced)<<"\n";
#if __has_include("readback_stage.h")
    std::cout<<"  readback p50="<<h.readback_p50_ms<<" p95="<<h.readback_p95_ms<<"ms mapWait p50="<<h.readback_map_wait_p50_ms<<" p95="<<h.readback_map_wait_p95_ms
        <<"ms stalls="<<(h.readback_map_stalls-warm.readback_map_stalls)<<" staging="<<h.readback_staging_peak<<"/"<<h.readback_staging_slots
        <<" cpuFrames="<<h.readback_cpu_frames_peak<<"/"<<h.readback_cpu_frames<<" allocationsAfterWarmup="<<(h.frame_allocations-warm.frame_allocations)
        <<" readbackDrops="<<h.readback_pressure_drops<<"\n";
#endif
    return 0;
}
// Manual benchmark: --wgc-bench <width> <height> <fps> <animate 0|1> [seconds].
// Real Windows Graphics Capture of the primary monitor through the production
// encoder candidates. animate=1 shows a 48x48 corner window presenting every
// vsync so DWM composes at the display rate; animate=0 measures the desktop
// as it is (idle or throttled).
class VsyncAnimator {
    std::atomic<bool> stop_{false};std::thread thread_;
public:
    VsyncAnimator(){thread_=std::thread([this]{
        WNDCLASSW type{};type.lpfnWndProc=DefWindowProcW;type.hInstance=GetModuleHandleW(nullptr);type.lpszClassName=L"ClypDatWgcBenchAnimator";RegisterClassW(&type);
        HWND window=CreateWindowExW(WS_EX_TOPMOST|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE,type.lpszClassName,L"",WS_POPUP,0,0,48,48,nullptr,nullptr,type.hInstance,nullptr);
        if(!window)return;ShowWindow(window,SW_SHOWNOACTIVATE);
        Microsoft::WRL::ComPtr<ID3D11Device> device;Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
        D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context);
        Microsoft::WRL::ComPtr<IDXGIDevice> dxgi;Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;Microsoft::WRL::ComPtr<IDXGIFactory2> factory;
        device.As(&dxgi);dxgi->GetAdapter(&adapter);adapter->GetParent(IID_PPV_ARGS(&factory));
        DXGI_SWAP_CHAIN_DESC1 desc{};desc.Width=48;desc.Height=48;desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM;desc.SampleDesc.Count=1;
        desc.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;desc.BufferCount=2;desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;
        Microsoft::WRL::ComPtr<IDXGISwapChain1> swap;factory->CreateSwapChainForHwnd(device.Get(),window,&desc,nullptr,nullptr,&swap);
        for(int frame=0;!stop_&&swap;++frame){
            MSG message{};while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)){TranslateMessage(&message);DispatchMessageW(&message);}
            Microsoft::WRL::ComPtr<ID3D11Texture2D> back;swap->GetBuffer(0,IID_PPV_ARGS(&back));Microsoft::WRL::ComPtr<ID3D11RenderTargetView> view;
            device->CreateRenderTargetView(back.Get(),nullptr,&view);const float shade=frame&1?.12f:.125f;const float color[4]{shade,shade,shade,1};
            context->ClearRenderTargetView(view.Get(),color);swap->Present(1,0);
        }
        swap.Reset();DestroyWindow(window);UnregisterClassW(type.lpszClassName,type.hInstance);});}
    ~VsyncAnimator(){stop_=true;if(thread_.joinable())thread_.join();}
};
int wgc_bench(int width,int height,int fps,bool animate,int seconds){
    std::unique_ptr<VsyncAnimator> animator;if(animate)animator=std::make_unique<VsyncAnimator>();
    RecordingCaptureConfig config;config.width=width;config.height=height;config.fps=fps;config.bitrate_mbps=25;
    std::atomic<uint64_t> packets{0};RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto,Packet,int64_t,bool){++packets;};
    RecordingCapture capture(config,callbacks);capture.start();
    std::this_thread::sleep_for(3s);
    struct Sample{uint64_t cpu;size_t private_ws;};
    auto sample=[]{FILETIME created{},exited{},kernel{},user{};GetProcessTimes(GetCurrentProcess(),&created,&exited,&kernel,&user);
        auto ticks=[](FILETIME t){return (uint64_t(t.dwHighDateTime)<<32)|t.dwLowDateTime;};
        PROCESS_MEMORY_COUNTERS_EX2 memory{};GetProcessMemoryInfo(GetCurrentProcess(),reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory),sizeof(memory));
        return Sample{ticks(kernel)+ticks(user),memory.PrivateWorkingSetSize};};
    const auto warm=capture.health();const auto before=sample();const auto started=std::chrono::steady_clock::now();
    const auto allocations_before=warm.source_details.owned_textures_allocated;
    // Rates averaged over one-second health windows.
    double fresh=0,output=0,delivered=0,callbacks_fps=0,duplicates=0,overwritten=0,selection=0;int windows=0;
    for(int second=0;second<seconds;++second){std::this_thread::sleep_for(1s);const auto h=capture.health();
        fresh+=h.unique_fps;output+=h.output_fps;delivered+=h.wgc_delivered_fps;callbacks_fps+=h.wgc_callback_fps;duplicates+=h.duplicate_fps;
        overwritten+=h.wgc_overwritten_fps;selection+=h.selection_dropped_fps;++windows;}
    const auto after=sample();const double elapsed=std::chrono::duration<double>(std::chrono::steady_clock::now()-started).count();
    const auto h=capture.health();
    Microsoft::WRL::ComPtr<IDXGIFactory1> factory;Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;Microsoft::WRL::ComPtr<IDXGIAdapter3> adapter3;
    DXGI_QUERY_VIDEO_MEMORY_INFO local{},shared{};
    if(SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))&&SUCCEEDED(factory->EnumAdapters1(0,&adapter))&&SUCCEEDED(adapter.As(&adapter3))){
        adapter3->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_LOCAL,&local);adapter3->QueryVideoMemoryInfo(0,DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL,&shared);}
    CHECK(capture.stop());animator.reset();
    if(!capture.health().error.empty())throw std::runtime_error("WGC bench: "+capture.health().error);
    const double mb=1024.0*1024.0;
    const double allocations=double(h.source_details.owned_textures_allocated-allocations_before)/elapsed;
    const double copy_p50=h.source_details.copy_p50_ms,copy_p95=h.source_details.copy_p95_ms;
    std::cout<<std::fixed<<std::setprecision(2)<<"WGC "<<width<<"x"<<height<<"@"<<fps<<(animate?" animated":" idle")<<": source="<<h.source
        <<" encoder="<<h.encoder<<" ownedAllocations/s="<<allocations<<" copy p50="<<copy_p50<<" p95="<<copy_p95<<"ms"
        <<" cpu="<<double(after.cpu-before.cpu)/1e7/elapsed*100<<"% privateWS="<<double(after.private_ws)/mb<<"MB dedicated="<<double(local.CurrentUsage)/mb
        <<"MB shared="<<double(shared.CurrentUsage)/mb<<"MB callbacks="<<callbacks_fps/windows<<" delivered="<<delivered/windows<<" overwritten="<<overwritten/windows
        <<" fresh="<<fresh/windows<<" output="<<output/windows<<" duplicates="<<duplicates/windows<<" selectionDropped="<<selection/windows
        <<" completion p50="<<h.completion_p50_ms<<" p95="<<h.completion_p95_ms<<"ms drops="<<(h.backpressure_drops-warm.backpressure_drops)+(h.replaced-warm.replaced)<<"\n";
    const auto& s=h.source_details;
    std::cout<<"  owned textures: capacity="<<s.owned_texture_capacity<<" allocated="<<s.owned_textures_allocated<<" leased="<<s.owned_textures_leased
        <<" peak="<<s.owned_textures_peak<<" pressureDrops="<<(s.owned_texture_pressure_drops-warm.source_details.owned_texture_pressure_drops)<<"\n";
    return 0;
}
int main(int argc,char**argv) {
    try{
    av_log_set_level(AV_LOG_ERROR);
    if(argc>1&&std::string_view(argv[1])=="--4k-gpu"){gpu_4k_to_1440p(90);return 0;}
    if(argc>1&&std::string_view(argv[1])=="--qsv")return qsv_hardware();
    if(argc>1&&std::string_view(argv[1])=="--wgc-holders"){measure_source_texture_holders();return 0;}
    if(argc>5&&std::string_view(argv[1])=="--wgc-bench")return wgc_bench(std::atoi(argv[2]),std::atoi(argv[3]),std::atoi(argv[4]),std::atoi(argv[5])!=0,argc>6?std::atoi(argv[6]):8);
    if(argc>5&&std::string_view(argv[1])=="--readback-bench")return readback_bench(argv[2],std::atoi(argv[3]),std::atoi(argv[4]),std::atoi(argv[5]));
    CHECK(capture_queue_capacity(30)==4);CHECK(capture_queue_capacity(120)==15);
    CHECK(capture_final_hold(true,16667,500000)==33334);CHECK(capture_final_hold(false,16667,500000)==16667);
    auto fit=capture_aspect_fit(1920,1200,1920,1080); CHECK(fit.width==1728&&fit.height==1080&&fit.x==96);
    int64_t deadline=0; CHECK(!capture_variable_deadline(15000,16667,deadline));CHECK(capture_variable_deadline(16000,16667,deadline));CHECK(deadline==16667);
    RecordingFramePacer constant(60,false);CHECK(constant.next(40000,true)==0);CHECK(constant.next(90000,false)==16667);CHECK(constant.next(100000,false)==33333);
    RecordingFramePacer variable(60,true);CHECK(variable.next(40000,true)==40000);CHECK(variable.next(90000,false)==56667);CHECK(variable.next(50000,true)==56668);
    RecordingRecoveryTimeline recovery;recovery.observe(true,false,4000000);int64_t safe=0;CHECK(!recovery.safe_start(0,60000000,safe));
    recovery.observe(false,false,11000000);CHECK(!recovery.safe_start(0,60000000,safe));recovery.observe(false,false,12000000);CHECK(recovery.safe_start(0,60000000,safe));CHECK(safe==12000000);
    CHECK(!capture_transport_shortfall(true,true,90,54));
    recording_encoder_plans();amf_recording_plans();qsv_recording_plans();
    frame_selection_policy();fresh_frame_delivery();
    const bool gpu=argc>1&&std::string_view(argv[1])=="--gpu";
    for(int fps:{30,60,90,120})for(bool variable:{false,true}){
        roundtrip(fps,variable);
        if(gpu){roundtrip(fps,variable,true,false);roundtrip(fps,variable,true,true);}
    }
    blocked_writer();source_eligibility_controls_capture_pause();startup_source_dip_does_not_switch_backend();detector();aspect_fit_processing(false);if(gpu){aspect_fit_processing(true);hdr_shader();gpu_generation_failover();planned_adapter_selection();pool_backpressure();busy_backpressure();stuck_encoder_recovery();stuck_encoder_without_fallback();amf_zero_copy_plan();amf_frame_context_ownership();amf_backpressure();
        qsv_surface_mapping();qsv_derivation();qsv_zero_copy_plan();qsv_backpressure();
        readback_stage_reuse();readback_pipeline();readback_pressure();captured_frame_store();pooled_wgc_capture();for(int fps:{60,90,120})gpu_4k_to_1440p(fps);}
    std::cout<<"Native recording generated capture, CFR/VFR pacing, encoding, drain and decode passed\n";
    return 0;
    }catch(const std::exception& error){std::cerr<<error.what()<<"\n";return 1;}
}
