using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Level.Spawns;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The selected zone's or biome's types: the chance lists behind it (<see cref="SpawnChoices.ForZone"/>,
/// <see cref="SpawnChoices.ForBiome"/>) as one grouped, searchable list (owner: "choose exactly which zombie or NPC types
/// spawn there, one type only or several"; 0 % = that type never spawns), the chance of each type in its list and among
/// everything the zone spawns, and a 3D preview of the selected type.
/// </summary>
public sealed partial class SpawnsPageViewModel
{
    private readonly Dictionary<string, IReadOnlyList<SpawnWeightGroup>> _weights = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SpawnWeightGroup> _shownGroups = [];
    private int _preview;

    /// <summary>The type rows shown (list headers and entries), filtered by category and search.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTypes))]
    private IReadOnlyList<SpawnWeightRowViewModel> _typeRows = [];

    /// <summary>Type search.</summary>
    [ObservableProperty]
    private string _typeFilter = string.Empty;

    /// <summary>The type whose 3D model is shown.</summary>
    [ObservableProperty]
    private SpawnWeightRowViewModel? _selectedType;

    /// <summary>The selected type's 3D model.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTypePreview))]
    private PreviewModel? _typePreview;

    /// <summary>Under the preview: the model's name, or why there is none.</summary>
    [ObservableProperty]
    private string _typePreviewText = string.Empty;

    /// <summary>The preview is being built.</summary>
    [ObservableProperty]
    private bool _isTypePreviewLoading;

    /// <summary>The selected entry has type lists for this category.</summary>
    public bool HasTypes => TypeRows.Count > 0;

    /// <summary>A 3D model is shown.</summary>
    public bool HasTypePreview => TypePreview is not null;

    partial void OnTypeFilterChanged(string value) => FilterTypes();

    partial void OnSelectedTypeChanged(SpawnWeightRowViewModel? value) => _ = LoadTypePreviewAsync(value);

    /// <summary>The groups of one zone's or biome's lists, each row at the project's applied weight.</summary>
    private IReadOnlyList<SpawnWeightGroup> BuildGroups(string package, IReadOnlyList<SpawnWeightList> lists, IReadOnlyDictionary<string, SpawnReach> users)
    {
        var state = _services.Projects.Current?.State;
        var groups = new List<SpawnWeightGroup>();
        string? TopCharacter(string preset) => lists.FirstOrDefault(l => l.Level == 2 && l.Of.Equals(preset, StringComparison.OrdinalIgnoreCase))?.Entries.MaxBy(e => e.Weight)?.Name;
        SpawnCategory PresetKind(string preset) =>
            lists.FirstOrDefault(l => l.Level == 2 && l.Of.Equals(preset, StringComparison.OrdinalIgnoreCase)) is { Category: not SpawnCategory.Other } l ? l.Category : SpawnChoices.CategoryOf(preset);

        // A *_Child encounter's presets are its parent's list (same package and export, another Of): one group per Of.
        foreach (var list in lists.DistinctBy(l => (l.Package, l.Export, l.Level, l.Of)))
        {
            var encounters = list.Level == 0 && list.Category != SpawnCategory.Animals;
            var kind = list.Category == SpawnCategory.Animals ? SpawnCategory.Animals
                : list.Level == 1 ? SpawnWeightTree.KindOf(list.Of, lists)
                : list.Level == 2 && list.Category != SpawnCategory.Other ? list.Category
                : list.Level == 2 ? PresetKind(list.Of) : SpawnCategory.Other;
            var reach = users.GetValueOrDefault(list.Package + "|" + list.Export);
            var group = new SpawnWeightGroup(list, reach.Zones, OnWeightsMoved, reach.Island) { Kind = kind };
            var level = list.Level switch
            {
                0 when list.Category == SpawnCategory.Animals => Loc.T("Spawns.Types.Level.Animals"),
                0 => Loc.T("Spawns.Card.What.Caption"),
                1 => Loc.T("Spawns.Types.Level.Presets"),
                _ => Loc.T("Spawns.Types.Level.Characters"),
            };
            _ = new SpawnWeightRowViewModel(group, null, encounters ? Loc.T("Spawns.Card.What") : Words(list.Name), level, 0f);
            var kinds = list.Entries.Select(e => encounters ? SpawnWeightTree.KindOf(e.Name, lists)
                : list.Level == 1 ? PresetKind(e.Name)
                : kind is SpawnCategory.Other ? SpawnChoices.CategoryOf(e.Name) : kind).ToList();
            var labels = encounters ? SpawnWeightTree.EncounterLabels(kinds) : list.Entries.Select(e => Words(e.Name.StartsWith("BP_", StringComparison.Ordinal) ? e.Name[3..] : e.Name)).ToList();
            for (var i = 0; i < list.Entries.Count; i++)
            {
                var entry = list.Entries[i];
                var preview = encounters ? SpawnWeightTree.PresetsOf(entry.Name, lists)?.Entries.MaxBy(e => e.Weight)?.Name is { } p ? TopCharacter(p) : null
                    : list.Level == 1 ? TopCharacter(entry.Name)
                    : entry.Name;
                _ = new SpawnWeightRowViewModel(group, entry, labels[i], entry.Name, group.Applied(state, entry), SpawnWeightTree.TypeGroupOf(kinds[i], entry.Name))
                {
                    Kind = kinds[i],
                    PreviewName = preview,
                };
            }

            group.Commit();
            groups.Add(group);
        }

        SpawnWeightTree.UpdateOverall(groups);
        _weights[package] = groups;
        return groups;
    }

    /// <summary>The selected entry's type lists (the zone's own encounter list is in its card).</summary>
    private void ShowTypes(SpawnEntry? entry)
    {
        _shownGroups = entry is not null && _weights.TryGetValue(entry.PackagePath, out var groups)
            ? groups.Where(g => g.List.Level > 0 || g.List.Category == SpawnCategory.Animals).ToList()
            : [];
        FilterTypes();
    }

    private void FilterTypes()
    {
        bool Shown(SpawnWeightGroup g) => Tab switch
        {
            SpawnTab.Zombies => g.Kind is SpawnCategory.Zombies or SpawnCategory.Other,
            SpawnTab.Npcs => g.Kind is SpawnCategory.Npcs or SpawnCategory.Drones,
            SpawnTab.Animals => g.Kind == SpawnCategory.Animals,
            _ => false,
        };

        var filter = TypeFilter.Trim();
        bool Hit(SpawnWeightRowViewModel r) => filter.Length == 0 || r.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
            || r.Detail.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || r.TypeGroup.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
        var rows = new List<SpawnWeightRowViewModel>();
        foreach (var group in _shownGroups.Where(Shown).OrderBy(g => g.Kind).ThenBy(g => g.List.Level))
        {
            var header = Hit(group.Header!);
            var hits = group.Rows.Where(r => header || Hit(r)).ToList();
            if (hits.Count > 0)
            {
                rows.Add(group.Header!);
                rows.AddRange(hits);
            }
        }

        TypeRows = rows;
        if (SelectedType is not null && !rows.Contains(SelectedType))
        {
            SelectedType = null;
        }
    }

    private void OnWeightsMoved()
    {
        UpdateOverall();
        UpdatePending();
    }

    private void UpdateOverall()
    {
        foreach (var groups in _weights.Values)
        {
            SpawnWeightTree.UpdateOverall(groups);
        }
    }

    /// <summary>Builds the 3D model of the selected type on a worker (its Blueprint, else the stand-in of its kind).</summary>
    private async Task LoadTypePreviewAsync(SpawnWeightRowViewModel? row)
    {
        var load = ++_preview;
        TypePreview = null;
        TypePreviewText = string.Empty;
        if (row is not { IsEntry: true } || _source is not { } source)
        {
            IsTypePreviewLoading = false;
            return;
        }

        IsTypePreviewLoading = true;
        try
        {
            var model = await Task.Run(() => source.Preview(row.PreviewName, row.Kind)).ConfigureAwait(true);
            if (load != _preview)
            {
                return;
            }

            TypePreview = model;
            TypePreviewText = model is null ? Loc.T("Spawns.Types.NoPreview") : Loc.F("Module.PreviewInfo", model.Name, model.Parts.Count, model.Triangles);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Spawns: no preview for {Type}: {Message}", row.PreviewName, ex.Message);
            if (load == _preview)
            {
                TypePreviewText = Loc.T("Spawns.Types.NoPreview");
            }
        }
        finally
        {
            if (load == _preview)
            {
                IsTypePreviewLoading = false;
            }
        }
    }
}
