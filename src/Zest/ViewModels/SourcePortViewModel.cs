using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zest.Services;
using Zest.Services.Native;

namespace Zest.ViewModels;

/// <summary>The details page card for a game with a source port: "Install native" builds it on this PC.</summary>
public partial class SourcePortViewModel : ViewModelBase
{
    private readonly GameViewModel _game;
    private readonly AppConfig _config;
    private readonly Func<SourcePort, GameViewModel, Task> _onBuilt;
    private CancellationTokenSource? _work;

    public SourcePortViewModel(SourcePort port, GameViewModel game, AppConfig config, Func<SourcePort, GameViewModel, Task> onBuilt)
    {
        Port = port;
        _game = game;
        _config = config;
        _onBuilt = onBuilt;
        IsBuilt = NativeBuilder.IsBuilt(port);
    }

    public SourcePort Port { get; }

    public string Heading => $"{Port.Name} · {Port.Status}";
    public string Facts => Port.NeedsUpdate
        ? $"Built on this PC from your dump and its update ({UpdateText}). Project: {Port.ProjectDir}"
        : $"Built on this PC from your dump. Project: {Port.ProjectDir}";

    private string? UpdatePath => _game.Game.AddOns
        .Where(a => a.Kind == Models.ContentKind.Update)
        .OrderByDescending(a => a.Version, StringComparer.Ordinal)
        .FirstOrDefault()?.Path;

    private string UpdateText => UpdatePath is { } u ? Path.GetFileName(u) : "no update found in your library";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(InstallText))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallText))]
    public partial bool IsBuilt { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    public bool IsIdle => !IsBusy;
    public string InstallText => IsBuilt ? "Rebuild native" : "Install native";
    public string LogPath => NativeBuilder.LogPath(Port);

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsBusy)
            return;
        if (Port.NeedsUpdate && UpdatePath is null)
        {
            Message = "This port needs the game's update: add the update file to your Switch games folder first.";
            return;
        }
        if (string.IsNullOrEmpty(_config.ProdKeys))
        {
            Message = "Zest needs your prod.keys (Settings) to read Switch games.";
            return;
        }

        IsBusy = true;
        _work = new CancellationTokenSource();
        var token = _work.Token;
        var progress = new Progress<BuildProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress = p.Percent;
            Message = p.Step;
        }));
        try
        {
            var dlcs = _game.Game.AddOns.Where(a => a.Kind == Models.ContentKind.Dlc).Select(a => a.Path).ToList();
            await Task.Run(() => NativeBuilder.BuildAsync(Port, _config, _game.Game.Path, Port.NeedsUpdate ? UpdatePath : null, progress, token, dlcs), token);
            IsBuilt = true;
            await _onBuilt(Port, _game);
            Message = $"Installed. {Port.Name} is in your library as its own game (the emulator copy is hidden).";
        }
        catch (OperationCanceledException)
        {
            Message = "Cancelled. Finished steps are kept, so the next Install continues from there.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception or LibHac.Common.HorizonResultException
                                      or LibHac.Common.Keys.MissingKeyException)
        {
            Message = $"The build stopped: {e.Message}\nFull log: {LogPath}";
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
            _work.Dispose();
            _work = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _work?.Cancel();
}
