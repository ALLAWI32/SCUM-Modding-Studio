using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;

namespace ScumStudio.App.ViewModels;

/// <summary>One loot preset of the selected container (its items are listed under "What can spawn here").</summary>
/// <param name="Path">The preset class path.</param>
/// <param name="Name">The preset's name (<c>Examine_Weapon_Locker_Lockpick_Weapon</c>).</param>
public sealed record LootPresetRow(string Path, string Name);

/// <summary>
/// The loot editor (owner: "every crate, locker, safe, cabinet: Lootable on/off and what it can contain"). A player
/// searches a mesh through its component's <c>ExamineAssetData</c> entries (one loot preset each, e.g.
/// <c>Examine_Weapon_Locker_Lockpick_Weapon</c>); the editor shows the selected container's presets (their groups and
/// items appear under "What can spawn here"), turns it into decoration (no entries) or back, and swaps or adds presets.
/// Every change is a <see cref="SetLootOp"/>; copies carry the loot (see <see cref="LootOf(ActorItemViewModel, ComponentRecord)"/>).
/// </summary>
public sealed partial class MapPageViewModel
{
    private const string PresetsFolder = "/Game/ConZ_Files/Items/SpawnerPresets2/";
    private IReadOnlyList<LootPresetRow>? _lootChoices;
    private bool _syncingLoot;

    /// <summary>True when the selection is a searchable container (or a mesh part that was or can be one).</summary>
    [ObservableProperty]
    private bool _hasLootEditor;

    /// <summary>Off = decoration only: nothing to search, no loot.</summary>
    [ObservableProperty]
    private bool _isLootable;

    /// <summary>The selected container's loot presets.</summary>
    [ObservableProperty]
    private IReadOnlyList<LootPresetRow> _lootPresets = [];

    /// <summary>The preset picked in the "add" box.</summary>
    [ObservableProperty]
    private LootPresetRow? _selectedLootChoice;

    /// <summary>Every examine preset of the game (<c>SpawnerPresets2/**/Examine_*</c>), by name.</summary>
    public IReadOnlyList<LootPresetRow> LootChoices => _lootChoices ??= ReadLootChoices();

    private IReadOnlyList<LootPresetRow> ReadLootChoices()
    {
        if (_services.Workspace.Catalog is not { } catalog)
        {
            return [];
        }

        var folder = AssetPaths.ToFilePathWithoutExtension(PresetsFolder, catalog.ProjectName);
        return catalog.PackageFiles
            .Where(f => f.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(f).StartsWith("Examine_", StringComparison.OrdinalIgnoreCase)
                        && f.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            .Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
            .Select(p => new LootPresetRow(p + "." + p[(p.LastIndexOf('/') + 1)..] + "_C", p[(p.LastIndexOf('/') + 1)..]))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The mesh component the editor edits, with the name the journal uses (null = the actor's root): the selected part, or
    /// the actor's first searchable mesh (a locker's, a crate's); a plain mesh actor's own mesh.
    /// </summary>
    private (ActorItemViewModel Item, string? Component, ComponentRecord Record)? LootTarget()
    {
        if (SelectedActor is not { } item || _services.Projects.Current is null)
        {
            return null;
        }

        if (SelectedInstanceInfo() is { Instance: null } part)
        {
            return part.Component.IsStaticMeshComponent && !part.Component.IsSynthesized ? (item, part.Component.Name, part.Component) : null;
        }

        var root = item.Actor.Root;
        var searchable = item.Actor.Components.FirstOrDefault(c => c.LootPresets.Count > 0 && !c.IsSynthesized);
        if (searchable is not null && (item.Actor.IsItemContainer || item.Actor.Kind == ActorKind.StaticMeshActor || item.IsAdded))
        {
            return (item, ReferenceEquals(searchable, root) || searchable.Name == root?.Name ? null : searchable.Name, searchable);
        }

        return item.Actor.Kind == ActorKind.StaticMeshActor && root is { IsSynthesized: false } r ? (item, null, r) : null;
    }

    /// <summary>The loot a component has now: the journal's setting, an added mesh's carried loot, or what the level stores.</summary>
    private LootSetting CurrentLoot(ActorItemViewModel item, string? component, ComponentRecord record)
    {
        if (_services.Projects.Current?.State is { } state)
        {
            if (state.GetLoot(item.Reference, component) is { } set)
            {
                return set;
            }

            if (item.IsAdded && state.AddedActors.GetValueOrDefault(item.Reference) is AddStaticMeshActorOp { Loot: { } carried })
            {
                return new LootSetting(carried.Count > 0, carried);
            }
        }

        return new LootSetting(record.LootPresets.Count > 0, record.LootPresets);
    }

    /// <summary>The loot a copy of <paramref name="record"/> carries (null = none of its own: only what its mesh asset has).</summary>
    private IReadOnlyList<string>? LootOf(ActorItemViewModel item, ComponentRecord record)
    {
        var component = ReferenceEquals(record, item.Actor.Root) || record.Name == item.Actor.Root?.Name ? null : record.Name;
        var loot = CurrentLoot(item, component, record);
        return loot.Lootable ? (loot.Presets.Count > 0 ? loot.Presets : null) : [];
    }

    /// <summary>Shows the selection's loot (called with the selection's other panels).</summary>
    private void RefreshLootEditor()
    {
        _syncingLoot = true;
        try
        {
            if (LootTarget() is { } target)
            {
                var loot = CurrentLoot(target.Item, target.Component, target.Record);
                HasLootEditor = true;
                IsLootable = loot.Lootable;
                LootPresets = loot.Presets.Select(p => new LootPresetRow(p, PresetName(p))).ToList();
            }
            else
            {
                HasLootEditor = false;
                IsLootable = false;
                LootPresets = [];
            }
        }
        finally
        {
            _syncingLoot = false;
        }
    }

    partial void OnIsLootableChanged(bool value)
    {
        if (_syncingLoot || LootTarget() is not { } target)
        {
            return;
        }

        var current = CurrentLoot(target.Item, target.Component, target.Record);
        var presets = current.Presets.Count > 0 ? current.Presets : SelectedLootChoice is { } pick ? [pick.Path] : [];
        if (value && presets.Count == 0)
        {
            _services.Notifications.Info(Loc.T("Map.Loot.Title"), Loc.T("Map.Loot.PickFirst"));
            RefreshLootEditor();
            return;
        }

        ApplyLoot(target, current, new LootSetting(value, presets));
    }

    /// <summary>Adds the preset picked in the box (a container that was decoration becomes lootable with it).</summary>
    [RelayCommand]
    private void AddLootPreset()
    {
        if (SelectedLootChoice is not { } pick || LootTarget() is not { } target)
        {
            return;
        }

        var current = CurrentLoot(target.Item, target.Component, target.Record);
        var presets = (current.Lootable ? current.Presets : []).Append(pick.Path).ToList();
        ApplyLoot(target, current, new LootSetting(true, presets));
    }

    /// <summary>Removes one preset; the last one gone leaves the container as decoration.</summary>
    [RelayCommand]
    private void RemoveLootPreset(LootPresetRow? row)
    {
        if (row is null || LootTarget() is not { } target)
        {
            return;
        }

        var current = CurrentLoot(target.Item, target.Component, target.Record);
        var presets = current.Presets.Where(p => !string.Equals(p, row.Path, StringComparison.OrdinalIgnoreCase)).ToList();
        ApplyLoot(target, current, new LootSetting(presets.Count > 0, presets));
    }

    private void ApplyLoot((ActorItemViewModel Item, string? Component, ComponentRecord Record) target, LootSetting old, LootSetting next)
    {
        if (old.Equals(next))
        {
            return;
        }

        try
        {
            var entry = _services.Projects.Apply(new SetLootOp(target.Item.Reference, target.Component, old, next));
            _services.Notifications.Info(Loc.T("Map.Loot.Title"), entry.Op.Describe());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Loc.T("Map.Loot.Title"), ex.Message);
        }

        RefreshLootEditor();
        ShowSpawnInfo(SelectedActor);
    }

    /// <summary>The selection's loot presets as loot points, so "What can spawn here" lists their groups and items.</summary>
    private IEnumerable<SpawnMarker> LootMarkers() =>
        LootTarget() is { } target && CurrentLoot(target.Item, target.Component, target.Record) is { Lootable: true } loot
            ? loot.Presets.Select(p => new SpawnMarker(FTransform.Identity, PresetName(p), 100, 1, 1) { PresetPath = p })
            : [];

    private static string PresetName(string path)
    {
        var name = path[(path.LastIndexOf('.') + 1)..];
        return name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name;
    }
}
