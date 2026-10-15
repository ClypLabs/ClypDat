#include "clip_thumbnail.h"
#include <Windows.h>
#include <algorithm>
#include <fstream>
#include <memory>
#include <stdexcept>
#include <string>
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

// First decoded frame at or after at_us. One decode thread: a single picture
// gains nothing from ffmpeg's frame threads, which each want their own packet
// in flight and cost a burst of CPU on a machine that is running a game.
Frame decode_frame(const ClipThumbnailConfig& config) {
    AVFormatContext* opened = nullptr;
    const auto utf8_path = config.path.u8string();
    const std::string path(utf8_path.begin(), utf8_path.end());
    check(avformat_open_input(&opened, path.c_str(), nullptr, nullptr), "Open clip");
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
    decoder->thread_count = 1;
    decoder->thread_type = FF_THREAD_SLICE;
    check(avcodec_open2(decoder.get(), codec, nullptr), "Open decoder");

    const int64_t origin_us = format->start_time != AV_NOPTS_VALUE ? format->start_time : 0;
    const int64_t target_us = origin_us + std::max<int64_t>(0, config.at_us);
    if (config.at_us > 0) {
        check(avformat_seek_file(format.get(), -1, INT64_MIN, target_us, target_us, 0), "Seek clip");
        avcodec_flush_buffers(decoder.get());
    }
    const int64_t target_pts = av_rescale_q(target_us, AV_TIME_BASE_Q, stream->time_base);
    // Without a seek, frame 0 is the answer: the card shows the clip's
    // opening frame, the same one hover preview starts on.
    const auto wanted = [&](const AVFrame& frame) {
        return config.at_us <= 0 || frame.best_effort_timestamp == AV_NOPTS_VALUE || frame.best_effort_timestamp >= target_pts;
    };

    auto packet = new_packet();
    auto frame = new_frame();
    Frame last;
    bool draining = false;
    while (true) {
        if (!draining) {
            const int read = av_read_frame(format.get(), packet.get());
            if (read == AVERROR_EOF) { draining = true; check(avcodec_send_packet(decoder.get(), nullptr), "Flush decoder"); }
            else {
                check(read, "Read clip");
                if (packet->stream_index != index) { av_packet_unref(packet.get()); continue; }
                const int sent = avcodec_send_packet(decoder.get(), packet.get());
                av_packet_unref(packet.get());
                if (sent < 0 && sent != AVERROR(EAGAIN)) check(sent, "Decode clip");
            }
        }
        while (true) {
            const int received = avcodec_receive_frame(decoder.get(), frame.get());
            if (received == AVERROR(EAGAIN)) break;
            if (received == AVERROR_EOF) {
                // A target past the last frame still gets the last frame.
                if (last) return last;
                throw std::runtime_error("No video frames in this clip");
            }
            check(received, "Decode clip");
            if (wanted(*frame)) return frame;
            last = std::move(frame);
            frame = new_frame();
        }
    }
}

std::string encode_jpeg(const AVFrame& source, int width, int quality) {
    width = std::clamp(width, 16, 7680) & ~1;
    int height = int(std::max<int64_t>(2, (int64_t(source.height) * width + source.width / 2) / std::max(1, source.width)));
    height &= ~1;
    height = std::max(height, 2);

    // JPEG is BT.601 full range. Convert explicitly: handing the encoder
    // limited-range video is what made ffmpeg refuse filmstrips outright.
    auto picture = new_frame();
    picture->format = AV_PIX_FMT_YUV420P;
    picture->width = width;
    picture->height = height;
    picture->color_range = AVCOL_RANGE_JPEG;
    picture->colorspace = AVCOL_SPC_BT470BG;
    check(av_frame_get_buffer(picture.get(), 0), "Allocate thumbnail");
    std::unique_ptr<SwsContext, ScalerFree> scaler(sws_getContext(source.width, source.height, AVPixelFormat(source.format),
        width, height, AV_PIX_FMT_YUV420P, SWS_BILINEAR, nullptr, nullptr, nullptr));
    if (!scaler) throw std::runtime_error("Thumbnail scaler unavailable for this clip's pixel format");
    sws_setColorspaceDetails(scaler.get(), coefficients(source.colorspace), source.color_range == AVCOL_RANGE_JPEG ? 1 : 0,
        sws_getCoefficients(SWS_CS_ITU601), 1, 0, 1 << 16, 1 << 16);
    if (sws_scale(scaler.get(), source.data, source.linesize, 0, source.height, picture->data, picture->linesize) <= 0)
        throw std::runtime_error("Thumbnail conversion failed");

    const AVCodec* codec = avcodec_find_encoder(AV_CODEC_ID_MJPEG);
    if (!codec) throw std::runtime_error("No JPEG encoder in this FFmpeg build");
    std::unique_ptr<AVCodecContext, CodecFree> encoder(avcodec_alloc_context3(codec));
    if (!encoder) throw std::bad_alloc();
    encoder->width = width;
    encoder->height = height;
    encoder->pix_fmt = AV_PIX_FMT_YUV420P;
    encoder->color_range = AVCOL_RANGE_JPEG;
    encoder->colorspace = AVCOL_SPC_BT470BG;
    encoder->time_base = { 1, 25 };
    encoder->thread_count = 1;
    // Same scale as ffmpeg.exe's -q:v.
    encoder->flags |= AV_CODEC_FLAG_QSCALE;
    encoder->global_quality = std::clamp(quality, 2, 31) * FF_QP2LAMBDA;
    picture->quality = encoder->global_quality;
    check(avcodec_open2(encoder.get(), codec, nullptr), "Open JPEG encoder");
    check(avcodec_send_frame(encoder.get(), picture.get()), "Encode thumbnail");
    check(avcodec_send_frame(encoder.get(), nullptr), "Finish thumbnail");
    auto packet = new_packet();
    check(avcodec_receive_packet(encoder.get(), packet.get()), "Encode thumbnail");
    return std::string(reinterpret_cast<const char*>(packet->data), size_t(packet->size));
}
}

void write_clip_thumbnail(const ClipThumbnailConfig& config) {
    if (config.path.empty() || config.output.empty()) throw std::invalid_argument("Thumbnail needs a clip and an output path");
    const auto frame = decode_frame(config);
    const auto jpeg = encode_jpeg(*frame, config.width, config.quality);

    // Cards load the cache path as soon as it exists; a half-written file
    // would decode as a broken image and be cached that way.
    auto staging = config.output;
    staging += L".partial";
    {
        std::ofstream file(staging, std::ios::binary | std::ios::trunc);
        if (!file) throw std::runtime_error("Could not create the thumbnail file");
        file.write(jpeg.data(), std::streamsize(jpeg.size()));
        if (!file) throw std::runtime_error("Could not write the thumbnail file");
    }
    if (!MoveFileExW(staging.c_str(), config.output.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        const auto error = GetLastError();
        DeleteFileW(staging.c_str());
        throw std::runtime_error("Could not place the thumbnail file (Windows error " + std::to_string(error) + ")");
    }
}
}
