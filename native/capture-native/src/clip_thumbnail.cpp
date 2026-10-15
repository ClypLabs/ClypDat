#include "clip_thumbnail.h"
#include <Windows.h>
#include <algorithm>
#include <fstream>
#include <memory>
#include <optional>
#include <stdexcept>
#include <string>
#include <vector>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libswscale/swscale.h>
}

namespace clypdat {
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
struct ScalerFree { void operator()(SwsContext* value) const { sws_freeContext(value); } };
using Frame = std::unique_ptr<AVFrame, FrameFree>;
using Packet = std::unique_ptr<AVPacket, PacketFree>;

Frame new_frame() {
    Frame frame(av_frame_alloc());
    if (!frame) throw std::bad_alloc();
    return frame;
}
Packet new_packet() {
    Packet packet(av_packet_alloc());
    if (!packet) throw std::bad_alloc();
    return packet;
}
const int* coefficients(AVColorSpace space) {
    switch (space) {
    case AVCOL_SPC_BT709: return sws_getCoefficients(SWS_CS_ITU709);
    case AVCOL_SPC_BT2020_NCL: case AVCOL_SPC_BT2020_CL: return sws_getCoefficients(SWS_CS_BT2020);
    case AVCOL_SPC_SMPTE240M: return sws_getCoefficients(SWS_CS_SMPTE240M);
    case AVCOL_SPC_FCC: return sws_getCoefficients(SWS_CS_FCC);
    default: return sws_getCoefficients(SWS_CS_DEFAULT);
    }
}

// One open clip with a single-threaded decoder. A handful of pictures gains
// nothing from ffmpeg's frame threads, which each want their own packet in
// flight and cost a burst of CPU on a machine that is running a game.
class ClipReader {
public:
    explicit ClipReader(const std::filesystem::path& file) {
        AVFormatContext* opened = nullptr;
        const auto utf8_path = file.u8string();
        const std::string path(utf8_path.begin(), utf8_path.end());
        check(avformat_open_input(&opened, path.c_str(), nullptr, nullptr), "Open clip");
        format_.reset(opened);
        check(avformat_find_stream_info(format_.get(), nullptr), "Read clip streams");
        index_ = av_find_best_stream(format_.get(), AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
        check(index_, "Find video stream");
        for (unsigned i = 0; i < format_->nb_streams; ++i) if (int(i) != index_) format_->streams[i]->discard = AVDISCARD_ALL;
        stream_ = format_->streams[index_];
        const AVCodec* codec = avcodec_find_decoder(stream_->codecpar->codec_id);
        if (!codec) throw std::runtime_error("No decoder for this clip's video codec");
        decoder_.reset(avcodec_alloc_context3(codec));
        if (!decoder_) throw std::bad_alloc();
        check(avcodec_parameters_to_context(decoder_.get(), stream_->codecpar), "Configure decoder");
        decoder_->pkt_timebase = stream_->time_base;
        decoder_->thread_count = 1;
        decoder_->thread_type = FF_THREAD_SLICE;
        check(avcodec_open2(decoder_.get(), codec, nullptr), "Open decoder");
        origin_us_ = format_->start_time != AV_NOPTS_VALUE ? format_->start_time : 0;
    }

    int64_t duration_us() const { return format_->duration != AV_NOPTS_VALUE ? format_->duration : 0; }

    // Frame at or after at_us; past the last frame, the last frame. With
    // keyframe_within_us > 0, the keyframe at or before at_us is accepted
    // when it is that close: one decoded picture instead of a GOP's worth.
    Frame frame_at(int64_t at_us, int64_t keyframe_within_us = 0) {
        const int64_t target_us = origin_us_ + std::max<int64_t>(0, at_us);
        const int64_t target_pts = av_rescale_q(target_us, AV_TIME_BASE_Q, stream_->time_base);
        if (keyframe_within_us > 0) {
            seek(target_us, at_us);
            decoder_->skip_frame = AVDISCARD_NONKEY;
            auto key = decode([](const AVFrame&) { return true; });
            decoder_->skip_frame = AVDISCARD_DEFAULT;
            if (key) {
                const auto pts = key->best_effort_timestamp;
                const auto key_us = pts == AV_NOPTS_VALUE ? target_us : av_rescale_q(pts, stream_->time_base, AV_TIME_BASE_Q);
                if (target_us - key_us <= keyframe_within_us) return key;
            }
        }
        seek(target_us, at_us);
        auto frame = decode([&](const AVFrame& candidate) {
            return at_us <= 0 || candidate.best_effort_timestamp == AV_NOPTS_VALUE || candidate.best_effort_timestamp >= target_pts;
        });
        if (!frame) throw std::runtime_error("No video frames at this point in the clip");
        return frame;
    }

private:
    void seek(int64_t target_us, int64_t at_us) {
        // Frame 0 needs no seek; a fresh reader is already there.
        if (at_us <= 0 && !read_any_) return;
        check(avformat_seek_file(format_.get(), -1, INT64_MIN, target_us, target_us, 0), "Seek clip");
        avcodec_flush_buffers(decoder_.get());
    }

    template<class Wanted> Frame decode(Wanted wanted) {
        read_any_ = true;
        auto packet = new_packet();
        auto frame = new_frame();
        Frame last;
        bool draining = false;
        while (true) {
            if (!draining) {
                const int read = av_read_frame(format_.get(), packet.get());
                if (read == AVERROR_EOF) { draining = true; check(avcodec_send_packet(decoder_.get(), nullptr), "Flush decoder"); }
                else {
                    check(read, "Read clip");
                    if (packet->stream_index != index_) { av_packet_unref(packet.get()); continue; }
                    const int sent = avcodec_send_packet(decoder_.get(), packet.get());
                    av_packet_unref(packet.get());
                    if (sent < 0 && sent != AVERROR(EAGAIN)) check(sent, "Decode clip");
                }
            }
            while (true) {
                const int received = avcodec_receive_frame(decoder_.get(), frame.get());
                if (received == AVERROR(EAGAIN)) break;
                if (received == AVERROR_EOF) {
                    // Drained: the decoder must be reset before the next seek.
                    avcodec_flush_buffers(decoder_.get());
                    return last;
                }
                check(received, "Decode clip");
                if (wanted(*frame)) return frame;
                last = std::move(frame);
                frame = new_frame();
            }
        }
    }

    std::unique_ptr<AVFormatContext, FormatClose> format_;
    std::unique_ptr<AVCodecContext, CodecFree> decoder_;
    AVStream* stream_ = nullptr;
    int index_ = -1;
    int64_t origin_us_ = 0;
    bool read_any_ = false;
};

int even(int64_t value) { return int(std::max<int64_t>(2, value) & ~int64_t(1)); }

// JPEG is BT.601 full range. Convert explicitly: handing the encoder
// limited-range video is what made ffmpeg refuse filmstrips outright.
Frame new_picture(int width, int height) {
    auto picture = new_frame();
    picture->format = AV_PIX_FMT_YUV420P;
    picture->width = width;
    picture->height = height;
    picture->color_range = AVCOL_RANGE_JPEG;
    picture->colorspace = AVCOL_SPC_BT470BG;
    check(av_frame_get_buffer(picture.get(), 0), "Allocate image");
    return picture;
}

// Scales source into picture's region starting at column x (even).
void scale_into(SwsContext*& scaler, const AVFrame& source, AVFrame& picture, int x, int width, int height) {
    scaler = sws_getCachedContext(scaler, source.width, source.height, AVPixelFormat(source.format),
        width, height, AV_PIX_FMT_YUV420P, SWS_BILINEAR, nullptr, nullptr, nullptr);
    if (!scaler) throw std::runtime_error("Image scaler unavailable for this clip's pixel format");
    sws_setColorspaceDetails(scaler, coefficients(source.colorspace), source.color_range == AVCOL_RANGE_JPEG ? 1 : 0,
        sws_getCoefficients(SWS_CS_ITU601), 1, 0, 1 << 16, 1 << 16);
    uint8_t* planes[4] = {
        picture.data[0] + x,
        picture.data[1] + x / 2,
        picture.data[2] + x / 2,
        nullptr };
    if (sws_scale(scaler, source.data, source.linesize, 0, source.height, planes, picture.linesize) <= 0)
        throw std::runtime_error("Image conversion failed");
}

std::string encode_jpeg(AVFrame& picture, int quality) {
    const AVCodec* codec = avcodec_find_encoder(AV_CODEC_ID_MJPEG);
    if (!codec) throw std::runtime_error("No JPEG encoder in this FFmpeg build");
    std::unique_ptr<AVCodecContext, CodecFree> encoder(avcodec_alloc_context3(codec));
    if (!encoder) throw std::bad_alloc();
    encoder->width = picture.width;
    encoder->height = picture.height;
    encoder->pix_fmt = AV_PIX_FMT_YUV420P;
    encoder->color_range = AVCOL_RANGE_JPEG;
    encoder->colorspace = AVCOL_SPC_BT470BG;
    encoder->time_base = { 1, 25 };
    encoder->thread_count = 1;
    // Same scale as ffmpeg.exe's -q:v.
    encoder->flags |= AV_CODEC_FLAG_QSCALE;
    encoder->global_quality = std::clamp(quality, 2, 31) * FF_QP2LAMBDA;
    picture.quality = encoder->global_quality;
    check(avcodec_open2(encoder.get(), codec, nullptr), "Open JPEG encoder");
    check(avcodec_send_frame(encoder.get(), &picture), "Encode image");
    check(avcodec_send_frame(encoder.get(), nullptr), "Finish image");
    auto packet = new_packet();
    check(avcodec_receive_packet(encoder.get(), packet.get()), "Encode image");
    return std::string(reinterpret_cast<const char*>(packet->data), size_t(packet->size));
}

// Cards and the timeline load the cache path as soon as it exists; a
// half-written file would decode as a broken image and be cached that way.
void write_atomically(const std::filesystem::path& output, const std::string& bytes) {
    auto staging = output;
    staging += L".partial";
    {
        std::ofstream file(staging, std::ios::binary | std::ios::trunc);
        if (!file) throw std::runtime_error("Could not create the image file");
        file.write(bytes.data(), std::streamsize(bytes.size()));
        if (!file) throw std::runtime_error("Could not write the image file");
    }
    if (!MoveFileExW(staging.c_str(), output.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        const auto error = GetLastError();
        DeleteFileW(staging.c_str());
        throw std::runtime_error("Could not place the image file (Windows error " + std::to_string(error) + ")");
    }
}
}

void write_clip_thumbnail(const ClipThumbnailConfig& config) {
    if (config.path.empty() || config.output.empty()) throw std::invalid_argument("Thumbnail needs a clip and an output path");
    ClipReader reader(config.path);
    // Without a seek, frame 0: the card shows the clip's opening frame, the
    // same one hover preview starts on.
    const auto frame = reader.frame_at(config.at_us);
    const int width = even(std::clamp(config.width, 16, 7680));
    const int height = even((int64_t(frame->height) * width + frame->width / 2) / std::max(1, frame->width));
    auto picture = new_picture(width, height);
    SwsContext* scaler = nullptr;
    try { scale_into(scaler, *frame, *picture, 0, width, height); }
    catch (...) { sws_freeContext(scaler); throw; }
    sws_freeContext(scaler);
    write_atomically(config.output, encode_jpeg(*picture, config.quality));
}

int write_clip_filmstrip(const ClipFilmstripConfig& config) {
    if (config.path.empty() || config.output.empty()) throw std::invalid_argument("Filmstrip needs a clip and an output path");
    const int count = std::clamp(config.frame_count, 1, 80);
    ClipReader reader(config.path);
    const int64_t duration_us = config.duration_us > 0 ? config.duration_us : reader.duration_us();
    if (duration_us <= 0) throw std::runtime_error("Clip duration unknown");
    // A keyframe inside the tile's own time slot (half a span either side of
    // its point) stands for it: one decoded picture. Outside that, decode on
    // to the exact frame so long GOPs do not repeat the same picture. Strips
    // denser than the keyframes pay that exact decode at most points.
    const int64_t span_us = duration_us / count / 2;

    std::vector<Frame> frames(static_cast<size_t>(count));
    int decoded = 0;
    std::string first_error;
    for (int i = 0; i < count; ++i) {
        const int64_t at_us = int64_t((i + 0.5) / count * double(duration_us));
        try { frames[size_t(i)] = reader.frame_at(at_us, span_us); ++decoded; }
        catch (const std::exception& error) { if (first_error.empty()) first_error = error.what(); }
    }
    if (decoded == 0) throw std::runtime_error(first_error.empty() ? "No frames decoded" : first_error);
    // Damaged data, or a container claiming more duration than its video
    // holds, leaves gaps; the nearest good frame stands in.
    for (int i = 0; i < count; ++i) {
        if (frames[size_t(i)]) continue;
        for (int distance = 1; distance < count; ++distance) {
            const int before = i - distance, after = i + distance;
            const AVFrame* stand_in = before >= 0 && frames[size_t(before)] ? frames[size_t(before)].get()
                : after < count && frames[size_t(after)] ? frames[size_t(after)].get() : nullptr;
            if (stand_in) { frames[size_t(i)].reset(av_frame_clone(stand_in)); break; }
        }
    }

    // Same tile size as ffmpeg's scale=-2:height.
    const auto& sample = *frames[0];
    const int height = even(std::clamp(config.frame_height, 16, 1080));
    const int tile = even((int64_t(sample.width) * height + sample.height / 2) / std::max(1, sample.height));
    auto picture = new_picture(tile * count, height);
    SwsContext* scaler = nullptr;
    try {
        for (int i = 0; i < count; ++i) scale_into(scaler, *frames[size_t(i)], *picture, i * tile, tile, height);
    } catch (...) { sws_freeContext(scaler); throw; }
    sws_freeContext(scaler);
    write_atomically(config.output, encode_jpeg(*picture, config.quality));
    return decoded;
}
}
