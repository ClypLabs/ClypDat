using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

// Shown while a quit waits on work that would be lost if the process ended now
// (see ShutdownGuard). The main window is already hidden at that point, so this
// is its own topmost window with a taskbar button. It can't be dismissed: the
// only way out is the work finishing or "Quit anyway", which appears after a
// short grace period.
internal sealed class ClosingSafelyWindow : Window
{
    private static readonly TimeSpan QuitAnywayDelay = TimeSpan.FromSeconds(10);
    private readonly StackPanel _tasks = new() { Spacing = 6 };
    private readonly Button _quitAnyway;
    private readonly DispatcherTimer _quitAnywayTimer;
    private string? _extraTask;
    private bool _allowClose;
    private bool _shutdownBlocked;

    public event Action? QuitAnywayRequested;

    public ClosingSafelyWindow()
    {
        Title = "ClypDat - Closing Safely";
        Icon = AppThemeService.CreateWindowIcon();
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Transparent;
        WindowDecorations = WindowDecorations.None;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var header = new Border
        {
            Background = AppThemeService.Brush("Surface_0C1319", "#0C1319"),
            CornerRadius = new CornerRadius(11, 11, 0, 0),
            Height = 56,
            Child = new TextBlock
            {
                Text = "CLOSING SAFELY",
                Foreground = AppThemeService.Brush("Text_D8E4F2", "#D8E4F2"),
                FontSize = 17,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            }
        };

        _quitAnyway = new Button
        {
            Classes = { "deleteButton" },
            Content = "Quit anyway",
            Height = 34,
            Padding = new Thickness(16, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        _quitAnyway.Click += (_, _) =>
        {
            _quitAnyway.IsEnabled = false;
            QuitAnywayRequested?.Invoke();
        };

        var body = new StackPanel
        {
            Margin = new Thickness(28, 24, 28, 28),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = "ClypDat is finishing up so you don't lose anything. It will close by itself when it's done.",
                    Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6"),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap
                },
                new ProgressBar { Height = 6, CornerRadius = new CornerRadius(3), IsIndeterminate = true },
                _tasks,
                _quitAnyway
            }
        };

        var layout = new DockPanel { LastChildFill = true, Children = { header, body } };
        DockPanel.SetDock(header, Dock.Top);
        Content = new Border
        {
            Background = AppThemeService.Brush("Surface_111920", "#111920"),
            BorderBrush = AppThemeService.Brush("Surface_232F3A", "#232F3A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Child = layout
        };

        _quitAnywayTimer = new DispatcherTimer { Interval = QuitAnywayDelay };
        _quitAnywayTimer.Tick += (_, _) =>
        {
            _quitAnywayTimer.Stop();
            _quitAnyway.IsVisible = true;
        };

        ShutdownGuard.Changed += RefreshTasks;
        Opened += (_, _) => _quitAnywayTimer.Start();
        // Alt+F4 / End task on the popup itself must not skip the wait.
        Closing += (_, e) => { if (!_allowClose) e.Cancel = true; };
        Closed += (_, _) =>
        {
            ShutdownGuard.Changed -= RefreshTasks;
            _quitAnywayTimer.Stop();
            ReleaseShutdownBlock();
        };
        RefreshTasks();
    }

    // Shown alongside the ShutdownGuard labels, for waits the guard doesn't
    // track (the recorder finishing its files after the pipe has closed).
    public void SetExtraTask(string? label)
    {
        _extraTask = label;
        RefreshTasks();
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Close();
    }

    // Names ClypDat on the Windows shutdown/sign-out screen while it holds the
    // session open. Needs a visible top-level window, which is why it lives here.
    public void BlockShutdown(string reason)
    {
        if (_shutdownBlocked || !OperatingSystem.IsWindows()) return;
        if (TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        _shutdownBlocked = ShutdownBlockReasonCreate(hwnd, reason);
        if (!_shutdownBlocked) AppLog.Info($"[Quit] ShutdownBlockReasonCreate failed: {Marshal.GetLastWin32Error()}");
    }

    private void ReleaseShutdownBlock()
    {
        if (!_shutdownBlocked) return;
        _shutdownBlocked = false;
        if (TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero) ShutdownBlockReasonDestroy(hwnd);
    }

    private void RefreshTasks()
    {
        var labels = ShutdownGuard.ActiveLabels.ToList();
        if (!string.IsNullOrWhiteSpace(_extraTask)) labels.Add(_extraTask);
        _tasks.Children.Clear();
        foreach (var label in labels)
        {
            _tasks.Children.Add(new TextBlock
            {
                Text = "• " + label,
                Foreground = AppThemeService.Brush("Text_D8E4F2", "#D8E4F2"),
                FontSize = 13
            });
        }
        _tasks.IsVisible = labels.Count > 0;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
}
