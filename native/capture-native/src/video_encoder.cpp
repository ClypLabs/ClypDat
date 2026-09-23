#include "video_encoder.h"
extern "C" {
#include <libavformat/avformat.h>
#include <libavutil/hwcontext.h>
#include <libavutil/opt.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>
}
#include <algorithm>
#include <cstdlib>
#include <stdexcept>
#include <utility>

namespace clypdat {
namespace {
void checked(int result, const char* operation) {
    if (result >= 0) return;
    char message[AV_ERROR_MAX_STRING_SIZE]{};
    av_strerror(result, message, sizeof(message));
    throw std::runtime_error(std::string(operation) + ": " + message);
}
}

bool runtime_versions_match() {
    return avcodec_version() == LIBAVCODEC_VERSION_INT &&
        avformat_version() == LIBAVFORMAT_VERSION_INT &&
        avutil_version() == LIBAVUTIL_VERSION_INT &&
        swresample_version() == LIBSWRESAMPLE_VERSION_INT &&
        swscale_version() == LIBSWSCALE_VERSION_INT;
}

VideoEncoder::VideoEncoder(const VideoEncoderConfig& config, CodecCalls calls) : calls_(std::move(calls)) {
    if (!runtime_versions_match()) throw std::runtime_error("Bundled FFmpeg runtime does not match the recording SDK");
    if (config.width <= 0 || config.height <= 0 || (config.width & 1) || (config.height & 1) ||
        config.fps < 30 || config.fps > 120 || !calls_.send || !calls_.receive)
        throw std::invalid_argument("Invalid recording encoder configuration");
    const auto& name = config.name;
    if (name != "libx264" && name != "h264_nvenc" && name != "av1_nvenc" &&
        name != "h264_amf" && name != "av1_amf" && name != "h264_qsv" && name != "av1_qsv")
        throw std::invalid_argument("Unsupported recording encoder");
    const auto* codec = avcodec_find_encoder_by_name(name.c_str());
    if (!codec) throw std::runtime_error("Recording encoder unavailable: " + name);
    context_.reset(avcodec_alloc_context3(codec));
    if (!context_) throw std::bad_alloc();
    auto& context = *context_;
    context.width = config.width;
    context.height = config.height;
    context.time_base = { 1, 1000000 };
    context.framerate = { config.fps, 1 };
    context.pix_fmt = AV_PIX_FMT_NV12;
    context.color_range = AVCOL_RANGE_MPEG;
    context.colorspace = AVCOL_SPC_BT709;
    context.color_primaries = AVCOL_PRI_BT709;
    context.color_trc = AVCOL_TRC_BT709;
    context.bit_rate = static_cast<int64_t>(std::clamp(config.bitrate_mbps, 5, 100)) * 1000000;
    context.rc_buffer_size = static_cast<int>(context.bit_rate);
    context.rc_max_rate = context.bit_rate;
    context.gop_size = config.fps;
    context.max_b_frames = 0;
    context.flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    if (config.hardware_frames) {
        const auto* frames = reinterpret_cast<const AVHWFramesContext*>(config.hardware_frames->data);
        if (!frames || frames->format != AV_PIX_FMT_D3D11 || frames->sw_format != AV_PIX_FMT_NV12)
            throw std::invalid_argument("Recording requires D3D11 NV12 hardware frames");
        context.pix_fmt = AV_PIX_FMT_D3D11;
        context.hw_frames_ctx = av_buffer_ref(config.hardware_frames);
        context.hw_device_ctx = av_buffer_ref(frames->device_ref);
        if (!context.hw_frames_ctx || !context.hw_device_ctx) throw std::bad_alloc();
    }
    auto option = [&](const char* key, const std::string& value) {
        const int result = av_opt_set(context.priv_data, key, value.c_str(), 0);
        if (result < 0) unsupported_options_.push_back(std::string(key) + "=" + value + " (" + std::to_string(result) + ")");
    };
    if (name.ends_with("_nvenc")) {
        option("preset", "p1");
        if (name == "h264_nvenc") option("profile", "high");
        option("tune", "ll");
        option("surfaces", std::to_string(std::clamp((config.fps + 1) / 2, 16, 60)));
        if(config.nvenc_delay==4||config.nvenc_delay==8)option("delay",std::to_string(config.nvenc_delay));
        char* delay = nullptr;
        size_t length = 0;
        if (_dupenv_s(&delay, &length, "CLYPDAT_NVENC_DELAY") == 0 && delay) {
            const std::unique_ptr<char, decltype(&std::free)> owned(delay, &std::free);
            if (config.nvenc_delay!=4&&config.nvenc_delay!=8&&(std::string(delay) == "4" || std::string(delay) == "8")) option("delay", delay);
        }
        option("spatial-aq", "0");
        option("temporal-aq", "0");
        option("rc-lookahead", "0");
        option("rc", "cbr");
        option("forced-idr", "1");
    } else if (name.ends_with("_amf")) {
        option("usage", "ultralowlatency");
        option("quality", "speed");
        option("rc", name == "av1_amf" ? "hqcbr" : "cbr");
        option("forced_idr", "1");
    } else if (name.ends_with("_qsv")) {
        option("preset", "veryfast");
        option("rc_mode", "cbr");
        option("forced_idr", "1");
        option("async_depth", "4");
        if (config.low_power) option("low_power", "1");
    } else {
        option("preset", "ultrafast");
        option("tune", "zerolatency");
        option("bitrate", std::to_string(context.bit_rate));
    }
    checked(avcodec_open2(&context, codec, nullptr), "Open recording encoder");
}

int VideoEncoder::receive(std::vector<Packet>& packets) {
    while (true) {
        Packet packet(av_packet_alloc());
        if (!packet) throw std::bad_alloc();
        const int result = calls_.receive(context_.get(), packet.get());
        if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return result;
        checked(result, "Receive recording packet");
        packets.push_back(std::move(packet));
    }
}

void VideoEncoder::send(const AVFrame* frame, std::vector<Packet>& packets) {
    int result = calls_.send(context_.get(), frame);
    if (result == AVERROR(EAGAIN)) {
        const auto previous_size = packets.size();
        const int drained = receive(packets);
        if (drained != AVERROR(EAGAIN) || packets.size() == previous_size)
            throw std::runtime_error("Recording encoder made no progress after EAGAIN");
        // Retry the identical frame, including a null flush frame. Never advance
        // the source or release its surface while submission is pending.
        result = calls_.send(context_.get(), frame);
    }
    checked(result, "Submit recording frame");
}

std::vector<Packet> VideoEncoder::submit(const AVFrame& frame) {
    if (finished_ || failed_) throw std::logic_error("Recording encoder is closed");
    if (frame.width != context_->width || frame.height != context_->height || frame.format != context_->pix_fmt)
        throw std::invalid_argument("Recording frame does not match the encoder");
    try {
        std::vector<Packet> packets;
        send(&frame, packets);
        if (receive(packets) == AVERROR_EOF) throw std::runtime_error("Recording encoder ended before drain");
        return packets;
    } catch (...) { failed_ = true; throw; }
}

std::vector<Packet> VideoEncoder::finish() {
    if (failed_) throw std::logic_error("Recording encoder failed");
    if (finished_) return {};
    try {
        std::vector<Packet> packets;
        send(nullptr, packets);
        if (receive(packets) != AVERROR_EOF) throw std::runtime_error("Recording encoder did not finish draining");
        finished_ = true;
        return packets;
    } catch (...) { failed_ = true; throw; }
}
}
