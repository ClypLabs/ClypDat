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

int main() {
    av_log_set_level(AV_LOG_ERROR);
    retries_same_frame(false);
    retries_same_frame(true);
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
