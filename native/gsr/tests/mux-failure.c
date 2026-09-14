#include "recorder/muxer.h"
#include "ffmpeg_utils.h"
#include <assert.h>
#include <libavformat/avformat.h>
#include <libavcodec/packet.h>
#include <stdio.h>
static void run(const char *path, bool inject_packet_failure) {
    gsr_recording_output output = {0};
    assert(avformat_alloc_output_context2(&output.av_format_context, NULL, "mpegts", path) == 0);
    AVStream *stream = avformat_new_stream(output.av_format_context, NULL); assert(stream);
    stream->codecpar->codec_type = AVMEDIA_TYPE_VIDEO; stream->codecpar->codec_id = AV_CODEC_ID_H264;
    stream->codecpar->width = 64; stream->codecpar->height = 64;
    assert(avio_open(&output.av_format_context->pb, path, AVIO_FLAG_WRITE) == 0);
    assert(avformat_write_header(output.av_format_context, NULL) == 0);
    if (inject_packet_failure) { output.av_format_context->opaque = (void*)2; gsr_av_format_context_mark_packet_written(output.av_format_context); assert(output.av_format_context->opaque == (void*)2); }
    else { unsigned char payload[32768] = {0}; avio_write(output.av_format_context->pb, payload, sizeof(payload)); }
    assert(!gsr_recording_output_stop(&output));
    assert(!output.av_format_context);
}
int main(void) { run("/dev/full", false); run("/dev/null", true); puts("PASS: full disk and earlier packet failures prevent successful finalization"); }
