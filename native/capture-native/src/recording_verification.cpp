#include "recording_verification.h"
#include "recorder_session.h"
#include <Windows.h>
#include <psapi.h>
#include <cmath>
#include <thread>
#include <sstream>
extern "C" {
#include <libavformat/avformat.h>
}
namespace clypdat {
namespace {
void check(bool ok, const char* message) { if (!ok) throw std::runtime_error(message); }
class GeneratedFrames final : public RecordingFrameSource {
    int fps_, index_ = 0;
    std::chrono::steady_clock::time_point next_ = std::chrono::steady_clock::now();
public:
    explicit GeneratedFrames(int fps) : fps_(fps) {}
    bool acquire(CapturePixels& value, std::chrono::milliseconds timeout) override {
        const auto now = std::chrono::steady_clock::now();
        if (now < next_) {
            std::this_thread::sleep_for(std::min(timeout,
                std::chrono::duration_cast<std::chrono::milliseconds>(next_ - now) + std::chrono::milliseconds(1)));
            return false;
        }
        next_ += std::chrono::microseconds(1000000 / fps_);
        value.width = 128; value.height = 72; value.stride = 512;
        value.bgra.resize(128 * 72 * 4);
        for (int y = 0; y < 72; ++y) for (int x = 0; x < 128; ++x) {
            auto* pixel = &value.bgra[(y * 128 + x) * 4];
            pixel[0] = static_cast<uint8_t>(x + index_); pixel[1] = static_cast<uint8_t>(y + index_);
            pixel[2] = static_cast<uint8_t>(x + y + index_); pixel[3] = 255;
        }
        ++index_; return true;
    }
    bool eligible() const override { return true; }
    const char* name() const override { return "Native generated verification source"; }
};
uint64_t filetime(const FILETIME& t) { return (uint64_t(t.dwHighDateTime) << 32) | t.dwLowDateTime; }
uint64_t cpu_time() {
    FILETIME created{}, exited{}, kernel{}, user{};
    check(GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user) != 0, "Read process CPU times");
    return filetime(kernel) + filetime(user);
}
void verify_media(const std::filesystem::path& path) {
    AVFormatContext* format = nullptr;
    const auto encoded_path = path.u8string();
    check(avformat_open_input(&format, reinterpret_cast<const char*>(encoded_path.c_str()), nullptr, nullptr) >= 0, "Open native recording output");
    struct Close { AVFormatContext*& value; ~Close() { avformat_close_input(&value); } } close{format};
    check(avformat_find_stream_info(format, nullptr) >= 0, "Probe native recording output");
    check(format->duration > 0, "Native recording has no duration");
    check(format->nb_streams == 2, "Native recording must contain video and generated audio");
    std::vector<CodecContext> decoders;
    std::vector<int> counts(format->nb_streams);
    for (unsigned i = 0; i < format->nb_streams; ++i) {
        const auto* codec = avcodec_find_decoder(format->streams[i]->codecpar->codec_id);
        check(codec != nullptr, "Native recording decoder unavailable");
        CodecContext decoder(avcodec_alloc_context3(codec));
        check(decoder != nullptr, "Allocate native recording decoder");
        check(avcodec_parameters_to_context(decoder.get(), format->streams[i]->codecpar) >= 0, "Copy decoder parameters");
        check(avcodec_open2(decoder.get(), codec, nullptr) >= 0, "Open native recording decoder");
        decoders.push_back(std::move(decoder));
    }
    Packet packet(av_packet_alloc()); AVFrame* frame = av_frame_alloc();
    check(packet && frame, "Allocate native verification frames");
    struct FreeFrame { AVFrame*& value; ~FreeFrame() { av_frame_free(&value); } } free_frame{frame};
    auto receive = [&](unsigned index) {
        for (;;) {
            const auto code = avcodec_receive_frame(decoders[index].get(), frame);
            if (code == AVERROR(EAGAIN) || code == AVERROR_EOF) return;
            check(code >= 0, "Decode native recording frame"); ++counts[index]; av_frame_unref(frame);
        }
    };
    while (av_read_frame(format, packet.get()) >= 0) {
        const auto index = packet->stream_index;
        check(avcodec_send_packet(decoders[index].get(), packet.get()) >= 0, "Submit native recording packet");
        receive(index); av_packet_unref(packet.get());
    }
    for (unsigned i = 0; i < decoders.size(); ++i) {
        check(avcodec_send_packet(decoders[i].get(), nullptr) >= 0, "Flush verification decoder"); receive(i);
        check(counts[i] > 0, "Native recording stream is empty");
    }
    check(av_seek_frame(format, -1, format->duration / 2, AVSEEK_FLAG_BACKWARD) >= 0, "Seek native recording output");
    check(av_read_frame(format, packet.get()) >= 0, "Read native recording after seek");
}
}
std::string verify_recording(const std::filesystem::path& directory,
    const std::filesystem::path& ffmpeg, int fps, bool variable, int seconds) {
    check(fps >= 30 && fps <= 120 && seconds >= 1 && seconds <= 10, "Invalid recording fixture");
    std::filesystem::create_directories(directory);
    RecorderSessionConfig config;
    config.capture.width = 128; config.capture.height = 72; config.capture.fps = fps;
    config.capture.bitrate_mbps = 5; config.capture.cpu_encoder = true;
    config.capture.variable_frame_rate = variable;
    LARGE_INTEGER counter{}, frequency{}; QueryPerformanceCounter(&counter); QueryPerformanceFrequency(&frequency);
    config.capture.qpc_anchor = counter.QuadPart; config.capture.qpc_frequency = frequency.QuadPart;
    config.capture.monotonic_anchor_us = static_cast<int64_t>(counter.QuadPart * (1000000.L / frequency.QuadPart));
    const auto start_us = config.capture.monotonic_anchor_us;
    config.work_directory = directory / L"work"; config.ffmpeg = ffmpeg;
    config.capture_input = false; config.audio_lanes = {{"game", "Game Audio", 2, 1, false}};
    const auto start = std::chrono::steady_clock::now(); const auto cpu = cpu_time();
    RecorderSession recorder(config, std::make_unique<GeneratedFrames>(fps));
    for (uint64_t revision = 1; revision <= 100; ++revision) {
        OverlaySettingsNative settings; settings.revision = revision; settings.at_us = start_us;
        recorder.overlay_settings(std::move(settings));
    }
    const auto changed = recorder.events(0, 0);
    check(changed.sequence >= 100 && (changed.mask & 32) != 0, "Native settings events were lost");
    check(recorder.events(changed.sequence, 0).mask == 0, "Native replaceable events did not coalesce");
    recorder.start();
    // Native PCM traverses the production disk history and snapshot barrier.
    PcmBlock audio; audio.lane = "game"; audio.source = "generated"; audio.generation = 1;
    audio.start_us = start_us; audio.sample_rate = 44100; audio.channels = 2;
    audio.samples.resize(size_t(seconds + 1) * audio.sample_rate * audio.channels);
    for (size_t i = 0; i < audio.samples.size() / 2; ++i) {
        const auto sample = .1f * static_cast<float>(std::sin(2 * 3.141592653589793 * 440 * double(i) / audio.sample_rate));
        audio.samples[i * 2] = sample; audio.samples[i * 2 + 1] = sample;
    }
    recorder.submit_pcm(std::move(audio));
    const auto deadline = start + std::chrono::seconds(seconds + 5);
    while (recorder.health().encoded < uint64_t(fps * seconds)) {
        check(recorder.health().error.empty(), recorder.health().error.c_str());
        check(std::chrono::steady_clock::now() < deadline, "Generated recording timed out");
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    recorder.pause(true);
    QueryPerformanceCounter(&counter);
    const auto end_us = static_cast<int64_t>(counter.QuadPart * (1000000.L / frequency.QuadPart));
    const auto path = directory / L"native-recording.mp4";
    check(!std::filesystem::exists(path), "Verification output already exists");
    const auto save_start = std::chrono::steady_clock::now();
    recorder.save("generated-verification", start_us, end_us, path);
    auto saved = recorder.saved("generated-verification"); check(saved.has_value(), "Accepted save missing");
    check(recorder.stop(), "Native recording shutdown requires restart");
    check(saved->completion.wait_for(std::chrono::seconds(30)) == std::future_status::ready, "Native save timed out");
    const auto result = saved->completion.get(); check(result.error.empty(), result.error.c_str());
    check((recorder.events(changed.sequence, 0).mask & 8) != 0, "Native terminal save event missing");
    check(result.duration_us > 0 && !result.cancelled, "Native save produced no media");
    const auto save_ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - save_start).count();
    check(recorder.saved("generated-verification").has_value(), "Save result lost across stop");
    check(recorder.release_save("generated-verification"), "Terminal save result could not be released");
    verify_media(path);
    PROCESS_MEMORY_COUNTERS_EX memory{}; memory.cb = sizeof(memory);
    GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory));
    const auto health = recorder.health();
    std::ostringstream report;
    report << "{\"fps\":" << fps << ",\"variable\":" << (variable ? "true" : "false")
        << ",\"durationUs\":" << result.duration_us << ",\"encoded\":" << health.encoded
        << ",\"replaced\":" << health.replaced << ",\"cpuMs\":" << (cpu_time() - cpu) / 10000.0
        << ",\"workingSet\":" << memory.WorkingSetSize << ",\"privateBytes\":" << memory.PrivateUsage
        << ",\"saveMs\":" << save_ms << ",\"decoded\":true,\"seeked\":true}";
    return report.str();
}
}
