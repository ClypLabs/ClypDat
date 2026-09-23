extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/channel_layout.h>
#include <libavutil/opt.h>
#include <libswresample/swresample.h>
#include <libswscale/swscale.h>
}

#include <cstdlib>
#include <iostream>
#include <memory>
#include <vector>

#define CHECK(expression) do { if (!(expression)) { \
    std::cerr << __FILE__ << ':' << __LINE__ << ": " #expression " failed\n"; \
    std::exit(EXIT_FAILURE); } } while (false)

struct context_deleter { void operator()(AVCodecContext* value) const { avcodec_free_context(&value); } };
struct frame_deleter { void operator()(AVFrame* value) const { av_frame_free(&value); } };
struct packet_deleter { void operator()(AVPacket* value) const { av_packet_free(&value); } };
using context_ptr = std::unique_ptr<AVCodecContext, context_deleter>;
using frame_ptr = std::unique_ptr<AVFrame, frame_deleter>;
using packet_ptr = std::unique_ptr<AVPacket, packet_deleter>;

void video_round_trip(int fps) {
    const auto* codec = avcodec_find_encoder_by_name("libx264");
    CHECK(codec != nullptr);
    context_ptr encoder(avcodec_alloc_context3(codec));
    CHECK(encoder);
    encoder->width = 64;
    encoder->height = 48;
    encoder->pix_fmt = AV_PIX_FMT_YUV420P;
    encoder->time_base = { 1, fps };
    encoder->framerate = { fps, 1 };
    encoder->gop_size = fps;
    encoder->max_b_frames = 0;
    encoder->thread_count = 1;
    CHECK(av_opt_set(encoder->priv_data, "preset", "ultrafast", 0) == 0);
    CHECK(av_opt_set(encoder->priv_data, "tune", "zerolatency", 0) == 0);
    CHECK(avcodec_open2(encoder.get(), codec, nullptr) == 0);
    frame_ptr frame(av_frame_alloc());
    CHECK(frame);
    frame->format = encoder->pix_fmt;
    frame->width = encoder->width;
    frame->height = encoder->height;
    CHECK(av_frame_get_buffer(frame.get(), 32) == 0);
    std::vector<packet_ptr> packets;
    auto drain = [&] {
        while (true) {
            packet_ptr packet(av_packet_alloc());
            CHECK(packet);
            const int result = avcodec_receive_packet(encoder.get(), packet.get());
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) break;
            CHECK(result == 0);
            packets.push_back(std::move(packet));
        }
    };
    for (int index = 0; index < fps * 2; ++index) {
        CHECK(av_frame_make_writable(frame.get()) == 0);
        for (int plane = 0; plane < 3; ++plane) {
            const int width = plane == 0 ? frame->width : frame->width / 2;
            const int height = plane == 0 ? frame->height : frame->height / 2;
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                    frame->data[plane][y * frame->linesize[plane] + x] =
                        static_cast<uint8_t>(plane == 0 ? 32 + (x + y + index) % 180 : 128);
        }
        frame->pts = index;
        CHECK(avcodec_send_frame(encoder.get(), frame.get()) == 0);
        drain();
    }
    CHECK(avcodec_send_frame(encoder.get(), nullptr) == 0);
    drain();
    CHECK(packets.size() == static_cast<size_t>(fps * 2));
    CHECK((packets[0]->flags & AV_PKT_FLAG_KEY) != 0);
    CHECK((packets[fps]->flags & AV_PKT_FLAG_KEY) != 0);
    const auto* decoder_codec = avcodec_find_decoder(AV_CODEC_ID_H264);
    CHECK(decoder_codec);
    context_ptr decoder(avcodec_alloc_context3(decoder_codec));
    CHECK(decoder);
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
            CHECK(decoded->width == 64 && decoded->height == 48);
            CHECK(decoded->pts == count);
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

int main() {
    CHECK(avcodec_version() == LIBAVCODEC_VERSION_INT);
    CHECK(avformat_version() == LIBAVFORMAT_VERSION_INT);
    CHECK(avutil_version() == LIBAVUTIL_VERSION_INT);
    CHECK(swresample_version() == LIBSWRESAMPLE_VERSION_INT);
    CHECK(swscale_version() == LIBSWSCALE_VERSION_INT);
    av_log_set_level(AV_LOG_ERROR);
    for (const int fps : { 30, 60, 90, 120 }) video_round_trip(fps);
    resample_silence();
    std::cout << "Pinned FFmpeg SDK: H.264 encode/decode and PCM resampling passed\n";
}
