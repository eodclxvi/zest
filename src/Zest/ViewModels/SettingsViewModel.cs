using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zest.Models;
using Zest.Services;

namespace Zest.ViewModels;

public partial class EmulatorSettingsViewModel : ViewModelBase
{
    public required Platform Platform { get; init; }
    public string SystemName => PlatformInfo.Name(Platform);
    public string EmulatorName => PlatformInfo.Emulator(Platform);
    public bool SupportsInstalled => Platform == Platform.N3DS;
    public List<string> Excluded { get; set; } = [];

    [ObservableProperty]
    public partial string Command { get; set; } = "";

    [ObservableProperty]
    public partial bool IncludeInstalled { get; set; }

    public ObservableCollection<string> Folders { get; } = [];

    [RelayCommand]
    private void RemoveFolder(string folder) => Folders.Remove(folder);
}

public partial class ThemeOption : ObservableObject
{
    public required string Name { get; init; }
    public required IBrush Swatch { get; init; }
    public required IBrush SwatchBackground { get; init; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public partial class SettingsViewModel : ViewModelBase
{
    public ObservableCollection<EmulatorSettingsViewModel> Emulators { get; } = [];
    public ObservableCollection<ThemeOption> Themes { get; } = [];

    /// <summary>The theme in use when Settings opened, restored on Cancel.</summary>
    public string OriginalTheme { get; }

    [ObservableProperty]
    public partial string ProdKeys { get; set; } = "";

    [ObservableProperty]
    public partial string TitleKeys { get; set; } = "";

    [ObservableProperty]
    public partial string Theme { get; set; } = "";

    public SettingsViewModel(AppConfig config)
    {
        OriginalTheme = config.Theme;
        foreach (var t in ThemeService.Themes)
            Themes.Add(new ThemeOption { Name = t.Name, Swatch = new SolidColorBrush(t.Primary), SwatchBackground = new SolidColorBrush(t.Background) });
        LoadFrom(config);
        SelectTheme(Themes.FirstOrDefault(t => t.Name == ThemeService.Find(config.Theme).Name) ?? Themes[0]);
    }

    /// <summary>Previews the theme on every open window right away; Save keeps it, Cancel reverts.</summary>
    [RelayCommand]
    private void SelectTheme(ThemeOption option)
    {
        foreach (var t in Themes)
            t.IsSelected = t == option;
        Theme = option.Name;
        ThemeService.Apply(option.Name);
    }

    [RelayCommand]
    private void DetectAgain() => LoadFrom(AppConfig.Detect());

    public AppConfig ToConfig(AppConfig current)
    {
        foreach (var e in Emulators)
        {
            var target = current.For(e.Platform);
            target.Command = e.Command.Trim();
            target.Folders = e.Folders.ToList();
            target.Excluded = e.Excluded;
            target.IncludeInstalled = e.IncludeInstalled;
        }
        current.ProdKeys = ProdKeys.Trim();
        current.TitleKeys = TitleKeys.Trim();
        current.Theme = Theme;
        return current;
    }

    private void LoadFrom(AppConfig config)
    {
        Emulators.Clear();
        foreach (var p in PlatformInfo.All)
        {
            var e = config.For(p);
            var vm = new EmulatorSettingsViewModel
            {
                Platform = p,
                Command = e.Command,
                Excluded = e.Excluded.ToList(),
                IncludeInstalled = e.IncludeInstalled,
            };
            foreach (var f in e.Folders)
                vm.Folders.Add(f);
            Emulators.Add(vm);
        }
        ProdKeys = config.ProdKeys;
        TitleKeys = config.TitleKeys;
    }
}
