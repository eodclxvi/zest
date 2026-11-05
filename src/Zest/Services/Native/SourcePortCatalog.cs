using Zest.Models;

namespace Zest.Services.Native;

/// <summary>
/// A native port built on this PC from source: a static recompilation project (LAS Recomp family) that Zest turns
/// into a playable build from the player's own dump with one button (NativeBuilder). Unlike <see cref="NativePort"/>
/// there is no release to download: the project folder is a local git checkout, cloned from a sibling project's
/// branch when it doesn't exist yet.
/// </summary>
public sealed record SourcePort
{
    /// <summary>Folder name under Zest's data folder (game files, build log); never changes.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Platform Platform { get; init; }
    public required ulong TitleId { get; init; }

    /// <summary>The project checkout (may start with ~).</summary>
    public required string ProjectDir { get; init; }

    /// <summary>When the project folder is missing: clone this local project (path, branch) and branch off it.</summary>
    public (string Dir, string Branch)? CloneFrom { get; init; }

    /// <summary>The project's own branch (created by the clone).</summary>
    public required string Branch { get; init; }

    /// <summary>Main module build ids (NSO header, first 8 bytes as hex) the port is known to play, with the version.</summary>
    public required IReadOnlyDictionary<string, string> KnownBuilds { get; init; }

    /// <summary>The port needs the game's update applied (the base game alone isn't the version it was made for).</summary>
    public bool NeedsUpdate { get; init; }

    /// <summary>Playable, In progress (boots, not playable yet) or Experimental (never run).</summary>
    public required string Status { get; init; }
    public required string Summary { get; init; }

    /// <summary>Extra `make` arguments (dependency paths on this PC).</summary>
    public IReadOnlyList<string> MakeArgs { get; init; } = ["DEPS=~/Projects/recomp/deps", "SHADERC=-lshaderc_shared"];

    public string TitleIdText => TitleId.ToString("X16");
}

/// <summary>The user's own ports (~/Projects/recomp), one per game. Shared fixes are made in one project and merged
/// into the others; each project has its own branch and is never pushed.</summary>
public static class SourcePortCatalog
{
    public static readonly IReadOnlyList<SourcePort> All =
    [
        new()
        {
            Id = "swsh-sword", Name = "Sword Recomp", Platform = Platform.Switch, TitleId = 0x0100ABF008968000,
            ProjectDir = "~/Projects/recomp/swsh-recomp", Branch = "swsh",
            KnownBuilds = new Dictionary<string, string> { ["A3B75BCD3311385A"] = "1.3.2" },
            Status = "Playable",
            Summary = "Pokémon Sword natively on Vulkan: 60 fps, supersampling, DLC, your Eden save.",
        },
        new()
        {
            Id = "swsh-shield", Name = "Shield Recomp", Platform = Platform.Switch, TitleId = 0x01008DB008C2C000,
            ProjectDir = "~/Projects/recomp/shield-recomp", Branch = "shield", CloneFrom = ("~/Projects/recomp/swsh-recomp", "swsh"),
            KnownBuilds = new Dictionary<string, string> { ["A16802625E7826BF"] = "1.3.2" },
            NeedsUpdate = true,
            Status = "Playable",
            Summary = "Pokémon Shield natively on Vulkan: 60 fps, supersampling, Isle of Armor and Crown Tundra.",
        },
        new()
        {
            Id = "sv-violet", Name = "Violet Recomp", Platform = Platform.Switch, TitleId = 0x01008F6008C5E000,
            ProjectDir = "~/Projects/recomp/violet-recomp", Branch = "violet", CloneFrom = ("~/Projects/recomp/swsh-recomp", "swsh"),
            KnownBuilds = new Dictionary<string, string> { ["709BFD6611529864"] = "4.0.0", ["92A2F1DDB9523799"] = "1.2.0" },
            NeedsUpdate = true,
            Status = "Playable",
            Summary = "Pokémon Violet 4.0.0 natively on Vulkan at 30 fps with supersampling, with The Teal Mask and The Indigo Disk.",
        },
        new()
        {
            Id = "sv-scarlet", Name = "Scarlet Recomp", Platform = Platform.Switch, TitleId = 0x0100A3D008C5C000,
            ProjectDir = "~/Projects/recomp/scarlet-recomp", Branch = "scarlet", CloneFrom = ("~/Projects/recomp/violet-recomp", "violet"),
            KnownBuilds = new Dictionary<string, string> { ["421C5411B487EB4D"] = "4.0.0" },
            NeedsUpdate = true,
            Status = "Playable",
            Summary = "Pokémon Scarlet 4.0.0 on Violet's port: 30 fps with supersampling, with The Teal Mask and The Indigo Disk.",
        },
        new()
        {
            Id = "botw", Name = "BotW Recomp", Platform = Platform.Switch, TitleId = 0x01007EF00011E000,
            ProjectDir = "~/Projects/recomp/botw-recomp", Branch = "botw", CloneFrom = ("~/Projects/recomp/violet-recomp", "violet"),
            KnownBuilds = new Dictionary<string, string> { ["2094F152836683A3"] = "1.0.0" },
            Status = "In progress",
            Summary = "Breath of the Wild 1.0.0 on the same runtime. Boots into the game's start-up; not playable yet.",
        },
    ];

    public static SourcePort? For(Platform platform, ulong titleId) =>
        All.FirstOrDefault(p => p.Platform == platform && p.TitleId == titleId);
}
