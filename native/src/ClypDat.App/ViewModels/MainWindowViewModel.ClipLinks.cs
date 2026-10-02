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
        // Plus is the middle plan and the one most people should pick.
        IsRecommended = offer.Id == "plus";
        StorageLabel = MainWindowViewModel.FormatStorage(offer.StorageBytes);
        MonthlySlug = offer.MonthlySlug;
        YearlySlug = offer.YearlySlug;
        MonthlyLabel = offer.MonthlyPrice is { } monthly ? $"{monthly} / month" : "Monthly";
        YearlyLabel = offer.YearlyPrice is { } yearly ? $"{yearly} / year" : "Yearly";
    }

    public string Name { get; }
    public bool IsRecommended { get; }
    public string StorageLabel { get; }
    public string MonthlySlug { get; }
    public string YearlySlug { get; }
    public string MonthlyLabel { get; }
    public string YearlyLabel { get; }
}

// Settings -> Account -> the clip-link plan: storage used, the
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
            if (ClipPlan is { IsActive: false, ClipsDeleteAt: { } deleteAt })
                return $"Your plan has ended. Your shared clips stay up until {FormatPlanDate(deleteAt)}, then they and their links are deleted. Pick a plan to keep them.";
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
        OnPropertyChanged(nameof(SharedClipsStorageLabel));
        // A plan that has just arrived closes the list it was bought from.
        if (ClipPlanCanManage && ClipPlansOpen) ClipPlansOpen = false;
        // Signed out, or no active plan: the page goes with its rail button.
        // Posted because the poll reports from a background thread, and the
        // page's visibility drives the editor surface.
        if (!ClipPlanActive) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (!ClipPlanActive) CloseSharedClips(); });
        // Another account (or none) may be signed in now.
        if (!ClypDatAccountIsConnected) _liveSharedIds = null;
    }

    // --- The Shared clips page -------------------------------------------

    public ObservableCollection<SharedClipViewModel> SharedClips { get; } = [];
    private CancellationTokenSource? _sharedClipsCts;

    private bool _isSharedClipsVisible;
    public bool IsSharedClipsVisible
    {
        get => _isSharedClipsVisible;
        private set
        {
            if (!SetProperty(ref _isSharedClipsVisible, value)) return;
            OnPropertyChanged(nameof(IsLibraryVisible));
            OnPropertyChanged(nameof(ShowLibraryActions));
            OnPropertyChanged(nameof(ShowLibraryStatus));
            OnPropertyChanged(nameof(ShowHeaderUpdateButton));
        }
    }

    private bool _sharedClipsLoading;
    public bool SharedClipsLoading
    {
        get => _sharedClipsLoading;
        private set { if (SetProperty(ref _sharedClipsLoading, value)) OnPropertyChanged(nameof(SharedClipsEmpty)); }
    }

    private bool _sharedClipsLoaded;
    public bool SharedClipsEmpty => _sharedClipsLoaded && !SharedClipsLoading && SharedClips.Count == 0 && !SharedClipsHasMessage;

    private string? _sharedClipsMessage;
    public string? SharedClipsMessage
    {
        get => _sharedClipsMessage;
        private set
        {
            if (!SetProperty(ref _sharedClipsMessage, value)) return;
            OnPropertyChanged(nameof(SharedClipsHasMessage));
            OnPropertyChanged(nameof(SharedClipsEmpty));
        }
    }
    public bool SharedClipsHasMessage => !string.IsNullOrWhiteSpace(SharedClipsMessage);

    public string SharedClipsStorageLabel => ClipPlan is { IsActive: true } plan
        ? $"{FormatStorage(plan.UsedBytes)} of {FormatStorage(plan.StorageBytes)} used"
        : ClipPlan is { } lapsed && lapsed.UsedBytes > 0 ? $"{FormatStorage(lapsed.UsedBytes)} used · no plan" : "No plan";

    public void OpenSharedClips()
    {
        if (IsSharedClipsVisible) return;
        IsSharedClipsVisible = true;
        IsEditorVisible = false;
        _ = LoadSharedClipsAsync();
    }

    public void CloseSharedClips()
    {
        if (!IsSharedClipsVisible) return;
        IsSharedClipsVisible = false;
        _sharedClipsCts?.Cancel();
    }

    /// <summary>Reads the list from clypdat.xyz. Also the page's Refresh button.</summary>
    public async Task LoadSharedClipsAsync()
    {
        _sharedClipsCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _sharedClipsCts = cts;
        SharedClipsLoading = true;
        SharedClipsMessage = null;
        try
        {
            var token = await GetClipHostingTokenAsync(cts.Token);
            var list = await ClipHostingService.ListAsync(token, cts.Token);
            if (!ReferenceEquals(_sharedClipsCts, cts)) return;
            SharedClips.Clear();
            foreach (var clip in list.Clips) SharedClips.Add(new SharedClipViewModel(clip));
            _liveSharedIds = list.Clips.Select(clip => clip.Id).ToHashSet(StringComparer.Ordinal);
            _clypDatAccount.SetPlanUsage(list.UsedBytes);
            foreach (var card in SharedClips) _ = card.LoadThumbnailAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ReferenceEquals(_sharedClipsCts, cts)) { return; }
        catch (Exception error) when (error is ClipHostingException or InvalidOperationException)
        {
            SharedClipsMessage = error.Message;
        }
        catch (Exception error)
        {
            AppLog.Error("Shared clips: listing failed.", error);
            SharedClipsMessage = "ClypDat couldn't be reached. Check your connection and try again.";
        }
        finally
        {
            if (ReferenceEquals(_sharedClipsCts, cts))
            {
                _sharedClipsLoaded = true;
                SharedClipsLoading = false;
                OnPropertyChanged(nameof(SharedClipsEmpty));
            }
        }
    }

    // --- Links remembered on library clips -------------------------------

    // IDs of the account's clips that are still live, from the site's list.
    // Loaded once a session (or by the Shared clips page), so a remembered
    // link is never copied after the clip behind it was deleted elsewhere.
    private HashSet<string>? _liveSharedIds;

    private async Task<HashSet<string>?> LiveSharedIdsAsync()
    {
        if (_liveSharedIds is not null) return _liveSharedIds;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var token = await GetClipHostingTokenAsync(timeout.Token);
            var list = await ClipHostingService.ListAsync(token, timeout.Token);
            _liveSharedIds = list.Clips.Select(clip => clip.Id).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception error)
        {
            AppLog.Info($"Clip links: could not check which links are live ({error.Message}).");
        }
        return _liveSharedIds;
    }

    /// <summary>
    /// The tile's link button, when the clip already has a link: copies it and
    /// returns true. Returns false (and forgets the link) when the clip behind
    /// it has been deleted, so the button uploads it again instead.
    /// </summary>
    internal async Task<bool> TryCopySharedLinkAsync(ClipCardViewModel card, Func<string, Task> copy)
    {
        if (card is not { HasSharedLink: true, SharedClipId: { } id, SharedClipUrl: { } url }) return false;
        // Signed out or the site unreachable: the link is most likely still
        // good, and copying it costs nothing if it is not.
        var live = ClypDatAccountIsConnected ? await LiveSharedIdsAsync() : null;
        if (live is not null && !live.Contains(id))
        {
            card.SetSharedLink(null, null);
            return false;
        }
        await copy(url);
        card.LinkCopied = true;
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => card.LinkCopied = false));
        return true;
    }

    /// <summary>Remembers a new link on the library clip it was made from.</summary>
    internal void RecordSharedLink(string clipPath, HostedClip clip)
    {
        _liveSharedIds?.Add(clip.Id);
        var card = AllClips.FirstOrDefault(item => string.Equals(item.Path, clipPath, StringComparison.OrdinalIgnoreCase));
        if (card is not null) card.SetSharedLink(clip.Id, clip.Url);
        else if (!string.IsNullOrWhiteSpace(Settings.LibraryFolder)) ClipInfoSidecar.SaveSharedLink(Settings.LibraryFolder, clipPath, clip.Id, clip.Url);
    }

    private void ForgetSharedLink(string clipId)
    {
        _liveSharedIds?.Remove(clipId);
        foreach (var card in AllClips.Where(item => item.SharedClipId == clipId).ToArray()) card.SetSharedLink(null, null);
    }

    /// <summary>First press arms the card; the second deletes the clip and its link for good.</summary>
    public async Task DeleteSharedClipAsync(SharedClipViewModel clip)
    {
        if (clip.IsBusy) return;
        if (!clip.ConfirmingDelete)
        {
            foreach (var other in SharedClips) other.ConfirmingDelete = false;
            clip.ConfirmingDelete = true;
            return;
        }
        clip.IsBusy = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = await GetClipHostingTokenAsync(timeout.Token);
            await ClipHostingService.DeleteAsync(token, clip.Id, timeout.Token);
            SharedClips.Remove(clip);
            ForgetSharedLink(clip.Id);
            NoteClipStorageChange(-clip.Bytes);
            SharedClipsMessage = null;
            OnPropertyChanged(nameof(SharedClipsEmpty));
        }
        catch (Exception error) when (error is ClipHostingException or InvalidOperationException)
        {
            SharedClipsMessage = error.Message;
        }
        catch (Exception error)
        {
            AppLog.Error("Shared clips: delete failed.", error);
            SharedClipsMessage = "The clip could not be deleted. Check your connection and try again.";
        }
        finally
        {
            clip.IsBusy = false;
            clip.ConfirmingDelete = false;
        }
    }

    /// <summary>"See plans": shows the plans with this country's prices, or hides them again.</summary>
    public async Task ToggleClipPlansAsync()
    {
        if (ClipPlansOpen)
        {
            ClipPlansOpen = false;
            return;
        }
        ClipPlansOpen = await LoadClipPlanOffersAsync();
    }

    /// <summary>
    /// Fills <see cref="ClipPlanOffers"/> with this country's prices. False,
    /// with the reason in <see cref="ClipPlanMessage"/>, when there are none.
    /// Settings' plan list and the plans dialog both draw from it.
    /// </summary>
    public async Task<bool> LoadClipPlanOffersAsync()
    {
        var loaded = false;
        await RunClipPlanActionAsync(async cancellationToken =>
        {
            var token = await GetClipHostingTokenAsync(cancellationToken);
            var offers = await ClipHostingService.GetPlansAsync(token, cancellationToken);
            ClipPlanOffers.Clear();
            foreach (var offer in offers) ClipPlanOffers.Add(new ClipPlanOfferViewModel(offer));
            loaded = ClipPlanOffers.Count > 0;
            if (!loaded) ClipPlanMessage = "Plans are unavailable right now.";
        });
        return loaded;
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
