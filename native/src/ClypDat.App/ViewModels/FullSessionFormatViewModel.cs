using ClypDat.Core.Settings;

namespace ClypDat.App.ViewModels;

public sealed class FullSessionFormatViewModel(AppSettings settings, Action save) : ViewModelBase
{
    private bool _warningIgnored;
    public IReadOnlyList<string> Formats { get; } = new[] { "MKV (Recommended)", "MP4" };
    public string Selected
    {
        get => IsMp4 ? Formats[1] : Formats[0];
        set
        {
            settings.FullSessionContainer = FullSessionFormat.Normalize(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMp4));
            OnPropertyChanged(nameof(IsWarningVisible));
            save();
        }
    }
    public bool IsMp4 => settings.FullSessionContainer == "MP4";
    public bool IsWarningVisible => IsMp4 && !settings.HideFullSessionMp4Warning && !_warningIgnored;
    public string Recommendation => FullSessionFormat.Recommendation;
    public string Warning => FullSessionFormat.Warning;
    public void UseMkv() => Selected = "MKV";
    public void IgnoreWarning()
    {
        if (_warningIgnored) return;
        _warningIgnored = true;
        OnPropertyChanged(nameof(IsWarningVisible));
    }
    public void HideWarningPermanently()
    {
        if (settings.HideFullSessionMp4Warning) return;
        settings.HideFullSessionMp4Warning = true;
        OnPropertyChanged(nameof(IsWarningVisible));
        save();
    }
}
