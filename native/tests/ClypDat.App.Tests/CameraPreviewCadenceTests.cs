using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Buffers;
using Avalonia;
using Avalonia.Threading;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using ClypDat.Core.Settings;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class CameraPreviewCadenceTests
{
    [Fact]
    [Trait("Category", "IsolatedSTA")]
    public void LatestFrameIsAppliedAndNotifiedWithoutPropertyRefreshes()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<ClypDat.App.App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
                var service = new FakeCameraPreviewService();
                var settings = new VideoOverlaySettings { Camera = new VideoOverlayCameraSelection("camera", "Camera") };
                using var viewModel = new VideoOverlayViewModel(settings, () => { }, null, service);
                var notifications = 0;
                var previewProperties = 0;
                viewModel.CameraPreviewFrameUpdated += () => notifications++;
                viewModel.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName is nameof(VideoOverlayViewModel.CameraPreviewImage) or nameof(VideoOverlayViewModel.CameraPreviewActive)) previewProperties++;
                };

                viewModel.StartCameraPreview();
                previewProperties = 0;
                for (var index = 1; index <= 75; index++)
                {
                    var frame = ArrayPool<byte>.Shared.Rent(CameraPreviewService.FrameBytes);
                    frame[0] = (byte)index;
                    service.Emit(frame);
                }
                Dispatcher.UIThread.RunJobs();

                Assert.Equal(1, notifications);
                Assert.Equal(2, previewProperties);
                using (var locked = Assert.IsType<Avalonia.Media.Imaging.WriteableBitmap>(viewModel.CameraPreviewImage).Lock())
                {
                    Assert.Equal(75, Marshal.ReadByte(locked.Address));
                }

                viewModel.StopCameraPreview();
                service.Emit(ArrayPool<byte>.Shared.Rent(CameraPreviewService.FrameBytes));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, notifications);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Camera preview cadence test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class FakeCameraPreviewService : ICameraPreviewService
    {
        public event Action<CameraPreviewFrame>? FrameReady;
        public event Action<CameraPreviewFailure>? Failed { add { } remove { } }
        public bool IsRunning { get; private set; }
        public int Session { get; private set; }
        public void Start(string deviceMoniker) { Session++; IsRunning = true; }
        public void Stop() { Session++; IsRunning = false; }
        public void Emit(byte[] frame) => FrameReady?.Invoke(new CameraPreviewFrame(Session, frame));
        public void Dispose() => Stop();
    }
}
