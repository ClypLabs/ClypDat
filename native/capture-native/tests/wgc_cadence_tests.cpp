// Windows Graphics Capture cadence: a deterministic model of display
// composition ticks, the WGC MinUpdateInterval gate, callback delivery, the
// depth-2 source queue and the recorder's timestamp-aware output selection
// (capture_select_frame). Every throttled run is compared with the same
// source and display at the current safe cadence.
//
// Manual: --explore prints the target x refresh x source x ticks matrix.
#include "recording_capture.h"
extern "C" {
#include <libavutil/log.h>
}
#include <algorithm>
#include <cmath>
#include <deque>
#include <functional>
#include <iomanip>
#include <iostream>
#include <map>
#include <mutex>
#include <random>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

using namespace clypdat;

#define CHECK(x) do { if (!(x)) throw std::runtime_error(std::string("Check failed: ") + #x + " (line " + std::to_string(__LINE__) + ")"); } while (0)

namespace {
// How the captured application presents. Rates are frames per second.
struct Source {
    double fps = 60;
    double random_jitter = 0;   // Uniform +-fraction of the frame interval.
    double periodic_jitter = 0; // Sinusoidal +-fraction, 1.7 s period.
    double uneven = 0;          // Alternating long/short frames, +-fraction.
    bool refresh_locked = false; // A new frame for every composition (uncapped animation).
    struct Dip { double start, seconds, factor; };
    std::vector<Dip> dips;       // Temporary frame-rate drops.
    double rate_at(double seconds) const {
        for (const auto& dip : dips) if (seconds >= dip.start && seconds < dip.start + dip.seconds) return fps * dip.factor;
        return fps;
    }
};
struct Display {
    double refresh = 240;
    double drift_ppm = 37;   // Display clock against the source clock.
    double latch_us = 700;   // A present must land this long before composition.
};
struct Window { double delivered = 0, fresh = 0, duplicates = 0, selection_dropped = 0, latency_p50 = 0, latency_p95 = 0; };
struct Result {
    double seconds = 0, delivered = 0, fresh = 0, duplicates = 0, selection_dropped = 0, latency_p50 = 0, latency_p95 = 0, min_fresh_window = 1e9;
    // |selected timestamp - sample target| and |source-time step between
    // consecutive outputs - output interval| (judder), milliseconds.
    double error_p50 = 0, error_p95 = 0, judder_p50 = 0, judder_p95 = 0;
    std::vector<Window> windows;
};
double percentile(std::vector<double> values, double rank) {
    if (values.empty()) return 0; std::sort(values.begin(), values.end());
    return values[std::min(values.size() - 1, size_t(rank * double(values.size())))];
}

// Runs the pipeline for `seconds`. ticks(window) chooses the WGC gate in
// whole display ticks (0 = no MinUpdateInterval) and may change the target.
struct Control { int target; int ticks; double refresh; };
Result simulate(const Source& source, Display display, Control control, double seconds, uint32_t seed,
    const std::function<void(int second, const Window&, Control&)>& each_second = {}) {
    std::mt19937_64 random(seed);
    std::uniform_real_distribution<double> unit(0, 1);
    Result result; result.seconds = seconds;
    const double end = seconds * 1e6;
    // Source presents, generated ahead of the display.
    double present = unit(random) * 1e6 / source.fps; uint64_t presented = 0; int parity = 0;
    std::deque<double> presents;
    auto next_present = [&] {
        const double interval = 1e6 / std::max(1.0, source.rate_at(present / 1e6));
        double step = interval * (1 + source.random_jitter * (2 * unit(random) - 1));
        step += interval * source.periodic_jitter * std::sin(present / 1e6 * 2 * 3.14159265 / 1.7);
        step += interval * source.uneven * ((parity++ & 1) ? 1 : -1);
        present += std::max(100.0, step);
        return present;
    };
    // Display composition.
    double tick_period = 1e6 / (display.refresh * (1 + display.drift_ppm * 1e-6)); double tick = unit(random) * tick_period;
    uint64_t shown = 0, delivered_content = 0; double last_delivery = -1e18; uint64_t sequence = 0;
    struct Arrival { double at; CaptureFrameStamp frame; };
    std::deque<Arrival> arrivals;
    std::deque<CaptureFrameStamp> recent; uint64_t consumed = 0;
    // CFR pacer: wakes up to 1 ms early like the recorder, late by timer jitter.
    double deadline = 0, pacer = 0;
    // The recorder's timer is set 1 ms before the deadline; wakes land late by timer jitter.
    auto pacer_wake = [&](double due) { const double late = unit(random) * 600 + (unit(random) < .005 ? 1500 + unit(random) * 2500 : 0); return due - 1000 + late; };
    pacer = pacer_wake(deadline + 1e6 / control.target);
    Window window; std::vector<double> latencies, all_latencies, errors, judders; int second = 0; double previous_selected = -1;
    double next_window = 1e6;
    while (true) {
        const double arrival_at = arrivals.empty() ? 1e300 : arrivals.front().at;
        const double now = std::min({tick, arrival_at, pacer, next_window});
        if (now > end) break;
        if (now == next_window) {
            window.latency_p50 = percentile(latencies, .5); window.latency_p95 = percentile(latencies, .95); latencies.clear();
            result.windows.push_back(window);
            if (each_second) {
                const int previous_target = control.target; const double previous_refresh = control.refresh;
                each_second(second, window, control);
                if (control.refresh != previous_refresh) { display.refresh = control.refresh; tick_period = 1e6 / (display.refresh * (1 + display.drift_ppm * 1e-6)); }
                if (control.target != previous_target) { deadline = now; pacer = pacer_wake(deadline + 1e6 / control.target); }
            }
            ++second; window = {}; next_window += 1e6; continue;
        }
        if (now == tick) {
            // Content latched for this composition.
            if (source.refresh_locked) { ++presented; ++shown; }
            else {
                while (presents.empty() || presents.back() < tick) presents.push_back(next_present());
                while (!presents.empty() && presents.front() + display.latch_us <= tick) { presents.pop_front(); ++presented; }
                shown = presented;
            }
            const double gate = control.ticks > 0 ? (control.ticks - .5) * 1e6 / display.refresh : 0;
            if (shown > delivered_content && tick - last_delivery >= gate) {
                delivered_content = shown; last_delivery = tick;
                arrivals.push_back({tick + 200 + unit(random) * 1300, {++sequence, int64_t(tick)}});
                ++window.delivered; ++result.delivered;
            }
            tick += tick_period; continue;
        }
        if (now == arrival_at) {
            recent.push_back(arrivals.front().frame); arrivals.pop_front();
            while (recent.size() > 2) { if (recent.front().sequence > consumed) { ++window.selection_dropped; ++result.selection_dropped; } recent.pop_front(); }
            continue;
        }
        // Pacer tick: sample one output interval back.
        const double interval = 1e6 / control.target;
        const int64_t intervals = std::max<int64_t>(1, int64_t(std::floor((now - deadline + 1000) / interval)));
        deadline += interval * double(intervals);
        const std::vector<CaptureFrameStamp> view(recent.begin(), recent.end());
        if (const auto pick = capture_select_frame(view, consumed, int64_t(now - interval))) {
            for (size_t i = 0; i < *pick; ++i) if (view[i].sequence > consumed) { ++window.selection_dropped; ++result.selection_dropped; }
            consumed = view[*pick].sequence; recent.erase(recent.begin(), recent.begin() + std::ptrdiff_t(*pick) + 1);
            ++window.fresh; ++result.fresh; latencies.push_back((now - double(view[*pick].timestamp_us)) / 1000); all_latencies.push_back(latencies.back());
            const double selected = double(view[*pick].timestamp_us);
            errors.push_back(std::abs(selected - (now - interval)) / 1000);
            judders.push_back(std::abs((previous_selected < 0 ? interval : selected - previous_selected) - interval) / 1000); previous_selected = selected;
        } else { ++window.duplicates; ++result.duplicates; previous_selected += interval; }
        pacer = pacer_wake(deadline + interval);
    }
    result.delivered /= seconds; result.fresh /= seconds; result.duplicates /= seconds; result.selection_dropped /= seconds;
    result.latency_p50 = percentile(all_latencies, .5); result.latency_p95 = percentile(all_latencies, .95);
    result.error_p50 = percentile(errors, .5); result.error_p95 = percentile(errors, .95);
    result.judder_p50 = percentile(judders, .5); result.judder_p95 = percentile(judders, .95);
    for (size_t i = 2; i < result.windows.size(); ++i) result.min_fresh_window = std::min(result.min_fresh_window, result.windows[i].fresh);
    return result;
}

// The source shapes every candidate is checked against.
std::vector<std::pair<std::string, Source>> sources_for(int target, double refresh) {
    std::vector<std::pair<std::string, Source>> list;
    const double rates[] = {24, 30, 40, 45, 50, 60, 72, 75, 90, 100, 110, 120, 144, 165, 180, 240, 360};
    for (const double rate : rates) {
        if (rate > refresh * 1.01 || rate < target * .75) continue;
        Source clean; clean.fps = rate; list.push_back({std::to_string(int(rate)), clean});
        Source jitter = clean; jitter.random_jitter = .25; list.push_back({std::to_string(int(rate)) + "~rand", jitter});
        Source periodic = clean; periodic.periodic_jitter = .3; list.push_back({std::to_string(int(rate)) + "~wave", periodic});
        Source uneven = clean; uneven.uneven = .35; uneven.random_jitter = .05; list.push_back({std::to_string(int(rate)) + "~uneven", uneven});
        Source dips = clean; dips.random_jitter = .1; dips.dips = {{20, 3, .6}, {70, 5, .8}, {130, 2, .5}}; list.push_back({std::to_string(int(rate)) + "~dips", dips});
    }
    Source locked; locked.fps = refresh; locked.refresh_locked = true; list.push_back({"refresh", locked});
    Source slightly; slightly.fps = target * 1.03; slightly.random_jitter = .15; list.push_back({"target+3%", slightly});
    return list;
}
// Current production policy, in display ticks.
int safe_ticks(int target, double refresh) { return std::max(1, int(std::floor(refresh / (target * 1.5) + .000001))); }

// One line per target, refresh and candidate: worst fresh loss, duplicate
// gain, worst one-second fresh window, delivery and latency against the safe
// cadence over every source shape.
int explore(double seconds, bool verbose) {
    std::cout << std::fixed << std::setprecision(2);
    for (const int target : {30, 60, 90, 120}) for (const double refresh : {60.0, 120.0, 144.0, 165.0, 240.0, 360.0}) {
        const int safe = safe_ticks(target, refresh);
        for (int ticks = 1; refresh / ticks > target * 1.0001; ++ticks) {
            double worst_fresh = 1e9, worst_window = 1e9, worst_latency = -1e9, worst_p50 = -1e9, worst_dup = -1e9, base_p95 = 0, delivered_saved = 0;
            double worst_judder = -1e9, worst_error = -1e9, base_judder = 0; std::string worst_judder_name;
            std::string worst_fresh_name, worst_latency_name, worst_window_name; int unsafe = 0, total = 0;
            for (const auto& [name, source] : sources_for(target, refresh)) {
                Display display; display.refresh = refresh;
                const auto base = simulate(source, display, {target, safe, refresh}, seconds, 1234);
                const auto test = simulate(source, display, {target, ticks, refresh}, seconds, 1234);
                const double fresh_loss = base.fresh - test.fresh, dup_gain = test.duplicates - base.duplicates;
                const double window_loss = base.min_fresh_window - test.min_fresh_window;
                ++total;
                if (fresh_loss > target * .002 || dup_gain > target * .002 || window_loss > std::max(1.0, target * .02)) ++unsafe;
                if (-fresh_loss < worst_fresh) { worst_fresh = -fresh_loss; worst_fresh_name = name; }
                if (-window_loss < worst_window) { worst_window = -window_loss; worst_window_name = name; }
                if (test.latency_p95 - base.latency_p95 > worst_latency) { worst_latency = test.latency_p95 - base.latency_p95; worst_latency_name = name; base_p95 = base.latency_p95; }
                worst_p50 = std::max(worst_p50, test.latency_p50 - base.latency_p50);
                worst_dup = std::max(worst_dup, dup_gain);
                if (test.judder_p95 - base.judder_p95 > worst_judder) { worst_judder = test.judder_p95 - base.judder_p95; worst_judder_name = name; base_judder = base.judder_p95; }
                worst_error = std::max(worst_error, test.error_p95 - base.error_p95);
                if (name == "refresh") delivered_saved = base.delivered - test.delivered;
                if (verbose) std::cout << "    " << name << ": delivered " << base.delivered << " -> " << test.delivered << " fresh " << base.fresh << " -> " << test.fresh
                    << " minWindow " << base.min_fresh_window << " -> " << test.min_fresh_window << " p95 " << base.latency_p95 << " -> " << test.latency_p95 << " judder " << base.judder_p95 << " -> " << test.judder_p95 << " error " << base.error_p95 << " -> " << test.error_p95 << "\n";
            }
            std::cout << target << "fps@" << int(refresh) << "Hz ticks=" << ticks << (ticks == safe ? " (safe)" : "") << " ceiling=" << refresh / ticks << " (x" << refresh / ticks / target
                      << "): unsafe " << unsafe << "/" << total << " fresh " << worst_fresh << " (" << worst_fresh_name << ") window " << worst_window << " (" << worst_window_name
                      << ") dup +" << worst_dup << " latency p50 +" << worst_p50 << " p95 +" << worst_latency << "ms of " << base_p95 << " (" << worst_latency_name
                      << ") judder p95 +" << worst_judder << "ms of " << base_judder << " (" << worst_judder_name << ") error p95 +" << worst_error
                      << " saves " << delivered_saved << " copies/s\n";
        }
    }
    return 0;
}

// The production policy in display ticks, and the interval that asks for it.
void policy_table() {
    struct Expected { int target; double refresh; int ticks; };
    const Expected expected[] = {
        {30, 60, 1}, {60, 60, 1}, {90, 60, 1}, {120, 60, 1},
        {30, 120, 2}, {60, 120, 1}, {90, 120, 1}, {120, 120, 1},
        {30, 144, 3}, {60, 144, 1}, {90, 144, 1}, {120, 144, 1},
        {30, 165, 3}, {60, 165, 1}, {90, 165, 1}, {120, 165, 1},
        {30, 240, 5}, {60, 240, 2}, {90, 240, 1}, {120, 240, 1},
        {30, 360, 8}, {60, 360, 4}, {90, 360, 2}, {120, 360, 2},
        {90, 239.96, 1}, {60, 59.94, 1}, {60, 143.98, 1}};
    for (const auto& e : expected) {
        const int ticks = capture_wgc_update_ticks(e.target, e.refresh);
        if (ticks != e.ticks) throw std::runtime_error("Policy ticks for " + std::to_string(e.target) + " fps at " + std::to_string(e.refresh) + " Hz: " + std::to_string(ticks));
        // Never at or below target; 1.5x headroom whenever more than one tick.
        CHECK(e.refresh / ticks > e.target || ticks == 1);
        if (ticks > 1) CHECK(e.refresh / ticks >= e.target * 1.5 - .01);
        CHECK(e.refresh / (ticks + 1) < e.target * 1.5);
        CHECK(capture_wgc_interval_100ns(e.target, e.refresh) == int64_t(std::llround(1e7 / e.refresh * (ticks - .5))));
    }
    CHECK(capture_wgc_update_ticks(90, 0) == 0 && capture_wgc_interval_100ns(90, 0) == int64_t(std::llround(1e7 / 90 / 2)));
    CHECK(capture_wgc_update_ticks(90, std::nan("")) == 0);
    std::cout << "  policy table: " << std::size(expected) << " target/refresh pairs\n";
}

double permitted(int target, const Source& source, double refresh) { return std::min<double>({double(target), source.refresh_locked ? refresh : source.fps, refresh}); }
std::vector<std::pair<std::string, Source>> quality_sources(int target, double refresh) {
    std::vector<std::pair<std::string, Source>> list;
    for (const double factor : {.8, 1.0, 1.1, 1.34, 1.6, 2.0}) {
        const double rate = target * factor; if (rate > refresh * 1.01) continue;
        Source clean; clean.fps = rate; list.push_back({std::to_string(int(rate)), clean});
        Source jitter = clean; jitter.random_jitter = .25; list.push_back({std::to_string(int(rate)) + "~rand", jitter});
        Source uneven = clean; uneven.uneven = .35; uneven.random_jitter = .05; list.push_back({std::to_string(int(rate)) + "~uneven", uneven});
        Source dips = clean; dips.random_jitter = .1; dips.dips = {{20, 3, .6}, {70, 5, .8}, {130, 2, .5}}; list.push_back({std::to_string(int(rate)) + "~dips", dips});
    }
    Source locked; locked.fps = refresh; locked.refresh_locked = true; list.push_back({"refresh", locked});
    return list;
}
// Three simulated minutes per target, refresh and source: the policy's
// cadence keeps every fresh frame the finest cadence gets.
void safe_policy_quality() {
    int cases = 0;
    for (const int target : {30, 60, 90, 120}) for (const double refresh : {60.0, 120.0, 144.0, 165.0, 240.0, 360.0}) {
        const int safe = capture_wgc_update_ticks(target, refresh);
        for (const auto& [name, source] : quality_sources(target, refresh)) {
            Display display; display.refresh = refresh;
            const auto finest = simulate(source, display, {target, 1, refresh}, 180, 99);
            const auto policy = simulate(source, display, {target, safe, refresh}, 180, 99);
            const auto label = std::to_string(target) + " fps @ " + std::to_string(int(refresh)) + " Hz source " + name;
            if (finest.fresh - policy.fresh > target * .002 || policy.duplicates - finest.duplicates > target * .002 || finest.min_fresh_window - policy.min_fresh_window > std::max(1.0, target * .02))
                throw std::runtime_error(label + ": policy fresh " + std::to_string(policy.fresh) + " vs finest " + std::to_string(finest.fresh));
            // A steady source with headroom over the target and under the
            // refresh (so composition never merges its frames) is output
            // fresh at the target. Jittered sources near the refresh lose
            // frames to composition at every cadence; the comparison above
            // covers them.
            const bool steady = source.refresh_locked || (source.random_jitter == 0 && source.uneven == 0 && source.fps <= refresh * .9);
            if (!steady || !source.dips.empty() || permitted(target, source, refresh) < target || (source.refresh_locked ? refresh : source.fps) < target * 1.3) continue;
            if (policy.fresh < target * .99) throw std::runtime_error(label + ": fresh " + std::to_string(policy.fresh));
            ++cases;
        }
    }
    std::cout << "  safe policy matches the finest cadence across the matrix (" << cases << " sources able to supply the target)\n";
}
// Every coarser whole-tick cadence with savings keeps fresh-frame counts but
// samples a sparser grid: judder or latency rises well past noise. This is
// why the policy stays at 1.5x headroom.
void coarser_cadence_costs_timing() {
    std::cout << std::fixed << "  target  refresh  safe->coarse  ceiling  copies saved  fresh  judder p95  latency p50\n";
    int candidates = 0;
    for (const int target : {30, 60, 90, 120}) for (const double refresh : {60.0, 120.0, 144.0, 165.0, 240.0, 360.0}) {
        const int safe = capture_wgc_update_ticks(target, refresh), coarse = safe + 1;
        if (refresh / coarse <= target * 1.0001) continue;
        Source locked; locked.fps = refresh; locked.refresh_locked = true; Display display; display.refresh = refresh;
        const auto base = simulate(locked, display, {target, safe, refresh}, 180, 7), test = simulate(locked, display, {target, coarse, refresh}, 180, 7);
        const double judder = test.judder_p95 - base.judder_p95, latency = test.latency_p50 - base.latency_p50;
        std::cout << "  " << std::setw(6) << target << std::setw(9) << int(refresh) << std::setw(8) << safe << "->" << coarse << std::setw(9) << std::setprecision(1) << refresh / coarse
                  << std::setw(14) << base.delivered - test.delivered << std::setw(7) << test.fresh - base.fresh << std::setw(7) << base.judder_p95 << "->" << test.judder_p95
                  << std::setw(7) << base.latency_p50 << "->" << test.latency_p50 << "\n";
        CHECK(std::abs(test.fresh - base.fresh) < target * .002);
        if (judder < 1 && latency < 1) throw std::runtime_error("Coarser cadence " + std::to_string(coarse) + " for " + std::to_string(target) + " fps at " +
            std::to_string(refresh) + " Hz costs no timing accuracy; revisit the policy");
        ++candidates;
    }
    CHECK(candidates >= 10);
}
// The cadence follows the active rate the moment it changes (the recorder
// reapplies it before the next acquisition): no window starves after
// 120 -> 90 -> 60 -> 30 -> 60 -> 90 -> 120. A cadence left at the previous
// rate would.
void runtime_rate_changes() {
    const int rates[] = {120, 90, 60, 30, 60, 90, 120};
    for (const double refresh : {144.0, 240.0, 360.0}) for (const bool stale : {false, true}) {
        Source locked; locked.fps = refresh; locked.refresh_locked = true; Display display; display.refresh = refresh;
        std::vector<std::pair<int, double>> windows; int stage = 0;
        simulate(locked, display, {rates[0], capture_wgc_update_ticks(rates[0], refresh), refresh}, 7 * 20, 21, [&](int second, const Window& window, Control& control) {
            windows.push_back({control.target, window.fresh});
            if ((second + 1) % 20 == 0 && stage + 1 < int(std::size(rates))) {
                const int previous = control.target; control.target = rates[++stage];
                control.ticks = capture_wgc_update_ticks(stale ? previous : control.target, refresh);
            }
        });
        // The first full window at each new rate against the steady windows.
        std::map<int, double> steady; double worst_first = 1e9;
        for (size_t i = 3; i < windows.size(); ++i) {
            if (windows[i].first != windows[i - 1].first) continue;
            const double ratio = windows[i].second / windows[i].first;
            if (windows[i - 1].first != windows[i - 2].first) worst_first = std::min(worst_first, ratio);
            else if (!steady.count(windows[i].first) || ratio < steady[windows[i].first]) steady[windows[i].first] = ratio;
        }
        double floor = 1e9; for (const auto& [rate, ratio] : steady) floor = std::min(floor, ratio);
        if (!stale && worst_first < floor - .02) throw std::runtime_error("Rate change starved output at " + std::to_string(int(refresh)) + " Hz: " + std::to_string(worst_first) + " vs steady " + std::to_string(floor));
        if (stale && refresh == 240) CHECK(worst_first < .9); // 30 -> 60 on a 48 FPS producer.
        if (!stale) std::cout << "  rate changes 120>90>60>30>60>90>120 at " << int(refresh) << " Hz: first window after a change " << std::setprecision(3) << worst_first
                              << " of target (steady windows " << floor << ")\n";
    }
}
// Refresh changes and a monitor move recompute the ticks; the source keeps
// supplying what the new display allows.
void refresh_changes() {
    for (const int target : {30, 60, 90, 120}) {
        const double refreshes[] = {240, 144, 60, 360, 165, 120};
        Source source; source.fps = 150; source.random_jitter = .15; Display display; display.refresh = refreshes[0];
        std::vector<std::pair<double, double>> windows; int stage = 0;
        simulate(source, display, {target, capture_wgc_update_ticks(target, refreshes[0]), refreshes[0]}, 6 * 30, 5, [&](int second, const Window& window, Control& control) {
            windows.push_back({control.refresh, window.fresh});
            if ((second + 1) % 30 == 0 && stage + 1 < int(std::size(refreshes))) { control.refresh = refreshes[++stage]; control.ticks = capture_wgc_update_ticks(target, control.refresh); }
        });
        for (size_t i = 2; i < windows.size(); ++i) {
            if (windows[i].first != windows[i - 1].first) continue;
            const double expected = std::min({double(target), windows[i].first, 150.0});
            if (windows[i].second < expected * .95 - 1) throw std::runtime_error("Refresh " + std::to_string(int(windows[i].first)) + " Hz starved " + std::to_string(target) + " fps: " + std::to_string(windows[i].second));
        }
    }
    std::cout << "  refresh changes 240>144>60>360>165>120 Hz at 30/60/90/120 fps: no window starved\n";
}
// Without MinUpdateInterval WGC delivers every composition with new content:
// more copies, the same fresh frames.
void update_interval_unavailable() {
    for (const int target : {30, 60, 90, 120}) {
        Source locked; locked.fps = 240; locked.refresh_locked = true; Display display; display.refresh = 240;
        const auto ungated = simulate(locked, display, {target, 0, 240}, 120, 3), policy = simulate(locked, display, {target, capture_wgc_update_ticks(target, 240), 240}, 120, 3);
        CHECK(std::abs(ungated.fresh - policy.fresh) < target * .002 && ungated.delivered > 239 && ungated.delivered >= policy.delivered);
    }
    std::cout << "  MinUpdateInterval unavailable: every composition delivered, fresh unchanged\n";
}

// RecordingCapture hands the source every active-rate change at once.
class RateSpy final : public RecordingFrameSource {
    std::chrono::steady_clock::time_point next_ = std::chrono::steady_clock::now();
public:
    std::mutex mutex; std::vector<std::pair<int, std::chrono::steady_clock::time_point>> calls;
    bool acquire(CapturePixels& pixels, std::chrono::milliseconds timeout) override {
        const auto now = std::chrono::steady_clock::now();
        if (now < next_) { std::this_thread::sleep_for(std::min(timeout, std::chrono::duration_cast<std::chrono::milliseconds>(next_ - now) + std::chrono::milliseconds(1))); return false; }
        next_ += std::chrono::microseconds(1000000 / 240);
        pixels.width = 256; pixels.height = 144; pixels.stride = 256 * 4; pixels.bgra.assign(size_t(256) * 144 * 4, 64); return true;
    }
    bool eligible() const override { return true; }
    const char* name() const override { return "rate spy"; }
    void set_frame_rate(int fps) override { std::lock_guard lock(mutex); calls.push_back({fps, std::chrono::steady_clock::now()}); }
};
void capture_follows_active_rate() {
    RecordingCaptureConfig config; config.width = 256; config.height = 144; config.fps = 120; config.cpu_encoder = true;
    auto owned = std::make_unique<RateSpy>(); auto* spy = owned.get();
    RecordingCapture capture(config, {}, std::move(owned)); capture.start();
    std::this_thread::sleep_for(std::chrono::milliseconds(300));
    for (const int rate : {90, 60, 30, 60, 90, 120}) {
        const auto requested = std::chrono::steady_clock::now(); capture.request_frame_rate(rate);
        const auto deadline = requested + std::chrono::milliseconds(200); bool applied = false;
        while (!applied && std::chrono::steady_clock::now() < deadline) {
            { std::lock_guard lock(spy->mutex); for (const auto& [fps, at] : spy->calls) if (fps == rate && at >= requested) applied = true; }
            if (!applied) std::this_thread::sleep_for(std::chrono::milliseconds(2));
        }
        if (!applied) throw std::runtime_error("Source cadence did not follow the active rate " + std::to_string(rate));
        std::this_thread::sleep_for(std::chrono::milliseconds(300));
        std::lock_guard lock(spy->mutex); CHECK(spy->calls.back().first == rate); // Periodic reapply keeps the active rate.
    }
    CHECK(capture.stop());
    std::cout << "  RecordingCapture applies each active-rate change to the source at once\n";
}
}

int main(int argc, char** argv) {
    try {
        av_log_set_level(AV_LOG_ERROR);
        if (argc > 1 && std::string(argv[1]) == "--explore") return explore(argc > 2 ? std::atof(argv[2]) : 180, argc > 3);
        policy_table();
        safe_policy_quality();
        coarser_cadence_costs_timing();
        runtime_rate_changes();
        refresh_changes();
        update_interval_unavailable();
        capture_follows_active_rate();
        std::cout << "WGC cadence tests passed\n";
        return 0;
    } catch (const std::exception& error) { std::cerr << error.what() << "\n"; return 1; }
}
