using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zest.Services;
using Zest.Services.Native;

namespace Zest.ViewModels;

/// <summary>The details page card for a game that has a native port: install, prepare files, play.</summary>
public partial class NativePortViewModel : ViewModelBase
{
    private readonly GameViewModel _game;
    private readonly Func<SwitchContent?> _content;
    private CancellationTokenSource? _work;

    private readonly Func<string?> _keys;

    public NativePortViewModel(NativePort port, GameViewModel game, SwitchApplication app, Func<SwitchContent?> content, Func<string?>? keys = null)
    {
        Port = port;
        _game = game;
        _content = content;
        _keys = keys ?? (() => null);
        DumpVersion = app.DisplayVersion;
        Refresh();
    }

    public NativePort Port { get; }
    public string DumpVersion { get; }

    public string Heading => $"{Port.Name} · {Port.Status}";
    public string Facts => $"by {Port.Author} · licence: {Port.Licence} · your dump: base game v{DumpVersion} (accepted: {string.Join(", ", Port.Versions)})";
    public string GameFilesPath => NativePorts.GameFilesFolder(Port);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(SetupText), nameof(ReleaseText))]
    public partial InstalledRelease? Release { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(SetupText), nameof(HasGameFiles), nameof(GameFilesText))]
    public partial PreparedFiles? Files { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetupText))]
    public partial string? UpdateTag { get; set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay), nameof(SetupText))]
    public partial bool GameBuilt { get; set; }

    public bool CanPlay => Release is not null && (Port.Setup is null || GameBuilt);
    public bool HasGameFiles => Files is not null;
    public string SetupText => Port.Setup is not null
        ? Release is null || !GameBuilt ? "Install native" : UpdateTag is not null ? $"Update to {UpdateTag}" : "Check for update"
        : Release is null ? "Set up native port"
        : Files is null ? "Prepare game files"
        : UpdateTag is not null ? $"Update to {UpdateTag}"
        : "Check for update";

    /// <summary>The newest update for this game in the library (ports with a command-line setup need one).</summary>
    private Zest.Models.Game? Update => _game.Game.AddOns
        .Where(a => a.Kind == Zest.Models.ContentKind.Update)
        .OrderByDescending(a => a.Version, StringComparer.Ordinal)
        .FirstOrDefault();
    public string ReleaseText => Release is null ? "Not installed" : $"Installed {Release.Tag}";

    public string GameFilesText => Files is null
        ? "Game files: not prepared"
        : $"Game files ready: v{Files.Version}, {Files.Bytes / (double)(1L << 30):0.0} GB at {GameFilesPath}";

    public void Refresh()
    {
        Release = NativePorts.Installed(Port);
        Files = NativePorts.Prepared(Port);
        GameBuilt = NativePorts.IsGameBuilt(Port);
    }

    /// <summary>Installs (or updates) the official release, then extracts the base game if that isn't done yet.</summary>
    [RelayCommand]
    private async Task SetUpAsync()
    {
        if (IsBusy)
            return;
        var content = _content();
        if (content is null)
        {
            Message = "Zest needs your prod.keys (Settings) to read Switch games.";
            return;
        }

        IsBusy = true;
        _work = new CancellationTokenSource();
        try
        {
            var changed = false;
            var latest = await NativePorts.LatestTagAsync(Port, _work.Token);
            if (Release is null || (latest is not null && latest != Release.Tag))
            {
                changed = true;
                Message = $"Downloading {Port.Name} {latest} from GitHub…";
                var download = new Progress<(long done, long total)>(p => Progress = p.total > 0 ? 100.0 * p.done / p.total : 0);
                Release = await NativePorts.InstallAsync(Port, download, _work.Token);
            }

            if (Port.Setup is { } cli)
            {
                // The release builds the game itself from the dump and its update: Zest runs that setup (it skips what
                // it finished before, so this also resumes and updates)
                if (Update is not { } update)
                    throw new InvalidDataException($"{Port.Name} needs the game's update ({string.Join(", ", cli.UpdateVersions)}): add it to your Switch games folder.");
                if (update.Version is { } v && !cli.UpdateVersions.Contains(v))
                    throw new InvalidDataException($"{Port.Name} takes update {string.Join(", ", cli.UpdateVersions)}; yours is {v}.");
                var keys = _keys() ?? throw new InvalidDataException("Zest needs your prod.keys (Settings).");
                changed = true;
                var steps = new Progress<(double percent, string text)>(p => { Progress = p.percent; Message = p.text; });
                await NativePorts.RunSetupAsync(Port, keys, _game.Game.Path, update.Path, steps, _work.Token);
            }
            else if (Files is null)
            {
                changed = true;
                Message = "Extracting the base game's exefs and romfs (several GB)…";
                var extract = new Progress<(long done, long total)>(p => Progress = p.total > 0 ? 100.0 * p.done / p.total : 0);
                var token = _work.Token;
                Files = await Task.Run(() => NativePorts.Prepare(Port, content, _game.Game.Path, extract, token), token);
            }

            UpdateTag = null;
            Message = changed && Port.Setup is not null
                ? $"Installed. Press Play native to start {Port.Name}; its settings are in its in-game menu."
                : changed
                ? $"Ready. On the first Play, choose the game files folder in {Port.Name}'s setup screen (Play native copies its path for you)."
                : latest is null ? "Couldn't reach GitHub to check for an update." : $"{Release!.Tag} is the latest release.";
        }
        catch (OperationCanceledException)
        {
            Message = "Cancelled. Nothing half-finished was kept.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or LibHac.Common.HorizonResultException or LibHac.Common.Keys.MissingKeyException)
        {
            Message = $"Setup failed: {e.Message}";
        }
        finally
        {
            Progress = 0;
            IsBusy = false;
            _work.Dispose();
            _work = null;
            Refresh();
        }
    }

    [RelayCommand]
    private void Cancel() => _work?.Cancel();

    [RelayCommand]
    private void RemoveGameFiles()
    {
        if (IsBusy)
            return;
        NativePorts.RemoveGameFiles(Port);
        Refresh();
        Message = $"Removed Zest's extracted copy. {Port.Name} keeps the files its setup screen imported.";
    }

    /// <summary>Checks GitHub for a newer release, quietly; shown as "Update to …" on the card.</summary>
    public async Task CheckForUpdateAsync()
    {
        if (Release is null)
            return;
        var latest = await NativePorts.LatestTagAsync(Port, CancellationToken.None);
        if (latest is not null && latest != Release.Tag)
            UpdateTag = latest;
    }
}
