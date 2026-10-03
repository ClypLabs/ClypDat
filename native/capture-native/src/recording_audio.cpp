#include "recording_audio.h"
#include "recording_audio_codec.h"
#include "recording_audio_timing.h"
#include "recording_process.h"
#include <Windows.h>
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <audioclientactivationparams.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <propvarutil.h>
#include <ksmedia.h>
#include <audiopolicy.h>
#include <TlHelp32.h>
#include <set>
#include <cwctype>
#include <algorithm>
#include <cmath>
#include <chrono>
#include <condition_variable>
#include <deque>
#include <fstream>
#include <map>
#include <mutex>
#include <optional>
#include <thread>
#include <stdexcept>
extern "C" {
#include <libswresample/swresample.h>
#include <libavutil/channel_layout.h>
}

namespace clypdat {
namespace {
void require(bool condition, const char* message) { if (!condition) throw std::runtime_error(message); }
void hr(HRESULT result, const char* operation) { if (FAILED(result)) throw std::runtime_error(std::string(operation) + ": " + std::to_string(uint32_t(result))); }
}
struct AudioHistory::State {
    using Clock = std::chrono::steady_clock;
    struct Entry { std::shared_ptr<const AVPacket> packet; bool audible = false; };
    struct Ring { std::shared_ptr<const AVCodecParameters> parameters; std::deque<Entry> packets; int64_t end = INT64_MIN; };
    struct Work { PcmBlock block; std::shared_ptr<std::promise<AudioSnapshot>> barrier; std::optional<std::vector<AudioLaneConfig>> lanes; int64_t start = 0, end = 0; };
    struct Pending { std::shared_ptr<std::promise<AudioSnapshot>> promise; int64_t start = 0, end = 0; Clock::time_point deadline; };
    AudioHistoryOptions options;
    int64_t retention_us = 0;
    mutable std::mutex mutex;
    std::condition_variable ready, exited;
    std::deque<Work> queue;
    uint64_t queued_duration_us = 0;
    std::atomic_uint64_t lost{0};
    bool closed = false, done = false;
    std::string failure;
    // Worker-owned until done.
    std::unique_ptr<AudioTrackEncoder> encoder;
    std::map<std::string, Ring> rings;
    std::vector<Pending> pending;
    int64_t latest = INT64_MIN; Clock::time_point latest_at;
    int64_t lag() const { return audio_frames(options.encode_lag_us); }
    // Packets a save can still ask for: retention, keyframe lead-in and preroll.
    int64_t kept() const { return audio_frames(retention_us) + 5 * 48000 + encoder->preroll(); }
    void sync_rings() {
        std::map<std::string, Ring> kept_rings;
        auto keep = [&](const std::string& key) {
            auto parameters = encoder->parameters(key); auto& ring = kept_rings[key];
            auto found = rings.find(key);
            if (found != rings.end() && found->second.parameters == parameters) ring = std::move(found->second);
            ring.parameters = std::move(parameters);
        };
        for (const auto& lane : encoder->lanes()) keep(lane.key);
        keep(kAllTracksKey);
        rings = std::move(kept_rings);
    }
    void packet(const std::string& key, Packet value, bool audible) {
        auto& ring = rings[key];
        ring.end = (std::max)(ring.end, value->pts + value->duration);
        std::shared_ptr<const AVPacket> owned(value.release(), [](const AVPacket* p) { auto mutable_packet = const_cast<AVPacket*>(p); av_packet_free(&mutable_packet); });
        ring.packets.push_back({std::move(owned), audible});
        const auto cutoff = ring.end - kept();
        while (!ring.packets.empty() && ring.packets.front().packet->pts + ring.packets.front().packet->duration < cutoff) ring.packets.pop_front();
    }
    void write(const PcmBlock& block) {
        if (options.before_write) options.before_write();
        const auto position = audio_frames(block.start_us);
        const auto end = position + av_rescale(int64_t(block.samples.size() / block.channels), 48000, block.sample_rate);
        encoder->anchor(position);
        if (end > latest) { latest = end; latest_at = Clock::now(); }
        // Never encode past this block's start before it is mixed in.
        encoder->encode_through((std::min)(position, latest - lag()));
        encoder->submit(block, position);
        encoder->encode_through(latest - lag());
    }
    // Sources that go quiet stop delivering PCM. Keep the timeline moving in
    // real time so saves never wait on, or burst-encode, a long silent gap.
    void advance() {
        if (latest == INT64_MIN || !encoder->anchored()) return;
        const auto elapsed = std::chrono::duration_cast<std::chrono::microseconds>(Clock::now() - latest_at).count();
        encoder->encode_through(latest + audio_frames(elapsed) - lag());
    }
    bool covered(const Pending& request) const {
        const auto end = audio_frames(request.end);
        for (const auto& [key, ring] : rings) if (ring.end < end) return false;
        return true;
    }
    AudioSnapshot collect(int64_t start, int64_t end) const {
        AudioSnapshot result{start, end, {}};
        const auto first = audio_frames(start), last = audio_frames(end), lead = first - encoder->preroll();
        auto add = [&](const std::string& key, const std::string& title, int channels, bool mix) {
            auto found = rings.find(key); if (found == rings.end()) return;
            AudioTrack track{key, title, channels, mix, false, found->second.parameters, {}};
            for (const auto& entry : found->second.packets) {
                const auto pts = entry.packet->pts, stop = pts + entry.packet->duration;
                if (stop <= lead || pts >= last) continue;
                track.packets.push_back(entry.packet);
                track.audible |= entry.audible && stop > first;
            }
            result.tracks.push_back(std::move(track));
        };
        for (const auto& lane : encoder->lanes()) add(lane.key, lane.title, lane.channels, false);
        add(kAllTracksKey, kAllTracksTitle, 2, true);
        return result;
    }
    void service(bool force) {
        const auto now = Clock::now();
        for (auto it = pending.begin(); it != pending.end();) {
            if (!covered(*it) && (force || now >= it->deadline)) {
                // Nothing reached the window yet, or a source stalled: pad with silence.
                encoder->anchor(audio_frames(it->start) - encoder->preroll());
                encoder->encode_through(audio_frames(it->end) + 4096 + 3 * encoder->block());
            }
            if (force || covered(*it)) { it->promise->set_value(collect(it->start, it->end)); it = pending.erase(it); } else ++it;
        }
    }
    void fail(std::exception_ptr error) {
        for (auto& request : pending) request.promise->set_exception(error);
        pending.clear();
    }
    void record(std::exception_ptr error) {
        try { std::rethrow_exception(error); }
        catch (const std::exception& e) { std::lock_guard lock(mutex); if (failure.empty()) failure = std::string(e.what()) + "; audio worker restart required"; }
        catch (...) { std::lock_guard lock(mutex); if (failure.empty()) failure = "Audio encoder failed; audio worker restart required"; }
    }
    void run() {
        for (;;) {
            Work work; bool have = false;
            {
                std::unique_lock lock(mutex);
                ready.wait_for(lock, std::chrono::milliseconds(50), [&] { return closed || !queue.empty(); });
                if (!queue.empty()) { work = std::move(queue.front()); queue.pop_front(); have = true; }
                else if (closed) break;
            }
            const bool pcm = have && !work.barrier && !work.lanes;
            try {
                std::string error; { std::lock_guard lock(mutex); error = failure; }
                if (!error.empty()) throw std::runtime_error(error);
                if (have && work.barrier) {
                    if (options.before_flush) options.before_flush();
                    pending.push_back({work.barrier, work.start, work.end, Clock::now() + std::chrono::microseconds(options.encode_lag_us) + std::chrono::milliseconds(500)});
                    work.barrier.reset();
                } else if (have && work.lanes) { encoder->configure(std::move(*work.lanes)); sync_rings(); }
                else if (have) write(work.block);
                advance(); service(false);
            } catch (...) {
                auto error = std::current_exception();
                if (work.barrier) work.barrier->set_exception(error);
                fail(error); record(error);
            }
            if (pcm) { std::lock_guard lock(mutex); queued_duration_us -= work.block.duration_us(); }
        }
        try {
            std::string error; { std::lock_guard lock(mutex); error = failure; }
            if (!error.empty()) throw std::runtime_error(error);
            if (latest != INT64_MIN) encoder->finish(latest);
            service(true);
        } catch (...) { auto error = std::current_exception(); fail(error); record(error); }
        { std::lock_guard lock(mutex); done = true; } exited.notify_all();
    }
};
AudioHistory::AudioHistory(std::vector<AudioLaneConfig> lanes, int64_t retention_us, AudioHistoryOptions options) : state_(std::make_shared<State>()) {
    auto s = state_; s->retention_us = retention_us; s->options = std::move(options);
    State* raw = s.get();
    s->encoder = std::make_unique<AudioTrackEncoder>(s->options.codec, std::move(lanes), true, int64_t(48000) * 120,
        [raw](const std::string& key, Packet packet, bool audible) { raw->packet(key, std::move(packet), audible); });
    s->sync_rings();
    std::thread([s] { s->run(); }).detach();
}
AudioHistory::~AudioHistory() { stop(); }
bool AudioHistory::submit(PcmBlock block) {
    if (block.channels < 1 || block.channels > 32 || block.sample_rate < 8000 || block.sample_rate > 384000 || block.samples.size() % block.channels) throw std::invalid_argument("Invalid PCM block");
    auto s = state_; std::lock_guard lock(s->mutex);
    // A worker that falls behind sheds new audio instead of failing the session:
    // the encoders continue with silence once it catches up, so later saves
    // keep working. Only real encoder errors are sticky.
    constexpr uint64_t max_queued_us = 30000000;
    if (s->closed || !s->failure.empty() || s->queued_duration_us + block.duration_us() > max_queued_us) {
        s->lost += block.samples.size();
        return false;
    }
    s->queued_duration_us += block.duration_us(); s->queue.push_back({std::move(block), {}, {}, 0, 0}); s->ready.notify_one(); return true;
}
void AudioHistory::configure(std::vector<AudioLaneConfig> lanes) {
    auto s = state_; std::lock_guard lock(s->mutex); if (s->closed) return;
    s->queue.push_back({{}, {}, std::move(lanes), 0, 0}); s->ready.notify_one();
}
std::future<AudioSnapshot> AudioHistory::snapshot(int64_t start_us, int64_t end_us) {
    auto promise = std::make_shared<std::promise<AudioSnapshot>>(); auto future = promise->get_future();
    auto s = state_; std::lock_guard lock(s->mutex);
    if(s->done){try{if(!s->failure.empty())throw std::runtime_error(s->failure);promise->set_value(s->collect(start_us,end_us));}catch(...){promise->set_exception(std::current_exception());}}
    else if (s->closed) {try{std::thread([s,promise,start_us,end_us]{std::unique_lock lock(s->mutex);s->exited.wait(lock,[&]{return s->done;});try{if(!s->failure.empty())throw std::runtime_error(s->failure);promise->set_value(s->collect(start_us,end_us));}catch(...){promise->set_exception(std::current_exception());}}).detach();}catch(...){promise->set_exception(std::current_exception());}}
    else { s->queue.push_back({{}, promise, {}, start_us, end_us}); s->ready.notify_one(); }
    return future;
}
void AudioHistory::stop() {
    auto s = state_; std::unique_lock lock(s->mutex); s->closed = true; s->ready.notify_one();
    if (!s->exited.wait_for(lock, std::chrono::seconds(5), [&] { return s->done; }) && s->failure.empty()) s->failure = "Audio encoder shutdown timed out; resource graph retained; worker restart required";
}
uint64_t AudioHistory::lost_samples() const { return state_->lost.load(); }
std::string AudioHistory::error() const { std::lock_guard lock(state_->mutex); return state_->failure; }

struct WasapiSource::State {
    WasapiConfig config; Sink sink;
    std::atomic_bool stop{false}; std::atomic<float> peak{0};
    mutable std::mutex mutex; std::condition_variable ready; bool started=false,done=false; std::string failure;
    void run();
};
namespace {
class Activation final : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IActivateAudioInterfaceCompletionHandler, Microsoft::WRL::FtmBase> {
public:
    HANDLE event=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    HRESULT result=E_PENDING; Microsoft::WRL::ComPtr<IAudioClient> client;
    ~Activation(){CloseHandle(event);}
    HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) override {
        Microsoft::WRL::ComPtr<IUnknown> unknown;
        HRESULT outer=operation->GetActivateResult(&result,&unknown);
        if(FAILED(outer)) result=outer;
        if(SUCCEEDED(result)) result=unknown.As(&client);
        SetEvent(event); return S_OK;
    }
};
}
void WasapiSource::State::run() {
    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    std::unique_ptr<RecordingAudioTiming> timing;
    try {
        hr(com,"Initialize audio COM");
        Microsoft::WRL::ComPtr<IAudioClient> client;
        WAVEFORMATEX* allocated=nullptr;
        WAVEFORMATEX process_format{WAVE_FORMAT_IEEE_FLOAT,2,48000,48000*8,8,32,0};
        WAVEFORMATEX* format=&process_format;
        DWORD flags=AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        if(config.process_id) {
            auto activation=Microsoft::WRL::Make<Activation>();
            AUDIOCLIENT_ACTIVATION_PARAMS parameters{}; parameters.ActivationType=AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
            parameters.ProcessLoopbackParams.TargetProcessId=config.process_id;
            parameters.ProcessLoopbackParams.ProcessLoopbackMode=config.exclude_process_tree?PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE:PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
            PROPVARIANT value{}; value.vt=VT_BLOB; value.blob.cbSize=sizeof(parameters); value.blob.pBlobData=reinterpret_cast<BYTE*>(&parameters);
            Microsoft::WRL::ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
            hr(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,__uuidof(IAudioClient),&value,activation.Get(),&operation),"Process-loopback unavailable; endpoint fallback forbidden");
            require(WaitForSingleObject(activation->event,5000)==WAIT_OBJECT_0,"Process-loopback activation timed out");
            hr(activation->result,"Process-loopback activation unavailable"); client=activation->client;
            flags|=AUDCLNT_STREAMFLAGS_LOOPBACK;
        } else {
            require(!config.exclude_process_tree,"Process exclusions require process-loopback routing");
            Microsoft::WRL::ComPtr<IMMDeviceEnumerator> enumerator; Microsoft::WRL::ComPtr<IMMDevice> device;
            hr(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&enumerator)),"Create audio endpoint enumerator");
            if(config.device_id.empty()) hr(enumerator->GetDefaultAudioEndpoint(config.microphone?eCapture:eRender,eMultimedia,&device),"Resolve default audio endpoint");
            else hr(enumerator->GetDevice(config.device_id.c_str(),&device),"Resolve audio endpoint");
            hr(device->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,&client),"Activate audio endpoint");
            hr(client->GetMixFormat(&allocated),"Read audio mix format"); format=allocated;
            if(!config.microphone) flags|=AUDCLNT_STREAMFLAGS_LOOPBACK;
        }
        struct FormatScope{WAVEFORMATEX* p;~FormatScope(){CoTaskMemFree(p);}} free_format{allocated};
        HRESULT initialized=client->Initialize(AUDCLNT_SHAREMODE_SHARED,flags,5000000,0,format,nullptr);
        if(FAILED(initialized))initialized=client->Initialize(AUDCLNT_SHAREMODE_SHARED,flags,0,0,format,nullptr);
        if(FAILED(initialized)&&config.process_id){flags&=~AUDCLNT_STREAMFLAGS_LOOPBACK;initialized=client->Initialize(AUDCLNT_SHAREMODE_SHARED,flags,5000000,0,format,nullptr);if(FAILED(initialized))initialized=client->Initialize(AUDCLNT_SHAREMODE_SHARED,flags,0,0,format,nullptr);}
        hr(initialized,"Initialize audio capture");
        HANDLE event=CreateEventW(nullptr,FALSE,FALSE,nullptr);
        struct EventScope{HANDLE h;~EventScope(){CloseHandle(h);}} free_event{event};
        hr(client->SetEventHandle(event),"Set audio capture event");
        Microsoft::WRL::ComPtr<IAudioCaptureClient> capture; hr(client->GetService(IID_PPV_ARGS(&capture)),"Get audio capture client");
        bool floating=format->wFormatTag==WAVE_FORMAT_IEEE_FLOAT;
        if(format->wFormatTag==WAVE_FORMAT_EXTENSIBLE) floating=reinterpret_cast<WAVEFORMATEXTENSIBLE*>(format)->SubFormat==KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        require(format->nChannels>0&&format->nChannels<=32,"Unsupported capture channel count");
        require((floating&&format->wBitsPerSample==32)||(!floating&&(format->wBitsPerSample==16||format->wBitsPerSample==24||format->wBitsPerSample==32)),"Unsupported capture sample format");
        auto deliver=[this](PcmBlock block){
            float maximum=0;
            for(float sample:block.samples)maximum=(std::max)(maximum,std::abs(sample));
            peak=maximum;sink(std::move(block));
        };
        if(config.microphone) timing=std::make_unique<RecordingAudioTiming>(deliver);
        hr(client->Start(),"Start audio capture");
        {std::lock_guard lock(mutex);started=true;} ready.notify_all();
        bool stopped=false;
        for(;;) {
            if(stop.load()&&!stopped){hr(client->Stop(),"Stop audio acquisition");stopped=true;}
            if(!stopped)WaitForSingleObject(event,50);
            UINT32 available=0; hr(capture->GetNextPacketSize(&available),"Query audio packet");
            while(available) {
                BYTE* bytes=nullptr; UINT32 frames=0; DWORD status=0; UINT64 position=0,qpc_100ns=0;
                hr(capture->GetBuffer(&bytes,&frames,&status,&position,&qpc_100ns),"Acquire audio packet");
                struct BufferScope{IAudioCaptureClient* c;UINT32 n;~BufferScope(){if(n)c->ReleaseBuffer(n);}} release{capture.Get(),frames};
                PcmBlock block; block.lane=config.lane;block.source=config.source; block.generation=config.generation; block.channels=format->nChannels;block.sample_rate=format->nSamplesPerSec;
                // WASAPI QPC position is already scaled to 100 ns, not raw QPC ticks.
                const int64_t anchor_100ns=int64_t((long double)config.qpc_anchor*10000000/config.qpc_frequency);
                block.start_us=config.monotonic_anchor_us+(int64_t(qpc_100ns)-anchor_100ns)/10;
                if(status&AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) { LARGE_INTEGER now;QueryPerformanceCounter(&now);block.start_us=config.monotonic_anchor_us+int64_t((long double)(now.QuadPart-config.qpc_anchor)*1000000/config.qpc_frequency)-int64_t(frames)*1000000/block.sample_rate; }
                block.samples.resize(size_t(frames)*block.channels);
                if(!(status&AUDCLNT_BUFFERFLAGS_SILENT)) for(size_t i=0;i<block.samples.size();++i) {
                    float sample=0;
                    if(floating) memcpy(&sample,bytes+i*4,4);
                    else if(format->wBitsPerSample==16){int16_t value;memcpy(&value,bytes+i*2,2);sample=value/32768.f;}
                    else if(format->wBitsPerSample==32){int32_t value;memcpy(&value,bytes+i*4,4);sample=float(value/2147483648.0);}
                    else {const BYTE* p=bytes+i*3;int32_t value=int32_t(uint32_t(p[0])<<8|uint32_t(p[1])<<16|uint32_t(p[2])<<24);sample=float(value/2147483648.0);}
                    if(!std::isfinite(sample)) sample=0; block.samples[i]=sample;
                }
                if(timing) timing->submit(std::move(block),position,!(status&AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR));
                else deliver(std::move(block));
                // Release before querying the next packet.
                release.c->ReleaseBuffer(release.n);release.n=0;
                hr(capture->GetNextPacketSize(&available),"Query next audio packet");
            }
            if(stopped)break;
        }
    } catch(const std::exception& e) {std::lock_guard lock(mutex);failure=e.what();}
    try { if(timing)timing->finish(); }
    catch(const std::exception& e) {std::lock_guard lock(mutex);if(failure.empty())failure=e.what();}
    if(SUCCEEDED(com)) CoUninitialize();
    {std::lock_guard lock(mutex);done=true;started=true;} ready.notify_all();
}
WasapiSource::WasapiSource(WasapiConfig config,Sink sink):state_(std::make_shared<State>()) {
    require(config.qpc_frequency>0,"Audio clock frequency is required");state_->config=std::move(config);state_->sink=std::move(sink);
    std::thread([state=state_]{state->run();}).detach();
    std::unique_lock lock(state_->mutex);
    if(!state_->ready.wait_for(lock,std::chrono::seconds(6),[&]{return state_->started;})) {state_->stop=true;throw std::runtime_error("Audio startup timed out; worker restart required");}
    if(!state_->failure.empty()) throw std::runtime_error(state_->failure);
}
WasapiSource::~WasapiSource(){stop();}
void WasapiSource::stop(){auto s=state_;s->stop=true;std::unique_lock lock(s->mutex);if(!s->ready.wait_for(lock,std::chrono::seconds(5),[&]{return s->done;})&&s->failure.empty())s->failure="Audio capture shutdown timed out; worker restart required";}
std::string WasapiSource::error() const{std::lock_guard lock(state_->mutex);return state_->failure;}
float WasapiSource::peak() const{return state_->peak.load();}

namespace {
std::wstring process_title(std::wstring name){auto first=name.find_first_not_of(L" \t\r\n");if(first==std::wstring::npos)return {};name=name.substr(first,name.find_last_not_of(L" \t\r\n")-first+1);name=std::filesystem::path(name).filename().wstring();if(name.size()>=4){auto suffix=name.substr(name.size()-4);std::transform(suffix.begin(),suffix.end(),suffix.begin(),[](wchar_t ch){return wchar_t(towlower(ch));});if(suffix==L".exe")name.resize(name.size()-4);}return name;}
std::wstring normalize_process(std::wstring name){name=process_title(std::move(name));std::transform(name.begin(),name.end(),name.begin(),[](wchar_t ch){return wchar_t(towlower(ch));});return name;}
std::string narrow(const std::wstring& value){if(value.empty())return {};int bytes=WideCharToMultiByte(CP_UTF8,0,value.data(),int(value.size()),nullptr,0,nullptr,nullptr);std::string result(bytes,'\0');WideCharToMultiByte(CP_UTF8,0,value.data(),int(value.size()),result.data(),bytes,nullptr,nullptr);return result;}
struct ProcessTable {
    std::map<uint32_t,uint32_t> parents;
    std::map<std::wstring,std::set<uint32_t>> names;
    std::set<uint32_t> active;
    ProcessTable(IMMDeviceEnumerator* enumerator){
        HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);if(snapshot!=INVALID_HANDLE_VALUE){PROCESSENTRY32W entry{};entry.dwSize=sizeof(entry);if(Process32FirstW(snapshot,&entry))do{parents[entry.th32ProcessID]=entry.th32ParentProcessID;names[normalize_process(entry.szExeFile)].insert(entry.th32ProcessID);}while(Process32NextW(snapshot,&entry));CloseHandle(snapshot);}
        Microsoft::WRL::ComPtr<IMMDeviceCollection> devices;if(FAILED(enumerator->EnumAudioEndpoints(eRender,DEVICE_STATE_ACTIVE,&devices)))return;UINT count=0;devices->GetCount(&count);
        for(UINT i=0;i<count;++i){Microsoft::WRL::ComPtr<IMMDevice> device;devices->Item(i,&device);Microsoft::WRL::ComPtr<IAudioSessionManager2> manager;if(!device||FAILED(device->Activate(__uuidof(IAudioSessionManager2),CLSCTX_ALL,nullptr,&manager)))continue;Microsoft::WRL::ComPtr<IAudioSessionEnumerator> sessions;if(FAILED(manager->GetSessionEnumerator(&sessions)))continue;int total=0;sessions->GetCount(&total);for(int j=0;j<total;++j){Microsoft::WRL::ComPtr<IAudioSessionControl> control;Microsoft::WRL::ComPtr<IAudioSessionControl2> detail;if(FAILED(sessions->GetSession(j,&control))||FAILED(control.As(&detail)))continue;AudioSessionState state;DWORD pid=0;if(SUCCEEDED(control->GetState(&state))&&state==AudioSessionStateActive&&SUCCEEDED(detail->GetProcessId(&pid))&&pid)active.insert(pid);}}
    }
    bool ancestor(uint32_t pid,const std::set<uint32_t>& candidates)const{for(int i=0;i<64;++i){auto found=parents.find(pid);if(found==parents.end()||found->second==0||found->second==pid)return false;pid=found->second;if(candidates.contains(pid))return true;}return false;}
    std::set<uint32_t> roots(std::set<uint32_t> pids)const{std::set<uint32_t> result;for(auto pid:pids)if(!ancestor(pid,pids))result.insert(pid);return result;}
    uint32_t app_root(const std::wstring& name)const{auto found=names.find(normalize_process(name));if(found==names.end()||found->second.empty())return 0;auto candidates=roots(found->second);for(auto root:candidates)for(auto pid:active)if(pid==root||ancestor(pid,{root}))return root;uint32_t best=0;size_t largest=0;for(auto root:candidates){size_t count=1;for(auto pid:found->second)if(ancestor(pid,{root}))++count;if(count>largest){largest=count;best=root;}}return best;}
};
std::wstring endpoint_id(IMMDeviceEnumerator* enumerator,bool microphone,const std::wstring& selected){
    if(!selected.empty()&&selected!=L"default"&&selected!=L"Default")return selected;
    Microsoft::WRL::ComPtr<IMMDevice> device;hr(enumerator->GetDefaultAudioEndpoint(microphone?eCapture:eRender,eMultimedia,&device),"Resolve current default audio device");LPWSTR id=nullptr;hr(device->GetId(&id),"Read audio endpoint identity");std::wstring result=id;CoTaskMemFree(id);return result;
}
bool social(const std::wstring& name){static const std::set<std::wstring> names{L"discord",L"guilded",L"teamspeak",L"mumble",L"skype",L"teams",L"zoom",L"slack",L"signal",L"telegram",L"whatsapp"};return names.contains(normalize_process(name));}
std::vector<AudioLaneConfig> graph_lanes(const AudioGraphConfig& config){
    std::vector<AudioLaneConfig> result{config.system_audio
        ? AudioLaneConfig{"system","Full System Audio",2,config.game_gain,false}
        : AudioLaneConfig{"game","Game Audio",2,config.game_gain,false}};
    auto apps=config.system_audio?std::vector<AudioApplicationConfig>{}:config.applications;std::sort(apps.begin(),apps.end(),[](const auto& a,const auto& b){if(social(a.process_name)!=social(b.process_name))return social(a.process_name);return normalize_process(a.process_name)<normalize_process(b.process_name);});
    std::set<std::string> added;
    auto add=[&](const AudioApplicationConfig& app){auto key=narrow(normalize_process(app.process_name));if(key.empty()||!added.insert(key).second)return;result.push_back({key,narrow(process_title(app.process_name)),2,std::clamp(app.gain,0.f,1.5f),key=="spotify"});};
    for(const auto& app:apps)if(social(app.process_name))add(app);
    for(size_t i=0;i<config.microphone_device_ids.size();++i)result.push_back({"mic:"+narrow(config.microphone_device_ids[i]),config.microphone_device_ids.size()==1?"Microphone":"Microphone "+std::to_string(i+1),config.microphone_stereo?2:1,config.microphone_gain,false});
    for(const auto& app:apps)if(!social(app.process_name))add(app);return result;
}
class MicrophoneFilter:public std::enable_shared_from_this<MicrophoneFilter>{
    struct Pending{PcmBlock original;size_t bytes_sent=0;int64_t output_frames=0,delivered=0;};
    std::filesystem::path executable_,model_;double threshold_;WasapiSource::Sink sink_;
    std::mutex mutex_,delivery_;std::condition_variable changed_;std::deque<std::shared_ptr<Pending>> input_,timeline_;
    std::atomic_bool cancel_{false};bool started_=false,failed_=false,closed_=false,done_=false;int rate_=0,channels_=0;int64_t admitted_frames_=0;
    size_t queued_bytes_=0;std::vector<uint8_t> remainder_;
    int64_t completed_frames_=0;
    std::vector<std::pair<int64_t,std::shared_ptr<std::promise<void>>>> barriers_;
    void complete_barriers(){for(auto it=barriers_.begin();it!=barriers_.end();){if(it->first<=completed_frames_){it->second->set_value();it=barriers_.erase(it);}else ++it;}}
    std::wstring filter()const{auto path=model_.wstring();std::wstring escaped;for(auto ch:path){if(ch==L'\\'||ch==L':'||ch==L'\'')escaped.push_back(L'\\');escaped.push_back(ch);}auto result=L"aresample=48000,arnndn=m='"+escaped+L"'";if(threshold_>-100)result+=L",agate=threshold="+std::to_wstring(std::pow(10.0,std::clamp(threshold_,-100.0,-25.0)/20))+L":range=0.06:ratio=2:attack=20:release=250:detection=rms";return result;}
    size_t read(uint8_t* destination,size_t capacity){std::unique_lock lock(mutex_);if(!changed_.wait_for(lock,std::chrono::seconds(2),[&]{return cancel_||closed_||!input_.empty();}))return 0;if(cancel_||input_.empty())return 0;auto pending=input_.front();auto bytes=pending->original.samples.size()*4;auto count=(std::min)(capacity,bytes-pending->bytes_sent);memcpy(destination,reinterpret_cast<uint8_t*>(pending->original.samples.data())+pending->bytes_sent,count);pending->bytes_sent+=count;if(pending->bytes_sent==bytes)input_.pop_front();return count;}
    void output(const uint8_t* bytes,size_t count){std::lock_guard delivery(delivery_);std::vector<PcmBlock> ready;int64_t completed=0;{std::lock_guard lock(mutex_);remainder_.insert(remainder_.end(),bytes,bytes+count);size_t consumed=0;while(!timeline_.empty()){auto& pending=*timeline_.front();size_t available=(remainder_.size()-consumed)/(size_t(channels_)*4);if(!available)break;size_t frames=(std::min)(available,size_t(pending.output_frames-pending.delivered));PcmBlock block;block.lane=pending.original.lane;block.source=pending.original.source;block.generation=pending.original.generation;block.channels=channels_;block.sample_rate=48000;block.start_us=audio_offset_time(pending.original.start_us,pending.delivered);block.samples.resize(frames*channels_);memcpy(block.samples.data(),remainder_.data()+consumed,block.samples.size()*4);consumed+=block.samples.size()*4;pending.delivered+=frames;ready.push_back(std::move(block));if(pending.delivered==pending.output_frames){queued_bytes_-=pending.original.samples.size()*4;completed+=pending.original.samples.size()/pending.original.channels;timeline_.pop_front();}}remainder_.erase(remainder_.begin(),remainder_.begin()+consumed);}for(auto& block:ready)sink_(std::move(block));{std::lock_guard lock(mutex_);completed_frames_+=completed;complete_barriers();}}
    void run(){try{std::vector<std::wstring> args{L"-hide_banner",L"-nostdin",L"-v",L"error",L"-f",L"f32le",L"-ar",std::to_wstring(rate_),L"-ac",std::to_wstring(channels_),L"-i",L"pipe:0",L"-af",filter(),L"-f",L"f32le",L"-ar",L"48000",L"-ac",std::to_wstring(channels_),L"-flush_packets",L"1",L"pipe:1"};ProcessRunner::run(executable_,args,cancel_,std::chrono::milliseconds::zero(),[this](auto bytes,auto count){output(bytes,count);},true,[this](auto bytes,auto count){return read(bytes,count);});}catch(...){}
        std::lock_guard delivery(delivery_);std::vector<PcmBlock> fallback;{std::lock_guard lock(mutex_);failed_=true;for(auto& pending:timeline_){auto block=std::move(pending->original);size_t skip=size_t(pending->delivered)*block.sample_rate/48000;skip=(std::min)(skip,block.samples.size()/block.channels);block.start_us=audio_offset_time(block.start_us,int64_t(skip),block.sample_rate);block.samples.erase(block.samples.begin(),block.samples.begin()+skip*block.channels);if(!block.samples.empty())fallback.push_back(std::move(block));}input_.clear();timeline_.clear();queued_bytes_=0;}for(auto& block:fallback)sink_(std::move(block));{std::lock_guard lock(mutex_);completed_frames_=admitted_frames_;complete_barriers();done_=true;}changed_.notify_all();
    }
public:
    MicrophoneFilter(std::filesystem::path executable,std::filesystem::path model,double threshold,WasapiSource::Sink sink):executable_(std::move(executable)),model_(std::move(model)),threshold_(threshold),sink_(std::move(sink)){}
    void submit(PcmBlock block){
        std::lock_guard delivery(delivery_);bool direct=false;std::vector<PcmBlock> fallback;
        {
            std::lock_guard lock(mutex_);
            if(failed_||closed_)direct=true;
            else {
                if(!started_){rate_=block.sample_rate;channels_=block.channels;started_=true;try{std::thread([self=shared_from_this()]{self->run();}).detach();}catch(...){started_=false;failed_=true;direct=true;}}
                if(!direct&&(rate_!=block.sample_rate||channels_!=block.channels||queued_bytes_+block.samples.size()*4>size_t(rate_)*channels_*4*10)){
                    cancel_=true;failed_=true;direct=true;
                    for(auto& pending:timeline_){auto original=std::move(pending->original);size_t skip=size_t(pending->delivered)*original.sample_rate/48000;skip=(std::min)(skip,original.samples.size()/original.channels);original.start_us=audio_offset_time(original.start_us,int64_t(skip),original.sample_rate);original.samples.erase(original.samples.begin(),original.samples.begin()+skip*original.channels);if(!original.samples.empty())fallback.push_back(std::move(original));}
                    input_.clear();timeline_.clear();queued_bytes_=0;
                }
                if(!direct){auto pending=std::make_shared<Pending>();auto frames=int64_t(block.samples.size()/channels_);pending->output_frames=(admitted_frames_+frames)*48000/rate_-admitted_frames_*48000/rate_;admitted_frames_+=frames;pending->original=std::move(block);queued_bytes_+=pending->original.samples.size()*4;input_.push_back(pending);timeline_.push_back(std::move(pending));}
            }
        }
        changed_.notify_all();for(auto& pending:fallback)sink_(std::move(pending));if(direct)sink_(std::move(block));if(!fallback.empty()){std::lock_guard lock(mutex_);completed_frames_=admitted_frames_;complete_barriers();}
    }
    std::shared_future<void> barrier(){auto promise=std::make_shared<std::promise<void>>();auto result=promise->get_future().share();std::lock_guard delivery(delivery_);std::lock_guard lock(mutex_);if(completed_frames_>=admitted_frames_)promise->set_value();else barriers_.emplace_back(admitted_frames_,promise);return result;}
    void abort(){cancel_=true;changed_.notify_all();}
    bool stop(){std::unique_lock lock(mutex_);closed_=true;changed_.notify_all();if(started_&&!changed_.wait_for(lock,std::chrono::seconds(3),[&]{return done_;})){cancel_=true;changed_.notify_all();changed_.wait_for(lock,std::chrono::seconds(3),[&]{return done_;});}return !started_||done_;}
};
class EndpointChanges final:public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,IMMNotificationClient>{
public:
    std::function<void()> changed;
    HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR,DWORD)override{changed();return S_OK;}
    HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR)override{changed();return S_OK;}
    HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR)override{changed();return S_OK;}
    HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow,ERole role,LPCWSTR)override{if(role==eMultimedia||role==eConsole)changed();return S_OK;}
    HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR,const PROPERTYKEY)override{return S_OK;}
};
}
struct AudioGraph::State:public std::enable_shared_from_this<AudioGraph::State> {
    AudioGraphConfig config;WasapiSource::Sink live;std::shared_ptr<AudioHistory> history;
    mutable std::mutex mutex;std::condition_variable changed;bool stopping=false,running=false,done=false;uint64_t revision=0,generation=0;std::string failure;
    struct Route {WasapiConfig config;std::unique_ptr<WasapiSource> capture;std::shared_ptr<MicrophoneFilter> filter;};
    std::map<std::string,Route> routes;
    std::vector<std::shared_ptr<MicrophoneFilter>> filters;
    std::wstring filter_signature;
    int stable_passes=0;
    void refresh(const AudioGraphConfig& settings){
        auto previous_generation=generation;auto previous_count=routes.size();{std::lock_guard lock(mutex);if(failure.find("worker restart required")==std::string::npos)failure.clear();}
        auto signature=std::to_wstring(settings.noise_suppression)+L":"+std::to_wstring(settings.gate_threshold_db)+L":"+settings.rnnoise_model.wstring();
        bool changed_filter=signature!=filter_signature;filter_signature=std::move(signature);
        Microsoft::WRL::ComPtr<IMMDeviceEnumerator> enumerator;hr(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&enumerator)),"Enumerate audio routes");
        std::map<std::string,WasapiConfig> wanted;
        auto source=[&](std::string key,std::string lane,uint32_t pid,bool mic,std::wstring device){WasapiConfig c;c.lane=std::move(lane);c.source=key;c.process_id=pid;c.microphone=mic;c.device_id=std::move(device);c.qpc_anchor=settings.qpc_anchor;c.qpc_frequency=settings.qpc_frequency;c.monotonic_anchor_us=settings.monotonic_anchor_us;wanted.emplace(std::move(key),std::move(c));};
        if(settings.system_audio){
            // Process loopback spans all playback endpoints. Exclude this
            // recorder's own tree so capture cannot feed back into itself.
            source("system:all","system",GetCurrentProcessId(),false,{});
            wanted.at("system:all").exclude_process_tree=true;
        }else{
            ProcessTable table(enumerator.Get());
            bool routed=!settings.applications.empty()||!settings.excluded_processes.empty();std::set<uint32_t> excluded{GetCurrentProcessId()};
            for(const auto& name:settings.excluded_processes){auto ids=table.names[normalize_process(name)];excluded.insert(ids.begin(),ids.end());}
            for(const auto& app:settings.applications){auto ids=table.names[normalize_process(app.process_name)];excluded.insert(ids.begin(),ids.end());auto pid=table.app_root(app.process_name);if(pid){auto lane=narrow(normalize_process(app.process_name));source("app:"+lane,lane,pid,false,{});}}
            if(routed){std::set<uint32_t> allowed;for(auto pid:table.active)if(!excluded.contains(pid)&&!table.ancestor(pid,excluded))allowed.insert(pid);for(auto it=allowed.begin();it!=allowed.end();){bool overlaps=false;for(auto excluded_pid:excluded)if(table.ancestor(excluded_pid,{*it})){overlaps=true;break;}if(overlaps){it=allowed.erase(it);std::lock_guard lock(mutex);failure="Audio process tree contains an excluded application; that route is unavailable";}else ++it;}auto game=table.names[normalize_process(settings.game_executable)];std::set<uint32_t> matched;std::set_intersection(allowed.begin(),allowed.end(),game.begin(),game.end(),std::inserter(matched,matched.end()));if(!matched.empty())allowed=std::move(matched);for(auto pid:table.roots(std::move(allowed)))source("game:"+std::to_string(pid),"game",pid,false,{});}
            else source("game:default","game",0,false,endpoint_id(enumerator.Get(),false,settings.output_device_id));
        }
        for(const auto& selected:settings.microphone_device_ids){auto lane="mic:"+narrow(selected);try{source(lane,lane,0,true,endpoint_id(enumerator.Get(),true,selected));}catch(const std::exception& e){std::lock_guard lock(mutex);failure=e.what();}}
        for(auto it=routes.begin();it!=routes.end();){auto desired=wanted.find(it->first);auto& old=it->second;if(desired==wanted.end()||desired->second.process_id!=old.config.process_id||desired->second.device_id!=old.config.device_id||!old.capture->error().empty()||(old.config.microphone&&changed_filter)){old.capture->stop();if(old.capture->error().find("worker restart required")!=std::string::npos){std::lock_guard lock(mutex);failure=old.capture->error();}if(old.filter&&!old.filter->stop()){std::lock_guard lock(mutex);failure="Microphone filter shutdown timed out; worker restart required";}it=routes.erase(it);}else ++it;}
        for(auto& [key,c]:wanted){if(routes.contains(key))continue;c.generation=++generation;try{auto history_owner=history;auto live_sink=live;WasapiSource::Sink sink=[history_owner,live_sink](PcmBlock block){if(live_sink)live_sink(block);history_owner->submit(std::move(block));};std::shared_ptr<MicrophoneFilter> filter;if(c.microphone&&settings.noise_suppression&&!settings.rnnoise_model.empty()&&std::filesystem::exists(settings.rnnoise_model)){filter=std::make_shared<MicrophoneFilter>(settings.ffmpeg,settings.rnnoise_model,settings.gate_threshold_db,sink);sink=[filter](PcmBlock block){filter->submit(std::move(block));};{std::lock_guard lock(mutex);filters.push_back(filter);}}auto capture=std::make_unique<WasapiSource>(c,std::move(sink));routes.emplace(key,Route{c,std::move(capture),std::move(filter)});}catch(const std::exception& e){std::lock_guard lock(mutex);failure=e.what();}}
        stable_passes=previous_generation==generation&&previous_count==routes.size()?(std::min)(5,stable_passes+1):0;{std::lock_guard lock(mutex);filters.clear();for(const auto& [key,route]:routes)if(route.filter)filters.push_back(route.filter);}
    }
    void run(){HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);uint64_t seen=UINT64_MAX;try{hr(com,"Initialize audio routing COM");Microsoft::WRL::ComPtr<IMMDeviceEnumerator> notifications;hr(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&notifications)),"Create audio endpoint watcher");auto watcher=Microsoft::WRL::Make<EndpointChanges>();watcher->changed=[weak=weak_from_this()]{if(auto owner=weak.lock()){std::lock_guard lock(owner->mutex);++owner->revision;owner->changed.notify_all();}};hr(notifications->RegisterEndpointNotificationCallback(watcher.Get()),"Register audio endpoint watcher");struct Registration{IMMDeviceEnumerator* owner;IMMNotificationClient* watcher;~Registration(){owner->UnregisterEndpointNotificationCallback(watcher);}} registration{notifications.Get(),watcher.Get()};for(;;){AudioGraphConfig settings;{std::lock_guard lock(mutex);if(stopping)break;settings=config;seen=revision;}try{refresh(settings);}catch(const std::exception& e){std::lock_guard lock(mutex);failure=e.what();}std::unique_lock lock(mutex);changed.wait_for(lock,std::chrono::seconds(stable_passes>=5?5:2),[&]{return stopping||revision!=seen;});}}catch(const std::exception& e){std::lock_guard lock(mutex);failure=e.what();}for(auto& [key,route]:routes){route.capture->stop();if(route.capture->error().find("worker restart required")!=std::string::npos){std::lock_guard lock(mutex);failure=route.capture->error();}if(route.filter&&!route.filter->stop()){std::lock_guard lock(mutex);failure="Microphone filter shutdown timed out; worker restart required";}}routes.clear();history->stop();if(SUCCEEDED(com))CoUninitialize();{std::lock_guard lock(mutex);done=true;running=false;}changed.notify_all();}
};
AudioGraph::AudioGraph(AudioGraphConfig config,WasapiSource::Sink live):state_(std::make_shared<State>()){state_->history=std::make_shared<AudioHistory>(graph_lanes(config),config.retention_us,AudioHistoryOptions{config.codec});state_->config=std::move(config);state_->live=std::move(live);}
AudioGraph::~AudioGraph(){stop();}
void AudioGraph::start(){auto s=state_;std::lock_guard lock(s->mutex);if(s->running)return;if(s->stopping)throw std::runtime_error("Audio graph cannot restart after stop");s->running=true;std::thread([s]{s->run();}).detach();}
bool AudioGraph::stop(){auto s=state_;std::unique_lock lock(s->mutex);if(!s->running){lock.unlock();s->history->stop();return !restart_required();}s->stopping=true;s->changed.notify_all();if(!s->changed.wait_for(lock,std::chrono::seconds(10),[&]{return s->done;})){s->failure="Audio graph shutdown timed out; resource graph retained; worker restart required";return false;}lock.unlock();return !restart_required();}
void AudioGraph::update(AudioGraphConfig config){auto s=state_;s->history->configure(graph_lanes(config));std::lock_guard lock(s->mutex);s->config=std::move(config);++s->revision;s->changed.notify_all();}
std::future<AudioSnapshot> AudioGraph::snapshot(int64_t start,int64_t end){
    auto history=state_->history;
    std::vector<std::shared_ptr<MicrophoneFilter>> filters;{std::lock_guard lock(state_->mutex);filters=state_->filters;}
    if(filters.empty())return history->snapshot(start,end);
    std::vector<std::shared_future<void>> tickets;for(const auto& filter:filters)tickets.push_back(filter->barrier());
    auto promise=std::make_shared<std::promise<AudioSnapshot>>();auto result=promise->get_future();
    try{std::thread([owner=state_,history,promise,filters=std::move(filters),tickets=std::move(tickets),start,end]()mutable{
        try{
            // Suppressed microphone PCM admitted before the save must reach the
            // encoders before the snapshot barrier does.
            for(size_t i=0;i<tickets.size();++i){if(tickets[i].wait_for(std::chrono::seconds(5))!=std::future_status::ready)filters[i]->abort();if(tickets[i].wait_for(std::chrono::seconds(5))!=std::future_status::ready)throw std::runtime_error("Audio suppression snapshot barrier timed out; worker restart required");tickets[i].get();}
            promise->set_value(history->snapshot(start,end).get());
        }catch(const std::exception& error){if(std::string(error.what()).find("worker restart required")!=std::string::npos){std::lock_guard lock(owner->mutex);owner->failure=error.what();}promise->set_exception(std::current_exception());}catch(...){promise->set_exception(std::current_exception());}
    }).detach();}catch(...){promise->set_exception(std::current_exception());}
    return result;
}
std::vector<AudioLaneConfig> AudioGraph::lanes()const{std::lock_guard lock(state_->mutex);return graph_lanes(state_->config);}
std::vector<AudioLaneConfig> recording_audio_lanes(const AudioGraphConfig& config){return graph_lanes(config);}
std::string AudioGraph::error()const{std::lock_guard lock(state_->mutex);return state_->failure.empty()?state_->history->error():state_->failure;}
bool AudioGraph::restart_required()const{return error().find("worker restart required")!=std::string::npos;}
struct RecordingMicrophoneFilter::State {std::shared_ptr<MicrophoneFilter> filter;};
RecordingMicrophoneFilter::RecordingMicrophoneFilter(std::filesystem::path ffmpeg,std::filesystem::path model,double gate,WasapiSource::Sink sink):state_(std::make_shared<State>()){state_->filter=std::make_shared<MicrophoneFilter>(std::move(ffmpeg),std::move(model),gate,std::move(sink));}
RecordingMicrophoneFilter::~RecordingMicrophoneFilter(){stop();}
void RecordingMicrophoneFilter::submit(PcmBlock block){state_->filter->submit(std::move(block));}
bool RecordingMicrophoneFilter::stop(){return state_->filter->stop();}
std::shared_future<void> RecordingMicrophoneFilter::barrier(){return state_->filter->barrier();}
struct AudioMeter::State {std::atomic<float> db{-100};std::unique_ptr<WasapiSource> capture;std::shared_ptr<MicrophoneFilter> filter;};
AudioMeter::AudioMeter(AudioMeterConfig config):state_(std::make_shared<State>()){
    auto state=state_;std::weak_ptr<State> weak=state;
    WasapiSource::Sink sink=[weak](PcmBlock block){auto p=weak.lock();if(!p)return;float peak=0;for(auto sample:block.samples)peak=(std::max)(peak,std::abs(sample));float level=peak>0?std::clamp(20.f*std::log10(peak),-100.f,0.f):-100.f;float old=p->db.load();p->db=level>old?level:old+(level-old)*.25f;};
    if(config.suppression&&!config.rnnoise_model.empty()&&std::filesystem::exists(config.rnnoise_model)){state->filter=std::make_shared<MicrophoneFilter>(config.ffmpeg,config.rnnoise_model,config.gate_db,sink);sink=[filter=state->filter](PcmBlock block){filter->submit(std::move(block));};}
    config.source.microphone=true;state->capture=std::make_unique<WasapiSource>(std::move(config.source),std::move(sink));
}
AudioMeter::~AudioMeter(){stop();}
float AudioMeter::level_db()const{return state_->db.load();}
bool AudioMeter::stop(){if(state_->capture)state_->capture->stop();bool filter_stopped=!state_->filter||state_->filter->stop();return filter_stopped&&(!state_->capture||state_->capture->error().find("worker restart required")==std::string::npos);}
}
