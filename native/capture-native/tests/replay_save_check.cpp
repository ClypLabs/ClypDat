// Manual: records the primary monitor through the production RecorderSession
// (WGC, 2560x1440 at 90 FPS VFR, 25 Mbps NVENC) with a generated audio lane,
// keeps the replay history for its full length, saves all of it, and checks
// the clip: first packet a keyframe, every frame decodes, a mid-clip seek
// decodes, and audio and video start and end together.
//
// replay_save_check <history seconds> <work directory> <ffmpeg.exe> [--reference] [--hdr]
// --reference prunes the history with the original full scan; --hdr asks for
// HDR conversion as the app does (FP16 capture on an HDR display).
// Build with CLYPDAT_BASELINE for revisions without VideoHistory::stats.
#include "recorder_session.h"
#include <Windows.h>
#include <intrin.h>
#include <tlhelp32.h>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/log.h>
}
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <thread>
#include <vector>

using namespace clypdat;
using namespace std::chrono_literals;

namespace {
void check(bool condition, const std::string& message) { if (!condition) throw std::runtime_error(message); }
int64_t monotonic_us() { LARGE_INTEGER counter{}, frequency{}; QueryPerformanceCounter(&counter); QueryPerformanceFrequency(&frequency); return int64_t(counter.QuadPart * (1000000.L / frequency.QuadPart)); }
// Cycles run by the capture's encoding thread, which appends every packet to
// the replay history.
uint64_t encoding_cycles() {
    const HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0); THREADENTRY32 entry{sizeof(entry)}; uint64_t cycles = 0;
    for (BOOL more = Thread32First(snapshot, &entry); more; more = Thread32Next(snapshot, &entry)) {
        if (entry.th32OwnerProcessID != GetCurrentProcessId()) continue;
        const HANDLE thread = OpenThread(THREAD_QUERY_LIMITED_INFORMATION, FALSE, entry.th32ThreadID); if (!thread) continue;
        PWSTR name = nullptr;
        if (SUCCEEDED(GetThreadDescription(thread, &name)) && name) {
            if (std::wstring(name) == L"ClypDat capture encoding") { ULONG64 value = 0; QueryThreadCycleTime(thread, &value); cycles = value; }
            LocalFree(name);
        }
        CloseHandle(thread);
    }
    CloseHandle(snapshot); return cycles;
}
double tsc_hz() {
    LARGE_INTEGER frequency{}, start{}, end{}; QueryPerformanceFrequency(&frequency); QueryPerformanceCounter(&start);
    const auto cycles = __rdtsc(); std::this_thread::sleep_for(200ms); QueryPerformanceCounter(&end);
    return double(__rdtsc() - cycles) / (double(end.QuadPart - start.QuadPart) / frequency.QuadPart);
}
double percentile(std::vector<double> values, double rank) { if (values.empty()) return 0; std::sort(values.begin(), values.end()); return values[std::min(values.size() - 1, size_t(rank * values.size()))]; }

struct Clip { double video_start = 0, video_end = 0, audio_start = 0, audio_end = 0; int64_t frames = 0, audio_frames = 0; bool key_first = false, seek = false; };
Clip inspect(const std::filesystem::path& path) {
    Clip clip; AVFormatContext* format = nullptr; const auto name = path.u8string();
    check(avformat_open_input(&format, reinterpret_cast<const char*>(name.c_str()), nullptr, nullptr) >= 0, "Open saved clip");
    struct Close { AVFormatContext*& value; ~Close() { avformat_close_input(&value); } } close{format};
    check(avformat_find_stream_info(format, nullptr) >= 0, "Probe saved clip");
    const int video = av_find_best_stream(format, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0), audio = av_find_best_stream(format, AVMEDIA_TYPE_AUDIO, -1, -1, nullptr, 0);
    check(video >= 0 && audio >= 0, "Saved clip needs video and audio");
    std::vector<AVCodecContext*> decoders(format->nb_streams, nullptr);
    struct Free { std::vector<AVCodecContext*>& value; ~Free() { for (auto*& d : value) avcodec_free_context(&d); } } free_decoders{decoders};
    for (const int index : {video, audio}) {
        const auto* codec = avcodec_find_decoder(format->streams[index]->codecpar->codec_id); check(codec != nullptr, "No decoder");
        decoders[index] = avcodec_alloc_context3(codec);
        check(avcodec_parameters_to_context(decoders[index], format->streams[index]->codecpar) >= 0 && avcodec_open2(decoders[index], codec, nullptr) >= 0, "Open decoder");
    }
    AVPacket* packet = av_packet_alloc(); AVFrame* frame = av_frame_alloc();
    struct FreeAll { AVPacket*& p; AVFrame*& f; ~FreeAll() { av_packet_free(&p); av_frame_free(&f); } } free_all{packet, frame};
    auto seconds = [&](int index, int64_t value) { return double(value) * av_q2d(format->streams[index]->time_base); };
    bool first_video = true, first_audio = true;
    auto drain = [&](int index) {
        for (;;) {
            const int code = avcodec_receive_frame(decoders[index], frame);
            if (code == AVERROR(EAGAIN) || code == AVERROR_EOF) return;
            check(code >= 0, "Decode saved clip");
            if (index == video) ++clip.frames; else clip.audio_frames += frame->nb_samples;
            av_frame_unref(frame);
        }
    };
    while (av_read_frame(format, packet) >= 0) {
        const int index = packet->stream_index;
        if (index == video || index == audio) {
            const double start = seconds(index, packet->pts), end = start + seconds(index, packet->duration);
            if (index == video) { if (first_video) { clip.key_first = packet->flags & AV_PKT_FLAG_KEY; clip.video_start = start; first_video = false; } clip.video_end = std::max(clip.video_end, end); }
            else { if (first_audio) { clip.audio_start = start; first_audio = false; } clip.audio_end = std::max(clip.audio_end, end); }
            check(avcodec_send_packet(decoders[index], packet) >= 0, "Send saved packet"); drain(index);
        }
        av_packet_unref(packet);
    }
    for (const int index : {video, audio}) { avcodec_send_packet(decoders[index], nullptr); drain(index); }
    // Seek to the middle and decode a frame from the preceding keyframe.
    const auto middle = int64_t(((clip.video_start + clip.video_end) / 2) / av_q2d(format->streams[video]->time_base));
    check(av_seek_frame(format, video, middle, AVSEEK_FLAG_BACKWARD) >= 0, "Seek saved clip");
    avcodec_flush_buffers(decoders[video]);
    while (!clip.seek && av_read_frame(format, packet) >= 0) {
        if (packet->stream_index == video && avcodec_send_packet(decoders[video], packet) >= 0 && avcodec_receive_frame(decoders[video], frame) >= 0) { clip.seek = true; av_frame_unref(frame); }
        av_packet_unref(packet);
    }
    return clip;
}
}

int main(int argc, char** argv) {
    try {
        av_log_set_level(AV_LOG_ERROR); std::cout << std::unitbuf << std::fixed << std::setprecision(2);
        check(argc >= 4, "replay_save_check <history seconds> <work directory> <ffmpeg.exe>");
        const int history = std::atoi(argv[1]); const auto directory = std::filesystem::absolute(argv[2]);
        std::filesystem::create_directories(directory);
        RecorderSessionConfig config;
        config.capture.width = 2560; config.capture.height = 1440; config.capture.fps = 90; config.capture.bitrate_mbps = 25; config.capture.variable_frame_rate = true;
        LARGE_INTEGER counter{}, frequency{}; QueryPerformanceCounter(&counter); QueryPerformanceFrequency(&frequency);
        config.capture.qpc_anchor = counter.QuadPart; config.capture.qpc_frequency = frequency.QuadPart; config.capture.monotonic_anchor_us = monotonic_us();
#ifndef CLYPDAT_BASELINE
        for (int i = 4; i < argc; ++i) if (std::string(argv[i]) == "--reference") config.video_history.reference_pruning = true;
#endif
        config.history_seconds = history; config.work_directory = directory / L"work"; config.ffmpeg = argv[3];
        config.capture_input = false; config.audio_lanes = {{"game", "Game Audio", 2, 1, false}};
        for (int i = 4; i < argc; ++i) if (std::string(argv[i]) == "--hdr") config.capture.capture_hdr = true;
        RecorderSession recorder(config);
        recorder.start();
        // A continuous 440 Hz tone in 20 ms blocks stamped with capture time.
        std::atomic<bool> stop{false}; std::string audio_error;
        std::thread audio([&] { try {
            int64_t next = monotonic_us(); uint64_t sample = 0;
            while (!stop) {
                PcmBlock block; block.lane = "game"; block.source = "generated"; block.generation = 1; block.start_us = next; block.sample_rate = 48000; block.channels = 2;
                block.samples.resize(960 * 2);
                for (size_t i = 0; i < 960; ++i, ++sample) block.samples[i * 2] = block.samples[i * 2 + 1] = .1f * float(std::sin(2 * 3.141592653589793 * 440 * double(sample) / 48000));
                recorder.submit_pcm(std::move(block)); next += 20000;
                const auto wait = next - monotonic_us(); if (wait > 0) std::this_thread::sleep_for(std::chrono::microseconds(wait));
            }
        } catch (const std::exception& error) { audio_error = error.what(); } });
        struct Join { std::thread& thread; std::atomic<bool>& stop; ~Join() { stop = true; if (thread.joinable()) thread.join(); } } join{audio, stop};
        std::vector<double> output, fresh; uint64_t drops_start = 0; double completion50 = 0, completion95 = 0;
        const int total = history + 8; const double hz = tsc_hz(); uint64_t cycles_full = 0; auto full_at = std::chrono::steady_clock::now();
        for (int second = 0; second < total; ++second) {
            std::this_thread::sleep_for(1s);
            const auto h = recorder.health(); check(h.error.empty(), "Capture failed: " + h.error);
            if (second == 5) drops_start = h.backpressure_drops + h.replaced;
            if (second == history) { cycles_full = encoding_cycles(); full_at = std::chrono::steady_clock::now(); } // History now full.
            if (second >= 5) { output.push_back(h.output_fps); fresh.push_back(h.unique_fps); completion50 = h.completion_p50_ms; completion95 = h.completion_p95_ms; }
        }
        const double encoding_ms = double(encoding_cycles() - cycles_full) / hz * 1000 / std::chrono::duration<double>(std::chrono::steady_clock::now() - full_at).count();
        const auto h = recorder.health();
        const auto end_us = monotonic_us(), start_us = end_us - int64_t(history) * 1000000;
        const auto path = directory / (std::to_string(history) + "s.mp4"); std::filesystem::remove(path);
        const auto saving = std::chrono::steady_clock::now();
        recorder.save("check", start_us, end_us, path);
        auto saved = recorder.saved("check"); check(saved.has_value(), "Save not accepted");
        check(saved->completion.wait_for(120s) == std::future_status::ready, "Save timed out");
        const auto result = saved->completion.get(); check(result.error.empty(), "Save failed: " + result.error);
        const double save_s = std::chrono::duration<double>(std::chrono::steady_clock::now() - saving).count();
#ifndef CLYPDAT_BASELINE
        const auto stats = recorder.video_history_stats();
#endif
        stop = true; audio.join(); check(audio_error.empty(), "Audio submission failed: " + audio_error); check(recorder.stop(), "Recorder did not stop");
        const auto clip = inspect(path);
        double sum_output = 0, sum_fresh = 0; for (size_t i = 0; i < output.size(); ++i) { sum_output += output[i]; sum_fresh += fresh[i]; }
        std::cout << "history=" << history << "s source=" << h.source << (h.source_details.hdr_conversion ? " (HDR, FP16 tone-mapped)" : "") << " encoder=" << h.encoder << "\n  capture: output=" << sum_output / output.size() << " fresh=" << sum_fresh / fresh.size()
                  << " fps (min output " << *std::min_element(output.begin(), output.end()) << ") drops=" << (h.backpressure_drops + h.replaced - drops_start)
                  << " backpressure=" << h.backpressure_drops << " completion p50=" << completion50 << " p95=" << completion95 << " ms"
                  << "\n  encoding thread with full history: " << encoding_ms << " ms/s (" << encoding_ms / 10 << "% of a core)"
                  << "\n  save: " << save_s << " s, duration " << result.duration_us / 1e6 << " s, generation " << result.generation
                  << "\n  clip: firstPacketKey=" << clip.key_first << " videoFrames=" << clip.frames << " video " << clip.video_start << "-" << clip.video_end
                  << " s, audio " << clip.audio_start << "-" << clip.audio_end << " s (" << clip.audio_frames << " samples) startDelta=" << (clip.audio_start - clip.video_start) * 1000
                  << " ms endDelta=" << (clip.audio_end - clip.video_end) * 1000 << " ms seek=" << clip.seek << "\n";
#ifndef CLYPDAT_BASELINE
        std::vector<double> holds; for (const auto value : stats.recent_hold_ns) holds.push_back(value / 1000.0);
        std::cout << "  history: appended=" << stats.appended << " examined=" << stats.examined << " (" << double(stats.examined) / stats.appended << "/append) pruned=" << stats.pruned
                  << " retained=" << stats.packets << " keyframes=" << stats.keyframes << " peak=" << stats.keyframes_peak << " slowScans=" << stats.slow_scans
                  << " hold avg=" << double(stats.hold_ns_total) / stats.appended / 1000 << " p50=" << percentile(holds, .5) << " p95=" << percentile(holds, .95)
                  << " max=" << stats.hold_ns_max / 1000.0 << " us\n";
#endif
        check(clip.key_first && clip.seek && clip.frames > 0, "Saved clip failed verification");
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
