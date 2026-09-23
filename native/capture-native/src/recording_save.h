#pragma once
#include "recording_capture.h"
#include "recording_audio.h"
#include <mutex>
#include <deque>
#include <future>

namespace clypdat {
struct HistoryPacket { std::shared_ptr<const CaptureGeneration> generation; std::shared_ptr<const AVPacket> packet; int64_t acquired_us=0; bool fresh=true; };
struct SourceMapping { int64_t source_start_us=0, duration_us=0, output_start_us=0; };
struct VideoSnapshot {
    std::vector<HistoryPacket> packets;
    std::vector<SourceMapping> mappings;
    std::vector<SourceMapping> overlay_mappings;
    int64_t start_us=0,end_us=0;
    int64_t duration_us=0;
    uint64_t generation=0;
    bool frozen=false;
};
class VideoHistory {
public:
    explicit VideoHistory(int64_t retention_us);
    void append(std::shared_ptr<const CaptureGeneration> generation, Packet packet, int64_t acquired_us=0, bool fresh=true);
    VideoSnapshot snapshot(int64_t start_us,int64_t end_us,bool start_at_or_after=false,bool variable_frame_rate=false,int fps=60) const;
    void clear();
private:
    mutable std::mutex mutex_; std::deque<HistoryPacket> packets_; int64_t retention_us_;
};
struct ReplaySaveRequest {
    std::string id;
    std::filesystem::path output,work_directory,ffmpeg;
    VideoSnapshot video;
    std::shared_future<AudioSnapshot> audio;
    std::vector<AudioLaneConfig> lanes;
    // Captures an immutable native overlay snapshot at admission, never live state.
    std::function<void(const std::filesystem::path&,const std::vector<SourceMapping>&,const std::atomic_bool&)> publish_overlays;
    std::function<void()> completed;
};
struct ReplaySaveResult {
    std::string id,error;
    std::filesystem::path output;
    int64_t duration_us=0;
    uint64_t generation=0;
    bool frozen=false,cancelled=false;
    std::vector<SourceMapping> audio_mappings,overlay_mappings;
};
class SaveCoordinator {
public:
    SaveCoordinator(); ~SaveCoordinator();
    // Rejects busy admission immediately. Accepted requests own all snapshots.
    std::shared_future<ReplaySaveResult> begin(ReplaySaveRequest request);
    void cancel(const std::string& id);
    bool busy() const;
private:
    struct State; std::shared_ptr<State> state_;
};
void remux_video(const VideoSnapshot& video,const std::filesystem::path& output,const std::atomic_bool& cancel,bool fragmented=false);
bool recover_recording(const std::filesystem::path& input,const std::filesystem::path& ffmpeg,const std::atomic_bool& cancel);

struct FullSessionConfig {
    std::filesystem::path output;
    std::vector<AudioLaneConfig> lanes;
    size_t queue_bytes=64*1024*1024,queue_items=8192;
};
struct FullSessionStatus { bool running=false,finished=false; std::string error; int64_t duration_us=0; uint64_t converted_audio_frames=0; };
class FullSessionWriter {
public:
    FullSessionWriter(FullSessionConfig config,std::shared_ptr<const CaptureGeneration> generation);
    ~FullSessionWriter();
    bool video(std::shared_ptr<const CaptureGeneration> generation,const AVPacket& packet);
    bool audio(PcmBlock block);
    bool stop();
    FullSessionStatus status() const;
private:
    struct State; std::shared_ptr<State> state_;
};
}
