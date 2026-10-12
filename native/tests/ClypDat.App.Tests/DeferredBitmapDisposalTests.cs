using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using ClypDat.App.Services;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class DeferredBitmapDisposalTests
{
    [Fact]
    [Trait("Category", "IsolatedSTA")]
    public void EvictedReferenceSurvivesUntilRenderFenceAndCanBeCollectedAfterIt()
    {
        AvaloniaTestThread.Run(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var reference = EvictReference();
            var fence = DeferredBitmapDisposal.WaitForReferenceReleaseAsync(TimeSpan.FromSeconds(2));
            Assert.False(fence.IsCompleted);

            Collect();
            Assert.True(reference.IsAlive); // previous rendered frame is protected

            var watch = Stopwatch.StartNew();
            while (!fence.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(1);
            }
            Assert.True(fence.IsCompletedSuccessfully);
            Assert.True(fence.GetAwaiter().GetResult());

            Collect();
            Assert.False(reference.IsAlive); // intended cleanup can now reclaim the image
        }, TimeSpan.FromSeconds(10), "Bitmap render fence test timed out.");
    }

    [Fact]
    [Trait("Category", "IsolatedSTA")]
    public void DispatcherWithoutAPumpTimesOutWithoutReleasingProtectedReferences()
    {
        AvaloniaTestThread.Run(() =>
        {
            Dispatcher.UIThread.RunJobs();
            var reference = EvictReference();
            var watch = Stopwatch.StartNew();
            var released = DeferredBitmapDisposal.WaitForReferenceReleaseAsync(TimeSpan.FromMilliseconds(30))
                .GetAwaiter().GetResult(); // deliberately do not run dispatcher jobs

            Assert.False(released);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
            Collect();
            Assert.True(reference.IsAlive);

            Dispatcher.UIThread.RunJobs();
            Collect();
            Assert.False(reference.IsAlive);
        }, TimeSpan.FromSeconds(10), "Stopped dispatcher bitmap fence test timed out.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference EvictReference()
    {
        var value = new object();
        var reference = new WeakReference(value);
        DeferredBitmapDisposal.ReleaseReferenceAfterRender(value);
        return reference;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
