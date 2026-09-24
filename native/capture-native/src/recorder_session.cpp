#include "recorder_session.h"
#include <Windows.h>
#include <algorithm>
#include <stdexcept>
#include <condition_variable>

namespace clypdat {
struct RecorderSession::State {
    RecorderSessionConfig config;
    mutable std::mutex mutex;
    std::mutex admission, lifecycle;
    std::mutex event_mutex;
    std::condition_variable event_ready;
    uint64_t event_sequence = 0;
    uint32_t event_mask = 0;
    int64_t last_health_event = 0;
    bool started = false, stopped = false;
    std::atomic_bool restart_required = false;
    std::atomic_bool retained_after_failure = false;
    std::atomic_bool burned = false;
    std::unique_ptr<RecordingCapture> capture;
    std::shared_ptr<AudioHistory> audio;
    std::unique_ptr<AudioGraph> graph;
    std::vector<std::unique_ptr<WasapiSource>> sources;
    std::shared_ptr<InputHistory> input = std::make_shared<InputHistory>();
    std::shared_ptr<OverlayHistory> overlays = std::make_shared<OverlayHistory>(input);
    std::unique_ptr<RawInputCapture> raw_input;
    std::unique_ptr<RecordingCamera> camera;
    VideoHistory video;
    SaveCoordinator saves;
    std::map<std::string, RecorderSave> results;
    std::optional<CapturePixels> detector;
    std::optional<RecordingDetectorSnapshot> detector_regions;
    std::map<int64_t, OverlayCompositionResult> compositions;
    std::deque<std::pair<int64_t, OverlayCompositionResult>> composed_packets;
    // What burned overlay composition did, frame by frame.
    struct OverlayTally {
        uint64_t camera_frames = 0, keyboard_frames = 0, skipped_frames = 0;
        int64_t last_rendered_us = 0, last_composed_us = 0;
        bool last_complete = false; // The last frame drew every requested layer.
        std::string last_skip_reason;
    } overlay_tally;
    std::shared_ptr<FullSessionWriter> session;
    std::filesystem::path active_session_path;
    std::vector<RecorderClosedSession> completed_sessions;
    std::string session_error;
    std::vector<std::future<bool>> closing_sessions;
    explicit State(RecorderSessionConfig value) : config(std::move(value)),
        video(int64_t(config.history_seconds) * 1000000) {}
    void notify(uint32_t mask) {
        { std::lock_guard lock(event_mutex); event_mask |= mask; ++event_sequence; }
        event_ready.notify_all();
    }
    static void retain(const std::shared_ptr<State>& state) {
        if (state->retained_after_failure.exchange(true)) return;
        static std::mutex retained_mutex;
        static auto* retained = new std::vector<std::shared_ptr<State>>;
        std::lock_guard lock(retained_mutex);
        retained->push_back(state);
    }
    // Why a requested layer was not drawn on a frame.
    static std::string skip_reason(const OverlayFrame& layers, OverlayCompositionResult drawn) {
        if (layers.camera.requested && !drawn.camera) {
            if (!layers.camera.bitmap) return layers.camera_failure.empty() ? "Camera has no frame yet." : "Camera unavailable: " + layers.camera_failure;
            return "Camera frame could not be drawn.";
        }
        if (layers.keyboard.requested && !drawn.keyboard)
            return layers.keyboard.bitmap ? "Keyboard artwork could not be drawn." : "Keyboard artwork is not ready.";
        return {};
    }
    void composed(int64_t pts, const OverlayFrame& layers, OverlayCompositionResult drawn) {
        std::lock_guard lock(mutex);
        compositions[pts] = drawn;
        // Frames dropped before encoding never reach a packet.
        while (compositions.begin()->first < pts - 10000000) compositions.erase(compositions.begin());
        if (!layers.any_requested()) return;
        auto& tally = overlay_tally;
        tally.camera_frames += drawn.camera; tally.keyboard_frames += drawn.keyboard;
        tally.last_composed_us = pts;
        auto reason = skip_reason(layers, drawn);
        tally.last_complete = reason.empty();
        if (!reason.empty()) { ++tally.skipped_frames; tally.last_skip_reason = std::move(reason); }
        if (drawn.camera || drawn.keyboard) tally.last_rendered_us = pts;
    }
    void start_camera(const OverlaySettingsNative& settings) {
        try {
            if (!camera->start(config.ffmpeg, config.work_directory / L"camera", settings.camera_moniker, !settings.burned))
                overlays->camera_failed("Camera did not stop in time to restart.");
        } catch (const std::exception& error) { overlays->camera_failed(std::string("Camera could not start: ") + error.what()); }
        catch (...) { overlays->camera_failed("Camera could not start."); }
    }
    int64_t now() const {
        LARGE_INTEGER tick{}; QueryPerformanceCounter(&tick);
        return config.capture.monotonic_anchor_us +
            static_cast<int64_t>((tick.QuadPart - config.capture.qpc_anchor) *
                (1000000.0L / config.capture.qpc_frequency));
    }
    void pcm(PcmBlock block) {
        std::shared_ptr<FullSessionWriter> writer;
        { std::lock_guard lock(mutex); writer = session; }
        if (writer) writer->audio(block);
        audio->submit(std::move(block));
    }
};

RecorderSession::RecorderSession(RecorderSessionConfig config, std::unique_ptr<RecordingFrameSource> source) {
    if (config.history_seconds < 1 || config.history_seconds > 1200 ||
        config.capture.qpc_frequency <= 0 || config.work_directory.empty())
        throw std::invalid_argument("Invalid recorder configuration");
    auto s = state_ = std::make_shared<State>(std::move(config));
    s->audio = std::make_shared<AudioHistory>(s->config.work_directory / L"audio",
        int64_t(s->config.history_seconds) * 1000000);
    std::weak_ptr<State> weak = s;
    s->input->on_change([weak] { if (auto state = weak.lock()) state->notify(2); });
    s->overlays->reset(s->config.overlays.burned);
    s->overlays->retention(int64_t(s->config.history_seconds) * 1000000);
    s->burned = s->config.overlays.burned;
    s->overlays->apply(s->config.overlays);
    auto clock = [weak] { auto p = weak.lock(); return p ? p->now() : int64_t(0); };
    s->raw_input = std::make_unique<RawInputCapture>(s->input, clock);
    s->camera = std::make_unique<RecordingCamera>(s->overlays, clock);
    RecordingCaptureCallbacks callbacks;
    callbacks.generation = [weak](std::shared_ptr<const CaptureGeneration> generation) {
        auto p = weak.lock(); if (!p || !p->config.full_session) return;
        std::lock_guard lock(p->mutex);
        if (p->session) {
            auto old = std::move(p->session);
            auto path = p->active_session_path;
            p->closing_sessions.push_back(std::async(std::launch::async, [old, path, weak] {
                const bool stopped = old->stop();
                if (auto state = weak.lock()) {
                    std::lock_guard lock(state->mutex);
                    state->completed_sessions.push_back({state->completed_sessions.size() + 1, path, old->status()});
                    state->notify(16);
                }
                return stopped;
            }));
        }
        auto settings = *p->config.full_session;
        if (p->graph) settings.lanes = p->graph->lanes();
        if (generation->id > 1) {
            auto extension = settings.output.extension();
            settings.output.replace_extension();
            settings.output += L"-" + std::to_wstring(generation->id);
            settings.output += extension.native();
        }
        // Session failures are isolated by FullSessionWriter.
        try {
            p->active_session_path = settings.output;
            p->session = std::make_shared<FullSessionWriter>(std::move(settings), std::move(generation));
        }
        catch (const std::exception& error) { p->session_error = error.what(); p->session.reset(); }
    };
    callbacks.packet = [weak](std::shared_ptr<const CaptureGeneration> generation, Packet packet, int64_t acquired_us, bool fresh) {
        auto p = weak.lock(); if (!p) return;
        std::shared_ptr<FullSessionWriter> writer;
        bool publish_health = false;
        { std::lock_guard lock(p->mutex); writer = p->session; }
        {
            std::lock_guard lock(p->mutex);
            auto composition = p->compositions.find(packet->pts);
            if (composition != p->compositions.end()) {
                p->composed_packets.emplace_back(packet->pts, composition->second);
                p->compositions.erase(composition);
            }
            while (!p->composed_packets.empty() && p->composed_packets.front().first < packet->pts - int64_t(p->config.history_seconds + 5) * 1000000)
                p->composed_packets.pop_front();
            if (packet->pts - p->last_health_event >= 250000) { p->last_health_event = packet->pts; publish_health = true; }
        }
        if (writer) writer->video(generation, *packet);
        p->video.append(std::move(generation), std::move(packet), acquired_us, fresh);
        if (publish_health) p->notify(1);
    };
    callbacks.detector = [weak](CapturePixels pixels) {
        if (auto p = weak.lock()) { std::lock_guard lock(p->mutex); p->detector = std::move(pixels); }
    };
    callbacks.detector_snapshot = [weak](RecordingDetectorSnapshot pixels) {
        if (auto p = weak.lock()) {
            { std::lock_guard lock(p->mutex); p->detector_regions = std::move(pixels); }
            p->notify(4);
        }
    };
    callbacks.failure = [weak](const std::string&) { if (auto state = weak.lock()) state->notify(1); };
    callbacks.overlay_frame = [weak](int64_t pts) { auto p = weak.lock(); return p ? p->overlays->frame(pts) : OverlayFrame{}; };
    callbacks.overlay_composed = [weak](int64_t pts, const OverlayFrame& layers, OverlayCompositionResult drawn) {
        if (auto p = weak.lock()) p->composed(pts, layers, drawn);
    };
    callbacks.overlay_enabled = [weak] { auto p = weak.lock(); return p && p->burned.load(); };
    if (s->config.audio_graph) s->graph = std::make_unique<AudioGraph>(*s->config.audio_graph,
        [weak](PcmBlock block) {
            auto p = weak.lock(); if (!p) return;
            std::shared_ptr<FullSessionWriter> writer;
            { std::lock_guard lock(p->mutex); writer = p->session; }
            if (writer) writer->audio(std::move(block));
        });
    s->capture = std::make_unique<RecordingCapture>(s->config.capture, std::move(callbacks), std::move(source));
}
RecorderSession::~RecorderSession() { stop(); }
void RecorderSession::start() {
    auto s = state_; std::lock_guard lifecycle(s->lifecycle);
    if (s->restart_required || s->stopped) throw std::logic_error("Create a new recorder session after stop");
    if (s->started) return;
    s->started = true;
    if (s->graph) s->graph->start();
    if (s->config.capture_input) s->raw_input->start();
    // A camera that cannot start is reported, never a failed session.
    if (!s->config.overlays.camera_moniker.empty()) s->start_camera(s->config.overlays);
    std::weak_ptr<State> weak = s;
    for (auto source : s->config.audio_sources) {
        source.qpc_anchor = s->config.capture.qpc_anchor;
        source.qpc_frequency = s->config.capture.qpc_frequency;
        source.monotonic_anchor_us = s->config.capture.monotonic_anchor_us;
        s->sources.push_back(std::make_unique<WasapiSource>(source,
            [weak](PcmBlock block) { if (auto p = weak.lock()) p->pcm(std::move(block)); }));
    }
    s->capture->start();
    s->notify(1 | 32);
}
bool RecorderSession::stop() {
    auto s = state_; if (!s) return true;
    std::lock_guard lifecycle(s->lifecycle);
    if (s->stopped) return !s->restart_required;
    // Never tear down a graph whose acquisition/encoding owner still uses it.
    if (!s->capture->stop()) {
        s->restart_required = true;
        // Keep the entire graph through process exit after failed bounded joins.
        State::retain(s);
        return false;
    }
    bool audio_stopped = true;
    for (auto& source : s->sources) {
        source->stop();
        audio_stopped &= source->error().find("worker restart required") == std::string::npos;
    }
    audio_stopped = (!s->graph || s->graph->stop()) && audio_stopped;
    s->sources.clear();
    const bool input_stopped = s->raw_input->stop();
    const bool camera_stopped = s->camera->stop();
    s->audio->stop();
    audio_stopped &= s->audio->error().find("worker restart required") == std::string::npos;
    std::shared_ptr<FullSessionWriter> writer;
    { std::lock_guard lock(s->mutex); writer = s->session; }
    bool session_stopped = !writer || writer->stop();
    if (writer) {
        std::lock_guard lock(s->mutex);
        s->completed_sessions.push_back({s->completed_sessions.size() + 1, s->active_session_path, writer->status()});
    }
    for (auto& future : s->closing_sessions) session_stopped = future.get() && session_stopped;
    s->closing_sessions.clear();
    s->stopped = true;
    s->restart_required = s->restart_required || !input_stopped || !camera_stopped || !session_stopped || !audio_stopped;
    if (s->restart_required) State::retain(s);
    s->notify(1 | 16);
    return !s->restart_required;
}
void RecorderSession::pause(bool value) { state_->capture->pause(value); }
void RecorderSession::frame_rate(int value) { state_->capture->request_frame_rate(value); }
RecordingCaptureHealth RecorderSession::health() const {
    auto result = state_->capture->health();
    const auto current = state_->now();
    const auto layers = state_->overlays->frame(current);
    auto& overlay = result.overlay;
    overlay.enabled = layers.any_requested();
    overlay.camera_requested = layers.burned && layers.camera.requested; overlay.camera_ready = overlay.camera_requested && layers.camera.bitmap;
    overlay.camera_stale = layers.camera_stale;
    overlay.keyboard_requested = layers.burned && layers.keyboard.requested; overlay.keyboard_ready = overlay.keyboard_requested && layers.keyboard.bitmap;
    overlay.settings_revision = layers.settings_revision; overlay.camera_generation = layers.camera_generation;
    overlay.keyboard_revision = layers.keyboard_revision;
    overlay.failure = overlay.camera_requested ? layers.camera_failure : std::string{};
    State::OverlayTally tally; { std::lock_guard lock(state_->mutex); tally = state_->overlay_tally; }
    overlay.camera_frames = tally.camera_frames; overlay.keyboard_frames = tally.keyboard_frames;
    overlay.skipped_frames = tally.skipped_frames; overlay.last_skip_reason = tally.last_skip_reason;
    overlay.last_rendered_us = tally.last_rendered_us;
    const bool ready = (!overlay.camera_requested || overlay.camera_ready) && (!overlay.keyboard_requested || overlay.keyboard_ready);
    overlay.state = !overlay.enabled ? "disabled" :
        overlay.camera_requested && !overlay.camera_ready && !layers.camera_failure.empty() ? "failed" :
        !ready ? "source-not-ready" : layers.camera_stale ? "stale" :
        tally.last_complete && current - tally.last_composed_us < 1000000 ? "rendered" : "ready";
    if (state_->restart_required) {
        result.restart_required = true;
        if (result.error.empty()) result.error = "Native recorder shutdown requires a worker restart; resources retained.";
    }
    return result;
}
void RecorderSession::submit_pcm(PcmBlock block) { state_->pcm(std::move(block)); }
void RecorderSession::save(const std::string& id, int64_t start_us, int64_t end_us,
    const std::filesystem::path& output) {
    auto s = state_; std::lock_guard admission(s->admission);
    std::lock_guard lifecycle(s->lifecycle);
    if (!s->started || s->stopped) throw std::logic_error("Replay buffer is not recording");
    if (id.empty() || end_us <= start_us || output.empty()) throw std::invalid_argument("Invalid save request");
    if (s->saves.busy()) throw std::logic_error("A replay save is already in progress");
    {
        std::lock_guard lock(s->mutex);
        if (s->results.contains(id)) throw std::invalid_argument("Save identifier already exists");
    }
    ReplaySaveRequest request;
    request.id = id; request.output = output; request.ffmpeg = s->config.ffmpeg;
    request.work_directory = s->config.work_directory / L"saves" / std::filesystem::path(id);
    int64_t safe_start = start_us;
    if (!s->capture->safe_save_start(start_us, end_us, safe_start))
        throw std::logic_error("Replay capture is still recovering. Try again after recording resumes.");
    request.video = s->video.snapshot(safe_start, end_us, safe_start > start_us,
        s->config.capture.variable_frame_rate, s->config.capture.fps);
    // Include the preceding keyframe's source interval in every snapshot.
    // Audio correction and overlay publication use independent mappings later.
    auto source_start = request.video.start_us, source_end = request.video.end_us;
    for (const auto& mapping : request.video.overlay_mappings) {
        source_start = std::min(source_start, mapping.source_start_us);
        source_end = std::max(source_end, mapping.source_start_us + mapping.duration_us);
    }
    auto overlays = std::make_shared<OverlaySnapshot>(s->overlays->snapshot(source_start, source_end));
    request.publish_overlays = [overlays, ffmpeg = s->config.ffmpeg,
        directory = s->config.work_directory / L"camera-saves" / std::filesystem::path(id)](
            const std::filesystem::path&, const std::vector<SourceMapping>&, const std::atomic_bool& cancel) {
        finalize_camera_snapshot(*overlays, ffmpeg, directory, cancel);
    };
    request.audio = (s->graph ? s->graph->snapshot(source_start, source_end) : s->audio->snapshot(source_start, source_end)).share();
    request.lanes = s->graph ? s->graph->lanes() : s->config.audio_lanes;
    bool burned_camera = false, burned_keyboard = false;
    {
        std::lock_guard lock(s->mutex);
        const auto& first = request.video.packets.front();
        const auto& last = request.video.packets.back();
        const auto first_pts = av_rescale_q(first.packet->pts, first.generation->time_base, AVRational{1, 1000000});
        const auto last_pts = av_rescale_q(last.packet->pts, last.generation->time_base, AVRational{1, 1000000});
        for (const auto& [pts, result] : s->composed_packets) if (pts >= first_pts && pts <= last_pts) {
            burned_camera |= result.camera; burned_keyboard |= result.keyboard;
        }
    }
    s->capture->set_save_in_progress(true);
    std::weak_ptr<State> weak = s;
    request.completed = [weak] {
        if (auto state = weak.lock()) { state->capture->set_save_in_progress(false); state->notify(8); }
    };
    std::shared_future<ReplaySaveResult> completion;
    try { completion = s->saves.begin(std::move(request)); }
    catch (...) { s->capture->set_save_in_progress(false); throw; }
    std::lock_guard lock(s->mutex);
    s->results.emplace(id, RecorderSave{std::move(completion), std::move(overlays), burned_camera, burned_keyboard});
}
std::optional<RecorderSave> RecorderSession::saved(const std::string& id) const {
    std::lock_guard lock(state_->mutex);
    const auto found = state_->results.find(id);
    return found == state_->results.end() ? std::nullopt : std::optional(found->second);
}
void RecorderSession::cancel_save(const std::string& id) { state_->saves.cancel(id); }
bool RecorderSession::release_save(const std::string& id) {
    std::optional<RecorderSave> released;
    {
        std::lock_guard lock(state_->mutex);
        auto found = state_->results.find(id);
        if (found == state_->results.end()) return false;
        if (found->second.completion.wait_for(std::chrono::seconds(0)) != std::future_status::ready) return false;
        released.emplace(std::move(found->second));
        state_->results.erase(found);
    }
    return true;
}
void RecorderSession::overlay_settings(OverlaySettingsNative settings) {
    auto s = state_; std::lock_guard lifecycle(s->lifecycle);
    const bool camera_changed = settings.camera_moniker != s->config.overlays.camera_moniker;
    if (!s->started) {
        s->config.overlays.burned = settings.burned;
        s->burned = settings.burned;
        s->overlays->reset(settings.burned);
    }
    if (!s->overlays->apply(settings)) return;
    settings.burned = s->config.overlays.burned;
    s->config.overlays = settings;
    s->notify(32);
    if (camera_changed && s->started && !s->stopped) {
        if (!s->camera->stop()) { s->restart_required = true; State::retain(s); s->notify(1); return; }
        if (!settings.camera_moniker.empty()) s->start_camera(settings);
    }
}
bool RecorderSession::artwork(std::shared_ptr<const OverlayBitmap> value) { return state_->overlays->set_artwork(std::move(value)); }
uint64_t RecorderSession::input_revision() const { return state_->input->revision(); }
std::vector<PhysicalKey> RecorderSession::pressed_keys() const { return state_->input->pressed(); }
void RecorderSession::detector_regions(std::vector<CaptureRect> regions, std::vector<CaptureRect> masks, int width, int height) {
    state_->capture->set_detector_regions(std::move(regions), std::move(masks), width, height);
}
std::optional<CapturePixels> RecorderSession::detector_frame() const { std::lock_guard lock(state_->mutex); return state_->detector; }
void RecorderSession::detector_regions(std::array<CaptureNormalizedRect, 3> regions, bool enabled, bool counter_mask) {
    state_->capture->set_detector_regions(regions, enabled, counter_mask);
}
std::optional<RecordingDetectorSnapshot> RecorderSession::detector_snapshot() const {
    std::lock_guard lock(state_->mutex); return state_->detector_regions;
}
FullSessionStatus RecorderSession::full_session_status() const {
    std::lock_guard lock(state_->mutex);
    return state_->session ? state_->session->status() : FullSessionStatus{false, !state_->session_error.empty(), state_->session_error};
}
std::vector<RecorderClosedSession> RecorderSession::closed_sessions() const {
    std::lock_guard lock(state_->mutex); return state_->completed_sessions;
}
std::filesystem::path RecorderSession::full_session_path() const {
    std::lock_guard lock(state_->mutex); return state_->active_session_path;
}
std::shared_ptr<const OverlayBitmap> RecorderSession::camera_preview() const { return state_->overlays->preview(); }
RecorderEvents RecorderSession::events(uint64_t after_sequence, uint32_t timeout_ms) {
    if (timeout_ms > 1000) throw std::invalid_argument("Native event wait exceeds its bound");
    auto s = state_; std::unique_lock lock(s->event_mutex);
    s->event_ready.wait_for(lock, std::chrono::milliseconds(timeout_ms), [&] {
        return s->event_sequence != after_sequence || s->event_mask != 0;
    });
    RecorderEvents result{s->event_sequence, s->event_mask}; s->event_mask = 0;
    return result;
}
}
