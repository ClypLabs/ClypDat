#include "recording_save.h"
#include "recording_process.h"
#include <Windows.h>
#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <fstream>
#include <map>
#include <thread>
#include <stdexcept>
extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/channel_layout.h>
#include <libavutil/opt.h>
#include <libswresample/swresample.h>
}
namespace clypdat {
namespace {
void check(int code,const char* operation){if(code<0){char error[256]{};av_strerror(code,error,sizeof(error));throw std::runtime_error(std::string(operation)+": "+error);}}
std::string utf8(const std::filesystem::path& path){auto text=path.u8string();return std::string(text.begin(),text.end());}
std::wstring wide(const std::string& text){if(text.empty())return {};int count=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),int(text.size()),nullptr,0);if(!count)throw std::runtime_error("Invalid UTF-8 audio lane name");std::wstring result(count,L'\0');MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,text.data(),int(text.size()),result.data(),count);return result;}
struct Format {
    AVFormatContext* value=nullptr;
    ~Format(){if(value){if(value->pb)avio_closep(&value->pb);avformat_free_context(value);}}
};
void open_output(Format& format,const std::filesystem::path& path){auto p=utf8(path);check(avformat_alloc_output_context2(&format.value,nullptr,nullptr,p.c_str()),"Create media muxer");if(!format.value)throw std::runtime_error("Unsupported media container");check(avio_open(&format.value->pb,p.c_str(),AVIO_FLAG_WRITE),"Open media output");}
void header(AVFormatContext* format,bool fragmented){AVDictionary* options=nullptr;if(fragmented&&std::string(format->oformat->name).find("mp4")!=std::string::npos)av_dict_set(&options,"movflags","frag_keyframe+empty_moov+default_base_moof",0);else if(std::string(format->oformat->name).find("mp4")!=std::string::npos)av_dict_set(&options,"movflags","+faststart",0);if(std::string(format->oformat->name).find("matroska")!=std::string::npos)av_dict_set(&options,"cluster_time_limit","1000",0);auto code=avformat_write_header(format,&options);av_dict_free(&options);check(code,"Write media header");}
int64_t packet_us(const HistoryPacket& packet,int64_t timestamp){return av_rescale_q(timestamp,packet.generation->time_base,{1,1000000});}
bool compatible(const CaptureGeneration& a,const CaptureGeneration& b){return a.codec->codec_id==b.codec->codec_id&&a.codec->width==b.codec->width&&a.codec->height==b.codec->height&&a.codec->extradata_size==b.codec->extradata_size&&(a.codec->extradata_size==0||memcmp(a.codec->extradata,b.codec->extradata,a.codec->extradata_size)==0);}
}
VideoHistory::VideoHistory(int64_t retention,VideoHistoryOptions options):retention_us_(retention),options_(options){if(retention<=0)throw std::invalid_argument("Invalid video retention");}
// The recorder's PTS strictly increase within a session: one frame pacer per
// capture run, shared by every encoder generation, and no B-frames. Keyframe
// PTS that go backwards are still handled exactly, by scanning the keyframe
// index until the inversion has been pruned.
void VideoHistory::append(std::shared_ptr<const CaptureGeneration> generation,Packet packet,int64_t acquired_us,bool fresh){
    if(!generation||!generation->codec||!packet)throw std::invalid_argument("Invalid video packet");
    std::shared_ptr<const AVPacket> owned(packet.release(),[](const AVPacket* p){auto mutable_packet=const_cast<AVPacket*>(p);av_packet_free(&mutable_packet);});
    HistoryPacket item{std::move(generation),std::move(owned),acquired_us,fresh};
    const auto pts_us=packet_us(item,item.packet->pts);const bool key=(item.packet->flags&AV_PKT_FLAG_KEY)!=0;
    // Pruned packets are freed after the lock is released.
    std::vector<HistoryPacket> released;
    std::lock_guard lock(mutex_);const auto held=std::chrono::steady_clock::now();
    packets_.push_back(std::move(item));
    const auto cutoff=pts_us-retention_us_;
    if(options_.reference_pruning){
        // Keep the preceding GOP. Pruning to an arbitrary packet breaks safe cuts.
        size_t keep=0;for(size_t i=0;i<packets_.size();++i)if((packets_[i].packet->flags&AV_PKT_FLAG_KEY)&&packet_us(packets_[i],packets_[i].packet->pts)<=cutoff)keep=i;
        stats_.examined+=packets_.size();stats_.pruned+=keep;
        while(keep--)packets_.pop_front();
    }else{
        if(key){
            try{keyframes_.push_back({next_sequence_,pts_us});}catch(...){packets_.pop_back();throw;}
            if(keyframes_.size()>1&&keyframes_[keyframes_.size()-2].pts_us>pts_us)++pts_inversions_;
        }
        ++next_sequence_;
        // The newest keyframe at or before the cutoff in append order, as a
        // scan of every packet would choose it.
        size_t chosen=SIZE_MAX;
        if(!pts_inversions_){
            for(size_t i=0;i<keyframes_.size();++i){++stats_.examined;if(keyframes_[i].pts_us>cutoff)break;chosen=i;}
        }else{
            ++stats_.slow_scans;stats_.examined+=keyframes_.size();
            for(size_t i=0;i<keyframes_.size();++i)if(keyframes_[i].pts_us<=cutoff)chosen=i;
        }
        if(chosen!=SIZE_MAX){
            const auto keep=keyframes_[chosen].sequence;released.reserve(size_t(keep-front_sequence_));
            for(size_t i=0;i<chosen;++i){if(keyframes_[0].pts_us>keyframes_[1].pts_us)--pts_inversions_;keyframes_.pop_front();}
            for(;front_sequence_<keep;++front_sequence_){released.push_back(std::move(packets_.front()));packets_.pop_front();}
            stats_.pruned+=released.size();
        }
        stats_.keyframes_peak=std::max(stats_.keyframes_peak,keyframes_.size());
    }
    ++stats_.appended;
    const auto hold=uint64_t(std::chrono::duration_cast<std::chrono::nanoseconds>(std::chrono::steady_clock::now()-held).count());
    stats_.hold_ns_total+=hold;stats_.hold_ns_max=std::max(stats_.hold_ns_max,hold);hold_ns_[holds_++%hold_ns_.size()]=uint32_t(std::min<uint64_t>(hold,UINT32_MAX));
}
VideoSnapshot VideoHistory::snapshot(int64_t start,int64_t end,bool start_at_or_after,bool variable,int fps)const{
    std::lock_guard lock(mutex_);if(packets_.empty()||end<=start)throw std::runtime_error("No video available for save");
    auto source=[](const HistoryPacket& packet){return packet.acquired_us?packet.acquired_us:packet_us(packet,packet.packet->pts);};
    size_t last=packets_.size();while(last&&source(packets_[last-1])>=end)--last;
    if(!last)throw std::runtime_error("Requested video window is unavailable");
    const auto& latest=*packets_[last-1].generation;
    size_t compatible_start=last-1;while(compatible_start&&compatible(*packets_[compatible_start-1].generation,latest))--compatible_start;
    size_t first=last;
    for(size_t i=compatible_start;i<last;++i)if(packets_[i].packet->flags&AV_PKT_FLAG_KEY){
        auto at=source(packets_[i]);
        if(start_at_or_after){if(at>=start){first=i;break;}}
        else {if(first==last)first=i;if(at<=start)first=i;}
    }
    if(first==last)throw std::runtime_error("Video has no safe retained keyframe");
    VideoSnapshot result;result.packets.assign(packets_.begin()+first,packets_.begin()+last);
    const auto& head=result.packets.front();const auto& tail=result.packets.back();
    const auto first_pts=packet_us(head,head.packet->pts),last_pts=packet_us(tail,tail.packet->pts);
    auto cadence=result.packets.size()>1?last_pts-packet_us(result.packets[result.packets.size()-2],result.packets[result.packets.size()-2].packet->pts):1000000/std::clamp(fps,30,120);
    auto final_hold=capture_final_hold(variable,cadence,end-source(tail));
    result.duration_us=last_pts-first_pts+final_hold;
    result.start_us=source(head);auto requested_duration=(std::max)(int64_t(1000000),end-result.start_us);
    if(requested_duration-result.duration_us>50000)result.start_us=source(tail)-result.duration_us;
    result.end_us=result.start_us+result.duration_us;
    result.generation=latest.id;result.frozen=std::none_of(result.packets.begin(),result.packets.end(),[](const auto& item){return item.fresh;});
    result.mappings.push_back({result.start_us,result.duration_us,0});
    for(size_t i=0;i<result.packets.size();++i){auto& item=result.packets[i];auto pts=packet_us(item,item.packet->pts);auto duration=i+1<result.packets.size()?packet_us(result.packets[i+1],result.packets[i+1].packet->pts)-pts:final_hold;result.overlay_mappings.push_back({source(item),duration,pts-first_pts});}
    return result;
}
void VideoHistory::clear(){
    std::deque<HistoryPacket> released;std::lock_guard lock(mutex_);
    released.swap(packets_);keyframes_.clear();front_sequence_=next_sequence_=0;pts_inversions_=0;
}
VideoHistoryStats VideoHistory::stats()const{
    std::lock_guard lock(mutex_);auto result=stats_;result.packets=packets_.size();result.keyframes=keyframes_.size();
    const auto count=std::min(holds_,hold_ns_.size());result.recent_hold_ns.reserve(count);
    for(size_t i=holds_-count;i<holds_;++i)result.recent_hold_ns.push_back(hold_ns_[i%hold_ns_.size()]);
    return result;
}
void VideoHistory::inspect(const std::function<void(const std::deque<HistoryPacket>&)>& reader)const{std::lock_guard lock(mutex_);reader(packets_);}
void remux_video(const VideoSnapshot& video,const std::filesystem::path& output,const std::atomic_bool& cancel,bool fragmented){
    if(video.packets.empty())throw std::runtime_error("Empty video snapshot");
    Format format;open_output(format,output);auto* stream=avformat_new_stream(format.value,nullptr);if(!stream)throw std::bad_alloc();
    check(avcodec_parameters_copy(stream->codecpar,video.packets.front().generation->codec.get()),"Copy video parameters");stream->codecpar->codec_tag=0;stream->time_base={1,1000000};header(format.value,fragmented);
    const auto& first=video.packets.front();auto origin=packet_us(first,first.packet->dts==AV_NOPTS_VALUE?first.packet->pts:first.packet->dts);
    int64_t last_dts=AV_NOPTS_VALUE;
    for(size_t index=0;index<video.packets.size();++index){const auto& item=video.packets[index];if(cancel.load())throw std::runtime_error("Video save cancelled");Packet packet(av_packet_clone(item.packet.get()));if(!packet)throw std::bad_alloc();av_packet_rescale_ts(packet.get(),item.generation->time_base,stream->time_base);auto next_pts=index+1<video.packets.size()?packet_us(video.packets[index+1],video.packets[index+1].packet->pts):packet_us(video.packets.front(),video.packets.front().packet->pts)+video.duration_us;packet->duration=av_rescale_q((std::max)(int64_t(1),next_pts-packet_us(item,item.packet->pts)),{1,1000000},stream->time_base);const auto offset=av_rescale_q(origin,{1,1000000},stream->time_base);if(packet->pts!=AV_NOPTS_VALUE)packet->pts-=offset;if(packet->dts!=AV_NOPTS_VALUE)packet->dts-=offset;packet->stream_index=stream->index;packet->pos=-1;if(packet->dts!=AV_NOPTS_VALUE&&last_dts!=AV_NOPTS_VALUE&&packet->dts<=last_dts)throw std::runtime_error("Non-increasing video DTS across generations");last_dts=packet->dts;check(av_interleaved_write_frame(format.value,packet.get()),"Write replay packet");}
    check(av_write_trailer(format.value),"Finalize replay video");
}
struct SaveCoordinator::State {mutable std::mutex mutex;bool active=false;std::string id;std::shared_ptr<std::atomic_bool> cancel;};
SaveCoordinator::SaveCoordinator():state_(std::make_shared<State>()){}
SaveCoordinator::~SaveCoordinator()=default;
std::shared_future<ReplaySaveResult> SaveCoordinator::begin(ReplaySaveRequest request){
    static std::atomic_uint64_t sequence{0};
    if(request.id.empty()||!request.output.is_absolute()||!request.work_directory.is_absolute())throw std::invalid_argument("Save requires stable ID and absolute paths");
    request.work_directory/=L"native-save-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(++sequence);
    auto promise=std::make_shared<std::promise<ReplaySaveResult>>();auto future=promise->get_future().share();auto s=state_;auto cancel=std::make_shared<std::atomic_bool>(false);
    {std::lock_guard lock(s->mutex);if(s->active)throw std::runtime_error("A replay save is already in progress");s->active=true;s->id=request.id;s->cancel=cancel;}
    try{std::thread([s,promise,cancel,request=std::move(request)]()mutable{
        ReplaySaveResult result;result.id=request.id;result.output=request.output;result.duration_us=request.video.duration_us;result.generation=request.video.generation;result.frozen=request.video.frozen;result.audio_mappings=request.video.mappings;result.overlay_mappings=request.video.overlay_mappings;
        std::filesystem::path partial=request.output;partial+=L".partial"+request.output.extension().wstring();
        bool published=false,work_created=false;
        try{
            if(request.id.empty()||!request.output.is_absolute()||!request.work_directory.is_absolute())throw std::invalid_argument("Save requires stable ID and absolute paths");
            if(std::filesystem::exists(request.output))throw std::runtime_error("Replay output already exists");
            work_created=std::filesystem::create_directories(request.work_directory);if(!work_created)throw std::runtime_error("Replay work directory already exists");
            auto video=request.work_directory/L"video.mp4";remux_video(request.video,video,*cancel);
            std::vector<std::pair<std::string,std::filesystem::path>> tracks;
            if(request.audio.valid()){
                while(request.audio.wait_for(std::chrono::milliseconds(25))!=std::future_status::ready){if(cancel->load())throw std::runtime_error("Replay save cancelled");}
                auto snapshot=request.audio.get();snapshot.start_us=request.video.start_us;snapshot.end_us=request.video.end_us;
                tracks=render_audio(snapshot,request.lanes,request.work_directory,*cancel);
            }
            std::vector<std::wstring> args{L"-v",L"error",L"-nostdin",L"-y",L"-i",video.wstring()};
            for(const auto& track:tracks){args.push_back(L"-i");args.push_back(track.second.wstring());}
            args.insert(args.end(),{L"-map",L"0:v:0",L"-c:v",L"copy"});
            for(size_t i=0;i<tracks.size();++i){auto title=wide(tracks[i].first);args.insert(args.end(),{L"-map",std::to_wstring(i+1)+L":a:0",L"-metadata:s:a:"+std::to_wstring(i),L"title="+title,L"-metadata:s:a:"+std::to_wstring(i),L"handler_name="+title});}
            args.insert(args.end(),{L"-c:a",L"aac",L"-b:a",L"192k"});if(request.output.extension()==L".mp4")args.insert(args.end(),{L"-movflags",L"+faststart"});args.push_back(partial.wstring());
            ProcessRunner::run(request.ffmpeg,args,*cancel,std::chrono::minutes(10));
            if(cancel->load())throw std::runtime_error("Replay save cancelled");
            if(request.publish_overlays)request.publish_overlays(partial,result.overlay_mappings,*cancel);
            std::filesystem::rename(partial,request.output);published=true;
        }catch(const std::exception& e){result.error=e.what();result.cancelled=cancel->load();}
        std::error_code ignored;if(!published)std::filesystem::remove(partial,ignored);
        // Only remove files created in this request's dedicated work directory.
        if(work_created)std::filesystem::remove_all(request.work_directory,ignored);
        if(request.completed){try{request.completed();}catch(...){}}
        {std::lock_guard lock(s->mutex);s->active=false;s->cancel.reset();}promise->set_value(std::move(result));
    }).detach();}catch(...){std::lock_guard lock(s->mutex);s->active=false;s->cancel.reset();throw;}return future;
}
void SaveCoordinator::cancel(const std::string& id){std::lock_guard lock(state_->mutex);if(state_->active&&state_->id==id)*state_->cancel=true;}
bool SaveCoordinator::busy()const{std::lock_guard lock(state_->mutex);return state_->active;}

namespace {
struct Inspection {int64_t duration=0;std::vector<std::string> identities;};
Inspection inspect(const std::filesystem::path& path){AVFormatContext* raw=nullptr;check(avformat_open_input(&raw,utf8(path).c_str(),nullptr,nullptr),"Inspect session");struct Input{AVFormatContext* p;~Input(){avformat_close_input(&p);}} input{raw};check(avformat_find_stream_info(raw,nullptr),"Inspect session streams");Inspection result;result.duration=raw->duration;for(unsigned i=0;i<raw->nb_streams;++i){auto* stream=raw->streams[i];std::string id=std::to_string(stream->codecpar->codec_type)+":"+std::to_string(stream->codecpar->codec_id);for(const char* key:{"title","handler_name"})if(auto* tag=av_dict_get(stream->metadata,key,nullptr,0))id+=std::string(":")+key+"="+tag->value;result.identities.push_back(std::move(id));}return result;}
}
bool recover_recording(const std::filesystem::path& input,const std::filesystem::path& ffmpeg,const std::atomic_bool& cancel){
    auto temp=input.parent_path()/(input.stem().wstring()+L".recovery-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(GetTickCount64())+input.extension().wstring());
    if(std::filesystem::exists(temp))return false;try{auto before=inspect(input);if(before.identities.empty())return false;std::vector<std::wstring> args{L"-v",L"error",L"-nostdin",L"-y",L"-i",input.wstring(),L"-map",L"0",L"-c",L"copy",L"-map_metadata",L"0"};if(input.extension()==L".mp4")args.insert(args.end(),{L"-movflags",L"+faststart"});args.push_back(temp.wstring());ProcessRunner::run(ffmpeg,args,cancel,std::chrono::hours(1));auto after=inspect(temp);if(after.duration<=0||before.identities!=after.identities)throw std::runtime_error("Recovered session stream identity mismatch");if(!ReplaceFileW(input.c_str(),temp.c_str(),nullptr,REPLACEFILE_IGNORE_MERGE_ERRORS,nullptr,nullptr))throw std::runtime_error("Cannot replace recovered session");std::filesystem::remove(input.wstring()+L".interrupted");return true;}catch(...){std::error_code ignored;std::filesystem::remove(temp,ignored);return false;}
}

struct FullSessionWriter::State {
    struct Work {std::shared_ptr<const CaptureGeneration> generation;Packet packet;PcmBlock pcm;size_t bytes=0;};
    struct Lane {AudioLaneConfig config;CodecContext codec;AVStream* stream=nullptr;int64_t next=0;std::map<int64_t,std::vector<float>> blocks;};
    struct Converter {SwrContext* swr=nullptr;Lane* lane=nullptr;std::string logical;int rate=0,channels=0;int64_t next=0;bool anchored=false;~Converter(){swr_free(&swr);}};
    FullSessionConfig config;std::shared_ptr<const CaptureGeneration> generation;
    mutable std::mutex mutex;std::condition_variable ready,exited;std::deque<Work> queue;size_t queued=0;bool closed=false,done=false;FullSessionStatus status;
    Format format;AVStream* video_stream=nullptr;std::vector<Lane> lanes;int64_t origin=AV_NOPTS_VALUE,video_end=0;HANDLE lease=INVALID_HANDLE_VALUE;
    std::map<std::pair<std::string,uint64_t>,std::unique_ptr<Converter>> converters;
    std::map<std::string,int64_t> source_ends;
    std::map<std::string,uint64_t> source_generations;
    std::deque<PcmBlock> early_audio;size_t early_bytes=0;
    ~State(){if(lease!=INVALID_HANDLE_VALUE)CloseHandle(lease);}
    void initialize(){
        lease=CreateFileW((config.output.wstring()+L".recording").c_str(),GENERIC_READ|GENERIC_WRITE,FILE_SHARE_DELETE,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);if(lease==INVALID_HANDLE_VALUE)throw std::runtime_error("Full-session recording lease unavailable");
        std::ofstream(config.output.wstring()+L".interrupted").put('\n');open_output(format,config.output);format.value->flags|=AVFMT_FLAG_FLUSH_PACKETS;
        video_stream=avformat_new_stream(format.value,nullptr);if(!video_stream)throw std::bad_alloc();check(avcodec_parameters_copy(video_stream->codecpar,generation->codec.get()),"Copy session video parameters");video_stream->codecpar->codec_tag=0;video_stream->time_base=generation->time_base;
        for(auto& config_lane:config.lanes){Lane lane;lane.config=config_lane;auto* codec=avcodec_find_encoder(AV_CODEC_ID_AAC);if(!codec)throw std::runtime_error("AAC encoder unavailable");lane.codec.reset(avcodec_alloc_context3(codec));if(!lane.codec)throw std::bad_alloc();lane.codec->sample_rate=48000;lane.codec->sample_fmt=AV_SAMPLE_FMT_FLTP;av_channel_layout_default(&lane.codec->ch_layout,config_lane.channels);lane.codec->bit_rate=192000;lane.codec->time_base={1,48000};if(format.value->oformat->flags&AVFMT_GLOBALHEADER)lane.codec->flags|=AV_CODEC_FLAG_GLOBAL_HEADER;check(avcodec_open2(lane.codec.get(),codec,nullptr),"Open session AAC encoder");lane.stream=avformat_new_stream(format.value,nullptr);if(!lane.stream)throw std::bad_alloc();check(avcodec_parameters_from_context(lane.stream->codecpar,lane.codec.get()),"Copy session AAC parameters");lane.stream->time_base={1,48000};av_dict_set(&lane.stream->metadata,"title",config_lane.title.c_str(),0);av_dict_set(&lane.stream->metadata,"handler_name",config_lane.title.c_str(),0);lanes.push_back(std::move(lane));}
        header(format.value,true);std::lock_guard lock(mutex);status.running=true;
    }
    void receive(Lane& lane){Packet packet(av_packet_alloc());if(!packet)throw std::bad_alloc();for(;;){auto code=avcodec_receive_packet(lane.codec.get(),packet.get());if(code==AVERROR(EAGAIN)||code==AVERROR_EOF)break;check(code,"Receive session AAC packet");av_packet_rescale_ts(packet.get(),lane.codec->time_base,lane.stream->time_base);packet->stream_index=lane.stream->index;check(av_interleaved_write_frame(format.value,packet.get()),"Write session AAC");}}
    void encode_audio(Lane& lane,int64_t through,bool finish){const int block=lane.codec->frame_size;while(lane.next+block<=through||(finish&&lane.next<through)){AVFrame* raw=av_frame_alloc();if(!raw)throw std::bad_alloc();struct Frame{AVFrame* p;~Frame(){av_frame_free(&p);}} frame{raw};raw->format=lane.codec->sample_fmt;raw->sample_rate=48000;raw->nb_samples=block;raw->pts=lane.next;check(av_channel_layout_copy(&raw->ch_layout,&lane.codec->ch_layout),"Set AAC channels");check(av_frame_get_buffer(raw,0),"Allocate session audio frame");auto found=lane.blocks.find(lane.next/block);for(int ch=0;ch<lane.config.channels;++ch){auto* samples=reinterpret_cast<float*>(raw->data[ch]);for(int i=0;i<block;++i)samples[i]=found==lane.blocks.end()?0:found->second[size_t(i)*lane.config.channels+ch];}if(found!=lane.blocks.end())lane.blocks.erase(found);int code=avcodec_send_frame(lane.codec.get(),raw);if(code==AVERROR(EAGAIN)){receive(lane);code=avcodec_send_frame(lane.codec.get(),raw);}check(code,"Send session AAC frame");receive(lane);lane.next+=block;}}
    void append_samples(Converter& source,int64_t start,const std::vector<float>& samples,int count){
        {std::lock_guard lock(mutex);status.converted_audio_frames+=count;}
        auto& lane=*source.lane;const int block=lane.codec->frame_size;auto& prior_end=source_ends[source.logical];
        for(int i=0;i<count;++i){auto position=start+i;if(position<lane.next||position<0||position<prior_end)continue;if(position-lane.next>480000)throw std::runtime_error("Full-session secondary audio bound exceeded");auto& target=lane.blocks[position/block];if(target.empty())target.resize(size_t(block)*lane.config.channels);for(int ch=0;ch<lane.config.channels;++ch)target[size_t(position%block)*lane.config.channels+ch]+=samples[size_t(i)*lane.config.channels+ch]*std::clamp(lane.config.gain,0.f,1.5f);}
        prior_end=(std::max)(prior_end,start+count);
    }
    void flush_source(Converter& source){
        if(!source.anchored)return;int capacity=swr_get_out_samples(source.swr,0);check(capacity,"Measure session resampler tail");if(!capacity)return;
        std::vector<float> samples(size_t(capacity)*source.lane->config.channels);uint8_t* output=reinterpret_cast<uint8_t*>(samples.data());int count=swr_convert(source.swr,&output,capacity,nullptr,0);check(count,"Drain session resampler tail");append_samples(source,source.next,samples,count);source.next+=count;
    }
    void audio(PcmBlock& pcm){
        if(origin==AV_NOPTS_VALUE){auto bytes=pcm.samples.size()*4;if(early_bytes+bytes>16*1024*1024)throw std::runtime_error("Full-session early audio bound exceeded");early_bytes+=bytes;early_audio.push_back(std::move(pcm));return;}
        auto found=std::find_if(lanes.begin(),lanes.end(),[&](const Lane& l){return l.config.key==pcm.lane;});if(found==lanes.end())return;auto& lane=*found;
        auto logical=pcm.lane+":"+pcm.source;auto& latest=source_generations[logical];if(pcm.generation<latest)return;
        if(pcm.generation>latest){for(auto it=converters.begin();it!=converters.end();){if(it->first.first==logical){flush_source(*it->second);it=converters.erase(it);}else ++it;}latest=pcm.generation;}
        auto key=std::make_pair(logical,pcm.generation);auto& converter=converters[key];
        if(!converter){converter=std::make_unique<Converter>();converter->lane=&lane;converter->logical=logical;converter->rate=pcm.sample_rate;converter->channels=pcm.channels;AVChannelLayout input{};av_channel_layout_default(&input,pcm.channels);auto code=swr_alloc_set_opts2(&converter->swr,&lane.codec->ch_layout,AV_SAMPLE_FMT_FLT,48000,&input,AV_SAMPLE_FMT_FLT,pcm.sample_rate,0,nullptr);av_channel_layout_uninit(&input);check(code,"Allocate session resampler");check(swr_init(converter->swr),"Initialize session resampler");}
        auto& source=*converter;if(source.rate!=pcm.sample_rate||source.channels!=pcm.channels)throw std::runtime_error("Session PCM format changed without generation");
        int count=int(pcm.samples.size()/pcm.channels);int capacity=swr_get_out_samples(source.swr,count);std::vector<float> samples(size_t(capacity)*lane.config.channels);const uint8_t* src=reinterpret_cast<const uint8_t*>(pcm.samples.data());uint8_t* dst=reinterpret_cast<uint8_t*>(samples.data());int actual=swr_convert(source.swr,&dst,capacity,&src,count);check(actual,"Convert session audio");
        auto timestamp=(pcm.start_us-origin)*48000/1000000;auto start=source.anchored?source.next:timestamp;if(!source.anchored||std::abs(timestamp-source.next)>4800)start=timestamp;source.anchored=true;source.next=start+actual;
        append_samples(source,start,samples,actual);
    }
    void video(Work& work){if(work.generation->id!=generation->id)throw std::runtime_error("Full-session encoder generation changed; rotate output");auto& packet=*work.packet;auto pts=av_rescale_q(packet.pts,generation->time_base,{1,1000000});if(origin==AV_NOPTS_VALUE){if(!(packet.flags&AV_PKT_FLAG_KEY))return;origin=pts;while(!early_audio.empty()){auto pending=std::move(early_audio.front());early_audio.pop_front();audio(pending);}early_bytes=0;}av_packet_rescale_ts(&packet,generation->time_base,video_stream->time_base);auto offset=av_rescale_q(origin,{1,1000000},video_stream->time_base);if(packet.pts!=AV_NOPTS_VALUE)packet.pts-=offset;if(packet.dts!=AV_NOPTS_VALUE)packet.dts-=offset;packet.stream_index=video_stream->index;video_end=pts-origin+av_rescale_q(packet.duration,video_stream->time_base,{1,1000000});check(av_interleaved_write_frame(format.value,&packet),"Write full-session video");for(auto& lane:lanes)encode_audio(lane,(std::max)(int64_t(0),video_end*48000/1000000-48000),false);avio_flush(format.value->pb);check(format.value->pb->error,"Flush full-session file");std::lock_guard lock(mutex);status.duration_us=video_end;}
    void run(){try{initialize();for(;;){Work work;{std::unique_lock lock(mutex);ready.wait(lock,[&]{return closed||!queue.empty();});if(queue.empty())break;work=std::move(queue.front());queue.pop_front();}if(work.packet)video(work);else audio(work.pcm);{std::lock_guard lock(mutex);queued-=work.bytes;}}for(auto& [key,source]:converters)flush_source(*source);for(auto& lane:lanes){encode_audio(lane,video_end*48000/1000000,true);int code=avcodec_send_frame(lane.codec.get(),nullptr);if(code==AVERROR(EAGAIN)){receive(lane);code=avcodec_send_frame(lane.codec.get(),nullptr);}check(code,"Flush session AAC");receive(lane);}check(av_write_trailer(format.value),"Finalize full session");avio_flush(format.value->pb);check(format.value->pb->error,"Flush finalized full-session file");if(format.value->pb)avio_closep(&format.value->pb);std::filesystem::remove(config.output.wstring()+L".interrupted");}catch(const std::exception& e){std::lock_guard lock(mutex);status.error=e.what();}if(format.value&&format.value->pb)avio_closep(&format.value->pb);if(lease!=INVALID_HANDLE_VALUE){DeleteFileW((config.output.wstring()+L".recording").c_str());CloseHandle(lease);lease=INVALID_HANDLE_VALUE;} {std::lock_guard lock(mutex);closed=true;done=true;status.running=false;status.finished=true;queue.clear();queued=0;}exited.notify_all();}
    bool admit(Work work){std::lock_guard lock(mutex);if(closed)return false;if(queue.size()>=config.queue_items||queued+work.bytes>config.queue_bytes){closed=true;status.error="Full-session 64 MiB/8192-item queue bound exceeded";ready.notify_one();return false;}queued+=work.bytes;queue.push_back(std::move(work));ready.notify_one();return true;}
};
FullSessionWriter::FullSessionWriter(FullSessionConfig config,std::shared_ptr<const CaptureGeneration> generation):state_(std::make_shared<State>()){if(!generation||!generation->codec)throw std::invalid_argument("Session requires encoder generation");state_->config=std::move(config);state_->generation=std::move(generation);std::thread([state=state_]{state->run();}).detach();}
FullSessionWriter::~FullSessionWriter(){stop();}
bool FullSessionWriter::video(std::shared_ptr<const CaptureGeneration> generation,const AVPacket& packet){Packet copy(av_packet_clone(&packet));if(!copy)return false;return state_->admit({std::move(generation),std::move(copy),{},size_t(packet.size)});}
bool FullSessionWriter::audio(PcmBlock block){auto size=block.samples.size()*4;return state_->admit({{}, {},std::move(block),size});}
bool FullSessionWriter::stop(){auto s=state_;std::unique_lock lock(s->mutex);s->closed=true;s->ready.notify_one();if(!s->exited.wait_for(lock,std::chrono::seconds(10),[&]{return s->done;})){s->status.error="Full-session shutdown timed out; resource graph retained; worker restart required";return false;}return true;}
FullSessionStatus FullSessionWriter::status()const{std::lock_guard lock(state_->mutex);return state_->status;}
}
