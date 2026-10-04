#include "recording_audio_codec.h"
#include <algorithm>
#include <cmath>
#include <cwctype>
#include <deque>
#include <stdexcept>
extern "C" {
#include <libavutil/channel_layout.h>
#include <libswresample/swresample.h>
}

namespace clypdat {
namespace {
void check(int code, const char* operation) {
    if (code >= 0) return;
    char error[256]{}; av_strerror(code, error, sizeof(error));
    throw std::runtime_error(std::string(operation) + ": " + error);
}
bool equal(std::wstring_view a, std::wstring_view b) {
    return a.size() == b.size() && std::equal(a.begin(), a.end(), b.begin(),
        [](wchar_t x, wchar_t y) { return std::towlower(x) == std::towlower(y); });
}
constexpr float kAudibleLevel = .000001f;
}

AudioCodec parse_audio_codec(std::wstring_view name) {
    if (equal(name, L"AAC")) return AudioCodec::Aac;
    if (equal(name, L"Vorbis")) return AudioCodec::Vorbis;
    return AudioCodec::Opus;
}
const char* audio_codec_label(AudioCodec codec) {
    return codec == AudioCodec::Aac ? "AAC" : codec == AudioCodec::Vorbis ? "Vorbis" : "Opus";
}
bool audio_codec_needs_matroska(AudioCodec codec) { return codec == AudioCodec::Vorbis; }

CodecContext open_audio_encoder(AudioCodec codec, int channels, int bitrate_kbps) {
    const char* name = codec == AudioCodec::Aac ? "aac" : codec == AudioCodec::Vorbis ? "libvorbis" : "libopus";
    const auto* encoder = avcodec_find_encoder_by_name(name);
    if (!encoder) throw std::runtime_error(std::string(audio_codec_label(codec)) + " audio encoder unavailable");
    CodecContext context(avcodec_alloc_context3(encoder));
    if (!context) throw std::bad_alloc();
    // libopus takes interleaved samples only; AAC and Vorbis take planar.
    const void* supported = nullptr; int count = 0;
    check(avcodec_get_supported_config(context.get(), encoder, AV_CODEC_CONFIG_SAMPLE_FORMAT, 0, &supported, &count), "Query audio sample formats");
    const auto* formats = static_cast<const AVSampleFormat*>(supported);
    auto offered = [&](AVSampleFormat format) { return !formats || std::find(formats, formats + count, format) != formats + count; };
    if (offered(AV_SAMPLE_FMT_FLTP)) context->sample_fmt = AV_SAMPLE_FMT_FLTP;
    else if (offered(AV_SAMPLE_FMT_FLT)) context->sample_fmt = AV_SAMPLE_FMT_FLT;
    else throw std::runtime_error(std::string(audio_codec_label(codec)) + " encoder has no float input");
    context->sample_rate = 48000; context->time_base = {1, 48000};
    av_channel_layout_default(&context->ch_layout, channels);
    const int default_bitrate = codec == AudioCodec::Opus ? 128 : 192;
    context->bit_rate = int64_t(bitrate_kbps > 0 ? std::clamp(bitrate_kbps, 32, 320) : default_bitrate) * 1000;
    context->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
    check(avcodec_open2(context.get(), encoder, nullptr), "Open audio encoder");
    if (context->frame_size <= 0) throw std::runtime_error("Audio encoder has no fixed frame size");
    return context;
}

struct AudioLaneMixer::State {
    struct Lane { AudioLaneConfig config; std::map<int64_t, std::vector<float>> blocks; };
    struct Converter {
        SwrContext* swr = nullptr; std::string lane, logical; int rate = 0, channels = 0, output_channels = 0;
        int64_t next = 0; bool anchored = false;
        ~Converter() { swr_free(&swr); }
    };
    std::vector<AudioLaneConfig> configs;
    std::map<std::string, Lane> lanes;
    std::map<std::pair<std::string, uint64_t>, std::unique_ptr<Converter>> converters;
    std::map<std::string, int64_t> source_ends;
    std::map<std::string, uint64_t> source_generations;
    int block = 0; int64_t bound = 0, next = 0; bool anchored = false;
    uint64_t converted = 0;
    void append(Converter& source, int64_t start, const std::vector<float>& samples, int count) {
        converted += uint64_t(count);
        auto found = lanes.find(source.lane); if (found == lanes.end()) return;
        auto& lane = found->second; const int channels = lane.config.channels;
        if (source.output_channels != channels) return;
        const float gain = std::clamp(lane.config.gain, 0.f, 1.5f);
        auto& prior_end = source_ends[source.logical];
        for (int i = 0; i < count; ++i) {
            const auto position = start + i;
            if (!anchored || position < next || position < 0 || position < prior_end) continue;
            if (position - next > bound) throw std::runtime_error("Audio timeline bound exceeded");
            auto& target = lane.blocks[position / block];
            if (target.empty()) target.resize(size_t(block) * channels);
            for (int ch = 0; ch < channels; ++ch)
                target[size_t(position % block) * channels + ch] += samples[size_t(i) * channels + ch] * gain;
        }
        prior_end = (std::max)(prior_end, start + count);
    }
    void drain(Converter& source) {
        if (!source.anchored) return;
        const int capacity = swr_get_out_samples(source.swr, 0); check(capacity, "Measure audio resampler tail");
        if (!capacity) return;
        std::vector<float> samples(size_t(capacity) * source.output_channels);
        auto* output = reinterpret_cast<uint8_t*>(samples.data());
        const int count = swr_convert(source.swr, &output, capacity, nullptr, 0); check(count, "Drain audio resampler tail");
        append(source, source.next, samples, count); source.next += count;
    }
};
AudioLaneMixer::AudioLaneMixer(std::vector<AudioLaneConfig> lanes, int block, int64_t bound) : state_(std::make_unique<State>()) {
    if (block <= 0 || bound <= 0) throw std::invalid_argument("Invalid audio mixer grid");
    state_->block = block; state_->bound = bound; configure(std::move(lanes));
}
AudioLaneMixer::~AudioLaneMixer() = default;
void AudioLaneMixer::configure(std::vector<AudioLaneConfig> lanes) {
    auto& s = *state_;
    for (const auto& lane : lanes) if (lane.channels != 1 && lane.channels != 2) throw std::invalid_argument("Invalid output audio channels");
    std::map<std::string, State::Lane> kept;
    for (const auto& config : lanes) {
        auto found = s.lanes.find(config.key);
        State::Lane lane;
        if (found != s.lanes.end() && found->second.config.channels == config.channels) lane = std::move(found->second);
        lane.config = config; kept[config.key] = std::move(lane);
    }
    s.lanes = std::move(kept); s.configs = std::move(lanes);
    // Resamplers target their lane's channel count when they are created.
    for (auto it = s.converters.begin(); it != s.converters.end();) {
        auto lane = s.lanes.find(it->second->lane);
        if (lane == s.lanes.end() || lane->second.config.channels != it->second->output_channels) it = s.converters.erase(it); else ++it;
    }
}
const std::vector<AudioLaneConfig>& AudioLaneMixer::lanes() const { return state_->configs; }
void AudioLaneMixer::anchor(int64_t position) {
    auto& s = *state_; if (s.anchored) return;
    s.next = (position >= 0 ? position : position - s.block + 1) / s.block * s.block; s.anchored = true;
}
bool AudioLaneMixer::anchored() const { return state_->anchored; }
int64_t AudioLaneMixer::next() const { return state_->next; }
void AudioLaneMixer::submit(const PcmBlock& pcm, int64_t timestamp) {
    auto& s = *state_;
    auto lane = s.lanes.find(pcm.lane); if (lane == s.lanes.end()) return;
    const int channels = lane->second.config.channels;
    auto logical = pcm.lane + ":" + pcm.source; auto& latest = s.source_generations[logical];
    if (pcm.generation < latest) return;
    if (pcm.generation > latest) {
        for (auto it = s.converters.begin(); it != s.converters.end();) {
            if (it->first.first == logical) { s.drain(*it->second); it = s.converters.erase(it); } else ++it;
        }
        latest = pcm.generation;
    }
    auto& converter = s.converters[std::make_pair(logical, pcm.generation)];
    if (!converter) {
        converter = std::make_unique<State::Converter>();
        converter->lane = pcm.lane; converter->logical = logical; converter->rate = pcm.sample_rate;
        converter->channels = pcm.channels; converter->output_channels = channels;
        AVChannelLayout input{}, output{}; av_channel_layout_default(&input, pcm.channels); av_channel_layout_default(&output, channels);
        const int code = swr_alloc_set_opts2(&converter->swr, &output, AV_SAMPLE_FMT_FLT, 48000, &input, AV_SAMPLE_FMT_FLT, pcm.sample_rate, 0, nullptr);
        av_channel_layout_uninit(&input); av_channel_layout_uninit(&output);
        check(code, "Allocate audio resampler"); check(swr_init(converter->swr), "Initialize audio resampler");
    }
    auto& source = *converter;
    if (source.rate != pcm.sample_rate || source.channels != pcm.channels) throw std::runtime_error("Audio PCM format changed without generation");
    const int count = int(pcm.samples.size() / pcm.channels);
    const int capacity = swr_get_out_samples(source.swr, count); check(capacity, "Measure audio resampler output");
    std::vector<float> samples(size_t(capacity) * channels);
    const auto* input = reinterpret_cast<const uint8_t*>(pcm.samples.data()); auto* output = reinterpret_cast<uint8_t*>(samples.data());
    const int actual = swr_convert(source.swr, &output, capacity, &input, count); check(actual, "Convert audio");
    auto start = source.anchored ? source.next : timestamp;
    if (!source.anchored || std::abs(timestamp - source.next) > 4800) start = timestamp;
    source.anchored = true; source.next = start + actual;
    s.append(source, start, samples, actual);
}
void AudioLaneMixer::flush() { for (auto& [key, source] : state_->converters) state_->drain(*source); }
std::vector<AudioLaneMixer::Block> AudioLaneMixer::take() {
    auto& s = *state_; if (!s.anchored) throw std::logic_error("Audio mixer is not anchored");
    std::vector<Block> result; result.reserve(s.configs.size());
    const auto index = s.next / s.block;
    for (const auto& config : s.configs) {
        auto& lane = s.lanes.at(config.key); Block block;
        auto found = lane.blocks.find(index);
        if (found != lane.blocks.end()) { block.samples = std::move(found->second); lane.blocks.erase(found); }
        else block.samples.assign(size_t(s.block) * config.channels, 0.f);
        for (auto value : block.samples) if (std::abs(value) > kAudibleLevel) { block.audible = true; break; }
        // Blocks behind the grid can only come from a lane added mid-block.
        while (!lane.blocks.empty() && lane.blocks.begin()->first < index) lane.blocks.erase(lane.blocks.begin());
        result.push_back(std::move(block));
    }
    s.next += s.block;
    return result;
}
uint64_t AudioLaneMixer::converted_frames() const { return state_->converted; }

struct AudioTrackEncoder::State {
    struct Track {
        std::string key; int channels = 2; CodecContext codec;
        std::shared_ptr<const AVCodecParameters> parameters;
        // Audible flag of each frame still inside the encoder.
        std::deque<std::pair<int64_t, bool>> frames;
    };
    AudioCodec codec = AudioCodec::Opus; int bitrate_kbps = 0; bool mix = false; Sink sink;
    std::unique_ptr<AudioLaneMixer> mixer;
    std::map<std::string, Track> tracks;
    int block = 0, preroll = 0; bool finished = false;
    Track make(const std::string& key, int channels) {
        Track track; track.key = key; track.channels = channels; track.codec = open_audio_encoder(codec, channels, bitrate_kbps);
        auto* parameters = avcodec_parameters_alloc(); if (!parameters) throw std::bad_alloc();
        std::shared_ptr<AVCodecParameters> owned(parameters, [](AVCodecParameters* p) { avcodec_parameters_free(&p); });
        check(avcodec_parameters_from_context(parameters, track.codec.get()), "Copy audio encoder parameters");
        track.parameters = std::move(owned);
        return track;
    }
    void receive(Track& track) {
        for (;;) {
            Packet packet(av_packet_alloc()); if (!packet) throw std::bad_alloc();
            const int code = avcodec_receive_packet(track.codec.get(), packet.get());
            if (code == AVERROR(EAGAIN) || code == AVERROR_EOF) return;
            check(code, "Receive audio packet");
            const auto end = packet->pts + (packet->duration > 0 ? packet->duration : block);
            bool audible = false;
            for (const auto& [start, flag] : track.frames) if (flag && start < end && start + block > packet->pts) audible = true;
            while (!track.frames.empty() && track.frames.front().first + block <= packet->pts) track.frames.pop_front();
            sink(track.key, std::move(packet), audible);
        }
    }
    void send(Track& track, const float* samples, int64_t pts, bool audible) {
        AVFrame* raw = av_frame_alloc(); if (!raw) throw std::bad_alloc();
        struct Frame { AVFrame* p; ~Frame() { av_frame_free(&p); } } frame{raw};
        raw->format = track.codec->sample_fmt; raw->sample_rate = 48000; raw->nb_samples = block; raw->pts = pts;
        check(av_channel_layout_copy(&raw->ch_layout, &track.codec->ch_layout), "Set audio frame channels");
        check(av_frame_get_buffer(raw, 0), "Allocate audio frame");
        if (av_sample_fmt_is_planar(track.codec->sample_fmt)) {
            for (int ch = 0; ch < track.channels; ++ch) {
                auto* plane = reinterpret_cast<float*>(raw->data[ch]);
                for (int i = 0; i < block; ++i) plane[i] = samples[size_t(i) * track.channels + ch];
            }
        } else std::copy(samples, samples + size_t(block) * track.channels, reinterpret_cast<float*>(raw->data[0]));
        track.frames.emplace_back(pts, audible);
        int code = avcodec_send_frame(track.codec.get(), raw);
        if (code == AVERROR(EAGAIN)) { receive(track); code = avcodec_send_frame(track.codec.get(), raw); }
        check(code, "Send audio frame"); receive(track);
    }
    void encode_block() {
        const auto pts = mixer->next();
        auto blocks = mixer->take();
        std::vector<float> all(mix ? size_t(block) * 2 : 0, 0.f); bool any = false;
        const auto& lanes = mixer->lanes();
        for (size_t i = 0; i < lanes.size(); ++i) {
            const auto& samples = blocks[i].samples;
            send(tracks.at(lanes[i].key), samples.data(), pts, blocks[i].audible);
            any |= blocks[i].audible;
            if (!mix) continue;
            // Mono lanes enter the stereo mix at -3 dB per side, as FFmpeg's
            // default rematrix does.
            if (lanes[i].channels == 1) for (int f = 0; f < block; ++f) { const float value = samples[size_t(f)] * .70710678f; all[size_t(f) * 2] += value; all[size_t(f) * 2 + 1] += value; }
            else for (size_t f = 0; f < all.size(); ++f) all[f] += samples[f];
        }
        if (mix) send(tracks.at(kAllTracksKey), all.data(), pts, any);
    }
    void sync_tracks() {
        std::map<std::string, Track> kept;
        for (const auto& lane : mixer->lanes()) {
            auto found = tracks.find(lane.key);
            if (found != tracks.end() && found->second.channels == lane.channels) kept[lane.key] = std::move(found->second);
            else kept[lane.key] = make(lane.key, lane.channels);
        }
        if (mix) { auto found = tracks.find(kAllTracksKey); kept[kAllTracksKey] = found != tracks.end() ? std::move(found->second) : make(kAllTracksKey, 2); }
        tracks = std::move(kept);
    }
};
AudioTrackEncoder::AudioTrackEncoder(AudioCodec codec, int bitrate_kbps, std::vector<AudioLaneConfig> lanes, bool mix, int64_t bound, Sink sink) : state_(std::make_unique<State>()) {
    auto& s = *state_; s.codec = codec; s.bitrate_kbps = bitrate_kbps; s.mix = mix; s.sink = std::move(sink);
    auto probe = open_audio_encoder(codec, 2, bitrate_kbps); s.block = probe->frame_size;
    // Opus needs 80 ms to converge after a cut; the others need one frame of overlap.
    s.preroll = codec == AudioCodec::Opus ? 3840 : (std::max)(s.block, probe->initial_padding);
    s.mixer = std::make_unique<AudioLaneMixer>(std::move(lanes), s.block, bound);
    s.sync_tracks();
}
AudioTrackEncoder::~AudioTrackEncoder() = default;
void AudioTrackEncoder::configure(std::vector<AudioLaneConfig> lanes) { state_->mixer->configure(std::move(lanes)); state_->sync_tracks(); }
const std::vector<AudioLaneConfig>& AudioTrackEncoder::lanes() const { return state_->mixer->lanes(); }
void AudioTrackEncoder::anchor(int64_t position) { state_->mixer->anchor(position); }
bool AudioTrackEncoder::anchored() const { return state_->mixer->anchored(); }
int64_t AudioTrackEncoder::next() const { return state_->mixer->next(); }
int AudioTrackEncoder::block() const { return state_->block; }
int AudioTrackEncoder::preroll() const { return state_->preroll; }
void AudioTrackEncoder::submit(const PcmBlock& block, int64_t position) {
    if (state_->finished) throw std::logic_error("Audio encoder already finished");
    state_->mixer->submit(block, position);
}
void AudioTrackEncoder::encode_through(int64_t position) {
    auto& s = *state_; if (s.finished || !s.mixer->anchored()) return;
    while (s.mixer->next() + s.block <= position) s.encode_block();
}
void AudioTrackEncoder::finish(int64_t position) {
    auto& s = *state_; if (s.finished) return;
    s.mixer->flush();
    if (s.mixer->anchored()) while (s.mixer->next() < position) s.encode_block();
    s.finished = true;
    for (auto& [key, track] : s.tracks) {
        int code = avcodec_send_frame(track.codec.get(), nullptr);
        if (code == AVERROR(EAGAIN)) { s.receive(track); code = avcodec_send_frame(track.codec.get(), nullptr); }
        check(code, "Flush audio encoder"); s.receive(track);
    }
}
std::shared_ptr<const AVCodecParameters> AudioTrackEncoder::parameters(const std::string& key) const {
    auto found = state_->tracks.find(key); return found == state_->tracks.end() ? nullptr : found->second.parameters;
}
uint64_t AudioTrackEncoder::converted_frames() const { return state_->mixer->converted_frames(); }
}
