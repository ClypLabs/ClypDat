using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClypDat.App.ViewModels;

namespace ClypDat.App.Views;

public partial class ClipPlansDialog : Window
{
    private readonly MainWindowViewModel? _viewModel;

    // Avalonia's XAML loader needs a parameterless constructor to accept this
    // as a top-level control; the one below is what actually opens.
    public ClipPlansDialog() => InitializeComponent();

    public ClipPlansDialog(MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        Opened += async (_, _) => await viewModel.LoadClipPlanOffersAsync();
    }

    // Bought in the browser: the poll brings the plan, and there is nothing
    // left to choose here.
    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ClipPlanActive) && _viewModel?.ClipPlanActive == true)
            Dispatcher.UIThread.Post(Close);
    }

    private async void Buy_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string slug } && _viewModel is { } vm) await vm.BuyClipPlanAsync(slug);
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

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => Close();
}
