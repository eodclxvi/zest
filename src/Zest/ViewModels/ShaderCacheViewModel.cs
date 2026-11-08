using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zest.Models;
using Zest.Services;

namespace Zest.ViewModels;

/// <summary>The details page card that searches for shared shader caches and installs the ones Zest can.</summary>
public partial class ShaderCacheViewModel(GameViewModel game) : ViewModelBase
{
    private CancellationTokenSource? _work;

    public ObservableCollection<ShaderCacheHitViewModel> Results { get; } = [];

    public string Explanation => game.IsLocal
        ? "Native builds compile their own shaders on this PC and keep them between runs; shared caches are made for emulators."
        : ShaderCaches.SupportsPacks(game.Game)
            ? "Shared Vulkan pipeline caches cut the stutter of the first play. Packs from Lichii Shader Sync are merged into Lichii's cache, and kept up to date before each launch."
            : $"Searches GitHub for shader caches others shared for {game.Title}. Those are other people's files, so they open in your browser for you to check; Zest installs only packs for Lichii.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial bool HasSearched { get; set; }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (IsBusy)
            return;
        IsBusy = true;
        Message = "Searching…";
        Results.Clear();
        _work = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var hits = await ShaderCaches.SearchAsync(game.Game, game.Title, _work.Token);
            var id = ShaderCaches.TitleId(game.Game);
            foreach (var h in hits)
                Results.Add(new ShaderCacheHitViewModel(h, this, h.Installable && id is not null && await IsPackCurrentAsync(id)));
            Message = hits.Count == 0 ? "No shared shader caches found." : hits.Count == 1 ? "Found 1 result." : $"Found {hits.Count} results.";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Message = $"Search failed: {e.Message}";
        }
        finally
        {
            HasSearched = true;
            IsBusy = false;
        }
    }

    private static async Task<bool> IsPackCurrentAsync(string id) =>
        await ShaderCaches.FindPackAsync(id) is { } pack && ShaderCaches.IsUpToDate(id, pack);

    public async Task InstallAsync(ShaderCacheHitViewModel hit)
    {
        if (IsBusy || ShaderCaches.TitleId(game.Game) is not { } id)
            return;
        if (game.IsRunning)
        {
            Message = "Close the game first.";
            return;
        }
        IsBusy = true;
        _work = new CancellationTokenSource();
        try
        {
            var pack = await ShaderCaches.FindPackAsync(id, _work.Token);
            if (pack is null)
            {
                Message = "The pack is no longer online.";
                return;
            }
            var progress = new Progress<string>(m => Message = m);
            Message = await ShaderCaches.SyncAsync(id, pack, progress, _work.Token);
            hit.IsInstalled = ShaderCaches.IsUpToDate(id, pack);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Message = $"Couldn't install the shader cache: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _work?.Cancel();

    [RelayCommand]
    private void SearchWeb() => Open(ShaderCaches.WebSearchUrl(game.Game, game.Title));

    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", [url]) { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

public partial class ShaderCacheHitViewModel(ShaderCacheHit hit, ShaderCacheViewModel owner, bool installed) : ViewModelBase
{
    public ShaderCacheHit Hit { get; } = hit;
    public string Source => Hit.Source.ToUpperInvariant();
    public string Title => Hit.Title;
    public string Detail => Hit.Detail;
    public bool Installable => Hit.Installable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallText))]
    public partial bool IsInstalled { get; set; } = installed;

    public string InstallText => IsInstalled ? "Check for more" : "Install";

    [RelayCommand]
    private Task InstallAsync() => owner.InstallAsync(this);

    [RelayCommand]
    private void Open() => ShaderCacheViewModel.Open(Hit.Url);
}
