using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PKForge.Domain;
using PKForge.Engine;
using Zest.Services.Forge;

namespace Zest.ViewModels;

public enum ForgeTab { Save, Trainer, Bag, Gifts, Autopilot, Bank, Backups }

/// <summary>One box or party slot, drawn as a text tile (name, level marks, legality).</summary>
public partial class SlotTile : ObservableObject
{
    public required int Box { get; init; }
    public required int Slot { get; init; }
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public bool IsEmpty { get; init; }
    public bool IsShiny { get; init; }
    public bool IsLegal { get; init; } = true;
    public bool IsEgg { get; init; }

    /// <summary>Bank entry id, for tiles on the Bank tab.</summary>
    public Guid? BankId { get; init; }

    public bool ShowIllegal => !IsEmpty && !IsLegal;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>An editable IV/EV row in the stats grid.</summary>
public partial class StatRow : ObservableObject
{
    public required string Name { get; init; }

    [ObservableProperty]
    public partial decimal Iv { get; set; }

    [ObservableProperty]
    public partial decimal Ev { get; set; }
}

public sealed record AbilityChoice(int Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record BackupRow(BackupInfo Info)
{
    public string When => Info.CreatedUtc.ToLocalTime().ToString("MMM d, HH:mm");
    public string What => Info.ChangeDescription ?? "Restore point";
}

/// <summary>
/// The Forge page: PKForge's save editor and Bank inside Zest. Every write goes through
/// PKForge's SafeSaveWriter: validated, scoped to the slots it means to touch, and preceded
/// by a restore point.
/// </summary>
public partial class ForgeViewModel : ViewModelBase
{
    private static readonly string[] StatNames = ["HP", "Attack", "Defense", "Sp. Atk", "Sp. Def", "Speed"];

    private readonly ForgeHost _forge = ForgeHost.Shared;
    private readonly Func<bool> _gameRunning;
    private int _boxCount;
    private DateTime _openedStamp;
    private Guid? _pendingWithdraw;

    public ForgeViewModel(Func<bool> gameRunning)
    {
        _gameRunning = gameRunning;
        NatureNames = _forge.Data.NatureNames;
        BallNames = _forge.Data.BallNames;
        MoveNames = _forge.Data.MoveNames;
        ItemNames = _forge.Data.ItemNames;
        foreach (var name in StatNames)
            Stats.Add(new StatRow { Name = name });
    }

    public ObservableCollection<PokemonSave> Saves { get; } = [];
    public ObservableCollection<SlotTile> Party { get; } = [];
    public ObservableCollection<SlotTile> BoxSlots { get; } = [];
    public ObservableCollection<SlotTile> BankSlots { get; } = [];
    public ObservableCollection<StatRow> Stats { get; } = [];
    public ObservableCollection<AbilityChoice> Abilities { get; } = [];
    public ObservableCollection<string> LegalityLines { get; } = [];
    public ObservableCollection<BackupRow> RestorePoints { get; } = [];

    public IReadOnlyList<string> NatureNames { get; }
    public IReadOnlyList<string> BallNames { get; }
    public IReadOnlyList<string> MoveNames { get; }

    public IReadOnlyList<string> ItemNames { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSaveTab), nameof(IsBankTab), nameof(IsBackupsTab), nameof(IsTrainerTab), nameof(IsBagTab), nameof(IsGiftsTab), nameof(IsDataTab), nameof(IsAutopilotTab))]
    public partial ForgeTab Tab { get; set; }

    public bool IsSaveTab => Tab == ForgeTab.Save;
    public bool IsTrainerTab => Tab == ForgeTab.Trainer;
    public bool IsBagTab => Tab == ForgeTab.Bag;
    public bool IsGiftsTab => Tab == ForgeTab.Gifts;

    /// <summary>Trainer, Bag and Gifts use one wide panel instead of boxes + editor.</summary>
    public bool IsDataTab => Tab is ForgeTab.Trainer or ForgeTab.Bag or ForgeTab.Gifts or ForgeTab.Autopilot;
    public bool IsBankTab => Tab == ForgeTab.Bank;
    public bool IsBackupsTab => Tab == ForgeTab.Backups;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSave), nameof(SaveTitle), nameof(SaveFacts), nameof(WithdrawText))]
    public partial PokemonSave? CurrentSave { get; set; }

    public bool HasSave => CurrentSave is not null;
    public string SaveTitle => CurrentSave?.Label ?? "No save open";
    public string SaveFacts => CurrentSave is { Description: var d }
        ? $"{d.TrainerName} · {d.PlayTime.Replace('ː', ':')} played · Gen {d.Generation} · {CurrentSave.Platform switch { Zest.Models.Platform.Switch => "Eden", _ => "Azahar" }}"
        : "";

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoxTitle))]
    public partial int BoxIndex { get; set; }

    public string BoxTitle { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BankBoxTitle))]
    public partial int BankBoxIndex { get; set; }

    public string BankBoxTitle => $"Bank box {BankBoxIndex + 1} of {Math.Max(1, _forge.Bank.BoxCount)}";

    // ---- Selected Pokémon (Save tab) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial EntityDetail? Selected { get; set; }

    public bool HasSelection => Selected is { IsEmpty: false };

    [ObservableProperty]
    public partial string SelectedHeader { get; set; } = "";

    [ObservableProperty]
    public partial bool SelectedIsLegal { get; set; }

    [ObservableProperty]
    public partial string EditNickname { get; set; } = "";

    [ObservableProperty]
    public partial decimal EditLevel { get; set; }

    [ObservableProperty]
    public partial int EditNature { get; set; }

    [ObservableProperty]
    public partial AbilityChoice? EditAbility { get; set; }

    [ObservableProperty]
    public partial int EditItem { get; set; }

    [ObservableProperty]
    public partial int EditBall { get; set; }

    [ObservableProperty]
    public partial MoveOption? EditMove1 { get; set; }

    [ObservableProperty]
    public partial MoveOption? EditMove2 { get; set; }

    [ObservableProperty]
    public partial MoveOption? EditMove3 { get; set; }

    [ObservableProperty]
    public partial MoveOption? EditMove4 { get; set; }

    [ObservableProperty]
    public partial bool EditShiny { get; set; }

    [ObservableProperty]
    public partial string EditOt { get; set; } = "";

    [ObservableProperty]
    public partial decimal EditFriendship { get; set; }

    [ObservableProperty]
    public partial decimal IvMax { get; set; } = 31;

    [ObservableProperty]
    public partial decimal EvMax { get; set; } = 252;

    // ---- Selected bank entry ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBankSelection), nameof(BankSelectedText))]
    public partial BankEntry? BankSelected { get; set; }

    public bool HasBankSelection => BankSelected is not null;

    public string BankSelectedText => BankSelected is { Info: var i }
        ? $"Lv. {i.Level} · Gen {i.Generation}{(i.Shiny ? " · Shiny" : "")} · from {i.SourceName}"
        : "";

    public string WithdrawText => _pendingWithdraw is not null
        ? "Confirm move"
        : CurrentSave is null ? "Open a save to move it" : $"Move to {CurrentSave.GameName}";

    [ObservableProperty]
    public partial string WithdrawPreview { get; set; } = "";

    // =====================================================================
    // Opening
    // =====================================================================

    /// <summary>Rescans Eden/Azahar and opens <paramref name="save"/> (or keeps the current one).</summary>
    public async Task LoadAsync(PokemonSave? save, ForgeTab tab)
    {
        Tab = tab;
        var found = await Task.Run(() => SaveLocator.Scan(_forge.Engine));
        Saves.Clear();
        foreach (var s in found)
            Saves.Add(s);

        var target = save is null ? null : Saves.FirstOrDefault(s => s.Key == save.Key) ?? save;
        if (target is not null && target.Path != CurrentSave?.Path)
            await OpenAsync(target);
        else if (CurrentSave is not null)
            await OpenAsync(CurrentSave);
        RefreshBank();
    }

    public async Task OpenAsync(PokemonSave save)
    {
        IsBusy = true;
        Status = $"Opening {save.Label}…";
        try
        {
            _forge.Sessions.Close();
            await _forge.Sessions.OpenAsync(new PickedDocument(save.Path, save.Label));
            _openedStamp = File.GetLastWriteTimeUtc(save.Path);
            CurrentSave = save;
            var caps = _forge.Sessions.CurrentSession!.GetTrainingCaps();
            IvMax = caps.IvMax;
            EvMax = caps.EvMax;
            _boxCount = _forge.Sessions.CurrentSession.Snapshot.Slots.Where(s => s.Box >= 0).Select(s => s.Box).DefaultIfEmpty(-1).Max() + 1;
            BoxIndex = Math.Clamp(BoxIndex, 0, Math.Max(0, _boxCount - 1));
            RefreshSlots();
            SelectSlot(Party.FirstOrDefault(t => !t.IsEmpty) ?? BoxSlots.FirstOrDefault());
            await RefreshBackupsAsync();
            RefreshSaveTabs(reloadGifts: true);
            Status = WriteBlocker() ?? "";
        }
        catch (Exception error)
        {
            CurrentSave = null;
            Status = $"Could not open {save.Label}: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        OnPropertyChanged(nameof(WithdrawText));
    }

    /// <summary>Controller face buttons cycle the tabs (the save-bound ones are skipped while no save is open).</summary>
    public void StepTab(int delta)
    {
        var tabs = Enum.GetValues<ForgeTab>().Where(t => HasSave || t is ForgeTab.Bank or ForgeTab.Autopilot).ToList();
        var next = tabs[(tabs.IndexOf(Tab) + delta + tabs.Count) % tabs.Count];
        SetTab(next.ToString());
    }

    [RelayCommand]
    private void SetTab(string tab)
    {
        Tab = Enum.Parse<ForgeTab>(tab);
        if (Tab == ForgeTab.Bank)
            RefreshBank();
        else if (Tab == ForgeTab.Backups)
            _ = RefreshBackupsAsync();
        else if (Tab == ForgeTab.Autopilot)
            LoadShelf();
        else
            RefreshSaveTabs();
    }

    // =====================================================================
    // Boxes
    // =====================================================================

    [RelayCommand]
    private void PrevBox() => ChangeBox(-1);

    [RelayCommand]
    private void NextBox() => ChangeBox(+1);

    public void ChangeBox(int delta)
    {
        if (Tab == ForgeTab.Bank)
        {
            var count = Math.Max(1, _forge.Bank.BoxCount);
            BankBoxIndex = (BankBoxIndex + delta + count) % count;
            RefreshBank();
            return;
        }
        if (_boxCount == 0)
            return;
        BoxIndex = (BoxIndex + delta + _boxCount) % _boxCount;
        RefreshSlots();
    }

    private void RefreshSlots()
    {
        // The engine session's snapshot is live; Sessions.Current keeps the baseline the writer diffs against.
        var session = _forge.Sessions.CurrentSession;
        var snapshot = session?.Snapshot;
        Party.Clear();
        BoxSlots.Clear();
        if (session is null || snapshot is null)
            return;

        var selected = Selected is { } s ? (s.Box, s.Slot) : (-2, -2);
        foreach (var slot in snapshot.Slots.Where(x => x.Box == -1).OrderBy(x => x.Slot))
            Party.Add(Tile(slot, selected));
        foreach (var slot in snapshot.Slots.Where(x => x.Box == BoxIndex).OrderBy(x => x.Slot))
            BoxSlots.Add(Tile(slot, selected));

        string name;
        try { name = session.GetBoxName(BoxIndex); }
        catch (Exception) { name = ""; }
        BoxTitle = string.IsNullOrWhiteSpace(name) ? $"Box {BoxIndex + 1}" : name;
        OnPropertyChanged(nameof(BoxTitle));
    }

    private SlotTile Tile(SlotSummary slot, (int Box, int Slot) selected)
    {
        var empty = slot.Species is not > 0;
        var species = empty ? "" : SpeciesName(slot.Species!.Value);
        var nick = slot.Nickname ?? "";
        return new SlotTile
        {
            Box = slot.Box,
            Slot = slot.Slot,
            IsEmpty = empty,
            Name = empty ? "" : slot.IsEgg ? "Egg" : nick.Length > 0 ? nick : species,
            Detail = empty || slot.IsEgg || nick.Length == 0 || nick == species ? "" : species,
            IsShiny = !empty && slot.IsShiny,
            IsLegal = slot.IsLegal,
            IsEgg = slot.IsEgg,
            IsSelected = (slot.Box, slot.Slot) == selected,
        };
    }

    private string SpeciesName(int species) =>
        species < _forge.Data.SpeciesNames.Count ? _forge.Data.SpeciesNames[species] : $"#{species}";

    [RelayCommand]
    private void SelectSlot(SlotTile? tile)
    {
        if (tile is null)
            return;
        if (tile.BankId is { } id)
        {
            foreach (var t in BankSlots)
                t.IsSelected = t == tile;
            BankSelected = _forge.Bank.GetAll().FirstOrDefault(e => e.Id == id);
            _pendingWithdraw = null;
            WithdrawPreview = "";
            OnPropertyChanged(nameof(WithdrawText));
            return;
        }

        foreach (var t in Party.Concat(BoxSlots))
            t.IsSelected = t.Box == tile.Box && t.Slot == tile.Slot;
        LoadSelected(tile.Box, tile.Slot);
    }

    private void LoadSelected(int box, int slot)
    {
        var session = _forge.Sessions.CurrentSession;
        if (session is null)
            return;
        var detail = session.ReadEntity(box, slot);
        Selected = detail;
        LegalityLines.Clear();
        if (detail.IsEmpty)
        {
            SelectedHeader = box < 0 ? $"Party slot {slot + 1} is empty" : $"{BoxTitle}, slot {slot + 1} is empty";
            return;
        }

        SelectedHeader = $"{SpeciesName(detail.Species)} · Lv. {detail.Level}" + (detail.IsShiny ? " · Shiny" : "");
        EditNickname = detail.Nickname;
        EditLevel = detail.Level;
        EditNature = detail.Nature;
        EditItem = detail.HeldItem;
        EditBall = detail.Ball;
        LoadMoves(session, detail);
        EditShiny = detail.IsShiny;
        EditOt = detail.OriginalTrainer;
        EditFriendship = detail.Friendship;
        for (var i = 0; i < 6; i++)
        {
            Stats[i].Iv = detail.IVs.ElementAtOrDefault(i);
            Stats[i].Ev = detail.EVs.ElementAtOrDefault(i);
        }

        Abilities.Clear();
        foreach (var id in session.GetAbilityChoices(detail.Species, detail.Form).Distinct())
            Abilities.Add(new AbilityChoice(id, id < _forge.Data.AbilityNames.Count ? _forge.Data.AbilityNames[id] : $"Ability {id}"));
        if (Abilities.All(a => a.Id != detail.Ability))
            Abilities.Add(new AbilityChoice(detail.Ability, _forge.Data.AbilityNames.ElementAtOrDefault(detail.Ability) ?? $"Ability {detail.Ability}"));
        EditAbility = Abilities.First(a => a.Id == detail.Ability);

        var report = _forge.Legality.Analyze(session, box, slot);
        SelectedIsLegal = report.Valid;
        foreach (var line in report.Lines)
            LegalityLines.Add(line);
        if (LegalityLines.Count == 0)
            LegalityLines.Add(report.Valid ? "Legal" : "Not legal");
    }

    // =====================================================================
    // Edits (every one goes through CommitAsync)
    // =====================================================================

    [RelayCommand]
    private async Task ApplyEditAsync()
    {
        if (Selected is not { IsEmpty: false } detail)
            return;
        var ivs = Stats.Select(s => (int)s.Iv).ToArray();
        var evs = Stats.Select(s => (int)s.Ev).ToArray();
        var edit = new EntityEdit(
            Nickname: EditNickname != detail.Nickname ? EditNickname : null,
            Level: (int)EditLevel != detail.Level ? (int)EditLevel : null,
            Nature: EditNature != detail.Nature ? EditNature : null,
            Ability: EditAbility is { } a && a.Id != detail.Ability ? a.Id : null,
            HeldItem: EditItem != detail.HeldItem ? EditItem : null,
            Move1: MoveEdit(EditMove1, detail.Move1),
            Move2: MoveEdit(EditMove2, detail.Move2),
            Move3: MoveEdit(EditMove3, detail.Move3),
            Move4: MoveEdit(EditMove4, detail.Move4),
            IVs: ivs.SequenceEqual(detail.IVs) ? null : ivs,
            EVs: evs.SequenceEqual(detail.EVs) ? null : evs,
            IsShiny: EditShiny != detail.IsShiny ? EditShiny : null,
            Ball: EditBall != detail.Ball ? EditBall : null,
            OriginalTrainer: EditOt != detail.OriginalTrainer && EditOt.Length > 0 ? EditOt : null,
            Friendship: (int)EditFriendship != detail.Friendship ? (int)EditFriendship : null);

        await CommitAsync(s =>
        {
            s.ApplyEdit(detail.Box, detail.Slot, edit);
            return new GenerationOutcome(true, "Saved.");
        }, [new SlotRef(detail.Box, detail.Slot)], $"Edit {SubjectName(detail)} ({SlotLabel(detail.Box, detail.Slot)})");
    }

    [RelayCommand]
    private void ResetEdit()
    {
        if (Selected is { } d)
            LoadSelected(d.Box, d.Slot);
    }

    [RelayCommand]
    private async Task LegalizeAsync()
    {
        if (Selected is not { IsEmpty: false } detail)
            return;
        await CommitAsync(s =>
            {
                try
                {
                    return _forge.Legalizer.LegalizeSlot(s, detail.Box, detail.Slot);
                }
                catch (ArgumentException)
                {
                    // Auto Legality can hand back another game's format for some event gifts (PKForge 3.1.1).
                    return new GenerationOutcome(false,
                        "Auto Legality couldn't rebuild this one for this game. Nothing was changed. If you just edited it, Undo edits may make it legal again.");
                }
            },
            [new SlotRef(detail.Box, detail.Slot)], $"Legalize {SubjectName(detail)} ({SlotLabel(detail.Box, detail.Slot)})");
    }

    [RelayCommand]
    private async Task MoveToBankAsync()
    {
        if (Selected is not { IsEmpty: false } detail || _forge.Sessions.CurrentSession is not { } session || CurrentSave is null)
            return;
        if (WriteBlocker() is { } blocked)
        {
            Status = blocked;
            return;
        }
        if (detail.Box < 0 && Party.Count(t => !t.IsEmpty) <= 1)
        {
            Status = "Your party needs at least one Pokémon.";
            return;
        }

        var export = session.ExportSlot(detail.Box, detail.Slot);
        if (_forge.Engine.TryDescribeEntity(export.Data, CurrentSave.Label, export.Format) is not { } info)
        {
            Status = $"{SubjectName(detail)} could not be read for the Bank.";
            return;
        }

        var name = SubjectName(detail);
        var outcome = await BankTransfers.DepositAsync(_forge.Bank, [new BankDeposit(export.Data, info)],
            async () => await CommitAsync(s =>
            {
                s.ReleaseSlot(detail.Box, detail.Slot);
                return new GenerationOutcome(true, $"{name} left for the Bank.");
            }, [new SlotRef(detail.Box, detail.Slot)], $"Move {name} to the Bank") ? null : Status);
        Status = outcome.Message;
        RefreshBank();
    }

    [RelayCommand]
    private async Task MoveFromBankAsync()
    {
        if (BankSelected is not { } entry)
            return;
        if (CurrentSave is null || _forge.Sessions.Current is not { } open || _forge.Sessions.CurrentSession is not { } session)
        {
            Status = "Open a save first: pick one from the list on the left.";
            return;
        }
        if (WriteBlocker() is { } blocked)
        {
            Status = blocked;
            return;
        }

        if (FirstEmptySlot(session) is not { } room)
        {
            Status = $"{CurrentSave.GameName} has no empty box slot.";
            return;
        }

        var bytes = _forge.Bank.GetData(entry.Id);
        var format = entry.Info.Format;
        var name = entry.Info.Nickname;

        // Step 1: show what the conversion changes and whether it stays legal; step 2 confirms.
        if (_pendingWithdraw != entry.Id)
        {
            var preview = new TransferPreviewService(_forge.Legality).Preview(session, room.Box, room.Slot, bytes, format);
            _forge.Sessions.RevertToBaseline(); // the preview ran in the live session; drop it
            if (preview is null)
            {
                Status = $"{name} cannot go to {CurrentSave.GameName}: that game cannot hold this Pokémon.";
                return;
            }
            var lines = new List<string> { $"{name} → {CurrentSave.GameName}, box {room.Box + 1}, slot {room.Slot + 1}." };
            lines.AddRange(preview.Changes);
            lines.AddRange(preview.Warnings);
            lines.Add($"Legality after the move: {preview.Legality}");
            WithdrawPreview = string.Join("\n", lines);
            _pendingWithdraw = entry.Id;
            OnPropertyChanged(nameof(WithdrawText));
            return;
        }

        _pendingWithdraw = null;
        WithdrawPreview = "";
        OnPropertyChanged(nameof(WithdrawText));
        var outcome = await BankTransfers.WithdrawAsync(_forge.Bank, [entry.Id], CurrentSave.GameName,
            async () => await CommitAsync(s => BankTransfers.TryImport(s, room.Box, room.Slot, bytes, out var refusal, format)
                    ? new GenerationOutcome(true, $"{name} joined {CurrentSave.GameName}.")
                    : new GenerationOutcome(false, refusal ?? $"{name} cannot go to {CurrentSave.GameName}."),
                [room], $"{name} arrived from the Bank") ? null : Status,
            async () => await CommitAsync(s =>
            {
                s.ReleaseSlot(room.Box, room.Slot);
                return new GenerationOutcome(true, $"{name} went back to the Bank.");
            }, [room], $"Undo {name} arriving from the Bank") ? null : Status);
        Status = outcome.Message;
        BankSelected = null;
        RefreshBank();
    }

    /// <summary>First empty box slot, starting from the box on screen.</summary>
    private SlotRef? FirstEmptySlot(ISaveEngineSession session) => session.Snapshot.Slots
        .Where(s => s.Box >= 0 && s.Species is not > 0)
        .OrderBy(s => (s.Box - BoxIndex + _boxCount) % Math.Max(1, _boxCount)).ThenBy(s => s.Slot)
        .Select(s => (SlotRef?)new SlotRef(s.Box, s.Slot))
        .FirstOrDefault();

    /// <summary>
    /// The one write path: mutate the live session, then validate + restore point + atomic
    /// write limited to <paramref name="scope"/>. Any failure rolls the session back.
    /// </summary>
    private async Task<bool> CommitAsync(Func<ISaveEngineSession, GenerationOutcome> mutate, IReadOnlyCollection<SlotRef> scope, string description)
    {
        if (WriteBlocker() is { } blocked)
        {
            Status = blocked;
            return false;
        }
        if (_forge.Sessions.Current is not { } open || _forge.Sessions.CurrentSession is not { } session || CurrentSave is null)
            return false;

        if (File.GetLastWriteTimeUtc(CurrentSave.Path) != _openedStamp)
        {
            await OpenAsync(CurrentSave);
            Status = "The save changed on disk since it was opened (the game saved?). It was reloaded; nothing was written. Try again.";
            return false;
        }

        IsBusy = true;
        try
        {
            var outcome = mutate(session);
            if (!outcome.Success)
            {
                _forge.Sessions.RevertToBaseline();
                Status = outcome.Message;
                return false;
            }

            var candidate = session.Serialize();
            var documentId = open.Document.DocumentId;
            var receipt = await Task.Run(async () =>
                await _forge.Writer.WriteScopedAsync(documentId, open.Snapshot, candidate, new WriteScope(scope), description));
            if (receipt.Changed)
            {
                _forge.Sessions.MarkWritten(documentId, candidate);
                _openedStamp = File.GetLastWriteTimeUtc(CurrentSave.Path);
            }
            Status = receipt.Changed ? $"{outcome.Message} Restore point saved." : "Nothing changed, so nothing was written.";
            return true;
        }
        catch (Exception error)
        {
            _forge.Sessions.RevertToBaseline();
            Status = error is UnsafeSaveWriteException ? $"Refused to write: {error.Message}" : $"Nothing was written: {error.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
            RefreshAfterWrite();
            await RefreshBackupsAsync();
        }
    }

    private void RefreshAfterWrite()
    {
        var keep = Selected is { } d ? (d.Box, d.Slot) : ((int, int)?)null;
        RefreshSlots();
        RefreshSaveTabs();
        if (keep is var (box, slot))
            LoadSelected(box, slot);
    }

    /// <summary>Writing while the emulator runs the game would be overwritten (or corrupt its next save).</summary>
    private string? WriteBlocker() => _gameRunning()
        ? "A game is running in Zest. Close it before editing so the emulator doesn't overwrite your changes."
        : null;

    // =====================================================================
    // Bank and restore points
    // =====================================================================

    public void RefreshBank()
    {
        var all = _forge.Bank.GetAll();
        BankBoxIndex = Math.Clamp(BankBoxIndex, 0, Math.Max(0, _forge.Bank.BoxCount - 1));
        var inBox = all.Where(e => e.Box == BankBoxIndex).ToDictionary(e => e.Slot);
        BankSlots.Clear();
        for (var i = 0; i < IBankService.SlotsPerBox; i++)
        {
            if (inBox.TryGetValue(i, out var e))
            {
                var species = SpeciesName(e.Info.Species);
                BankSlots.Add(new SlotTile
                {
                    Box = e.Box, Slot = e.Slot, BankId = e.Id,
                    Name = e.Info.Nickname.Length > 0 ? e.Info.Nickname : species,
                    Detail = e.Info.Nickname == species ? $"Lv. {e.Info.Level}" : species,
                    IsShiny = e.Info.Shiny,
                    IsSelected = e.Id == BankSelected?.Id,
                });
            }
            else
            {
                BankSlots.Add(new SlotTile { Box = BankBoxIndex, Slot = i, Name = "", Detail = "", IsEmpty = true });
            }
        }
        OnPropertyChanged(nameof(BankBoxTitle));
    }

    [RelayCommand]
    private void AddBankBox()
    {
        _forge.Bank.AddBox();
        BankBoxIndex = _forge.Bank.BoxCount - 1;
        RefreshBank();
    }

    private async Task RefreshBackupsAsync()
    {
        RestorePoints.Clear();
        if (_forge.Sessions.Current is not { } open)
            return;
        var all = await _forge.Backups.ListAsync();
        foreach (var b in all.Where(b => b.BelongsTo(open.Document.DocumentId, open.Snapshot)).OrderByDescending(b => b.CreatedUtc))
            RestorePoints.Add(new BackupRow(b));
    }

    [RelayCommand]
    private async Task RestoreAsync(BackupRow? row)
    {
        if (row is null || _forge.Sessions.Current is not { } open || CurrentSave is null)
            return;
        if (WriteBlocker() is { } blocked)
        {
            Status = blocked;
            return;
        }
        IsBusy = true;
        try
        {
            var bytes = await _forge.Backups.ReadAsync(row.Info.BackupId);
            // Restoring is itself a write, so the current state gets its own restore point first.
            await _forge.Writer.WriteScopedAsync(open.Document.DocumentId, open.Snapshot, bytes, WriteScope.Everything,
                $"Undo restore to {row.When}");
            await OpenAsync(CurrentSave);
            Status = $"Restored the save to {row.When}.";
        }
        catch (Exception error)
        {
            Status = $"Nothing was restored: {error.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string SlotLabel(int box, int slot) => box < 0 ? $"party {slot + 1}" : $"box {box + 1}, slot {slot + 1}";

    private string SubjectName(EntityDetail d) => d.Nickname.Length > 0 ? d.Nickname : SpeciesName(d.Species);
}
