using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;
using D3D11Device = Vortice.Direct3D11.ID3D11Device;
using D3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace ClypDat.App.Services;

// A deliberately small MFT host for the replay path.  Capture owns the D3D11
// texture pool; this class owns the Media Foundation transform and retains an
// AVFrame until the MFT has returned the matching encoded sample.  That keeps
// a reusable texture from being overwritten while the driver still reads it.
internal sealed unsafe class MediaFoundationH264Encoder : IDisposable
{
    private const uint HardwareAndSorted = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter);
    private const int EventNoWait = 0x00000001;
    private const int MfENoEventsAvailable = unchecked((int)0xC00D3E80);
    private static readonly Guid D3D11Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    private readonly D3D11Device _device;
    private readonly int _frameRate;
    private readonly ConcurrentDictionary<nint, D3D11Texture2D> _textureWrappers = new();
    private readonly object _disposeGate = new();
    private IMFDXGIDeviceManager? _deviceManager;
    private IMFTransform? _transform;
    private IMFMediaEventGenerator? _events;
    private bool _async;
    private bool _started;
    private bool _disposed;

    private MediaFoundationH264Encoder(D3D11Device device, int frameRate)
    {
        _device = device;
        _frameRate = Math.Clamp(frameRate, 30, 144);
    }

    public string Name { get; private set; } = "Media Foundation H.264 MFT";
    public byte[] SequenceHeader { get; private set; } = Array.Empty<byte>();
    public string? Failure { get; private set; }
    public bool IsFailed => Failure is not null;

    public static bool TryCreate(
        D3D11Device device,
        int width,
        int height,
        int frameRate,
        int bitrate,
        out MediaFoundationH264Encoder? encoder,
        out string detail)
    {
        encoder = null;
        var candidate = new MediaFoundationH264Encoder(device, frameRate);
        try
        {
            candidate.Initialize(width, height, bitrate);
            encoder = candidate;
            detail = candidate.Name;
            return true;
        }
        catch (Exception error)
        {
            detail = error.Message;
            candidate.Dispose();
            return false;
        }
    }

    private void Initialize(int width, int height, int bitrate)
    {
        MediaFactory.MFStartup().CheckError();
        _started = true;
        _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
        _deviceManager.ResetDevice(_device).CheckError();

        var input = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.NV12
        };
        var output = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype = VideoFormatGuids.H264
        };

        using var activations = MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoEncoder,
            HardwareAndSorted,
            input,
            output);

        Exception? lastError = null;
        foreach (var activation in activations)
        {
            IMFTransform? transform = null;
            IMFMediaEventGenerator? events = null;
            try
            {
                transform = activation.ActivateObject<IMFTransform>();
                var friendlyName = TryFriendlyName(activation);
                var async = TryGetUInt32(transform.Attributes, TransformAttributeKeys.TransformAsync) != 0;
                if (async)
                {
                    // Hardware encoders can expose the asynchronous MFT contract
                    // locked.  Unlocking is explicitly required before clients may
                    // feed ProcessInput/ProcessOutput themselves.
                    transform.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true).CheckError();
                    events = transform.QueryInterface<IMFMediaEventGenerator>();
                }
                ConfigureTransform(transform, width, height, bitrate);

                var outputType = transform.GetOutputCurrentType(0);
                try
                {
                    SequenceHeader = BuildAvcConfigurationRecord(TryGetBlob(outputType, MediaTypeAttributeKeys.MpegSequenceHeader));
                }
                finally
                {
                    outputType.Dispose();
                }

                if (SequenceHeader.Length == 0)
                {
                    throw new InvalidOperationException("The Media Foundation H.264 encoder did not expose an AVC sequence header.");
                }

                _transform = transform;
                _events = events;
                _async = async;
                Name = string.IsNullOrWhiteSpace(friendlyName) ? "Media Foundation H.264 MFT" : friendlyName;
                transform = null;
                events = null;
                return;
            }
            catch (Exception error)
            {
                lastError = error;
                events?.Dispose();
                transform?.Dispose();
            }
        }

        throw new InvalidOperationException(
            $"No compatible hardware Media Foundation H.264 encoder MFT could be configured ({lastError?.Message ?? "no activation candidates"}).",
            lastError);
    }

    private void ConfigureTransform(IMFTransform transform, int width, int height, int bitrate)
    {
        transform.ProcessMessage(TMessageType.MessageSetD3DManager, unchecked((nuint)_deviceManager!.NativePointer));

        using var outputType = MediaFactory.MFCreateMediaType();
        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264).CheckError();
        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)_frameRate, 1).CheckError();
        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)Math.Clamp(bitrate, 5_000_000, 100_000_000)).CheckError();
        outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
        outputType.Set(MediaTypeAttributeKeys.MaxKeyframeSpacing, (uint)ReplayEncoderProfilePolicy.GopFrames(_frameRate)).CheckError();
        outputType.Set(MediaTypeAttributeKeys.Mpeg2Profile, 100u).CheckError(); // H.264 High profile.
        transform.SetOutputType(0, outputType, 0);

        using var inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video).CheckError();
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12).CheckError();
        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height).CheckError();
        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)_frameRate, 1).CheckError();
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive).CheckError();
        transform.SetInputType(0, inputType, 0);
    }

    // Owns queue.FramePtr until it is matched with encoded output. The supplied
    // callbacks must be non-blocking; packet copying/remuxing is already bounded
    // by the replay ring's existing locks.
    public void Run(
        BlockingCollection<NativeReplayBuffer.EncodeJob> queue,
        Action<MediaFoundationEncodedSample> onSample,
        Action<nint> releaseFrame,
        Action<TimeSpan> onSubmission)
    {
        try
        {
            _transform!.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
            if (_async) RunAsync(queue, onSample, releaseFrame, onSubmission);
            else RunSynchronous(queue, onSample, releaseFrame, onSubmission);
        }
        catch (Exception error)
        {
            Failure = error.Message;
            AppLog.Error("Native capture: Media Foundation encoder failed.", error);
            while (queue.TryTake(out var remaining))
            {
                if (remaining.FramePtr != 0) releaseFrame(remaining.FramePtr);
                remaining.SwapCompleted?.Set();
            }
        }
    }

    private void RunAsync(
        BlockingCollection<NativeReplayBuffer.EncodeJob> queue,
        Action<MediaFoundationEncodedSample> onSample,
        Action<nint> releaseFrame,
        Action<TimeSpan> onSubmission)
    {
        var pending = new Queue<(nint Frame, DateTime WallClockUtc)>();
        var draining = false;
        try
        {
            while (true)
            {
                IMFMediaEvent? mediaEvent;
                try
                {
                    mediaEvent = _events!.GetEvent(0);
                }
                catch (SharpGenException error) when (error.HResult == MfENoEventsAvailable)
                {
                    continue;
                }

                using (mediaEvent)
                {
                    if (mediaEvent.Status.Failure) throw new COMException("Media Foundation MFT reported an error.", mediaEvent.Status.Code);
                    switch (mediaEvent.EventType)
                    {
                        case MediaEventTypes.TransformNeedInput:
                            if (queue.TryTake(out var job, Timeout.Infinite))
                            {
                                if (job.FramePtr == 0)
                                {
                                    job.SwapCompleted?.Set();
                                    continue;
                                }
                                Submit(job, pending, onSubmission);
                            }
                            else if (!draining)
                            {
                                draining = true;
                                _transform!.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
                                _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
                            }
                            break;

                        case MediaEventTypes.TransformHaveOutput:
                            DrainOneOutput(pending, onSample, releaseFrame);
                            break;

                        case MediaEventTypes.TransformDrainComplete:
                            return;
                    }
                }
            }
        }
        finally
        {
            while (pending.TryDequeue(out var retained)) releaseFrame(retained.Frame);
        }
    }

    private void RunSynchronous(
        BlockingCollection<NativeReplayBuffer.EncodeJob> queue,
        Action<MediaFoundationEncodedSample> onSample,
        Action<nint> releaseFrame,
        Action<TimeSpan> onSubmission)
    {
        var pending = new Queue<(nint Frame, DateTime WallClockUtc)>();
        try
        {
            foreach (var job in queue.GetConsumingEnumerable())
            {
                if (job.FramePtr == 0)
                {
                    job.SwapCompleted?.Set();
                    continue;
                }
                Submit(job, pending, onSubmission);
                while (DrainOneOutput(pending, onSample, releaseFrame)) { }
            }

            _transform!.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
            while (DrainOneOutput(pending, onSample, releaseFrame)) { }
        }
        finally
        {
            while (pending.TryDequeue(out var retained)) releaseFrame(retained.Frame);
        }
    }

    private void Submit(NativeReplayBuffer.EncodeJob job, Queue<(nint Frame, DateTime WallClockUtc)> pending, Action<TimeSpan> onSubmission)
    {
        var frame = (AVFrame*)job.FramePtr;
        var texturePointer = (nint)frame->data[0];
        var subresource = (uint)(nint)frame->data[1];
        if (texturePointer == 0) throw new InvalidOperationException("Media Foundation received an empty D3D11 encode frame.");

        // The AVFrame owns this texture's pool reference; MFCreateDXGISurfaceBuffer
        // adds its own COM reference for the transform. The wrapper is intentionally
        // never disposed because it was created over a pointer owned by that pool.
        var texture = _textureWrappers.GetOrAdd(texturePointer, pointer => new D3D11Texture2D(pointer));
        using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(D3D11Texture2DIid, texture, subresource, false);
        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = frame->pts * 10; // FFmpeg microseconds -> Media Foundation 100ns.
        sample.SampleDuration = 10_000_000L / _frameRate;
        if (frame->pict_type == AVPictureType.AV_PICTURE_TYPE_I)
        {
            sample.Set(SampleAttributeKeys.CleanPoint, true).CheckError();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _transform!.ProcessInput(0, sample, 0);
        onSubmission(sw.Elapsed);
        pending.Enqueue((job.FramePtr, job.WallClockUtc));
    }

    private bool DrainOneOutput(
        Queue<(nint Frame, DateTime WallClockUtc)> pending,
        Action<MediaFoundationEncodedSample> onSample,
        Action<nint> releaseFrame)
    {
        var streamInfo = _transform!.GetOutputStreamInfo(0);
        IMFSample? suppliedSample = null;
        try
        {
            if (((OutputStreamInfoFlags)streamInfo.Flags & OutputStreamInfoFlags.OutputStreamProvidesSamples) == 0)
            {
                suppliedSample = MediaFactory.MFCreateSample();
                using var outputBuffer = MediaFactory.MFCreateMemoryBuffer(Math.Max(1, streamInfo.Size));
                suppliedSample.AddBuffer(outputBuffer);
                var provided = new OutputDataBuffer { StreamID = 0, Sample = suppliedSample };
                var providedResult = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref provided, out var providedStatus);
                if (providedResult.Failure) return false;
                EmitOutput(provided.Sample ?? suppliedSample, pending, onSample, releaseFrame);
                return true;
            }

            var output = new OutputDataBuffer { StreamID = 0 };
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out var status);
            if (result.Failure || output.Sample is null) return false;
            using (output.Sample) EmitOutput(output.Sample, pending, onSample, releaseFrame);
            return true;
        }
        finally
        {
            suppliedSample?.Dispose();
        }
    }

    private static void EmitOutput(
        IMFSample sample,
        Queue<(nint Frame, DateTime WallClockUtc)> pending,
        Action<MediaFoundationEncodedSample> onSample,
        Action<nint> releaseFrame)
    {
        var buffer = sample.ConvertToContiguousBuffer();
        try
        {
            buffer.Lock(out var data, out _, out var currentLength);
            try
            {
                var bytes = new byte[currentLength];
                Marshal.Copy(data, bytes, 0, currentLength);
                bytes = ConvertAnnexBToLengthPrefixed(bytes);
                var keyframe = TryGetUInt32(sample, SampleAttributeKeys.CleanPoint) != 0;
                var wallClock = pending.Count > 0 ? pending.Dequeue() : default;
                onSample(new MediaFoundationEncodedSample(bytes, sample.SampleTime / 10, keyframe, wallClock.WallClockUtc));
                if (wallClock.Frame != 0) releaseFrame(wallClock.Frame);
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private static uint TryGetUInt32(IMFAttributes attributes, Guid key)
    {
        return attributes.GetUInt32(key, out var value).Success ? value : 0;
    }

    private static byte[] TryGetBlob(IMFAttributes attributes, Guid key)
    {
        try { return attributes.GetBlob(key); }
        catch { return Array.Empty<byte>(); }
    }

    // Hardware MFTs commonly expose Annex-B H.264 while the replay remuxer
    // writes MP4. FFmpeg's hardware encoders deliver AVCC already, but MP4
    // needs length-prefixed access units plus AVCDecoderConfigurationRecord
    // extradata. Normalise only Annex-B; drivers that already return AVCC pass
    // straight through unchanged.
    private static byte[] BuildAvcConfigurationRecord(byte[] sequenceHeader)
    {
        if (sequenceHeader.Length == 0 || sequenceHeader[0] == 1) return sequenceHeader;
        var nals = SplitAnnexB(sequenceHeader);
        var sps = nals.FirstOrDefault(nal => nal.Length >= 4 && (nal[0] & 0x1F) == 7);
        var pps = nals.FirstOrDefault(nal => nal.Length > 0 && (nal[0] & 0x1F) == 8);
        if (sps is null || pps is null) return sequenceHeader;

        var avcc = new byte[11 + sps.Length + pps.Length];
        avcc[0] = 1;
        avcc[1] = sps[1];
        avcc[2] = sps[2];
        avcc[3] = sps[3];
        avcc[4] = 0xFF; // 4-byte NAL lengths.
        avcc[5] = 0xE1; // One SPS.
        WriteUInt16(avcc, 6, sps.Length);
        Buffer.BlockCopy(sps, 0, avcc, 8, sps.Length);
        var ppsCountOffset = 8 + sps.Length;
        avcc[ppsCountOffset] = 1;
        WriteUInt16(avcc, ppsCountOffset + 1, pps.Length);
        Buffer.BlockCopy(pps, 0, avcc, ppsCountOffset + 3, pps.Length);
        return avcc;
    }

    private static byte[] ConvertAnnexBToLengthPrefixed(byte[] data)
    {
        if (!StartsWithAnnexBStartCode(data)) return data;
        var nals = SplitAnnexB(data);
        if (nals.Count == 0) return data;
        var length = nals.Sum(nal => nal.Length + 4);
        var converted = new byte[length];
        var offset = 0;
        foreach (var nal in nals)
        {
            converted[offset] = (byte)(nal.Length >> 24);
            converted[offset + 1] = (byte)(nal.Length >> 16);
            converted[offset + 2] = (byte)(nal.Length >> 8);
            converted[offset + 3] = (byte)nal.Length;
            Buffer.BlockCopy(nal, 0, converted, offset + 4, nal.Length);
            offset += nal.Length + 4;
        }
        return converted;
    }

    private static List<byte[]> SplitAnnexB(byte[] data)
    {
        var nals = new List<byte[]>();
        var start = FindStartCode(data, 0, out var prefixLength);
        while (start >= 0)
        {
            var payloadStart = start + prefixLength;
            var next = FindStartCode(data, payloadStart, out var nextPrefixLength);
            var payloadEnd = next >= 0 ? next : data.Length;
            if (payloadEnd > payloadStart)
            {
                var nal = new byte[payloadEnd - payloadStart];
                Buffer.BlockCopy(data, payloadStart, nal, 0, nal.Length);
                nals.Add(nal);
            }
            start = next;
            prefixLength = nextPrefixLength;
        }
        return nals;
    }

    private static bool StartsWithAnnexBStartCode(byte[] data) =>
        data.Length >= 4 && data[0] == 0 && data[1] == 0 && (data[2] == 1 || (data[2] == 0 && data[3] == 1));

    private static int FindStartCode(byte[] data, int offset, out int prefixLength)
    {
        for (var i = offset; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            if (data[i + 2] == 1) { prefixLength = 3; return i; }
            if (data[i + 2] == 0 && data[i + 3] == 1) { prefixLength = 4; return i; }
        }
        prefixLength = 0;
        return -1;
    }

    private static void WriteUInt16(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static string TryFriendlyName(IMFActivate activation)
    {
        try { return activation.FriendlyName; }
        catch { return string.Empty; }
    }

    public void Dispose()
    {
        lock (_disposeGate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _transform?.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero); }
            catch { }
            _events?.Dispose();
            _transform?.Dispose();
            _deviceManager?.Dispose();
            _textureWrappers.Clear();
            if (_started) MediaFactory.MFShutdown();
            _started = false;
        }
    }
}

internal readonly record struct MediaFoundationEncodedSample(byte[] Data, long PtsMicroseconds, bool IsKeyframe, DateTime WallClockUtc);
