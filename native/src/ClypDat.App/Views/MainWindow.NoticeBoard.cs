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
// at launch; info and critical ones also interrupt a running session. Critical
// notices pop up every launch until acknowledged. The bell
// in the header reopens everything current.
public sealed partial class MainWindow
{
    private static readonly TimeSpan NoticeRefreshInterval = TimeSpan.FromSeconds(15);
    private DispatcherTimer? _noticeTimer;
    private bool _noticeRefreshInProgress;
    // Critical notices already popped this session, so the minute refresh does
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
        if (_noticeRefreshInProgress) return;
        _noticeRefreshInProgress = true;
        try
        {
            await NoticeBoardService.RefreshAsync();
            // Expiry is local policy state too. Reapply every tick even when
            // the signed revision did not change, so controls resume on time.
            ViewModel?.ApplyRemotePolicy();
            UpdateAutoClipStates();
            UpdateNoticeBadge();
            // Reconsider the held feed even when unchanged: another dialog may
            // have blocked presentation on the tick that fetched this notice.
            if (ViewModel is not { } model) return;
            var urgent = NoticeBoardRules.ToShowDuringSession(ApplicableNotices(),
                model.Settings.SeenNoticeIds, model.Settings.AcknowledgedNoticeIds, _noticesPoppedThisSession);
            if (urgent.Count > 0 && !_updateDialogOpen) await ShowNoticeDialogAsync(urgent, board: false);
        }
        catch (Exception error)
        {
            AppLog.Error("Notice board refresh tick failed (non-fatal)", error);
        }
        finally
        {
            _noticeRefreshInProgress = false;
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
            Title = "Notice Board",
            Width = 600,
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

        var titleBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titleLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Avalonia.Thickness(24, 20, 0, 18),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border
                {
                    Width = 40, Height = 40, CornerRadius = new Avalonia.CornerRadius(12),
                    Background = AppThemeService.Brush("Surface_1E2A34", "#1E2A34"),
                    Child = new Image { Source = AppThemeService.CurrentLogo(large: false), Width = 22, Height = 22 }
                },
                new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children =
                {
                    new TextBlock { Text = "Notice Board", Foreground = AppThemeService.Brush("Text_EDF4FB", "#EDF4FB"), FontSize = 17, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Updates from ClypDat", Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6"), FontSize = 12 }
                } }
            }
        };
        // The same corner close every other popup has (CreateChromelessDialog):
        // flush to the card's top-right corner, rounded to match it, quiet
        // until hovered - not the red window-caption button.
        var closeButton = new Button
        {
            Classes = { "dialogClose" },
            Content = "✕",
            Width = 52,
            Height = 56,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new Avalonia.CornerRadius(0, 11, 0, 0)
        };
        Avalonia.Automation.AutomationProperties.SetName(closeButton, "Close Notice Board");
        ToolTip.SetTip(closeButton, "Close");
        closeButton.Click += (_, _) => window.Close();
        Grid.SetColumn(closeButton, 1);
        titleBar.Children.Add(titleLeft);
        titleBar.Children.Add(closeButton);
        var roundedTitleBar = new Border { Child = titleBar };

        var list = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(24, 0, 24, 20) };
        if (notices.Count == 0)
        {
            list.Children.Add(new Border
            {
                Background = AppThemeService.Brush("Surface_0C1319", "#0C1319"),
                CornerRadius = new Avalonia.CornerRadius(12), Padding = new Avalonia.Thickness(24, 30),
                Child = new StackPanel { Spacing = 8, Children =
                {
                    new TextBlock { Text = "You're all caught up", FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = AppThemeService.Brush("Text_EDF4FB", "#EDF4FB") },
                    new TextBlock { Text = "New features and important updates will appear here.", FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6") }
                } }
            });
        }
        foreach (var notice in notices) list.Children.Add(BuildNoticeCard(notice));

        var closeFooter = new Button { Content = board ? "Done" : "Got it", MinWidth = 104, Height = 38, Padding = new Avalonia.Thickness(20, 0), CornerRadius = new Avalonia.CornerRadius(9), FontSize = 13, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "primaryButton" } };
        closeFooter.Click += (_, _) => window.Close();
        var footerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        footerRow.Children.Add(new TextBlock
        {
            Text = board ? $"{notices.Count} active {(notices.Count == 1 ? "notice" : "notices")}" : "Revisit anytime from the bell.",
            FontSize = 12, Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6"),
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(closeFooter, 1);
        footerRow.Children.Add(closeFooter);
        var footer = new Border
        {
            BorderBrush = AppThemeService.Brush("Surface_232F3A", "#232F3A"), BorderThickness = new Avalonia.Thickness(0, 1, 0, 0),
            Padding = new Avalonia.Thickness(24, 16), Child = footerRow
        };

        var content = new DockPanel { Children = { roundedTitleBar, footer, new ScrollViewer { Content = list, MaxHeight = 520, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } } };
        DockPanel.SetDock(roundedTitleBar, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        var shell = CreateRoundedDialogShell(content);
        window.Content = shell;
        window.Opened += (_, _) => WindowTransparencyFallback.ApplyIfNeeded(window, shell.Background, brush => shell.Background = brush, "rounded-dialog");
        return window;
    }

    private Border BuildNoticeCard(Notice notice)
    {
        var (label, accent) = notice.Severity switch
        {
            "critical" => ("IMPORTANT", "#FF8991"),
            "feature" => ("NEW FEATURE", "#45DDC1"),
            _ => ("INFO", "#91BDFA")
        };
        var accentColor = Color.Parse(accent);
        var accentBrush = new SolidColorBrush(accentColor);
        var tint = new SolidColorBrush(Color.FromArgb(22, accentColor.R, accentColor.G, accentColor.B));

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12,
            Children =
            {
                new Border
                {
                    Background = tint,
                    CornerRadius = new Avalonia.CornerRadius(6),
                    Padding = new Avalonia.Thickness(9, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = label, Foreground = accentBrush, FontSize = 10, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.7 }
                },
                new TextBlock
                {
                    Text = notice.PublishedAt.ToLocalTime().ToString("d MMM yyyy"),
                    Foreground = AppThemeService.Brush("Text_8EA1B6", "#8EA1B6"),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        Grid.SetColumn(header.Children[1], 1);

        var body = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(22, 20, 22, 22), Children = { header, new TextBlock
        {
            Text = notice.Title,
            Foreground = AppThemeService.Brush("Text_EDF4FB", "#EDF4FB"),
            FontSize = 23,
            LineHeight = 29,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        } } };
        var paragraphs = new StackPanel { Spacing = 8 };
        foreach (var block in BuildNoticeBody(notice.Body)) paragraphs.Children.Add(block);
        body.Children.Add(paragraphs);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        if (notice.Link is { } link && NoticeBoardRules.IsAllowedLink(link.Url))
        {
            var linkButton = new Button { Content = new TextBlock { Text = link.Label, TextWrapping = TextWrapping.Wrap }, MaxWidth = 460, MinHeight = 36, Padding = new Avalonia.Thickness(14, 8), Margin = new Avalonia.Thickness(0, 0, 10, 6), CornerRadius = new Avalonia.CornerRadius(8), FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
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
            var acknowledge = new Button { Content = "I understand", MinHeight = 36, Padding = new Avalonia.Thickness(14, 8), Margin = new Avalonia.Thickness(0, 0, 0, 6), CornerRadius = new Avalonia.CornerRadius(8), FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Classes = { "primaryButton" } };
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

        var card = new DockPanel();
        var accentLine = new Border { Height = 3, Background = accentBrush };
        DockPanel.SetDock(accentLine, Dock.Top);
        card.Children.Add(accentLine);
        card.Children.Add(body);
        return new Border
        {
            Background = AppThemeService.Brush("Surface_0C1319", "#0C1319"),
            BorderBrush = notice.IsCritical ? new SolidColorBrush(Color.FromArgb(90, accentColor.R, accentColor.G, accentColor.B)) : AppThemeService.Brush("Surface_232F3A", "#232F3A"),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(12),
            ClipToBounds = true,
            Child = card
        };
    }

    // Enough Markdown for an announcement: "# " headings, "- " bullets and
    // paragraphs, each rendered inline by ReleaseNotesMarkdownRenderer with links
    // limited to the Notice Board's own allow-list.
    private static IEnumerable<Control> BuildNoticeBody(string markdown)
    {
        static bool AllowLink(Uri uri) => NoticeBoardRules.IsAllowedLink(uri.AbsoluteUri);
        var paragraph = new List<string>();

        TextBlock Text(string value, double size = 14, FontWeight? weight = null)
        {
            var text = new TextBlock
            {
                Foreground = AppThemeService.Brush("Text_C4D2E0", "#C4D2E0"),
                FontSize = size,
                FontWeight = weight ?? FontWeight.Normal,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = size * 1.55
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
