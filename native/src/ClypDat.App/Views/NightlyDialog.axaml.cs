using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ClypDat.App.Services;

namespace ClypDat.App.Views;

public sealed record NightlyStepRow(string Glyph, IBrush Brush, string Label, IBrush LabelBrush, string Status, bool IsRunning,
    bool IsIndeterminate, double Percent, string Detail)
{
    public bool HasDetail => Detail.Length > 0;
}

public sealed record NightlyChangeRow(string Tag, IBrush Brush, string Text);

public enum NightlyDialogMode { Intro, Progress, Available, Ready }

public sealed partial class NightlyDialog : Window
{
    // Rough share of a first build's time per step, for the overall bar.
    private static readonly Dictionary<string, double> Weights = new()
    {
        ["space"] = 1, ["cpp"] = 14, ["git"] = 2, ["source"] = 6, ["dotnet"] = 4, ["avalonia"] = 30, ["app"] = 40, ["check"] = 3
    };

    private readonly Func<Task>? _start;
    private readonly Func<Task>? _restart;
    private NightlyDialogMode _mode;
    private NightlyReport? _report;

    public NightlyDialog() => InitializeComponent();

    public NightlyDialog(NightlyDialogMode mode, Func<Task> start, Func<Task> restart) : this()
    {
        _start = start;
        _restart = restart;
        NightlyBuild.Changed += OnChanged;
        Closed += (_, _) => NightlyBuild.Changed -= OnChanged;
        SetMode(mode);
    }

    public bool StartedBuild { get; private set; }

    private void OnChanged(NightlyReport report) => Dispatcher.UIThread.Post(() =>
    {
        if (_mode == NightlyDialogMode.Progress) Show(report);
    });

    public void SetMode(NightlyDialogMode mode)
    {
        _mode = mode;
        IntroPanel.IsVisible = mode == NightlyDialogMode.Intro;
        ProgressPanel.IsVisible = mode == NightlyDialogMode.Progress;
        ChangesPanel.IsVisible = mode is NightlyDialogMode.Available or NightlyDialogMode.Ready;
        NoteText.IsVisible = false;
        LogButton.IsVisible = false;
        switch (mode)
        {
            case NightlyDialogMode.Intro:
                TitleText.Text = "Switch to Nightly?";
                SecondaryButton.Content = "Cancel";
                PrimaryButton.Content = "Build Nightly";
                PrimaryButton.IsVisible = true;
                break;
            case NightlyDialogMode.Available:
                TitleText.Text = "Nightly update available";
                ShowChanges();
                SecondaryButton.Content = "Later";
                PrimaryButton.Content = "Build update";
                PrimaryButton.IsVisible = true;
                break;
            case NightlyDialogMode.Ready:
                TitleText.Text = "Nightly update ready";
                ShowChanges();
                SecondaryButton.Content = "Later";
                PrimaryButton.Content = "Restart to update";
                PrimaryButton.IsVisible = true;
                break;
            case NightlyDialogMode.Progress:
                Show(NightlyBuild.Last);
                break;
        }
    }

    private void ShowChanges()
    {
        var rows = (NightlyBuild.LastFound?.Changes ?? []).Select(change => change.Kind switch
        {
            "new" => new NightlyChangeRow("NEW", Brush("Semantic_8BD9AE", "#8BD9AE"), change.Text),
            "fixed" => new NightlyChangeRow("FIX", Brush("Semantic_59B6FF", "#59B6FF"), change.Text),
            _ => new NightlyChangeRow("OTHER", Brush("Text_8EA1B6", "#8EA1B6"), change.Text)
        }).ToArray();
        ChangesList.ItemsSource = rows;
        NoChangesText.IsVisible = rows.Length == 0;
    }

    private void Show(NightlyReport? report)
    {
        _report = report;
        var running = report?.Running == true || (report is null && NightlyBuild.IsRunning);
        var installed = report is { Finished: true, Error: null, Note: null } && NightlyBuild.ReadyCommit is not null;
        TitleText.Text = running ? "Building Nightly"
            : report?.Error is not null ? "Nightly build stopped"
            : report?.Note is not null ? "Nightly is up to date"
            : installed ? "Nightly is ready"
            : "Building Nightly";
        if (report is null)
        {
            StepsList.ItemsSource = null;
            TotalBar.Value = 0;
        }
        else
        {
            StepsList.ItemsSource = report.Steps.Select(Row).ToArray();
            TotalBar.Value = Progress(report) * 100;
        }
        ErrorBox.IsVisible = report?.Error is not null;
        ErrorText.Text = report?.Error ?? string.Empty;
        NoteText.Text = report?.Note ?? string.Empty;
        NoteText.IsVisible = report?.Note is not null;
        LogButton.IsVisible = !running && report?.LogPath is not null;
        if (report is null && !running)
        {
            TitleText.Text = "Nightly";
            NoteText.Text = "No Nightly build has run since ClypDat started.";
            NoteText.IsVisible = true;
            SecondaryButton.Content = "Close";
            PrimaryButton.Content = "Build now";
            PrimaryButton.IsVisible = true;
        }
        else if (running)
        {
            SecondaryButton.Content = "Cancel build";
            PrimaryButton.Content = "Hide";
            PrimaryButton.IsVisible = true;
        }
        else if (installed)
        {
            SecondaryButton.Content = "Later";
            PrimaryButton.Content = "Restart to update";
            PrimaryButton.IsVisible = true;
        }
        else
        {
            SecondaryButton.Content = "Close";
            PrimaryButton.Content = "Try again";
            PrimaryButton.IsVisible = report?.Error is not null;
        }
    }

    internal static double Progress(NightlyReport report)
    {
        double total = 0, done = 0;
        foreach (var step in report.Steps)
        {
            var weight = Weights.GetValueOrDefault(step.Id, 2);
            total += weight;
            if (step.State is NightlyStepState.Done or NightlyStepState.Skipped) done += weight;
            else if (step.State == NightlyStepState.Running) done += weight * (step.Progress ?? 0);
        }
        return total > 0 ? done / total : 0;
    }

    private static NightlyStepRow Row(NightlyStep step)
    {
        var muted = Brush("Text_6B7C8C", "#6B7C8C");
        var text = Brush("Text_D6E2F0", "#D6E2F0");
        var (glyph, brush, status) = step.State switch
        {
            NightlyStepState.Done => ("✓", Brush("Semantic_8BD9AE", "#8BD9AE"), "Done"),
            NightlyStepState.Skipped => ("–", muted, "Already there"),
            NightlyStepState.Failed => ("✕", Brush("Semantic_F05A63", "#F05A63"), "Failed"),
            NightlyStepState.Running => ("●", Brush("Semantic_A8CFFF", "#A8CFFF"), step.Progress is { } progress ? $"{progress * 100:0}%" : string.Empty),
            _ => ("○", muted, string.Empty)
        };
        var detail = step.State is NightlyStepState.Running or NightlyStepState.Failed ? step.Detail : string.Empty;
        if (step.Id == "app" && step.State == NightlyStepState.Running && detail.Length == 0)
            detail = "This part takes the longest, often 10 minutes or more.";
        return new NightlyStepRow(glyph, brush, step.Label, step.State == NightlyStepState.Pending ? muted : text, status,
            step.State == NightlyStepState.Running, step.Progress is null, (step.Progress ?? 0) * 100, detail);
    }

    private static IBrush Brush(string key, string fallback) => AppThemeService.Brush(key, fallback);

    private async void PrimaryButton_OnClick(object? sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case NightlyDialogMode.Intro:
            case NightlyDialogMode.Available:
                StartedBuild = true;
                SetMode(NightlyDialogMode.Progress);
                if (_start is not null) await _start();
                break;
            case NightlyDialogMode.Ready:
                if (_restart is not null) await _restart();
                break;
            case NightlyDialogMode.Progress:
                if (NightlyBuild.IsRunning) Close();
                else if ((_report is null || _report.Error is not null) && _start is not null) await _start();
                else if (NightlyBuild.ReadyCommit is not null && _restart is not null) await _restart();
                break;
        }
    }

    private void SecondaryButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_mode == NightlyDialogMode.Progress && NightlyBuild.IsRunning)
        {
            NightlyBuild.Cancel();
            return;
        }
        Close();
    }

    private void HideButton_OnClick(object? sender, RoutedEventArgs e) => Close();

    private void LogButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_report?.LogPath is not { } path || !File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception error) { AppLog.Error("Nightly: opening the log failed", error); }
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void Dialog_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
