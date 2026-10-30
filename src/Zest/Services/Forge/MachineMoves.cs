using System.Text.RegularExpressions;
using PKHeX.Core;

namespace Zest.Services.Forge;

/// <summary>
/// Which move each TM / HM / TR teaches in a given game, from PKHeX's machine tables, so the bag
/// can say "TM40 - Aerial Ace" instead of just "TM40". Tables list TM1… in order, then the HMs.
/// </summary>
public static partial class MachineMoves
{
    private sealed record Table(ushort[] Technical, int FirstNumber = 1, ushort[]? Hidden = null, ushort[]? Records = null);

    private static readonly Dictionary<string, Table> Games = Build();

    /// <summary>"TM40 - Aerial Ace" (Scarlet/Violet's three-digit "TM001 - Take Down"), or the name unchanged when it isn't a machine.</summary>
    public static string Label(string gameName, string itemName, IReadOnlyList<string> moveNames)
    {
        if (MoveFor(gameName, itemName) is not { } move || move >= moveNames.Count || MachineName().Match(itemName) is not { Success: true } m)
            return itemName;
        var number = int.Parse(m.Groups[2].Value);
        var digits = gameName is "Scarlet" or "Violet" ? 3 : 2;
        return $"{m.Groups[1].Value.ToUpperInvariant()}{number.ToString().PadLeft(digits, '0')} - {moveNames[move]}";
    }

    /// <summary>The move taught by <paramref name="itemName"/> ("TM40", "HM03", "TR12") in <paramref name="gameName"/>, or null.</summary>
    public static int? MoveFor(string gameName, string itemName)
    {
        if (!Games.TryGetValue(gameName, out var table) || MachineName().Match(itemName) is not { Success: true } m)
            return null;
        var number = int.Parse(m.Groups[2].Value);
        var (list, first) = m.Groups[1].Value.ToUpperInvariant() switch
        {
            "TM" => (table.Technical, table.FirstNumber),
            "HM" => (table.Hidden, 1),
            "TR" => (table.Records, 0),
            _ => (null, 0),
        };
        var index = number - first;
        return list is not null && index >= 0 && index < list.Length ? list[index] : null;
    }

    private static Dictionary<string, Table> Build()
    {
        // Gen 3-6 tables carry the HMs after the TMs; split them at the TM count.
        static Table Split(ReadOnlySpan<ushort> all, int tms) => new(all[..tms].ToArray(), Hidden: all[tms..].ToArray());

        var gen3 = new Table(PersonalInfo3.MachineMovesTechnical.ToArray(), Hidden: PersonalInfo3.MachineMovesHidden.ToArray());
        var dppt = new Table(PersonalInfo4.MachineMovesTechnical.ToArray(), Hidden: PersonalInfo4.MachineMovesHiddenDPPt.ToArray());
        var hgss = new Table(PersonalInfo4.MachineMovesTechnical.ToArray(), Hidden: PersonalInfo4.MachineMovesHiddenHGSS.ToArray());
        var gen5 = Split(PersonalInfo5BW.MachineMoves, 95);
        var xy = Split(LearnSource6XY.MachineMoves, 100);
        var oras = Split(LearnSource6AO.MachineMoves, 100);
        var gen7 = new Table(PersonalInfo7.MachineMoves.ToArray());
        var lgpe = new Table(LearnSource7GG.MachineMoves.ToArray());
        var swsh = new Table(PersonalInfo8SWSH.MachineMovesTechnical.ToArray(), FirstNumber: 0, Records: PersonalInfo8SWSH.MachineMovesRecord.ToArray());
        var bdsp = new Table(PersonalInfo8BDSP.MachineMoves.ToArray());
        // Scarlet/Violet's table is indexed by TM number itself (slot 0 is unused): TM001 = slot 1.
        var sv = new Table(PersonalInfo9SV.MachineMoves.ToArray(), FirstNumber: 0);

        var map = new Dictionary<string, Table>(StringComparer.OrdinalIgnoreCase);
        void Add(Table t, params string[] names)
        {
            foreach (var n in names)
                map[n] = t;
        }
        Add(gen3, "Ruby", "Sapphire", "Emerald", "FireRed", "LeafGreen");
        Add(dppt, "Diamond", "Pearl", "Platinum");
        Add(hgss, "HeartGold", "SoulSilver");
        Add(gen5, "Black", "White", "Black 2", "White 2");
        Add(xy, "X", "Y");
        Add(oras, "Omega Ruby", "Alpha Sapphire");
        Add(gen7, "Sun", "Moon", "Ultra Sun", "Ultra Moon");
        Add(lgpe, "Let's Go Pikachu", "Let's Go Eevee");
        Add(swsh, "Sword", "Shield");
        Add(bdsp, "Brilliant Diamond", "Shining Pearl");
        Add(sv, "Scarlet", "Violet");
        // Legends: Z-A renumbers its TMs (ItemConverter.RemapTechnicalMachineItemName9a); left out until verified.
        return map;
    }

    [GeneratedRegex(@"^(TM|HM|TR)\s*0*(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MachineName();
}
