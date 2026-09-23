#include "recording_overlays.h"
#include "recording_process.h"
#include <cstdlib>
#include <iostream>
#include <stdexcept>
#include <chrono>
#include <fstream>
extern "C" {
#include <libavutil/frame.h>
#include <libavutil/pixfmt.h>
}
#define CHECK(x) do { if(!(x)) { std::cerr << __LINE__ << ": " #x " failed\n"; std::exit(EXIT_FAILURE); } } while(false)
using namespace clypdat;
void camera_snapshots() {
    const auto ffmpeg=std::filesystem::path(__FILE__).parent_path().parent_path().parent_path()/L"vendor"/L"ffmpeg"/L"ffmpeg.exe";
    CHECK(std::filesystem::exists(ffmpeg));
    const auto root=std::filesystem::current_path()/(L"camera-snapshot-tests-"+std::to_wstring(std::chrono::steady_clock::now().time_since_epoch().count()));
    CHECK(std::filesystem::create_directories(root));
    struct Cleanup { std::filesystem::path path; ~Cleanup() { std::error_code ignored; std::filesystem::remove_all(path,ignored); } } cleanup{root};
    auto source=root/L"active.mp4"; std::atomic_bool cancel=false;
    ProcessRunner::run(ffmpeg,{L"-hide_banner",L"-y",L"-f",L"lavfi",L"-i",L"testsrc2=size=64x48:rate=30",L"-t",L"0.8",
        L"-c:v",L"libx264",L"-preset",L"ultrafast",L"-tune",L"zerolatency",L"-g",L"60",L"-bf",L"0",
        L"-movflags",L"+frag_keyframe+empty_moov+default_base_moof",L"-frag_duration",L"100000",source.wstring()},cancel);
    auto input=std::make_shared<InputHistory>(); auto history=std::make_shared<OverlayHistory>(input);
    history->retention(1000000); const auto generation=history->replace_camera();
    history->camera_segment({generation,1000000,1000000,source,false});
    auto snapshot=history->snapshot(1000000,1800000);
    CHECK(snapshot.camera_segments.size()==1 && !snapshot.camera_segments[0].completed);
    const auto pinned=snapshot.camera_segments[0].pinned_bytes; CHECK(pinned>0);
    // Bytes appended after admission cannot enter this save's media.
    { std::ofstream append(source,std::ios::binary|std::ios::app); append<<"incomplete-future-fragment"; }
    CHECK(std::filesystem::file_size(source)>pinned);
    history->camera_segment({generation,5000000,5000000,root/L"later.mp4",false});
    history->camera_segment({generation,8000000,8000000,root/L"latest.mp4",false});
    CHECK(std::filesystem::exists(source)); // The accepted snapshot still owns it.
    finalize_camera_snapshot(snapshot,ffmpeg,root/L"saved",cancel);
    CHECK(snapshot.camera_segments[0].completed);
    CHECK(snapshot.camera_segments[0].end_us>1000000 && snapshot.camera_segments[0].end_us<=1800000);
    CHECK(std::filesystem::exists(snapshot.camera_segments[0].path));
    CHECK(!std::filesystem::exists(source)); // Pruning releases after snapshot remux.
    cancel=true; bool cancelled=false;
    try { finalize_camera_snapshot(snapshot,ffmpeg,root/L"cancelled",cancel); } catch(const std::exception&) { cancelled=true; }
    CHECK(cancelled);
}
int main() {
    auto modes=camera_preview_modes("pixel_format=yuyv422 min s=640x480 fps=30 max s=1920x1080 fps=30\n"
        "vcodec=mjpeg min s=640x360 fps=60 max s=1280x720 fps=60\n"
        "pixel_format=nv12 min s=640x360 fps=60 max s=640x360 fps=60\n");
    CHECK(modes.size()==3 && modes[0].format=="nv12" && modes[1].format=="mjpeg");
    auto input=std::make_shared<InputHistory>(); input->reset(true);
    const PhysicalKey left{0x1d,false,false,0}, right{0x1d,true,false,0}, back{0,false,false,4}, forward{0,false,false,5};
    CHECK(input->add(100,left,true)); CHECK(!input->add(110,left,true));
    CHECK(input->add(200,right,true)); CHECK(input->add(300,left,false));
    CHECK(input->add(400,back,true)); CHECK(input->add(500,forward,true)); CHECK(input->add(600,back,false));
    auto snapshot=input->snapshot(350,700);
    CHECK(snapshot.checkpoints.size()==1 && snapshot.checkpoints[0].down==std::vector<PhysicalKey>{right});
    CHECK(snapshot.edges.size()==3);
    CHECK(snapshot.json().find("\"Version\":2")!=std::string::npos);
    CHECK(snapshot.json().find("MouseForward")!=std::string::npos);
    input->reset(false); CHECK(snapshot.edges.size()==3); CHECK(input->snapshot(0,1).unavailable);
    InputHistory limited(1); limited.reset(true); limited.add(1,left,true); limited.add(2,left,false);
    CHECK(limited.snapshot(0,3).overflowed && limited.pressed().empty());
    input->reset(true); input->add(200,left,true); input->add(100,left,false);
    CHECK(input->snapshot(0,300).edges.back().at_us==200);

    auto overlays=std::make_shared<OverlayHistory>(input); overlays->reset(true);
    OverlaySettingsNative settings; settings.at_us=10; settings.revision=1; settings.keyboard_layout="WASD";
    settings.keyboard_transform={0,0,1}; settings.camera_moniker=L"camera"; settings.camera_transform={0,0,1};
    CHECK(overlays->apply(settings)); CHECK(!overlays->apply(settings));
    std::vector<uint8_t> pixels(16,255);
    auto artwork=OverlayBitmap::copy(2,2,8,pixels.data(),pixels.size(),1,20,true);
    pixels[0]=0; CHECK(artwork->bgra[0]==255); CHECK(overlays->set_artwork(artwork));
    CHECK(!overlays->set_artwork(artwork));
    const auto generation=overlays->replace_camera(); CHECK(overlays->camera_frame(generation,artwork));
    overlays->camera_segment({generation,20,20,L"first.mp4",false});
    overlays->camera_segment({generation,40,40,L"second.mp4",false});
    overlays->complete_camera(generation,60);
    auto saved=overlays->snapshot(15,65); CHECK(saved.camera_segments.size()==2); CHECK(saved.settings.size()==1);
    const auto replacement=overlays->replace_camera(); CHECK(replacement!=generation); CHECK(!overlays->preview());
    CHECK(!overlays->camera_frame(generation,artwork));
    CHECK(overlays->snapshot(15,65).camera_segments.size()==2);
    auto* frame=av_frame_alloc(); CHECK(frame); frame->format=AV_PIX_FMT_NV12; frame->width=2; frame->height=2;
    CHECK(av_frame_get_buffer(frame,32)==0);
    for(int y=0;y<2;++y) for(int x=0;x<2;++x) frame->data[0][y*frame->linesize[0]+x]=16;
    frame->data[1][0]=128; frame->data[1][1]=128;
    CHECK(overlays->compose(*frame).keyboard); CHECK(frame->data[0][0]==235); CHECK(frame->data[1][0]>=127 && frame->data[1][0]<=129);
    // Limited BT.709 golden: half-opacity premultiplied red over studio black.
    // The prior managed compositor omitted Y's16 offset and treated Skia's
    // premultiplied red128 as straight red128, producing luma20 instead of40.
    std::vector<uint8_t> red(16);
    for(size_t offset=0;offset<red.size();offset+=4) { red[offset+2]=128; red[offset+3]=128; }
    CHECK(overlays->set_artwork(OverlayBitmap::copy(2,2,8,red.data(),red.size(),2,21,true)));
    for(int y=0;y<2;++y) for(int x=0;x<2;++x) frame->data[0][y*frame->linesize[0]+x]=16;
    frame->data[1][0]=128; frame->data[1][1]=128;
    CHECK(overlays->compose(*frame).keyboard); CHECK(frame->data[0][0]==40); CHECK(frame->data[1][0]==115); CHECK(frame->data[1][1]==184);
    CHECK(overlays->set_artwork(OverlayBitmap::copy(2,2,8,artwork->bgra.data(),artwork->bgra.size(),3,22,true)));
    av_frame_free(&frame);
    std::vector<uint8_t> target(16,0); CHECK(overlays->compose_bgra(target.data(),target.size(),2,2,8).keyboard); CHECK(target[0]==255);
    overlays->reset(false); CHECK(!overlays->compose_bgra(target.data(),target.size(),2,2,8).keyboard);
    CHECK(saved.camera_segments.size()==2 && saved.artwork[0]->bgra[0]==255);
    camera_snapshots();
    std::cout << "Native input history, immutable snapshots, camera generations and overlay composition passed\n";
}
