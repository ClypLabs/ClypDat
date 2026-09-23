#include "video_encoder.h"
#include <d3d11.h>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/channel_layout.h>
#include <libavutil/opt.h>
#include <libavutil/hwcontext.h>
#include <libavutil/hwcontext_d3d11va.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>
}

#include <cstdlib>
#include <iostream>
#include <memory>
#include <vector>
#include <string_view>

#define CHECK(expression) do { if (!(expression)) { \
    std::cerr << __FILE__ << ':' << __LINE__ << ": " #expression " failed\n"; \
    std::exit(EXIT_FAILURE); } } while (false)

struct context_deleter { void operator()(AVCodecContext* value) const { avcodec_free_context(&value); } };
struct frame_deleter { void operator()(AVFrame* value) const { av_frame_free(&value); } };
struct packet_deleter { void operator()(AVPacket* value) const { av_packet_free(&value); } };
using context_ptr = std::unique_ptr<AVCodecContext, context_deleter>;
using frame_ptr = std::unique_ptr<AVFrame, frame_deleter>;
using packet_ptr = std::unique_ptr<AVPacket, packet_deleter>;

struct buffer_deleter { void operator()(AVBufferRef* value) const { av_buffer_unref(&value); } };
using buffer_ptr = std::unique_ptr<AVBufferRef, buffer_deleter>;

void video_round_trip(int fps, const std::string& name = "libx264", bool gpu = false) {
    buffer_ptr device;
    buffer_ptr frames;
    const int width = gpu ? 256 : 64;
    const int height = gpu ? 144 : 48;
    if (gpu) {
        AVBufferRef* created = nullptr;
        CHECK(av_hwdevice_ctx_create(&created, AV_HWDEVICE_TYPE_D3D11VA, nullptr, nullptr, 0) == 0);
        device.reset(created);
        frames.reset(av_hwframe_ctx_alloc(device.get()));
        CHECK(frames);
        auto* pool = reinterpret_cast<AVHWFramesContext*>(frames->data);
        pool->format = AV_PIX_FMT_D3D11;
        pool->sw_format = AV_PIX_FMT_NV12;
        pool->width = width;
        pool->height = height;
        pool->initial_pool_size = 0; // Individual textures, as in the recorder.
        auto* d3d = reinterpret_cast<AVD3D11VAFramesContext*>(pool->hwctx);
        d3d->BindFlags = D3D11_BIND_RENDER_TARGET;
        CHECK(av_hwframe_ctx_init(frames.get()) == 0);
    }
    clypdat::VideoEncoder encoder({ width, height, fps, 5, name, false, frames.get() });
    const auto& context = encoder.context();
    frame_ptr frame(av_frame_alloc());
    CHECK(frame);
    frame->format = AV_PIX_FMT_NV12;
    frame->width = context.width;
    frame->height = context.height;
    CHECK(av_frame_get_buffer(frame.get(), 32) == 0);
    std::vector<clypdat::Packet> packets;
    auto append = [&](std::vector<clypdat::Packet> batch) {
        for (auto& packet : batch) packets.push_back(std::move(packet));
    };
    for (int index = 0; index < fps * 2; ++index) {
        CHECK(av_frame_make_writable(frame.get()) == 0);
        for (int plane = 0; plane < 2; ++plane) {
            const int width = frame->width;
            const int height = plane == 0 ? frame->height : frame->height / 2;
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                    frame->data[plane][y * frame->linesize[plane] + x] =
                        static_cast<uint8_t>(plane == 0 ? 32 + (x + y + index) % 180 : 128);
        }
        frame->pts = av_rescale_q(index, AVRational{ 1, fps }, context.time_base);
        if (gpu) {
            frame_ptr surface(av_frame_alloc());
            CHECK(surface);
            CHECK(av_hwframe_get_buffer(frames.get(), surface.get(), 0) == 0);
            CHECK(av_hwframe_transfer_data(surface.get(), frame.get(), 0) == 0);
            surface->pts = frame->pts;
            append(encoder.submit(*surface));
            // Release our surface immediately. The encoder must retain it
            // through delayed output and flush.
        } else {
            append(encoder.submit(*frame));
        }
    }
    append(encoder.finish());
    CHECK(encoder.finish().empty());
    CHECK(packets.size() == static_cast<size_t>(fps * 2));
    CHECK((packets[0]->flags & AV_PKT_FLAG_KEY) != 0);
    CHECK((packets[fps]->flags & AV_PKT_FLAG_KEY) != 0);
    const auto* decoder_codec = avcodec_find_decoder(context.codec_id);
    CHECK(decoder_codec);
    context_ptr decoder(avcodec_alloc_context3(decoder_codec));
    CHECK(decoder);
    AVCodecParameters* parameters = avcodec_parameters_alloc();
    CHECK(parameters);
    CHECK(avcodec_parameters_from_context(parameters, &context) == 0);
    CHECK(avcodec_parameters_to_context(decoder.get(), parameters) == 0);
    avcodec_parameters_free(&parameters);
    decoder->thread_count = 1;
    CHECK(avcodec_open2(decoder.get(), decoder_codec, nullptr) == 0);
    frame_ptr decoded(av_frame_alloc());
    CHECK(decoded);
    int count = 0;
    auto receive = [&] {
        while (true) {
            const auto result = avcodec_receive_frame(decoder.get(), decoded.get());
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) break;
            CHECK(result == 0);
            CHECK(decoded->width == width && decoded->height == height);
            CHECK(decoded->pts == av_rescale_q(count, AVRational{ 1, fps }, context.time_base));
            CHECK(decoded->color_range == AVCOL_RANGE_MPEG);
            CHECK(decoded->colorspace == AVCOL_SPC_BT709);
            ++count;
            av_frame_unref(decoded.get());
        }
    };
    for (const auto& packet : packets) {
        CHECK(avcodec_send_packet(decoder.get(), packet.get()) == 0);
        receive();
    }
    CHECK(avcodec_send_packet(decoder.get(), nullptr) == 0);
    receive();
    CHECK(count == fps * 2);
}

void resample_silence() {
    AVChannelLayout stereo = AV_CHANNEL_LAYOUT_STEREO;
    SwrContext* resampler = nullptr;
    CHECK(swr_alloc_set_opts2(&resampler, &stereo, AV_SAMPLE_FMT_FLT, 48000,
        &stereo, AV_SAMPLE_FMT_S16, 44100, 0, nullptr) == 0);
    CHECK(swr_init(resampler) == 0);
    std::vector<int16_t> input(44100 * 2);
    std::vector<float> output(48100 * 2, 1.0f);
    const uint8_t* source = reinterpret_cast<const uint8_t*>(input.data());
    auto* destination = reinterpret_cast<uint8_t*>(output.data());
    int count = swr_convert(resampler, &destination, 48100, &source, 44100);
    CHECK(count > 0);
    destination = reinterpret_cast<uint8_t*>(output.data() + count * 2);
    const int tail = swr_convert(resampler, &destination, 48100 - count, nullptr, 0);
    CHECK(tail >= 0);
    count += tail;
    CHECK(count == 48000);
    for (int index = 0; index < count * 2; ++index) CHECK(output[index] == 0);
    swr_free(&resampler);
    av_channel_layout_uninit(&stereo);
}

int main(int argc, char** argv) {
    const bool gpu = argc == 2 && std::string_view(argv[1]) == "--gpu";
    CHECK(clypdat::runtime_versions_match());
    CHECK(avcodec_version() == LIBAVCODEC_VERSION_INT);
    CHECK(avformat_version() == LIBAVFORMAT_VERSION_INT);
    CHECK(avutil_version() == LIBAVUTIL_VERSION_INT);
    CHECK(swresample_version() == LIBSWRESAMPLE_VERSION_INT);
    CHECK(swscale_version() == LIBSWSCALE_VERSION_INT);
    av_log_set_level(AV_LOG_ERROR);
    for (const int fps : { 30, 60, 90, 120 }) {
        video_round_trip(fps);
        if (gpu) {
            video_round_trip(fps, "h264_nvenc", true);
            video_round_trip(fps, "av1_nvenc", true);
        }
    }
    resample_silence();
    std::cout << (gpu ? "NVENC D3D11 H.264/AV1 and " : "") << "production x264 encode/decode and SDK PCM resampling passed\n";
}
