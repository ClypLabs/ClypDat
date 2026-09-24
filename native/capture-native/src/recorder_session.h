#pragma once
#include "recording_capture.h"
#include "recording_audio.h"
#include "recording_save.h"
#include "recording_overlays.h"
#include <optional>
#include <map>

namespace clypdat {
struct RecorderSessionConfig {
    RecordingCaptureConfig capture;
    int history_seconds = 60;
    VideoHistoryOptions video_history;
    std::filesystem::path work_directory, ffmpeg;
    std::vector<WasapiConfig> audio_sources;
    std::vector<AudioLaneConfig> audio_lanes;
    std::optional<AudioGraphConfig> audio_graph;
    OverlaySettingsNative overlays;
    bool capture_input = true;
    std::optional<FullSessionConfig> full_session;
};
struct RecorderSave {
    std::shared_future<ReplaySaveResult> completion;
    std::shared_ptr<OverlaySnapshot> overlays;
    bool burned_camera = false, burned_keyboard = false;
};
struct RecorderClosedSession { uint64_t sequence; std::filesystem::path path; FullSessionStatus status; };
struct RecorderEvents { uint64_t sequence = 0; uint32_t mask = 0; };
// The DLL and generated-source verification own the same recording graph.
// Frame and PCM injection stays C++-only and is never a user backend option.
class RecorderSession {
public:
    explicit RecorderSession(RecorderSessionConfig config,
        std::unique_ptr<RecordingFrameSource> source = {});
    ~RecorderSession();
    RecorderSession(const RecorderSession&) = delete;
    RecorderSession& operator=(const RecorderSession&) = delete;
    void start();
    bool stop();
    void pause(bool value);
    void frame_rate(int value);
    RecordingCaptureHealth health() const;
    VideoHistoryStats video_history_stats() const;
    void submit_pcm(PcmBlock block);
    void save(const std::string& id, int64_t start_us, int64_t end_us,
        const std::filesystem::path& output);
    std::optional<RecorderSave> saved(const std::string& id) const;
    void cancel_save(const std::string& id);
    bool release_save(const std::string& id);
    void overlay_settings(OverlaySettingsNative settings);
    bool artwork(std::shared_ptr<const OverlayBitmap> value);
    uint64_t input_revision() const;
    std::vector<PhysicalKey> pressed_keys() const;
    void detector_regions(std::vector<CaptureRect> regions, std::vector<CaptureRect> masks,
        int width, int height);
    std::optional<CapturePixels> detector_frame() const;
    void detector_regions(std::array<CaptureNormalizedRect, 3> regions, bool enabled, bool counter_mask);
    std::optional<RecordingDetectorSnapshot> detector_snapshot() const;
    FullSessionStatus full_session_status() const;
    std::filesystem::path full_session_path() const;
    std::vector<RecorderClosedSession> closed_sessions() const;
    std::shared_ptr<const OverlayBitmap> camera_preview() const;
    RecorderEvents events(uint64_t after_sequence, uint32_t timeout_ms);
private:
    struct State;
    std::shared_ptr<State> state_;
};
}
