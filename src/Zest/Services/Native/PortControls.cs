namespace Zest.Services.Native;

/// <summary>
/// Control mapping of a LAS Recomp-family port (the user's Sword/Shield/Violet builds), kept in the port's
/// larecomp.ini next to its other settings. Mirrors the runtime's runtime/hle/bindings.c: the same input names,
/// controller button names and defaults, and like the runtime only bindings that differ from the defaults are
/// written. Every other line of the file is kept as it is.
/// </summary>
public sealed class PortControls
{
    public const string FileName = "larecomp.ini";

    /// <summary>Switch inputs in the runtime's order (BIND_A..): the first 16 can be bound on a controller.</summary>
    public static readonly (string Ini, string Name)[] Inputs =
    [
        ("a", "A"), ("b", "B"), ("x", "X"), ("y", "Y"), ("lstick", "Left stick press"), ("rstick", "Right stick press"),
        ("l", "L"), ("r", "R"), ("zl", "ZL"), ("zr", "ZR"), ("plus", "Plus"), ("minus", "Minus"),
        ("left", "D-pad Left"), ("up", "D-pad Up"), ("right", "D-pad Right"), ("down", "D-pad Down"),
        ("ls_up", "Left stick Up"), ("ls_down", "Left stick Down"), ("ls_left", "Left stick Left"), ("ls_right", "Left stick Right"),
        ("rs_up", "Right stick Up"), ("rs_down", "Right stick Down"), ("rs_left", "Right stick Left"), ("rs_right", "Right stick Right"),
    ];
    public const int PadCount = 16;

    /// <summary>Controller buttons by position: SDL_GamepadButton order, then the two triggers (26, 27).</summary>
    public static readonly string[] PadIni =
    [
        "south", "east", "west", "north", "back", "guide", "start", "lstick", "rstick", "lshoulder", "rshoulder",
        "dpad_up", "dpad_down", "dpad_left", "dpad_right", "misc1", "rpaddle1", "lpaddle1", "rpaddle2", "lpaddle2",
        "touchpad", "misc2", "misc3", "misc4", "misc5", "misc6", "ltrigger", "rtrigger",
    ];
    public const int LTrigger = 26, RTrigger = 27;

    private static readonly string[] NintendoLabels =
    [
        "B", "A", "Y", "X", "Minus", "Home", "Plus", "Left stick press", "Right stick press", "L", "R",
        "D-pad Up", "D-pad Down", "D-pad Left", "D-pad Right", "Capture", "Right paddle 1", "Left paddle 1",
        "Right paddle 2", "Left paddle 2", "Touchpad", "Misc 2", "Misc 3", "Misc 4", "Misc 5", "Misc 6", "ZL", "ZR",
    ];
    private static readonly string[] XboxLabels =
    [
        "A", "B", "X", "Y", "View", "Xbox", "Menu", "LS (press)", "RS (press)", "LB", "RB",
        "D-pad Up", "D-pad Down", "D-pad Left", "D-pad Right", "Share", "Paddle P1", "Paddle P3",
        "Paddle P2", "Paddle P4", "Touchpad", "Misc 2", "Misc 3", "Misc 4", "Misc 5", "Misc 6", "LT", "RT",
    ];
    private static readonly string[] PlayStationLabels =
    [
        "Cross", "Circle", "Square", "Triangle", "Create", "PS", "Options", "L3", "R3", "L1", "R1",
        "D-pad Up", "D-pad Down", "D-pad Left", "D-pad Right", "Mute", "Right back button", "Left back button",
        "Right Fn", "Left Fn", "Touchpad", "Misc 2", "Misc 3", "Misc 4", "Misc 5", "Misc 6", "L2", "R2",
    ];

    /// <summary>A controller button in its family's own words (family: 0 Nintendo/other, 1 Xbox, 2 PlayStation).</summary>
    public static string PadLabel(int host, int family) =>
        host < 0 || host >= PadIni.Length ? "(none)"
        : (family == 1 ? XboxLabels : family == 2 ? PlayStationLabels : NintendoLabels)[host];

    // Defaults (bindings.c DEF_PAD / DEF_KEY). Controller: Nintendo layout by position, triggers = ZL / ZR.
    private static readonly int[] DefaultPadNintendo = [1, 0, 3, 2, 7, 8, 9, 10, LTrigger, RTrigger, 6, 4, 13, 11, 14, 12];
    private static readonly int[] XboxFace = [0, 1, 2, 3]; // A bottom, B right, X left, Y top
    private static readonly string[] DefaultKeys =
    [
        "Space", "Q", "Tab", "R", "F", "Keypad 0", "Z", "C", "1", "3", "=", "-", "Left", "Up", "Right", "Down",
        "W", "S", "A", "D", "Keypad 8", "Keypad 5", "Keypad 4", "Keypad 6",
    ];

    public static int DefaultPad(bool xbox, int slot) => xbox && slot < 4 ? XboxFace[slot] : DefaultPadNintendo[slot];
    public static string DefaultKey(int slot) => DefaultKeys[slot];

    public bool XboxLayout { get; set; }
    public int[] Pad { get; } = (int[])DefaultPadNintendo.Clone();
    /// <summary>SDL scancode names ("Space", "Keypad 0"...); "" = no key.</summary>
    public string[] Keys { get; } = (string[])DefaultKeys.Clone();
    public double StickDeadzone { get; set; } = 0.15;
    public bool DpadMove { get; set; }

    public string Path { get; }

    private PortControls(string path) => Path = path;

    public static string PathFor(string workingDirectory) => System.IO.Path.Combine(workingDirectory, FileName);

    /// <summary>Reads the port's larecomp.ini (missing file = the defaults), in the order the runtime applies it.</summary>
    public static PortControls Load(string workingDirectory)
    {
        var c = new PortControls(PathFor(workingDirectory));
        if (!File.Exists(c.Path))
            return c;
        foreach (var raw in File.ReadAllLines(c.Path))
        {
            if (!TrySplit(raw, out var key, out var val))
                continue;
            if (key == "pad_layout")
                c.SetLayout(val == "xbox");
            else if (key == "stick_deadzone" && double.TryParse(val, System.Globalization.CultureInfo.InvariantCulture, out var dz))
                c.StickDeadzone = Math.Clamp(dz, 0, 0.4);
            else if (key == "dpad_move")
                c.DpadMove = int.TryParse(val, out var on) && on != 0;
            else if (key.StartsWith("bind_pad_", StringComparison.Ordinal))
            {
                var slot = SlotOf(key[9..]);
                var host = Array.IndexOf(PadIni, val);
                if (slot is >= 0 and < PadCount && host >= 0)
                    c.Pad[slot] = host;
            }
            else if (key.StartsWith("bind_key_", StringComparison.Ordinal))
            {
                var slot = SlotOf(key[9..]);
                if (slot >= 0)
                    c.Keys[slot] = val == "none" ? "" : val;
            }
        }
        return c;
    }

    /// <summary>The runtime's bind_set_layout: A/B/X/Y still at the old layout's defaults move to the new one's.</summary>
    public void SetLayout(bool xbox)
    {
        if (xbox == XboxLayout)
            return;
        for (var i = 0; i < 4; i++)
            if (Pad[i] == DefaultPad(XboxLayout, i))
                Pad[i] = DefaultPad(xbox, i);
        XboxLayout = xbox;
    }

    public void ResetPad()
    {
        for (var i = 0; i < PadCount; i++)
            Pad[i] = DefaultPad(XboxLayout, i);
    }

    public void ResetKeys() => Array.Copy(DefaultKeys, Keys, Keys.Length);

    /// <summary>Binds a controller button to a slot; the slot that had it gets this slot's old button (a swap,
    /// as in the runtime). Returns the other slot, or -1.</summary>
    public int BindPad(int slot, int host)
    {
        var other = Array.IndexOf(Pad, host);
        if (other >= 0 && other != slot)
            Pad[other] = Pad[slot];
        Pad[slot] = host;
        return other == slot ? -1 : other;
    }

    public int BindKey(int slot, string key)
    {
        var other = key == "" ? -1 : Array.IndexOf(Keys, key);
        if (other >= 0 && other != slot)
            Keys[other] = Keys[slot];
        Keys[slot] = key;
        return other == slot ? -1 : other;
    }

    /// <summary>Writes the control lines into larecomp.ini, keeping every other line (a copy goes to .bak first).</summary>
    public void Save()
    {
        var kept = new List<string>();
        if (File.Exists(Path))
        {
            foreach (var raw in File.ReadAllLines(Path))
            {
                if (TrySplit(raw, out var key, out _) &&
                    (key is "pad_layout" or "stick_deadzone" or "dpad_move" ||
                     key.StartsWith("bind_pad_", StringComparison.Ordinal) || key.StartsWith("bind_key_", StringComparison.Ordinal)))
                    continue;
                kept.Add(raw);
            }
            File.Copy(Path, Path + ".bak", overwrite: true);
        }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        kept.Add(string.Create(inv, $"stick_deadzone = {StickDeadzone:0.00}"));
        kept.Add($"dpad_move = {(DpadMove ? 1 : 0)}");
        kept.Add($"pad_layout = {(XboxLayout ? "xbox" : "nintendo")}"); // (before the bind_pad_ lines, as the runtime writes it)
        for (var i = 0; i < PadCount; i++)
            if (Pad[i] != DefaultPad(XboxLayout, i))
                kept.Add($"bind_pad_{Inputs[i].Ini} = {PadIni[Pad[i]]}");
        for (var i = 0; i < Inputs.Length; i++)
            if (Keys[i] != DefaultKeys[i])
                kept.Add($"bind_key_{Inputs[i].Ini} = {(Keys[i] == "" ? "none" : Keys[i])}");

        var tmp = Path + ".tmp";
        File.WriteAllLines(tmp, kept);
        File.Move(tmp, Path, overwrite: true);
    }

    private static int SlotOf(string ini) => Array.FindIndex(Inputs, x => x.Ini == ini);

    // The runtime's format: "key = value"; lines starting with # or ; are comments.
    private static bool TrySplit(string line, out string key, out string val)
    {
        key = val = "";
        var t = line.Trim();
        if (t.Length == 0 || t[0] is '#' or ';')
            return false;
        var eq = t.IndexOf('=');
        if (eq <= 0)
            return false;
        key = t[..eq].Trim();
        val = t[(eq + 1)..].Trim();
        return true;
    }
}
