using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Eve.App.ViewModels;

namespace Eve.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private async void FolderButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select library folder",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder?.Path.LocalPath is { Length: > 0 } path)
        {
            ViewModel?.LoadLibraryFolder(path);
        }
    }

    private async void OpenButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open video",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video files")
                {
                    Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.m4v", "*.wmv" }
                }
            }
        });

        var file = files.FirstOrDefault();
        if (file?.Path.LocalPath is { Length: > 0 } path)
        {
            ViewModel?.OpenVideoFile(path);
        }
    }

    private void RefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ViewModel?.RefreshLibrary();
    }
}
