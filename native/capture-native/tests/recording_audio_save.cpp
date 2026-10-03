#include "recording_audio.h"
#include "recording_audio_codec.h"
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
PcmBlock tone(const char* lane,int channels,double frequency,float amplitude,int64_t start_us,int64_t frames){PcmBlock block;block.lane=lane;block.source=lane;block.generation=1;block.start_us=start_us;block.sample_rate=48000;block.channels=channels;block.samples.resize(size_t(frames)*channels);for(int64_t i=0;i<frames;++i)for(int ch=0;ch<channels;++ch)block.samples[size_t(i)*channels+ch]=float(amplitude*std::sin(2*3.141592653589793*frequency*double(i)/48000));return block;}
void decode_and_seek(const std::filesystem::path& path,int expected_streams){auto p=path.u8string();std::string utf8(p.begin(),p.end());AVFormatContext* input=nullptr;require(avformat_open_input(&input,utf8.c_str(),nullptr,nullptr)>=0,"Cannot open media");struct Input{AVFormatContext* p;~Input(){avformat_close_input(&p);}} cleanup{input};require(avformat_find_stream_info(input,nullptr)>=0,"Cannot probe media");require(int(input->nb_streams)==expected_streams,"Incorrect saved stream count");int video=av_find_best_stream(input,AVMEDIA_TYPE_VIDEO,-1,-1,nullptr,0);require(video>=0,"Saved video stream missing");const auto* decoder=avcodec_find_decoder(input->streams[video]->codecpar->codec_id);CodecContext context(avcodec_alloc_context3(decoder));require(context&&avcodec_parameters_to_context(context.get(),input->streams[video]->codecpar)>=0&&avcodec_open2(context.get(),decoder,nullptr)>=0,"Cannot open saved decoder");Packet packet(av_packet_alloc());AVFrame* frame=av_frame_alloc();require(frame!=nullptr,"Cannot allocate decode frame");int decoded=0;while(av_read_frame(input,packet.get())>=0){if(packet->stream_index==video){require(avcodec_send_packet(context.get(),packet.get())>=0,"Saved packet rejected by decoder");while(avcodec_receive_frame(context.get(),frame)>=0){++decoded;av_frame_unref(frame);}}av_packet_unref(packet.get());}require(decoded>0,"Saved file produced no decoded video");require(av_seek_frame(input,-1,input->duration/2,AVSEEK_FLAG_BACKWARD)>=0,"Saved file cannot seek");avcodec_flush_buffers(context.get());bool sought=false;while(!sought&&av_read_frame(input,packet.get())>=0){if(packet->stream_index==video&&avcodec_send_packet(context.get(),packet.get())>=0)sought=avcodec_receive_frame(context.get(),frame)>=0;av_packet_unref(packet.get());}av_frame_free(&frame);require(sought,"Saved seek produced no frame");if(expected_streams>1){auto* title=av_dict_get(input->streams[1]->metadata,"title",nullptr,0);auto* handler=av_dict_get(input->streams[1]->metadata,"handler_name",nullptr,0);require((title&&std::string(title->value)=="Game Audio")||(handler&&std::string(handler->value)=="Game Audio"),"Saved named audio lane missing");}}
std::filesystem::path bundled_ffmpeg(){return std::filesystem::path(__FILE__).parent_path().parent_path().parent_path()/L"vendor"/L"ffmpeg"/L"ffmpeg.exe";}
// Interleaved 48 kHz PCM of one saved audio stream, placed by timestamp from
// presentation zero, as a player would.
std::vector<float> decoded(const std::filesystem::path& path,int audio_index,int channels=2){
    std::atomic_bool cancel=false;
    auto result=ProcessRunner::run(bundled_ffmpeg(),{L"-v",L"error",L"-nostdin",L"-i",path.wstring(),L"-map",L"0:a:"+std::to_wstring(audio_index),L"-af",L"aresample=async=1:first_pts=0",L"-ac",std::to_wstring(channels),L"-ar",L"48000",L"-f",L"f32le",L"pipe:1"},cancel);
    std::vector<float> samples(result.output.size()/sizeof(float));std::memcpy(samples.data(),result.output.data(),samples.size()*sizeof(float));return samples;
}
// RMS over the steady middle of a stereo stream, skipping codec edges.
double rms(const std::vector<float>& samples){require(samples.size()>2*19200,"Decoded audio too short");double sum=0;size_t count=0;for(size_t i=2*4800;i+2*4800<samples.size();++i,++count)sum+=double(samples[i])*samples[i];return std::sqrt(sum/double(count));}
bool close_to(double value,double expected,double tolerance=.15){return std::abs(value-expected)<=expected*tolerance;}
AVCodecID codec_id(AudioCodec codec){return codec==AudioCodec::Aac?AV_CODEC_ID_AAC:codec==AudioCodec::Vorbis?AV_CODEC_ID_VORBIS:AV_CODEC_ID_OPUS;}
std::filesystem::path save_replay(const std::filesystem::path& root,const std::string& id,AudioCodec codec,const VideoSnapshot& video,const AudioSnapshot& snapshot,std::vector<AudioLaneConfig> lanes){
    std::promise<AudioSnapshot> promise;promise.set_value(snapshot);
    ReplaySaveRequest request;request.id=id;request.output=root/(id+(audio_codec_needs_matroska(codec)?".mkv":".mp4"));request.video=video;request.audio=promise.get_future().share();request.lanes=std::move(lanes);
    SaveCoordinator coordinator;auto result=coordinator.begin(std::move(request)).get();if(!result.error.empty())throw std::runtime_error("Replay save failed: "+result.error);return result.output;
}
struct Opened{AVFormatContext* p=nullptr;explicit Opened(const std::filesystem::path& path){auto text=path.u8string();std::string utf8(text.begin(),text.end());require(avformat_open_input(&p,utf8.c_str(),nullptr,nullptr)>=0&&avformat_find_stream_info(p,nullptr)>=0,"Cannot open saved replay");}~Opened(){avformat_close_input(&p);}};
std::string label(const AVStream* stream){for(const char* key:{"title","handler_name"})if(auto* tag=av_dict_get(stream->metadata,key,nullptr,0))return tag->value;return {};}
void check_replay_audio(const std::filesystem::path& root,const VideoSnapshot& video,AudioCodec codec){
    const auto name=std::string(audio_codec_label(codec));const auto frames=audio_frames(video.end_us)-audio_frames(video.start_us);
    // Capture delivers small interleaved packets; the encode lag lets every
    // source land before a block is encoded.
    AudioHistoryOptions options;options.codec=codec;
    AudioHistory history({{"game","Game Audio",2,.5f,false},{"discord","Discord",2,.75f,false},{"mic","Microphone",1,.5f,false}},3000000,options);
    AudioGraphConfig system_config;system_config.system_audio=true;system_config.game_gain=.5f;
    system_config.applications={{L"Discord",1}};system_config.microphone_device_ids={L"default"};
    AudioHistory system(recording_audio_lanes(system_config),3000000,options);
    // Both histories see every route. Each encodes only its configured lanes.
    const std::vector<PcmBlock> routes{tone("game",2,440,.2f,video.start_us,frames),tone("discord",2,660,.2f,video.start_us,frames),
        tone("mic",1,880,.3f,video.start_us,frames),tone("system",2,550,.4f,video.start_us,frames),tone("mic:default",1,990,.3f,video.start_us,frames)};
    for(int64_t offset=0;offset<frames;offset+=960)for(const auto& route:routes)for(auto* target:{&history,&system}){
        auto chunk=route;const auto count=(std::min)(int64_t(960),frames-offset);chunk.start_us=video.start_us+audio_time(offset);
        chunk.samples.assign(route.samples.begin()+offset*route.channels,route.samples.begin()+(offset+count)*route.channels);
        require(target->submit(std::move(chunk)),"Replay audio rejected");
    }
    auto snapshot=history.snapshot(video.start_us,video.end_us).get();history.stop();
    auto system_snapshot=system.snapshot(video.start_us,video.end_us).get();system.stop();
    for(const auto& track:snapshot.tracks){require(track.parameters&&track.parameters->codec_id==codec_id(codec),"Snapshot track used the wrong codec");if(codec==AudioCodec::Opus)require(track.parameters->bit_rate==128000,"Opus track is not 128 kb/s");require(!track.packets.empty(),"Snapshot track has no packets");}
    auto mixed=save_replay(root,"mixed-"+name,codec,video,snapshot,{{"game","Game Audio",2,.5f,false},{"discord","Discord",2,.75f,false},{"mic","Microphone",1,.5f,false}});
    {
        Opened input(mixed);require(input.p->nb_streams==5,"Mixed replay stream count wrong");
        const char* expected[]={"All Tracks","Game Audio","Discord","Microphone"};
        for(int i=0;i<4;++i){auto* stream=input.p->streams[i+1];require(stream->codecpar->codec_type==AVMEDIA_TYPE_AUDIO&&stream->codecpar->codec_id==codec_id(codec)&&label(stream)==expected[i],"Mixed replay stream codec, title or order wrong");require(bool(stream->disposition&AV_DISPOSITION_DEFAULT)==(i==0),"Mixed replay default audio disposition wrong");}
        const auto audio_duration=input.p->streams[1]->duration*av_q2d(input.p->streams[1]->time_base);
        require(std::abs(audio_duration-video.duration_us/1e6)<.05||input.p->streams[1]->duration==AV_NOPTS_VALUE,"Saved audio length does not match video");
    }
    auto all=rms(decoded(mixed,0)),game=rms(decoded(mixed,1)),chat=rms(decoded(mixed,2)),mic=rms(decoded(mixed,3));
    std::cout<<name<<" replay rms all "<<all<<" game "<<game<<" chat "<<chat<<" mic "<<mic<<'\n';
    require(close_to(game,.2*.5/std::sqrt(2.0))&&close_to(chat,.2*.75/std::sqrt(2.0))&&close_to(mic,.3*.5*.70710678/std::sqrt(2.0)),"Separate replay audio gains or sources wrong");
    require(close_to(all,std::sqrt(game*game+chat*chat+mic*mic)),"All Tracks does not contain each separate source once");
    auto single=save_replay(root,"single-"+name,codec,video,snapshot,{{"game","Game Audio",2,.5f,false}});
    {Opened input(single);require(input.p->nb_streams==2,"Single-track replay gained duplicate mix");}
    auto system_replay=save_replay(root,"system-"+name,codec,video,system_snapshot,recording_audio_lanes(system_config));
    {Opened input(system_replay);require(input.p->nb_streams==4,"System-audio replay retained game or app tracks");require(label(input.p->streams[2])=="Full System Audio","System-audio track label lost");}
    auto system_mix=rms(decoded(system_replay,0)),playback=rms(decoded(system_replay,1)),system_mic=rms(decoded(system_replay,2));
    require(close_to(playback,.4*.5/std::sqrt(2.0))&&close_to(system_mic,.3*.70710678/std::sqrt(2.0)),"System-audio capture gain or microphone source wrong");
    require(close_to(system_mix,std::sqrt(playback*playback+system_mic*system_mic)),"System-audio replay mixed duplicate game or app sources");
}
// A tone that starts mid-window lands at the same time in the saved clip:
// preroll before the cut is trimmed, not shifted into the clip.
void check_alignment(const std::filesystem::path& root,const VideoSnapshot& video,AudioCodec codec){
    AudioHistoryOptions options;options.codec=codec;options.encode_lag_us=0;
    AudioHistory history({{"game","Game Audio",2,1,false}},3000000,options);
    const int64_t lead=24000,onset=9600,frames=audio_frames(video.end_us)-audio_frames(video.start_us);
    auto block=tone("game",2,1000,.5f,video.start_us-audio_time(lead),lead+frames);
    for(int64_t i=0;i<lead+onset;++i)block.samples[size_t(i)*2]=block.samples[size_t(i)*2+1]=0;
    require(history.submit(std::move(block)),"Alignment audio rejected");
    auto snapshot=history.snapshot(video.start_us,video.end_us).get();history.stop();
    auto path=save_replay(root,std::string("aligned-")+audio_codec_label(codec),codec,video,snapshot,{{"game","Game Audio",2,1,false}});
    auto samples=decoded(path,0);size_t first=samples.size();
    for(size_t i=0;i<samples.size();i+=2)if(std::abs(samples[i])>.25f){first=i/2;break;}
    // A sine at phase zero crosses half amplitude 1/12 of a cycle after onset.
    const auto error=int64_t(first)-(onset+4);
    std::cout<<audio_codec_label(codec)<<" onset error "<<error<<" frames\n";
    require(std::abs(error)<=48,"Saved audio is not aligned with video within 1 ms");
}
void check_timed_microphone_save(const std::filesystem::path& root, const VideoSnapshot& video) {
    AudioHistoryOptions options; options.encode_lag_us = 0;
    AudioHistory history({{"mic", "Microphone", 1, 1, false}}, 3000000, options);
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
    const auto saved = save_replay(root, "timed-mic-replay", AudioCodec::Opus, video, snapshot, {{"mic", "Microphone", 1, 1, false}});
    for (const auto& path : {saved, root / L"timed-mic-session.mkv"}) {
        auto samples = decoded(path, 0, 1);
        require(samples.size() > 9600, "Saved microphone audio too short");
        double maximum_curvature = 0, energy = 0;
        for (size_t i = 2401; i + 2400 < samples.size(); ++i) {
            require(std::isfinite(samples[i]), "Saved microphone has invalid samples");
            maximum_curvature = (std::max)(maximum_curvature,
                std::abs(double(samples[i + 1]) - 2 * samples[i] + samples[i - 1]));
            energy += samples[i] * samples[i];
        }
        require(energy / (samples.size() - 4801) > .02, "Saved microphone lost its signal");
        // The 997 Hz fixture has curvature .0051. Leave room for codec error,
        // while rejecting abrupt packet-edge holes in newly saved audio.
        require(maximum_curvature < .02, "Newly saved microphone audio contains a packet-edge glitch");
        std::cout << path.filename().string() << " glitch check: " << maximum_curvature << '\n';
    }
}
// Exact placement is checked before encoding, where it is lossless.
void mixer_test(){
    AudioLaneMixer mixer({{"game","Game Audio",2,1,false}},960,480000);mixer.anchor(0);
    std::vector<float> lane;auto drain=[&]{while(mixer.next()<28800){auto blocks=mixer.take();lane.insert(lane.end(),blocks[0].samples.begin(),blocks[0].samples.end());}};
    mixer.submit(pcm(0,1,.25f),0);
    // Within 100 ms of the expected position a source is continuous jitter.
    mixer.submit(pcm(50000,1,.5f),audio_frames(50000));
    mixer.submit(pcm(200000,2,.75f),audio_frames(200000));
    mixer.submit(pcm(150000,1,.9f),audio_frames(150000));
    mixer.submit(pcm(500000,2,.6f),audio_frames(500000));
    drain();auto at=[&](int64_t frame){return lane[size_t(frame)*2];};
    require(std::abs(at(0)-.25f)<.0001,"First samples changed");
    require(std::abs(at(6000)-.5f)<.0001,"Continuous source jitter opened a hole");
    require(std::abs(at(12000)-.75f)<.0001,"Replacement source lost or older generation mixed in");
    require(at(20000)==0,"Audio silence gap not preserved");
    require(std::abs(at(24500)-.6f)<.0001,"Source after a gap misplaced");
    AudioLaneMixer mixed({{"game","Game Audio",2,1.5f,false},{"spotify","Spotify",2,1,true}},960,480000);mixed.anchor(0);
    auto first=pcm(0,1,.2f,4410);first.sample_rate=44100;first.channels=1;first.samples.resize(4410);first.source="first";auto second=first;second.source="second";second.samples.assign(4410,.3f);
    mixed.submit(first,0);mixed.submit(second,0);auto quiet=pcm(0,1,.000002f);quiet.lane="spotify";quiet.source="spotify";
    std::vector<AudioLaneMixer::Block> blocks;for(int i=0;i<3;++i)blocks=mixed.take();
    require(std::abs(blocks[0].samples[0]-float(.75/std::sqrt(2.0)))<.002,"Resample/mixed-source gain incorrect");require(!blocks[1].audible,"Silent Spotify lane reported audible");
    mixed.submit(quiet,mixed.next());require(mixed.take()[1].audible,"Quiet meaningful Spotify samples were treated as silence");
}
void audio_test(const std::filesystem::path& root){
    mixer_test();
    AudioHistoryOptions fast;fast.encode_lag_us=0;
    {
        AudioHistory history({{"game","Game Audio",2,1,false},{"spotify","Spotify",2,1,true}},1000000,fast);
        require(history.submit(pcm(0,1,.25f)),"First PCM rejected");
        auto barrier=history.snapshot(0,100000);
        require(history.submit(pcm(100000,1,1.f)),"Post-barrier PCM rejected");
        auto pinned=barrier.get();require(pinned.tracks.size()==3&&pinned.tracks[2].mix&&pinned.tracks[2].key==kAllTracksKey,"Snapshot did not return every lane and the mix");
        require(!pinned.tracks[0].packets.empty()&&pinned.tracks[0].audible,"Snapshot lost encoded game audio");
        require(!pinned.tracks[1].audible,"Silent Spotify lane reported audible");
        const auto& last=*pinned.tracks[0].packets.back();require(last.pts+last.duration>=4800,"Snapshot does not reach its window end");
        history.stop();require(history.error().empty(),"Audio history failed");
        auto final_snapshot=history.snapshot(0,200000).get();const auto& tail=*final_snapshot.tracks[0].packets.back();
        require(tail.pts+tail.duration>=9600,"Final post-stop audio boundary lost accepted packet");
    }
    AudioHistory bound({{"game","Game Audio",2,1,false}},1000000,fast);require(!bound.submit(pcm(0,1,1.f,1440001)),"Thirty-second audio bound ignored");require(bound.lost_samples()==2880002,"Lost audio accounting wrong");
    require(bound.error().empty(),"Audio bound overflow poisoned the history");require(bound.submit(pcm(0,1,.5f)),"Audio after a shed block rejected");require(!bound.snapshot(0,100000).get().tracks[0].packets.empty(),"Save after a shed block failed");bound.stop();
    AudioHistoryOptions failing=fast;failing.before_write=[]{throw std::runtime_error("Injected encoder failure");};
    AudioHistory failed({{"game","Game Audio",2,1,false}},1000000,failing);
    failed.submit(pcm(0,1,.5f));auto failed_snapshot=failed.snapshot(0,100000);bool failed_barrier=false;try{failed_snapshot.get();}catch(...){failed_barrier=true;}require(failed_barrier,"Encoder failure did not fail snapshot barrier");failed.stop();
    std::promise<void> entered,release;auto released=release.get_future().share();
    AudioHistoryOptions blocking=fast;blocking.before_write=[&]{static bool first=true;if(first){first=false;entered.set_value();released.wait();}};
    AudioHistory blocked({{"game","Game Audio",2,1,false}},1000000,blocking);
    blocked.submit(pcm(0,1,.5f,1440000));entered.get_future().wait();require(!blocked.submit(pcm(30000000,1,.5f,1)),"In-flight buffer excluded from audio bound");release.set_value();
    require(!blocked.snapshot(29000000,30000000).get().tracks[0].packets.empty(),"Stalled encoder poisoned later saves");require(blocked.error().empty(),"Stalled encoder recorded a failure");blocked.stop();
    // A save with no audio at all still gets silent, decodable tracks.
    AudioHistory quiet({{"game","Game Audio",2,1,false}},1000000,fast);
    auto empty=quiet.snapshot(0,100000).get();require(!empty.tracks[0].packets.empty()&&!empty.tracks[0].audible,"Silent history produced no audio packets");quiet.stop();
    for(const auto& entry:std::filesystem::recursive_directory_iterator(root))require(entry.path().extension()!=L".wav","Audio history wrote PCM to disk");
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
    for(auto codec:{AudioCodec::Opus,AudioCodec::Aac,AudioCodec::Vorbis}){check_replay_audio(root,snapshot,codec);check_alignment(root,snapshot,codec);}
    check_timed_microphone_save(root,snapshot);
    for(auto codec:{AudioCodec::Opus,AudioCodec::Aac,AudioCodec::Vorbis})for(const wchar_t* extension:{L".mkv",L".mp4"}){
        if(codec==AudioCodec::Vorbis&&std::wstring(extension)==L".mp4")continue;
        FullSessionConfig config{root/(std::wstring(L"codec-session-")+std::to_wstring(int(codec))+extension),{{"game","Game Audio",2,1,false}}};config.codec=codec;
        FullSessionWriter writer(config,generation);for(const auto& packet:snapshot.packets){require(writer.video(generation,*packet.packet),"Codec session video rejected");require(writer.audio(tone("game",2,440,.2f,packet.packet->pts,1600)),"Codec session PCM rejected");}
        require(writer.stop()&&writer.status().error.empty(),"Codec session failed to finalize");
        Opened input(config.output);require(input.p->nb_streams==2&&input.p->streams[1]->codecpar->codec_id==codec_id(codec),"Full session used the wrong audio codec");
    }
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
    require(system.size()==3&&system[0].key=="system"&&system[0].title=="Full System Audio"&&system[0].gain==.75f,"System audio did not replace game and application lanes");
    require(system[1].title=="Microphone 1"&&system[2].title=="Microphone 2","System audio lost separate microphones");
    config.system_audio=false;
    require(recording_audio_lanes(config).size()==7,"System audio toggle lost saved application selections");
    config.system_audio=true;config.microphone_device_ids.clear();
    require(recording_audio_lanes(config).size()==1,"System audio without microphones created extra tracks");
}
}
int main(int argc,char** argv){if(argc>1&&std::string(argv[1])=="--fail-child"){std::cerr<<"injected child failure\n";return 7;}if(argc>1&&std::string(argv[1])=="--wait-child"){Sleep(60000);return 0;}auto root=std::filesystem::current_path()/(L"audio-save-fixture-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64()));bool owned=false;try{owned=std::filesystem::create_directory(root);require(owned,"Fixture directory already exists");audio_test(root);video_test(root);process_test();timeline_test();lane_test();require(ProcessRunner::quote(L"a b\\")==L"\"a b\\\\\"","Process quoting corrupts trailing slash");std::filesystem::remove_all(root);std::cout<<"Native audio/history/save/session tests passed\n";return 0;}catch(const std::exception& e){std::cerr<<e.what()<<'\n';std::error_code ignored;if(owned)std::filesystem::remove_all(root,ignored);return 1;}}
