#include "recording_audio.h"
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
#include <condition_variable>
#include <deque>
#include <fstream>
#include <map>
#include <mutex>
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
void wav_header(std::fstream& out, int rate, int channels, uint64_t frames) {
    const uint64_t bytes = frames * channels * sizeof(float);
    require(bytes <= UINT32_MAX - 36, "Audio WAV RIFF size limit exceeded");
    auto u16 = [&](uint16_t v) { out.write(reinterpret_cast<const char*>(&v), 2); };
    auto u32 = [&](uint32_t v) { out.write(reinterpret_cast<const char*>(&v), 4); };
    out.seekp(0); out.write("RIFF", 4); u32(uint32_t(bytes + 36)); out.write("WAVEfmt ", 8); u32(16);
    u16(3); u16(uint16_t(channels)); u32(rate); u32(rate * channels * 4); u16(uint16_t(channels * 4)); u16(32);
    out.write("data", 4); u32(uint32_t(bytes));
}
std::atomic_uint64_t file_sequence{0};
}
struct AudioFile {
    std::filesystem::path path;
    ~AudioFile() { std::error_code ignored; std::filesystem::remove(path, ignored); }
};
struct AudioHistory::State {
    struct Source {
        std::shared_ptr<AudioFile> file;
        std::fstream stream;
        std::string lane,source;
        uint64_t generation = 0, frames = 0;
        int rate = 48000, channels = 2;
        int64_t start_us = 0;
    };
    struct Work { PcmBlock block; std::shared_ptr<std::promise<AudioSnapshot>> barrier; int64_t start = 0, end = 0; };
    std::filesystem::path directory;
    AudioHistoryCalls calls;
    int64_t retention_us;
    mutable std::mutex mutex;
    std::condition_variable ready, exited;
    std::deque<Work> queue;
    std::map<std::tuple<std::string,uint64_t,int64_t>, std::unique_ptr<Source>> sources;
    uint64_t queued_duration_us = 0;
    std::atomic_uint64_t lost{0};
    bool closed = false, done = false;
    std::string failure;
    void write(PcmBlock& block) {
        if(calls.before_write)calls.before_write();
        // Rotate bounded files even when a source never changes devices. This
        // also bounds silence padding and keeps every file below RIFF limits.
        auto segment_us=std::clamp(retention_us,int64_t(5000000),int64_t(60000000));
        auto key = std::make_tuple(block.lane+":"+block.source, block.generation,block.start_us/segment_us);
        auto& source = sources[key];
        if (!source) {
            source = std::make_unique<Source>(); source->lane = block.lane; source->source=block.source; source->generation = block.generation;
            source->rate = block.sample_rate; source->channels = block.channels; source->start_us = block.start_us;
            source->file = std::make_shared<AudioFile>();
            source->file->path = directory / (L"audio-" + std::to_wstring(++file_sequence) + L".wav");
            source->stream.open(source->file->path, std::ios::binary | std::ios::in | std::ios::out | std::ios::trunc);
            source->stream.exceptions(std::ios::badbit | std::ios::failbit);
            wav_header(source->stream, source->rate, source->channels, 0);
        }
        auto& s = *source;
        require(s.rate == block.sample_rate && s.channels == block.channels, "Audio format changed without a generation change");
        int64_t target = (block.start_us - s.start_us) * s.rate / 1000000;
        uint64_t skip = target < int64_t(s.frames) ? uint64_t(int64_t(s.frames) - target) : 0;
        uint64_t frames = block.samples.size() / s.channels;
        if (skip >= frames) return;
        uint64_t gap = target > int64_t(s.frames) ? uint64_t(target) - s.frames : 0;
        require((s.frames + gap + frames - skip) * s.channels * 4 <= UINT32_MAX - 36, "Audio WAV RIFF size limit exceeded");
        s.stream.seekp(44 + std::streamoff(s.frames * s.channels * 4));
        float silence[8192]{};
        uint64_t missing = gap * s.channels;
        while (missing) { auto count = (std::min)(missing, uint64_t(std::size(silence))); s.stream.write(reinterpret_cast<char*>(silence), count * 4); missing -= count; }
        s.stream.write(reinterpret_cast<const char*>(block.samples.data() + skip * s.channels), (frames - skip) * s.channels * 4);
        s.frames += gap + frames - skip;
        // File lifetime is pinned by snapshots. Pruning never removes a file a save uses.
        const auto cutoff = block.start_us - retention_us;
        for (auto it = sources.begin(); it != sources.end();) {
            auto& old = *it->second;
            if (it->first != key && old.start_us + int64_t(old.frames) * 1000000 / old.rate < cutoff) it = sources.erase(it); else ++it;
        }
    }
    AudioSnapshot snapshot(int64_t start, int64_t end) {
        if(calls.before_flush)calls.before_flush();
        AudioSnapshot result{start, end, {}};
        for (auto& [key, pointer] : sources) {
            auto& s = *pointer;
            int64_t first = (std::max)(int64_t(0), (start - s.start_us) * s.rate / 1000000);
            int64_t last = (std::min)(int64_t(s.frames), (end - s.start_us) * s.rate / 1000000);
            if (last <= first) continue;
            wav_header(s.stream, s.rate, s.channels, s.frames); s.stream.flush();
            result.ranges.push_back({s.file,s.lane,s.source,s.generation,uint64_t(first),uint64_t(last-first),s.start_us+first*1000000/s.rate,s.rate,s.channels});
        }
        return result;
    }
    void run() {
        for (;;) {
            Work work;
            { std::unique_lock lock(mutex); ready.wait(lock, [&] { return closed || !queue.empty(); }); if (queue.empty()) break; work = std::move(queue.front()); queue.pop_front(); }
            try {
                std::string error; { std::lock_guard lock(mutex); error = failure; }
                if (!error.empty()) throw std::runtime_error(error);
                if (work.barrier) work.barrier->set_value(snapshot(work.start, work.end)); else write(work.block);
            } catch (...) {
                if (work.barrier) work.barrier->set_exception(std::current_exception());
                try { throw; } catch (const std::exception& e) { std::lock_guard lock(mutex); if (failure.empty()) failure = std::string(e.what())+"; audio worker restart required"; }
            }
            if (!work.barrier) { std::lock_guard lock(mutex); queued_duration_us -= work.block.duration_us(); }
        }
        for (auto& [key, source] : sources) { try { wav_header(source->stream,source->rate,source->channels,source->frames); source->stream.flush(); } catch (...) {} }
        { std::lock_guard lock(mutex); done = true; } exited.notify_all();
    }
};
AudioHistory::AudioHistory(std::filesystem::path directory, int64_t retention_us,AudioHistoryCalls calls) : state_(std::make_shared<State>()) {
    state_->directory = std::move(directory); state_->retention_us = retention_us;
    state_->calls=std::move(calls);
    std::filesystem::create_directories(state_->directory);
    std::thread([state=state_] { state->run(); }).detach();
}
AudioHistory::~AudioHistory() { stop(); }
bool AudioHistory::submit(PcmBlock block) {
    if (block.channels < 1 || block.channels > 32 || block.sample_rate < 8000 || block.sample_rate > 384000 || block.samples.size() % block.channels) throw std::invalid_argument("Invalid PCM block");
    auto s = state_; std::lock_guard lock(s->mutex);
    if (s->closed || !s->failure.empty() || s->queued_duration_us + block.duration_us() > 10000000) {
        s->lost += block.samples.size();
        if (!s->closed && s->failure.empty()) s->failure = "Audio writer ten-second queued/in-flight bound exceeded; worker restart required";
        return false;
    }
    s->queued_duration_us += block.duration_us(); s->queue.push_back({std::move(block),{},0,0}); s->ready.notify_one(); return true;
}
std::future<AudioSnapshot> AudioHistory::snapshot(int64_t start_us, int64_t end_us) {
    auto promise = std::make_shared<std::promise<AudioSnapshot>>(); auto future = promise->get_future();
    auto s = state_; std::lock_guard lock(s->mutex);
    if(s->done){try{if(!s->failure.empty())throw std::runtime_error(s->failure);promise->set_value(s->snapshot(start_us,end_us));}catch(...){promise->set_exception(std::current_exception());}}
    else if (s->closed) {try{std::thread([s,promise,start_us,end_us]{std::unique_lock lock(s->mutex);s->exited.wait(lock,[&]{return s->done;});try{if(!s->failure.empty())throw std::runtime_error(s->failure);promise->set_value(s->snapshot(start_us,end_us));}catch(...){promise->set_exception(std::current_exception());}}).detach();}catch(...){promise->set_exception(std::current_exception());}}
    else { s->queue.push_back({{},promise,start_us,end_us}); s->ready.notify_one(); }
    return future;
}
void AudioHistory::stop() {
    auto s = state_; std::unique_lock lock(s->mutex); s->closed = true; s->ready.notify_one();
    if (!s->exited.wait_for(lock, std::chrono::seconds(5), [&] { return s->done; }) && s->failure.empty()) s->failure = "Audio writer shutdown timed out; resource graph retained; worker restart required";
}
uint64_t AudioHistory::lost_samples() const { return state_->lost.load(); }
std::string AudioHistory::error() const { std::lock_guard lock(state_->mutex); return state_->failure; }

std::vector<std::pair<std::string,std::filesystem::path>> render_audio(const AudioSnapshot& snapshot,
    const std::vector<AudioLaneConfig>& lanes, const std::filesystem::path& directory, const std::atomic_bool& cancel) {
    require(snapshot.end_us > snapshot.start_us, "Invalid audio snapshot window");
    std::filesystem::create_directories(directory);
    const int64_t total = (snapshot.end_us - snapshot.start_us) * 48000 / 1000000;
    std::vector<std::pair<std::string,std::filesystem::path>> result;
    for (size_t lane_index = 0; lane_index < lanes.size(); ++lane_index) {
        const auto& lane = lanes[lane_index]; require(lane.channels == 1 || lane.channels == 2, "Invalid output audio channels");
        auto path = directory / (L"lane-" + std::to_wstring(lane_index) + L".wav");
        std::fstream output(path, std::ios::binary | std::ios::in | std::ios::out | std::ios::trunc); output.exceptions(std::ios::badbit|std::ios::failbit);
        wav_header(output,48000,lane.channels,total);
        std::vector<AudioRange> ranges;
        for (const auto& r : snapshot.ranges) if (r.lane == lane.key) ranges.push_back(r);
        std::sort(ranges.begin(),ranges.end(),[](const auto& a,const auto& b) { return a.start_us < b.start_us || (a.start_us == b.start_us && a.generation < b.generation); });
        struct Reader { AudioRange range; std::ifstream input; SwrContext* swr = nullptr; std::vector<float> data; int64_t output_start = 0; ~Reader(){swr_free(&swr);} };
        std::vector<std::unique_ptr<Reader>> readers;
        for (const auto& range : ranges) {
            auto reader = std::make_unique<Reader>(); reader->range = range;
            reader->input.open(range.file->path,std::ios::binary); reader->input.exceptions(std::ios::badbit|std::ios::failbit);
            reader->input.seekg(44 + std::streamoff(range.first_frame * range.channels * 4));
            AVChannelLayout input_layout{},output_layout{};
            av_channel_layout_default(&input_layout,range.channels); av_channel_layout_default(&output_layout,lane.channels);
            int code = swr_alloc_set_opts2(&reader->swr,&output_layout,AV_SAMPLE_FMT_FLT,48000,&input_layout,AV_SAMPLE_FMT_FLT,range.sample_rate,0,nullptr);
            av_channel_layout_uninit(&input_layout); av_channel_layout_uninit(&output_layout);
            require(code >= 0 && swr_init(reader->swr) >= 0,"Audio resampler initialization failed");
            reader->output_start = (range.start_us-snapshot.start_us)*48000/1000000;
            readers.push_back(std::move(reader));
        }
        // Stream sources into sparse aligned output; each input chunk is bounded.
        std::vector<float> zero(48000 * lane.channels,0);
        for(int64_t position=0;position<total;position+=48000) { if(cancel.load()) throw std::runtime_error("Audio render cancelled"); output.write(reinterpret_cast<char*>(zero.data()), (std::min)(int64_t(48000),total-position)*lane.channels*4); }
        bool audible = false;
        std::map<std::string,int64_t> source_ends;
        for(auto& ptr:readers) {
            auto& reader=*ptr; uint64_t remaining=reader.range.frame_count; int64_t cursor=reader.output_start;
            auto& previous_end=source_ends[reader.range.source];
            bool flushed=false;
            while(remaining||!flushed) {
                if(cancel.load()) throw std::runtime_error("Audio render cancelled");
                int count=int((std::min)(remaining,uint64_t(8192))); std::vector<float> input(size_t(count)*reader.range.channels);
                if(count)reader.input.read(reinterpret_cast<char*>(input.data()),input.size()*4);else flushed=true;
                int capacity=swr_get_out_samples(reader.swr,count); std::vector<float> converted(size_t(capacity)*lane.channels);
                const uint8_t* source=reinterpret_cast<const uint8_t*>(input.data()); uint8_t* target=reinterpret_cast<uint8_t*>(converted.data());
                int actual=swr_convert(reader.swr,&target,capacity,count?&source:nullptr,count); require(actual>=0,"Audio conversion failed");
                int64_t first=(std::max)({int64_t(0),-cursor,previous_end-cursor}); int64_t last=(std::min)(int64_t(actual),total-cursor);
                if(last>first) {
                    for(int64_t i=first*lane.channels;i<last*lane.channels;++i) { converted[size_t(i)]*=std::clamp(lane.gain,0.f,1.5f); audible|=std::abs(converted[size_t(i)])>0.000001f; }
                    std::vector<float> existing(size_t(last-first)*lane.channels);output.seekg(44+(cursor+first)*lane.channels*4);output.read(reinterpret_cast<char*>(existing.data()),existing.size()*4);
                    for(size_t i=0;i<existing.size();++i)existing[i]+=converted[size_t(first)*lane.channels+i];
                    output.seekp(44+(cursor+first)*lane.channels*4); output.write(reinterpret_cast<char*>(existing.data()),existing.size()*4);
                }
                cursor+=actual; remaining-=count;
            }
            previous_end=(std::max)(previous_end,cursor);
        }
        output.close();
        if(lane.omit_if_silent&&!audible) std::filesystem::remove(path); else result.emplace_back(lane.title,path);
    }
    return result;
}

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
                float maximum=0;
                if(!(status&AUDCLNT_BUFFERFLAGS_SILENT)) for(size_t i=0;i<block.samples.size();++i) {
                    float sample=0;
                    if(floating) memcpy(&sample,bytes+i*4,4);
                    else if(format->wBitsPerSample==16){int16_t value;memcpy(&value,bytes+i*2,2);sample=value/32768.f;}
                    else if(format->wBitsPerSample==32){int32_t value;memcpy(&value,bytes+i*4,4);sample=float(value/2147483648.0);}
                    else {const BYTE* p=bytes+i*3;int32_t value=int32_t(uint32_t(p[0])<<8|uint32_t(p[1])<<16|uint32_t(p[2])<<24);sample=float(value/2147483648.0);}
                    if(!std::isfinite(sample)) sample=0; block.samples[i]=sample;maximum=(std::max)(maximum,std::abs(sample));
                }
                peak=maximum; sink(std::move(block));
                // Release before querying the next packet.
                release.c->ReleaseBuffer(release.n);release.n=0;
                hr(capture->GetNextPacketSize(&available),"Query next audio packet");
            }
            if(stopped)break;
        }
    } catch(const std::exception& e) {std::lock_guard lock(mutex);failure=e.what();}
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
    std::vector<AudioLaneConfig> result{{"game","Game Audio",2,config.game_gain,false}};
    auto apps=config.applications;std::sort(apps.begin(),apps.end(),[](const auto& a,const auto& b){if(social(a.process_name)!=social(b.process_name))return social(a.process_name);return normalize_process(a.process_name)<normalize_process(b.process_name);});
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
    void output(const uint8_t* bytes,size_t count){std::lock_guard delivery(delivery_);std::vector<PcmBlock> ready;int64_t completed=0;{std::lock_guard lock(mutex_);remainder_.insert(remainder_.end(),bytes,bytes+count);size_t consumed=0;while(!timeline_.empty()){auto& pending=*timeline_.front();size_t available=(remainder_.size()-consumed)/(size_t(channels_)*4);if(!available)break;size_t frames=(std::min)(available,size_t(pending.output_frames-pending.delivered));PcmBlock block;block.lane=pending.original.lane;block.source=pending.original.source;block.generation=pending.original.generation;block.channels=channels_;block.sample_rate=48000;block.start_us=pending.original.start_us+pending.delivered*1000000/48000;block.samples.resize(frames*channels_);memcpy(block.samples.data(),remainder_.data()+consumed,block.samples.size()*4);consumed+=block.samples.size()*4;pending.delivered+=frames;ready.push_back(std::move(block));if(pending.delivered==pending.output_frames){queued_bytes_-=pending.original.samples.size()*4;completed+=pending.original.samples.size()/pending.original.channels;timeline_.pop_front();}}remainder_.erase(remainder_.begin(),remainder_.begin()+consumed);}for(auto& block:ready)sink_(std::move(block));{std::lock_guard lock(mutex_);completed_frames_+=completed;complete_barriers();}}
    void run(){try{std::vector<std::wstring> args{L"-hide_banner",L"-nostdin",L"-v",L"error",L"-f",L"f32le",L"-ar",std::to_wstring(rate_),L"-ac",std::to_wstring(channels_),L"-i",L"pipe:0",L"-af",filter(),L"-f",L"f32le",L"-ar",L"48000",L"-ac",std::to_wstring(channels_),L"-flush_packets",L"1",L"pipe:1"};ProcessRunner::run(executable_,args,cancel_,std::chrono::milliseconds::zero(),[this](auto bytes,auto count){output(bytes,count);},true,[this](auto bytes,auto count){return read(bytes,count);});}catch(...){}
        std::lock_guard delivery(delivery_);std::vector<PcmBlock> fallback;{std::lock_guard lock(mutex_);failed_=true;for(auto& pending:timeline_){auto block=std::move(pending->original);size_t skip=size_t(pending->delivered)*block.sample_rate/48000;skip=(std::min)(skip,block.samples.size()/block.channels);block.start_us+=int64_t(skip)*1000000/block.sample_rate;block.samples.erase(block.samples.begin(),block.samples.begin()+skip*block.channels);if(!block.samples.empty())fallback.push_back(std::move(block));}input_.clear();timeline_.clear();queued_bytes_=0;}for(auto& block:fallback)sink_(std::move(block));{std::lock_guard lock(mutex_);completed_frames_=admitted_frames_;complete_barriers();done_=true;}changed_.notify_all();
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
                    for(auto& pending:timeline_){auto original=std::move(pending->original);size_t skip=size_t(pending->delivered)*original.sample_rate/48000;skip=(std::min)(skip,original.samples.size()/original.channels);original.start_us+=int64_t(skip)*1000000/original.sample_rate;original.samples.erase(original.samples.begin(),original.samples.begin()+skip*original.channels);if(!original.samples.empty())fallback.push_back(std::move(original));}
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
        ProcessTable table(enumerator.Get());std::map<std::string,WasapiConfig> wanted;
        auto source=[&](std::string key,std::string lane,uint32_t pid,bool mic,std::wstring device){WasapiConfig c;c.lane=std::move(lane);c.source=key;c.process_id=pid;c.microphone=mic;c.device_id=std::move(device);c.qpc_anchor=settings.qpc_anchor;c.qpc_frequency=settings.qpc_frequency;c.monotonic_anchor_us=settings.monotonic_anchor_us;wanted.emplace(std::move(key),std::move(c));};
        bool routed=!settings.applications.empty()||!settings.excluded_processes.empty();std::set<uint32_t> excluded{GetCurrentProcessId()};
        for(const auto& name:settings.excluded_processes){auto ids=table.names[normalize_process(name)];excluded.insert(ids.begin(),ids.end());}
        for(const auto& app:settings.applications){auto ids=table.names[normalize_process(app.process_name)];excluded.insert(ids.begin(),ids.end());auto pid=table.app_root(app.process_name);if(pid){auto lane=narrow(normalize_process(app.process_name));source("app:"+lane,lane,pid,false,{});}}
        if(routed){std::set<uint32_t> allowed;for(auto pid:table.active)if(!excluded.contains(pid)&&!table.ancestor(pid,excluded))allowed.insert(pid);for(auto it=allowed.begin();it!=allowed.end();){bool overlaps=false;for(auto excluded_pid:excluded)if(table.ancestor(excluded_pid,{*it})){overlaps=true;break;}if(overlaps){it=allowed.erase(it);std::lock_guard lock(mutex);failure="Audio process tree contains an excluded application; that route is unavailable";}else ++it;}auto game=table.names[normalize_process(settings.game_executable)];std::set<uint32_t> matched;std::set_intersection(allowed.begin(),allowed.end(),game.begin(),game.end(),std::inserter(matched,matched.end()));if(!matched.empty())allowed=std::move(matched);for(auto pid:table.roots(std::move(allowed)))source("game:"+std::to_string(pid),"game",pid,false,{});}
        else source("game:default","game",0,false,endpoint_id(enumerator.Get(),false,settings.output_device_id));
        for(const auto& selected:settings.microphone_device_ids){auto lane="mic:"+narrow(selected);try{source(lane,lane,0,true,endpoint_id(enumerator.Get(),true,selected));}catch(const std::exception& e){std::lock_guard lock(mutex);failure=e.what();}}
        for(auto it=routes.begin();it!=routes.end();){auto desired=wanted.find(it->first);auto& old=it->second;if(desired==wanted.end()||desired->second.process_id!=old.config.process_id||desired->second.device_id!=old.config.device_id||!old.capture->error().empty()||(old.config.microphone&&changed_filter)){old.capture->stop();if(old.capture->error().find("worker restart required")!=std::string::npos){std::lock_guard lock(mutex);failure=old.capture->error();}if(old.filter&&!old.filter->stop()){std::lock_guard lock(mutex);failure="Microphone filter shutdown timed out; worker restart required";}it=routes.erase(it);}else ++it;}
        for(auto& [key,c]:wanted){if(routes.contains(key))continue;c.generation=++generation;try{auto history_owner=history;auto live_sink=live;WasapiSource::Sink sink=[history_owner,live_sink](PcmBlock block){if(live_sink)live_sink(block);history_owner->submit(std::move(block));};std::shared_ptr<MicrophoneFilter> filter;if(c.microphone&&settings.noise_suppression&&!settings.rnnoise_model.empty()&&std::filesystem::exists(settings.rnnoise_model)){filter=std::make_shared<MicrophoneFilter>(settings.ffmpeg,settings.rnnoise_model,settings.gate_threshold_db,sink);sink=[filter](PcmBlock block){filter->submit(std::move(block));};{std::lock_guard lock(mutex);filters.push_back(filter);}}auto capture=std::make_unique<WasapiSource>(c,std::move(sink));routes.emplace(key,Route{c,std::move(capture),std::move(filter)});}catch(const std::exception& e){std::lock_guard lock(mutex);failure=e.what();}}
        stable_passes=previous_generation==generation&&previous_count==routes.size()?(std::min)(5,stable_passes+1):0;{std::lock_guard lock(mutex);filters.clear();for(const auto& [key,route]:routes)if(route.filter)filters.push_back(route.filter);}
    }
    void run(){HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);uint64_t seen=UINT64_MAX;try{hr(com,"Initialize audio routing COM");Microsoft::WRL::ComPtr<IMMDeviceEnumerator> notifications;hr(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&notifications)),"Create audio endpoint watcher");auto watcher=Microsoft::WRL::Make<EndpointChanges>();watcher->changed=[weak=weak_from_this()]{if(auto owner=weak.lock()){std::lock_guard lock(owner->mutex);++owner->revision;owner->changed.notify_all();}};hr(notifications->RegisterEndpointNotificationCallback(watcher.Get()),"Register audio endpoint watcher");struct Registration{IMMDeviceEnumerator* owner;IMMNotificationClient* watcher;~Registration(){owner->UnregisterEndpointNotificationCallback(watcher);}} registration{notifications.Get(),watcher.Get()};for(;;){AudioGraphConfig settings;{std::lock_guard lock(mutex);if(stopping)break;settings=config;seen=revision;}try{refresh(settings);}catch(const std::exception& e){std::lock_guard lock(mutex);failure=e.what();}std::unique_lock lock(mutex);changed.wait_for(lock,std::chrono::seconds(stable_passes>=5?5:2),[&]{return stopping||revision!=seen;});}}catch(const std::exception& e){std::lock_guard lock(mutex);failure=e.what();}for(auto& [key,route]:routes){route.capture->stop();if(route.capture->error().find("worker restart required")!=std::string::npos){std::lock_guard lock(mutex);failure=route.capture->error();}if(route.filter&&!route.filter->stop()){std::lock_guard lock(mutex);failure="Microphone filter shutdown timed out; worker restart required";}}routes.clear();history->stop();if(SUCCEEDED(com))CoUninitialize();{std::lock_guard lock(mutex);done=true;running=false;}changed.notify_all();}
};
AudioGraph::AudioGraph(AudioGraphConfig config,WasapiSource::Sink live):state_(std::make_shared<State>()){state_->history=std::make_shared<AudioHistory>(config.history_directory,config.retention_us);state_->config=std::move(config);state_->live=std::move(live);}
AudioGraph::~AudioGraph(){stop();}
void AudioGraph::start(){auto s=state_;std::lock_guard lock(s->mutex);if(s->running)return;if(s->stopping)throw std::runtime_error("Audio graph cannot restart after stop");s->running=true;std::thread([s]{s->run();}).detach();}
bool AudioGraph::stop(){auto s=state_;std::unique_lock lock(s->mutex);if(!s->running){lock.unlock();s->history->stop();return !restart_required();}s->stopping=true;s->changed.notify_all();if(!s->changed.wait_for(lock,std::chrono::seconds(10),[&]{return s->done;})){s->failure="Audio graph shutdown timed out; resource graph retained; worker restart required";return false;}lock.unlock();return !restart_required();}
void AudioGraph::update(AudioGraphConfig config){auto s=state_;std::lock_guard lock(s->mutex);s->config=std::move(config);++s->revision;s->changed.notify_all();}
std::future<AudioSnapshot> AudioGraph::snapshot(int64_t start,int64_t end){
    auto history=state_->history;auto initial=history->snapshot(start,end);
    std::vector<std::shared_ptr<MicrophoneFilter>> filters;{std::lock_guard lock(state_->mutex);filters=state_->filters;}
    if(filters.empty())return initial;
    std::vector<std::shared_future<void>> tickets;for(const auto& filter:filters)tickets.push_back(filter->barrier());
    auto promise=std::make_shared<std::promise<AudioSnapshot>>();auto result=promise->get_future();
    try{std::thread([owner=state_,history,promise,initial=std::move(initial),filters=std::move(filters),tickets=std::move(tickets),start,end]()mutable{
        try{
            // Pin currently-written generations immediately, before delayed
            // suppression can let retention pruning discard their prefixes.
            auto snapshot=initial.get();
            for(size_t i=0;i<tickets.size();++i){if(tickets[i].wait_for(std::chrono::seconds(5))!=std::future_status::ready)filters[i]->abort();if(tickets[i].wait_for(std::chrono::seconds(5))!=std::future_status::ready)throw std::runtime_error("Audio suppression snapshot barrier timed out; worker restart required");tickets[i].get();}
            auto tail=history->snapshot(start,end).get();
            for(auto& range:tail.ranges){auto existing=std::find_if(snapshot.ranges.begin(),snapshot.ranges.end(),[&](const AudioRange& value){return value.file==range.file&&value.first_frame==range.first_frame;});if(existing==snapshot.ranges.end())snapshot.ranges.push_back(std::move(range));else if(range.frame_count>existing->frame_count)*existing=std::move(range);}
            promise->set_value(std::move(snapshot));
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
