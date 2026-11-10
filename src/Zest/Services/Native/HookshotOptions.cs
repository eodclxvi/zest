using System.Text;

namespace Zest.Services.Native;

public enum HookshotTab { Graphics, Gameplay, Controls }

/// <summary>A choice of a Hookshot setting: the number saved in hookshot_options.ini and what the menu calls it.</summary>
public sealed record HookshotChoice(int Value, string Label);

/// <summary>
/// One row of Hookshot's in-game menu (gpu/vk/vk_menu.cpp, GPL-3.0-or-later): its key in hookshot_options.ini, the labels
/// it has had (to tell whether the installed build has it), its choices, and whether it applies only after a restart or
/// leaves the Quality preset alone.
/// </summary>
public sealed record HookshotRow(
    string Key, HookshotTab Tab, string[] Labels, IReadOnlyList<HookshotChoice> Choices, int Default, string Help,
    bool Restart = false, bool PresetExempt = false, bool NeedsDlss = false)
{
    public string Label => Labels[0];
}

/// <summary>
/// Hookshot's options file (hookshot_options.ini in the game folder): read and written the way the game's own menu does
/// (key=number lines; the controls and anything else Zest doesn't know are kept as they are). Only the rows the installed
/// build has are offered: Zest looks for each row's label in the game's binary, so a newer Hookshot's rows (NVIDIA Reflex,
/// DLSS) appear once it's installed. The game reads the file when it starts, so it's not written while the game runs.
/// </summary>
public sealed class HookshotOptions
{
    private static HookshotChoice[] OnOff => [new(0, "Off"), new(1, "On")];

    private static HookshotChoice[] Steps(int lo, int hi, int step, Func<int, string> label) =>
        [.. Enumerable.Range(0, (hi - lo) / step + 1).Select(i => lo + i * step).Select(v => new HookshotChoice(v, label(v)))];

    public static readonly IReadOnlyList<HookshotRow> AllRows =
    [
        // ---- Graphics
        new("quality_preset", HookshotTab.Graphics, ["Quality preset"],
            [new(0, "Custom"), new(1, "Minimum (4 GB)"), new(2, "Low (6 GB)"), new(3, "Balanced (8 GB)"), new(4, "High (12 GB)"), new(5, "Ultra (16 GB+)")],
            0, "Sets resolution, scaling, frame generation and video-memory limits for a graphics card with that much memory. Changing one of those rows yourself makes it Custom."),
        new("fullscreen", HookshotTab.Graphics, ["Fullscreen"], OnOff, 0, "Fills the whole screen, without the window's border (F11 in game).", PresetExempt: true),
        new("frame_generation", HookshotTab.Graphics, ["Frame generation"],
            [new(0, "Off"), .. Enumerable.Range(2, 7).Select(v => new HookshotChoice(v, $"{v}x"))],
            0, "Makes extra frames between the game's own for smoother motion (4x: 240 fps on a 240 Hz screen). Adds a little input delay; fast motion can show artifacts."),
        new("frame_pacing", HookshotTab.Graphics, ["Frame pacing"], OnOff, 0, "Shows each frame at an even interval: less stutter, up to one frame of delay."),
        new("nvidia_reflex", HookshotTab.Graphics, ["NVIDIA Reflex"], [new(0, "Off"), new(1, "On"), new(2, "On + Boost")], 0,
            "NVIDIA cards: starts each frame as late as it can, for less input delay. Not used with frame generation."),
        new("output_res", HookshotTab.Graphics, ["Output resolution"],
            [new(3, "1920×1080"), new(0, "2048×1152 (the game's)"), new(1, "2560×1440"), new(2, "3840×2160")], 0,
            "The size of the game's whole frame, HUD included, before it's scaled to the window. Match your screen for the sharpest picture. Docked only.", Restart: true),
        new("dlss", HookshotTab.Graphics, ["Upscaler"], [new(0, "Game's own"), new(1, "NVIDIA DLSS"), new(2, "NVIDIA DLAA")], 0,
            "What scales the 3D up to the output resolution. DLSS and DLAA need an RTX card and a game built with NVIDIA's DLSS SDK.", NeedsDlss: true),
        new("dlss_model", HookshotTab.Graphics, ["DLSS model"],
            [new(0, "Transformer (K)"), new(1, "Transformer 2 (M)"), new(2, "Transformer 2 (L)"), new(3, "NVIDIA default")], 0,
            "Which of NVIDIA's DLSS networks draws the picture.", PresetExempt: true, NeedsDlss: true),
        new("resolution", HookshotTab.Graphics, ["Render resolution", "Resolution"],
            [new(0, "Auto (dynamic)"), new(1, "100%"), new(2, "75%"), new(3, "67%"), new(4, "50%")], 0,
            "How much of the output the 3D is drawn at. Auto lets the game lower it when the GPU is busy; a fixed share stays steady."),
        new("upscaling", HookshotTab.Graphics, ["Window scaling filter", "Upscaling"],
            [new(0, "Off"), new(1, "Catmull-Rom"), new(2, "Lanczos")], 0,
            "How the finished picture is scaled to the window when their sizes differ. Lanczos is the sharpest."),
        new("sharpness", HookshotTab.Graphics, ["Sharpness"], Steps(0, 100, 10, v => $"{v}%"), 0,
            "Sharpens the picture on its way to the window. Too much shows halos around edges."),
        new("handheld_profile", HookshotTab.Graphics, ["Handheld profile"], OnOff, 0,
            "Runs the game as if the console were undocked: lower resolution, lighter on the GPU.", Restart: true),
        new("ultrawide", HookshotTab.Graphics, ["Ultrawide"], [new(0, "16:9"), new(1, "Monitor's shape")], 0,
            "Widens the camera to fill a screen wider (or narrower) than 16:9.", Restart: true),
        new("robust_buffers", HookshotTab.Graphics, ["Robust buffers"], [new(0, "Auto"), new(1, "Off"), new(2, "On")], 0,
            "A read past a buffer's end is dropped instead of stopping the GPU. Auto is on; Off can be faster on some NVIDIA cards.", Restart: true),

        // ---- Gameplay
        new("fps_counter", HookshotTab.Gameplay, ["FPS counter"], OnOff, 0, "Frames per second in the top right corner.", PresetExempt: true),
        new("game_speed", HookshotTab.Gameplay, ["Game speed"], [new(0, "Per frame"), new(1, "Real time")], 1,
            "Per frame: the game slows down below 60 fps. Real time: it keeps normal speed down to 15 fps."),
        new("text_speed", HookshotTab.Gameplay, ["Text speed"], [new(0, "Normal"), new(1, "2x"), new(2, "4x"), new(3, "Instant")], 0,
            "How fast dialogue appears.", PresetExempt: true),
        new("audio_output", HookshotTab.Gameplay, ["Audio output"], [new(0, "Auto"), new(1, "Stereo"), new(2, "5.1")], 0,
            "The speakers the game mixes its sound for.", Restart: true, PresetExempt: true),

        // ---- Controls (the button bindings stay in the game's own Controls page)
        new("controller_in_background", HookshotTab.Controls, ["Controller in background"], OnOff, 0,
            "Keeps the controller working while another window has the focus.", PresetExempt: true),
        new("mouse_camera", HookshotTab.Controls, ["Mouse camera"], [new(0, "As the stick"), new(1, "Native")], 0,
            "Native turns the camera with the mouse one to one, like a PC game.", PresetExempt: true),
        new("mouse_sensitivity", HookshotTab.Controls, ["Mouse sensitivity"], Steps(10, 500, 10, v => (v / 100.0).ToString("0.00")), 100,
            "How far the camera turns for a mouse movement (- and = change it in game too).", PresetExempt: true),
        new("invert_mouse_vertical", HookshotTab.Controls, ["Invert mouse vertical"], OnOff, 0, "Swaps up and down for the mouse.", PresetExempt: true),
        new("invert_camera_x", HookshotTab.Controls, ["Invert camera X"], OnOff, 0, "Inverts left/right look on the right stick.", PresetExempt: true),
        new("invert_camera_y", HookshotTab.Controls, ["Invert camera Y"], OnOff, 0, "Inverts up/down look on the right stick.", PresetExempt: true),
        new("stick_dead_zone", HookshotTab.Controls, ["Stick dead zone"],
            [new(0, "Default (24%)"), new(1, "5%"), new(2, "10%"), new(3, "15%"), new(4, "20%"), new(5, "30%")], 0,
            "How far a stick moves before it counts. Lower for a precise stick, higher for one that drifts.", PresetExempt: true),
        new("rumble_strength", HookshotTab.Controls, ["Rumble strength"], Steps(0, 150, 25, v => v == 0 ? "Off" : $"{v}%"), 100,
            "How strongly the controller rumbles; 100% is the game's own.", PresetExempt: true),
    ];

    /// <summary>What each Quality preset sets (vk_menu.cpp apply_preset): Render resolution, filter, sharpness, frame
    /// generation, pacing, game speed, handheld profile.</summary>
    private static readonly Dictionary<int, (int res, int filter, int sharp, int framegen, int pace, int speed, int handheld)> Presets = new()
    {
        [1] = (4, 2, 50, 2, 1, 1, 1),
        [2] = (2, 2, 35, 2, 1, 1, 1),
        [3] = (1, 1, 25, 2, 1, 1, 0),
        [4] = (1, 2, 30, 2, 1, 1, 0),
        [5] = (1, 2, 40, 3, 1, 1, 0),
    };

    private readonly string _file;
    private readonly List<string> _lines;
    private readonly Dictionary<string, int> _values = new(StringComparer.Ordinal);

    public IReadOnlyList<HookshotRow> Rows { get; }

    private HookshotOptions(string gameFolder, IReadOnlyList<HookshotRow> rows)
    {
        _file = Path.Combine(gameFolder, "hookshot_options.ini");
        _lines = File.Exists(_file) ? [.. File.ReadAllLines(_file)] : [];
        foreach (var line in _lines)
        {
            var eq = line.IndexOf('=');
            if (eq > 0 && int.TryParse(line[(eq + 1)..].Trim(), out var v))
                _values[line[..eq].Trim()] = v;
        }
        Rows = rows;
    }

    /// <summary>The game folder's options, offering the rows its build has (the menu is in its renderer, bin/hookshot_vk.so).</summary>
    public static HookshotOptions Load(string gameFolder)
    {
        var renderer = Path.Combine(gameFolder, "bin", "hookshot_vk.so");
        var binary = File.Exists(renderer) ? renderer : Path.Combine(gameFolder, "bin", "hookshot");
        var has = BuildRows(binary);
        var dlss = HookshotDlss.GameHasDlss(gameFolder);
        return new HookshotOptions(gameFolder, [.. AllRows.Where(r => has(r) && (!r.NeedsDlss || dlss))]);
    }

    /// <summary>Which rows the game's binary has: a row is there when one of its labels is (as a C string).</summary>
    private static Func<HookshotRow, bool> BuildRows(string binary)
    {
        if (!File.Exists(binary))
            return _ => true;
        var data = File.ReadAllBytes(binary);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in AllRows.SelectMany(r => r.Labels).Distinct())
        {
            var needle = Encoding.UTF8.GetBytes("\0" + label + "\0");
            if (data.AsSpan().IndexOf(needle) >= 0)
                found.Add(label);
        }
        return r => r.Labels.Any(found.Contains);
    }

    public int Get(HookshotRow row) =>
        _values.TryGetValue(row.Key, out var v) && row.Choices.Any(c => c.Value == v) ? v
        : _values.TryGetValue(row.Key, out var raw) ? Nearest(row, raw) : row.Default;

    private static int Nearest(HookshotRow row, int v) => row.Choices.MinBy(c => Math.Abs(c.Value - v))!.Value;

    /// <summary>Sets a row the way the menu does: a preset fills in its rows, and changing one of those makes it Custom.</summary>
    public void Set(HookshotRow row, int value)
    {
        _values[row.Key] = value;
        if (row.Key == "quality_preset" && Presets.TryGetValue(value, out var p))
        {
            _values["resolution"] = p.res;
            _values["upscaling"] = p.filter;
            _values["sharpness"] = p.sharp;
            _values["frame_generation"] = p.framegen;
            _values["frame_pacing"] = p.pace;
            _values["game_speed"] = p.speed;
            _values["handheld_profile"] = p.handheld;
        }
        else if (row.Key != "quality_preset" && !row.PresetExempt)
        {
            _values["quality_preset"] = 0;
        }
    }

    /// <summary>Writes the file: known keys updated in place, new ones added, every other line kept. A copy of the
    /// file as it was is kept once, as hookshot_options.ini.zest-backup.</summary>
    public void Save()
    {
        var backup = _file + ".zest-backup";
        if (File.Exists(_file) && !File.Exists(backup))
            File.Copy(_file, backup);
        var written = new HashSet<string>(StringComparer.Ordinal);
        var known = AllRows.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var line in _lines)
        {
            var eq = line.IndexOf('=');
            var key = eq > 0 ? line[..eq].Trim() : "";
            if (known.Contains(key) && _values.TryGetValue(key, out var v))
            {
                output.Add($"{key}={v}");
                written.Add(key);
            }
            else
            {
                output.Add(line);
            }
        }
        // New keys go before options_version, as the game writes them
        var at = output.FindIndex(l => l.StartsWith("options_version=", StringComparison.Ordinal));
        var added = AllRows.Where(r => !written.Contains(r.Key) && _values.ContainsKey(r.Key)).Select(r => $"{r.Key}={_values[r.Key]}").ToList();
        if (at < 0)
        {
            output.AddRange(added);
            output.Add("options_version=3");
        }
        else
        {
            output.InsertRange(at, added);
        }
        File.WriteAllLines(_file + ".tmp", output);
        File.Move(_file + ".tmp", _file, overwrite: true);
        _lines.Clear();
        _lines.AddRange(output);
    }

    /// <summary>The game is running (bin/hookshot): it reads the file at its start and writes it from its menu.</summary>
    public static bool GameRunning() =>
        System.Diagnostics.Process.GetProcessesByName("hookshot").Length > 0;
}
