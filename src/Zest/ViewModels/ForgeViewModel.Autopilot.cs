using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.Domain;
using PKForge.Engine;
using Zest.Services.Forge;

namespace Zest.ViewModels;

/// <summary>A save (or the Bank) on the Autopilot shelf, with the user's include switch.</summary>
public partial class ShelfEntry : ObservableObject
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public PokemonSave? Save { get; init; }

    [ObservableProperty]
    public partial bool Include { get; set; } = true;

    public override string ToString() => Label;
}

/// <summary>
/// Forge's Living Dex Autopilot (ported from PKForge): plans moves (never copies) from every
/// included save and the Bank into one destination, evolving trade evolutions on the way,
/// then stages the whole run and writes it all-or-nothing, with a restore point per save.
/// </summary>
public partial class ForgeViewModel
{
    private static readonly string AutopilotFile = Path.Combine(ForgeHost.Root, "autopilot.json");
    private LivingDexPlan? _plan;
    private Dictionary<string, DetectedSave> _shelfSaves = [];
    private bool _confirmApply;

    public ObservableCollection<ShelfEntry> Shelf { get; } = [];
    public ObservableCollection<string> PlanLines { get; } = [];

    [ObservableProperty]
    public partial ShelfEntry? Destination { get; set; }

    [ObservableProperty]
    public partial bool DexShiny { get; set; }

    [ObservableProperty]
    public partial bool DexForms { get; set; }

    [ObservableProperty]
    public partial bool DexIncludeParty { get; set; }

    [ObservableProperty]
    public partial bool DexAllowLastCopies { get; set; }

    [ObservableProperty]
    public partial bool DexAllowDowngrades { get; set; } = true;

    [ObservableProperty]
    public partial string PlanSummary { get; set; } = "Pick where the living dex should live, then make a plan. Nothing is written until you apply it.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApply))]
    public partial bool HasPlan { get; set; }

    [ObservableProperty]
    public partial string ApplyText { get; set; } = "Apply plan";

    public bool CanApply => HasPlan && _plan is { HasWork: true };

    public bool IsAutopilotTab => Tab == ForgeTab.Autopilot;

    // Any option change makes the plan on screen stale.
    partial void OnDestinationChanged(ShelfEntry? value) => DropPlan();
    partial void OnDexShinyChanged(bool value) => DropPlan();
    partial void OnDexFormsChanged(bool value) => DropPlan();
    partial void OnDexIncludePartyChanged(bool value) => DropPlan();
    partial void OnDexAllowLastCopiesChanged(bool value) => DropPlan();
    partial void OnDexAllowDowngradesChanged(bool value) => DropPlan();

    private void DropPlan()
    {
        _plan = null;
        HasPlan = false;
        _confirmApply = false;
        ApplyText = "Apply plan";
        PlanLines.Clear();
    }

    private void LoadShelf()
    {
        var keep = Shelf.ToDictionary(e => e.Id, e => e.Include);
        var destination = Destination?.Id;
        Shelf.Clear();
        foreach (var save in Saves)
            Shelf.Add(new ShelfEntry { Id = save.Path, Label = save.Label, Save = save, Include = keep.GetValueOrDefault(save.Path, true) });
        Shelf.Add(new ShelfEntry { Id = LivingDexPlanner.BankId, Label = "Bank" });
        foreach (var entry in Shelf)
            entry.PropertyChanged += (_, _) => DropPlan();
        Destination = Shelf.FirstOrDefault(e => e.Id == destination)
            ?? Shelf.FirstOrDefault(e => e.Save?.Path == CurrentSave?.Path)
            ?? Shelf.Last();
    }

    [RelayCommand]
    private async Task MakePlanAsync()
    {
        if (Destination is not { } destination)
            return;
        IsBusy = true;
        PlanSummary = "Reading every save…";
        DropPlan();
        try
        {
            var entries = Shelf.ToList();
            var bankStart = LoadBankStart();
            var options = new LivingDexOptions(destination.Id, DexShiny, DexForms, DexAllowLastCopies, DexIncludeParty,
                DexAllowDowngrades, bankStart);
            var (plan, saves) = await Task.Run(() =>
            {
                var catalog = LivingDexCatalogBuilder.Build();
                var sources = new List<LivingDexSource>();
                var detected = new Dictionary<string, DetectedSave>(StringComparer.Ordinal);
                foreach (var entry in entries.Where(e => e.Save is not null))
                {
                    var save = entry.Save!;
                    var label = save.GameName;
                    detected[save.Path] = new DetectedSave(save.Path, Path.GetFileName(save.Path), save.Label,
                        save.Platform == Zest.Models.Platform.Switch ? EmulatorKind.Eden : EmulatorKind.Azahar,
                        RequiresExtraCare: false, new DateTimeOffset(save.Modified), save.Description.Generation,
                        save.Description.TrainerName, save.Description.PlayTime);
                    if (!entry.Include && entry.Id != destination.Id)
                    {
                        sources.Add(new LivingDexSource(save.Path, label, LivingDexSourceKind.Save, save.Description.Generation, 0, [],
                            LivingDexExclusion.Hidden, "Left out by you"));
                        continue;
                    }
                    try
                    {
                        var bytes = File.ReadAllBytes(save.Path);
                        using var session = _forge.Engine.OpenSession(bytes, save.Label);
                        var (exclusion, reason) = Judge(save.Path, session);
                        sources.Add(LivingDexSources.FromSession(save.Path, label, session, bytes, exclusion, reason));
                    }
                    catch (Exception error)
                    {
                        sources.Add(new LivingDexSource(save.Path, label, LivingDexSourceKind.Save, save.Description.Generation, 0, [],
                            LivingDexExclusion.Unreadable, $"Could not be read: {error.Message}"));
                    }
                }
                if (destination.Id == LivingDexPlanner.BankId || entries.First(e => e.Id == LivingDexPlanner.BankId).Include)
                    sources.Add(LivingDexSources.FromBank(_forge.Bank));
                return (LivingDexPlanner.Plan(catalog, sources, options), detected);
            });

            _plan = plan;
            _shelfSaves = saves;
            HasPlan = true;
            ShowPlan(plan);
        }
        catch (Exception error)
        {
            PlanSummary = $"Couldn't make a plan: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>PKForge's rules for saves the autopilot must not touch.</summary>
    private (LivingDexExclusion, string?) Judge(string path, ISaveEngineSession session)
    {
        if (session is not SaveEngineSession || _forge.Writer.LayoutRiskOf(path, session.Snapshot) is not null
            || session.GameNames.Any(n => n.Contains("Luminescent", StringComparison.OrdinalIgnoreCase) || n.Contains("Compass", StringComparison.OrdinalIgnoreCase)))
            return (LivingDexExclusion.RomHack, null);
        if (_forge.Writer.WhyWritesAreRefused(path, session.Snapshot) is { } refusal)
            return (LivingDexExclusion.ReadOnly, refusal);
        if (!session.SupportsBoxTools)
            return (LivingDexExclusion.Unsupported, null);
        return (LivingDexExclusion.None, null);
    }

    private void ShowPlan(LivingDexPlan plan)
    {
        var parts = new List<string>
        {
            $"{plan.DestinationLabel}: {plan.CoveredBefore} of {plan.TargetCount} now, {plan.CoveredAfter} after this plan.",
        };
        if (plan.Moves > 0) parts.Add($"{plan.Moves} to move");
        if (plan.Evolutions > 0) parts.Add($"{plan.Evolutions} to evolve on the way");
        if (plan.Arranges > 0) parts.Add($"{plan.Arranges} to put in order");
        if (plan.Downgrades > 0) parts.Add($"{plan.Downgrades} go to an older game (some data is lost)");
        if (plan.BlockedCount > 0) parts.Add($"{plan.BlockedCount} blocked");
        if (plan.Guides > 0) parts.Add($"{plan.Guides} not covered");
        PlanSummary = string.Join(" · ", parts) + (plan.HasWork ? "" : "\nNothing to do with these settings.");

        var labels = plan.Sources.ToDictionary(s => s.Id, s => s.Label);
        foreach (var skipped in plan.Skipped)
            PlanLines.Add($"Left out: {skipped.Label}: {skipped.Reason}");
        foreach (var step in plan.Steps.Where(s => s.Kind != LivingDexStepKind.Guide).Take(600))
            PlanLines.Add(StepLine(step, labels));
        // Guide steps are everything the plan can't fill: owned but held back, or not owned at all.
        var owned = plan.Sources.Where(s => !s.IsExcluded).SelectMany(s => s.Holdings).Where(h => !h.IsEgg)
            .Select(h => h.Species).ToHashSet();
        var guides = plan.Steps.Where(s => s.Kind == LivingDexStepKind.Guide).ToList();
        var keptBack = guides.Where(s => owned.Contains(s.Species)).ToList();
        var uncaught = guides.Where(s => !owned.Contains(s.Species)).ToList();
        if (keptBack.Count > 0)
            PlanLines.Add($"Kept back ({keptBack.Count}), each is a game's only copy or in a party. Turn on \"May take a game's last copy\" or \"May take from parties\" to include them: "
                + List(keptBack));
        if (uncaught.Count > 0)
            PlanLines.Add($"Not caught in any included save ({uncaught.Count}): " + List(uncaught));
    }

    private string List(IReadOnlyList<LivingDexStep> steps) =>
        string.Join(", ", steps.Take(40).Select(s => Subject(s.Species, s.Form, s.Shiny))) + (steps.Count > 40 ? ", …" : "");

    private string StepLine(LivingDexStep step, IReadOnlyDictionary<string, string> labels)
    {
        var who = Subject(step.Species, step.Form, step.Shiny);
        var from = step.SourceId is { } id ? labels.GetValueOrDefault(id, "?") : "?";
        var to = step.Destination is { } d ? $"box {d.Box + 1}, slot {d.Slot + 1}" : "";
        var line = step.Kind switch
        {
            LivingDexStepKind.EvolveAndMove => $"#{step.Ordinal} {SpeciesName(step.FromSpecies)} → {who} (evolves): {from} → {to}",
            LivingDexStepKind.Arrange => $"#{step.Ordinal} {who}: reorder to {to}",
            _ => $"#{step.Ordinal} {who}: {from} → {to}",
        };
        var notes = new List<string>();
        if (step.Warnings.HasFlag(LivingDexWarnings.Downgrade)) notes.Add("older game");
        if (step.Warnings.HasFlag(LivingDexWarnings.IllegalSource)) notes.Add("not legal now");
        if (step.Warnings.HasFlag(LivingDexWarnings.FromParty)) notes.Add("from party");
        if (step.Warnings.HasFlag(LivingDexWarnings.LastCopy)) notes.Add("last copy");
        if (step.Blocked is { } why) notes.Add($"blocked: {why}");
        return notes.Count == 0 ? line : $"{line} ({string.Join(", ", notes)})";
    }

    private string Subject(int species, int form, bool shiny)
    {
        var name = SpeciesName(species);
        if (form > 0 && LivingDexCatalogBuilder.FormName(species, form) is { Length: > 0 } formName)
            name += $" ({formName})";
        return shiny ? $"{name} ★" : name;
    }

    [RelayCommand]
    private async Task ApplyPlanAsync()
    {
        if (_plan is not { HasWork: true } plan)
            return;
        if (WriteBlocker() is { } blocked)
        {
            Status = blocked;
            return;
        }
        if (!_confirmApply)
        {
            _confirmApply = true;
            ApplyText = $"Tap again to apply to {plan.TouchedSaves.Count + (plan.OutgoingBySource.ContainsKey(LivingDexPlanner.BankId) || plan.DestinationKind == LivingDexSourceKind.Bank ? 1 : 0)} place(s)";
            return;
        }

        IsBusy = true;
        ApplyText = "Applying…";
        try
        {
            // The open save gets written by the run; drop the session first so nothing stale is kept.
            var reopen = CurrentSave;
            _forge.Sessions.Close();
            var executor = new LivingDexExecutor(_forge.Engine, _forge.Writer, _forge.Access, _forge.Bank,
                new EvolutionService(), _forge.Legality, _forge.Sessions);
            var progress = new Progress<LivingDexProgress>(p => PlanSummary = p.Message);
            var result = await Task.Run(() => executor.ApplyAsync(plan, _shelfSaves, progress));
            if (result.Committed && plan.DestinationKind == LivingDexSourceKind.Bank)
                SaveBankStart(plan.BankStartBox);

            DropPlan();
            PlanSummary = result.Summary;
            foreach (var failed in result.Outcomes.Where(o => o.Status == LivingDexStepStatus.Failed).Take(50))
                PlanLines.Add($"Failed: {Subject(failed.Step.Species, failed.Step.Form, failed.Step.Shiny)}: {failed.Message}");
            Status = result.Committed ? "Living dex updated. Every changed save has a restore point." : result.RolledBack
                ? "Nothing was changed: the run was rolled back." : result.Summary;

            if (reopen is not null)
                await OpenAsync(reopen);
            RefreshBank();
        }
        catch (Exception error)
        {
            PlanSummary = $"The autopilot stopped: {error.Message}";
            if (CurrentSave is { } save)
                await OpenAsync(save);
        }
        finally
        {
            IsBusy = false;
            _confirmApply = false;
            ApplyText = "Apply plan";
        }
    }

    private static int? LoadBankStart()
    {
        try
        {
            return File.Exists(AutopilotFile) ? JsonSerializer.Deserialize<AutopilotState>(File.ReadAllText(AutopilotFile))?.BankStartBox : null;
        }
        catch (Exception error) when (error is IOException or JsonException)
        {
            return null;
        }
    }

    private static void SaveBankStart(int box) =>
        File.WriteAllText(AutopilotFile, JsonSerializer.Serialize(new AutopilotState(box)));

    private sealed record AutopilotState(int? BankStartBox);
}
