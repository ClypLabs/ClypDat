using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

// The Notice Board: announcements written at www.clypdat.xyz/admin that reach
// every install without an update (NoticeBoardService). New notices pop up once
// at launch; critical ones (security, severe bugs) pop up every launch until
// acknowledged, and interrupt a running session as soon as they arrive. The bell
// in the header reopens everything current.
public sealed partial class MainWindow
{
    private static readonly TimeSpan NoticeRefreshInterval = TimeSpan.FromMinutes(30);
    private DispatcherTimer? _noticeTimer;
    // Critical notices already popped this session, so the 30-minute refresh does
    // not keep reopening one the user just closed without acknowledging.
    private readonly HashSet<string> _noticesPoppedThisSession = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<Notice> ApplicableNotices() =>
        NoticeBoardRules.Applicable(NoticeBoardService.Current, AppUpdateService.CurrentVersion, DateTimeOffset.UtcNow);

    private IReadOnlyList<Notice> NoticesToShow() => ViewModel is { } model
        ? NoticeBoardRules.ToShow(ApplicableNotices(), model.Settings.SeenNoticeIds, model.Settings.AcknowledgedNoticeIds)
        : Array.Empty<Notice>();

    private void UpdateNoticeBadge()
    {
        if (ViewModel is { } model) model.HasUnreadNotices = NoticesToShow().Count > 0;
    }

    private void StartNoticeBoardTimer()
    {
        UpdateNoticeBadge();
        _noticeTimer ??= new DispatcherTimer { Interval = NoticeRefreshInterval };
        _noticeTimer.Tick -= NoticeTimer_OnTick;
        _noticeTimer.Tick += NoticeTimer_OnTick;
        _noticeTimer.Start();
    }

    private void StopNoticeBoardTimer() => _noticeTimer?.Stop();

    private async void NoticeTimer_OnTick(object? sender, EventArgs e)
    {
        try
        {
            if (!await NoticeBoardService.RefreshAsync()) return;
            UpdateNoticeBadge();
            // Only a critical notice interrupts a session in progress; anything
            // else waits on the bell and the next launch.
            var urgent = NoticesToShow().Where(notice => notice.IsCritical && !_noticesPoppedThisSession.Contains(notice.Id)).ToList();
            if (urgent.Count > 0 && !_updateDialogOpen) await ShowNoticeDialogAsync(urgent, board: false);
        }
        catch (Exception error)
        {
            AppLog.Error("Notice board refresh tick failed (non-fatal)", error);
        }
    }

    // Startup queue step (RunStartupDialogsAsync), after the update check: a short,
    // bounded wait for a fresh feed, then whatever is new.
    private async Task ShowStartupNoticesAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await NoticeBoardService.RefreshAsync(timeout.Token);
        }
        catch (Exception error)
        {
            AppLog.Error("Notice board startup refresh failed (non-fatal)", error);
        }
        UpdateNoticeBadge();
        var toShow = NoticesToShow();
        if (toShow.Count > 0) await ShowNoticeDialogAsync(toShow, board: false);
    }

    private async void NoticeBoardButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var all = ApplicableNotices();
        await ShowNoticeDialogAsync(all, board: true);
        // The bell also nudges a refresh, so it is never more than a click stale.
        if (await NoticeBoardService.RefreshAsync()) UpdateNoticeBadge();
    }

    private async Task ShowNoticeDialogAsync(IReadOnlyList<Notice> notices, bool board)
    {
        if (ViewModel is not { } model || _updateDialogOpen) return;
        if (!board && notices.Count == 0) return;

        foreach (var notice in notices) _noticesPoppedThisSession.Add(notice.Id);
        // Opening counts as seen for everything except critical notices, which
        // need the explicit "I understand".
        var newlySeen = notices.Where(notice => !notice.IsCritical && !model.Settings.SeenNoticeIds.Contains(notice.Id)).Select(notice => notice.Id).ToList();
        if (newlySeen.Count > 0)
        {
            model.Settings.SeenNoticeIds.AddRange(newlySeen);
            model.SaveSettings();
        }
        UpdateNoticeBadge();

        await ShowUpdateDialogAsync(CreateNoticeDialog(notices, board));
        UpdateNoticeBadge();
    }

    private Window CreateNoticeDialog(IReadOnlyList<Notice> notices, bool board)
    {
        var window = new Window
        {
            Width = 640,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 720,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent,
            WindowDecorations = WindowDecorations.None,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = -1,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }
        };

        var titleBar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 48 };
        var titleLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Image { Source = AppThemeService.CurrentLogo(large: false), Width = 16, Height = 16, Margin = new Avalonia.Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = "Notice Board", Foreground = AppThemeService.Brush("Text_B9C6D4", "#B9C6D4"), FontSize = 12, FontWeight = FontWeight.SemiBold, Margin = new Avalonia.Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center }
            }
        };
        var closeButton = new Button { Classes = { "windowChromeButton", "windowCloseButton" }, Content = "✕", Width = 40, Height = 40, FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, CornerRadius = new Avalonia.CornerRadius(0, 11, 0, 0) };
        closeButton.Click += (_, _) => window.Close();
        Grid.SetColumn(closeButton, 2);
        titleBar.Children.Add(titleLeft);
        titleBar.Children.Add(closeButton);
        var roundedTitleBar = new Border { Background = AppThemeService.Brush("Surface_0C1319", "#0C1319"), CornerRadius = new Avalonia.CornerRadius(11, 11, 0, 0), Child = titleBar };

        var list = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(22, 18, 22, 18) };
        if (notices.Count == 0)
        {
            list.Children.Add(new TextBlock
            {
                Text = "Nothing on the board right now. Big features and anything urgent will show up here.",
                Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });
        }
        foreach (var notice in notices) list.Children.Add(BuildNoticeCard(notice));

        var closeFooter = new Button { Content = board ? "Close" : "Got it", Width = 120, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "primaryButton" } };
        closeFooter.Click += (_, _) => window.Close();
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(22, 0, 22, 20),
            Children = { closeFooter }
        };

        var content = new DockPanel { Children = { roundedTitleBar, footer, new ScrollViewer { Content = list, MaxHeight = 560 } } };
        DockPanel.SetDock(roundedTitleBar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        var shell = CreateRoundedDialogShell(content);
        window.Content = shell;
        window.Opened += (_, _) => WindowTransparencyFallback.ApplyIfNeeded(window, shell.Background, brush => shell.Background = brush, "rounded-dialog");
        return window;
    }

    private Border BuildNoticeCard(Notice notice)
    {
        var (label, accent, pillBackground) = notice.Severity switch
        {
            "critical" => ("CRITICAL", "#F05A63", "#3A1C20"),
            "feature" => ("NEW FEATURE", "#13C8B5", "#1C3A36"),
            _ => ("INFO", "#8EA1B6", "#1E2A34")
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new Border
                {
                    Background = Brush.Parse(pillBackground),
                    CornerRadius = new Avalonia.CornerRadius(5),
                    Padding = new Avalonia.Thickness(7, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = label, Foreground = Brush.Parse(accent), FontSize = 10, FontWeight = FontWeight.Bold }
                },
                new TextBlock
                {
                    Text = notice.PublishedAt.ToLocalTime().ToString("d MMM yyyy"),
                    Foreground = AppThemeService.Brush("Text_5C6D7E", "#5C6D7E"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var body = new StackPanel { Spacing = 8, Children = { header, new TextBlock
        {
            Text = notice.Title,
            Foreground = AppThemeService.Brush("Text_EDF4FB", "#EDF4FB"),
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        } } };
        foreach (var block in BuildNoticeBody(notice.Body)) body.Children.Add(block);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        if (notice.Link is { } link && NoticeBoardRules.IsAllowedLink(link.Url))
        {
            var linkButton = new Button { Content = link.Label, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(linkButton, link.Url);
            linkButton.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true }); }
                catch (Exception error) { AppLog.Error("Notice link could not be opened", error); }
            };
            actions.Children.Add(linkButton);
        }
        if (notice.IsCritical && ViewModel is { } model && !model.Settings.AcknowledgedNoticeIds.Contains(notice.Id, StringComparer.OrdinalIgnoreCase))
        {
            var acknowledge = new Button { Content = "I understand", HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "primaryButton" } };
            acknowledge.Click += (_, _) =>
            {
                model.Settings.AcknowledgedNoticeIds.Add(notice.Id);
                if (!model.Settings.SeenNoticeIds.Contains(notice.Id)) model.Settings.SeenNoticeIds.Add(notice.Id);
                model.SaveSettings();
                acknowledge.IsEnabled = false;
                acknowledge.Content = "Acknowledged";
                UpdateNoticeBadge();
            };
            actions.Children.Add(acknowledge);
        }
        if (actions.Children.Count > 0) body.Children.Add(actions);

        return new Border
        {
            Background = AppThemeService.Brush("Surface_0C1319", "#0C1319"),
            BorderBrush = notice.IsCritical ? Brush.Parse("#5A2A30") : AppThemeService.Brush("Surface_1E2A34", "#1E2A34"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(8),
            Padding = new Avalonia.Thickness(16, 14),
            Child = body
        };
    }

    // Enough Markdown for an announcement: "# " headings, "- " bullets and
    // paragraphs, each rendered inline by ReleaseNotesMarkdownRenderer with links
    // limited to the Notice Board's own allow-list.
    private static IEnumerable<Control> BuildNoticeBody(string markdown)
    {
        static bool AllowLink(Uri uri) => NoticeBoardRules.IsAllowedLink(uri.AbsoluteUri);
        var paragraph = new List<string>();

        TextBlock Text(string value, double size = 13, FontWeight? weight = null)
        {
            var text = new TextBlock
            {
                Foreground = AppThemeService.Brush("Text_C4D2E0", "#C4D2E0"),
                FontSize = size,
                FontWeight = weight ?? FontWeight.Normal,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size * 1.45
            };
            ReleaseNotesMarkdownRenderer.Apply(text, value, AllowLink);
            return text;
        }

        IEnumerable<Control> Flush()
        {
            if (paragraph.Count == 0) yield break;
            yield return Text(string.Join(" ", paragraph));
            paragraph.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                foreach (var block in Flush()) yield return block;
                continue;
            }
            if (line.StartsWith('#'))
            {
                foreach (var block in Flush()) yield return block;
                var heading = Text(line.TrimStart('#').Trim(), 14, FontWeight.Bold);
                heading.Foreground = AppThemeService.Brush("Text_EDF4FB", "#EDF4FB");
                heading.Margin = new Avalonia.Thickness(0, 4, 0, 0);
                yield return heading;
                continue;
            }
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("+ "))
            {
                foreach (var block in Flush()) yield return block;
                var bullet = new Grid { ColumnDefinitions = new ColumnDefinitions("12,*") };
                var dot = new Border { Width = 4, Height = 4, CornerRadius = new Avalonia.CornerRadius(2), Background = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6"), Margin = new Avalonia.Thickness(0, 8, 0, 0), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
                var text = Text(line[2..].Trim());
                Grid.SetColumn(text, 1);
                bullet.Children.Add(dot);
                bullet.Children.Add(text);
                yield return bullet;
                continue;
            }
            paragraph.Add(line);
        }
        foreach (var block in Flush()) yield return block;
    }
}
