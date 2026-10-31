using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zest.Services.Packages;

namespace Zest.ViewModels;

/// <summary>The details page card for an nsz-compressed dump: convert it back to a standard NSP or XCI.</summary>
public partial class ConvertViewModel : ViewModelBase
{
    private readonly Func<Task> _rescan;
    private readonly Action _closeDetails;
    private CancellationTokenSource? _work;

    public ConvertViewModel(string source, Func<Task> rescan, Action closeDetails)
    {
        Source = source;
        Output = PackageConverter.DefaultOutput(source);
        _rescan = rescan;
        _closeDetails = closeDetails;
    }

    public string Source { get; }
    public string Output { get; }

    private string InExt => Path.GetExtension(Source).TrimStart('.').ToUpperInvariant();
    private string OutExt => Path.GetExtension(Output).TrimStart('.').ToUpperInvariant();

    public string Heading => $"Compressed {InExt} dump";
    public string Explanation =>
        $"{InExt} files are compressed with nsz. Converting rebuilds the standard {OutExt}: every game file is checked " +
        $"against its own SHA-256 name, so the result is either exact or not kept. The {OutExt} is written next to this file.";
    public string ConvertText => $"Convert to {OutExt}";
    public string DeleteText => $"Delete the original {InExt}";
    public string OutputName => Path.GetFileName(Output);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanConvert), nameof(CanDeleteOriginal))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConvert), nameof(CanDeleteOriginal))]
    public partial bool OutputExists { get; set; }

    public bool IsIdle => !IsBusy;
    public bool CanConvert => !IsBusy && !OutputExists;
    public bool CanDeleteOriginal => !IsBusy && OutputExists && File.Exists(Source);

    public void Refresh()
    {
        OutputExists = File.Exists(Output);
        if (OutputExists && string.IsNullOrEmpty(Message))
            Message = $"{OutputName} already exists next to this file.";
    }

    [RelayCommand]
    private async Task ConvertAsync()
    {
        if (!CanConvert)
            return;
        IsBusy = true;
        _work = new CancellationTokenSource();
        Message = $"Converting to {OutExt}…";
        try
        {
            var progress = new Progress<(long done, long total)>(p => Progress = p.total > 0 ? 100.0 * p.done / p.total : 0);
            var token = _work.Token;
            await Task.Run(() => PackageConverter.Convert(Source, Output, progress, token), token);
            Message = $"Converted and verified: {OutputName}. The original is kept; delete it below once you're happy.";
            OutputExists = true;
            await _rescan();
        }
        catch (OperationCanceledException)
        {
            Message = "Cancelled. Nothing half-written was kept.";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or EndOfStreamException or ZstdSharp.ZstdException)
        {
            Message = $"Conversion failed: {e.Message}";
        }
        finally
        {
            Progress = 0;
            IsBusy = false;
            _work.Dispose();
            _work = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _work?.Cancel();

    /// <summary>Deletes the compressed original, only once the verified conversion exists next to it.</summary>
    [RelayCommand]
    private async Task DeleteOriginalAsync()
    {
        if (!CanDeleteOriginal)
            return;
        try
        {
            File.Delete(Source);
            _closeDetails();
            await _rescan();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Couldn't delete it: {e.Message}";
        }
    }
}
