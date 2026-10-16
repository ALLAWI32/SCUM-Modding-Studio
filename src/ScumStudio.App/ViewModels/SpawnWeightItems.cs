using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// One chance list of the game (a zone's encounters, an encounter's presets, a preset's characters, a biome's animals):
/// its rows keep percentages that add up to 100; moving one moves the others in proportion. The weights written are the
/// percentages times the list's applied total, so the game's scale (75/25, 0.3/0.2) stays.
/// </summary>
public sealed class SpawnWeightGroup
{
    private readonly Action _moved;
    private bool _balancing;

    /// <summary>
    /// Creates the group of a list; <paramref name="users"/> = the threat zones of the island that spawn from it,
    /// <paramref name="island"/> = the low-threat settings (the whole island) spawn from it too.
    /// </summary>
    public SpawnWeightGroup(SpawnWeightList list, int users, Action moved, bool island = false)
    {
        List = list;
        Users = users;
        IsIsland = island;
        _moved = moved;
    }

    /// <summary>The game's list.</summary>
    public SpawnWeightList List { get; }

    /// <summary>What the list spawns (from its entries when the list's name does not say).</summary>
    public SpawnCategory Kind { get; init; } = SpawnCategory.Other;

    /// <summary>How many threat zones of the island spawn from this list (an edit changes all of them).</summary>
    public int Users { get; }

    /// <summary>The whole island's low-threat settings spawn from this list.</summary>
    public bool IsIsland { get; }

    /// <summary>An edit reaches more than one place.</summary>
    public bool IsShared => IsIsland || Users > 1;

    /// <summary>The header row.</summary>
    public SpawnWeightRowViewModel? Header { get; private set; }

    /// <summary>The entry rows.</summary>
    public List<SpawnWeightRowViewModel> Rows { get; } = [];

    /// <summary>Its share of the zone's spawns (0-1): the overall chance of the rows that pick it.</summary>
    public double ParentShare { get; internal set; } = 1;

    /// <summary>Every weight of the list is 0 or every row is off.</summary>
    public bool AllZero => Rows.Count > 0 && Rows.All(r => r.Weight <= 0f);

    internal void Add(SpawnWeightRowViewModel row)
    {
        if (row.Entry is null)
        {
            Header = row;
        }
        else
        {
            Rows.Add(row);
        }
    }

    /// <summary>The applied weights become the reference; the percentages follow them.</summary>
    internal void Commit()
    {
        foreach (var row in Rows)
        {
            row.Committed = row.Weight;
        }

        ShowWeights();
    }

    /// <summary>The sliders back at the applied weights.</summary>
    internal void Discard()
    {
        foreach (var row in Rows)
        {
            row.Weight = row.Committed;
        }

        ShowWeights();
        _moved();
    }

    /// <summary>The sliders at the game's weights (applied with Save).</summary>
    internal void ResetToGame()
    {
        foreach (var row in Rows)
        {
            row.Weight = row.Entry!.Weight;
        }

        ShowWeights();
        _moved();
    }

    /// <summary>The applied weights as the project has them now (after undo/redo); moved rows keep the user's value.</summary>
    internal void Reload(EditState? state)
    {
        var moved = Rows.Any(r => r.IsPending);
        foreach (var row in Rows)
        {
            var applied = Applied(state, row.Entry!);
            if (!moved)
            {
                row.Weight = applied;
            }

            row.Committed = applied;
        }

        ShowWeights();
    }

    /// <summary>The weight the project applies to <paramref name="entry"/> (else the game's).</summary>
    internal float Applied(EditState? state, SpawnWeight entry) =>
        entry.Path is { } path && state?.GetAssetValue(List.Package, List.Export, path)?.Current is { } text
            && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var set) ? set : entry.Weight;

    private void ShowWeights()
    {
        var total = Rows.Sum(r => r.Weight);
        _balancing = true;
        foreach (var row in Rows)
        {
            row.SetPercent(total > 0f ? row.Weight / total * 100.0 : 0.0);
            row.NotifyPending();
        }

        _balancing = false;
    }

    /// <summary>One row's percentage moved: the others share the rest in proportion (evenly when they are all at 0).</summary>
    internal void Moved(SpawnWeightRowViewModel changed)
    {
        if (_balancing)
        {
            return;
        }

        _balancing = true;
        var others = Rows.Where(r => r != changed).ToList();
        var rest = 100.0 - changed.Percent;
        var sum = others.Sum(r => r.Percent);
        foreach (var row in others)
        {
            row.SetPercent(sum > 0.0001 ? row.Percent / sum * rest : rest / others.Count);
        }

        // The weights the game would read: the applied total shared out by percentage (100 when the list was all 0).
        var total = Rows.Sum(r => r.Committed);
        total = total > 0f ? total : 100f;
        foreach (var row in Rows)
        {
            row.Weight = (float)(row.Percent / 100.0 * total);
            row.NotifyPending();
        }

        _balancing = false;
        _moved();
    }

    /// <summary>The value edits for the moved weights.</summary>
    internal IEnumerable<EditOp> Changes(EditState state) => Rows.Where(r => r.IsPending && r.Entry!.Path is not null).Select(r =>
    {
        var path = r.Entry!.Path!;
        var old = state.GetAssetValue(List.Package, List.Export, path)?.Current ?? TunableValue.Format(r.Entry.Weight);
        return (EditOp)new SetAssetValueOp(List.Package, List.Export, path, nameof(TunableKind.Float), old, TunableValue.Format(MathF.Round(r.Weight, 4)));
    }).ToList();
}

/// <summary>
/// A row of the type picker: a list's header (title, shared-asset note, back to the game's weights) or one entry (a
/// zombie, NPC or animal type, an encounter) with its chance in the list and overall.
/// </summary>
public sealed partial class SpawnWeightRowViewModel : ObservableObject
{
    private bool _percentFromGroup;

    /// <summary>Creates a row and adds it to its group.</summary>
    public SpawnWeightRowViewModel(SpawnWeightGroup group, SpawnWeight? entry, string name, string detail, float weight, string typeGroup = "")
    {
        Group = group;
        Entry = entry;
        Name = name;
        Detail = detail;
        TypeGroup = typeGroup;
        Weight = weight;
        Committed = weight;
        group.Add(this);
    }

    /// <summary>The list the row belongs to.</summary>
    public SpawnWeightGroup Group { get; }

    /// <summary>The game's entry; null for a header.</summary>
    public SpawnWeight? Entry { get; }

    /// <summary>Title (header) or what spawns (entry).</summary>
    public string Name { get; }

    /// <summary>The note (header) or the asset name (entry).</summary>
    public string Detail { get; }

    /// <summary>The picker's group ("Zombies", "Drifters", "Guards", "Animals", "Drones").</summary>
    public string TypeGroup { get; }

    /// <summary>What the entry spawns.</summary>
    public SpawnCategory Kind { get; init; } = SpawnCategory.Other;

    /// <summary>The character Blueprint the 3D preview shows (null: the stand-in of its kind).</summary>
    public string? PreviewName { get; init; }

    /// <summary>The weight the game would read now.</summary>
    public float Weight { get; internal set; }

    /// <summary>The weight as applied in the project.</summary>
    public float Committed { get; internal set; }

    /// <summary>True for a list header.</summary>
    public bool IsHeader => Entry is null;

    /// <summary>True for an entry.</summary>
    public bool IsEntry => Entry is not null;

    /// <summary>The entry's slider moves (its list stores every weight).</summary>
    public bool CanEdit => IsEntry && Group.List.IsEditable && Group.Rows.Count > 1;

    /// <summary>The header warns that the list is shared.</summary>
    public bool IsShared => IsHeader && Group.IsShared;

    /// <summary>"Changes every place that uses it (12)" on a shared list's header.</summary>
    public string SharedText => !IsShared ? string.Empty
        : Group.IsIsland ? Loc.T("Spawns.Shared.Island")
        : Loc.F("Spawns.Shared.Warning", Group.Users);

    /// <summary>Moved and not applied.</summary>
    public bool IsPending => IsEntry && MathF.Abs(Weight - Committed) > 0.0001f * MathF.Max(1f, MathF.Abs(Committed));

    /// <summary>The chance in its list, 0-100.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double _percent;

    /// <summary>The chance among everything the zone spawns, 0-100.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverallText))]
    private double _overall;

    /// <summary>"12.5 %".</summary>
    public string PercentText => IsEntry ? Percent.ToString("0.#", CultureInfo.CurrentCulture) + " " + Loc.T("Spawns.Unit.Percent") : string.Empty;

    /// <summary>"3.1 %".</summary>
    public string OverallText => IsEntry ? Overall.ToString("0.##", CultureInfo.CurrentCulture) + " " + Loc.T("Spawns.Unit.Percent") : string.Empty;

    /// <summary>Tooltip of an entry: the asset, its weight and the game's.</summary>
    public string Tip => IsHeader ? Detail
        : $"{Detail}\n{Loc.T("Spawns.Chance.InList.Tip")}\n{Loc.F("Spawns.GameValue", Entry!.Weight.ToString("0.###", CultureInfo.CurrentCulture))}";

    /// <summary>Header: the list's weights back to the game's.</summary>
    [RelayCommand]
    private void Reset() => Group.ResetToGame();

    /// <summary>Sets the percentage without moving the others (the group balances).</summary>
    internal void SetPercent(double value)
    {
        _percentFromGroup = true;
        Percent = Math.Clamp(value, 0.0, 100.0);
        _percentFromGroup = false;
    }

    internal void NotifyPending() => OnPropertyChanged(nameof(IsPending));

    partial void OnPercentChanged(double value)
    {
        if (!_percentFromGroup && CanEdit)
        {
            Group.Moved(this);
        }
    }
}

/// <summary>Builds the picker rows of a zone's or biome's lists and keeps their overall chances.</summary>
public static class SpawnWeightTree
{
    /// <summary>
    /// What an encounter of a zone spawns: the kind <see cref="SpawnChoices.ForZone"/> gave its preset list (from the
    /// presets, as <see cref="SpawnChoices.KindOf"/>; a <c>*_Child</c> encounter's list is its parent's, found by
    /// <see cref="SpawnWeightList.Of"/>), the most common kind of mixed presets, else its class name.
    /// </summary>
    public static SpawnCategory KindOf(string encounter, IReadOnlyList<SpawnWeightList> lists)
    {
        ArgumentNullException.ThrowIfNull(lists);
        var presets = PresetsOf(encounter, lists);
        if (presets is { Category: not SpawnCategory.Other })
        {
            return presets.Category;
        }

        var kinds = presets?.Entries
            .Select(e => lists.FirstOrDefault(l => l.Level == 2 && l.Of.Equals(e.Name, StringComparison.OrdinalIgnoreCase))?.Category ?? SpawnChoices.CategoryOf(e.Name))
            .Where(c => c != SpawnCategory.Other)
            .GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        return kinds is { Count: > 0 } ? kinds[0] : SpawnChoices.CategoryOf(encounter);
    }

    /// <summary>The preset list of a zone's encounter (its own, or its parent's for a <c>*_Child</c>), or null.</summary>
    public static SpawnWeightList? PresetsOf(string encounter, IReadOnlyList<SpawnWeightList> lists) =>
        lists.FirstOrDefault(l => l.Level == 1 && l.Of.Equals(encounter, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Zombies", "Armed NPCs"... with " (2)" on the second of a kind.</summary>
    public static IReadOnlyList<string> EncounterLabels(IReadOnlyList<SpawnCategory> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var labels = kinds.Select(KindName).ToList();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        return labels.Select(l =>
        {
            var n = seen[l] = seen.GetValueOrDefault(l) + 1;
            return labels.Count(x => x == l) > 1 ? $"{l} ({n})" : l;
        }).ToList();
    }

    /// <summary>The page's name of a kind.</summary>
    public static string KindName(SpawnCategory kind) => kind switch
    {
        SpawnCategory.Zombies => Loc.T("Spawns.Encounter.Zombies"),
        SpawnCategory.Npcs => Loc.T("Spawns.Encounter.Npcs"),
        SpawnCategory.Animals => Loc.T("Spawns.Encounter.Animals"),
        SpawnCategory.Drones => Loc.T("Spawns.Encounter.Drones"),
        _ => Loc.T("Spawns.Kind.Other"),
    };

    /// <summary>The picker group of a type: Zombies, Drifters, Guards, Animals or Drones.</summary>
    public static string TypeGroupOf(SpawnCategory kind, string name) => kind switch
    {
        SpawnCategory.Npcs => name.Contains("Guard", StringComparison.OrdinalIgnoreCase) ? Loc.T("Spawns.Types.Group.Guards") : Loc.T("Spawns.Types.Group.Drifters"),
        SpawnCategory.Animals => Loc.T("Spawns.Types.Group.Animals"),
        SpawnCategory.Drones => Loc.T("Spawns.Types.Group.Drones"),
        _ => Loc.T("Spawns.Types.Group.Zombies"),
    };

    /// <summary>
    /// The overall chance of every entry: its share of its list times the share of the rows that pick the list (an
    /// encounter's presets follow the encounter's row, a preset's characters the preset's rows), the rule of
    /// <see cref="SpawnChances.Effective"/> applied to the weights on the page.
    /// </summary>
    public static void UpdateOverall(IReadOnlyList<SpawnWeightGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        foreach (var group in groups.OrderBy(g => g.List.Level))
        {
            var parents = group.List.Level switch
            {
                1 => groups.Where(g => g.List.Level == 0 && g.List.Category != SpawnCategory.Animals).SelectMany(g => g.Rows)
                    .Where(r => group.List.Of.Equals(r.Entry!.Name, StringComparison.OrdinalIgnoreCase)).ToList(),
                2 => groups.Where(g => g.List.Level == 1).SelectMany(g => g.Rows)
                    .Where(r => r.Entry!.Name.Equals(group.List.Of, StringComparison.OrdinalIgnoreCase)).ToList(),
                _ => [],
            };
            group.ParentShare = parents.Count == 0 ? 1 : Math.Min(1, parents.Sum(r => r.Overall) / 100.0);
            foreach (var row in group.Rows)
            {
                row.Overall = row.Percent * group.ParentShare;
            }
        }
    }
}
