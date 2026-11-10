using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zest.Services;
using Zest.Services.Native;

namespace Zest.ViewModels;

public partial class HookshotSettingRow : ObservableObject
{
    private readonly HookshotSettingsViewModel _owner;
    private bool _loading;

    public HookshotSettingRow(HookshotSettingsViewModel owner, HookshotRow row)
    {
        _owner = owner;
        Row = row;
    }

    public HookshotRow Row { get; }
    public string Label => Row.Label;
    public string Help => Row.Help + (Row.Restart ? " Applies the next time the game starts." : "");
    public IReadOnlyList<HookshotChoice> Choices => Row.Choices;

    [ObservableProperty]
    public partial HookshotChoice? Selected { get; set; }

    public void Show(int value)
    {
        _loading = true;
        Selected = Choices.FirstOrDefault(c => c.Value == value) ?? Choices[0];
        _loading = false;
    }

    partial void OnSelectedChanged(HookshotChoice? value)
    {
        if (!_loading && value is not null)
            _owner.Change(this, value.Value);
    }
}

/// <summary>
/// Hookshot's in-game settings, in Zest (the rows the installed build has; button bindings stay in the game's own
/// Controls page), and the opt-in for building it with NVIDIA DLSS.
/// </summary>
public partial class HookshotSettingsViewModel : ViewModelBase
{
    private readonly NativePort _port;
    private readonly AppConfig _config;
    private HookshotOptions? _options;

    public HookshotSettingsViewModel(NativePort port, AppConfig config)
    {
        _port = port;
        _config = config;
        DlssWanted = config.HookshotDlss && config.NvidiaDlssLicenceAccepted is not null;
    }

    public ObservableCollection<HookshotSettingRow> Graphics { get; } = [];
    public ObservableCollection<HookshotSettingRow> Gameplay { get; } = [];
    public ObservableCollection<HookshotSettingRow> Controls { get; } = [];

    [ObservableProperty]
    public partial string Message { get; set; } = "Reading Hookshot's settings…";

    [ObservableProperty]
    public partial string DlssStatus { get; set; } = "";

    [ObservableProperty]
    public partial bool DlssWanted { get; set; }

    /// <summary>The licence step is showing: the user ticked DLSS and hasn't accepted NVIDIA's licence yet.</summary>
    [ObservableProperty]
    public partial bool ShowLicence { get; set; }

    public string LicenceUrl => HookshotDlss.LicenceUrl;

    public string GameFolder => NativePorts.GameFolder(_port);

    public async Task LoadAsync()
    {
        try
        {
            _options = await Task.Run(() => HookshotOptions.Load(GameFolder));
            foreach (var row in _options.Rows)
            {
                var vm = new HookshotSettingRow(this, row);
                vm.Show(_options.Get(row));
                (row.Tab switch { HookshotTab.Graphics => Graphics, HookshotTab.Gameplay => Gameplay, _ => Controls }).Add(vm);
            }
            Message = HookshotOptions.GameRunning()
                ? "The game is running: close it to change these (it reads them when it starts)."
                : $"Saved to {Path.Combine(GameFolder, "hookshot_options.ini")}. Button bindings are in the game's own menu (Insert → Controls).";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Message = $"Couldn't read Hookshot's settings: {e.Message}";
        }
        await UpdateDlssStatusAsync();
    }

    public void Change(HookshotSettingRow row, int value)
    {
        if (_options is null)
            return;
        if (HookshotOptions.GameRunning())
        {
            Message = "The game is running: close it first. It reads these when it starts, and its own menu would write over Zest's change.";
            row.Show(_options.Get(row.Row));
            return;
        }
        _options.Set(row.Row, value);
        try
        {
            _options.Save();
            Message = row.Row.Restart ? $"{row.Label}: saved; applies the next time the game starts." : $"{row.Label}: saved.";
        }
        catch (IOException e)
        {
            Message = $"Couldn't save: {e.Message}";
        }
        // A preset fills in other rows, and changing one of those makes the preset Custom
        foreach (var r in Graphics.Concat(Gameplay).Concat(Controls))
            if (r != row)
                r.Show(_options.Get(r.Row));
    }

    // ---- NVIDIA DLSS

    partial void OnDlssWantedChanged(bool value)
    {
        if (value && _config.NvidiaDlssLicenceAccepted is null)
        {
            ShowLicence = true;   // ticked: the licence first
            return;
        }
        if (_config.HookshotDlss != value)
        {
            _config.HookshotDlss = value;
            _config.Save();
        }
        _ = UpdateDlssStatusAsync();
    }

    [RelayCommand]
    private void AcceptLicence()
    {
        _config.NvidiaDlssLicenceAccepted = DateTime.Now;
        _config.HookshotDlss = true;
        _config.Save();
        ShowLicence = false;
        _ = UpdateDlssStatusAsync();
    }

    [RelayCommand]
    private void DeclineLicence()
    {
        ShowLicence = false;
        DlssWanted = false;
    }

    [RelayCommand]
    private void OpenLicence() =>
        Process.Start(new ProcessStartInfo("xdg-open", [HookshotDlss.LicenceUrl]) { UseShellExecute = false });

    private async Task UpdateDlssStatusAsync()
    {
        var release = NativePorts.Installed(_port);
        var has = HookshotDlss.GameHasDlss(GameFolder);
        var supported = release is not null && await HookshotDlss.ReleaseSupportsAsync(release.Tag, CancellationToken.None);
        DlssStatus = (DlssWanted && _config.NvidiaDlssLicenceAccepted is not null, has, supported) switch
        {
            (_, true, _) => "Your game is built with NVIDIA DLSS: choose it in Graphics → Upscaler.",
            (true, false, true) => "Ready: use \"Check for update\" on the game's page to rebuild it with DLSS (Zest fetches NVIDIA's SDK first).",
            (true, false, false) => $"On. Hookshot {release?.Tag ?? ""} can't build with DLSS yet: it's in Hookshot's source but not in a release. " +
                                    "Zest builds it in when you update to a release that has it.",
            (false, false, true) => "Your Hookshot release can be built with DLSS: turn it on below.",
            _ => $"Hookshot {release?.Tag ?? ""} can't build with DLSS yet (it's coming in a later release). You can turn it on now; Zest builds it in once a release has it.",
        };
    }
}
