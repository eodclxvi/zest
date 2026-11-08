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

    // ---- "Find artwork" on the details page

    public ObservableCollection<ArtCandidateViewModel> ArtCandidates { get; } = [];

    [ObservableProperty]
    public partial bool IsArtPickerOpen { get; set; }

    [ObservableProperty]
    public partial string ArtSearchMessage { get; set; } = "";

    private CancellationTokenSource? _artSearch;

    partial void OnDetailsGameChanged(GameViewModel? value) => CloseArtPicker();

    [RelayCommand]
    private void CloseArtPicker()
    {
        _artSearch?.Cancel();
        IsArtPickerOpen = false;
        ArtCandidates.Clear();
        ArtSearchMessage = "";
    }

    /// <summary>Lists covers for the game from SteamGridDB (with a key), Wikipedia and libretro, thumbnails loading as they come.</summary>
    [RelayCommand]
    private async Task FindArtworkAsync(GameViewModel? game)
    {
        if (game is null)
            return;
        CloseArtPicker();
        IsArtPickerOpen = true;
        ArtSearchMessage = "Searching…";
        var cancel = (_artSearch = new CancellationTokenSource()).Token;
        var art = _art;
        var title = game.Title;
        var found = await art.SearchAsync(game.Game, title, cancel);
        if (cancel.IsCancellationRequested)
            return;
        foreach (var c in found)
            ArtCandidates.Add(new ArtCandidateViewModel(c));
        ArtSearchMessage = found.Count == 0 ? $"No covers found for \"{title}\"."
            : $"{found.Count} covers found. Click one to use it." + (art.HasSteamGridDbKey ? "" : " Add a SteamGridDB key in Settings for more.");
        await Parallel.ForEachAsync(ArtCandidates.ToList(), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel }, async (vm, token) =>
        {
            try
            {
                await using var stream = new MemoryStream(await art.ThumbnailAsync(vm.Candidate, token));
                var bitmap = Bitmap.DecodeToWidth(stream, 240, BitmapInterpolationMode.HighQuality);
                await Dispatcher.UIThread.InvokeAsync(() => vm.Thumb = bitmap);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ArgumentException or InvalidOperationException) { }
        }).ContinueWith(_ => { });
    }

    [RelayCommand]
    private async Task UseArtworkAsync(ArtCandidateViewModel? candidate)
    {
        if (candidate is null || DetailsGame is not { } game)
            return;
        ArtSearchMessage = "Saving the cover…";
        try
        {
            await _art.UseAsync(game.Game, candidate.Candidate);
            await LoadArtForAsync(game, _art);
            CloseArtPicker();
            Status = $"New cover for {game.Title} from {candidate.Candidate.Source}. \"Fetch art again\" goes back to the default.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            ArtSearchMessage = $"Couldn't download that cover: {e.Message}";
        }
    }

    /// <summary>The Forge page (PKForge's save editor and Bank); created on first open.</summary>
    [ObservableProperty]
    public partial ForgeViewModel? Forge { get; set; }

    [ObservableProperty]
    public partial bool IsForgeOpen { get; set; }

    /// <summary>The window's controller (set by MainWindow): the Controls page captures presses through it.</summary>
    public GamepadService? Gamepad { get; set; }

    /// <summary>The Controls page of a native port (its larecomp.ini bindings); null when closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsControlsOpen))]
    public partial ControlsViewModel? Controls { get; set; }

    public bool IsControlsOpen => Controls is not null;

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

    // Carousel cover size: 300 x 380 on a 720-pixel-tall monitor, larger on taller ones (the window sets it from
    // the monitor it is on: MainWindow.UpdateCarouselSize).
    [ObservableProperty]
    public partial double CarouselCardWidth { get; set; } = 300;

    [ObservableProperty]
    public partial double CarouselCardHeight { get; set; } = 380;

    [ObservableProperty]
    public partial double CarouselInitialsSize { get; set; } = 72;
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
    private void OpenControls(GameViewModel? game)
    {
        if (game?.Game.Local is not { } port || Gamepad is null)
            return;
        var dir = AppPaths.ExpandHome(port.WorkingDirectory);
        if (dir.Length == 0)
            dir = Path.GetDirectoryName(AppPaths.ExpandHome(port.Executable)) ?? "";
        Controls = new ControlsViewModel(game.Title, dir, Gamepad, () => IsGameRunning);
    }

    [RelayCommand]
    private void CloseControls()
    {
        Controls?.CancelCapture();
        Controls = null;
    }

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
        NestEmulatorCopies();
        UpdateCounts();
        UpdateContinue();
        ApplyFilter();
        IsScanning = false;
        Status = $"{_all.Count(g => !g.IsHidden && g.NestedUnder is null)} games";

        await Task.WhenAll(LoadArtAsync(generation), FindPokemonSavesAsync(generation), FindNativePortsAsync(generation));
    }

    /// <summary>Marks the library games whose Pokémon save Forge can open (Eden NAND / Azahar SD).</summary>
    private async Task FindPokemonSavesAsync(int generation)
    {
        // (a native build keeps its own saves, not the emulator's)
        var games = _all.Where(g => g.Game.Platform != Platform.PS2 && g.Game.Local is null).ToList();
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
        var games = _all.Where(g => g.Game.Platform == Platform.Switch && !g.Game.IsInstalled && g.Game.Local is null).ToList();
        var matches = await Task.Run(() =>
        {
            var content = Services.Native.SwitchContent.TryCreate(config);
            if (content is null)
                return [];
            var found = new List<(GameViewModel game, Services.Native.NativePort? port, Services.Native.SwitchApplication app)>();
            foreach (var game in games)
            {
                try
                {
                    if (content.Identify(game.Game.Path) is { } app)
                        found.Add((game, Services.Native.NativePortCatalog.For(Platform.Switch, app.TitleId), app));
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

        // Switch 2 files (.nup) aren't read by Zest: the title ID and version come from the file name's
        // [0400486024DEC000][v131072]; the port's own builder checks the file before building
        foreach (var game in _all.Where(g => g.Game.Platform == Platform.Switch2 && g.Game.Local is null))
        {
            if (ShaderCaches.TitleId(game.Game) is not { } id
                || Services.Native.NativePortCatalog.For(Platform.Switch2, Convert.ToUInt64(id, 16)) is not { } port
                || _all.Any(g => g.NativePort?.Port.Id == port.Id))
                continue;
            var version = NupVersion().Match(game.Game.FileName) is { Success: true } v ? v.Groups[1].Value : "?";
            game.NativePort = new NativePortViewModel(port, game, new Services.Native.SwitchApplication(port.TitleId, version, "", 0), () => null);
            _ = game.NativePort.CheckForUpdateAsync();
        }

        // Two dumps of one game would share the port's folders; keep the first.
        foreach (var (game, port, app) in matches.Where(m => m.port is not null).DistinctBy(m => m.port!.Id))
        {
            game.NativePort = new NativePortViewModel(port!, game, app, () => Services.Native.SwitchContent.TryCreate(_config),
                () => string.IsNullOrEmpty(_config.ProdKeys) ? null : AppPaths.ExpandHome(_config.ProdKeys),
                () => string.IsNullOrEmpty(_config.TitleKeys) ? null : AppPaths.ExpandHome(_config.TitleKeys));
            _ = game.NativePort.CheckForUpdateAsync();
        }
        // Ports built on this PC from source ("Install native")
        foreach (var (game, _, app) in matches)
        {
            if (Services.Native.SourcePortCatalog.For(Platform.Switch, app.TitleId) is { } source &&
                !_all.Any(g => g.SourcePort?.Port.Id == source.Id))
                game.SourcePort = new SourcePortViewModel(source, game, _config, RegisterNativeBuildAsync);
        }
    }

    /// <summary>After "Install native": the build becomes its own game in the library and the emulator copy is hidden.</summary>
    /// <summary>
    /// A native build lists the emulator dumps of its game on its details page (Play there runs them in the
    /// emulator) instead of beside it in the library: the dump it takes its art from, and dumps of the same title
    /// (a redump). Those dumps are taken off the Hidden list, where builds before this one put them.
    /// </summary>
    private void NestEmulatorCopies()
    {
        var hiddenChanged = false;
        foreach (var native in _all.Where(g => g.Game.Local is not null))
        {
            var port = native.Game.Local!;
            var key = LibraryScanner.TitleKey(port.Title);
            var artFrom = AppPaths.ExpandHome(port.ArtFrom);
            foreach (var copy in _all.Where(g => g.Game.Local is null && g.NestedUnder is null && g.Game.Platform == port.Platform
                         && g.Game.Kind == ContentKind.Game
                         && (g.Game.Path == artFrom || LibraryScanner.TitleKey(Game.TitleFromFileName(g.Game.Path)) == key)))
            {
                copy.NestedUnder = native;
                native.EmulatorCopies.Add(copy);
                if (_config.Hidden.Remove(copy.Game.Path))
                    hiddenChanged = true;
                copy.IsHidden = false;
            }
        }
        if (hiddenChanged)
            _config.Save();
    }

    private async Task RegisterNativeBuildAsync(Services.Native.SourcePort port, GameViewModel game)
    {
        var exe = Services.Native.NativeBuilder.Executable(port);
        if (!_config.LocalPorts.Any(p => AppPaths.ExpandHome(p.Executable) == exe))
            _config.LocalPorts.Add(new LocalPort
            {
                Title = game.Title,
                Platform = Platform.Switch,
                Executable = exe,
                Arguments = ["--play"],
                WorkingDirectory = Services.Native.NativeBuilder.Project(port),
                ArtFrom = game.Game.Path,
                Log = Path.Combine(Services.Native.NativeBuilder.Root(port), "play.log"),
            });
        _config.Save();
        await RefreshAsync();
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
        if (native.Port.Builder is { PreparedMarker: { } marker, FirstStartNote: { } note }
            && !File.Exists(Path.Combine(Services.Native.NativePorts.GameFolder(native.Port), marker)))
            Status += $" · {note}";
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
        Status = $"Playing {game.Title} {game.PlayedWith}";
        try
        {
            if (_config.UpdateShaderCaches
                && await ShaderCaches.BeforeLaunchAsync(game.Game, new Progress<string>(m => Status = $"{game.Title}: {m}")) is { } shaders)
                Status = $"Playing {game.Title} {game.PlayedWith} · {shaders}";
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
            var shown = _all.Where(g => g.NestedUnder is null);
            chip.Count = chip.IsHiddenChip ? shown.Count(g => g.IsHidden)
                : chip.Platform is { } p ? shown.Count(g => !g.IsHidden && g.Game.Platform == p)
                : shown.Count(g => !g.IsHidden);
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

        IEnumerable<GameViewModel> list = _all.Where(g => g.NestedUnder is null && g.IsHidden == chip.IsHiddenChip);
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
                bitmap = Bitmap.DecodeToWidth(stream, 720, BitmapInterpolationMode.HighQuality);
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

    [System.Text.RegularExpressions.GeneratedRegex(@"\[v(\d+)\]")]
    private static partial System.Text.RegularExpressions.Regex NupVersion();
}
