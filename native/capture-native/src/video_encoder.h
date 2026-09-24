#pragma once

extern "C" {
#include <libavcodec/avcodec.h>
}
#include <functional>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace clypdat {
struct CodecContextDeleter { void operator()(AVCodecContext* value) const { avcodec_free_context(&value); } };
struct PacketDeleter { void operator()(AVPacket* value) const { av_packet_free(&value); } };
using CodecContext = std::unique_ptr<AVCodecContext, CodecContextDeleter>;
using Packet = std::unique_ptr<AVPacket, PacketDeleter>;

// Native-only injection seam. No callbacks or FFmpeg pointers cross the ABI.
struct CodecCalls {
    std::function<int(AVCodecContext*, const AVFrame*)> send = avcodec_send_frame;
    std::function<int(AVCodecContext*, AVPacket*)> receive = avcodec_receive_packet;
};

struct VideoEncoderConfig {
    int width = 0;
    int height = 0;
    int fps = 60;
    int bitrate_mbps = 20;
    std::string name = "libx264";
    bool low_power = false;
    // NV12 frames context: QSV frames for *_qsv encoders, D3D11 frames for the
    // other hardware encoders. Retained by the opened context.
    AVBufferRef* hardware_frames = nullptr;
    // EncoderPlan resource options (surfaces, delay, async_depth, ...), applied
    // after the vendor quality defaults. Empty leaves FFmpeg's own defaults.
    std::vector<std::pair<std::string, std::string>> resource_options;
    int codec_flags = 0; // OR'd into AVCodecContext::flags (AV_CODEC_FLAG_LOW_DELAY for AMF).
    // Hardware frames must come from hardware_frames itself. AMF asserts this
    // inside FFmpeg; checking first turns an abort into an exception.
    bool require_encoder_frames = false;
    // Copy every packet into an exact-size buffer before it leaves the
    // encoder. QSV allocates each packet at its VBV size and only shrinks
    // `size`, so history would otherwise pin the whole allocation.
    bool right_size_packets = false;
};

enum class SubmitStatus { Accepted, Busy };
struct SubmitResult { SubmitStatus status = SubmitStatus::Accepted; std::vector<Packet> packets; };

// Single encoding thread owns this object. Packets are transferred to history
// by value; FFmpeg retains each submitted reference-counted surface as needed.
class VideoEncoder {
public:
    explicit VideoEncoder(const VideoEncoderConfig& config, CodecCalls calls = {});
    // EAGAIN that draining cannot clear returns Busy: the frame was not
    // accepted, FFmpeg holds no reference to it and the encoder stays usable.
    SubmitResult try_submit(const AVFrame& frame);
    // Strict form: Busy is a failure that closes the encoder.
    std::vector<Packet> submit(const AVFrame& frame);
    // Packets the encoder has ready, without sending input.
    std::vector<Packet> drain_ready();
    std::vector<Packet> finish();
    const AVCodecContext& context() const { return *context_; }
    const std::vector<std::string>& unsupported_options() const { return unsupported_options_; }
private:
    int receive(std::vector<Packet>& packets);
    void send(const AVFrame* frame, std::vector<Packet>& packets);
    CodecContext context_;
    CodecCalls calls_;
    std::vector<std::string> unsupported_options_;
    bool require_encoder_frames_ = false;
    bool right_size_packets_ = false;
    bool finished_ = false;
    bool failed_ = false;
};

// Exact versions, matching the pinned SDK and shipped runtime.
bool runtime_versions_match();
}
