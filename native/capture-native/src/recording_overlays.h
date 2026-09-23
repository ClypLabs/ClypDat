#pragma once

#include <cstdint>
#include <compare>
#include <atomic>
#include <deque>
#include <filesystem>
#include <functional>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <vector>

struct AVFrame;

namespace clypdat {
// Every timestamp uses the recorder's monotonic microsecond clock.
using OverlayClock = std::function<int64_t()>;
struct PhysicalKey {
    uint16_t scan_code = 0;
    bool e0 = false, e1 = false;
    uint8_t mouse_button = 0; // 1 left, 2 right, 3 middle, 4 back, 5 forward.
    auto operator<=>(const PhysicalKey&) const = default;
};
struct InputEdge { int64_t at_us; PhysicalKey key; bool down; };
struct InputCheckpoint { int64_t at_us; std::vector<PhysicalKey> down; };
struct InputSnapshot {
    int64_t start_us = 0, end_us = 0;
    bool unavailable = false, overflowed = false;
    std::vector<InputEdge> edges;
    std::vector<InputCheckpoint> checkpoints;
    std::string json(double media_scale = 1) const;
};
class InputHistory {
public:
    explicit InputHistory(size_t maximum_edges = 100000);
    void reset(bool available);
    void set_available(bool value);
    bool add(int64_t at_us, PhysicalKey key, bool down);
    InputSnapshot snapshot(int64_t start_us, int64_t end_us) const;
    std::vector<PhysicalKey> pressed() const;
    uint64_t revision() const;
    void on_change(std::function<void()> callback);
private:
    mutable std::mutex mutex_;
    size_t maximum_edges_;
    bool available_ = false, overflowed_ = false;
    uint64_t revision_ = 0;
    std::vector<InputEdge> edges_;
    std::vector<InputCheckpoint> checkpoints_;
    std::set<PhysicalKey> down_;
    std::function<void()> changed_;
};
class RawInputCapture {
public:
    RawInputCapture(std::shared_ptr<InputHistory> history, OverlayClock clock);
    ~RawInputCapture();
    RawInputCapture(const RawInputCapture&) = delete;
    RawInputCapture& operator=(const RawInputCapture&) = delete;
    bool start();
    // False means the worker must restart. Shared state remains alive until exit.
    bool stop(uint32_t timeout_ms = 2000);
private:
    struct State;
    std::shared_ptr<State> state_;
};
struct OverlayTransformNative { double x = 0, y = 0, width = 0.25; };
struct OverlaySettingsNative {
    uint64_t revision = 0;
    int64_t at_us = 0;
    bool burned = false;
    std::wstring camera_moniker, camera_name;
    std::string keyboard_layout = "None";
    // Immutable managed settings JSON retains custom keyboard caps for publication.
    std::string settings_json;
    OverlayTransformNative camera_transform{.7,.05,.25}, keyboard_transform{.05,.7,.35};
};
struct OverlayBitmap {
    int width = 0, height = 0, stride = 0;
    uint64_t revision = 0;
    int64_t at_us = 0;
    bool premultiplied = false;
    std::vector<uint8_t> bgra;
    static std::shared_ptr<const OverlayBitmap> copy(int width, int height, int stride,
        const uint8_t* pixels, size_t bytes, uint64_t revision, int64_t at_us, bool premultiplied);
};
struct CameraFileLease {
    std::filesystem::path path;
    bool remove_on_release = true;
    ~CameraFileLease();
};
struct CameraSegmentNative {
    uint64_t generation = 0;
    int64_t start_us = 0, end_us = 0;
    std::filesystem::path path;
    bool completed = false;
    uint64_t pinned_bytes = 0;
    std::shared_ptr<CameraFileLease> lease;
};
struct OverlaySnapshot {
    int64_t start_us = 0, end_us = 0;
    InputSnapshot input;
    std::vector<OverlaySettingsNative> settings;
    std::vector<std::shared_ptr<const OverlayBitmap>> artwork;
    std::vector<CameraSegmentNative> camera_segments;
};
struct OverlayCompositionResult { bool camera = false, keyboard = false; };
class OverlayHistory {
public:
    explicit OverlayHistory(std::shared_ptr<InputHistory> input);
    void reset(bool burned);
    void retention(int64_t duration_us);
    bool apply(OverlaySettingsNative settings);
    bool set_artwork(std::shared_ptr<const OverlayBitmap> bitmap);
    uint64_t replace_camera();
    bool camera_frame(uint64_t generation, std::shared_ptr<const OverlayBitmap> frame);
    void camera_segment(CameraSegmentNative segment);
    void complete_camera(uint64_t generation, int64_t end_us);
    std::shared_ptr<const OverlayBitmap> preview() const;
    OverlaySnapshot snapshot(int64_t start_us, int64_t end_us) const;
    OverlayCompositionResult compose(AVFrame& frame) const;
    OverlayCompositionResult compose_bgra(uint8_t* pixels, size_t bytes, int width, int height, int stride) const;
private:
    mutable std::mutex mutex_;
    std::shared_ptr<InputHistory> input_;
    bool burned_ = false;
    uint64_t camera_generation_ = 0;
    std::vector<OverlaySettingsNative> settings_;
    std::vector<std::shared_ptr<const OverlayBitmap>> artwork_;
    std::shared_ptr<const OverlayBitmap> camera_frame_;
    std::vector<CameraSegmentNative> camera_segments_;
    int64_t retention_us_ = 1200000000;
};
void finalize_camera_snapshot(OverlaySnapshot& snapshot, const std::filesystem::path& ffmpeg,
    const std::filesystem::path& destination, const std::atomic_bool& cancel);
// Independently owned handles serve recording and settings preview. Both use
// the same complete-frame reader and generation checks.
class RecordingCamera {
public:
    RecordingCamera(std::shared_ptr<OverlayHistory> history, OverlayClock clock);
    ~RecordingCamera();
    bool start(const std::filesystem::path& ffmpeg, const std::filesystem::path& work_root,
        const std::wstring& device_moniker, bool editable, bool settings_preview = false);
    bool stop(uint32_t timeout_ms = 5000);
    std::string error() const;
    bool running() const;
private:
    struct State;
    std::shared_ptr<State> state_;
};
struct CameraPreviewModeNative {
    int width = 0, height = 0;
    double fps = 0;
    std::string format;
    bool compressed = false;
    bool operator==(const CameraPreviewModeNative&) const = default;
};
std::vector<CameraPreviewModeNative> camera_preview_modes(const std::string& probe_output);
}
