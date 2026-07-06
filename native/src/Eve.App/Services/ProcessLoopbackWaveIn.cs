using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Eve.App.Services;

[SupportedOSPlatform("windows")]
internal sealed class ProcessLoopbackWaveIn : IWaveIn
{
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientGuid = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    private static readonly Guid AudioCaptureClientGuid = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    private readonly uint _processId;
    private readonly ProcessLoopbackCaptureMode _mode;
    private readonly IAudioClient _audioClient;
    private readonly IAudioCaptureClientNative _captureClient;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;

    public ProcessLoopbackWaveIn(int processId, ProcessLoopbackCaptureMode mode)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        _processId = (uint)processId;
        _mode = mode;
        _audioClient = ActivateAudioClient(_processId, _mode);
        var mixFormatPtr = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(_audioClient.GetMixFormat(out mixFormatPtr));
            WaveFormat = WaveFormat.MarshalFromPtr(mixFormatPtr);
        }
        finally
        {
            if (mixFormatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormatPtr);
        }

        var sessionGuid = Guid.Empty;
        Marshal.ThrowExceptionForHR(_audioClient.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback,
            0,
            0,
            WaveFormat,
            ref sessionGuid));
        object service;
        var captureClientGuid = AudioCaptureClientGuid;
        Marshal.ThrowExceptionForHR(_audioClient.GetService(captureClientGuid, out service));
        _captureClient = (IAudioCaptureClientNative)service;
    }

    public WaveFormat WaveFormat { get; set; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void StartRecording()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        _captureTask = Task.Run(() => CaptureLoop(_cts.Token));
    }

    public void StopRecording()
    {
        var cts = _cts;
        if (cts is null) return;
        cts.Cancel();
        try
        {
            _captureTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Stop is best effort.
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
        }
    }

    public void Dispose()
    {
        StopRecording();
        if (_captureClient is not null) Marshal.ReleaseComObject(_captureClient);
        if (_audioClient is not null) Marshal.ReleaseComObject(_audioClient);
    }

    private void CaptureLoop(CancellationToken token)
    {
        Exception? stoppedError = null;
        try
        {
            Marshal.ThrowExceptionForHR(_audioClient.Start());
            while (!token.IsCancellationRequested)
            {
                Marshal.ThrowExceptionForHR(_captureClient.GetNextPacketSize(out var packetFrames));
                while (packetFrames > 0)
                {
                    Marshal.ThrowExceptionForHR(_captureClient.GetBuffer(
                        out var data,
                        out var frames,
                        out var flags,
                        out _,
                        out _));

                    var bytes = frames * WaveFormat.BlockAlign;
                    var buffer = new byte[bytes];
                    if (!flags.HasFlag(AudioClientBufferFlags.Silent) && data != IntPtr.Zero)
                    {
                        Marshal.Copy(data, buffer, 0, bytes);
                    }

                    _captureClient.ReleaseBuffer(frames);
                    if (bytes > 0) DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                    Marshal.ThrowExceptionForHR(_captureClient.GetNextPacketSize(out packetFrames));
                }

                Thread.Sleep(10);
            }
        }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested) stoppedError = error;
        }
        finally
        {
            try
            {
                _audioClient.Stop();
            }
            catch
            {
                // Stop is best effort.
            }

            RecordingStopped?.Invoke(this, new StoppedEventArgs(stoppedError));
        }
    }

    private static IAudioClient ActivateAudioClient(uint processId, ProcessLoopbackCaptureMode mode)
    {
        var activation = new AudioClientActivationParamsNative
        {
            ActivationType = 1,
            TargetProcessId = processId,
            ProcessLoopbackMode = mode == ProcessLoopbackCaptureMode.IncludeTargetProcessTree ? 0 : 1
        };
        var activationPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParamsNative>());
        var propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlobNative>());
        try
        {
            Marshal.StructureToPtr(activation, activationPtr, false);
            var propVariant = new PropVariantBlobNative
            {
                VariantType = 65,
                BlobSize = (uint)Marshal.SizeOf<AudioClientActivationParamsNative>(),
                BlobData = activationPtr
            };
            Marshal.StructureToPtr(propVariant, propVariantPtr, false);
            var handler = new ActivateAudioInterfaceCompletionHandler();
            var audioClientGuid = AudioClientGuid;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                ref audioClientGuid,
                propVariantPtr,
                handler,
                out _));
            return (IAudioClient)handler.WaitForResult();
        }
        finally
        {
            Marshal.FreeHGlobal(propVariantPtr);
            Marshal.FreeHGlobal(activationPtr);
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParamsNative
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlobNative
    {
        public ushort VariantType;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public uint BlobSize;
        public IntPtr BlobData;
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClientNative
    {
        int GetBuffer(
            out IntPtr data,
            out int numFramesToRead,
            out AudioClientBufferFlags bufferFlags,
            out long devicePosition,
            out long qpcPosition);

        int ReleaseBuffer(int numFramesRead);

        int GetNextPacketSize(out int numFramesInNextPacket);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    private sealed class ActivateAudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly TaskCompletionSource<object> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                activateOperation.GetActivateResult(out var activateResult, out var activatedInterface);
                Marshal.ThrowExceptionForHR(activateResult);
                _completion.TrySetResult(activatedInterface);
            }
            catch (Exception error)
            {
                _completion.TrySetException(error);
            }
        }

        public object WaitForResult()
        {
            return _completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }
}

internal enum ProcessLoopbackCaptureMode
{
    IncludeTargetProcessTree,
    ExcludeTargetProcessTree
}
