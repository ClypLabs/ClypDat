using Avalonia.Controls;

namespace ClypDat.App.Controls;

/// <summary>
/// "Paused by ClypDat" card for a settings section whose feature a remote kill
/// switch has turned off. DataContext is a <see cref="ViewModels.PolicyPause"/>.
/// </summary>
public sealed partial class PolicyPauseBanner : UserControl
{
    public PolicyPauseBanner()
    {
        InitializeComponent();
    }
}
