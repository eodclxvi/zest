using Avalonia.Input;

namespace Zest.Services.Native;

/// <summary>
/// Avalonia's physical keys (positions, like SDL scancodes) as SDL3 scancode names, the form larecomp.ini's
/// bind_key_* lines use (the runtime reads them with SDL_GetScancodeFromName).
/// </summary>
public static class SdlKeyNames
{
    private static readonly Dictionary<PhysicalKey, string> Names = Build();

    public static string? FromPhysical(PhysicalKey key) => Names.GetValueOrDefault(key);

    private static Dictionary<PhysicalKey, string> Build()
    {
        var d = new Dictionary<PhysicalKey, string>();
        // (by name: the enum's numbering isn't promised to keep these runs together)
        for (var c = 'A'; c <= 'Z'; c++)
            d[Enum.Parse<PhysicalKey>(c.ToString())] = c.ToString();
        for (var i = 0; i <= 9; i++)
        {
            d[Enum.Parse<PhysicalKey>($"Digit{i}")] = i.ToString();
            d[Enum.Parse<PhysicalKey>($"NumPad{i}")] = $"Keypad {i}";
        }
        for (var i = 1; i <= 12; i++)
            d[Enum.Parse<PhysicalKey>($"F{i}")] = $"F{i}";

        d[PhysicalKey.Enter] = "Return";
        d[PhysicalKey.Backspace] = "Backspace";
        d[PhysicalKey.Tab] = "Tab";
        d[PhysicalKey.Space] = "Space";
        d[PhysicalKey.Minus] = "-";
        d[PhysicalKey.Equal] = "=";
        d[PhysicalKey.BracketLeft] = "[";
        d[PhysicalKey.BracketRight] = "]";
        d[PhysicalKey.Backslash] = "\\";
        d[PhysicalKey.Semicolon] = ";";
        d[PhysicalKey.Quote] = "'";
        d[PhysicalKey.Backquote] = "`";
        d[PhysicalKey.Comma] = ",";
        d[PhysicalKey.Period] = ".";
        d[PhysicalKey.Slash] = "/";
        d[PhysicalKey.CapsLock] = "CapsLock";
        d[PhysicalKey.Insert] = "Insert";
        d[PhysicalKey.Delete] = "Delete";
        d[PhysicalKey.Home] = "Home";
        d[PhysicalKey.End] = "End";
        d[PhysicalKey.PageUp] = "PageUp";
        d[PhysicalKey.PageDown] = "PageDown";
        d[PhysicalKey.ArrowLeft] = "Left";
        d[PhysicalKey.ArrowRight] = "Right";
        d[PhysicalKey.ArrowUp] = "Up";
        d[PhysicalKey.ArrowDown] = "Down";
        d[PhysicalKey.NumPadDivide] = "Keypad /";
        d[PhysicalKey.NumPadMultiply] = "Keypad *";
        d[PhysicalKey.NumPadSubtract] = "Keypad -";
        d[PhysicalKey.NumPadAdd] = "Keypad +";
        d[PhysicalKey.NumPadEnter] = "Keypad Enter";
        d[PhysicalKey.NumPadDecimal] = "Keypad .";
        d[PhysicalKey.ControlLeft] = "Left Ctrl";
        d[PhysicalKey.ShiftLeft] = "Left Shift";
        d[PhysicalKey.AltLeft] = "Left Alt";
        d[PhysicalKey.ControlRight] = "Right Ctrl";
        d[PhysicalKey.ShiftRight] = "Right Shift";
        d[PhysicalKey.AltRight] = "Right Alt";
        return d;
    }
}
