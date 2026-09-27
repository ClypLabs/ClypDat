#include "recording_capture.h"
#include <d3d11.h>
extern "C" {
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libavutil/opt.h>
}
#include <chrono>
#include <atomic>
#include <condition_variable>
#include <deque>
#include <fstream>
#include <iostream>
#include <mutex>
#include <thread>

using namespace clypdat;
using namespace std::chrono_literals;
#define CHECK(x) do { if (!(x)) throw std::runtime_error("Check failed: " #x); } while (0)
struct BufferFree { void operator()(AVBufferRef* p) const { av_buffer_unref(&p); } };
using Buffer = std::unique_ptr<AVBufferRef, BufferFree>;
struct FrameFree { void operator()(AVFrame* p) const { av_frame_free(&p); } };
using Frame = std::unique_ptr<AVFrame, FrameFree>;

// Generated pixels only. Never captures a user's desktop or moves input.
class Source final : public RecordingFrameSource {
public:
    bool acquire(CapturePixels& p, std::chrono::milliseconds) override {
        std::this_thread::sleep_for(20ms);
        p.width=64; p.height=48; p.stride=256; p.bgra.assign(64*48*4,128); return true;
    }
    bool eligible() const override { return true; }
    const char* name() const override { return "keyframe fixture"; }
};

// An encoder that ignores automatic GOP configuration, like the incident.
// Only explicitly requested I pictures become real key packets.
void recorder_contract(bool busy=false,bool break_first=false,bool always_bad=false) {
    RecordingCaptureConfig config; config.width=64; config.height=48; config.fps=60; config.cpu_encoder=true;
    RecordingCaptureDependencies dependencies;
    auto opens=std::make_shared<std::atomic_int>(0);
    auto busy_seen=std::make_shared<std::atomic_bool>(false);
    dependencies.open_encoder=[=](const VideoEncoderConfig& c,size_t) {
        const bool broken=always_bad||(break_first&&(*opens)==0); ++*opens;
        struct Pending { int64_t pts; bool key; };
        auto queue=std::make_shared<std::deque<Pending>>();
        auto first=std::make_shared<int64_t>(-1),drop=std::make_shared<int64_t>(-1);
        CodecCalls calls;
        calls.send=[=](AVCodecContext*,const AVFrame* f) {
            if(!f)return 0;
            if(*first<0)*first=f->pts;
            if(busy&&*drop<0&&f->pts-*first>=999999&&f->pict_type==AV_PICTURE_TYPE_I) { *drop=f->pts; *busy_seen=true; }
            if(f->pts==*drop)return AVERROR(EAGAIN); // all retries of this tick refused
            queue->push_back({f->pts,f->pict_type==AV_PICTURE_TYPE_I&&(!broken||f->pts==*first)}); return 0;
        };
        calls.receive=[queue](AVCodecContext*,AVPacket* p) {
            if(queue->empty())return AVERROR(EAGAIN);
            auto item=queue->front(); queue->pop_front();
            if(av_new_packet(p,1)<0)return AVERROR(ENOMEM);
            p->pts=p->dts=item.pts; p->flags=item.key?AV_PKT_FLAG_KEY:0; return 0;
        };
        return std::make_unique<VideoEncoder>(c,calls);
    };
    std::mutex mutex; std::condition_variable ready; std::vector<int64_t> keys;
    int64_t first=-1,last=-1;
    RecordingCaptureCallbacks callbacks;
    callbacks.packet=[&](auto,Packet p,int64_t,bool) {
        std::lock_guard lock(mutex); if(first<0)first=p->pts; last=p->pts;
        if(p->flags&AV_PKT_FLAG_KEY)keys.push_back(p->pts); ready.notify_all();
    };
    RecordingCapture capture(config,callbacks,std::make_unique<Source>(),dependencies); capture.start();
    const auto deadline=std::chrono::steady_clock::now()+8s;
    bool reached=false;
    while(std::chrono::steady_clock::now()<deadline) {
        const auto health=capture.health();
        if(always_bad&&!health.error.empty()) { reached=true; break; }
        { std::lock_guard lock(mutex); if(!always_bad&&last-first>=(break_first?3500000:2200000)) { reached=true; break; } }
        std::this_thread::sleep_for(10ms);
    }
    const auto health=capture.health(); const bool stopped=capture.stop(); CHECK(reached);
    if(always_bad) {
        CHECK(health.error.find(KeyframeCadenceError().what())!=std::string::npos);
        CHECK(*opens==2); CHECK(!health.keyframes.safe);
        std::cout<<"broken encoder stopped after exactly one native recovery\n"; return;
    }
    CHECK(stopped); CHECK(health.error.empty());
    if(busy)CHECK(*busy_seen&&health.encoder_busy_drops>0);
    if(break_first) { CHECK(*opens==2); CHECK(health.keyframes.recoveries==1); CHECK(health.keyframes.safe); }
    std::cout<<"recorder ignoring automatic GOP: keys="<<keys.size()<<" elapsed_us="<<last-first<<"\n";
    CHECK(keys.size()>=3);
    CHECK(keys.front()==first);
    for(size_t i=1;i<keys.size();++i)CHECK(keys[i]-keys[i-1]<=(break_first?3000000:1100000));
}

void policy_tests() {
    for(int fps:{30,60,90,120})for(bool variable:{false,true}) {
        ReplayKeyframes policy; policy.generation(fps,8);
        const auto threshold=policy.health.watchdog_us; CHECK(threshold>=2100000&&threshold<=3000000);
        int64_t pts=0,last_key=-1,max_gap=0; bool dropped=false;
        for(int i=0;i<fps*7;++i) {
            const int active=i<fps*3?fps:30; // FPS changes without resetting PTS
            pts+=(variable&&i%5==0?2:1)*1000000/active;
            if(i==fps*5)policy.generation(active,8); // recovery/startup must request again
            const bool requested=policy.due(pts);
            if(requested&&!dropped&&pts>=1000000) { dropped=true; CHECK(policy.due(pts+10000)); continue; }
            policy.accepted(pts,requested);
            CHECK(policy.packet(pts,requested));
            if(requested) { if(last_key>=0)max_gap=std::max(max_gap,pts-last_key); last_key=pts; }
        }
        CHECK(dropped); CHECK(max_gap<=kReplayMaxLeadInUs); CHECK(policy.health.periodic_requested>=5);
        std::cout<<"policy fps="<<fps<<" variable="<<variable<<" max_gap_us="<<max_gap<<"\n";
    }
    ReplayKeyframes ignored; ignored.generation(120,8); ignored.accepted(0,true);
    CHECK(ignored.packet(0,true)); ignored.accepted(1000000,true);
    CHECK(ignored.packet(ignored.health.watchdog_us,false));
    CHECK(!ignored.packet(ignored.health.watchdog_us+1,false));
    ignored.generation(30,8); CHECK(ignored.due(3000000)); CHECK(!ignored.packet(3000000,false));
    CHECK(ignored.packet(3100000,true)); CHECK(ignored.health.safe);
    // Verify option names against the shipped runtime without pretending that
    // AVOption acceptance proves a driver honored SetProperty/SubmitInput.
    for(const auto* name:{"h264_amf","av1_amf","h264_qsv","av1_qsv"}) {
        const auto* codec=avcodec_find_encoder_by_name(name); CHECK(codec);
        CodecContext context(avcodec_alloc_context3(codec)); CHECK(context);
        CHECK(av_opt_set(context->priv_data,"forced_idr","1",0)==0);
        if(std::string(name).ends_with("_amf")) {
            CHECK(av_opt_set(context->priv_data,"usage","ultralowlatency",0)==0);
            CHECK(av_opt_set(context->priv_data,"quality","speed",0)==0);
            CHECK(av_opt_set(context->priv_data,"rc",std::string(name).starts_with("av1")?"hqcbr":"cbr",0)==0);
        }
    }
}

void encoder_check(const std::string& backend,bool gpu,bool explicit_requests,int fps,std::ostream* trace) {
    Buffer device,frames;
    constexpr int width=320,height=192;
    if(gpu) {
        AVBufferRef* d=nullptr;
        CHECK(av_hwdevice_ctx_create(&d,AV_HWDEVICE_TYPE_D3D11VA,nullptr,nullptr,0)==0); device.reset(d);
        frames.reset(av_hwframe_ctx_alloc(device.get())); CHECK(frames);
        auto* pool=reinterpret_cast<AVHWFramesContext*>(frames->data);
        pool->format=AV_PIX_FMT_D3D11; pool->sw_format=AV_PIX_FMT_NV12;
        pool->width=width; pool->height=height; pool->initial_pool_size=0;
        reinterpret_cast<AVD3D11VAFramesContext*>(pool->hwctx)->BindFlags=D3D11_BIND_RENDER_TARGET;
        CHECK(av_hwframe_ctx_init(frames.get())==0);
    }
    VideoEncoderConfig c; c.width=width; c.height=height; c.fps=fps; c.bitrate_mbps=5;
    c.name=backend; c.hardware_frames=frames.get();
    RecordingCaptureConfig recording; recording.fps=fps; recording.width=width; recording.height=height;
    recording.av1=backend.starts_with("av1");
    uint32_t vendor=backend.ends_with("_amf")?0x1002:backend.ends_with("_qsv")?0x8086:0x10de;
    const auto plan=plan_recording_encoder(recording,{backend,false,gpu},vendor,false);
    if(plan) { c.resource_options=plan->options; c.codec_flags=plan->low_delay_flag?AV_CODEC_FLAG_LOW_DELAY:0;
        c.require_encoder_frames=plan->frames_from_encoder_ctx; c.right_size_packets=plan->right_size_packets; }
    VideoEncoder encoder(c);
    for(const auto& option:encoder.unsupported_options())std::cout<<"unsupported "<<backend<<" "<<option<<"\n";
    Frame cpu(av_frame_alloc()); CHECK(cpu); cpu->format=AV_PIX_FMT_NV12; cpu->width=width; cpu->height=height;
    CHECK(av_frame_get_buffer(cpu.get(),32)==0);
    size_t packets=0,keys=0; int64_t first=-1,last_key=-1,max_gap=0,last=-1;
    auto receive=[&](std::vector<Packet> batch) {
        for(const auto& p:batch) {
            const bool key=p->flags&AV_PKT_FLAG_KEY; if(!packets)CHECK(key);
            CHECK(!packets||p->pts>last); if(first<0)first=p->pts; last=p->pts; ++packets;
            int64_t gap=last_key<0?0:p->pts-last_key;
            if(key) { ++keys; max_gap=std::max(max_gap,gap); last_key=p->pts; }
            if(trace)*trace<<backend<<','<<(gpu?"d3d11":"system")<<','<<fps<<",output,"<<p->pts<<",,"<<p->dts<<','<<key<<','<<gap<<'\n';
        }
    };
    for(int i=0;i<fps*4;++i) {
        CHECK(av_frame_make_writable(cpu.get())==0);
        for(int y=0;y<height;++y)for(int x=0;x<width;++x)cpu->data[0][y*cpu->linesize[0]+x]=uint8_t(32+(x+y+i)%180);
        for(int y=0;y<height/2;++y)memset(cpu->data[1]+y*cpu->linesize[1],128,width);
        cpu->pts=av_rescale_q(i,{1,fps},encoder.context().time_base);
        const bool request=i==0||(explicit_requests&&i%fps==0);
        cpu->pict_type=request?AV_PICTURE_TYPE_I:AV_PICTURE_TYPE_NONE;
        if(trace)*trace<<backend<<','<<(gpu?"d3d11":"system")<<','<<fps<<",input,"<<cpu->pts<<','<<request<<",,,\n";
        if(gpu) {
            Frame surface(av_frame_alloc()); CHECK(surface);
            CHECK(av_hwframe_get_buffer(frames.get(),surface.get(),0)==0);
            CHECK(av_hwframe_transfer_data(surface.get(),cpu.get(),0)==0);
            surface->pts=cpu->pts; surface->pict_type=cpu->pict_type; receive(encoder.submit(*surface));
        } else receive(encoder.submit(*cpu));
    }
    receive(encoder.finish()); max_gap=std::max(max_gap,last-last_key);
    std::cout<<backend<<' '<<(gpu?"d3d11":"system")<<" fps="<<fps<<" mode="<<(explicit_requests?"explicit":"auto")
        <<" packets="<<packets<<" keys="<<keys<<" max_gap_us="<<max_gap<<'\n';
    CHECK(packets==size_t(fps*4)); CHECK(keys>=4); CHECK(max_gap<=1100000);
}

int main(int argc,char** argv) {
    av_log_set_level(AV_LOG_ERROR);
    try {
        if(argc==2&&std::string(argv[1])=="--recorder") {
            policy_tests(); recorder_contract(); recorder_contract(true); recorder_contract(false,true); recorder_contract(false,false,true); return 0;
        }
        std::string backend="libx264"; bool gpu=false,explicit_requests=true;
        std::ofstream trace;
        for(int i=1;i<argc;++i) {
            const std::string arg=argv[i];
            if(arg=="--backend"&&i+1<argc)backend=argv[++i];
            else if(arg=="--d3d11")gpu=true;
            else if(arg=="--auto")explicit_requests=false;
            else if(arg=="--trace"&&i+1<argc) { trace.open(argv[++i]); CHECK(trace); trace<<"backend,input_path,fps,event,pts_us,requested_i,dts_us,key,gap_us\n"; }
            else throw std::runtime_error("Unknown or incomplete argument: "+arg);
        }
        for(int fps:{30,60,90,120})encoder_check(backend,gpu,explicit_requests,fps,trace.is_open()?&trace:nullptr);
        return 0;
    } catch(const std::exception& e) { std::cerr<<e.what()<<'\n'; return 1; }
}
