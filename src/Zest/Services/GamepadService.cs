using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Zest.Services;

public enum PadButton
{
    Up,
    Down,
    Left,
    Right,
    Confirm, // bottom face button (A on Xbox, ✕ on PlayStation, B on Nintendo)
    Back,    // right face button
    West,    // left face button
    North,   // top face button
    PrevTab, // left shoulder
    NextTab, // right shoulder
    Start,
    Select,
}

/// <summary>
/// Polls the first connected controller through SDL3 (no SDL window) and raises presses with key-repeat for
/// directions. Suspend() releases the controller entirely so emulators have it to themselves.
/// </summary>
public sealed class GamepadService : IDisposable
{
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan RepeatRate = TimeSpan.FromMilliseconds(95);
    private const short StickThreshold = 16000;

    private readonly DispatcherTimer _timer;
    private readonly bool[] _held = new bool[Enum.GetValues<PadButton>().Length];
    private readonly DateTime[] _nextRepeat = new DateTime[Enum.GetValues<PadButton>().Length];
    private nint _pad;
    private int _ticks;
    private bool _suspended;

    public event Action<PadButton>? Pressed;
    public event Action? ConnectionChanged;

    public bool Available { get; }
    public bool IsConnected => _pad != 0;
    public string? ControllerName { get; private set; }

    /// <summary>The printed label of each face button on the connected controller, e.g. "A" or "✕".</summary>
    public string LabelOf(PadButton button)
    {
        if (_pad == 0)
            return "";
        var sdlButton = button switch
        {
            PadButton.Confirm => Sdl.ButtonSouth,
            PadButton.Back => Sdl.ButtonEast,
            PadButton.West => Sdl.ButtonWest,
            PadButton.North => Sdl.ButtonNorth,
            _ => -1,
        };
        if (sdlButton < 0)
            return button.ToString();
        return Sdl.SDL_GetGamepadButtonLabel(_pad, sdlButton) switch
        {
            1 => "A", 2 => "B", 3 => "X", 4 => "Y",
            5 => "✕", 6 => "○", 7 => "□", 8 => "△",
            _ => button switch { PadButton.Confirm => "A", PadButton.Back => "B", PadButton.West => "X", _ => "Y" },
        };
    }

    public GamepadService()
    {
        try
        {
            // Read controllers through evdev only: HIDAPI would talk to PlayStation/Switch pads directly and
            // could clash with the emulators doing the same.
            Sdl.SDL_SetHint("SDL_JOYSTICK_HIDAPI", "0");
            Sdl.SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            Available = Sdl.SDL_Init(Sdl.InitGamepad);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Available = false;
        }

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => Poll());
        if (Available)
            _timer.Start();
    }

    /// <summary>Close the controller and stop polling (while a game is running).</summary>
    public void Suspend()
    {
        if (!Available || _suspended)
            return;
        _suspended = true;
        _timer.Stop();
        Close();
    }

    public void Resume()
    {
        if (!Available || !_suspended)
            return;
        _suspended = false;
        Array.Clear(_held);
        _ticks = 0;
        _timer.Start();
    }

    private void Poll()
    {
        Sdl.SDL_PumpEvents();
        Sdl.SDL_UpdateGamepads();

        if (_pad != 0 && !Sdl.SDL_GamepadConnected(_pad))
            Close();
        if (_pad == 0)
        {
            if (_ticks++ % 30 == 0) // look for a newly plugged-in controller twice a second
                TryOpen();
            if (_pad == 0)
                return;
        }

        var lx = Sdl.SDL_GetGamepadAxis(_pad, Sdl.AxisLeftX);
        var ly = Sdl.SDL_GetGamepadAxis(_pad, Sdl.AxisLeftY);
        Update(PadButton.Up, Button(Sdl.ButtonDpadUp) || ly < -StickThreshold);
        Update(PadButton.Down, Button(Sdl.ButtonDpadDown) || ly > StickThreshold);
        Update(PadButton.Left, Button(Sdl.ButtonDpadLeft) || lx < -StickThreshold);
        Update(PadButton.Right, Button(Sdl.ButtonDpadRight) || lx > StickThreshold);
        Update(PadButton.Confirm, Button(Sdl.ButtonSouth));
        Update(PadButton.Back, Button(Sdl.ButtonEast));
        Update(PadButton.West, Button(Sdl.ButtonWest));
        Update(PadButton.North, Button(Sdl.ButtonNorth));
        Update(PadButton.PrevTab, Button(Sdl.ButtonLeftShoulder));
        Update(PadButton.NextTab, Button(Sdl.ButtonRightShoulder));
        Update(PadButton.Start, Button(Sdl.ButtonStart));
        Update(PadButton.Select, Button(Sdl.ButtonBack));
    }

    private bool Button(int b) => Sdl.SDL_GetGamepadButton(_pad, b);

    private void Update(PadButton button, bool down)
    {
        var i = (int)button;
        var now = DateTime.UtcNow;
        if (down && !_held[i])
        {
            _nextRepeat[i] = now + RepeatDelay;
            Pressed?.Invoke(button);
        }
        else if (down && button <= PadButton.Right && now >= _nextRepeat[i])
        {
            _nextRepeat[i] = now + RepeatRate;
            Pressed?.Invoke(button);
        }
        _held[i] = down;
    }

    private void TryOpen()
    {
        var ids = Sdl.SDL_GetGamepads(out var count);
        try
        {
            if (count > 0)
                _pad = Sdl.SDL_OpenGamepad((uint)Marshal.ReadInt32(ids));
        }
        finally
        {
            if (ids != 0)
                Sdl.SDL_free(ids);
        }
        if (_pad == 0)
            return;
        ControllerName = Marshal.PtrToStringUTF8(Sdl.SDL_GetGamepadName(_pad));
        Array.Clear(_held);
        ConnectionChanged?.Invoke();
    }

    private void Close()
    {
        if (_pad == 0)
            return;
        Sdl.SDL_CloseGamepad(_pad);
        _pad = 0;
        ControllerName = null;
        ConnectionChanged?.Invoke();
    }

    public void Dispose()
    {
        _timer.Stop();
        Close();
        if (Available)
            Sdl.SDL_Quit();
    }

    private static class Sdl
    {
        private const string Lib = "libSDL3.so.0";

        public const uint InitGamepad = 0x00002000;
        public const int ButtonSouth = 0, ButtonEast = 1, ButtonWest = 2, ButtonNorth = 3, ButtonBack = 4, ButtonStart = 6;
        public const int ButtonLeftShoulder = 9, ButtonRightShoulder = 10;
        public const int ButtonDpadUp = 11, ButtonDpadDown = 12, ButtonDpadLeft = 13, ButtonDpadRight = 14;
        public const int AxisLeftX = 0, AxisLeftY = 1;

        [DllImport(Lib)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SDL_Init(uint flags);

        [DllImport(Lib)]
        public static extern void SDL_Quit();

        [DllImport(Lib)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(Lib)]
        public static extern void SDL_PumpEvents();

        [DllImport(Lib)]
        public static extern void SDL_UpdateGamepads();

        [DllImport(Lib)]
        public static extern nint SDL_GetGamepads(out int count);

        [DllImport(Lib)]
        public static extern nint SDL_OpenGamepad(uint instanceId);

        [DllImport(Lib)]
        public static extern void SDL_CloseGamepad(nint gamepad);

        [DllImport(Lib)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SDL_GamepadConnected(nint gamepad);

        [DllImport(Lib)]
        public static extern nint SDL_GetGamepadName(nint gamepad);

        [DllImport(Lib)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SDL_GetGamepadButton(nint gamepad, int button);

        [DllImport(Lib)]
        public static extern short SDL_GetGamepadAxis(nint gamepad, int axis);

        [DllImport(Lib)]
        public static extern int SDL_GetGamepadButtonLabel(nint gamepad, int button);

        [DllImport(Lib)]
        public static extern void SDL_free(nint memory);
    }
}
