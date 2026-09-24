// VideoHistory's incremental pruning against the original full scan
// (VideoHistoryOptions::reference_pruning): identical streams go into both,
// and the retained packets are compared after every append, and snapshots
// for random windows.
//
// Manual: --bench measures both at 60 s x 60/90/120 FPS and 300 s x 120 FPS.
#include "recording_save.h"
#include <Windows.h>
#include <psapi.h>
extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/log.h>
}
#include <algorithm>
#include <atomic>
#include <chrono>
#include <iomanip>
#include <iostream>
#include <random>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace clypdat;
using namespace std::chrono_literals;

#define CHECK(x) do { if (!(x)) throw std::runtime_error(std::string("Check failed: ") + #x + " (line " + std::to_string(__LINE__) + ")"); } while (0)

namespace {
std::shared_ptr<const CaptureGeneration> make_generation(uint64_t id, int width = 1920) {
    auto generation = std::make_shared<CaptureGeneration>(); generation->id = id;
    AVCodecParameters* codec = avcodec_parameters_alloc(); CHECK(codec);
    codec->codec_type = AVMEDIA_TYPE_VIDEO; codec->codec_id = AV_CODEC_ID_H264; codec->width = width; codec->height = width * 9 / 16;
    generation->codec = std::shared_ptr<const AVCodecParameters>(codec, CaptureCodecParametersDeleter{});
    return generation;
}
struct Spec { int64_t pts = 0, dts = 0, acquired = 0; bool key = false, fresh = true; std::shared_ptr<const CaptureGeneration> generation; int payload = 0; };
Packet make_packet(const Spec& spec, int64_t identity) {
    Packet packet(av_packet_alloc()); CHECK(packet);
    if (spec.payload) CHECK(av_new_packet(packet.get(), spec.payload) == 0);
    packet->pts = spec.pts; packet->dts = spec.dts; packet->flags = spec.key ? AV_PKT_FLAG_KEY : 0; packet->pos = identity;
    return packet;
}
bool same(const HistoryPacket& a, const HistoryPacket& b) {
    return a.packet->pos == b.packet->pos && a.packet->pts == b.packet->pts && a.packet->dts == b.packet->dts &&
        a.packet->flags == b.packet->flags && a.generation == b.generation && a.generation->id == b.generation->id &&
        a.acquired_us == b.acquired_us && a.fresh == b.fresh;
}
std::string where(const std::string& label, uint64_t append) { return label + " after append " + std::to_string(append); }

// The same stream into the incremental history and the reference.
class Pair {
public:
    Pair(int64_t retention, std::string label, bool full_every_append)
        : incremental_(retention), reference_(retention, {true}), label_(std::move(label)), full_(full_every_append) {}
    void append(const Spec& spec) {
        incremental_.append(spec.generation, make_packet(spec, identity_), spec.acquired, spec.fresh);
        reference_.append(spec.generation, make_packet(spec, identity_), spec.acquired, spec.fresh);
        ++identity_; compare(full_ || identity_ % 997 == 0);
    }
    void clear() { incremental_.clear(); reference_.clear(); compare(true); }
    // Every retained packet, or (for long streams) count and both ends: both
    // hold suffixes of the same stream, so equal counts and fronts mean
    // equal contents; the full comparison still runs every 997 appends.
    void compare(bool full) {
        incremental_.inspect([&](const std::deque<HistoryPacket>& mine) {
            reference_.inspect([&](const std::deque<HistoryPacket>& theirs) {
                if (mine.size() != theirs.size())
                    throw std::runtime_error(where(label_, identity_) + ": retained " + std::to_string(mine.size()) + " packets, reference " + std::to_string(theirs.size()));
                if (mine.empty()) return;
                if (full) { for (size_t i = 0; i < mine.size(); ++i) if (!same(mine[i], theirs[i])) throw std::runtime_error(where(label_, identity_) + ": packet " + std::to_string(i) + " differs"); }
                else if (!same(mine.front(), theirs.front()) || !same(mine.back(), theirs.back())) throw std::runtime_error(where(label_, identity_) + ": ends differ");
            });
        });
        ++comparisons_;
    }
    // Both snapshots, or both refusals with the same reason.
    void compare_snapshot(int64_t start, int64_t end, bool after, bool variable, int fps) {
        std::string mine_error, theirs_error; VideoSnapshot mine, theirs;
        try { mine = incremental_.snapshot(start, end, after, variable, fps); } catch (const std::exception& e) { mine_error = e.what(); }
        try { theirs = reference_.snapshot(start, end, after, variable, fps); } catch (const std::exception& e) { theirs_error = e.what(); }
        const auto context = where(label_, identity_) + " snapshot [" + std::to_string(start) + "," + std::to_string(end) + ") after=" + std::to_string(after);
        if (mine_error != theirs_error) throw std::runtime_error(context + ": '" + mine_error + "' vs '" + theirs_error + "'");
        ++snapshots_; if (!mine_error.empty()) { ++refused_; return; }
        bool equal = mine.packets.size() == theirs.packets.size() && mine.start_us == theirs.start_us && mine.end_us == theirs.end_us &&
            mine.duration_us == theirs.duration_us && mine.generation == theirs.generation && mine.frozen == theirs.frozen &&
            mine.mappings.size() == theirs.mappings.size() && mine.overlay_mappings.size() == theirs.overlay_mappings.size();
        for (size_t i = 0; equal && i < mine.packets.size(); ++i) equal = same(mine.packets[i], theirs.packets[i]);
        auto mapping = [](const SourceMapping& a, const SourceMapping& b) { return a.source_start_us == b.source_start_us && a.duration_us == b.duration_us && a.output_start_us == b.output_start_us; };
        for (size_t i = 0; equal && i < mine.mappings.size(); ++i) equal = mapping(mine.mappings[i], theirs.mappings[i]);
        for (size_t i = 0; equal && i < mine.overlay_mappings.size(); ++i) equal = mapping(mine.overlay_mappings[i], theirs.overlay_mappings[i]);
        if (!equal) throw std::runtime_error(context + ": snapshots differ");
    }
    VideoHistoryStats stats() const { return incremental_.stats(); }
    VideoHistoryStats reference_stats() const { return reference_.stats(); }
    uint64_t appended() const { return uint64_t(identity_); }
    uint64_t comparisons_ = 0, snapshots_ = 0, refused_ = 0;
private:
    VideoHistory incremental_, reference_;
    std::string label_; bool full_; int64_t identity_ = 0;
};

// A recorder-like stream: pacer PTS (CFR rounding or VFR jitter, strictly
// increasing), keyframes every gop frames, capture time trailing the PTS.
struct Stream {
    int fps = 60; bool variable = false; int gop = 60; int64_t pts = 0; int64_t frame = 0; double constant = 0;
    std::shared_ptr<const CaptureGeneration> generation = make_generation(1);
    std::mt19937_64* random = nullptr;
    int64_t since_key = -1;
    Spec next(bool force_key = false) {
        const double interval = 1000000.0 / fps;
        if (variable) pts = std::max(pts + 1, pts + int64_t(interval * std::uniform_real_distribution<double>(.5, 2.0)(*random)));
        else { pts = std::max(pts + 1, int64_t(std::nearbyint(constant))); constant += interval; }
        Spec spec; spec.pts = spec.dts = pts; spec.acquired = 5000000 + pts + (random ? int64_t((*random)() % 3000) : 0);
        spec.key = force_key || since_key < 0 || since_key + 1 >= gop; since_key = spec.key ? 0 : since_key + 1;
        spec.fresh = !random || (*random)() % 5 != 0; spec.generation = generation; ++frame;
        return spec;
    }
};
void random_snapshots(Pair& pair, std::mt19937_64& random, int64_t latest_acquired, int64_t span, int count) {
    for (int i = 0; i < count; ++i) {
        const int64_t end = latest_acquired - int64_t(random() % uint64_t(std::max<int64_t>(1, span / 4))) + int64_t(random() % 2000000) - 500000;
        const int64_t start = end - 1 - int64_t(random() % uint64_t(std::max<int64_t>(2, span + 2000000)));
        pair.compare_snapshot(start, end, random() % 2 == 0, random() % 2 == 0, int(30 + random() % 91));
    }
}

// Recorder-shaped streams at every rate and retention: CFR and VFR, GOP of
// one second, to well past steady state.
void regular_streams() {
    for (const int64_t retention_s : {15, 60, 300}) for (const int fps : {30, 60, 90, 120}) for (const bool variable : {false, true}) {
        if (variable && retention_s == 300 && fps != 120) continue;
        std::mt19937_64 random(uint64_t(retention_s * 1000 + fps * 2 + variable));
        const auto label = std::to_string(retention_s) + "s " + std::to_string(fps) + "fps " + (variable ? "VFR" : "CFR");
        Pair pair(retention_s * 1000000, label, false);
        Stream stream; stream.fps = fps; stream.gop = fps; stream.variable = variable; stream.random = &random;
        const int64_t total = (retention_s + 12) * fps; int64_t latest = 0;
        for (int64_t i = 0; i < total; ++i) {
            const auto spec = stream.next(); latest = spec.acquired; pair.append(spec);
            if (i % (fps * 3) == 0) random_snapshots(pair, random, latest, retention_s * 1000000, 3);
        }
        pair.compare(true);
        const auto stats = pair.stats();
        // Steady state: one retention plus at most one GOP, O(1) index work.
        CHECK(stats.packets <= size_t(retention_s * fps + stream.gop * (variable ? 3 : 1) + 2));
        CHECK(stats.slow_scans == 0 && stats.keyframes <= retention_s + 3);
        if (double(stats.examined) / stats.appended > 3.0) throw std::runtime_error(label + ": examined " + std::to_string(stats.examined) + " for " + std::to_string(stats.appended) + " appends");
        std::cout << "  " << std::left << std::setw(18) << label << " retained=" << stats.packets << " keyframes=" << stats.keyframes
                  << " examined/append=" << std::setprecision(3) << double(stats.examined) / stats.appended << " (reference "
                  << double(pair.reference_stats().examined) / stats.appended << ")\n" << std::right;
    }
}

// GOP shapes the recorder does not produce but the history must survive.
void unusual_streams() {
    std::mt19937_64 random(99);
    auto run = [&](const std::string& label, int64_t retention, int fps, int count, auto keyframe) {
        Pair pair(retention, label, true);
        Stream stream; stream.fps = fps; stream.gop = INT32_MAX; stream.since_key = 0; stream.random = &random; int64_t latest = 0;
        for (int i = 0; i < count; ++i) {
            auto spec = stream.next(); spec.key = keyframe(i); latest = spec.acquired; pair.append(spec);
            if (i % 37 == 0) random_snapshots(pair, random, latest, retention, 2);
        }
        std::cout << "  " << label << ": " << pair.appended() << " appends, slow scans " << pair.stats().slow_scans << "\n";
        CHECK(pair.stats().slow_scans == 0);
    };
    run("irregular keyframes", 2000000, 60, 3000, [&](int) { return random() % 40 == 0; });
    run("very long GOP", 1000000, 60, 2000, [](int i) { return i % 600 == 0; });
    run("GOP longer than history", 3000000, 90, 3000, [](int i) { return i == 0 || i == 2500; });
    run("consecutive keyframes", 500000, 120, 2000, [](int i) { return (i / 7) % 5 == 0; });
    run("every frame a keyframe", 400000, 60, 1000, [](int) { return true; });
    run("first keyframe late", 1000000, 60, 1500, [](int i) { return i >= 400 && i % 60 == 0; });
    run("no keyframes", 500000, 60, 800, [](int) { return false; });
}

// Encoder replacement mid-stream: a new generation (incompatible after the
// first switch) opening on a keyframe; PTS continue from the same pacer.
// Adaptive frame rate: the pacer's interval changes, GOP follows it.
void generations_and_rates() {
    std::mt19937_64 random(5);
    Pair pair(3000000, "generations and adaptive fps", true);
    Stream stream; stream.random = &random; int64_t latest = 0; uint64_t id = 1;
    const int rates[] = {120, 90, 60, 30, 60, 120, 90};
    for (int phase = 0; phase < 14; ++phase) {
        stream.fps = rates[phase % 7]; stream.gop = stream.fps; stream.variable = phase % 3 == 2;
        bool opening = phase % 2 == 1;
        if (opening) stream.generation = make_generation(++id, id % 3 == 0 ? 2560 : 1920);
        for (int i = 0; i < stream.fps * 2; ++i) {
            const auto spec = stream.next(opening); opening = false; latest = spec.acquired; pair.append(spec);
            if (i % 23 == 0) random_snapshots(pair, random, latest, 3000000, 2);
        }
    }
    CHECK(pair.stats().slow_scans == 0);
    std::cout << "  generations and adaptive fps: " << pair.appended() << " appends, " << pair.snapshots_ << " snapshots (" << pair.refused_ << " refused alike)\n";
}

// PTS jumps forward, then backwards (keyframes and between them): the
// incremental history scans its keyframe index until the inversion is
// pruned, then returns to the fast path.
void discontinuities() {
    std::mt19937_64 random(17);
    Pair pair(1000000, "discontinuities", true);
    Stream stream; stream.random = &random; stream.fps = 60; stream.gop = 30; int64_t latest = 0;
    auto feed = [&](int count) { for (int i = 0; i < count; ++i) { const auto spec = stream.next(); latest = spec.acquired; pair.append(spec); if (i % 11 == 0) random_snapshots(pair, random, latest, 1000000, 2); } };
    feed(300);
    stream.pts += 100000000; stream.constant += 100000000; feed(200);            // forward 100 s
    stream.pts -= 40000000; stream.constant -= 40000000; feed(200);              // back 40 s
    const auto regressed = pair.stats().slow_scans; CHECK(regressed > 0);
    stream.pts -= 1000000; stream.constant -= 1000000; stream.since_key = -1; feed(20); // a keyframe behind its predecessor
    feed(400);
    const auto settled = pair.stats().slow_scans; feed(300);
    CHECK(pair.stats().slow_scans == settled); // Inversions pruned: fast path again.
    std::cout << "  discontinuities: " << pair.appended() << " appends, " << settled << " slow scans while keyframe PTS went backwards\n";
}

// Thousands of random streams: rates, retentions, GOPs, VFR, generation
// switches, regressions and clears, compared after every append.
void random_streams(int count) {
    uint64_t appends = 0, snapshots = 0, slow = 0, clears = 0;
    for (int n = 0; n < count; ++n) {
        std::mt19937_64 random(uint64_t(n) * 7919 + 1);
        const int64_t retention = 100000 + int64_t(random() % 3000000);
        Pair pair(retention, "random stream " + std::to_string(n), true);
        Stream stream; stream.random = &random; stream.fps = int(30 + random() % 91); stream.variable = random() % 2;
        stream.gop = int(1 + random() % 200); if (random() % 5 == 0) stream.since_key = 0; // first keyframe late
        const int length = int(50 + random() % 900); int64_t latest = 0; uint64_t id = 1;
        const bool chaotic = random() % 6 == 0;
        for (int i = 0; i < length; ++i) {
            const auto event = random() % 1000;
            if (event < 4) { stream.generation = make_generation(++id, random() % 2 ? 1920 : 1280); stream.since_key = -1; }
            else if (event < 8) { stream.fps = int(30 + random() % 91); if (random() % 2) stream.gop = int(1 + random() % 200); }
            else if (event < 10) { pair.clear(); ++clears; }
            else if (chaotic && event < 20) { const auto jump = int64_t(random() % 4000000) - 2500000; stream.pts += jump; stream.constant += double(jump); }
            auto spec = stream.next();
            if (random() % 50 == 0) spec.key = !spec.key;
            if (random() % 40 == 0) spec.acquired = 0;
            if (spec.acquired) latest = spec.acquired;
            pair.append(spec);
            if (random() % 25 == 0) random_snapshots(pair, random, latest ? latest : spec.pts, retention, 1);
        }
        appends += pair.appended(); snapshots += pair.snapshots_; slow += pair.stats().slow_scans;
    }
    std::cout << "  " << count << " random streams: " << appends << " appends compared after each, " << snapshots << " snapshots, "
              << clears << " clears, " << slow << " slow scans\n";
}

// append, snapshot and clear from different threads: no deadlock or
// corruption, and clear resets the index so later appends prune as if new.
void concurrent_access() {
    VideoHistory history(1000000);
    std::atomic<bool> stop{false}; std::atomic<uint64_t> appended{0}, snapshots{0}, clears{0};
    std::thread writer([&] {
        Stream stream; stream.fps = 120; stream.gop = 60;
        while (!stop) { const auto spec = stream.next(); history.append(spec.generation, make_packet(spec, 0), spec.acquired, true); ++appended; std::this_thread::yield(); }
    });
    std::vector<std::thread> readers;
    for (int r = 0; r < 2; ++r) readers.emplace_back([&, r] {
        std::mt19937_64 random{uint64_t(r)};
        while (!stop) {
            const auto latest = int64_t(appended.load()) * 8333 + 5000000;
            try { history.snapshot(latest - int64_t(random() % 1500000), latest - int64_t(random() % 500000), random() % 2); } catch (const std::exception&) {}
            ++snapshots;
            const auto stats = history.stats(); CHECK(stats.keyframes <= stats.packets);
        }
    });
    std::thread clearer([&] { while (!stop) { std::this_thread::sleep_for(20ms); history.clear(); ++clears; } });
    std::this_thread::sleep_for(1500ms); stop = true;
    writer.join(); clearer.join(); for (auto& reader : readers) reader.join();
    if (appended < 1000 || snapshots < 100 || clears < 20)
        throw std::runtime_error("Concurrent access ran too little: " + std::to_string(appended) + " appends, " + std::to_string(snapshots) + " snapshots, " + std::to_string(clears) + " clears");
    // After a clear the history behaves exactly like a new one.
    history.clear();
    VideoHistory reference(1000000, {true}); Stream stream; stream.fps = 60; stream.gop = 45;
    for (int i = 0; i < 600; ++i) {
        const auto spec = stream.next();
        history.append(spec.generation, make_packet(spec, i), spec.acquired, spec.fresh); reference.append(spec.generation, make_packet(spec, i), spec.acquired, spec.fresh);
        size_t mine = 0, theirs = 0; int64_t first = -1, other = -2;
        history.inspect([&](const auto& packets) { mine = packets.size(); first = packets.front().packet->pos; });
        reference.inspect([&](const auto& packets) { theirs = packets.size(); other = packets.front().packet->pos; });
        CHECK(mine == theirs && first == other);
    }
    std::cout << "  concurrency: " << appended << " appends, " << snapshots << " snapshots, " << clears << " clears across threads\n";
}

// Manual benchmark: prefilled to steady state, then paced at the frame rate.
struct BenchResult { double append_cpu_percent, examined_per_second, average_us, p50_us, p95_us, max_us, hold_p50_us, hold_p95_us, hold_max_us; size_t retained, keyframes, private_mb; };
double percentile(std::vector<double> values, double rank) { if (values.empty()) return 0; std::sort(values.begin(), values.end()); return values[std::min(values.size() - 1, size_t(rank * values.size()))]; }
size_t private_bytes() { PROCESS_MEMORY_COUNTERS_EX memory{}; GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory), sizeof(memory)); return memory.PrivateUsage; }
BenchResult bench(int64_t retention_s, int fps, bool reference, int seconds) {
    VideoHistory history(retention_s * 1000000, {reference});
    Stream stream; stream.fps = fps; stream.gop = fps;
    const int payload = int(25000000 / 8 / fps); // 25 Mbps
    int64_t identity = 0;
    auto next = [&] { auto spec = stream.next(); spec.payload = spec.key ? payload * 4 : payload; return spec; };
    for (int64_t i = 0; i < (retention_s + 2) * fps; ++i) { const auto spec = next(); history.append(spec.generation, make_packet(spec, identity++), spec.acquired, true); }
    const auto before = history.stats();
    std::vector<double> times; times.reserve(size_t(seconds * fps));
    LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
    const auto started = std::chrono::steady_clock::now(); auto due = started; double busy = 0;
    for (int i = 0; i < seconds * fps; ++i) {
        due += std::chrono::microseconds(1000000 / fps); std::this_thread::sleep_until(due);
        const auto spec = next(); auto packet = make_packet(spec, identity++);
        LARGE_INTEGER a{}, b{}; QueryPerformanceCounter(&a);
        history.append(spec.generation, std::move(packet), spec.acquired, true);
        QueryPerformanceCounter(&b); const double us = double(b.QuadPart - a.QuadPart) * 1e6 / double(frequency.QuadPart);
        times.push_back(us); busy += us;
    }
    const double wall = std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count();
    const auto after = history.stats();
    std::vector<double> holds; for (const auto value : after.recent_hold_ns) holds.push_back(value / 1000.0);
    BenchResult result{};
    result.append_cpu_percent = busy / 1e6 / wall * 100; result.examined_per_second = double(after.examined - before.examined) / wall;
    result.average_us = busy / double(times.size()); result.p50_us = percentile(times, .5); result.p95_us = percentile(times, .95);
    result.max_us = *std::max_element(times.begin(), times.end());
    result.hold_p50_us = percentile(holds, .5); result.hold_p95_us = percentile(holds, .95); result.hold_max_us = percentile(holds, 1);
    result.retained = after.packets; result.keyframes = after.keyframes; result.private_mb = private_bytes() / (1024 * 1024);
    return result;
}
int run_bench(int seconds) {
    std::cout << std::fixed << std::setprecision(3);
    for (const auto [retention, fps] : std::vector<std::pair<int64_t, int>>{{60, 60}, {60, 90}, {60, 120}, {300, 120}}) {
        for (const bool reference : {true, false}) {
            const auto r = bench(retention, fps, reference, seconds);
            std::cout << retention << "s/" << fps << "fps " << (reference ? "reference  " : "incremental") << ": appendCpu=" << r.append_cpu_percent << "% examined/s="
                      << std::setprecision(0) << r.examined_per_second << std::setprecision(3) << " append avg=" << r.average_us << " p50=" << r.p50_us << " p95=" << r.p95_us
                      << " max=" << r.max_us << "us hold p50=" << r.hold_p50_us << " p95=" << r.hold_p95_us << " max=" << r.hold_max_us << "us retained=" << r.retained
                      << " keyframes=" << r.keyframes << " index=" << r.keyframes * 16 << "B private=" << r.private_mb << "MB\n";
        }
    }
    return 0;
}
}

int main(int argc, char** argv) {
    try {
        av_log_set_level(AV_LOG_ERROR);
        if (argc > 1 && std::string(argv[1]) == "--bench") return run_bench(argc > 2 ? std::atoi(argv[2]) : 20);
        const auto started = std::chrono::steady_clock::now();
        std::cout << "regular streams\n"; regular_streams();
        std::cout << "unusual GOPs\n"; unusual_streams();
        generations_and_rates();
        discontinuities();
        random_streams(argc > 1 ? std::atoi(argv[1]) : 3000);
        concurrent_access();
        std::cout << "Video history tests passed in " << std::chrono::duration<double>(std::chrono::steady_clock::now() - started).count() << " s\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
