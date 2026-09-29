using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using ClypDat.App.Controls;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class ClypDatLoaderTests
{
    [Theory]
    [InlineData(34)]
    [InlineData(64)]
    [InlineData(96)]
    public void CachedEditorLoaderAnimatesOnEveryAttachment(int size)
    {
        AvaloniaTestThread.Run(() =>
        {
            var host = new LoaderHost();
            try
            {
                var loader = new ClypDatLoader { Width = size, Height = size };
                for (var opening = 1; opening <= 3; opening++)
                {
                    host.SpinnerHost.Child = loader;
                    host.SpinnerHost.Measure(new Size(size, size));
                    host.SpinnerHost.Arrange(new Rect(0, 0, size, size));
                    loader.UpdateLayout();
                    foreach (var name in new[] { "Ring", "MarkOuter", "MarkInner", "Glow" })
                    {
                        var visual = ElementComposition.GetElementVisual(loader.FindControl<Control>(name)!);
                        Assert.NotNull(visual);
                        var count = PendingAnimationCount(visual);
                        Assert.True(count == (name == "Glow" ? 2 : 1),
                            $"{name} has {count} animations on clip opening {opening}; the cached loading icon is not fully animated.");
                    }
                    host.SpinnerHost.Child = null;
                }
            }
            finally { host.Close(); }
        }, TimeSpan.FromSeconds(15), "Repeated loader attachment did not finish.");
    }

    // Verify animations handed to the real compositor without capturing pixels
    // or opening a visible window. No dispatcher commit drains this queue here.
    private static int PendingAnimationCount(CompositionVisual visual)
    {
        var pending = typeof(CompositionObject).GetField("PendingAnimations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(visual)!;
        var iterator = pending.GetType().GetMethod("GetEnumerator")!.Invoke(pending, null)!;
        var moveNext = iterator.GetType().GetMethod("MoveNext")!;
        var count = 0;
        while ((bool)moveNext.Invoke(iterator, null)!) count++;
        return count;
    }

    private sealed class LoaderHost : Window
    {
        public Border SpinnerHost { get; } = new();

        public LoaderHost()
        {
            // Attach to the actual window compositor without showing the window.
            LogicalChildren.Add(SpinnerHost);
            VisualChildren.Add(SpinnerHost);
        }
    }
}
