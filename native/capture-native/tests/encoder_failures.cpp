#include "video_encoder.h"
#include <cstdlib>
#include <iostream>
#include <stdexcept>

#define CHECK(expression) do { if (!(expression)) { \
    std::cerr << __FILE__ << ':' << __LINE__ << ": " #expression " failed\n"; \
    std::exit(EXIT_FAILURE); } } while (false)

template<class Action> void must_throw(Action action) {
    bool threw = false;
    try { action(); } catch (const std::exception&) { threw = true; }
    CHECK(threw);
}

void retries_same_frame(bool flush) {
    AVFrame frame{};
    frame.width = 64;
    frame.height = 48;
    frame.format = AV_PIX_FMT_NV12;
    frame.pts = 1234567;
    int sends = 0;
    int receives = 0;
    clypdat::CodecCalls calls;
    calls.send = [&](AVCodecContext*, const AVFrame* submitted) {
        CHECK(submitted == (flush ? nullptr : &frame));
        CHECK(frame.pts == 1234567);
        return ++sends == 1 ? AVERROR(EAGAIN) : 0;
    };
    calls.receive = [&](AVCodecContext*, AVPacket* packet) {
        switch (++receives) {
        case 1: packet->pts = 1; return 0;
        case 2: return AVERROR(EAGAIN);
        case 3: packet->pts = 2; return 0;
        default: return flush ? AVERROR_EOF : AVERROR(EAGAIN);
        }
    };
    clypdat::VideoEncoder encoder({ 64, 48, 60, 5 }, calls);
    auto packets = flush ? encoder.finish() : encoder.submit(frame);
    CHECK(sends == 2 && receives == 4 && packets.size() == 2);
    CHECK(packets[0]->pts == 1 && packets[1]->pts == 2);
    if (flush) {
        CHECK(encoder.finish().empty());
        must_throw([&] { encoder.submit(frame); });
        CHECK(sends == 2);
    }
}

// EAGAIN that draining cannot clear is backpressure: the frame is refused,
// the encoder stays open, and it accepts input once it has room again.
void busy_is_not_failure() {
    AVFrame frame{};
    frame.width = 64;
    frame.height = 48;
    frame.format = AV_PIX_FMT_NV12;
    bool busy = true, flushing = false;
    int sends = 0, pending = 0;
    clypdat::CodecCalls calls;
    calls.send = [&](AVCodecContext*, const AVFrame* submitted) {
        ++sends;
        if (!submitted) { flushing = true; return 0; }
        return busy ? AVERROR(EAGAIN) : 0;
    };
    calls.receive = [&](AVCodecContext*, AVPacket* packet) {
        if (pending) { --pending; packet->pts = 7; return 0; }
        return flushing ? AVERROR_EOF : AVERROR(EAGAIN);
    };
    clypdat::VideoEncoder encoder({ 64, 48, 60, 5 }, calls);
    auto refused = encoder.try_submit(frame);
    CHECK(refused.status == clypdat::SubmitStatus::Busy && refused.packets.empty() && sends == 1);
    CHECK(encoder.drain_ready().empty());
    // Output drained but still no room: Busy, and the drained packet is kept.
    pending = 1;
    auto progressed = encoder.try_submit(frame);
    CHECK(progressed.status == clypdat::SubmitStatus::Busy && progressed.packets.size() == 1 && sends == 3);
    busy = false;
    auto accepted = encoder.try_submit(frame);
    CHECK(accepted.status == clypdat::SubmitStatus::Accepted && sends == 4);
    CHECK(encoder.finish().empty() && flushing);
    // Receive errors while draining still close the encoder.
    clypdat::CodecCalls failing;
    failing.send = [](AVCodecContext*, const AVFrame*) { return 0; };
    failing.receive = [](AVCodecContext*, AVPacket*) { return AVERROR(EIO); };
    clypdat::VideoEncoder broken({ 64, 48, 60, 5 }, failing);
    must_throw([&] { broken.drain_ready(); });
    must_throw([&] { broken.try_submit(frame); });
}

// QSV allocates every packet at its VBV size and only shrinks `size`. With
// right-sizing only an exact-size copy leaves the encoder, on submission and
// on drain, with timing, flags and side data intact.
void right_sized_packets() {
    constexpr int vbv = 3125000, payload = 35000;
    AVFrame frame{};
    frame.width = 64;
    frame.height = 48;
    frame.format = AV_PIX_FMT_NV12;
    for (const bool right_size : { true, false }) {
        int pending = 0, produced = 0;
        bool flushing = false;
        clypdat::CodecCalls calls;
        calls.send = [&](AVCodecContext*, const AVFrame* submitted) {
            if (submitted) ++pending; else flushing = true;
            return 0;
        };
        calls.receive = [&](AVCodecContext*, AVPacket* packet) {
            if (!pending) return flushing ? AVERROR_EOF : AVERROR(EAGAIN);
            --pending;
            if (av_new_packet(packet, vbv) < 0) return AVERROR(ENOMEM);
            for (int i = 0; i < payload; ++i) packet->data[i] = uint8_t(i * 7 + produced);
            packet->size = payload;
            packet->pts = 9000 + produced; packet->dts = 8000 + produced; packet->duration = 16667;
            packet->flags = produced ? 0 : AV_PKT_FLAG_KEY; packet->time_base = { 1, 1000000 };
            auto* stats = av_packet_new_side_data(packet, AV_PKT_DATA_QUALITY_STATS, 8);
            if (!stats) return AVERROR(ENOMEM);
            for (int i = 0; i < 8; ++i) stats[i] = uint8_t(i + produced);
            ++produced;
            return 0;
        };
        clypdat::VideoEncoderConfig config{ 64, 48, 60, 5 };
        config.right_size_packets = right_size;
        clypdat::VideoEncoder encoder(config, calls);
        auto packets = encoder.submit(frame);
        CHECK(packets.size() == 1);
        ++pending; // One more packet surfaces only while draining.
        for (auto& packet : encoder.finish()) packets.push_back(std::move(packet));
        CHECK(packets.size() == 2);
        for (int index = 0; index < 2; ++index) {
            const auto& packet = packets[size_t(index)];
            CHECK(packet->size == payload && packet->buf && packet->data == packet->buf->data);
            CHECK(packet->buf->size == size_t(right_size ? payload : vbv) + AV_INPUT_BUFFER_PADDING_SIZE);
            for (int i = 0; i < payload; ++i) CHECK(packet->data[i] == uint8_t(i * 7 + index));
            CHECK(packet->pts == 9000 + index && packet->dts == 8000 + index && packet->duration == 16667);
            CHECK(bool(packet->flags & AV_PKT_FLAG_KEY) == (index == 0));
            CHECK(packet->time_base.num == 1 && packet->time_base.den == 1000000);
            size_t size = 0;
            const auto* stats = av_packet_get_side_data(packet.get(), AV_PKT_DATA_QUALITY_STATS, &size);
            CHECK(stats && size == 8);
            for (int i = 0; i < 8; ++i) CHECK(stats[i] == uint8_t(i + index));
        }
    }
}

int main() {
    av_log_set_level(AV_LOG_ERROR);
    retries_same_frame(false);
    retries_same_frame(true);
    busy_is_not_failure();
    right_sized_packets();
    AVFrame frame{};
    frame.width = 64;
    frame.height = 48;
    frame.format = AV_PIX_FMT_NV12;
    for (const int send_error : { AVERROR(EAGAIN), AVERROR(EIO) }) {
        int sends = 0;
        clypdat::CodecCalls calls;
        calls.send = [&](AVCodecContext*, const AVFrame*) { ++sends; return send_error; };
        calls.receive = [](AVCodecContext*, AVPacket*) { return AVERROR(EAGAIN); };
        clypdat::VideoEncoder encoder({ 64, 48, 60, 5 }, calls);
        must_throw([&] { encoder.submit(frame); });
        must_throw([&] { encoder.submit(frame); });
        must_throw([&] { encoder.finish(); });
        CHECK(sends == 1); // No spin, no reuse of a failed generation.
    }
    for (const int receive_error : { AVERROR(EAGAIN), AVERROR(EIO) }) {
        clypdat::CodecCalls calls;
        calls.send = [](AVCodecContext*, const AVFrame*) { return 0; };
        calls.receive = [=](AVCodecContext*, AVPacket*) { return receive_error; };
        clypdat::VideoEncoder encoder({ 64, 48, 60, 5 }, calls);
        must_throw([&] { encoder.finish(); });
        must_throw([&] { encoder.finish(); });
    }
    must_throw([] { clypdat::VideoEncoder encoder({ 63, 48, 60, 5 }); });
    must_throw([] { clypdat::VideoEncoder encoder({ 64, 48, 121, 5 }); });
    std::cout << "Production encoder retry and failure checks passed\n";
}
