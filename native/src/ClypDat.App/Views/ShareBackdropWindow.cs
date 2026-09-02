using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

// Separate from ShareDialog so its layered alpha never affects the solid card.
internal sealed class ShareBackdropWindow : Window
{
    public event EventHandler? DismissRequested;
    private readonly Window _owner;
    private readonly bool _allowOwnerMove;
    private PixelPoint _lockedPosition;
    private bool _restoringPosition;

    // scrimColor is the dim itself, so callers that were designed around a
    // lighter one keep it. It also decides the layered-alpha level applied when
    // the platform refuses real transparency - this window carries nothing but
    // the scrim, so fading the whole window IS the effect, not a side effect.
    public ShareBackdropWindow(Window owner, bool allowOwnerMove = false, string scrimColor = "#DD000000")
    {
        _owner = owner;
        _allowOwnerMove = allowOwnerMove;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        Topmost = false;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var scrim = new Border
        {
            Background = new SolidColorBrush(Color.Parse(scrimColor)),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true
        };
        scrim.PointerPressed += Scrim_OnPointerPressed;
        Content = scrim;
        PositionOverOwner(owner);
        PositionChanged += (_, _) => RestoreLockedPosition();
        owner.PositionChanged += Owner_OnPositionChanged;
        owner.SizeChanged += Owner_OnSizeChanged;
        Closed += OnClosed;
        Opened += (_, _) =>
        {
            WindowTransparencyFallback.ApplyIfNeeded(this, scrim.Background, b => scrim.Background = b, "backdrop");
            RestoreLockedPosition();
        };
    }

    private void Scrim_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint((Visual)sender!).Properties.IsLeftButtonPressed) return;

        if (_allowOwnerMove && e.GetPosition((Visual)sender!).Y < OwnerTitleBarHeight)
        {
            e.Handled = true;
            _owner.BeginMoveDrag(e);
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PositionOverOwner(Window owner)
    {
        var handle = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
        {
            var scaling = owner.RenderScaling > 0 ? owner.RenderScaling : 1;
            _lockedPosition = new PixelPoint(rect.Left, rect.Top);
            Position = _lockedPosition;
            Width = (rect.Right - rect.Left) / scaling;
            Height = (rect.Bottom - rect.Top) / scaling;
            return;
        }

        _lockedPosition = owner.PointToScreen(new Point(0, 0));
        Position = _lockedPosition;
        Width = owner.Bounds.Width;
        Height = owner.Bounds.Height;
    }

    private void Owner_OnPositionChanged(object? sender, PixelPointEventArgs e) => PositionOverOwner(_owner);

    private void Owner_OnSizeChanged(object? sender, SizeChangedEventArgs e) => PositionOverOwner(_owner);

    private void OnClosed(object? sender, EventArgs e)
    {
        _owner.PositionChanged -= Owner_OnPositionChanged;
        _owner.SizeChanged -= Owner_OnSizeChanged;
    }

    private void RestoreLockedPosition()
    {
        if (_restoringPosition || Position == _lockedPosition) return;
        _restoringPosition = true;
        try { Position = _lockedPosition; }
        finally { _restoringPosition = false; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Win32Rect rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const double OwnerTitleBarHeight = 48;
}
