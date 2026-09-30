#include "recording_audio.h"
#include "recording_audio_timing.h"
#include "recording_save.h"
#include "recording_process.h"
#include <Windows.h>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <thread>
extern "C" {
#include <libavformat/avformat.h>
}
using namespace clypdat;
namespace {
void require(bool value,const char* message){if(!value)throw std::runtime_error(message);}
PcmBlock pcm(int64_t start,uint64_t generation,float value,int count=4800){PcmBlock block;block.lane="game";block.source="game:1";block.start_us=start;block.generation=generation;block.sample_rate=48000;block.channels=2;block.samples.assign(size_t(count)*2,value);return block;}
float sample(const std::filesystem::path& path,int64_t frame){std::ifstream input(path,std::ios::binary);input.seekg(44+frame*8);float result=0;input.read(reinterpret_cast<char*>(&result),4);require(bool(input),"Cannot read rendered PCM");return result;}
void decode_and_seek(const std::filesystem::path& path,int expected_streams){auto p=path.u8string();std::string utf8(p.begin(),p.end());AVFormatContext* input=nullptr;require(avformat_open_input(&input,utf8.c_str(),nullptr,nullptr)>=0,"Cannot open media");struct Input{AVFormatContext* p;~Input(){avformat_close_input(&p);}} cleanup{input};require(avformat_find_stream_info(input,nullptr)>=0,"Cannot probe media");require(int(input->nb_streams)==expected_streams,"Incorrect saved stream count");int video=av_find_best_stream(input,AVMEDIA_TYPE_VIDEO,-1,-1,nullptr,0);require(video>=0,"Saved video stream missing");const auto* decoder=avcodec_find_decoder(input->streams[video]->codecpar->codec_id);CodecContext context(avcodec_alloc_context3(decoder));require(context&&avcodec_parameters_to_context(context.get(),input->streams[video]->codecpar)>=0&&avcodec_open2(context.get(),decoder,nullptr)>=0,"Cannot open saved decoder");Packet packet(av_packet_alloc());AVFrame* frame=av_frame_alloc();require(frame!=nullptr,"Cannot allocate decode frame");int decoded=0;while(av_read_frame(input,packet.get())>=0){if(packet->stream_index==video){require(avcodec_send_packet(context.get(),packet.get())>=0,"Saved packet rejected by decoder");while(avcodec_receive_frame(context.get(),frame)>=0){++decoded;av_frame_unref(frame);}}av_packet_unref(packet.get());}require(decoded>0,"Saved file produced no decoded video");require(av_seek_frame(input,-1,input->duration/2,AVSEEK_FLAG_BACKWARD)>=0,"Saved file cannot seek");avcodec_flush_buffers(context.get());bool sought=false;while(!sought&&av_read_frame(input,packet.get())>=0){if(packet->stream_index==video&&avcodec_send_packet(context.get(),packet.get())>=0)sought=avcodec_receive_frame(context.get(),frame)>=0;av_packet_unref(packet.get());}av_frame_free(&frame);require(sought,"Saved seek produced no frame");if(expected_streams>1){auto* title=av_dict_get(input->streams[1]->metadata,"title",nullptr,0);auto* handler=av_dict_get(input->streams[1]->metadata,"handler_name",nullptr,0);require((title&&std::string(title->value)=="Game Audio")||(handler&&std::string(handler->value)=="Game Audio"),"Saved named audio lane missing");}}
std::filesystem::path bundled_ffmpeg(){return std::filesystem::path(__FILE__).parent_path().parent_path().parent_path()/L"vendor"/L"ffmpeg"/L"ffmpeg.exe";}
float decoded_sample(const std::filesystem::path& path,int audio_index){
    std::atomic_bool cancel=false;
    auto result=ProcessRunner::run(bundled_ffmpeg(),{L"-v",L"error",L"-nostdin",L"-i",path.wstring(),L"-map",L"0:a:"+std::to_wstring(audio_index),L"-ac",L"2",L"-ar",L"48000",L"-f",L"f32le",L"pipe:1"},cancel);
    constexpr size_t offset=4800*2*sizeof(float);
    require(result.output.size()>=offset+sizeof(float),"Decoded audio too short");
    float value=0;std::memcpy(&value,result.output.data()+offset,sizeof(value));return value;
}
void check_replay_audio(const std::filesystem::path& root,const VideoSnapshot& video){
    AudioHistory audio(root/L"replay-history",3000000);
    auto submit=[&](const char* lane,int channels,float level){auto block=pcm(video.start_us,1,level,int(video.duration_us*48000/1000000));block.lane=lane;block.source=lane;block.channels=channels;block.samples.assign(size_t(block.samples.size()/2)*channels,level);require(audio.submit(std::move(block)),"Replay audio rejected");};
    submit("game",2,.1f);submit("discord",2,.2f);submit("mic",1,.3f);submit("system",2,.4f);submit("mic:default",1,.3f);
    auto snapshot=audio.snapshot(video.start_us,video.end_us).get();audio.stop();
    auto save=[&](const char* id,std::vector<AudioLaneConfig> lanes){
        std::promise<AudioSnapshot> promise;promise.set_value(snapshot);
        ReplaySaveRequest request;request.id=id;request.output=root/(std::string(id)+".mp4");request.work_directory=root;request.ffmpeg=bundled_ffmpeg();request.video=video;request.audio=promise.get_future().share();request.lanes=std::move(lanes);
        SaveCoordinator coordinator;auto result=coordinator.begin(std::move(request)).get();require(result.error.empty(),"Replay save failed");return result.output;
    };
    auto mixed=save("mixed-replay",{{"game","Game Audio",2,.5f,false},{"discord","Discord",2,.75f,false},{"mic","Microphone",1,.5f,false}});
    auto path=mixed.u8string();std::string utf8(path.begin(),path.end());AVFormatContext* input=nullptr;require(avformat_open_input(&input,utf8.c_str(),nullptr,nullptr)>=0,"Cannot open mixed replay");
    require(avformat_find_stream_info(input,nullptr)>=0&&input->nb_streams==5,"Mixed replay stream count wrong");
    for(int i=0;i<4;++i){auto* stream=input->streams[i+1];auto* title=av_dict_get(stream->metadata,"title",nullptr,0);auto* handler=av_dict_get(stream->metadata,"handler_name",nullptr,0);const char* expected[]={"All Tracks","Game Audio","Discord","Microphone"};require(stream->codecpar->codec_type==AVMEDIA_TYPE_AUDIO&&((title&&std::string(title->value)==expected[i])||(handler&&std::string(handler->value)==expected[i])),"Mixed replay stream title or order wrong");require(bool(stream->disposition&AV_DISPOSITION_DEFAULT)==(i==0),"Mixed replay default audio disposition wrong");}
    avformat_close_input(&input);
    std::atomic_bool cancel=false;auto ffprobe=bundled_ffmpeg();ffprobe.replace_filename(L"ffprobe.exe");
    auto probe=ProcessRunner::run(ffprobe,{L"-v",L"error",L"-show_entries",L"stream=index,codec_type:stream_tags=title,handler_name:stream_disposition=default",L"-of",L"compact=p=0",mixed.wstring()},cancel);
    require(probe.output.find("All Tracks")!=std::string::npos&&probe.output.find("Discord")!=std::string::npos,"ffprobe lost replay audio titles");
    std::cout<<"ffprobe mixed replay:\n"<<probe.output;
    auto all=decoded_sample(mixed,0),game=decoded_sample(mixed,1),chat=decoded_sample(mixed,2),mic=decoded_sample(mixed,3);
    require(std::abs(game-.05f)<.02f&&std::abs(chat-.15f)<.02f&&mic>.08f,"Separate replay audio gains or sources wrong");
    require(std::abs(all-(game+chat+mic))<.025f,"All Tracks does not contain each separate source once");
    auto single=save("single-replay",{{"game","Game Audio",2,.5f,false}});
    auto single_path=single.u8string();std::string single_utf8(single_path.begin(),single_path.end());require(avformat_open_input(&input,single_utf8.c_str(),nullptr,nullptr)>=0,"Cannot open single-track replay");require(avformat_find_stream_info(input,nullptr)>=0&&input->nb_streams==2,"Single-track replay gained duplicate mix");avformat_close_input(&input);
    AudioGraphConfig system_config;system_config.system_audio=true;system_config.game_gain=.5f;
    system_config.applications={{L"Discord",1}};system_config.microphone_device_ids={L"default"};
    // The snapshot also contains the old game/app routes. Saving system mode
    // must select only system playback and the separate microphone.
    auto system=save("system-replay",recording_audio_lanes(system_config));
    auto system_path=system.u8string();std::string system_utf8(system_path.begin(),system_path.end());
    require(avformat_open_input(&input,system_utf8.c_str(),nullptr,nullptr)>=0,"Cannot open system-audio replay");
    require(avformat_find_stream_info(input,nullptr)>=0&&input->nb_streams==4,"System-audio replay retained game or app tracks");
    auto* system_title=av_dict_get(input->streams[2]->metadata,"handler_name",nullptr,0);
    require(system_title&&std::string(system_title->value)=="All System Audio","System-audio track label lost");
    avformat_close_input(&input);
    auto system_mix=decoded_sample(system,0),playback=decoded_sample(system,1),system_mic=decoded_sample(system,2);
    require(std::abs(playback-.2f)<.02f&&system_mic>.15f,"System-audio capture gain or microphone source wrong");
    require(std::abs(system_mix-(playback+system_mic))<.025f,"System-audio replay mixed duplicate game or app sources");
}
void check_timed_microphone_save(const std::filesystem::path& root, const VideoSnapshot& video) {
    AudioHistory history(root / L"timed-mic-history", 3000000);
    FullSessionWriter session({root / L"timed-mic-session.mkv", {{"mic", "Microphone", 1, 1, false}}}, video.packets.front().generation);
    require(session.video(video.packets.front().generation, *video.packets.front().packet), "Timed microphone session rejected first video");
    RecordingAudioTiming timing([&](PcmBlock block) {
        require(session.audio(block), "Session rejected timed microphone PCM");
        require(history.submit(std::move(block)), "History rejected timed microphone PCM");
    });
    const auto input_frames = audio_frames(video.end_us - video.start_us, 44100);
    int64_t position = 0;
    int index = 0;
    while (position < input_frames) {
        const auto count = (std::min)(int64_t(137 + index % 7 * 79), input_frames - position);
        PcmBlock block;
        block.lane = block.source = "mic"; block.generation = 1;
        block.channels = 1; block.sample_rate = 44100;
        block.start_us = video.start_us + audio_time(position, 44100) + (index == 0 ? 0 : index % 2 ? 1 : -1);
        for (int64_t i = 0; i < count; ++i)
            block.samples.push_back(float(.3 * std::sin(2 * 3.141592653589793 * 997 * (position + i) / 44100)));
        timing.submit(std::move(block), position);
        position += count; ++index;
    }
    timing.finish();
    for (size_t i = 1; i < video.packets.size(); ++i)
        require(session.video(video.packets[i].generation, *video.packets[i].packet), "Timed microphone session rejected video");
    require(session.stop() && session.status().error.empty(), "Timed microphone session failed to finalize");
    auto snapshot = history.snapshot(video.start_us, video.end_us).get(); history.stop();
    std::promise<AudioSnapshot> promise; promise.set_value(snapshot);
    ReplaySaveRequest request;
    request.id = "timed-mic-replay"; request.output = root / L"timed-mic-replay.mp4";
    request.work_directory = root; request.ffmpeg = bundled_ffmpeg(); request.video = video;
    request.audio = promise.get_future().share(); request.lanes = {{"mic", "Microphone", 1, 1, false}};
    SaveCoordinator coordinator;
    const auto saved = coordinator.begin(std::move(request)).get();
    require(saved.error.empty(), "Timed microphone replay failed to save");
    for (const auto& path : {saved.output, root / L"timed-mic-session.mkv"}) {
        std::atomic_bool cancel = false;
        const auto decoded = ProcessRunner::run(bundled_ffmpeg(), {L"-v", L"error", L"-nostdin", L"-i", path.wstring(),
            L"-map", L"0:a:0", L"-ac", L"1", L"-ar", L"48000", L"-f", L"f32le", L"pipe:1"}, cancel);
        std::vector<float> samples(decoded.output.size() / sizeof(float));
        std::memcpy(samples.data(), decoded.output.data(), samples.size() * sizeof(float));
        require(samples.size() > 9600, "Saved microphone audio too short");
        double maximum_curvature = 0, energy = 0;
        for (size_t i = 2401; i + 2400 < samples.size(); ++i) {
            require(std::isfinite(samples[i]), "Saved microphone has invalid samples");
            maximum_curvature = (std::max)(maximum_curvature,
                std::abs(double(samples[i + 1]) - 2 * samples[i] + samples[i - 1]));
            energy += samples[i] * samples[i];
        }
        require(energy / (samples.size() - 4801) > .02, "Saved microphone lost its signal");
        // The 997 Hz fixture has curvature .0051. Leave room for AAC error,
        // while rejecting abrupt packet-edge holes in newly saved audio.
        require(maximum_curvature < .02, "Newly saved microphone audio contains a packet-edge glitch");
        std::cout << path.filename().string() << " glitch check: " << maximum_curvature << '\n';
    }
}
void audio_test(const std::filesystem::path& root){
    AudioSnapshot pinned;
    {
        AudioHistory history(root/L"history",1000000);
        require(history.submit(pcm(0,1,.25f)),"First PCM rejected");
        require(history.submit(pcm(50000,1,.5f)),"Overlapping PCM rejected");
        require(history.submit(pcm(200000,2,.75f)),"Replacement PCM rejected");
        auto barrier=history.snapshot(0,300000);
        require(history.submit(pcm(300000,2,1.f)),"Post-barrier PCM rejected");
        pinned=barrier.get();require(pinned.ranges.size()==2,"Snapshot did not pin exact generations");
        history.stop();require(history.error().empty(),"Audio history failed");auto final_snapshot=history.snapshot(0,400000).get();require(final_snapshot.ranges.size()==2&&final_snapshot.ranges.back().frame_count==9600,"Final post-stop audio boundary lost accepted packet");
    }
    std::atomic_bool cancel=false;auto tracks=render_audio(pinned,{{"game","Game Audio",2,1,false}},root/L"render",cancel);
    require(tracks.size()==1,"Missing logical lane");
    require(std::abs(sample(tracks[0].second,0)-.25f)<.0001,"First samples changed");
    require(std::abs(sample(tracks[0].second,6000)-.5f)<.0001,"Overlap not trimmed");
    require(sample(tracks[0].second,8000)==0,"Audio silence gap not preserved");
    require(std::abs(sample(tracks[0].second,12000)-.75f)<.0001,"Replacement source lost");
    cancel=true;bool cancelled=false;try{render_audio(pinned,{{"game","Game Audio",2,1,false}},root/L"cancel",cancel);}catch(...){cancelled=true;}require(cancelled,"Audio cancellation ignored");
    AudioHistory bound(root/L"bound",1000000);require(!bound.submit(pcm(0,1,1.f,480001)),"Ten-second audio bound ignored");require(bound.lost_samples()==960002,"Lost audio accounting wrong");bound.stop();
    AudioHistory failed(root/L"failure",1000000,{[]{throw std::runtime_error("Injected disk failure");},{}});
    failed.submit(pcm(0,1,.5f));auto failed_snapshot=failed.snapshot(0,100000);bool failed_barrier=false;try{failed_snapshot.get();}catch(...){failed_barrier=true;}require(failed_barrier,"Writer failure did not fail snapshot barrier");failed.stop();
    std::promise<void> entered,release;auto released=release.get_future().share();
    AudioHistory blocked(root/L"blocked",1000000,{[&]{entered.set_value();released.wait();},{}});
    blocked.submit(pcm(0,1,.5f,480000));entered.get_future().wait();require(!blocked.submit(pcm(10000000,1,.5f,1)),"In-flight buffer excluded from audio bound");release.set_value();blocked.stop();
    AudioHistory mixed(root/L"mixed",1000000);auto first=pcm(0,1,.2f,4410);first.sample_rate=44100;first.channels=1;first.samples.resize(4410);first.source="first";auto second=first;second.source="second";second.samples.assign(4410,.3f);mixed.submit(std::move(first));mixed.submit(std::move(second));auto mixture=mixed.snapshot(0,100000).get();cancel=false;auto rendered=render_audio(mixture,{{"game","Game Audio",2,1.5f,false},{"spotify","Spotify",2,1,true}},root/L"mix-output",cancel);require(rendered.size()==1,"Silent Spotify lane retained");require(std::abs(sample(rendered[0].second,2400)-float(.75/std::sqrt(2.0)))<.002,"Resample/mixed-source gain incorrect");auto quiet=pcm(0,1,.000002f);quiet.lane="spotify";quiet.source="spotify";mixed.submit(std::move(quiet));auto quiet_snapshot=mixed.snapshot(0,100000).get();auto quiet_tracks=render_audio(quiet_snapshot,{{"spotify","Spotify",2,1,true}},root/L"quiet-output",cancel);require(quiet_tracks.size()==1,"Quiet meaningful Spotify samples were omitted");mixed.stop();
}
void video_test(const std::filesystem::path& root){
    VideoEncoderConfig config;config.width=64;config.height=64;config.fps=30;config.name="libx264";
    VideoEncoder encoder(config);
    auto generation=std::make_shared<CaptureGeneration>();generation->id=1;generation->time_base=encoder.context().time_base;
    auto* parameters=avcodec_parameters_alloc();require(parameters!=nullptr,"Cannot allocate parameters");require(avcodec_parameters_from_context(parameters,&encoder.context())>=0,"Cannot copy parameters");generation->codec={parameters,CaptureCodecParametersDeleter{}};
    VideoHistory history(1000000);
    AVFrame* frame=av_frame_alloc();require(frame!=nullptr,"Cannot allocate frame");frame->format=encoder.context().pix_fmt;frame->width=64;frame->height=64;require(av_frame_get_buffer(frame,32)>=0,"Cannot allocate frame pixels");
    for(int i=0;i<45;++i){require(av_frame_make_writable(frame)>=0,"Frame writable failed");for(int y=0;y<64;++y)memset(frame->data[0]+y*frame->linesize[0],16+i,64);for(int y=0;y<32;++y)memset(frame->data[1]+y*frame->linesize[1],128,64);frame->pts=int64_t(i)*1000000/30;for(auto& packet:encoder.submit(*frame))history.append(generation,std::move(packet),frame->pts,true);}
    av_frame_free(&frame);for(auto& packet:encoder.finish())history.append(generation,std::move(packet));
    auto snapshot=history.snapshot(500000,1400000);require(!snapshot.packets.empty(),"Video snapshot empty");require(snapshot.packets.front().packet->flags&AV_PKT_FLAG_KEY,"Video cut not at keyframe");history.clear();
    std::atomic_bool cancel=false;auto output=root/L"replay.mp4";remux_video(snapshot,output,cancel);
    AVFormatContext* input=nullptr;auto p=output.u8string();std::string path(p.begin(),p.end());require(avformat_open_input(&input,path.c_str(),nullptr,nullptr)>=0,"Saved video cannot open");require(avformat_find_stream_info(input,nullptr)>=0,"Saved video cannot probe");require(input->duration>0&&input->nb_streams==1,"Saved video invalid");avformat_close_input(&input);
    decode_and_seek(output,1);
    check_replay_audio(root,snapshot);
    check_timed_microphone_save(root,snapshot);
    FullSessionWriter session({root/L"session.mkv",{{"game","Game Audio",2,1,false}}},generation);
    for(const auto& packet:snapshot.packets){require(session.video(generation,*packet.packet),"Session video rejected");auto block=pcm(packet.packet->pts,packet.packet->pts<700000?1:2,.1f,1470);block.sample_rate=44100;block.channels=1;block.samples.resize(1470);require(session.audio(std::move(block)),"Session PCM rejected");}
    require(session.stop(),"Session did not stop");require(session.status().error.empty(),"Session mux failed");
    require(session.status().converted_audio_frames==snapshot.packets.size()*1600,"Session resampler tail lost samples during generation replacement or finalization");
    require(!std::filesystem::exists(root/L"session.mkv.interrupted"),"Clean session left interrupted marker");
    decode_and_seek(root/L"session.mkv",2);
}
void process_test(){wchar_t executable[32768]{};require(GetModuleFileNameW(nullptr,executable,DWORD(std::size(executable)))>0,"Cannot resolve test executable");std::atomic_bool cancel=false;bool failed=false;try{ProcessRunner::run(executable,{L"--fail-child"},cancel);}catch(...){failed=true;}require(failed,"Child process failure was ignored");auto result=ProcessRunner::run(executable,{L"--fail-child"},cancel,std::chrono::seconds(5),{},false);require(result.exit_code==7&&result.error.find("injected")!=std::string::npos,"Child stderr/exit code not retained");std::jthread canceller([&]{std::this_thread::sleep_for(std::chrono::milliseconds(100));cancel=true;});auto start=std::chrono::steady_clock::now();bool cancelled=false;try{ProcessRunner::run(executable,{L"--wait-child"},cancel);}catch(...){cancelled=true;}require(cancelled&&std::chrono::steady_clock::now()-start<std::chrono::seconds(5),"Child cancellation did not terminate promptly");}
void timeline_test(){auto generation=std::make_shared<CaptureGeneration>();generation->id=9;generation->codec={avcodec_parameters_alloc(),CaptureCodecParametersDeleter{}};require(bool(generation->codec),"Cannot allocate timeline codec");VideoHistory history(10000000);for(int i=0;i<10;++i){Packet packet(av_packet_alloc());packet->pts=packet->dts=i*100000;packet->flags=i%3==0?AV_PKT_FLAG_KEY:0;history.append(generation,std::move(packet),1000000+i*200000,false);}auto normal=history.snapshot(1250000,2900000);auto safe=history.snapshot(1250000,2900000,true);require(normal.overlay_mappings.front().source_start_us==1000000,"Normal save lost preceding source keyframe");require(safe.overlay_mappings.front().source_start_us==1600000,"Recovery save included unsafe preceding GOP");require(normal.mappings.front().source_start_us!=normal.overlay_mappings.front().source_start_us,"Audio shortfall correction moved overlay mapping");require(normal.frozen,"All-duplicate video not marked frozen");bool rejected=false;try{history.snapshot(2850000,2900000,true);}catch(...){rejected=true;}require(rejected,"Recovery save without safe keyframe accepted");}
void lane_test(){
    AudioGraphConfig config;
    config.applications={{L"Spotify.exe",1},{L"Guilded",1},{L"Discord.exe",1},{L"zebra.exe",.5f}};
    config.microphone_device_ids={L"default",L"second"};
    auto lanes=recording_audio_lanes(config);
    require(lanes.size()==7,"Logical audio lane count changed");
    require(lanes[0].title=="Game Audio"&&lanes[1].title=="Discord"&&lanes[2].title=="Guilded"&&lanes[3].title=="Microphone 1"&&lanes[4].title=="Microphone 2"&&lanes[5].title=="Spotify"&&lanes[6].title=="zebra","Audio ordering or display names changed");
    require(lanes[5].omit_if_silent&&lanes[6].gain==.5f,"Audio lane policy changed");
    config.system_audio=true;config.game_gain=.75f;
    auto system=recording_audio_lanes(config);
    require(system.size()==3&&system[0].key=="system"&&system[0].title=="All System Audio"&&system[0].gain==.75f,"System audio did not replace game and application lanes");
    require(system[1].title=="Microphone 1"&&system[2].title=="Microphone 2","System audio lost separate microphones");
    config.system_audio=false;
    require(recording_audio_lanes(config).size()==7,"System audio toggle lost saved application selections");
    config.system_audio=true;config.microphone_device_ids.clear();
    require(recording_audio_lanes(config).size()==1,"System audio without microphones created extra tracks");
}
}
int main(int argc,char** argv){if(argc>1&&std::string(argv[1])=="--fail-child"){std::cerr<<"injected child failure\n";return 7;}if(argc>1&&std::string(argv[1])=="--wait-child"){Sleep(60000);return 0;}auto root=std::filesystem::current_path()/(L"audio-save-fixture-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64()));bool owned=false;try{owned=std::filesystem::create_directory(root);require(owned,"Fixture directory already exists");audio_test(root);video_test(root);process_test();timeline_test();lane_test();require(ProcessRunner::quote(L"a b\\")==L"\"a b\\\\\"","Process quoting corrupts trailing slash");std::filesystem::remove_all(root);std::cout<<"Native audio/history/save/session tests passed\n";return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';std::error_code ignored;if(owned)std::filesystem::remove_all(root,ignored);return 1;}}
