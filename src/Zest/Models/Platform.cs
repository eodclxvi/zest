namespace Zest.Models;

public enum Platform
{
    Switch,
    N3DS,
    PS2,
}

public static class PlatformInfo
{
    public static readonly Platform[] All = [Platform.Switch, Platform.N3DS, Platform.PS2];

    public static string Name(Platform p) => p switch
    {
        Platform.Switch => "Nintendo Switch",
        Platform.N3DS => "Nintendo 3DS",
        Platform.PS2 => "PlayStation 2",
        _ => p.ToString(),
    };

    public static string Short(Platform p) => p switch
    {
        Platform.Switch => "Switch",
        Platform.N3DS => "3DS",
        Platform.PS2 => "PS2",
        _ => p.ToString(),
    };

    public static string Emulator(Platform p) => p switch
    {
        Platform.Switch => "Eden",
        Platform.N3DS => "Azahar",
        Platform.PS2 => "PCSX2",
        _ => p.ToString(),
    };

    public static IReadOnlySet<string> Extensions(Platform p) => p switch
    {
        Platform.Switch => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".nsp", ".xci", ".nsz", ".xcz", ".nro" },
        Platform.N3DS => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".3ds", ".cci", ".cxi", ".3dsx", ".z3ds", ".zcci", ".zcxi", ".z3dsx" },
        // .bin is left out on purpose: PS2 BIOS dumps use it too.
        Platform.PS2 => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".iso", ".chd", ".cso", ".zso", ".cue", ".gz", ".mdf", ".elf" },
        _ => new HashSet<string>(),
    };

    /// <summary>System folder name on thumbnails.libretro.com, or null when it has no boxart for the platform.</summary>
    public static string? LibretroSystem(Platform p) => p switch
    {
        Platform.N3DS => "Nintendo - Nintendo 3DS",
        Platform.PS2 => "Sony - PlayStation 2",
        _ => null,
    };
}
