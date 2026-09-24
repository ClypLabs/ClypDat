#pragma once
#include "recording_capture.h"
#include "recording_audio.h"
#include <array>
#include <deque>
#include <functional>
#include <future>
#include <mutex>
#include <vector>

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
struct VideoHistoryOptions {
    // Tests and benchmarks: the original pruning, a scan of every retained
    // packet on each append.
    bool reference_pruning = false;
};
// Pruning work since construction, and the retained packets and keyframes
// now. examined counts packets the full scan visited or keyframe index
// entries the incremental path compared; slow_scans counts appends that
// scanned the whole keyframe index because keyframe PTS went backwards.
struct VideoHistoryStats {
    uint64_t appended = 0, examined = 0, pruned = 0, slow_scans = 0;
    size_t packets = 0, keyframes = 0, keyframes_peak = 0;
    // Mutex hold per append: total, maximum, and the most recent appends.
    uint64_t hold_ns_total = 0, hold_ns_max = 0;
    std::vector<uint32_t> recent_hold_ns;
};
// Keeps the newest keyframe at or before (latest PTS - retention), in append
// order, and every packet after it, so a save can always start on the GOP
// preceding its window. With no such keyframe nothing is pruned.
class VideoHistory {
public:
    explicit VideoHistory(int64_t retention_us, VideoHistoryOptions options = {});
    void append(std::shared_ptr<const CaptureGeneration> generation, Packet packet, int64_t acquired_us=0, bool fresh=true);
    VideoSnapshot snapshot(int64_t start_us,int64_t end_us,bool start_at_or_after=false,bool variable_frame_rate=false,int fps=60) const;
    void clear();
    VideoHistoryStats stats() const;
    // Tests: reads the retained packets under the history lock.
    void inspect(const std::function<void(const std::deque<HistoryPacket>&)>& reader) const;
private:
    struct Keyframe { uint64_t sequence; int64_t pts_us; };
    mutable std::mutex mutex_; std::deque<HistoryPacket> packets_; int64_t retention_us_;
    VideoHistoryOptions options_;
    // Every keyframe in packets_, oldest first. Sequences are absolute:
    // packets_[i] was appended as front_sequence_ + i.
    std::deque<Keyframe> keyframes_;
    uint64_t front_sequence_ = 0, next_sequence_ = 0;
    // Adjacent keyframes whose PTS goes backwards. While there are none the
    // keyframes at or before any cutoff are a prefix of keyframes_.
    size_t pts_inversions_ = 0;
    VideoHistoryStats stats_;
    std::array<uint32_t, 1024> hold_ns_{}; size_t holds_ = 0;
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
