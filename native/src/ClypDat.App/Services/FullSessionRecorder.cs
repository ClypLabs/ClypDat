using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ClypDat.Core.Settings;
using ClypDat.Capture.Abstractions;
using FFmpeg.AutoGen;
using NAudio.Wave;

namespace ClypDat.App.Services;

internal sealed record LiveAudioPacket(AudioCapturePipeline.AudioCaptureKind Kind, string SourceKey,
    Guid SourceId, WaveFormat Format, byte[] Buffer, int Count, DateTime StartUtc);

internal sealed record SessionAudioTrack(AudioCapturePipeline.AudioCaptureKind Kind, string SourceKey, string Label, int Channels, float Gain)
{
    internal static SessionAudioTrack[] FromConfig(ReplayBufferConfig config)
    {
        var tracks = new List<SessionAudioTrack> { new(AudioCapturePipeline.AudioCaptureKind.Game, "", "Game Audio", 2, Math.Clamp(config.GameAudioVolumePercent, 0, 150) / 100f) };
        var apps = AudioProcessIdentity.NormalizeDictionary(config.AdditionalAudioProcesses);
        var ordered = AudioProcessIdentity.OrderForRecording(AudioProcessIdentity.NormalizeList(config.ChatAudioProcessNames).Concat(apps.Keys).Distinct(StringComparer.OrdinalIgnoreCase)).ToArray();
        void AddApps(IEnumerable<string> names)
        {
            foreach (var name in names)
                tracks.Add(new(AudioCapturePipeline.AudioCaptureKind.Chat, name, name, 2,
                    (AudioProcessIdentity.TryGetValue(apps, name, out var gain) ? Math.Clamp(gain, 0, 150) : 100) / 100f));
        }
        AddApps(ordered.Where(AudioProcessIdentity.IsSocial));
        var mics = config.MicrophoneDeviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        for (var i = 0; i < mics.Length; i++)
            tracks.Add(new(AudioCapturePipeline.AudioCaptureKind.Microphone, mics[i], mics.Length == 1 ? "Microphone" : $"Microphone {i + 1}",
                string.Equals(config.MicrophoneChannelMode, "Stereo", StringComparison.OrdinalIgnoreCase) ? 2 : 1, Math.Clamp(config.MicrophoneVolumePercent, 0, 150) / 100f));
        AddApps(ordered.Where(name => !AudioProcessIdentity.IsSocial(name)));
        return tracks.ToArray();
    }
}

// All muxing, conversion and AAC work belongs to this worker. Producers only
// clone packets into a bounded queue; overflow fails the session, never replay.
internal sealed unsafe class FullSessionRecorder : IDisposable
{
    private const int SampleRate = 48000;
    private const int BlockSize = 1024;
    internal const long DefaultQueueLimit = 64 * 1024 * 1024;
    private readonly BlockingCollection<Work> _queue = new(8192);
    private readonly object _gate = new();
    private readonly long _queueLimit;
    private long _queuedBytes;
    private bool _closed;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ReplayBufferConfig _config;
    private readonly Action<FullSessionStatus> _changed;
    private AVCodecParameters* _videoParameters;
    private readonly AVRational _videoTimeBase;
    private AVFormatContext* _format;
    private AVStream* _video;
    private AVPacket* _audioPacket;
    private RecordingFileOwnership? _ownership;
    private readonly List<AudioLane> _lanes = new();
    private readonly Dictionary<Guid, SourceConverter> _sources = new();
    private readonly List<LiveAudioPacket> _earlyAudio = new();
    private long _earlyBytes;
    private DateTime? _anchor;
    private long _videoOrigin;
    private long _videoEnd;
    private FullSessionStatus _status = new(FullSessionState.Starting);
    internal ReplayBufferConfig Configuration => _config;
    internal string OutputPath { get; }
    internal DateTime? StartUtc => _anchor;
    internal DateTime StartWallUtc { get; private set; }
    internal FullSessionStatus Status => Volatile.Read(ref _status);
    internal Task Completion => _completion.Task;
    internal long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    private sealed record Work(nint Video, LiveAudioPacket? Audio, DateTime Timestamp, long Bytes);
    private sealed class AudioLane(SessionAudioTrack track)
    {
        internal readonly SessionAudioTrack Track = track;
        internal AVCodecContext* Codec;
        internal AVStream* Stream;
        internal AVFrame* Frame;
        internal long NextSample;
        internal readonly Dictionary<long, float[]> Blocks = new();
    }
    private sealed class SourceConverter
    {
        internal SwrContext* Context;
        internal long NextSample;
        internal DateTime LastSeen;
        internal AudioLane Lane = null!;
    }

    internal FullSessionRecorder(ReplayBufferConfig config, AVCodecContext* videoCodec, Action<FullSessionStatus> changed, long queueLimit = DefaultQueueLimit)
    {
        _config = config with
        {
            FullSessionContainer = FullSessionFormat.Normalize(config.FullSessionContainer),
            ChatAudioProcessNames = config.ChatAudioProcessNames.ToArray(),
            MicrophoneDeviceIds = config.MicrophoneDeviceIds.ToArray(),
            AdditionalAudioProcesses = config.AdditionalAudioProcesses?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
        };
        _changed = changed;
        _queueLimit = queueLimit;
        _videoTimeBase = videoCodec->time_base;
        var label = string.IsNullOrWhiteSpace(config.GameDisplayName) ? "Session" : $"Session - {config.GameDisplayName}";
        OutputPath = ClipFileNaming.BuildUniquePath(config.FullSessionRecordingFolder,
            ClipFileNaming.BuildFileName(label, DateTime.Now, FullSessionFormat.Extension(config.FullSessionContainer), config.ClipFileNameScheme, config.CustomClipFileNameTemplate, config.GameDisplayName));
        try
        {
            _videoParameters = ffmpeg.avcodec_parameters_alloc();
            if (_videoParameters is null) throw new OutOfMemoryException();
            Check(ffmpeg.avcodec_parameters_from_context(_videoParameters, videoCodec));
            if (videoCodec->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11) _videoParameters->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            Publish(FullSessionState.Starting);
            new Thread(Run) { IsBackground = true, Name = "ClypDat full session mux" }.Start();
        }
        catch
        {
            var parameters = _videoParameters;
            ffmpeg.avcodec_parameters_free(&parameters);
            throw;
        }

    }

    internal void EnqueueVideo(AVPacket* packet, DateTime timestamp)
    {
        lock (_gate)
        {
            if (_closed) return;
            if (_queuedBytes + packet->size > _queueLimit) { FailQueue(); return; }
            var copy = ffmpeg.av_packet_clone(packet);
            if (copy is null) { FailQueue(); return; }
            if (!Offer(new((nint)copy, null, timestamp, packet->size))) ffmpeg.av_packet_free(&copy);
        }
    }
    internal void EnqueueAudio(LiveAudioPacket packet)
    {
        lock (_gate)
        {
            if (_closed) return;
            try
            {
                if (_queuedBytes + packet.Count > _queueLimit) { FailQueue(); return; }
                Offer(new(0, packet with { Buffer = packet.Buffer.AsSpan(0, packet.Count).ToArray() }, packet.StartUtc, packet.Count));
            }
            catch (Exception error) { FailQueue($"Full Session audio enqueue failed: {error.Message}"); }
        }
    }
    private bool Offer(Work work)
    {
        Interlocked.Add(ref _queuedBytes, work.Bytes);
        if (_queue.TryAdd(work)) return true;
        Interlocked.Add(ref _queuedBytes, -work.Bytes);
        FailQueue();
        return false;
    }
    private void FailQueue(string failure = "Full Session queue overflow; replay recording continues.")
    {
        _closed = true;
        // Report on a separate thread even if the mux worker is stuck in I/O.
        // Health callbacks and logging never run on the capture callback.
        var status = new FullSessionStatus(FullSessionState.Failed, OutputPath, failure);
        Volatile.Write(ref _status, status);
        _queue.CompleteAdding();
        ThreadPool.QueueUserWorkItem(_ => Notify(status));
    }
    internal void Complete()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _queue.CompleteAdding();
        }
    }
    public void Dispose() { Complete(); _completion.Task.GetAwaiter().GetResult(); }
    private void Publish(FullSessionState state, string failure = "")
    {
        var status = new FullSessionStatus(state, OutputPath, failure);
        Volatile.Write(ref _status, status);
        Notify(status);
    }
    private void Notify(FullSessionStatus status)
    {
        try { _changed(status); } catch (Exception error) { AppLog.Error("Full Session status delivery failed.", error); }
    }
    private static void Check(int result)
    {
        if (result < 0) throw new IOException($"Full Session FFmpeg error {result}.");
    }
    private void Open()
    {
        Directory.CreateDirectory(_config.FullSessionRecordingFolder);
        _ownership = RecordingFileOwnership.Acquire(OutputPath);
        StartWallUtc = DateTime.UtcNow;
        ClipInfoSidecar.Save(_config.LibraryFolder, OutputPath, new ClipInfo(_config.GameDisplayName, null, $"Session - {_config.GameDisplayName}", StartWallUtc, CaptureSource: _config.CaptureSource));
        AVFormatContext* format = null;
        Check(ffmpeg.avformat_alloc_output_context2(&format, null, FullSessionFormat.Normalize(_config.FullSessionContainer) == "MKV" ? "matroska" : "mp4", OutputPath));
        _format = format;
        if (_format is null) throw new OutOfMemoryException();
        _format->max_interleave_delta = 1_000_000;
        _format->flags |= ffmpeg.AVFMT_FLAG_FLUSH_PACKETS;
        ffmpeg.av_dict_set(&_format->metadata, "title", $"Session - {_config.GameDisplayName}", 0);
        ffmpeg.av_dict_set(&_format->metadata, "comment", ClipMetadataTagger.BuildCommentValue("Native"), 0);
        ffmpeg.av_dict_set(&_format->metadata, "creation_time", StartWallUtc.ToString("O"), 0);
        _video = ffmpeg.avformat_new_stream(_format, null);
        if (_video is null) throw new OutOfMemoryException();
        Check(ffmpeg.avcodec_parameters_copy(_video->codecpar, _videoParameters));
        _video->time_base = _videoTimeBase;
        _audioPacket = ffmpeg.av_packet_alloc();
        if (_audioPacket is null) throw new OutOfMemoryException();
        foreach (var track in SessionAudioTrack.FromConfig(_config))
        {
            var lane = new AudioLane(track);
            _lanes.Add(lane);
            var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            lane.Codec = ffmpeg.avcodec_alloc_context3(codec);
            if (codec is null || lane.Codec is null) throw new InvalidOperationException("AAC encoder unavailable.");
            lane.Codec->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            lane.Codec->sample_rate = SampleRate;
            lane.Codec->bit_rate = 192000;
            lane.Codec->time_base = new AVRational { num = 1, den = SampleRate };
            ffmpeg.av_channel_layout_default(&lane.Codec->ch_layout, track.Channels);
            lane.Codec->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            Check(ffmpeg.avcodec_open2(lane.Codec, codec, null));
            if (lane.Codec->frame_size != BlockSize) throw new InvalidOperationException("Unexpected AAC frame size.");
            lane.Stream = ffmpeg.avformat_new_stream(_format, null);
            if (lane.Stream is null) throw new OutOfMemoryException();
            Check(ffmpeg.avcodec_parameters_from_context(lane.Stream->codecpar, lane.Codec));
            lane.Stream->time_base = lane.Codec->time_base;
            ffmpeg.av_dict_set(&lane.Stream->metadata, "title", track.Label, 0);
            ffmpeg.av_dict_set(&lane.Stream->metadata, "handler_name", track.Label, 0);
            lane.Frame = ffmpeg.av_frame_alloc();
            if (lane.Frame is null) throw new OutOfMemoryException();
            lane.Frame->format = (int)lane.Codec->sample_fmt;
            lane.Frame->sample_rate = SampleRate;
            lane.Frame->nb_samples = BlockSize;
            Check(ffmpeg.av_channel_layout_copy(&lane.Frame->ch_layout, &lane.Codec->ch_layout));
            Check(ffmpeg.av_frame_get_buffer(lane.Frame, 0));
        }
        AVIOContext* io = null;
        Check(ffmpeg.avio_open(&io, OutputPath, ffmpeg.AVIO_FLAG_WRITE));
        _format->pb = io;
        AVDictionary* options = null;
        try
        {
            if (FullSessionFormat.Normalize(_config.FullSessionContainer) == "MKV") ffmpeg.av_dict_set(&options, "cluster_time_limit", "1000", 0);
            else ffmpeg.av_dict_set(&options, "movflags", "frag_keyframe+empty_moov+default_base_moof", 0);
            Check(ffmpeg.avformat_write_header(_format, &options));
        }
        finally { ffmpeg.av_dict_free(&options); }
        Flush();
    }
    private void Run()
    {
        var headerWritten = false;
        try
        {
            Open(); headerWritten = true;
            var lastFlush = Environment.TickCount64;
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (work.Video != 0) WriteVideo((AVPacket*)work.Video, work.Timestamp);
                    else if (work.Audio is { } audio) AddAudio(audio);
                    EncodeAudio(Math.Max(0, _videoEnd - SampleRate / 2));
                    if (Environment.TickCount64 - lastFlush >= 250) { Flush(); lastFlush = Environment.TickCount64; }
                }
                finally { Release(work); }
            }
            if (Status.State != FullSessionState.Failed) Publish(FullSessionState.Stopping);
            FlushSources();
            EncodeAudio((_videoEnd + BlockSize - 1) / BlockSize * BlockSize);
            foreach (var lane in _lanes) { Check(ffmpeg.avcodec_send_frame(lane.Codec, null)); DrainAudio(lane); }
            Check(ffmpeg.av_write_trailer(_format)); headerWritten = false;
            Flush();
        }
        catch (Exception error)
        {
            Publish(FullSessionState.Failed, error.Message);
            AppLog.Error("Full Session stopped; replay recording continues.", error);
            if (headerWritten) { try { ffmpeg.av_write_trailer(_format); } catch { } }
        }
        finally
        {
            Complete();
            while (_queue.TryTake(out var pending)) Release(pending);
            try { CloseNative(); }
            catch (Exception error) { Publish(FullSessionState.Failed, error.Message); }
            finally
            {
                try
                {
                    if (Status.State == FullSessionState.Failed && _anchor is not null)
                    {
                        try { File.WriteAllText(FullSessionRecovery.Marker(OutputPath), "Interrupted Full Session"); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    }
                    _ownership?.Dispose();
                }
                finally
                {
                    if (Status.State != FullSessionState.Failed) Publish(FullSessionState.Completed);
                    else Publish(FullSessionState.Failed, Status.Failure);
                    _completion.TrySetResult();
                }
            }
        }
    }
    private void Release(Work work)
    {
        if (work.Video != 0) { var packet = (AVPacket*)work.Video; ffmpeg.av_packet_free(&packet); }
        Interlocked.Add(ref _queuedBytes, -work.Bytes);
    }
    private void WriteVideo(AVPacket* packet, DateTime timestamp)
    {
        if (_anchor is null)
        {
            _anchor = timestamp;
            _videoOrigin = packet->pts;
            StartWallUtc = DateTime.UtcNow - (MonotonicClock.UtcNow - timestamp);
            ClipInfoSidecar.Save(_config.LibraryFolder, OutputPath, new ClipInfo(_config.GameDisplayName, null, $"Session - {_config.GameDisplayName}", StartWallUtc, CaptureSource: _config.CaptureSource));
            if (Status.State != FullSessionState.Failed) Publish(FullSessionState.Recording);
            foreach (var audio in _earlyAudio) AddAudio(audio);
            _earlyAudio.Clear(); _earlyBytes = 0;
        }
        packet->pts -= _videoOrigin;
        if (packet->dts != ffmpeg.AV_NOPTS_VALUE) packet->dts -= _videoOrigin;
        var duration = packet->duration > 0 ? packet->duration : Math.Max(1, ffmpeg.av_rescale_q(1, new AVRational { num = 1, den = Math.Max(1, _config.FrameRate) }, _videoTimeBase));
        _videoEnd = Math.Max(_videoEnd, ffmpeg.av_rescale_q(packet->pts + duration, _videoTimeBase, new AVRational { num = 1, den = SampleRate }));
        ffmpeg.av_packet_rescale_ts(packet, _videoTimeBase, _video->time_base);
        packet->stream_index = _video->index;
        Check(ffmpeg.av_interleaved_write_frame(_format, packet));
    }
    private void AddAudio(LiveAudioPacket audio)
    {
        if (_anchor is null)
        {
            if ((_earlyBytes += audio.Count) > 8 * 1024 * 1024) throw new IOException("Full Session received audio without video.");
            _earlyAudio.Add(audio); return;
        }
        var lane = _lanes.FirstOrDefault(lane => lane.Track.Kind == audio.Kind &&
            (audio.Kind == AudioCapturePipeline.AudioCaptureKind.Game || string.Equals(lane.Track.SourceKey, audio.SourceKey, StringComparison.OrdinalIgnoreCase)));
        if (lane is null) return;
        var observed = (long)Math.Round((audio.StartUtc - _anchor.Value).TotalSeconds * SampleRate);
        if (observed > _videoEnd + 10 * SampleRate) throw new IOException("Full Session audio exceeded the video queue limit.");
        if (!_sources.TryGetValue(audio.SourceId, out var source))
        {
            source = new SourceConverter { NextSample = observed, Lane = lane };
            _sources.Add(audio.SourceId, source);
            var inputLayout = new AVChannelLayout();
            ffmpeg.av_channel_layout_default(&inputLayout, audio.Format.Channels);
            SwrContext* converter = null;
            try { Check(ffmpeg.swr_alloc_set_opts2(&converter, &lane.Codec->ch_layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, SampleRate,
                &inputLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, audio.Format.SampleRate, 0, null)); }
            finally { ffmpeg.av_channel_layout_uninit(&inputLayout); }
            source.Context = converter;
            Check(ffmpeg.swr_init(converter));
        }
        source.LastSeen = audio.StartUtc;
        var error = observed - source.NextSample;
        if (Math.Abs(error) > SampleRate / 10)
        {
            // A missing device resumes its existing lane at the current clock.
            source.NextSample = observed;
            ffmpeg.swr_close(source.Context); Check(ffmpeg.swr_init(source.Context));
        }
        else Check(ffmpeg.swr_set_compensation(source.Context, (int)Math.Clamp(error / 8, -240, 240), SampleRate));
        var input = Decode(audio);
        var inputFrames = input.Length / audio.Format.Channels;
        var capacity = ffmpeg.swr_get_out_samples(source.Context, inputFrames);
        Check(capacity);
        var output = new float[capacity * lane.Track.Channels];
        int frames;
        fixed (float* from = input)
        fixed (float* to = output)
        {
            byte* fromBytes = (byte*)from; byte* toBytes = (byte*)to;
            frames = ffmpeg.swr_convert(source.Context, &toBytes, capacity, &fromBytes, inputFrames);
        }
        Check(frames);
        Mix(source, output, frames);
        foreach (var stale in _sources.Where(pair => audio.StartUtc - pair.Value.LastSeen > TimeSpan.FromSeconds(15)).Select(pair => pair.Key).ToArray())
        {
            var context = _sources[stale].Context; ffmpeg.swr_free(&context); _sources.Remove(stale);
        }
    }
    private static void Mix(SourceConverter source, float[] output, int frames)
    {
        var lane = source.Lane;
        for (var i = 0; i < frames; i++)
        {
            var position = source.NextSample + i;
            if (position < lane.NextSample) continue;
            var block = position / BlockSize;
            if (!lane.Blocks.TryGetValue(block, out var samples))
            {
                if (lane.Blocks.Count >= 512) throw new IOException("Full Session audio mix queue overflow.");
                lane.Blocks[block] = samples = new float[BlockSize * lane.Track.Channels];
            }
            var offset = (int)(position % BlockSize) * lane.Track.Channels;
            for (var ch = 0; ch < lane.Track.Channels; ch++) samples[offset + ch] += output[i * lane.Track.Channels + ch] * lane.Track.Gain;
        }
        source.NextSample += frames;
    }
    private void FlushSources()
    {
        foreach (var source in _sources.Values)
        {
            var capacity = ffmpeg.swr_get_out_samples(source.Context, 0);
            Check(capacity);
            var output = new float[Math.Max(1, capacity) * source.Lane.Track.Channels];
            int frames;
            fixed (float* samples = output)
            {
                byte* bytes = (byte*)samples;
                frames = ffmpeg.swr_convert(source.Context, &bytes, capacity, null, 0);
            }
            Check(frames);
            Mix(source, output, frames);
        }
    }
    private static float[] Decode(LiveAudioPacket audio)
    {
        var bytes = audio.Format.BitsPerSample / 8;
        var samples = new float[audio.Count / bytes];
        var floating = AudioSampleFormat.IsFloat32(audio.Format);
        if (!floating && AudioSampleFormat.ResolveEncoding(audio.Format) != WaveFormatEncoding.Pcm) throw new IOException("Unsupported Full Session audio sample format.");
        for (var i = 0; i < samples.Length; i++)
        {
            var offset = i * bytes;
            var value = floating ? BitConverter.ToSingle(audio.Buffer, offset) : bytes switch
            {
                1 => (audio.Buffer[offset] - 128) / 128f,
                2 => BitConverter.ToInt16(audio.Buffer, offset) / 32768f,
                3 => ((audio.Buffer[offset] << 8 | audio.Buffer[offset + 1] << 16 | audio.Buffer[offset + 2] << 24) >> 8) / 8388608f,
                4 => BitConverter.ToInt32(audio.Buffer, offset) / 2147483648f,
                _ => throw new IOException("Unsupported Full Session PCM format.")
            };
            samples[i] = float.IsFinite(value) ? value : 0;
        }
        return samples;
    }
    private void EncodeAudio(long until)
    {
        // Round-robin keeps interleaving bounded even when draining a backlog.
        while (_lanes.Count > 0 && _lanes[0].NextSample + BlockSize <= until)
        foreach (var lane in _lanes)
        {
            Check(ffmpeg.av_frame_make_writable(lane.Frame));
            lane.Blocks.Remove(lane.NextSample / BlockSize, out var samples);
            for (var ch = 0; ch < lane.Track.Channels; ch++)
            {
                var plane = (float*)lane.Frame->extended_data[ch];
                for (var i = 0; i < BlockSize; i++) plane[i] = samples is null ? 0 : Math.Clamp(samples[i * lane.Track.Channels + ch], -1f, 1f);
            }
            lane.Frame->pts = lane.NextSample;
            Check(ffmpeg.avcodec_send_frame(lane.Codec, lane.Frame));
            DrainAudio(lane);
            lane.NextSample += BlockSize;
        }
    }
    private void DrainAudio(AudioLane lane)
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(lane.Codec, _audioPacket);
            if (result == ffmpeg.AVERROR(ffmpeg.EAGAIN) || result == ffmpeg.AVERROR_EOF) break;
            Check(result);
            ffmpeg.av_packet_rescale_ts(_audioPacket, lane.Codec->time_base, lane.Stream->time_base);
            _audioPacket->stream_index = lane.Stream->index;
            Check(ffmpeg.av_interleaved_write_frame(_format, _audioPacket));
        }
    }
    private void Flush()
    {
        ffmpeg.avio_flush(_format->pb);
        Check(_format->pb->error);
    }
    private void CloseNative()
    {
        foreach (var source in _sources.Values) { var context = source.Context; ffmpeg.swr_free(&context); }
        foreach (var lane in _lanes)
        {
            var frame = lane.Frame; ffmpeg.av_frame_free(&frame);
            var codec = lane.Codec; ffmpeg.avcodec_free_context(&codec);
        }
        var packet = _audioPacket; ffmpeg.av_packet_free(&packet);
        var parameters = _videoParameters; ffmpeg.avcodec_parameters_free(&parameters);
        var closeResult = 0;
        if (_format is not null)
        {
            if (_format->pb is not null) closeResult = ffmpeg.avio_closep(&_format->pb);
            ffmpeg.avformat_free_context(_format);
        }
        Check(closeResult);
    }
}
