using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using ClypDat.App.Services;

namespace ClypDat.App.ViewModels;

/// <summary>One plan in Settings' plan list, with its prices in the buyer's currency.</summary>
public sealed class ClipPlanOfferViewModel
{
    internal ClipPlanOfferViewModel(ClipPlanOffer offer)
    {
        Name = offer.Name;
        StorageLabel = MainWindowViewModel.FormatStorage(offer.StorageBytes);
        MonthlySlug = offer.MonthlySlug;
        YearlySlug = offer.YearlySlug;
        MonthlyLabel = offer.MonthlyPrice is { } monthly ? $"{monthly} / month" : "Monthly";
        YearlyLabel = offer.YearlyPrice is { } yearly ? $"{yearly} / year" : "Yearly";
    }

    public string Name { get; }
    public string StorageLabel { get; }
    public string MonthlySlug { get; }
    public string YearlySlug { get; }
    public string MonthlyLabel { get; }
    public string YearlyLabel { get; }
}

// Settings -> Connected accounts -> the clip-link plan: storage used, the
// plans on offer, buying one and managing it. Everything here is for show and
// for opening the right page; the site alone decides what an account may do.
public sealed partial class MainWindowViewModel
{
    // Checkout happens in the browser; nothing tells the app when it is done,
    // so the poll watches this long for the plan to arrive.
    private static readonly TimeSpan CheckoutWatchWindow = TimeSpan.FromMinutes(10);

    public ObservableCollection<ClipPlanOfferViewModel> ClipPlanOffers { get; } = [];

    public bool ClipPlanActive => ClipPlan?.IsActive == true;
    public string ClipPlanTitle => ClipPlan is { IsActive: true, PlanName: { } name } ? name : "No plan";

    public string ClipPlanStatus
    {
        get
        {
            if (ClipPlan is not { IsActive: true } plan) return "Share clips as links anyone can open. Plans start at 50 GB of storage.";
            if (plan.Source == "grant")
                return plan.EndsAt is { } until ? $"Given to your account until {FormatPlanDate(until)}." : "Given to your account.";
            if (plan.RenewsAt is { } renews) return $"Renews {FormatPlanDate(renews)}.";
            return plan.EndsAt is { } ends ? $"Cancelled. You keep {plan.PlanName} until {FormatPlanDate(ends)}." : string.Empty;
        }
    }

    public string ClipStorageLabel => ClipPlan is { IsActive: true } plan
        ? $"{FormatStorage(plan.UsedBytes)} of {FormatStorage(plan.StorageBytes)} used"
        : string.Empty;
    public double ClipStoragePercent => ClipPlan is { IsActive: true, StorageBytes: > 0 } plan
        ? Math.Clamp(plan.UsedBytes * 100.0 / plan.StorageBytes, 0, 100)
        : 0;
    // A subscription changes plan or cancels in the portal; a granted plan
    // (or none) buys one here.
    public bool ClipPlanCanManage => ClipPlan is { IsActive: true, Source: "subscription" };
    public bool ClipPlanCanBuy => ClipLinksOffered && !ClipPlanCanManage;

    private bool _clipPlansOpen;
    public bool ClipPlansOpen
    {
        get => _clipPlansOpen;
        private set => SetProperty(ref _clipPlansOpen, value);
    }

    private bool _clipPlanBusy;
    public bool ClipPlanBusy
    {
        get => _clipPlanBusy;
        private set => SetProperty(ref _clipPlanBusy, value);
    }

    private string? _clipPlanMessage;
    public string? ClipPlanMessage
    {
        get => _clipPlanMessage;
        private set { if (SetProperty(ref _clipPlanMessage, value)) OnPropertyChanged(nameof(ClipPlanHasMessage)); }
    }
    public bool ClipPlanHasMessage => !string.IsNullOrWhiteSpace(ClipPlanMessage);

    private void RaiseClipPlanChanged()
    {
        OnPropertyChanged(nameof(ClipPlan));
        OnPropertyChanged(nameof(ClipLinksOffered));
        OnPropertyChanged(nameof(ClipPlanActive));
        OnPropertyChanged(nameof(ClipPlanTitle));
        OnPropertyChanged(nameof(ClipPlanStatus));
        OnPropertyChanged(nameof(ClipStorageLabel));
        OnPropertyChanged(nameof(ClipStoragePercent));
        OnPropertyChanged(nameof(ClipPlanCanManage));
        OnPropertyChanged(nameof(ClipPlanCanBuy));
        // A plan that has just arrived closes the list it was bought from.
        if (ClipPlanCanManage && ClipPlansOpen) ClipPlansOpen = false;
    }

    /// <summary>"See plans": shows the plans with this country's prices, or hides them again.</summary>
    public async Task ToggleClipPlansAsync()
    {
        if (ClipPlansOpen)
        {
            ClipPlansOpen = false;
            return;
        }
        await RunClipPlanActionAsync(async cancellationToken =>
        {
            var token = await GetClipHostingTokenAsync(cancellationToken);
            var offers = await ClipHostingService.GetPlansAsync(token, cancellationToken);
            ClipPlanOffers.Clear();
            foreach (var offer in offers) ClipPlanOffers.Add(new ClipPlanOfferViewModel(offer));
            ClipPlansOpen = ClipPlanOffers.Count > 0;
            if (!ClipPlansOpen) ClipPlanMessage = "Plans are unavailable right now.";
        });
    }

    /// <summary>Opens Polar's checkout for one plan in the browser, then watches for the plan to arrive.</summary>
    public Task BuyClipPlanAsync(string slug) => RunClipPlanActionAsync(async cancellationToken =>
    {
        var token = await GetClipHostingTokenAsync(cancellationToken);
        var url = await ClipHostingService.StartCheckoutAsync(token, slug, cancellationToken);
        _clypDatAccount.ExpectLinkChange(CheckoutWatchWindow);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        ClipPlanMessage = "Finish checking out in your browser. Your plan shows up here as soon as it's paid.";
    });

    /// <summary>Card, invoices, changing plan and cancelling all live in the subscription portal.</summary>
    public Task ManageClipPlanAsync() => RunClipPlanActionAsync(async cancellationToken =>
    {
        var token = await GetClipHostingTokenAsync(cancellationToken);
        var url = await ClipHostingService.PortalAsync(token, cancellationToken);
        _clypDatAccount.ExpectLinkChange(CheckoutWatchWindow);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    });

    private async Task RunClipPlanActionAsync(Func<CancellationToken, Task> action)
    {
        if (ClipPlanBusy) return;
        ClipPlanBusy = true;
        ClipPlanMessage = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await action(timeout.Token);
        }
        catch (Exception error) when (error is ClipHostingException or InvalidOperationException)
        {
            ClipPlanMessage = error.Message;
        }
        catch (Exception error)
        {
            AppLog.Error("Clip links: plan action failed.", error);
            ClipPlanMessage = "ClypDat couldn't be reached. Check your connection and try again.";
        }
        finally
        {
            ClipPlanBusy = false;
        }
    }

    // Decimal GB like the website: storage is sold in GB of 1,000,000,000
    // bytes. MB below 1 GB, so a few small clips do not read as "0 GB".
    internal static string FormatStorage(long bytes) => bytes < 1_000_000_000
        ? $"{Math.Round(bytes / 1_000_000.0).ToString("0", CultureInfo.InvariantCulture)} MB"
        : bytes % 1_000_000_000 == 0
            ? $"{bytes / 1_000_000_000} GB"
            : $"{(bytes / 1_000_000_000.0).ToString("0.0", CultureInfo.InvariantCulture)} GB";

    private static string FormatPlanDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
}
