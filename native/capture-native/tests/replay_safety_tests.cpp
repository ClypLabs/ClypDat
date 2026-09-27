#include "recording_save.h"
#include <iostream>
#include <stdexcept>
using namespace clypdat;
#define CHECK(x) do { if (!(x)) throw std::runtime_error("Check failed: " #x); } while (0)
namespace {
auto generation(uint64_t id,int width=64) {
    auto g=std::make_shared<CaptureGeneration>(); g->id=id;
    auto* codec=avcodec_parameters_alloc(); codec->codec_type=AVMEDIA_TYPE_VIDEO;
    codec->codec_id=AV_CODEC_ID_H264; codec->width=width; codec->height=48;
    g->codec={codec,CaptureCodecParametersDeleter{}}; return g;
}
void append(VideoHistory& history,std::shared_ptr<CaptureGeneration> g,int64_t pts,bool key) {
    Packet p(av_packet_alloc()); CHECK(p); p->pts=p->dts=pts; p->flags=key?AV_PKT_FLAG_KEY:0;
    history.append(g,std::move(p),pts+1000000,true);
}
void refused(VideoHistory& history,int64_t start,int64_t end) {
    bool failed=false;
    try { auto result=history.snapshot(start+1000000,end+1000000);
        std::cerr<<"unsafe snapshot duration_us="<<result.duration_us<<'\n'; }
    catch(const KeyframeCadenceError&) { failed=true; }
    CHECK(failed);
}
void cases() {
    auto g=generation(1);
    VideoHistory normal(120000000);
    for(int i=0;i<360*60;++i)append(normal,g,int64_t(i)*1000000/60,i%60==0);
    auto a=normal.snapshot(240000000+1000000,360000000+1000000);
    CHECK(a.duration_us>=120000000&&a.duration_us<=121100000);
    auto b=normal.snapshot(240500000+1000000,360000000+1000000);
    CHECK(b.start_us==241000000); CHECK(b.duration_us<=120600000);
    std::cout<<"normal120s="<<a.duration_us<<"us half-second lead-in="<<b.duration_us<<"us\n";

    VideoHistory sparse(120000000);
    for(int i=0;i<360*60;++i)append(sparse,g,int64_t(i)*1000000/60,i==0);
    refused(sparse,240000000,360000000); // original 6-minute incident

    VideoHistory old(120000000);
    for(int i=0;i<180*60;++i)append(old,g,int64_t(i)*1000000/60,i==30*60||i==0);
    refused(old,60000000,180000000); // key 30 seconds before requested start

    // No usable preceding key even though recent output has resumed.
    append(old,g,180000000,true);
    append(old,g,180016667,false);
    auto recovered=old.snapshot(60000000+1000000,180033334+1000000);
    CHECK(recovered.packets.front().packet->flags&AV_PKT_FLAG_KEY);
    CHECK(recovered.packets.front().acquired_us==181000000); CHECK(recovered.duration_us<1000000);
    VideoHistory excessive_lead(120000000);
    append(excessive_lead,g,0,true); append(excessive_lead,g,2000000,false);
    refused(excessive_lead,1500000,2016667); // 1.5-second lead-in, below the retention watchdog

    // Compatible generations keep valid preceding data; incompatible ones
    // start a new decodable section, as before.
    auto second=generation(2),third=generation(3,128);
    VideoHistory generations(120000000);
    for(int i=0;i<5*60;++i)append(generations,i<120?g:second,int64_t(i)*1000000/60,i%60==0);
    auto compatible=generations.snapshot(1500000+1000000,5000000+1000000);
    CHECK(compatible.packets.front().generation->id==1); CHECK(compatible.generation==2);
    append(generations,third,5000000,true); append(generations,third,5016667,false);
    auto incompatible=generations.snapshot(1500000+1000000,5033334+1000000);
    CHECK(incompatible.packets.front().generation->id==3);
    std::cout<<"unsafe30s/startup-only saves rejected; recovery and generations passed\n";

    VideoHistory hours(120000000);
    for(int i=0;i<2*60*60*120;++i)append(hours,g,int64_t(i)*1000000/120,i==0);
    const auto bounded=hours.stats();
    CHECK(bounded.packets==0);CHECK(bounded.keyframes==0);CHECK(!bounded.keyframe_safe);
    CHECK(bounded.peak_retained_us<=3000000);CHECK(bounded.keyframe_invalidations==1);
    CHECK(bounded.examined<1000);
    std::cout<<"two-hour broken cadence: peak_retained_us="<<bounded.peak_retained_us<<" retained_us="<<bounded.retained_us<<" packets="<<bounded.packets<<'\n';
    append(hours,second,7200000000,true); append(hours,second,7200008333,false);
    CHECK(hours.stats().keyframe_safe); CHECK(hours.stats().keyframes==1);
    hours.invalidate_keyframes(); CHECK(!hours.stats().keyframe_safe);
    refused(hours,7080000000,7200000000);
    append(hours,third,7200016667,false); CHECK(hours.stats().packets==0);
    append(hours,third,7200025000,true); CHECK(hours.stats().keyframe_safe);
}
}
int main() {
    try { cases(); return 0; }
    catch(const std::exception& error) { std::cerr<<error.what()<<'\n'; return 1; }
}
