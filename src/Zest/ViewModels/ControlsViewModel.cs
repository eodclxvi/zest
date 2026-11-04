using System.Collections.ObjectModel;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Zest.Services;
using Zest.Services.Native;

namespace Zest.ViewModels;

/// <summary>One Switch input on the Controls page: its controller button and its keyboard key.</summary>
public partial class ControlRow(ControlsViewModel owner, int slot) : ObservableObject
{
    public int Slot { get; } = slot;
    public string Name => PortControls.Inputs[Slot].Name;
    public bool HasPad => Slot < PortControls.PadCount;

    [ObservableProperty]
    public partial string PadText { get; set; } = "";

    [ObservableProperty]
    public partial string KeyText { get; set; } = "";

    [ObservableProperty]
    public partial bool PadChanged { get; set; }

    [ObservableProperty]
    public partial bool KeyChanged { get; set; }

    [ObservableProperty]
    public partial bool IsCapturingPad { get; set; }

    [ObservableProperty]
    public partial bool IsCapturingKey { get; set; }

    [RelayCommand]
    private void CapturePad() => owner.BeginPadCapture(this);

    [RelayCommand]
    private void CaptureKey() => owner.BeginKeyCapture(this);

    [RelayCommand]
    private void ClearKey() => owner.ClearKey(this);
}

/// <summary>
/// Controls page of a native port (LocalPorts): remaps its controller buttons and keyboard keys, the controller
/// layout, the stick dead zone and d-pad walking, saved in the port's larecomp.ini (PortControls). The port reads
/// the file when it starts, so changes are blocked while a game runs (the port would write its own copy back).
/// </summary>
public partial class ControlsViewModel : ViewModelBase
{
    private const int CaptureSeconds = 5;

    private readonly GamepadService _gamepad;
    private readonly Func<bool> _gameRunning;
    private readonly DispatcherTimer _countdown;
    private PortControls _controls;
    private ControlRow? _capturing;
    private bool _capturingKey;
    private DateTime _captureEnds;

    public ControlsViewModel(string title, string workingDirectory, GamepadService gamepad, Func<bool> gameRunning)
    {
        Title = title;
        _gamepad = gamepad;
        _gameRunning = gameRunning;
        _controls = PortControls.Load(workingDirectory);
        for (var i = 0; i < PortControls.Inputs.Length; i++)
            (i < PortControls.PadCount ? PadRows : KeyOnlyRows).Add(new ControlRow(this, i));
        _countdown = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Normal, (_, _) => Tick());
        _xbox = _controls.XboxLayout;
        _deadzone = Math.Round(_controls.StickDeadzone * 100);
        _dpadMove = _controls.DpadMove;
        Refresh();
        Status = $"Saved in {_controls.Path}";
    }

    public string Title { get; }
    public string FilePath => _controls.Path;

    /// <summary>The 16 buttons (controller and keyboard).</summary>
    public ObservableCollection<ControlRow> PadRows { get; } = [];

    /// <summary>The sticks' directions on the keyboard (a controller's sticks stay its sticks).</summary>
    public ObservableCollection<ControlRow> KeyOnlyRows { get; } = [];

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial string ControllerText { get; set; } = "";

    public bool IsCapturing => _capturing is not null;

    private bool _xbox;
    public bool XboxLayout
    {
        get => _xbox;
        set
        {
            if (!SetProperty(ref _xbox, value) || !Editable())
                return;
            _controls.SetLayout(value);
            Commit(value ? "Xbox layout: A is the bottom button, B the right one." : "Nintendo layout: A is the right button, B the bottom one.");
        }
    }

    public bool NintendoLayout
    {
        get => !XboxLayout;
        set => XboxLayout = !value;
    }

    private double _deadzone;
    /// <summary>Stick dead zone in percent of the travel (0-40).</summary>
    public double Deadzone
    {
        get => _deadzone;
        set
        {
            value = Math.Clamp(Math.Round(value), 0, 40);
            if (!SetProperty(ref _deadzone, value) || !Editable())
                return;
            _controls.StickDeadzone = value / 100;
            OnPropertyChanged(nameof(DeadzoneText));
            Commit($"Stick dead zone: {value:0}%.");
        }
    }

    public string DeadzoneText => $"{Deadzone:0}%";

    private bool _dpadMove;
    public bool DpadMove
    {
        get => _dpadMove;
        set
        {
            if (!SetProperty(ref _dpadMove, value) || !Editable())
                return;
            _controls.DpadMove = value;
            Commit(value ? "The d-pad now walks too (while the stick is at rest)." : "The d-pad only acts as the d-pad.");
        }
    }

    public void BeginPadCapture(ControlRow row) => BeginCapture(row, key: false);

    public void BeginKeyCapture(ControlRow row) => BeginCapture(row, key: true);

    private void BeginCapture(ControlRow row, bool key)
    {
        CancelCapture();
        if (!Editable())
            return;
        if (!key && !_gamepad.IsConnected)
        {
            Status = "Connect a controller first.";
            return;
        }
        _capturing = row;
        _capturingKey = key;
        row.IsCapturingPad = !key;
        row.IsCapturingKey = key;
        _captureEnds = DateTime.UtcNow.AddSeconds(CaptureSeconds);
        if (!key)
            _gamepad.BeginCapture(host => Dispatcher.UIThread.Post(() => PadCaptured(host)));
        _countdown.Start();
        OnPropertyChanged(nameof(IsCapturing));
        Tick();
    }

    private void Tick()
    {
        if (_capturing is null)
            return;
        var left = (int)Math.Ceiling((_captureEnds - DateTime.UtcNow).TotalSeconds);
        if (left <= 0)
        {
            CancelCapture();
            Status = "Nothing pressed: the binding is unchanged.";
            return;
        }
        Status = _capturingKey
            ? $"Press a key for {_capturing.Name}… ({left} s, Esc cancels)"
            : $"Press a button or trigger on the controller for {_capturing.Name}… ({left} s)";
    }

    public void CancelCapture()
    {
        if (_capturing is null)
            return;
        _gamepad.CancelCapture();
        _countdown.Stop();
        _capturing.IsCapturingPad = _capturing.IsCapturingKey = false;
        _capturing = null;
        OnPropertyChanged(nameof(IsCapturing));
        Status = "Cancelled.";
    }

    private void PadCaptured(int host)
    {
        if (_capturing is not { } row || _capturingKey)
            return;
        EndCapture();
        var other = _controls.BindPad(row.Slot, host);
        var label = PortControls.PadLabel(host, _gamepad.IsConnected ? _gamepad.Family : _controls.XboxLayout ? 1 : 0);
        Commit(other >= 0
            ? $"{row.Name} is now {label}; {PortControls.Inputs[other].Name} took {row.Name}'s old button."
            : $"{row.Name} is now {label}.");
    }

    /// <summary>A key pressed while the page is open: true when a capture took it.</summary>
    public bool KeyPressed(PhysicalKey key)
    {
        if (_capturing is not { } row || !_capturingKey)
            return false;
        if (key == PhysicalKey.Escape)
        {
            CancelCapture();
            return true;
        }
        var name = SdlKeyNames.FromPhysical(key);
        if (name is null)
        {
            Status = $"That key ({key}) can't be used; pick another.";
            return true;
        }
        EndCapture();
        var other = _controls.BindKey(row.Slot, name);
        Commit(other >= 0
            ? $"{row.Name} is now the {name} key; {PortControls.Inputs[other].Name} took {row.Name}'s old key."
            : $"{row.Name} is now the {name} key.");
        return true;
    }

    public void ClearKey(ControlRow row)
    {
        CancelCapture();
        if (!Editable())
            return;
        _controls.BindKey(row.Slot, "");
        Commit($"{row.Name} has no key now.");
    }

    [RelayCommand]
    private void ResetController()
    {
        CancelCapture();
        if (!Editable())
            return;
        _controls.ResetPad();
        Commit("Controller buttons are back to the defaults.");
    }

    [RelayCommand]
    private void ResetKeyboard()
    {
        CancelCapture();
        if (!Editable())
            return;
        _controls.ResetKeys();
        Commit("Keyboard keys are back to the defaults.");
    }

    /// <summary>Re-reads the file (the port may have saved its own changes from its in-game menu).</summary>
    public void Reload()
    {
        CancelCapture();
        _controls = PortControls.Load(Path.GetDirectoryName(_controls.Path)!);
        _xbox = _controls.XboxLayout;
        _deadzone = Math.Round(_controls.StickDeadzone * 100);
        _dpadMove = _controls.DpadMove;
        OnPropertyChanged(nameof(XboxLayout));
        OnPropertyChanged(nameof(NintendoLayout));
        OnPropertyChanged(nameof(Deadzone));
        OnPropertyChanged(nameof(DeadzoneText));
        OnPropertyChanged(nameof(DpadMove));
        Refresh();
    }

    public void Refresh()
    {
        // (no controller: name the buttons as on the layout picked, Xbox or Nintendo)
        var family = _gamepad.IsConnected ? _gamepad.Family : _controls.XboxLayout ? 1 : 0;
        foreach (var row in PadRows.Concat(KeyOnlyRows))
        {
            if (row.HasPad)
            {
                row.PadText = PortControls.PadLabel(_controls.Pad[row.Slot], family);
                row.PadChanged = _controls.Pad[row.Slot] != PortControls.DefaultPad(_controls.XboxLayout, row.Slot);
            }
            row.KeyText = _controls.Keys[row.Slot] == "" ? "(none)" : _controls.Keys[row.Slot];
            row.KeyChanged = _controls.Keys[row.Slot] != PortControls.DefaultKey(row.Slot);
        }
        ControllerText = _gamepad.IsConnected
            ? $"{_gamepad.ControllerName} · button names as printed on it"
            : $"No controller connected · showing {(family == 1 ? "Xbox" : "Nintendo")} button names";
    }

    private void EndCapture()
    {
        _countdown.Stop();
        if (_capturing is not null)
            _capturing.IsCapturingPad = _capturing.IsCapturingKey = false;
        _capturing = null;
        OnPropertyChanged(nameof(IsCapturing));
    }

    private bool Editable()
    {
        if (!_gameRunning())
            return true;
        Status = "Close the game first: it keeps its own copy of the settings and would write it back.";
        // (put the switches back to what the file says)
        Dispatcher.UIThread.Post(Reload);
        return false;
    }

    private void Commit(string message)
    {
        try
        {
            _controls.Save();
            Status = message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = $"Couldn't save {_controls.Path}: {e.Message}";
        }
        Refresh();
    }
}
