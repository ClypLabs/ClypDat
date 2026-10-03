#include "clip_preview_decoder.h"
#include <Windows.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <stdexcept>
#include <thread>
#include <vector>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libswscale/swscale.h>
}

namespace clypdat {
uint64_t clip_preview_frames_per_loop(int64_t duration_us, int fps) {
    if (duration_us <= 0 || fps <= 0) return 0;
    return (uint64_t(duration_us) * uint64_t(fps) + 999999) / 1000000;
}

namespace {
std::string av_error(int code) {
    char text[AV_ERROR_MAX_STRING_SIZE]{};
    av_strerror(code, text, sizeof(text));
    return text;
}
void check(int code, const char* operation) {
    if (code < 0) throw std::runtime_error(std::string(operation) + ": " + av_error(code));
}
struct FormatClose { void operator()(AVFormatContext* value) const { avformat_close_input(&value); } };
struct CodecFree { void operator()(AVCodecContext* value) const { avcodec_free_context(&value); } };
struct FrameFree { void operator()(AVFrame* value) const { av_frame_free(&value); } };
struct PacketFree { void operator()(AVPacket* value) const { av_packet_free(&value); } };
using Frame = std::unique_ptr<AVFrame, FrameFree>;

Frame new_frame() {
    Frame frame(av_frame_alloc());
    if (!frame) throw std::bad_alloc();
    return frame;
}
// swscale assumes BT.601 unless told otherwise; recorded clips are usually
// BT.709, and a wrong matrix shifts every colour on the card.
const int* coefficients(AVColorSpace space) {
    switch (space) {
    case AVCOL_SPC_BT709: return sws_getCoefficients(SWS_CS_ITU709);
    case AVCOL_SPC_BT2020_NCL: case AVCOL_SPC_BT2020_CL: return sws_getCoefficients(SWS_CS_BT2020);
    case AVCOL_SPC_SMPTE240M: return sws_getCoefficients(SWS_CS_SMPTE240M);
    case AVCOL_SPC_FCC: return sws_getCoefficients(SWS_CS_FCC);
    default: return sws_getCoefficients(SWS_CS_DEFAULT);
    }
}
}

struct ClipPreviewDecoder::State {
    ClipPreviewConfig config;
    uint64_t per_loop = 0;
    size_t bytes = 0;
    mutable std::mutex mutex;
    std::condition_variable changed;
    std::vector<uint8_t> latest;
    uint64_t sequence = 0, taken = 0;
    bool stopping = false, done = false;
    std::string failure;
    std::atomic_bool stop_requested{false};
    std::atomic_uint64_t bytes_read{0};
    std::thread worker;

    // Rendering state, owned by the worker thread.
    std::vector<uint8_t> canvas;
    SwsContext* scaler = nullptr;
    int64_t converted_pts = AV_NOPTS_VALUE;
    bool canvas_cleared = false;

    ~State() { sws_freeContext(scaler); }

    void run() {
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
        try { decode(); }
        catch (const std::exception& error) { std::lock_guard lock(mutex); if (!stopping) failure = error.what(); }
        catch (...) { std::lock_guard lock(mutex); if (!stopping) failure = "Clip preview decoder failed"; }
        { std::lock_guard lock(mutex); done = true; }
        changed.notify_all();
    }

    bool stopped() const { return stop_requested.load(); }

    // Unpaced: wait until the previous frame was taken. Paced: wait for the
    // frame's slot on the clock. Either wait ends at once on stop.
    bool publish(std::chrono::steady_clock::time_point due) {
        std::unique_lock lock(mutex);
        if (config.paced) changed.wait_until(lock, due, [&] { return stopping; });
        else changed.wait(lock, [&] { return stopping || taken == sequence; });
        if (stopping) return false;
        std::copy(canvas.begin(), canvas.end(), latest.begin());
        ++sequence;
        lock.unlock();
        changed.notify_all();
        return true;
    }

    // Normal footage covers the canvas (scale up, centre crop); a saved crop
    // edit is cut out first and fitted inside it with black bars - the same
    // composition the library tile and export use.
    void render(const AVFrame& source) {
        if (converted_pts == source.best_effort_timestamp && converted_pts != AV_NOPTS_VALUE) return;
        const int width = config.width, height = config.height;
        int crop_x = 0, crop_y = 0, crop_width = source.width, crop_height = source.height;
        int target_x = 0, target_y = 0, target_width = width, target_height = height;
        const bool fitted = config.crop_width > 0 && config.crop_height > 0;
        if (fitted) {
            crop_x = std::clamp(config.crop_x, 0, source.width - 1);
            crop_y = std::clamp(config.crop_y, 0, source.height - 1);
            crop_width = std::clamp(config.crop_width, 1, source.width - crop_x);
            crop_height = std::clamp(config.crop_height, 1, source.height - crop_y);
            if (int64_t(crop_width) * height > int64_t(crop_height) * width)
                target_height = std::max(1, int(int64_t(crop_height) * width / crop_width));
            else
                target_width = std::max(1, int(int64_t(crop_width) * height / crop_height));
            target_x = (width - target_width) / 2;
            target_y = (height - target_height) / 2;
        } else if (int64_t(source.width) * height > int64_t(source.height) * width) {
            crop_width = std::max(1, int(int64_t(source.height) * width / height));
            crop_x = (source.width - crop_width) / 2;
        } else {
            crop_height = std::max(1, int(int64_t(source.width) * height / width));
            crop_y = (source.height - crop_height) / 2;
        }

        Frame view(av_frame_clone(&source));
        if (!view) throw std::bad_alloc();
        view->crop_left = size_t(crop_x);
        view->crop_top = size_t(crop_y);
        view->crop_right = size_t(source.width - crop_x - crop_width);
        view->crop_bottom = size_t(source.height - crop_y - crop_height);
        check(av_frame_apply_cropping(view.get(), AV_FRAME_CROP_UNALIGNED), "Crop preview frame");

        scaler = sws_getCachedContext(scaler, view->width, view->height, AVPixelFormat(view->format),
            target_width, target_height, AV_PIX_FMT_RGBA, SWS_BILINEAR, nullptr, nullptr, nullptr);
        if (!scaler) throw std::runtime_error("Preview scaler unavailable for this clip's pixel format");
        const auto* matrix = coefficients(view->colorspace);
        sws_setColorspaceDetails(scaler, matrix, view->color_range == AVCOL_RANGE_JPEG ? 1 : 0,
            sws_getCoefficients(SWS_CS_DEFAULT), 1, 0, 1 << 16, 1 << 16);

        if (fitted && !canvas_cleared) {
            for (size_t i = 0; i < canvas.size(); i += 4) { canvas[i] = canvas[i + 1] = canvas[i + 2] = 0; canvas[i + 3] = 255; }
            canvas_cleared = true;
        }
        uint8_t* planes[4] = { canvas.data() + (size_t(target_y) * width + target_x) * 4, nullptr, nullptr, nullptr };
        const int strides[4] = { width * 4, 0, 0, 0 };
        if (sws_scale(scaler, view->data, view->linesize, 0, view->height, planes, strides) <= 0)
            throw std::runtime_error("Preview frame conversion failed");
        converted_pts = source.best_effort_timestamp;
    }

    void decode() {
        AVFormatContext* opened = avformat_alloc_context();
        if (!opened) throw std::bad_alloc();
        // A hover that ends mid-read must not wait out a slow disk.
        opened->interrupt_callback = { [](void* self) { return static_cast<State*>(self)->stopped() ? 1 : 0; }, this };
        const auto utf8_path = config.path.u8string();
        const std::string path(utf8_path.begin(), utf8_path.end());
        const int open_result = avformat_open_input(&opened, path.c_str(), nullptr, nullptr);
        if (open_result < 0) {
            if (stopped()) return;
            check(open_result, "Open clip");
        }
        std::unique_ptr<AVFormatContext, FormatClose> format(opened);
        check(avformat_find_stream_info(format.get(), nullptr), "Read clip streams");
        const int index = av_find_best_stream(format.get(), AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
        check(index, "Find video stream");
        for (unsigned i = 0; i < format->nb_streams; ++i) if (int(i) != index) format->streams[i]->discard = AVDISCARD_ALL;
        AVStream* stream = format->streams[index];

        const AVCodec* codec = avcodec_find_decoder(stream->codecpar->codec_id);
        if (!codec) throw std::runtime_error("No decoder for this clip's video codec");
        std::unique_ptr<AVCodecContext, CodecFree> decoder(avcodec_alloc_context3(codec));
        if (!decoder) throw std::bad_alloc();
        check(avcodec_parameters_to_context(decoder.get(), stream->codecpar), "Configure decoder");
        decoder->pkt_timebase = stream->time_base;
        // A card-sized preview never needs the whole CPU; it shares the
        // machine with a game and the replay buffer.
        decoder->thread_count = std::clamp(int(std::thread::hardware_concurrency()) / 2, 1, 4);
        check(avcodec_open2(decoder.get(), codec, nullptr), "Open decoder");

        std::unique_ptr<AVPacket, PacketFree> packet(av_packet_alloc());
        if (!packet) throw std::bad_alloc();
        auto frame = new_frame(), held = new_frame();
        const int64_t origin_us = format->start_time != AV_NOPTS_VALUE ? format->start_time : 0;
        const int64_t start = config.start_us, fps = config.fps;
        const int64_t half_frame_us = 500000 / fps;
        auto slot_time = [&](uint64_t n) { return start + int64_t(n * 1000000 / uint64_t(fps)); };

        while (!stopped()) {
            const int64_t target = origin_us + start;
            const int seek_result = avformat_seek_file(format.get(), -1, INT64_MIN, target, target, 0);
            if (seek_result < 0 && stopped()) return;
            check(seek_result, "Seek clip");
            avcodec_flush_buffers(decoder.get());
            av_frame_unref(held.get());
            bool have_held = false, input_done = false;
            uint64_t n = 0;
            std::chrono::steady_clock::time_point pass_start{};

            auto emit_until = [&](int64_t pts_us) -> bool {
                // A source frame owns every output slot nearer to it than to
                // the next frame - ffmpeg's fps filter, round=near.
                while (n < per_loop && (pts_us == INT64_MAX || pts_us > slot_time(n) + half_frame_us)) {
                    render(*held);
                    if (n == 0) pass_start = std::chrono::steady_clock::now();
                    const auto due = pass_start + std::chrono::microseconds(int64_t(n * 1000000 / uint64_t(fps)));
                    if (!publish(due)) return false;
                    ++n;
                }
                return true;
            };
            auto take_frame = [&]() -> bool {
                const int64_t pts = frame->best_effort_timestamp;
                if (pts == AV_NOPTS_VALUE) return true;
                const int64_t pts_us = av_rescale_q(pts, stream->time_base, AV_TIME_BASE_Q) - origin_us;
                // Accurate seek: decode from the keyframe, show nothing before the range.
                if (pts_us < start - half_frame_us) return true;
                if (have_held && !emit_until(pts_us)) return false;
                av_frame_unref(held.get());
                av_frame_move_ref(held.get(), frame.get());
                have_held = true;
                return true;
            };

            while (n < per_loop && !stopped()) {
                if (!input_done) {
                    const int read = av_read_frame(format.get(), packet.get());
                    bytes_read = format->pb ? uint64_t(std::max<int64_t>(0, format->pb->bytes_read)) : 0;
                    if (read == AVERROR_EOF) { input_done = true; check(avcodec_send_packet(decoder.get(), nullptr), "Flush decoder"); }
                    else if (read < 0) { if (stopped()) return; check(read, "Read clip"); }
                    else {
                        const bool ours = packet->stream_index == index;
                        const int sent = ours ? avcodec_send_packet(decoder.get(), packet.get()) : 0;
                        av_packet_unref(packet.get());
                        if (sent < 0 && sent != AVERROR(EAGAIN) && sent != AVERROR_INVALIDDATA) check(sent, "Decode clip");
                    }
                }
                for (;;) {
                    const int received = avcodec_receive_frame(decoder.get(), frame.get());
                    if (received == AVERROR(EAGAIN)) break;
                    if (received == AVERROR_EOF) {
                        // Past the last frame: it holds the rest of the range.
                        if (!have_held) throw std::runtime_error("No video frames in the preview range");
                        if (!emit_until(INT64_MAX)) return;
                        break;
                    }
                    check(received, "Decode clip");
                    if (!take_frame()) return;
                    if (n >= per_loop) break;
                }
                if (input_done && n < per_loop && !have_held) throw std::runtime_error("No video frames in the preview range");
            }
            converted_pts = AV_NOPTS_VALUE;
        }
    }
};

ClipPreviewDecoder::ClipPreviewDecoder(ClipPreviewConfig config) : state_(std::make_shared<State>()) {
    if (config.width < 2 || config.height < 2 || config.width > 4096 || config.height > 4096 ||
        config.fps < 1 || config.fps > 240 || config.duration_us <= 0 || config.start_us < 0 || config.path.empty())
        throw std::invalid_argument("Invalid clip preview configuration");
    state_->per_loop = clip_preview_frames_per_loop(config.duration_us, config.fps);
    state_->bytes = size_t(config.width) * size_t(config.height) * 4;
    state_->latest.resize(state_->bytes);
    state_->canvas.resize(state_->bytes);
    state_->config = std::move(config);
    state_->worker = std::thread([state = state_] { state->run(); });
}

ClipPreviewDecoder::~ClipPreviewDecoder() {
    stop();
    if (state_->worker.joinable()) state_->worker.join();
}

uint64_t ClipPreviewDecoder::take(uint64_t after, uint8_t* rgba, size_t capacity, uint32_t timeout_ms) {
    auto& s = *state_;
    std::unique_lock lock(s.mutex);
    s.changed.wait_for(lock, std::chrono::milliseconds(timeout_ms), [&] {
        return (s.sequence != 0 && s.sequence != after) || s.done || s.stopping;
    });
    if (s.sequence == 0 || s.sequence == after) return 0;
    if (!rgba || capacity < s.bytes) throw std::invalid_argument("Clip preview frame buffer is too small");
    std::copy(s.latest.begin(), s.latest.end(), rgba);
    s.taken = s.sequence;
    const auto sequence = s.sequence;
    lock.unlock();
    s.changed.notify_all();
    return sequence;
}

void ClipPreviewDecoder::stop() {
    state_->stop_requested = true;
    { std::lock_guard lock(state_->mutex); state_->stopping = true; }
    state_->changed.notify_all();
}

bool ClipPreviewDecoder::finished() const { std::lock_guard lock(state_->mutex); return state_->done; }
std::string ClipPreviewDecoder::error() const { std::lock_guard lock(state_->mutex); return state_->failure; }
uint64_t ClipPreviewDecoder::source_bytes_read() const { return state_->bytes_read.load(); }
uint64_t ClipPreviewDecoder::frames_per_loop() const { return state_->per_loop; }
size_t ClipPreviewDecoder::frame_bytes() const { return state_->bytes; }
}
