#pragma once
#include "recording_audio.h"

namespace clypdat {
// All audio placement uses the nearest frame on one absolute timeline. These
// conversions avoid multiplying long-running clocks before dividing them.
int64_t audio_frames(int64_t microseconds, int rate = 48000);
int64_t audio_time(int64_t frames, int rate = 48000);
int64_t audio_offset_time(int64_t start_us, int64_t frames, int rate = 48000);

// Single capture-thread owner. Device positions establish packet continuity;
// timestamps steer only soft clock compensation. Output is interleaved 48 kHz
// float PCM, with the original channel count and route identity.
class RecordingAudioTiming {
public:
    explicit RecordingAudioTiming(WasapiSource::Sink sink);
    ~RecordingAudioTiming();
    void submit(PcmBlock packet, uint64_t device_position, bool timestamp_valid = true);
    void finish();
private:
    struct State;
    std::unique_ptr<State> state_;
};
}
