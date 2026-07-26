using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media.Imaging;

namespace ClypDat.App.ViewModels;

// A group tile in the sidebar's game rail - either one the user made (right-
// click "Move to folder" / "New Folder", or dragging a game onto another) or
// the automatic "More Games" folder the rail folds overflow into when nothing
// has been customised yet (see MainWindowViewModel.RebuildGameRail). Games is
// the SAME FilterOptionViewModel instances GameFilterOptions holds, not
// copies, so an icon landing or a checkbox change is visible here too without
// a rebuild.
public sealed class GameRailFolderViewModel : ViewModelBase
{
    private readonly Action<string, bool>? _onExpandedChanged;

    public GameRailFolderViewModel(string id, string name, bool isAutomatic, Action<string, bool>? onExpandedChanged = null)
    {
        Id = id;
        _name = name;
        IsAutomatic = isAutomatic;
        _onExpandedChanged = onExpandedChanged;
        Games.CollectionChanged += OnGamesCollectionChanged;
    }

    // Slot1Icon/HasSlot1Icon/etc only re-raise on an explicit NotifyGamesChanged
    // call, not automatically whenever a member FilterOptionViewModel changes -
    // so a game's icon landing (or a refresh replacing it) asynchronously,
    // after this folder was already built, left the collapsed tile stuck
    // showing the letter fallback it started with. Forwarding each member's
    // own PropertyChanged into a re-raise here keeps the mosaic live the same
    // way the loose, non-folder rail icons already are (they bind Icon
    // directly, so INotifyPropertyChanged alone is enough for them).
    private void OnGamesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (FilterOptionViewModel game in e.OldItems)
                game.PropertyChanged -= OnGamePropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (FilterOptionViewModel game in e.NewItems)
                game.PropertyChanged += OnGamePropertyChanged;
        }

        NotifyGamesChanged();
    }

    private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FilterOptionViewModel.Icon) or nameof(FilterOptionViewModel.HasIcon))
        {
            NotifyGamesChanged();
        }
    }

    public string Id { get; }

    // True only for the synthetic overflow folder in automatic (uncustomised)
    // mode - it has no backing GameRailFolder in settings, can't be renamed or
    // ungrouped, and stops existing the instant the rail is customised (its
    // games become a real, persisted folder at that point instead).
    public bool IsAutomatic { get; }

    private string _name;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public ObservableCollection<FilterOptionViewModel> Games { get; } = new();

    private bool _isExpanded;

    // Every rail action (a drag, a rename, moving a game in or out) rebuilds
    // GameRailEntries from scratch, which means a brand new instance of this
    // class for every folder each time - without telling the owner when this
    // opens or closes, that rebuild would silently reset every folder back to
    // collapsed on the very next action, closing it out from under whoever
    // just dragged something into it.
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            _onExpandedChanged?.Invoke(Id, value);
        }
    }

    // Sets the initial state from the owner's remembered set without
    // re-announcing it right back - only a genuine UI-driven toggle should
    // fire the callback.
    public void SetExpandedSilently(bool value) => _isExpanded = value;

    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    public bool IsEmpty => Games.Count == 0;
    public string CountLabel => Games.Count.ToString();

    // Which of four hand-laid-out arrangements the collapsed tile uses -
    // mutually exclusive, so exactly one applies. A single fixed 2x2 grid for
    // every count left empty cells (dead space) whenever a folder held fewer
    // than 4 games, which is what actually made 2- and 3-game folders look
    // lopsided rather than "the icons are just small". 1 game fills the
    // whole tile, 2 sit side by side, 3 are two-over-one, and only 4+ uses
    // the real 2x2 (see HasSlot4Cell below).
    public bool IsSingleGame => Games.Count == 1;
    public bool IsTwoGames => Games.Count == 2;
    public bool IsThreeGames => Games.Count == 3;
    public bool IsFourPlusGames => Games.Count >= 4;

    // Collapsed tile is a mosaic of up to the first 4 member icons, Discord-
    // folder style - exposed as flat per-slot bool/string/bitmap properties
    // rather than binding through Games[i].HasIcon etc directly in XAML, so
    // there's no null-chain or out-of-range binding to reason about at
    // runtime. A 5th+ game doesn't get a 5th slot; the 4th cell becomes a
    // "+N" count instead of that game's own icon once there are more than 4.
    public bool HasSlot1 => Games.Count > 0;
    public bool HasSlot2 => Games.Count > 1;
    public bool HasSlot3 => Games.Count > 2;
    public bool HasSlot4Cell => Games.Count >= 4;
    public bool HasOverflowBadge => Games.Count > 4;
    public string OverflowBadgeLabel => Games.Count > 4 ? $"+{Games.Count - 3}" : string.Empty;

    public bool HasSlot1Icon => Games.Count > 0 && Games[0].HasIcon;
    public bool HasSlot2Icon => Games.Count > 1 && Games[1].HasIcon;
    public bool HasSlot3Icon => Games.Count > 2 && Games[2].HasIcon;
    public bool HasSlot4Icon => Games.Count == 4 && Games[3].HasIcon;
    // A plain condition rather than stacking two IsVisible bindings in XAML:
    // the 4th cell's initial-letter fallback only makes sense when there IS
    // a 4th game to letter AND it isn't already showing the overflow "+N".
    public bool ShowSlot4Initial => HasSlot4Cell && !HasSlot4Icon && !HasOverflowBadge;

    public Bitmap? Slot1Icon => Games.Count > 0 ? Games[0].Icon : null;
    public Bitmap? Slot2Icon => Games.Count > 1 ? Games[1].Icon : null;
    public Bitmap? Slot3Icon => Games.Count > 2 ? Games[2].Icon : null;
    public Bitmap? Slot4Icon => Games.Count == 4 ? Games[3].Icon : null;

    public string Slot1Initial => InitialOf(0);
    public string Slot2Initial => InitialOf(1);
    public string Slot3Initial => InitialOf(2);
    public string Slot4Initial => Games.Count == 4 ? InitialOf(3) : string.Empty;

    private string InitialOf(int index) =>
        index < Games.Count && !string.IsNullOrEmpty(Games[index].Key)
            ? char.ToUpperInvariant(Games[index].Key[0]).ToString()
            : string.Empty;

    public void NotifyGamesChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(IsSingleGame));
        OnPropertyChanged(nameof(IsTwoGames));
        OnPropertyChanged(nameof(IsThreeGames));
        OnPropertyChanged(nameof(IsFourPlusGames));
        OnPropertyChanged(nameof(HasSlot1));
        OnPropertyChanged(nameof(HasSlot2));
        OnPropertyChanged(nameof(HasSlot3));
        OnPropertyChanged(nameof(HasSlot4Cell));
        OnPropertyChanged(nameof(HasOverflowBadge));
        OnPropertyChanged(nameof(OverflowBadgeLabel));
        OnPropertyChanged(nameof(HasSlot1Icon));
        OnPropertyChanged(nameof(HasSlot2Icon));
        OnPropertyChanged(nameof(HasSlot3Icon));
        OnPropertyChanged(nameof(HasSlot4Icon));
        OnPropertyChanged(nameof(ShowSlot4Initial));
        OnPropertyChanged(nameof(Slot1Icon));
        OnPropertyChanged(nameof(Slot2Icon));
        OnPropertyChanged(nameof(Slot3Icon));
        OnPropertyChanged(nameof(Slot4Icon));
        OnPropertyChanged(nameof(Slot1Initial));
        OnPropertyChanged(nameof(Slot2Initial));
        OnPropertyChanged(nameof(Slot3Initial));
        OnPropertyChanged(nameof(Slot4Initial));
    }
}
