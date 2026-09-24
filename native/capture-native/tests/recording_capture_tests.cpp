#include "recording_capture.h"
#include "recording_save.h"
#include <atomic>
#include <cstdlib>
#include <chrono>
#include <condition_variable>
#include <iostream>
#include <mutex>
#include <random>
#include <stdexcept>
#include <thread>
#include <d3d11_4.h>
#include <wrl/client.h>
#include <DirectXPackedVector.h>
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
    explicit GeneratedSource(int fps,bool gpu=false,int width=256,int height=144) : fps_(fps) {
        if(gpu){width_=width;height_=height;
            CHECK(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&device_,nullptr,&context_)));
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
    // Candidates without a policy consumer keep their legacy runtime path.
    for(const auto name:{"h264_qsv","av1_qsv","libx264"})CHECK(!plan_recording_encoder(config,{name,false,false},kAdapterVendorNvidia,false));
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
    CHECK((names(recording_encoder_candidates(false,false))==std::vector<std::string>{"h264_nvenc+d3d11","h264_nvenc","h264_amf+d3d11","h264_amf","h264_qsv+lp","h264_qsv","libx264"}));
    CHECK((names(recording_encoder_candidates(false,true))==std::vector<std::string>{"av1_nvenc+d3d11","h264_nvenc+d3d11","av1_nvenc","h264_nvenc",
        "av1_amf+d3d11","av1_amf","av1_qsv+lp","av1_qsv","h264_amf+d3d11","h264_amf","h264_qsv+lp","h264_qsv","libx264"}));
    CHECK((names(recording_encoder_candidates(true,false))==std::vector<std::string>{"libx264"}));
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

// AMF cannot open on this NVIDIA machine. An NVENC context stands in for the
// encoder while RecordingCapture runs the real AMF plan: options, low-delay
// flag, frame-context ownership, pool and in-flight limits, backpressure.
std::unique_ptr<VideoEncoder> amf_stand_in(VideoEncoderConfig value,CodecCalls calls={}){
    const bool hardware=value.hardware_frames!=nullptr;
    value.name=hardware?"h264_nvenc":"libx264";value.resource_options.clear();value.codec_flags=0;
    return std::make_unique<VideoEncoder>(value,std::move(calls));
}
struct AmfRun{RecordingCaptureHealth health;std::vector<size_t> attempted;std::vector<VideoEncoderConfig> opened;};
AmfRun amf_run(std::optional<uint32_t> adapter){
    RecordingCaptureConfig config;config.width=256;config.height=144;config.fps=60;
    RecordingCaptureDependencies dependencies;dependencies.candidates={{"h264_amf",false,true},{"h264_amf",false,false}};dependencies.adapter_vendor=adapter;
    AmfRun run;std::atomic<uint64_t> packets{0};
    dependencies.open_encoder=[&](const VideoEncoderConfig& value,size_t candidate){run.attempted.push_back(candidate);run.opened.push_back(value);return amf_stand_in(value);};
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
        dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t){return amf_stand_in(value,probe_calls(probe));};
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
    dependencies.open_encoder=[probe](const VideoEncoderConfig& value,size_t candidate){return candidate==0?amf_stand_in(value,probe_calls(probe)):amf_stand_in(value);};
    std::atomic<uint64_t> second{0};RecordingCaptureCallbacks callbacks;callbacks.packet=[&](auto generation,Packet,int64_t,bool){if(generation->id==2)++second;};
    RecordingCapture capture(config,callbacks,std::make_unique<GeneratedSource>(60,true),std::move(dependencies));capture.start();
    CHECK(wait_health(capture,[](const auto& h){return h.retained_pressure_drops>=5;},3s));
    const auto pressured=capture.health();
    if(pressured.restart_required||pressured.submitted!=4||pressured.max_in_flight!=4)throw std::runtime_error("AMF retained pressure: "+pressure_state(pressured));
    if(!wait_health(capture,[&](const auto&){return second.load()>=30;},6s))throw std::runtime_error("Stuck AMF encoder not replaced: "+pressure_state(capture.health()));
    CHECK(capture.stop());const auto health=capture.health();
    if(!health.error.empty()||health.encoder_stall_recoveries!=1||health.generation!=2||health.encoder_vendor!="amd")throw std::runtime_error("AMF recovery: "+pressure_state(health));
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
int main(int argc,char**argv) {
    try{
    av_log_set_level(AV_LOG_ERROR);
    if(argc>1&&std::string_view(argv[1])=="--4k-gpu"){gpu_4k_to_1440p(90);return 0;}
    CHECK(capture_queue_capacity(30)==4);CHECK(capture_queue_capacity(120)==15);
    CHECK(capture_final_hold(true,16667,500000)==33334);CHECK(capture_final_hold(false,16667,500000)==16667);
    auto fit=capture_aspect_fit(1920,1200,1920,1080); CHECK(fit.width==1728&&fit.height==1080&&fit.x==96);
    int64_t deadline=0; CHECK(!capture_variable_deadline(15000,16667,deadline));CHECK(capture_variable_deadline(16000,16667,deadline));CHECK(deadline==16667);
    RecordingFramePacer constant(60,false);CHECK(constant.next(40000,true)==0);CHECK(constant.next(90000,false)==16667);CHECK(constant.next(100000,false)==33333);
    RecordingFramePacer variable(60,true);CHECK(variable.next(40000,true)==40000);CHECK(variable.next(90000,false)==56667);CHECK(variable.next(50000,true)==56668);
    RecordingRecoveryTimeline recovery;recovery.observe(true,false,4000000);int64_t safe=0;CHECK(!recovery.safe_start(0,60000000,safe));
    recovery.observe(false,false,11000000);CHECK(!recovery.safe_start(0,60000000,safe));recovery.observe(false,false,12000000);CHECK(recovery.safe_start(0,60000000,safe));CHECK(safe==12000000);
    CHECK(!capture_transport_shortfall(true,true,90,54));
    recording_encoder_plans();amf_recording_plans();
    frame_selection_policy();fresh_frame_delivery();
    const bool gpu=argc>1&&std::string_view(argv[1])=="--gpu";
    for(int fps:{30,60,90,120})for(bool variable:{false,true}){
        roundtrip(fps,variable);
        if(gpu){roundtrip(fps,variable,true,false);roundtrip(fps,variable,true,true);}
    }
    blocked_writer();source_eligibility_controls_capture_pause();startup_source_dip_does_not_switch_backend();detector();aspect_fit_processing(false);if(gpu){aspect_fit_processing(true);hdr_shader();gpu_generation_failover();planned_adapter_selection();pool_backpressure();busy_backpressure();stuck_encoder_recovery();stuck_encoder_without_fallback();amf_zero_copy_plan();amf_frame_context_ownership();amf_backpressure();for(int fps:{60,90,120})gpu_4k_to_1440p(fps);}
    std::cout<<"Native recording generated capture, CFR/VFR pacing, encoding, drain and decode passed\n";
    return 0;
    }catch(const std::exception& error){std::cerr<<error.what()<<"\n";return 1;}
}
