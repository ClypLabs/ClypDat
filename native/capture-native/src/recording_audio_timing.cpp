#include "recording_audio_timing.h"
#include <algorithm>
#include <climits>
#include <limits>
#include <stdexcept>
extern "C" {
#include <libavutil/mathematics.h>
#include <libavutil/opt.h>
#include <libswresample/swresample.h>
}

namespace clypdat {
int64_t audio_frames(int64_t us, int rate) { return av_rescale(us, rate, 1000000); }
int64_t audio_time(int64_t frames, int rate) { return av_rescale(frames, 1000000, rate); }
int64_t audio_offset_time(int64_t us, int64_t frames, int rate) {
    return audio_time(audio_frames(us, rate) + frames, rate);
}
namespace {
void check(int result, const char* operation) {
    if (result < 0) throw std::runtime_error(operation);
}
}
struct RecordingAudioTiming::State {
    WasapiSource::Sink sink;
    SwrContext* swr = nullptr;
    PcmBlock identity;
    uint64_t expected = 0;
    int64_t origin_us = 0, output_frame = 0;
    int64_t delivered_end = std::numeric_limits<int64_t>::min();
    bool finished = false;
    ~State() { swr_free(&swr); }

    void begin(const PcmBlock& packet) {
        identity.lane = packet.lane; identity.source = packet.source;
        identity.generation = packet.generation;
        identity.sample_rate = packet.sample_rate; identity.channels = packet.channels;
        origin_us = packet.start_us;
        output_frame = audio_frames(origin_us);
        AVChannelLayout layout{};
        av_channel_layout_default(&layout, packet.channels);
        const int result = swr_alloc_set_opts2(&swr, &layout, AV_SAMPLE_FMT_FLT, 48000,
            &layout, AV_SAMPLE_FMT_FLT, packet.sample_rate, 0, nullptr);
        av_channel_layout_uninit(&layout);
        check(result, "Allocate capture timing resampler");
        check(av_opt_set_double(swr, "min_comp", .001, 0), "Set clock compensation threshold");
        check(av_opt_set_double(swr, "comp_duration", 1, 0), "Set clock compensation duration");
        check(av_opt_set_double(swr, "max_soft_comp", .001, 0), "Set clock compensation limit");
        check(av_opt_set_double(swr, "min_hard_comp", 1000000, 0), "Disable hard clock compensation");
        check(av_opt_set_int(swr, "first_pts", 0, 0), "Anchor capture resampler");
        check(av_opt_set_int(swr, "flags", SWR_FLAG_RESAMPLE, 0), "Enable capture resampling");
        check(swr_init(swr), "Initialize capture timing resampler");
    }
    int convert(const float* samples, int frames) {
        const int capacity = swr_get_out_samples(swr, frames);
        check(capacity, "Measure capture timing output");
        PcmBlock block = identity;
        block.sample_rate = 48000;
        block.samples.resize(size_t(capacity) * block.channels);
        const auto* input = reinterpret_cast<const uint8_t*>(samples);
        auto* output = reinterpret_cast<uint8_t*>(block.samples.data());
        const int count = swr_convert(swr, &output, capacity, samples ? &input : nullptr, frames);
        check(count, "Convert capture timing audio");
        // A restarted device can repeat part of the previous span. Drain the
        // old span first, then discard only the overlapping replacement frames.
        const int64_t skip = delivered_end > output_frame
            ? (std::min)(int64_t(count), delivered_end - output_frame) : 0;
        block.start_us = audio_time(output_frame + skip);
        output_frame += count;
        if (count > skip) {
            block.samples.resize(size_t(count) * block.channels);
            block.samples.erase(block.samples.begin(), block.samples.begin() + skip * block.channels);
            delivered_end = output_frame;
            sink(std::move(block));
        }
        return count;
    }
    void drain() {
        if (!swr) return;
        while (convert(nullptr, 0) > 0) {}
        swr_free(&swr);
    }
    void submit(PcmBlock packet, uint64_t position, bool valid) {
        if (finished) throw std::logic_error("Capture timing already finished");
        if (packet.channels < 1 || packet.channels > 32 || packet.sample_rate < 8000 ||
            packet.sample_rate > 384000 || packet.samples.size() % packet.channels)
            throw std::invalid_argument("Invalid capture timing PCM");
        const auto frames = packet.samples.size() / packet.channels;
        if (!frames) return;
        // TIMESTAMP_ERROR invalidates the device/QPC pair. Keep the current
        // span and compensation until a trustworthy pair becomes available.
        if (!valid) position = swr ? expected : 0;
        if (frames > size_t(INT_MAX) || position > UINT64_MAX - frames)
            throw std::invalid_argument("Capture packet position overflow");
        const bool restart = swr && (packet.sample_rate != identity.sample_rate ||
            packet.channels != identity.channels || packet.generation != identity.generation ||
            packet.source != identity.source || packet.lane != identity.lane ||
            position < expected || position - expected > uint64_t(packet.sample_rate));
        if (restart) drain();
        if (!swr) { begin(packet); expected = position; }
        if (position > expected) {
            // Genuine short capture gaps pass through suppression as silence.
            // Never allocate in proportion to an untrusted device jump.
            std::vector<float> silence(size_t(4096) * packet.channels, 0);
            while (expected < position) {
                const int count = int((std::min)(position - expected, uint64_t(4096)));
                convert(silence.data(), count);
                expected += count;
            }
        }
        if (valid) {
            // swr_next_pts takes units of 1/(input_rate * output_rate).
            const auto pts = av_rescale(packet.start_us - origin_us,
                int64_t(packet.sample_rate) * 48000, 1000000);
            swr_next_pts(swr, pts);
        }
        // Also bound large input packets, so reset/drain never needs a large
        // temporary output allocation.
        for (size_t offset = 0; offset < frames;) {
            const int count = int((std::min)(frames - offset, size_t(4096)));
            convert(packet.samples.data() + offset * packet.channels, count);
            offset += count;
        }
        expected = position + frames;
    }
};
RecordingAudioTiming::RecordingAudioTiming(WasapiSource::Sink sink) : state_(std::make_unique<State>()) {
    state_->sink = std::move(sink);
}
RecordingAudioTiming::~RecordingAudioTiming() = default;
void RecordingAudioTiming::submit(PcmBlock packet, uint64_t position, bool valid) {
    state_->submit(std::move(packet), position, valid);
}
void RecordingAudioTiming::finish() {
    if (state_->finished) return;
    state_->drain();
    state_->finished = true;
}
}
