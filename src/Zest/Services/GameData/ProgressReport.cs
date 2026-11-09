namespace Zest.Services.GameData;

/// <summary>A row of chips in the tracker: badges, songs, the party…</summary>
public sealed record ProgressGroup(string Title, IReadOnlyList<Collectible> Items);

/// <summary>One save the Adventure Log can show; newest first in the picker.</summary>
public sealed record ProgressSlot(string Id, string Source, string Label, DateTime Saved);

/// <summary>What the Adventure Log shows for one save, whichever game it comes from.</summary>
public sealed record ProgressReport(
    string Where,
    string Stats,
    string Counts,
    byte[]? Picture,
    IReadOnlyList<QuestProgress> Quests,
    IReadOnlyList<ProgressGroup> Groups,
    IReadOnlyList<DungeonProgress> Dungeons,
    string Footer);

/// <summary>A game whose saves Zest can read progress from (Ocarina of Time on Switch 2, the Pokémon games).</summary>
public interface IProgressReader
{
    /// <summary>Loads what the reader needs once (game text…). Returns why the log can't be shown, or null.</summary>
    string? Prepare();

    /// <summary>Folders to watch: a save written there refreshes the log.</summary>
    IReadOnlyList<string> WatchFolders();

    IReadOnlyList<ProgressSlot> Slots();

    ProgressReport Read(ProgressSlot slot);

    /// <summary>Said when no save is found.</summary>
    string NoSaves { get; }
}
