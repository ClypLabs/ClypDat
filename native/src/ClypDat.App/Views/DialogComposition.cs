using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ClypDat.App.Views;

/// <summary>Shared chrome only; callers own modal results, cancellation and backdrops.</summary>
internal static class DialogComposition
{
    internal static Border Shell(Control content) => new()
    {
        Classes = { "dialogShell" },
        Child = content
    };

    internal static (Window Window, Panel Body) Create(string title)
    {
        var window = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent,
            WindowDecorations = WindowDecorations.None,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = -1,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }
        };
        var titleText = new TextBlock { Text = title, Classes = { "dialogTitle" } };
        var close = new Button
        {
            Content = "✕", Classes = { "dialogClose" }, Width = 48, Height = 48,
            CornerRadius = new CornerRadius(0, 15, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        Avalonia.Automation.AutomationProperties.SetName(close, "Close dialog");
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => window.Close();
        Grid.SetColumn(close, 1);
        var header = new Border
        {
            Classes = { "dialogHeader" },
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { titleText, close } }
        };
        var body = new StackPanel { Classes = { "dialogBody" } };
        DockPanel.SetDock(header, Dock.Top);
        window.Content = Shell(new DockPanel { Children = { header, body } });
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            window.Close();
            e.Handled = true;
        };
        return (window, body);
    }
}
