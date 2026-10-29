using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Zest.ViewModels;

namespace Zest.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not EmulatorSettingsViewModel emulator)
            return;

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Choose a {emulator.SystemName} game folder",
            AllowMultiple = true,
        });
        foreach (var folder in picked)
        {
            var path = folder.TryGetLocalPath();
            if (path is not null && !emulator.Folders.Contains(path))
                emulator.Folders.Add(path);
        }
    }
}
