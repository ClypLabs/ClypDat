using ClypDat.Core.Settings;

namespace ClypDat.App.ViewModels;

public sealed class FullSessionFormatViewModel(AppSettings settings, Action save) : ViewModelBase
{
    public IReadOnlyList<string> Formats { get; } = new[] { "MKV (Recommended)", "MP4" };
    public string Selected
    {
        get => IsMp4 ? Formats[1] : Formats[0];
        set
        {
            settings.FullSessionContainer = FullSessionFormat.Normalize(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsMp4));
            save();
        }
    }
    public bool IsMp4 => settings.FullSessionContainer == "MP4";
    public string Recommendation => FullSessionFormat.Recommendation;
    public string Warning => FullSessionFormat.Warning;
    public void UseMkv() => Selected = "MKV";
}
