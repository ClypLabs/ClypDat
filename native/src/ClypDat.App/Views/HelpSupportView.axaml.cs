using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views;

public enum HelpSupportAction { Changelog, Updates, Faq, Bug, Feature, Discord, Diagnostics, Logs }

public sealed class HelpSupportCard(
    HelpSupportAction action, string title, string description, string buttonLabel, string icon,
    bool external = false, string busyLabel = "Opening…") : ViewModelBase
{
    public HelpSupportAction Action { get; } = action;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string ButtonLabel { get; } = buttonLabel;
    public Geometry Icon { get; } = Geometry.Parse(icon);
    public Geometry ActionIcon { get; } = Geometry.Parse(external
        ? "M4,12 L12,4 M5,4 H12 V11"
        : "M3,8 H13 M9,4 L13,8 L9,12");
    public string ActionLabel => IsBusy ? busyLabel : ButtonLabel;

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(ActionLabel));
        }
    }
}

public sealed partial class HelpSupportView : UserControl
{
    public IReadOnlyList<HelpSupportCard> News { get; } =
    [
        new(HelpSupportAction.Changelog, "Read changelog", "The latest additions and fixes in your installed version.",
            "Read changelog", "M6,3 H15 L19,7 V21 H6 Z M14,3 V8 H19 M9,12 H16 M9,16 H14", busyLabel: "Loading…"),
        new(HelpSupportAction.Updates, "Check for updates", "Keep ClypDat up to date with the latest improvements.",
            "Check for updates", "M20,9 A8,8 0 0 0 6,6 L3,9 M3,3 V9 H9 M4,15 A8,8 0 0 0 18,18 L21,15 M15,15 H21 V21", busyLabel: "Checking…")
    ];

    public IReadOnlyList<HelpSupportCard> Support { get; } =
    [
        new(HelpSupportAction.Faq, "Find a solution", "Quick answers to common questions about recording and clips.",
            "Check FAQ", "M3,4 H8 Q12,4 12,7 Q12,4 16,4 H21 V20 H16 Q12,20 12,22 Q12,20 8,20 H3 Z M12,7 V22", external: true),
        new(HelpSupportAction.Bug, "Report a bug", "Something not working? Tell us what happened so we can fix it.",
            "Report a bug", "M8,9 H16 V16 A4,4 0 0 1 8,16 Z M9,9 V7 A3,3 0 0 1 15,7 V9 M8,12 H4 M8,16 H3 M16,12 H20 M16,16 H21 M6,5 L9,7 M18,5 L15,7 M12,10 V18", external: true),
        new(HelpSupportAction.Feature, "Suggest a feature", "Have an idea for ClypDat? Share what you'd like to see next.",
            "Share an idea", "M9,17 C9,13 6,13 6,9 A6,6 0 0 1 18,9 C18,13 15,13 15,17 Z M9,20 H15 M11,23 H13 M12,1 V0 M3,3 L1,1 M21,3 L23,1", external: true),
        new(HelpSupportAction.Discord, "Join the community", "Ask questions, swap tips, and chat with other ClypDat users.",
            "Join Discord", "M3,4 H17 A2,2 0 0 1 19,6 V13 A2,2 0 0 1 17,15 H8 L3,19 V6 Z M8,19 H16 L21,22 V10 M7,9 H7.1 M11,9 H11.1 M15,9 H15.1", external: true)
    ];

    public IReadOnlyList<HelpSupportCard> SelfHelp { get; } =
    [
        new(HelpSupportAction.Diagnostics, "Export diagnostic bundle", "Save a ZIP of logs and diagnostics to share with a bug report.",
            "Export bundle", "M4,3 H20 V7 H4 Z M6,7 V21 H18 V7 M12,10 V17 M9,14 L12,17 L15,14", busyLabel: "Exporting…"),
        new(HelpSupportAction.Logs, "Open logs", "Browse ClypDat's log files on your computer.",
            "Open logs", "M3,6 V20 H21 V8 H11 L8,4 H3 Z M3,8 H21")
    ];

    public HelpSupportView() => InitializeComponent();

    private async void Action_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HelpSupportCard card } || card.IsBusy ||
            TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        card.IsBusy = true;
        try { await owner.RunHelpActionAsync(card.Action); }
        finally { card.IsBusy = false; }
    }
}
