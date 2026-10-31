using System.Text.RegularExpressions;
using Zest.Models;

namespace Zest.Services.Native;

/// <summary>
/// A community static recompilation of one game: a native PC build of the game, made on this machine from the
/// player's own game files. Zest installs the project's official release and hands it a folder it prepares itself.
/// </summary>
public sealed record NativePort
{
    /// <summary>Folder name under Zest's data folder; never changes once released.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string Repo { get; init; }
    public required Platform Platform { get; init; }
    public required ulong TitleId { get; init; }

    /// <summary>Base-game versions (the control.nacp display version) the port accepts. Zest only extracts the base game.</summary>
    public required IReadOnlySet<string> Versions { get; init; }

    /// <summary>ExeFS files the port's setup screen checks for.</summary>
    public required IReadOnlyList<string> ExeFsFiles { get; init; }

    /// <summary>Picks the Linux x86-64 AppImage from a release's assets.</summary>
    public required Regex Asset { get; init; }

    public required string Status { get; init; }
    public required string Licence { get; init; }
    public required string Summary { get; init; }

    /// <summary>Shown under the card, as the project asks to be credited and described.</summary>
    public required string Credit { get; init; }

    public string ProjectUrl => $"https://github.com/{Repo}";
    public string TitleIdText => TitleId.ToString("X16");
}

/// <summary>
/// The ports that match games in this library. Each entry was checked against the project's own README:
/// accepted versions, the files its setup screen wants, and that it builds the game code on the player's machine
/// (no release ships Nintendo's code).
/// </summary>
public static class NativePortCatalog
{
    public static readonly IReadOnlyList<NativePort> All =
    [
        // github.com/peiikai/LASRecomp README: "v1.0.0, v1.0.1 or v1.1.0", a folder with exefs and romfs; MIT.
        new()
        {
            Id = "lasrecomp",
            Name = "LAS Recomp",
            Author = "peiikai",
            Repo = "peiikai/LASRecomp",
            Platform = Platform.Switch,
            TitleId = 0x01006BB00C6F0000,
            Versions = new HashSet<string> { "1.0.0", "1.0.1", "1.1.0" },
            ExeFsFiles = ["main", "main.npdm"],
            Asset = new Regex(@"^LAS\.Recomp-.+-linux-x86_64\.AppImage$"),
            Status = "Beta",
            Licence = "MIT",
            Summary = "Link's Awakening running natively on Vulkan: ultrawide, higher resolutions, remappable controls and a built-in randomizer.",
            Credit = "LAS Recomp by peiikai and contributors (MIT). The project says it was made almost exclusively with AI (Claude Code), "
                + "directed, tested and reviewed by a human. Not affiliated with Nintendo.",
        },

        // github.com/Apolinario-coder/SkyRecomp README: "v1.0.0, without updates"; exefs with main, sdk, subsdk0,
        // subsdk1 plus romfs/. Its runtime is built on LAS Recomp.
        new()
        {
            Id = "skyrecomp",
            Name = "Sky Recomp",
            Author = "Apolinario-coder",
            Repo = "Apolinario-coder/SkyRecomp",
            Platform = Platform.Switch,
            TitleId = 0x01002DA013484000,
            Versions = new HashSet<string> { "1.0.0" },
            ExeFsFiles = ["main", "main.npdm", "sdk", "subsdk0", "subsdk1"],
            Asset = new Regex(@"^Sky\.Recomp-.+-linux-x86_64\.AppImage$"),
            Status = "Experimental",
            Licence = "see the project page (its LAS Recomp parts are MIT)",
            Summary = "Skyward Sword HD running natively on Vulkan: 60 FPS by default, optional 120 FPS, mouse camera and a mod manager.",
            Credit = "Sky Recomp by Apolinario-coder, built on LAS Recomp. Experimental: crashes and menu freezes are still being investigated. "
                + "Not affiliated with Nintendo.",
        },
    ];

    public static NativePort? For(Platform platform, ulong titleId) =>
        All.FirstOrDefault(p => p.Platform == platform && p.TitleId == titleId);
}
