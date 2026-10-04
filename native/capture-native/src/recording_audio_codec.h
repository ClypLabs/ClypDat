#pragma once
#include "recording_audio.h"
#include "video_encoder.h"
#include <map>
#include <string_view>

namespace clypdat {
// Unknown or empty names select Opus.
AudioCodec parse_audio_codec(std::wstring_view name);
const char* audio_codec_label(AudioCodec codec);
bool audio_codec_needs_matroska(AudioCodec codec);
// One 48 kHz track: Opus at 128 kb/s, AAC or Vorbis at 192 kb/s. Global
// headers are always requested so packets can be muxed into MP4 or Matroska.
CodecContext open_audio_encoder(AudioCodec codec, int channels, int bitrate_kbps = 0);

// Track key and title of the stereo mix of every lane.
inline constexpr const char* kAllTracksKey = "*all";
inline constexpr const char* kAllTracksTitle = "All Tracks";

// Places PCM from any number of sources onto per-lane 48 kHz timelines, cut
// into blocks of one encoder frame. Each source generation has its own
// resampler; a newer generation replaces older ones of the same source and
// overlapping samples are never counted twice.
class AudioLaneMixer {
public:
    // Samples further than bound frames past next() are a timeline error.
    AudioLaneMixer(std::vector<AudioLaneConfig> lanes, int block, int64_t bound);
    ~AudioLaneMixer();
    AudioLaneMixer(const AudioLaneMixer&) = delete;
    AudioLaneMixer& operator=(const AudioLaneMixer&) = delete;
    // Keeps state for lanes whose key and channel count are unchanged.
    void configure(std::vector<AudioLaneConfig> lanes);
    const std::vector<AudioLaneConfig>& lanes() const;
    // Starts the block grid at the block containing position.
    void anchor(int64_t position);
    bool anchored() const;
    // First frame not yet taken. Earlier samples are dropped.
    int64_t next() const;
    // position is the 48 kHz frame of block.start_us on the caller's timeline.
    void submit(const PcmBlock& block, int64_t position);
    // Drains every resampler tail into the lanes.
    void flush();
    struct Block { std::vector<float> samples; bool audible = false; };
    // The block at next() for each lane, in lane order, gain applied and
    // zero where nothing arrived. Advances next() by one block.
    std::vector<Block> take();
    uint64_t converted_frames() const;
private:
    struct State; std::unique_ptr<State> state_;
};

// Encodes every lane, and optionally a stereo mix of all lanes, in lockstep
// on one block grid. Packet timestamps are 48 kHz frames on the mixer's
// timeline.
class AudioTrackEncoder {
public:
    using Sink = std::function<void(const std::string& key, Packet packet, bool audible)>;
    AudioTrackEncoder(AudioCodec codec, int bitrate_kbps, std::vector<AudioLaneConfig> lanes, bool mix, int64_t bound, Sink sink);
    ~AudioTrackEncoder();
    AudioTrackEncoder(const AudioTrackEncoder&) = delete;
    AudioTrackEncoder& operator=(const AudioTrackEncoder&) = delete;
    void configure(std::vector<AudioLaneConfig> lanes);
    const std::vector<AudioLaneConfig>& lanes() const;
    void anchor(int64_t position);
    bool anchored() const;
    int64_t next() const;
    int block() const;
    // Samples a decoder must see before a cut point to converge.
    int preroll() const;
    void submit(const PcmBlock& block, int64_t position);
    // Encodes whole blocks that end at or before position.
    void encode_through(int64_t position);
    // Drains resamplers, encodes through position (padding the last block
    // with silence), then drains every encoder. No input is accepted after.
    void finish(int64_t position);
    // Current parameters of a track. Replaced when a lane changes channels.
    std::shared_ptr<const AVCodecParameters> parameters(const std::string& key) const;
    uint64_t converted_frames() const;
private:
    struct State; std::unique_ptr<State> state_;
};
}
