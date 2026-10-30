using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Zest.Models;
using Zest.Services;

namespace Zest.ViewModels;

public partial class GameViewModel : ViewModelBase
{
    public Game Game { get; }

    public GameViewModel(Game game, GameStats? stats)
    {
        Game = game;
        Title = game.Title;
        UpdateStats(stats);
    }

    public string PlatformShort => PlatformInfo.Short(Game.Platform);
    public string SystemName => PlatformInfo.Name(Game.Platform);
    public string EmulatorName => PlatformInfo.Emulator(Game.Platform);
    public bool IsInstalled => Game.IsInstalled;
    public string Initials => string.Concat(Title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Where(w => char.IsLetterOrDigit(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Initials))]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArt))]
    public partial Bitmap? Art { get; set; }

    public bool HasArt => Art is not null;

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>This game's Pokémon save, when Forge found one in the emulator's save folders.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPokemonSave))]
    public partial Services.Forge.PokemonSave? PokemonSave { get; set; }

    public bool HasPokemonSave => PokemonSave is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HideActionText))]
    public partial bool IsHidden { get; set; }

    public string HideActionText => IsHidden ? "Show in library" : "Hide from library";

    [ObservableProperty]
    public partial bool ShowStats { get; set; } = true;

    [ObservableProperty]
    public partial string StatsText { get; set; } = "";

    [ObservableProperty]
    public partial string PlayTimeText { get; set; } = "";

    [ObservableProperty]
    public partial string LastPlayedText { get; set; } = "";

    [ObservableProperty]
    public partial string LaunchesText { get; set; } = "";

    public double PlayedSeconds { get; private set; }
    public DateTime? LastPlayed { get; private set; }

    // File facts for the details page, computed on first use (installed titles add up their whole content folder).
    private (string size, string format)? _fileFacts;

    public string SizeText => (_fileFacts ??= FileFacts()).size;
    public string FormatText => (_fileFacts ??= FileFacts()).format;
    public string Location => Game.IsInstalled ? "Installed in Azahar" : Path.GetDirectoryName(Game.Path) ?? "";
    public string FileName => Game.FileName;

    public void UpdateStats(GameStats? stats)
    {
        PlayedSeconds = stats?.Seconds ?? 0;
        LastPlayed = stats?.LastPlayed;
        var launches = stats?.Launches ?? 0;
        StatsText = launches == 0 ? "Not played yet" : $"{FormatDuration(PlayedSeconds)} · {FormatAgo(LastPlayed)}";
        PlayTimeText = launches == 0 ? "—" : FormatDuration(PlayedSeconds);
        LastPlayedText = launches == 0 ? "Never" : FormatAgo(LastPlayed);
        LaunchesText = launches.ToString();
    }

    private (string, string) FileFacts()
    {
        try
        {
            long bytes;
            if (Game.IsInstalled)
                bytes = new DirectoryInfo(Path.GetDirectoryName(Game.Path)!).EnumerateFiles().Sum(f => f.Length);
            else
                bytes = new FileInfo(Game.Path).Length;
            var format = Game.IsInstalled ? "Installed title" : Path.GetExtension(Game.Path).TrimStart('.').ToUpperInvariant();
            return (FormatSize(bytes), format);
        }
        catch (IOException)
        {
            return ("—", "—");
        }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m" : "<1m";
    }

    private static string FormatAgo(DateTime? when)
    {
        if (when is not { } w)
            return "never";
        var ago = DateTime.Now - w;
        return ago.TotalMinutes < 1 ? "just now"
            : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes}m ago"
            : ago.TotalDays < 1 ? $"{(int)ago.TotalHours}h ago"
            : ago.TotalDays < 30 ? $"{(int)ago.TotalDays}d ago"
            : w.ToString("MMM d");
    }
}
