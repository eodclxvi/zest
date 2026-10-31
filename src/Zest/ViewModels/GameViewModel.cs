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
        AddOns = game.AddOns.Select(a => new AddOnViewModel(a)).ToList();
        UpdateStats(stats);
    }

    /// <summary>Updates and DLC nested under this game.</summary>
    public IReadOnlyList<AddOnViewModel> AddOns { get; }

    public bool HasAddOns => AddOns.Count > 0;

    /// <summary>Short card badge: "UPDATE 2.0.2 · 2 DLC".</summary>
    public string AddOnBadge
    {
        get
        {
            var update = Game.AddOns.FirstOrDefault(a => a.Kind == ContentKind.Update);
            var dlc = Game.AddOns.Count(a => a.Kind == ContentKind.Dlc);
            var parts = new List<string>();
            if (update is not null)
                parts.Add($"UPDATE {update.Version}");
            if (dlc > 0)
                parts.Add($"{dlc} DLC");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>An update or DLC whose game isn't in the library is listed on its own, marked as such.</summary>
    public string? KindBadge => Game.Local is not null ? "NATIVE" : Game.Kind switch
    {
        ContentKind.Update => "UPDATE",
        ContentKind.Dlc => "DLC",
        _ => null,
    };

    public bool IsAddOn => Game.Kind != ContentKind.Game;

    public bool IsLocal => Game.Local is not null;
    public bool HasBadge => IsAddOn || IsLocal;

    public string PlatformShort => PlatformInfo.Short(Game.Platform);
    public string SystemName => PlatformInfo.Name(Game.Platform);
    public string EmulatorName => Game.Local is not null ? "Native" : PlatformInfo.Emulator(Game.Platform);
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

    /// <summary>A native PC port of this game (a static recompilation) that Zest can set up from this dump.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNativePort))]
    public partial NativePortViewModel? NativePort { get; set; }

    public bool HasNativePort => NativePort is not null;

    /// <summary>An nsz-compressed dump (.nsz / .xcz) that can be converted back to a standard NSP / XCI.</summary>
    public ConvertViewModel? Convert { get; set; }

    public bool CanConvert => Convert is not null;

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

    /// <summary>The verb for the status bar: "in Eden", or "natively" for a native build.</summary>
    public string PlayedWith => IsLocal ? "natively" : $"in {EmulatorName}";
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
            var format = Game.IsInstalled ? "Installed title" : IsLocal ? "Native build" : Path.GetExtension(Game.Path).TrimStart('.').ToUpperInvariant();
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

/// <summary>One update or DLC file in a game's "Updates & DLC" list.</summary>
public sealed class AddOnViewModel(Game addOn)
{
    public Game Game { get; } = addOn;
    public string KindLabel => Game.Kind == ContentKind.Update ? "UPDATE" : "DLC";
    public string Name => Game.Kind == ContentKind.Update ? $"Version {Game.Version}" : Game.Title;
    public string FileName => Game.FileName;
    public string TitleIdText => Game.TitleId is { } id ? id.ToString("X16") : "";

    public string SizeText
    {
        get
        {
            try
            {
                var bytes = new FileInfo(Game.Path).Length;
                // DLC often ships as a tiny stub whose data lives in the update.
                return bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
                    : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB"
                    : $"{Math.Max(1, bytes / 1024)} KB";
            }
            catch (IOException)
            {
                return "—";
            }
        }
    }
}

