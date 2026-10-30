using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using PKForge.Domain;
using PKForge.Engine;

namespace Zest.ViewModels;

/// <summary>One entry in a move picker: its type pill, and how this Pokémon can learn it ("Lv 24", "TM"…).</summary>
public sealed record MoveOption(int Id, string Name, string TypeName, IBrush TypeBrush, string LearnLabel, bool IsLearnable, string Facts)
{
    public bool HasType => TypeName.Length > 0;

    /// <summary>Dark text on the light pills (Electric, Ice, Ground…), white on the rest.</summary>
    public IBrush TypeForeground => TypeBrush is ISolidColorBrush { Color: var c } && 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 150
        ? Brushes.Black : Brushes.White;
    public double Dim => IsLearnable || Id == 0 ? 1 : 0.5;
    public override string ToString() => Name;
}

/// <summary>Move pickers sorted for the selected Pokémon: learnable moves first, then the rest dimmed.</summary>
public partial class ForgeViewModel
{
    // PKHeX's type ids (Normal = 0 … Fairy = 17) and the usual type colours.
    private static readonly string[] TypeNames =
    [
        "Normal", "Fighting", "Flying", "Poison", "Ground", "Rock", "Bug", "Ghost", "Steel",
        "Fire", "Water", "Grass", "Electric", "Psychic", "Ice", "Dragon", "Dark", "Fairy",
    ];

    private static readonly IBrush[] TypeBrushes = [.. new[]
    {
        "#A8A77A", "#C22E28", "#A98FF3", "#A33EA1", "#E2BF65", "#B6A136", "#A6B91A", "#735797", "#B7B7CE",
        "#EE8130", "#6390F0", "#7AC74C", "#F7D02C", "#F95587", "#96D9D6", "#6F35FC", "#705746", "#D685AD",
    }.Select(c => (IBrush)new ImmutableSolidColorBrush(Color.Parse(c)))];

    private static readonly MoveOption NoMove = new(0, "(No move)", "", Brushes.Transparent, "", true, "");

    private readonly MonInfoService _monInfo = new();

    [ObservableProperty]
    public partial IReadOnlyList<MoveOption> MoveOptions { get; set; } = [];

    [ObservableProperty]
    public partial string MoveHint { get; set; } = "";

    // A ⚠ beside a picker whose move this Pokémon can't learn: usually the reason it isn't legal.
    public bool Move1Unlearnable => EditMove1 is { IsLearnable: false };
    public bool Move2Unlearnable => EditMove2 is { IsLearnable: false };
    public bool Move3Unlearnable => EditMove3 is { IsLearnable: false };
    public bool Move4Unlearnable => EditMove4 is { IsLearnable: false };

    partial void OnEditMove1Changed(MoveOption? value) => OnPropertyChanged(nameof(Move1Unlearnable));
    partial void OnEditMove2Changed(MoveOption? value) => OnPropertyChanged(nameof(Move2Unlearnable));
    partial void OnEditMove3Changed(MoveOption? value) => OnPropertyChanged(nameof(Move3Unlearnable));
    partial void OnEditMove4Changed(MoveOption? value) => OnPropertyChanged(nameof(Move4Unlearnable));

    /// <summary>Rebuilds the move list for the Pokémon in (box, slot), then selects its four moves.</summary>
    private void LoadMoves(ISaveEngineSession session, EntityDetail detail)
    {
        IReadOnlyList<MoveChoice> choices;
        try
        {
            choices = _monInfo.GetMoveChoices(session, detail.Box, detail.Slot);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            choices = [];
        }

        List<MoveOption> options;
        if (choices.Count == 0)
        {
            // ROM-hack sessions have no learnability data: plain alphabetical list, nothing dimmed.
            options = [.. MoveNames.Select((name, id) => new MoveOption(id, name, "", Brushes.Transparent, "", true, ""))
                .Where(o => o.Id > 0 && o.Name.Length > 0).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)];
            MoveHint = "";
        }
        else
        {
            options = [.. choices
                .Select(c => new MoveOption(c.Id, MoveName(c.Id),
                    c.Type is >= 0 and < 18 ? TypeNames[c.Type] : "", c.Type is >= 0 and < 18 ? TypeBrushes[c.Type] : Brushes.Gray,
                    c.Learn.IsLegal ? c.Learn.Label : "", c.Learn.IsLegal, Facts(c)))
                .OrderBy(o => o.IsLearnable ? 0 : 1)
                .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)];
            MoveHint = $"The {options.Count(o => o.IsLearnable)} moves this Pokémon can learn are listed first.";
        }
        options.Insert(0, NoMove);

        // Keep the Pokémon's current moves even when the game's list wouldn't offer them.
        foreach (var id in new[] { detail.Move1, detail.Move2, detail.Move3, detail.Move4 })
            if (id > 0 && options.All(o => o.Id != id))
                options.Add(new MoveOption(id, MoveName(id), "", Brushes.Gray, "", false, ""));

        // New list first, then the picks: swapping the list clears the pickers' selection.
        MoveOptions = options;
        EditMove1 = Pick(options, detail.Move1);
        EditMove2 = Pick(options, detail.Move2);
        EditMove3 = Pick(options, detail.Move3);
        EditMove4 = Pick(options, detail.Move4);
    }

    /// <summary>The move to write for one slot, or null to leave it (an empty picker never wipes a move).</summary>
    private static int? MoveEdit(MoveOption? picked, int current) => picked is { } p && p.Id != current ? p.Id : null;

    private static MoveOption Pick(IReadOnlyList<MoveOption> options, int id) => options.FirstOrDefault(o => o.Id == id) ?? NoMove;

    private string MoveName(int id) => id < MoveNames.Count && MoveNames[id].Length > 0 ? MoveNames[id] : $"Move {id}";

    private static string Facts(MoveChoice c)
    {
        var parts = new List<string> { c.Category.ToString() };
        if (c.Power > 0) parts.Add($"{c.Power} power");
        if (c.Accuracy > 0) parts.Add($"{c.Accuracy}% accuracy");
        if (c.PP > 0) parts.Add($"{c.PP} PP");
        return string.Join(" · ", parts) + (c.Effect.Length > 0 ? $"\n{c.Effect}" : "");
    }
}
