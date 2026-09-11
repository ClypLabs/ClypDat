using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Avalonia;
using Xunit;

namespace ClypDat.App.Tests;

/// <summary>
/// The one thread Avalonia runs on for the whole test process.
///
/// Avalonia can be set up once per process, and its dispatcher belongs to the
/// thread that set it up. Each UI test used to start its own STA thread and
/// call SetupWithoutStarting there: fine run alone, but in a full run the
/// second of them threw "Setup was already called on one of AppBuilder
/// instances" before testing anything. Tests now hand their body to this
/// thread instead, and bodies run one at a time, so no two share the
/// application's resources mid-test either.
/// </summary>
internal static class AvaloniaTestThread
{
    private static readonly object Gate = new();
    private static readonly BlockingCollection<Action> Work = new();
    private static readonly ManualResetEventSlim Ready = new();
    private static Exception? _setupFailure;
    private static Thread? _thread;

    // Avalonia's dispatcher belongs to whichever thread touches it first, and
    // app code under test (view models, services posting to the UI thread)
    // touches it too. In a full run where one of those tests went first, the
    // dispatcher became that test's thread and setup here failed with "a
    // different thread owns it" - intermittently, by test order. Starting the
    // UI thread as the test assembly loads claims it before any test runs.
    //
    // Start only; never wait here. The thread runs code from this assembly,
    // which cannot proceed until the module initializer returns - waiting for
    // setup inside it deadlocked the whole run.
#pragma warning disable CA2255 // Module initializers are for exactly this kind of process-wide setup in a test assembly.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void ClaimDispatcherFirst() => EnsureStarted();

    private static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_thread is not null) return;
            _thread = new Thread(() =>
            {
                try
                {
                    AppBuilder.Configure<ClypDat.App.App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
                }
                catch (Exception error)
                {
                    _setupFailure = error;
                }
                Ready.Set();
                if (_setupFailure is not null) return;
                foreach (var item in Work.GetConsumingEnumerable()) item();
            })
            { IsBackground = true, Name = "Avalonia test UI thread" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> on the Avalonia thread and rethrows whatever
    /// it threw. <paramref name="timeout"/> counts from when the body starts,
    /// not from when it was queued behind another test's.
    /// </summary>
    public static void Run(Action body, TimeSpan timeout, string timeoutMessage)
    {
        EnsureStarted();
        Assert.True(Ready.Wait(TimeSpan.FromMinutes(2)), "Avalonia setup on the test UI thread never finished.");
        if (_setupFailure is not null) ExceptionDispatchInfo.Capture(_setupFailure).Throw();

        Exception? failure = null;
        using var started = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        Work.Add(() =>
        {
            started.Set();
            try { body(); }
            catch (Exception error) { failure = error; }
            finally { done.Set(); }
        });
        Assert.True(started.Wait(TimeSpan.FromMinutes(5)), "The Avalonia test thread never reached this test.");
        Assert.True(done.Wait(timeout), timeoutMessage);
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
