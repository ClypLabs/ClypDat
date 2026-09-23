#pragma once
#include <atomic>
#include <filesystem>
#include <functional>
#include <future>
#include <memory>
#include <string>
#include <vector>
#include <cstdint>

namespace clypdat {
struct PcmBlock {
    std::string lane;
    std::string source;
    uint64_t generation = 0;
    int64_t start_us = 0;
    int sample_rate = 48000, channels = 2;
    std::vector<float> samples;
    int64_t duration_us() const { return int64_t(samples.size() / channels) * 1000000 / sample_rate; }
};
struct AudioLaneConfig {
    std::string key, title;
    int channels = 2;
    float gain = 1;
    bool omit_if_silent = false;
};
struct AudioFile;
struct AudioRange {
    std::shared_ptr<AudioFile> file;
    std::string lane;
    std::string source;
    uint64_t generation = 0, first_frame = 0, frame_count = 0;
    int64_t start_us = 0;
    int sample_rate = 48000, channels = 2;
};
struct AudioSnapshot {
    int64_t start_us = 0, end_us = 0;
    std::vector<AudioRange> ranges;
};
struct AudioHistoryCalls {std::function<void()> before_write,before_flush;};
// Acquisition only copies/enqueues. One owner handles files; snapshot barriers
// preserve admission order and pin exact ranges, independent of later stop.
class AudioHistory {
public:
    explicit AudioHistory(std::filesystem::path directory, int64_t retention_us,AudioHistoryCalls calls={});
    ~AudioHistory();
    bool submit(PcmBlock block);
    std::future<AudioSnapshot> snapshot(int64_t start_us, int64_t end_us);
    void stop();
    uint64_t lost_samples() const;
    std::string error() const;
private:
    struct State; std::shared_ptr<State> state_;
};
// Writes one 48 kHz float WAV per logical lane. Input generations retain their
// source timestamps; overlapping replacement generations never double samples.
std::vector<std::pair<std::string, std::filesystem::path>> render_audio(
    const AudioSnapshot& snapshot, const std::vector<AudioLaneConfig>& lanes,
    const std::filesystem::path& directory, const std::atomic_bool& cancel);

struct WasapiConfig {
    std::wstring device_id; // Empty selects current default endpoint.
    bool microphone = false;
    uint32_t process_id = 0;
    bool exclude_process_tree = false;
    std::string lane;
    std::string source;
    uint64_t generation = 1;
    int64_t qpc_anchor = 0, qpc_frequency = 0, monotonic_anchor_us = 0;
};
class WasapiSource {
public:
    using Sink = std::function<void(PcmBlock)>;
    WasapiSource(WasapiConfig config, Sink sink);
    ~WasapiSource();
    void stop();
    std::string error() const;
    float peak() const;
private:
    struct State; std::shared_ptr<State> state_;
};
struct AudioApplicationConfig { std::wstring process_name; float gain=1; };
struct AudioGraphConfig {
    std::filesystem::path history_directory,ffmpeg,rnnoise_model;
    int64_t retention_us=60000000,qpc_anchor=0,qpc_frequency=0,monotonic_anchor_us=0;
    std::wstring game_executable,output_device_id;
    std::vector<std::wstring> excluded_processes,microphone_device_ids;
    std::vector<AudioApplicationConfig> applications;
    float game_gain=1,microphone_gain=1;
    bool microphone_stereo=false,noise_suppression=false;
    double gate_threshold_db=-100;
};
std::vector<AudioLaneConfig> recording_audio_lanes(const AudioGraphConfig& config);
class AudioGraph {
public:
    explicit AudioGraph(AudioGraphConfig config,WasapiSource::Sink live_audio={});
    ~AudioGraph();
    void start(); bool stop();
    void update(AudioGraphConfig config);
    std::future<AudioSnapshot> snapshot(int64_t start_us,int64_t end_us);
    std::vector<AudioLaneConfig> lanes()const;
    std::string error()const;
    bool restart_required()const;
private:
    struct State;std::shared_ptr<State> state_;
};
struct AudioMeterConfig {WasapiConfig source;std::filesystem::path ffmpeg,rnnoise_model;bool suppression=false;double gate_db=-100;};
// Native PCM injection exercises the same persistent stage used by graph/meter.
class RecordingMicrophoneFilter {
public:
    RecordingMicrophoneFilter(std::filesystem::path ffmpeg,std::filesystem::path model,double gate_db,WasapiSource::Sink sink);
    ~RecordingMicrophoneFilter();
    void submit(PcmBlock block);bool stop();std::shared_future<void> barrier();
private:
    struct State;std::shared_ptr<State> state_;
};
class AudioMeter {
public:
    explicit AudioMeter(AudioMeterConfig config);~AudioMeter();
    float level_db()const;bool stop();
private:
    struct State;std::shared_ptr<State> state_;
};
}
