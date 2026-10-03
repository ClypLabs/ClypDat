#include "recording_audio.h"
#include "recording_audio_codec.h"
#include "recording_audio_timing.h"
#include <chrono>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <mutex>
#include <stdexcept>

using namespace clypdat;
namespace {
void require(bool value, const char* message) { if (!value) throw std::runtime_error(message); }
void jitter_history() {
    AudioLaneMixer mixer({{"mic", "Microphone", 1, 1, false}}, 960, 480000);
    mixer.anchor(0);
    for (int packet = 0; packet < 600; ++packet) {
        PcmBlock block;
        block.lane = block.source = "mic";
        block.channels = 1;
        block.start_us = packet * 10000 + (packet == 0 ? 0 : packet % 2 ? 1 : -1);
        for (int i = 0; i < 480; ++i) block.samples.push_back(.2f + float(packet * 480 + i) / 1000000);
        mixer.submit(block, audio_frames(block.start_us));
    }
    mixer.flush();
    for (int frame = 0; frame < 288000; frame += 960) {
        auto samples = mixer.take()[0].samples;
        for (int i = 0; i < 960; ++i)
            require(std::abs(samples[size_t(i)] - (.2f + float(frame + i) / 1000000)) < .0000001f,
                "Continuous PCM contains an inserted zero or missing sample under +/-1 us jitter");
    }
}
PcmBlock packet(int rate, int channels, int frames, int64_t time) {
    PcmBlock block;
    block.lane = block.source = "mic"; block.generation = 1;
    block.sample_rate = rate; block.channels = channels; block.start_us = time;
    block.samples.resize(size_t(frames) * channels);
    for (int i = 0; i < frames; ++i)
        for (int ch = 0; ch < channels; ++ch) block.samples[size_t(i) * channels + ch] = .25f + .125f * ch;
    return block;
}
void clock_test(int rate, int channels, int ppm) {
    constexpr int64_t origin = 9876543210123; // Products with rate exceed int64.
    const auto first = audio_frames(origin);
    int64_t end = first;
    RecordingAudioTiming timing([&](PcmBlock block) {
        require(block.sample_rate == 48000 && block.channels == channels, "Timing changed channel format");
        require(audio_frames(block.start_us) == end, "Timing inserted a hole or overlap");
        end += block.samples.size() / channels;
        for (size_t i = 0; i < block.samples.size(); ++i)
            require(std::abs(block.samples[i] - (.25f + .125f * (i % channels))) < .00001f,
                "Timing inserted silence or corrupted a channel");
    });
    const double clock_rate = rate * (1 + ppm / 1000000.0);
    int64_t input = 0;
    double maximum_error = 0;
    int index = 0;
    while (input < int64_t(rate) * 120) {
        const int frames = (std::min)(int64_t(127 + index % 7 * 79), int64_t(rate) * 120 - input);
        const auto time = origin + int64_t(std::llround(input * 1000000.0 / clock_rate)) + (index % 2 ? 1 : -1);
        timing.submit(packet(rate, channels, frames, time), uint64_t(input));
        input += frames; ++index;
        maximum_error = (std::max)(maximum_error, std::abs((end - first) / 48000.0 - input / clock_rate));
    }
    timing.finish(); timing.finish();
    maximum_error = (std::max)(maximum_error, std::abs((end - first) / 48000.0 - input / clock_rate));
    std::cout << rate << " Hz " << channels << " channels " << ppm << " ppm: " << maximum_error * 1000 << " ms\n";
    require(maximum_error < .002, "Synthetic clock error exceeds 2 ms");
    if (!ppm) require(end - first == 5760000, "Resampler drain lost continuous frames");
}
void interruptions() {
    for (int gap : {240, 2400, 48000, 96000}) {
        std::vector<PcmBlock> output;
        RecordingAudioTiming timing([&](PcmBlock block) { output.push_back(std::move(block)); });
        timing.submit(packet(48000, 2, 4800, 0), 0);
        timing.submit(packet(48000, 2, 4800, audio_time(4800 + gap)), 4800 + gap);
        timing.finish();
        int64_t end = 0, count = 0; bool hole = false, silence = false;
        for (const auto& block : output) {
            auto start = audio_frames(block.start_us);
            require(start >= end, "Gap handling overlaps previous output");
            hole |= start > end;
            require(block.samples.size() / 2 < 8192, "Gap handling allocated an unbounded chunk");
            for (auto value : block.samples) silence |= value == 0;
            count += block.samples.size() / 2; end = start + block.samples.size() / 2;
        }
        require(end == 9600 + gap, "Gap endpoint moved");
        require(gap > 48000 ? hole && count == 9600 : !hole && silence && count == 9600 + gap,
            "Real gap was collapsed or large gap was materialized");
    }
    // Invalid QPC/device pairs must not reset continuous input or steer drift.
    int64_t end = 0;
    RecordingAudioTiming invalid([&](PcmBlock block) {
        require(audio_frames(block.start_us) == end, "Invalid timestamp disturbed continuity");
        end += block.samples.size();
    });
    for (int i = 0; i < 100; ++i) {
        bool valid = i % 3 != 1;
        invalid.submit(packet(48000, 1, 480, valid ? i * 10000 : -999999999), valid ? i * 480 : UINT64_MAX, valid);
    }
    invalid.finish(); require(end == 48000, "Invalid timestamp lost samples");

    end = 0;
    RecordingAudioTiming reset([&](PcmBlock block) {
        require(audio_frames(block.start_us) == end, "Backward reset was not overlap-trimmed");
        end += block.samples.size();
    });
    reset.submit(packet(48000, 1, 4800, 0), 10000);
    reset.submit(packet(48000, 1, 4800, 50000), 0);
    reset.finish(); require(end == 7200, "Backward reset duplicated or lost its tail");
}
void suppression(bool enabled, int rate, int channels) {
    AudioLaneMixer mixer({{"mic", "Microphone", channels, 1, false}}, 960, 480000);
    mixer.anchor(0);
    int64_t end = 0;
    bool filtered = false;
    auto sink = [&](PcmBlock block) {
        require(audio_frames(block.start_us) == end, "Suppression broke timing continuity");
        end += block.samples.size() / channels;
        for (size_t i = 0; i < block.samples.size(); ++i) {
            require(std::isfinite(block.samples[i]), "Suppression produced invalid PCM");
            filtered |= std::abs(block.samples[i] - (.25f + .125f * (i % channels))) > .001f;
        }
        mixer.submit(block, audio_frames(block.start_us));
    };
    auto vendor = std::filesystem::path(__FILE__).parent_path().parent_path().parent_path() / "vendor";
    std::unique_ptr<RecordingMicrophoneFilter> filter;
    if (enabled) filter = std::make_unique<RecordingMicrophoneFilter>(vendor / "ffmpeg/ffmpeg.exe", vendor / "rnnoise/lq.rnnn", -45, sink);
    RecordingAudioTiming timing([&](PcmBlock block) { if (filter) filter->submit(std::move(block)); else sink(std::move(block)); });
    int position = 0;
    while (position < rate) {
        int count = (std::min)(137, rate - position);
        timing.submit(packet(rate, channels, count, audio_time(position, rate)), position);
        position += count;
    }
    timing.finish();
    if (filter) require(filter->stop(), "Suppression failed to drain");
    require(filtered == enabled, "Suppression test silently fell back or changed unfiltered PCM");
    require(end == 48000, "Suppression drain lost timing output");
    mixer.flush();
    require(mixer.converted_frames() == 48000, "Mixer lost suppressed frames");
}
}
int main() {
    auto root = std::filesystem::current_path() / ("audio-timing-fixture-" +
        std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
    bool owned = false;
    try {
        owned = std::filesystem::create_directory(root);
        require(owned, "Timing fixture already exists");
        jitter_history();
        for (int rate : {44100, 48000}) for (int channels : {1, 2}) {
            for (int ppm : {-500, 0, 500}) clock_test(rate, channels, ppm);
            for (bool enabled : {false, true}) suppression(enabled, rate, channels);
        }
        interruptions();
        std::filesystem::remove_all(root);
        std::cout << "Native audio timing tests passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << error.what() << '\n';
        if (owned) { std::error_code ignored; std::filesystem::remove_all(root, ignored); }
        return 1;
    }
}
