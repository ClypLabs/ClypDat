#pragma once
#include <algorithm>
#include <cstdint>
#include <stdexcept>

namespace clypdat {
inline constexpr int64_t kReplayGopUs = 1000000;
// Three output intervals at the lowest supported rate (30 FPS). Shared by
// the recorder and stream-copy admission; not minutes of extra history.
inline constexpr int64_t kReplayKeyframeToleranceUs = 100000;
inline constexpr int64_t kReplayMaxLeadInUs = kReplayGopUs + kReplayKeyframeToleranceUs;
struct KeyframeCadenceError : std::runtime_error {
    KeyframeCadenceError() : std::runtime_error("Replay encoder did not provide a usable recent keyframe") {}
};
struct ReplayKeyframeHealth {
    int64_t last_pts_us=-1, gap_us=0, maximum_gap_us=0, watchdog_us=0;
    uint64_t requested=0, periodic_requested=0, emitted=0, recoveries=0;
    bool safe=false;
};
// Owned by the encoding thread. Decisions use output PTS, including duplicate
// frames. A Busy/dropped frame never calls accepted(), so its request persists.
class ReplayKeyframes {
    int64_t accepted_request_=-1;
public:
    ReplayKeyframeHealth health;
    void generation(int fps,int max_in_flight) {
        accepted_request_=-1; health.last_pts_us=-1; health.gap_us=0; health.safe=false;
        // Two GOP opportunities plus bounded asynchronous encoder occupancy.
        // At most one additional GOP: a longer delay is itself unhealthy.
        const auto delay=std::min(kReplayGopUs,
            (int64_t(std::max(1,max_in_flight))+1)*1000000/std::clamp(fps,30,120));
        health.watchdog_us=2*kReplayGopUs+std::max(kReplayKeyframeToleranceUs,delay);
    }
    bool due(int64_t pts) const { return accepted_request_<0 || pts-accepted_request_>=kReplayGopUs-1; }
    void accepted(int64_t pts,bool requested) {
        if(!requested)return;
        ++health.requested; if(accepted_request_>=0)++health.periodic_requested;
        accepted_request_=pts;
    }
    bool packet(int64_t pts,bool key) {
        if(key) {
            if(health.last_pts_us>=0)health.maximum_gap_us=std::max(health.maximum_gap_us,pts-health.last_pts_us);
            health.last_pts_us=pts; health.gap_us=0; ++health.emitted; health.safe=true;
        } else {
            health.gap_us=health.last_pts_us<0?0:std::max(int64_t(0),pts-health.last_pts_us);
            health.maximum_gap_us=std::max(health.maximum_gap_us,health.gap_us);
            if(health.last_pts_us<0||health.gap_us>health.watchdog_us)health.safe=false;
        }
        return health.safe;
    }
};
}
