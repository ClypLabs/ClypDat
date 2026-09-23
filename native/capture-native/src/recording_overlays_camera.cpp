#include "recording_overlays.h"
#include "recording_process.h"
#include <Windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <set>
#include <regex>
#include <sstream>
#include <locale>
#include <tuple>
#include <fstream>
#include <array>
extern "C" {
#include <libavformat/avformat.h>
}
#include <thread>

namespace clypdat {
std::vector<CameraPreviewModeNative> camera_preview_modes(const std::string& output) {
    static const std::regex option(R"((pixel_format|vcodec)=([^\s]+)[^\r\n]*?max\s+s=(\d+)x(\d+)\s+fps=([\d.]+))",std::regex::icase);
    std::vector<CameraPreviewModeNative> modes;
    for(auto match=std::sregex_iterator(output.begin(),output.end(),option);match!=std::sregex_iterator();++match) {
        try {
            const auto& m=*match;
            CameraPreviewModeNative mode{std::stoi(m[3]),std::stoi(m[4]),std::stod(m[5]),m[2],m[1].str()=="vcodec"};
            if(mode.width>0 && mode.height>0 && mode.fps>0 && std::isfinite(mode.fps) && std::find(modes.begin(),modes.end(),mode)==modes.end()) modes.push_back(mode);
        } catch(...) {}
    }
    auto key=[](const CameraPreviewModeNative& mode) {
        return std::tuple{-(mode.fps>=59?3:mode.fps>=29?2:1),-mode.fps,mode.width>=640 && mode.height>=360?0:1,
            static_cast<int64_t>(mode.width)*mode.height,mode.format=="nv12"?0:mode.compressed?2:1};
    };
    std::stable_sort(modes.begin(),modes.end(),[&](const auto& a,const auto& b) { return key(a)<key(b); });
    return modes;
}
struct RecordingCamera::State {
    std::shared_ptr<OverlayHistory> history;
    OverlayClock clock;
    std::mutex mutex;
    std::condition_variable changed;
    std::atomic_bool cancel{false};
    bool done=true;
    uint64_t generation=0;
    std::string failure;
    std::vector<uint8_t> partial;
    size_t offset=0;
    std::atomic_uint64_t sequence{0};
    void bytes(const uint8_t* data,size_t count) noexcept {
        try {
            while(count && !cancel) {
                const auto take=std::min(count,partial.size()-offset);
                std::copy_n(data,take,partial.data()+offset); offset+=take; data+=take; count-=take;
                if(offset==partial.size()) {
                    history->camera_frame(generation,OverlayBitmap::copy(640,360,640*4,partial.data(),partial.size(),++sequence,clock(),false));
                    offset=0;
                }
            }
        } catch(const std::exception& exception) { std::lock_guard lock(mutex); failure=exception.what(); cancel=true; }
        catch(...) { std::lock_guard lock(mutex); failure="Camera frame allocation failed."; cancel=true; }
    }
    void run(const std::filesystem::path& ffmpeg,const std::filesystem::path& root,const std::wstring& moniker,bool editable,bool settings_preview) noexcept {
        std::jthread watcher;
        std::atomic_bool process_done=false;
        HANDLE notification=INVALID_HANDLE_VALUE;
        try {
            const std::wstring filter=L"scale=640:360:force_original_aspect_ratio=decrease,pad=640:360:(ow-iw)/2:(oh-ih)/2";
            std::vector<std::wstring> arguments{L"-hide_banner",L"-nostdin",L"-f",L"dshow",L"-framerate",L"60",L"-i",L"video="+moniker};
            if(settings_preview) {
                std::vector<CameraPreviewModeNative> modes;
                try {
                    auto probe=ProcessRunner::run(ffmpeg,{L"-hide_banner",L"-list_options",L"true",L"-f",L"dshow",L"-i",L"video="+moniker},cancel,std::chrono::seconds(5),{},false);
                    modes=camera_preview_modes(probe.error);
                } catch(...) { if(cancel) throw; }
                modes.push_back({}); // Device default remains the final candidate.
                for(const auto& mode:modes) {
                    if(cancel) break;
                    arguments={L"-hide_banner",L"-nostdin",L"-f",L"dshow"};
                    if(mode.fps>0) {
                        std::wostringstream rate; rate.imbue(std::locale::classic()); rate<<mode.fps;
                        arguments.insert(arguments.end(),{L"-video_size",std::to_wstring(mode.width)+L"x"+std::to_wstring(mode.height),L"-framerate",rate.str(),
                            mode.compressed?L"-vcodec":L"-pixel_format",std::wstring(mode.format.begin(),mode.format.end())});
                    }
                    arguments.insert(arguments.end(),{L"-i",L"video="+moniker,L"-an",L"-vf",filter,L"-fps_mode",L"passthrough",L"-f",L"rawvideo",L"-pix_fmt",L"bgra",L"pipe:1"});
                    std::atomic_bool attempt_done=false,attempt_cancel=false;
                    auto first_frame_deadline=std::chrono::steady_clock::now()+std::chrono::seconds(10);
                    std::jthread watchdog([&] {
                        while(!attempt_done) {
                            if(cancel || (sequence.load()==0 && std::chrono::steady_clock::now()>=first_frame_deadline)) { attempt_cancel=true; break; }
                            std::this_thread::sleep_for(std::chrono::milliseconds(25));
                        }
                    });
                    try {
                        ProcessRunner::run(ffmpeg,arguments,attempt_cancel,std::chrono::milliseconds::zero(),[this](const uint8_t* data,size_t count) { bytes(data,count); });
                        attempt_done=true; watchdog.join();
                        if(sequence.load()>0 || cancel) break;
                    } catch(...) {
                        attempt_done=true; watchdog.join();
                        if(sequence.load()>0 || cancel || mode.fps==0) throw;
                    }
                    offset=0;
                }
                if(!cancel) { std::lock_guard lock(mutex); failure=sequence.load()==0?"Camera stopped before delivering frames.":"Camera preview stopped."; }
                { std::lock_guard lock(mutex); done=true; } changed.notify_all(); return;
            }
            if(editable) {
                std::filesystem::create_directories(root);
                notification=FindFirstChangeNotificationW(root.c_str(),FALSE,FILE_NOTIFY_CHANGE_FILE_NAME);
                if(notification==INVALID_HANDLE_VALUE) throw std::runtime_error("Camera segment directory notification failed");
                const auto prefix=std::to_wstring(generation)+L"-";
                watcher=std::jthread([&,prefix] {
                    std::set<std::filesystem::path> known;
                    try {
                        while(!process_done) {
                            const auto wait=WaitForSingleObject(notification,50);
                            if(wait==WAIT_OBJECT_0) FindNextChangeNotification(notification);
                            if(wait==WAIT_FAILED) throw std::runtime_error("Camera segment directory wait failed");
                            if(wait!=WAIT_OBJECT_0) continue;
                            const auto observed=clock();
                            std::vector<std::pair<unsigned long long,std::filesystem::path>> created;
                            for(const auto& item:std::filesystem::directory_iterator(root)) {
                                const auto name=item.path().filename().wstring();
                                if(!name.starts_with(prefix) || item.path().extension()!=L".mp4" || known.contains(item.path())) continue;
                                try { created.emplace_back(std::stoull(name.substr(prefix.size())),item.path()); } catch(...) { continue; }
                            }
                            std::sort(created.begin(),created.end());
                            for(const auto& item:created) {
                                known.insert(item.second);
                                history->camera_segment({generation,observed,observed,item.second,false});
                            }
                        }
                    } catch(const std::exception& exception) { std::lock_guard lock(mutex); failure=exception.what(); cancel=true; }
                });
                const std::vector<std::wstring> segment{
                    L"-map",L"0:v:0",L"-an",L"-vf",filter,L"-vsync",L"0",L"-c:v",L"libx264",L"-preset",L"ultrafast",
                    L"-tune",L"zerolatency",L"-g",L"120",L"-bf",L"0",L"-sc_threshold",L"0",L"-f",L"segment",L"-segment_time",L"2",
                    L"-reset_timestamps",L"1",L"-segment_format_options",L"movflags=+frag_keyframe+empty_moov+default_base_moof:frag_duration=100000:flush_packets=1",
                    (root/(prefix+L"%d.mp4")).wstring()};
                arguments.insert(arguments.end(),segment.begin(),segment.end());
            }
            const std::vector<std::wstring> preview{L"-map",L"0:v:0",L"-an",L"-vf",filter,L"-vsync",L"0",L"-f",L"rawvideo",L"-pix_fmt",L"bgra",L"pipe:1"};
            arguments.insert(arguments.end(),preview.begin(),preview.end());
            ProcessRunner::run(ffmpeg,arguments,cancel,std::chrono::milliseconds::zero(),[this](const uint8_t* data,size_t count) { bytes(data,count); });
            if(!cancel && sequence==0) { std::lock_guard lock(mutex); failure="Camera stopped before delivering frames."; }
        } catch(const std::exception& exception) { if(!cancel) { std::lock_guard lock(mutex); failure=exception.what(); } }
        catch(...) { std::lock_guard lock(mutex); failure="Camera capture failed."; }
        process_done=true;
        if(watcher.joinable()) watcher.join();
        if(notification!=INVALID_HANDLE_VALUE) FindCloseChangeNotification(notification);
        try { history->complete_camera(generation,clock()); } catch(...) {}
        { std::lock_guard lock(mutex); done=true; } changed.notify_all();
    }
};
RecordingCamera::RecordingCamera(std::shared_ptr<OverlayHistory> history,OverlayClock clock):state_(std::make_shared<State>()) {
    state_->history=std::move(history); state_->clock=std::move(clock);
    if(!state_->history || !state_->clock) throw std::invalid_argument("Camera history and clock are required");
}
RecordingCamera::~RecordingCamera() { stop(); }
bool RecordingCamera::start(const std::filesystem::path& ffmpeg,const std::filesystem::path& work_root,const std::wstring& device_moniker,bool editable,bool settings_preview) {
    if(!ffmpeg.is_absolute() || !work_root.is_absolute() || device_moniker.empty()) throw std::invalid_argument("Camera requires absolute paths and a device moniker");
    if(!stop()) return false;
    auto state=state_; std::lock_guard lock(state->mutex);
    state->cancel=false; state->failure.clear(); state->offset=0; state->sequence=0;
    state->partial.resize(640*360*4); state->generation=state->history->replace_camera(); state->done=false;
    try { std::thread([state,ffmpeg,work_root,device_moniker,editable,settings_preview] { state->run(ffmpeg,work_root,device_moniker,editable,settings_preview); }).detach(); }
    catch(...) { state->done=true; throw; }
    return true;
}
bool RecordingCamera::stop(uint32_t timeout_ms) {
    auto state=state_; state->cancel=true; std::unique_lock lock(state->mutex);
    return state->changed.wait_for(lock,std::chrono::milliseconds(timeout_ms),[&] { return state->done; });
}
std::string RecordingCamera::error() const { std::lock_guard lock(state_->mutex); return state_->failure; }
bool RecordingCamera::running() const { std::lock_guard lock(state_->mutex); return !state_->done; }

namespace {
uint64_t be32(const uint8_t* value) { return uint64_t(value[0])<<24|uint64_t(value[1])<<16|uint64_t(value[2])<<8|value[3]; }
uint64_t complete_camera_prefix(const std::filesystem::path& path,uint64_t extent) {
    std::ifstream input(path,std::ios::binary); if(!input) return 0;
    uint64_t offset=0,last_media_end=0; bool saw_movie=false;
    while(offset+8<=extent) {
        std::array<uint8_t,16> header{}; input.seekg(static_cast<std::streamoff>(offset));
        if(!input.read(reinterpret_cast<char*>(header.data()),8)) break;
        uint64_t size=be32(header.data()),header_size=8;
        if(size==1) { if(offset+16>extent || !input.read(reinterpret_cast<char*>(header.data()+8),8)) break; size=(be32(header.data()+8)<<32)|be32(header.data()+12); header_size=16; }
        if(size==0 || size<header_size || size>extent-offset) break;
        const auto type=be32(header.data()+4);
        if(type==0x6d6f6f76) saw_movie=true; // moov
        if(type==0x6d646174 && saw_movie) last_media_end=offset+size; // mdat
        offset+=size;
    }
    return last_media_end;
}
std::string path_utf8(const std::filesystem::path& path) {
    const auto text=path.u8string(); return {reinterpret_cast<const char*>(text.data()),text.size()};
}
int64_t camera_duration(const std::filesystem::path& path) {
    AVFormatContext* format=nullptr;
    if(avformat_open_input(&format,path_utf8(path).c_str(),nullptr,nullptr)<0) return 0;
    struct Close { AVFormatContext** value; ~Close() { avformat_close_input(value); } } close{&format};
    if(avformat_find_stream_info(format,nullptr)<0) return 0;
    const auto stream=av_find_best_stream(format,AVMEDIA_TYPE_VIDEO,-1,-1,nullptr,0);
    if(stream<0) return 0;
    auto* video=format->streams[stream];
    if(video->duration>0 && video->duration!=AV_NOPTS_VALUE) return av_rescale_q(video->duration,video->time_base,AVRational{1,1000000});
    return format->duration>0 && format->duration!=AV_NOPTS_VALUE?format->duration:0;
}
}
void finalize_camera_snapshot(OverlaySnapshot& snapshot,const std::filesystem::path& ffmpeg,
    const std::filesystem::path& destination,const std::atomic_bool& cancel) {
    if(!destination.is_absolute()) throw std::invalid_argument("Camera save directory must be absolute");
    size_t sequence=0;
    for(auto& segment:snapshot.camera_segments) {
        if(cancel) throw std::runtime_error("Camera snapshot cancelled");
        segment.completed=false;
        if(!segment.pinned_bytes) continue;
        const auto prefix_size=complete_camera_prefix(segment.path,segment.pinned_bytes);
        if(!prefix_size) continue;
        std::filesystem::path prefix,output;
        try {
            std::filesystem::create_directories(destination);
            const auto name=std::to_wstring(segment.generation)+L"-"+std::to_wstring(sequence++);
            prefix=destination/(name+L".prefix.mp4"); output=destination/(name+L".mp4");
            std::ifstream input(segment.path,std::ios::binary);
            std::ofstream copied(prefix,std::ios::binary|std::ios::trunc);
            if(!input || !copied) throw std::runtime_error("Cannot open pinned camera snapshot");
            std::array<char,65536> buffer{}; uint64_t remaining=prefix_size;
            while(remaining) {
                if(cancel) throw std::runtime_error("Camera snapshot cancelled");
                const auto bytes=static_cast<std::streamsize>(std::min<uint64_t>(remaining,buffer.size()));
                if(!input.read(buffer.data(),bytes) || !copied.write(buffer.data(),bytes)) throw std::runtime_error("Cannot copy pinned camera extent");
                remaining-=bytes;
            }
            copied.close(); input.close();
            ProcessRunner::run(ffmpeg,{L"-hide_banner",L"-nostdin",L"-y",L"-i",prefix.wstring(),L"-map",L"0:v:0",L"-an",L"-c:v",L"copy",L"-movflags",L"+faststart",output.wstring()},cancel,std::chrono::minutes(1));
            const auto duration=camera_duration(output);
            if(duration<=0) throw std::runtime_error("Camera snapshot has no positive video duration");
            auto lease=std::make_shared<CameraFileLease>(); lease->path=output;
            segment.path=output; segment.lease=std::move(lease); segment.end_us=std::min(segment.end_us,segment.start_us+duration);
            segment.completed=segment.end_us>segment.start_us;
        } catch(...) {
            std::error_code ignored; if(!output.empty()) std::filesystem::remove(output,ignored);
            if(cancel) { if(!prefix.empty()) std::filesystem::remove(prefix,ignored); throw; }
            // An optional camera source cannot fail the primary replay save.
        }
        std::error_code ignored; if(!prefix.empty()) std::filesystem::remove(prefix,ignored);
    }
}
}
