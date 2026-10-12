using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using ClypDat.App.Services;
using ClypDat.App.ViewModels;
using Xunit;

namespace ClypDat.App.Tests;

public sealed class LibraryRowRealizationTests
{
    [Fact]
    public void RecyclingContainersAfterDataContextClearsKeepsOnlyTheCurrentRows()
    {
        AvaloniaTestThread.Run(() =>
        {
            var rows = Enumerable.Range(0, 121).Select(index => new LibraryGridRow([], index)).ToArray();
            var items = new RecyclingItemsControl { ItemsSource = rows };
            var realization = new LibraryRowRealization();
            var clearingCount = 0;
            items.ContainerPrepared += (_, args) =>
                realization.Prepared(args.Container, Assert.IsType<LibraryGridRow>(args.Container.DataContext));
            items.ContainerClearing += (_, args) =>
            {
                Assert.Null(args.Container.DataContext);
                Assert.True(realization.Cleared(args.Container));
                clearingCount++;
            };
            var generator = items.ItemContainerGenerator;
            var containers = Enumerable.Range(0, 7).Select(_ => new ContentPresenter()).ToArray();
            for (var index = 0; index < rows.Length; index++)
            {
                var container = containers[index % containers.Length];
                if (index >= containers.Length) generator.ClearItemContainer(container);
                generator.PrepareItemContainer(container, rows[index], index);
                container.UpdateChild();
                generator.ItemContainerPrepared(container, rows[index], index);

                Assert.Equal(Math.Min(index + 1, containers.Length), realization.Rows.Count);
                Assert.True(rows[index].IsRealized);
                if (index >= containers.Length) Assert.False(rows[index - containers.Length].IsRealized);
            }
            Assert.Equal(rows.Length - containers.Length, clearingCount);
            Assert.All(rows[..^containers.Length], row => Assert.False(row.IsRealized));
            foreach (var container in containers) generator.ClearItemContainer(container);
            Assert.Empty(realization.Rows);
            Assert.All(rows, row => Assert.False(row.IsRealized));
        }, TimeSpan.FromSeconds(10), "Library row recycling timed out.");
    }

    [Fact]
    public void RepreparingAContainerKeepsASharedRowUntilItsLastOwnerClears()
    {
        AvaloniaTestThread.Run(() =>
        {
            var realization = new LibraryRowRealization();
            var first = new ContentPresenter();
            var second = new ContentPresenter();
            var shared = new LibraryGridRow([], 0);
            var replacement = new LibraryGridRow([], 1);
            realization.Prepared(first, shared);
            realization.Prepared(first, shared); // repeated notifications do not add another owner
            realization.Prepared(second, shared);
            realization.Prepared(first, replacement); // retire the previous row even without a clear event
            Assert.True(shared.IsRealized);
            Assert.True(replacement.IsRealized);
            Assert.Equal(2, realization.Rows.Count);

            Assert.True(realization.Cleared(second));
            Assert.False(shared.IsRealized);
            Assert.True(replacement.IsRealized);
            Assert.Same(replacement, Assert.Single(realization.Rows));
            Assert.False(realization.Cleared(second)); // duplicate clearing is harmless
            Assert.True(realization.Cleared(first));
            Assert.False(replacement.IsRealized);
            Assert.Empty(realization.Rows);
        }, TimeSpan.FromSeconds(10), "Library row ownership timed out.");
    }

    private sealed class RecyclingItemsControl : ItemsControl
    {
        protected override void ClearContainerForItemOverride(Control container)
        {
            base.ClearContainerForItemOverride(container);
            // An attached presenter updates its child during ContentChanged.
            // Perform that update without creating a native test window.
            ((ContentPresenter)container).UpdateChild();
        }
    }
}
