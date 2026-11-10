using Zest.Services.GameData;

namespace Zest.Services.Walkthroughs;

public enum TipStatus { Done, Partly, NotYet, Unknown }

/// <summary>A "do it as young Link" note, with what the save says about it.</summary>
public sealed record EraTip(string Title, string Where, string Why, string[] Chapters, TipStatus Status, string Detail);

/// <summary>
/// Things best done as young Link in Ocarina of Time (Switch 2), before pulling the Master Sword: Zest's own notes,
/// each checked against the newest save where the save records it. Nothing here is lost for good (putting the sword
/// back in the Temple of Time makes Link young again), but these are quicker now, or unlock things for adult Link.
/// </summary>
public static class OotYoungAdult
{
    private static readonly string[] BeanSoils =
    [
        "KokiriForest", "KokiriForest_LostWood", "LostWood01", "LostWood02", "ZoraRiver", "ZoraRiver02", "LakeHylia",
        "Graveyard", "DeathMountainTrail", "DeathMountainCrater", "GerudoValley", "DesertColossus",
    ];

    private static readonly (string flag, string song)[] FrogSongs =
    [
        ("ZeldaLullaby", "Zelda's Lullaby"), ("EponaSong", "Epona's Song"), ("SariaSong", "Saria's Song"),
        ("SunSong", "Sun's Song"), ("TimeSong", "Song of Time"), ("StormSong", "Song of Storms"),
    ];

    /// <summary>The tips, checked against <paramref name="save"/> (the newest progress.sav) when there is one.</summary>
    public static IReadOnlyList<EraTip> Tips(GameDataSave? save)
    {
        TipStatus Flag(string name) => save is null ? TipStatus.Unknown : save.Bool(name) ? TipStatus.Done : TipStatus.NotYet;
        TipStatus Step(string quest, params string[] doneSteps) =>
            save?.Enum("Step_" + quest) is not { } v ? TipStatus.Unknown
            : doneSteps.Any(s => Murmur3.Hash(s) == v) ? TipStatus.Done : TipStatus.NotYet;

        var beans = save is null ? -1 : BeanSoils.Count(s => save.Bool("Gimmick_MagicBeanSoil_" + s));
        var frogs = save is null ? -1 : FrogSongs.Count(f => save.Bool("MiniGame_FrogTenors_" + f.flag));
        var missingFrogs = save is null ? "" : string.Join(", ", FrogSongs.Where(f => !save.Bool("MiniGame_FrogTenors_" + f.flag)).Select(f => f.song));
        var skullKid = save is null ? TipStatus.Unknown
            : ((save.Bool("Npc_SkullKid_Child_IsGetHeartPiece") ? 1 : 0) + (save.Bool("Minigame_SkullKidSession_IsGetHeartPiece") ? 1 : 0)) switch
            {
                2 => TipStatus.Done,
                1 => TipStatus.Partly,
                _ => TipStatus.NotYet,
            };

        return
        [
            new("Learn Epona's Song from Malon", "Lon Lon Ranch",
                "Adult Link wins Epona from Ingo with this song; only young Link can learn it.",
                ["Hyrule Castle"], Flag("IsGet.Item_Melody_EponaSong"), ""),
            new("Plant the magic beans", "Buy them from the bean seller in Zora River (each one costs more), then plant them in soft soil",
                "Only young Link can plant them; seven years later they're platforms adult Link rides to heart pieces and Gold Skulltulas.",
                ["Magic Beans", "Jabu-Jabu's Belly", "The Beginning"],
                beans < 0 ? TipStatus.Unknown : beans == BeanSoils.Length ? TipStatus.Done : beans > 0 ? TipStatus.Partly : TipStatus.NotYet,
                beans < 0 ? "" : $"{beans} of {BeanSoils.Length} spots planted"),
            new("Teach Bonooru a song", "Lake Hylia, the scarecrow next to Pierre",
                "Play him a tune of your own as young Link; adult Link plays it back to call Pierre to hookshot spots.",
                ["Ocarina Songs", "Jabu-Jabu's Belly"],
                save is null ? TipStatus.Unknown
                    : save.Bool("BlowingOcarinaForBonooru") || Step("SubEvent_GetScarecrow", "IntoThePast", "PlayScarecrowSong", "IntoTheFuture", "Complete") == TipStatus.Done
                        ? TipStatus.Done : TipStatus.NotYet, ""),
            new("Finish the Happy Mask Shop chain", "Hyrule Castle Town",
                "Keaton Mask, Skull Mask, Spooky Mask, Bunny Hood, then the Mask of Truth. Castle Town is in ruins once you're adult.",
                ["The Happy Mask Shop", "Hyrule Castle"], Step("SubEvent_MaskSalesman", "Complete"), ""),
            new("Bring Richard the dog home", "Hyrule Castle Town's back alley, at night",
                "His owner gives you a heart piece. Castle Town only exists for young Link.",
                ["Hyrule Castle", "Heart Containers"], Flag("SubEvent_FindRichard_GetHeartPiece"), ""),
            new("Play Bombchu Bowling", "Hyrule Castle Town",
                "Prizes include a heart piece and a bigger bomb bag. Young Link only (Castle Town).",
                ["Hyrule Castle", "Minigames"], TipStatus.Unknown, "Zest can't see the prizes in the save; check your bomb bag."),
            new("Win the Treasure-Chest Shop", "Hyrule Castle Town, at night",
                "The last room holds a heart piece. Young Link only (Castle Town).",
                ["Hyrule Castle", "Minigames"], TipStatus.Unknown, "Zest can't see this one in the save."),
            new("Dig up Dampé's heart piece", "Kakariko Graveyard, the grave-digging tour (young Link, daytime)",
                "One of the holes Dampé digs drops a heart piece; it can take a few tries.",
                ["Kakariko Village", "Heart Containers"], Flag("GraveYard_DiggingHoleTour_Drop_HeartPiece"), ""),
            new("Get the Silver Scale", "Zora Domain, the diving game at the top of the waterfall",
                "Lets you dive deeper; the Golden Scale comes later from the Lake Hylia fishing pond.",
                ["Jabu-Jabu's Belly"], Flag("IsGet.Item_Scale_Silver"), ""),
            new("Skull Kid's heart pieces", "Lost Woods: play Saria's Song for Skull Kid, then his ocarina game",
                "Two heart pieces. Adult Link finds the Skull Kid gone.",
                ["The Lost Woods"], skullKid, ""),
            new("Sing for the Zora River frogs", "Zora River, the log in the stream",
                "One reward per song. Song of Time and Song of Storms come later, so come back as young Link after you have them.",
                ["Jabu-Jabu's Belly", "Ocarina Songs"],
                frogs < 0 ? TipStatus.Unknown : frogs == FrogSongs.Length ? TipStatus.Done : frogs > 0 ? TipStatus.Partly : TipStatus.NotYet,
                frogs < 0 ? "" : frogs == FrogSongs.Length ? "all six songs" : $"{frogs} of 6 songs; still to play: {missingFrogs}"),
            new("Meet the Great Fairy at Zora Fountain", "Zora Fountain, behind a bombable wall (after Jabu-Jabu)",
                "Farore's Wind. Easiest while you're there for Jabu-Jabu.",
                ["Jabu-Jabu's Belly", "Fairy Fountains"], Flag("IsGet.Item_FaroreWind"), ""),
        ];
    }

    public static string Mark(TipStatus s) => s switch
    {
        TipStatus.Done => "✓",
        TipStatus.Partly => "◐",
        TipStatus.NotYet => "○",
        _ => "?",
    };
}
