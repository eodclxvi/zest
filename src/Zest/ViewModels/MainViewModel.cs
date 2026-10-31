using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zest.Models;
using Zest.Services;

namespace Zest.ViewModels;

public partial class PlatformChip : ObservableObject
{
    public required Platform? Platform { get; init; }
    public required string Label { get; init; }

    /// <summary>The "Hidden" chip: shows only games the user hid.</summary>
    public bool IsHiddenChip { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial int Count { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShown))]
    public partial bool IsSelected { get; set; }

    public bool IsShown => !IsHiddenChip || Count > 0 || IsSelected;
}

/// <summary>Button labels for the connected controller, shown in the hint bar.</summary>
public partial class ControllerHints : ObservableObject
{
    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Confirm { get; set; } = "A";

    [ObservableProperty]
    public partial string Back { get; set; } = "B";

    [ObservableProperty]
    public partial string West { get; set; } = "X";

    [ObservableProperty]
    public partial string North { get; set; } = "Y";
}

public partial class MainViewModel : ViewModelBase
{
    private AppConfig _config;
    private ArtService _art;
    private readonly PlayStats _stats = PlayStats.Load();
    private List<GameViewModel> _all = [];
    private int _scanGeneration;
    private int _running;

    public MainViewModel(AppConfig config)
    {
        _config = config;
        _art = new ArtService(_config);
        ShowStats = _config.ShowStats;
        Sort = _config.Sort;
        View = _config.View;

        Chips = [new PlatformChip { Platform = null, Label = "All", IsSelected = true }];
        foreach (var p in PlatformInfo.All)
            Chips.Add(new PlatformChip { Platform = p, Label = PlatformInfo.Short(p) });
        Chips.Add(new PlatformChip { Platform = null, Label = "Hidden", IsHiddenChip = true });

        _ = RefreshAsync();
    }

    public AppConfig Config => _config;
    public ObservableCollection<GameViewModel> Games { get; } = [];
    public ObservableCollection<PlatformChip> Chips { get; }
    public ControllerHints Controller { get; } = new();

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial GameViewModel? SelectedGame { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContinueGame))]
    public partial GameViewModel? ContinueGame { get; set; }

    public bool HasContinueGame => ContinueGame is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailsOpen))]
    public partial GameViewModel? DetailsGame { get; set; }

    public bool IsDetailsOpen => DetailsGame is not null;

    /// <summary>The Forge page (PKForge's save editor and Bank); created on first open.</summary>
    [ObservableProperty]
    public partial ForgeViewModel? Forge { get; set; }

    [ObservableProperty]
    public partial bool IsForgeOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsScanning { get; set; }

    public bool IsEmpty => !IsScanning && Games.Count == 0;

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowStats { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortLabel))]
    public partial SortMode Sort { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridView), nameof(IsCarouselView), nameof(ViewToggleTip))]
    public partial ViewMode View { get; set; }

    public bool IsGridView => View == ViewMode.Grid;
    public bool IsCarouselView => View == ViewMode.Carousel;
    public string ViewToggleTip => IsGridView ? "Carousel view (V)" : "Grid view (V)";

    /// <summary>True while any emulator launched from Zest is still open.</summary>
    [ObservableProperty]
    public partial bool IsGameRunning { get; set; }

    public string SortLabel => Sort switch
    {
        SortMode.Title => "Sorted A–Z",
        SortMode.RecentlyPlayed => "Sorted by recently played",
        SortMode.MostPlayed => "Sorted by most played",
        SortMode.Platform => "Sorted by system",
        _ => "",
    };

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowStatsChanged(bool value)
    {
        foreach (var g in _all)
            g.ShowStats = value;
        _config.ShowStats = value;
        _config.Save();
    }

    [RelayCommand]
    private void SelectPlatform(PlatformChip chip)
    {
        foreach (var c in Chips)
            c.IsSelected = c == chip;
        ApplyFilter();
    }

    /// <summary>Moves to the previous/next visible chip (controller shoulder buttons).</summary>
    public void StepPlatform(int delta)
    {
        var shown = Chips.Where(c => c.IsShown).ToList();
        var current = shown.FindIndex(c => c.IsSelected);
        SelectPlatform(shown[(current + delta + shown.Count) % shown.Count]);
    }

    [RelayCommand]
    private void CycleSort()
    {
        Sort = (SortMode)(((int)Sort + 1) % Enum.GetValues<SortMode>().Length);
        _config.Sort = Sort;
        _config.Save();
        ApplyFilter();
        Status = SortLabel;
    }

    [RelayCommand]
    private void ToggleStats() => ShowStats = !ShowStats;

    [RelayCommand]
    private void ToggleView()
    {
        View = IsGridView ? ViewMode.Carousel : ViewMode.Grid;
        _config.View = View;
        _config.Save();
        if (IsCarouselView)
            SelectedGame ??= Games.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenDetails(GameViewModel? game) => DetailsGame = game ?? SelectedGame;

    [RelayCommand]
    private void CloseDetails() => DetailsGame = null;

    /// <summary>Opens Forge on the game's own save (from the details page), or on the Bank.</summary>
    [RelayCommand]
    private void OpenForge(GameViewModel? game)
    {
        Forge ??= new ForgeViewModel(() => IsGameRunning);
        IsForgeOpen = true;
        _ = Forge.LoadAsync(game?.PokemonSave, game?.PokemonSave is null ? ForgeTab.Bank : ForgeTab.Save);
    }

    [RelayCommand]
    private void CloseForge() => IsForgeOpen = false;

    [RelayCommand]
    private void ToggleHidden(GameViewModel? game)
    {
        game ??= DetailsGame ?? SelectedGame;
        if (game is null)
            return;

        game.IsHidden = !game.IsHidden;
        if (game.IsHidden)
            _config.Hidden.Add(game.Game.Path);
        else
            _config.Hidden.Remove(game.Game.Path);
        _config.Save();

        Status = game.IsHidden
            ? $"Hid {game.Title}. Find it under the Hidden chip."
            : $"{game.Title} is back in the library.";
        UpdateCounts();
        UpdateContinue();
        ApplyFilter();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var generation = ++_scanGeneration;
        IsScanning = true;
        Status = "Scanning game folders…";

        var games = await Task.Run(() => LibraryScanner.Scan(_config));
        if (generation != _scanGeneration)
            return;

        _all = games.Select(g => new GameViewModel(g, _stats.Get(g.Path))
        {
            ShowStats = ShowStats,
            IsHidden = _config.Hidden.Contains(g.Path),
            Convert = !g.IsInstalled && Services.Packages.PackageConverter.CanConvert(g.Path)
                ? new ConvertViewModel(g.Path, RefreshAsync, () => DetailsGame = null)
                : null,
        }).ToList();
        foreach (var game in _all)
            game.Convert?.Refresh();
        UpdateCounts();
        UpdateContinue();
        ApplyFilter();
        IsScanning = false;
        Status = $"{_all.Count(g => !g.IsHidden)} games";

        await Task.WhenAll(LoadArtAsync(generation), FindPokemonSavesAsync(generation), FindNativePortsAsync(generation));
    }

    /// <summary>Marks the library games whose Pokémon save Forge can open (Eden NAND / Azahar SD).</summary>
    private async Task FindPokemonSavesAsync(int generation)
    {
        var games = _all.Where(g => g.Game.Platform != Platform.PS2).ToList();
        var matches = await Task.Run(() =>
        {
            try
            {
                var saves = Services.Forge.SaveLocator.Scan(Services.Forge.ForgeHost.Shared.Engine);
                return games.Select(g => (g, save: Services.Forge.SaveLocator.ForGame(g.Game, saves))).ToList();
            }
            catch (Exception)
            {
                return [];
            }
        });
        if (generation != _scanGeneration)
            return;
        foreach (var (game, save) in matches)
            game.PokemonSave = save;
    }

    /// <summary>Marks the Switch dumps that have a native port in the catalog, by the title ID and version in their own metadata.</summary>
    private async Task FindNativePortsAsync(int generation)
    {
        var config = _config;
        var games = _all.Where(g => g.Game.Platform == Platform.Switch && !g.Game.IsInstalled).ToList();
        var matches = await Task.Run(() =>
        {
            var content = Services.Native.SwitchContent.TryCreate(config);
            if (content is null)
                return [];
            var found = new List<(GameViewModel game, Services.Native.NativePort port, Services.Native.SwitchApplication app)>();
            foreach (var game in games)
            {
                try
                {
                    if (content.Identify(game.Game.Path) is { } app && Services.Native.NativePortCatalog.For(Platform.Switch, app.TitleId) is { } port)
                        found.Add((game, port, app));
                }
                catch (Exception)
                {
                    // A dump the keys can't open: it simply gets no native port.
                }
            }
            return found;
        });
        if (generation != _scanGeneration)
            return;

        // Two dumps of one game would share the port's folders; keep the first.
        foreach (var (game, port, app) in matches.DistinctBy(m => m.port.Id))
        {
            game.NativePort = new NativePortViewModel(port, game, app, () => Services.Native.SwitchContent.TryCreate(_config));
            _ = game.NativePort.CheckForUpdateAsync();
        }
    }

    [RelayCommand]
    public async Task LaunchNativeAsync(GameViewModel? game)
    {
        game ??= DetailsGame ?? SelectedGame;
        if (game?.NativePort is not { CanPlay: true } native || game.IsRunning)
            return;

        game.IsRunning = true;
        IsGameRunning = ++_running > 0;
        game.UpdateStats(_stats.RecordLaunch(game.Game.Path));
        UpdateContinue();
        Status = $"Playing {game.Title} natively with {native.Port.Name}";
        try
        {
            var played = await Services.Native.NativePorts.RunAsync(native.Port);
            _stats.AddTime(game.Game.Path, played);
            game.UpdateStats(_stats.Get(game.Game.Path));
            Status = $"Played {game.Title} for {GameViewModel.FormatDuration(played.TotalSeconds)} with {native.Port.Name}";
        }
        catch (Exception e)
        {
            Status = $"Couldn't start {native.Port.Name}: {e.Message}";
        }
        finally
        {
            game.IsRunning = false;
            IsGameRunning = --_running > 0;
            ApplyFilter();
        }
    }

    /// <summary>Called after Settings are saved: rebuild services from the new config and rescan.</summary>
    public async Task ReloadConfigAsync()
    {
        _config = AppConfig.Load();
        _art = new ArtService(_config);
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task LaunchAsync(GameViewModel? game)
    {
        game ??= SelectedGame;
        if (game is null || game.IsRunning)
            return;

        game.IsRunning = true;
        IsGameRunning = ++_running > 0;
        game.UpdateStats(_stats.RecordLaunch(game.Game.Path));
        UpdateContinue();
        Status = $"Playing {game.Title} in {game.EmulatorName}";
        try
        {
            var played = await GameLauncher.RunAsync(game.Game, _config);
            _stats.AddTime(game.Game.Path, played);
            game.UpdateStats(_stats.Get(game.Game.Path));
            Status = $"Played {game.Title} for {GameViewModel.FormatDuration(played.TotalSeconds)}";
        }
        catch (Exception e)
        {
            Status = $"Couldn't start {game.EmulatorName}: {e.Message}";
        }
        finally
        {
            game.IsRunning = false;
            IsGameRunning = --_running > 0;
            ApplyFilter();
        }
    }

    [RelayCommand]
    private async Task RefetchArtAsync(GameViewModel? game)
    {
        if (game is null)
            return;
        ArtService.Forget(game.Game);
        game.Art = null;
        await LoadArtForAsync(game, _art);
    }

    private void UpdateCounts()
    {
        foreach (var chip in Chips)
        {
            chip.Count = chip.IsHiddenChip ? _all.Count(g => g.IsHidden)
                : chip.Platform is { } p ? _all.Count(g => !g.IsHidden && g.Game.Platform == p)
                : _all.Count(g => !g.IsHidden);
        }
        // Leaving the Hidden chip selected after unhiding the last game would show an empty page.
        if (Chips.First(c => c.IsHiddenChip) is { IsSelected: true, Count: 0 })
            SelectPlatform(Chips[0]);
    }

    private void UpdateContinue() =>
        ContinueGame = _all.Where(g => !g.IsHidden && g.LastPlayed is not null).MaxBy(g => g.LastPlayed);

    private void ApplyFilter()
    {
        var chip = Chips.FirstOrDefault(c => c.IsSelected) ?? Chips[0];
        var query = SearchText.Trim();

        IEnumerable<GameViewModel> list = _all.Where(g => g.IsHidden == chip.IsHiddenChip);
        if (chip.Platform is { } p)
            list = list.Where(g => g.Game.Platform == p);
        if (query.Length > 0)
            list = list.Where(g => g.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                                   || g.PlatformShort.Equals(query, StringComparison.OrdinalIgnoreCase));

        list = Sort switch
        {
            SortMode.RecentlyPlayed => list.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Title),
            SortMode.MostPlayed => list.OrderByDescending(g => g.PlayedSeconds).ThenBy(g => g.Title),
            SortMode.Platform => list.OrderBy(g => g.Game.Platform).ThenBy(g => g.Title),
            _ => list.OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase),
        };

        var selected = SelectedGame;
        Games.Clear();
        foreach (var g in list)
            Games.Add(g);
        if (selected is not null && Games.Contains(selected))
            SelectedGame = selected;
        else if (IsCarouselView)
            SelectedGame = Games.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task LoadArtAsync(int generation)
    {
        var art = _art;
        // Visible order first so the top of the grid fills in before the rest.
        var pending = Games.Concat(_all.Except(Games)).ToList();
        var titlesChanged = 0;
        await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (game, _) =>
        {
            if (generation != _scanGeneration)
                return;
            if (await LoadArtForAsync(game, art))
                Interlocked.Increment(ref titlesChanged);
        });

        if (generation == _scanGeneration && titlesChanged > 0 && Sort == SortMode.Title)
            ApplyFilter(); // titles read from the game files can change the A–Z order.
    }

    /// <returns>True when the game's title was replaced by one read from its metadata.</returns>
    private static async Task<bool> LoadArtForAsync(GameViewModel game, ArtService art)
    {
        var result = await art.GetAsync(game.Game);
        Bitmap? bitmap = null;
        if (result.ArtPath is not null)
        {
            try
            {
                await using var stream = File.OpenRead(result.ArtPath);
                bitmap = Bitmap.DecodeToWidth(stream, 360, BitmapInterpolationMode.HighQuality);
            }
            catch (Exception)
            {
                // Corrupt download: leave the placeholder; "Fetch art again" clears it.
            }
        }

        var titleChanged = result.Title is not null && result.Title != game.Title;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (bitmap is not null)
                game.Art = bitmap;
            if (titleChanged)
            {
                game.Title = result.Title!;
                game.Game.Title = result.Title!;
            }
        });
        return titleChanged;
    }
}
