using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Single-instance editing: a click on a tree, rock or plank selects that one ISM/HISM/foliage instance (not the actor that
/// holds thousands of them), a click on a road, river bank or bridge piece selects that one spline mesh segment (Ctrl+click
/// takes the whole road), and in part mode (or Alt+click) a click on a Blueprint building selects that one part of it (a
/// hangar's wall, shelf or lamp, as the level stores it); moves, deletes and copies act on it alone. Terrain tiles and
/// foliage containers cannot be moved.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>Part mode (kept for the next start): a click on a Blueprint building picks the part under the cursor, not the whole building.</summary>
    [ObservableProperty]
    private bool _pickParts;

    partial void OnPickPartsChanged(bool value) => _services.UiState.Update(u => u with { PickParts = value });

    /// <summary>The gizmo's arrows follow the object's own axes (kept for the next start); off: the world's.</summary>
    [ObservableProperty]
    private bool _localAxes;

    partial void OnLocalAxesChanged(bool value) => _services.UiState.Update(u => u with { LocalAxes = value });

    /// <summary>The selected instance (bound two-way to the viewport), or null when a whole actor is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedInstance), nameof(HasSelectedPart))]
    [NotifyCanExecuteChangedFor(nameof(SelectWholeCommand))]
    private InstanceKey? _selectedInstanceKey;

    /// <summary>True when one part of a Blueprint building (or one spawn point of a spawner) is selected, not the whole actor.</summary>
    public bool HasSelectedPart => (SelectedInstanceKey is { InstanceIndex: InstanceKey.Part } && HasSelectedInstance) || IsSpawnPointSelected;

    /// <summary>From the selected part to the whole building it belongs to.</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedPart))]
    private void SelectWhole() => SelectedInstanceKey = null;

    /// <summary>World transforms of instances the project moved (drawn by the viewport).</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<InstanceKey, FTransform> _instanceTransforms = new Dictionary<InstanceKey, FTransform>();

    /// <summary>
    /// A copied instance: Paste adds an instance to its component (<c>Source</c>, a stored foliage/ISM component)
    /// when pasting into its own level, else places its mesh as a new StaticMeshActor.
    /// </summary>
    private (string Mesh, TransformValue World, string? Collision, InstanceRef? Source)? _copiedInstance;

    /// <summary>True when one instance (not a whole actor) is selected.</summary>
    public bool HasSelectedInstance => SelectedInstanceInfo() is not null;

    partial void OnSelectedInstanceKeyChanged(InstanceKey? value)
    {
        ShowSelection(SelectedActor);
        if (value is { } key && SelectedActor is { } item && item.SelectableId == key.SelectableId && !_restoringSelection)
        {
            Remember(item); // another piece of the same road or forest
        }

        OnPropertyChanged(nameof(IsLootPointSelected));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DuplicateSelectedCommand.NotifyCanExecuteChanged();
        CopySelectedCommand.NotifyCanExecuteChanged();
        ApplyTransformCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Properties of one loot point: which building and spawner it belongs to, where it is.</summary>
    private static IReadOnlyList<PropertyRow> DescribeLootPoint(ActorItemViewModel item, InstanceKey point)
    {
        var component = item.Actor.FindComponent(point.Component);
        var marker = component?.SpawnMarkers is { } all && point.Marker < all.Count ? all[point.Marker] : null;
        var rows = new List<PropertyRow>
        {
            new(Localization.Loc.T("Map.Row.LootPoint"), Localization.Loc.F("Map.LootPoint.Of", point.Marker + 1, component?.SpawnMarkers.Count ?? 0, item.Name)),
            new(Localization.Loc.T("Map.Row.Level"), item.Level.PackagePath),
        };
        if (marker is not null && component is not null)
        {
            var t = (marker.Local * component.WorldTransform).Translation;
            rows.Add(new(Localization.Loc.T("Map.Row.Location"), string.Create(CultureInfo.InvariantCulture, $"{t.X:0.#}, {t.Y:0.#}, {t.Z:0.#} cm")));
        }

        return rows;
    }

    // ponytail: class-name list of containers that must stay where the level has them; extend when others turn up.
    /// <summary>Terrain tiles, foliage containers and level helpers: selecting them is fine, moving them would wreck the level.</summary>
    private static bool IsImmovable(ActorItemViewModel item) =>
        item.ClassName.StartsWith("Landscape", StringComparison.Ordinal)
        || item.ClassName is "InstancedFoliageActor" or "LevelBounds" or "WorldSettings" or "ConZWorldSettings";

    /// <summary>
    /// True when a spawn part is selected (a house's fixed-item spawner, a car shop's vehicle box): it moves, but it has no
    /// mesh to copy and what the game does with a deleted (scaled to nothing) spawner is not known, so Copy, Duplicate and
    /// Delete are off for it.
    /// </summary>
    private bool IsSpawnPartSelected => SelectedInstanceInfo() is { Instance: null } sel && SpawnMarkers.IsSpawnPart(sel.Item.Actor, sel.Component);

    /// <summary>True when the selected instance or part has no mesh (Copy and Duplicate place its mesh: nothing to place).</summary>
    private bool IsMeshlessSelected => SelectedInstanceInfo() is { } sel && MeshOf(sel) is null;

    /// <summary>The selected instance with its actor and component, or null.</summary>
    private SelectedInstance? SelectedInstanceInfo() =>
        SelectedInstanceKey is { } key && SelectedActor is { } item && item.SelectableId == key.SelectableId ? InstanceInfo(item, key) : null;

    /// <summary>The instance (or spline segment) <paramref name="key"/> of <paramref name="item"/>, or null.</summary>
    private SelectedInstance? InstanceInfo(ActorItemViewModel item, InstanceKey key)
    {
        if (key.InstanceIndex is InstanceKey.Segment or InstanceKey.Part)
        {
            return item.Actor.FindComponent(key.Component) is { } segment ? new SelectedInstance(item, null, segment) : null;
        }

        var instance = item.Actor.InstanceTransforms.FirstOrDefault(i =>
            i.InstanceIndex == key.InstanceIndex && string.Equals(i.ComponentName, key.Component, StringComparison.OrdinalIgnoreCase)) ?? AddedInstance(item, key);
        return instance is not null && item.Actor.FindComponent(instance.ComponentName) is { } component ? new SelectedInstance(item, instance, component) : null;
    }

    /// <summary>An instance the project added to the component (a copied tree), described like the component's stored ones; null when none.</summary>
    private ActorInstance? AddedInstance(ActorItemViewModel item, InstanceKey key)
    {
        if (item.IsAdded || _services.Projects.Current?.State is not { } state || item.Actor.FindComponent(key.Component) is not { IsInstanced: true } c
            || state.GetAddedInstanceTransform(new InstanceRef(item.Level.PackagePath, item.Name, c.Name, key.InstanceIndex)) is not { } value)
        {
            return null;
        }

        var local = value.ToTransform();
        return new ActorInstance(c.ExportIndex, c.Name, key.InstanceIndex, c.StaticMeshPath, local, local * c.WorldTransform) { EndCullDistance = c.InstanceEndCullDistance };
    }

    private static InstanceRef RefOf(SelectedInstance sel) =>
        new(sel.Item.Level.PackagePath, sel.Item.Name, sel.Instance!.ComponentName, sel.Instance.InstanceIndex);

    /// <summary>
    /// The instance's transform in its component (the project's override, else as stored); for a segment, the component's
    /// transform relative to its parent.
    /// </summary>
    private TransformValue CurrentInstanceTransform(SelectedInstance sel) => sel.Instance is null
        ? _services.Projects.Current?.State.GetTransformOverride(sel.Item.Reference, sel.Component.Name) ?? sel.Component.Relative
        : _services.Projects.Current?.State.GetInstanceOverride(RefOf(sel)) ?? TransformValue.FromTransform(sel.Instance.LocalTransform);

    /// <summary>World transform the selection's local transform is relative to: the component (instances) or its parent (segments, parts).</summary>
    private static FTransform SpaceOf(SelectedInstance sel)
    {
        if (sel.Instance is not null)
        {
            return sel.Component.WorldTransform;
        }

        // World = Relative * Parent, undone exactly: Relative.Inverse() is only exact for an even scale (a car shop's vehicle
        // box, 11 x 5 x 3.5 and turned, had its gizmo 5 m off).
        var (relative, world) = (sel.Component.RelativeTransform, sel.Component.WorldTransform);
        var scale = world.Scale3D * UeMath.SafeScaleReciprocal(relative.Scale3D);
        var rotation = world.Rotation * relative.Rotation.Inverse();
        return new FTransform(rotation, world.Translation - rotation.RotateVector(scale * relative.Translation), scale);
    }

    /// <summary>Properties, gizmo and edit fields for the current selection (one instance, or the actor).</summary>
    private void ShowSelection(ActorItemViewModel? item)
    {
        ShowSpawnInfo(item);
        if (item is not null && IsLootPointSelected && SelectedInstanceKey is { } point)
        {
            // One loot point of a building: what spawns there (SpawnInfo); no gizmo, it moves with its building.
            ActorProperties = DescribeLootPoint(item, point);
            SelectedRootWorld = null;
            LoadShape(null);
            return;
        }

        if (SelectedPointInfo() is { } spawnPoint)
        {
            // One point of a spawner's path or loot list: the gizmo stands on it, Duplicate adds one, Delete removes it.
            var local = spawnPoint.Points[spawnPoint.Index].Local;
            var world = local.ToTransform() * PointSpaceOf(spawnPoint);
            ActorProperties = DescribePoint(spawnPoint, world);
            SelectedRootWorld = world;
            SetEditFields(local);
            LoadShape(null);
            return;
        }

        if (SelectedInstanceInfo() is { } sel)
        {
            var local = CurrentInstanceTransform(sel);
            var world = local.ToTransform() * SpaceOf(sel);
            ActorProperties = DescribeInstance(sel, world);
            SelectedRootWorld = world;
            SetEditFields(local);
            LoadShape(item);
            return;
        }

        ActorProperties = item is null ? [] : DescribeSelected(item);
        SelectedRootWorld = item is null || IsImmovable(item) ? null : RootWorldOf(item, Drawn(item, CurrentRootTransform(item)));
        if (item is not null)
        {
            SetEditFields(CurrentRootTransform(item));
        }

        LoadShape(item);
    }

    private void SetEditFields(TransformValue t)
    {
        EditLocation = Fmt3(t.Location.X, t.Location.Y, t.Location.Z, "0.##");
        EditRotation = Fmt3(t.Rotation.Pitch, t.Rotation.Yaw, t.Rotation.Roll, "0.##");
        EditScale = Fmt3(t.Scale.X, t.Scale.Y, t.Scale.Z, "0.###");
    }

    private IReadOnlyList<PropertyRow> DescribeInstance(SelectedInstance sel, FTransform t)
    {
        var state = _services.Projects.Current?.State;
        var rows = new List<PropertyRow>
        {
            sel.Instance is null && sel.Component.SplineMesh is null
                ? new(Localization.Loc.T("Map.Row.Part"), Localization.Loc.F("Map.Part.Of", sel.Component.Name, sel.Item.Actor.ClassName.EndsWith("_C", StringComparison.Ordinal) ? sel.Item.Actor.ClassName[..^2] : sel.Item.Actor.ClassName))
            : sel.Instance is null
                ? new(Localization.Loc.T("Map.Row.Segment"), sel.Component.Name + Localization.Loc.T("Map.Segment.Hint"))
                : new(Localization.Loc.T("Map.Row.Instance"), Localization.Loc.F("Map.InstanceOf", sel.Instance.InstanceIndex.ToString(CultureInfo.InvariantCulture), sel.Instance.ComponentName)),
            new(Localization.Loc.T("Map.Row.Mesh"), MeshOf(sel) ?? Localization.Loc.T("Map.NoneParen")),
            new(Localization.Loc.T("Map.Row.Actor"), sel.Item.Name),
            new(Localization.Loc.T("Map.Row.Level"), sel.Item.Level.PackagePath),
            new(Localization.Loc.T("Map.Row.Location"), Invariant($"{t.Translation.X:0.#}, {t.Translation.Y:0.#}, {t.Translation.Z:0.#} cm")),
            new(Localization.Loc.T("Map.Row.Rotation"), Invariant($"{t.Rotation.Rotator().Pitch:0.##}, {t.Rotation.Rotator().Yaw:0.##}, {t.Rotation.Rotator().Roll:0.##} deg")),
            new(Localization.Loc.T("Map.Row.Scale"), Invariant($"{t.Scale3D.X:0.###}, {t.Scale3D.Y:0.###}, {t.Scale3D.Z:0.###}")),
        };
        if (sel.Instance is null)
        {
            if (SpawnMarkers.IsSpawnPart(sel.Item.Actor, sel.Component))
            {
                rows.Insert(1, new PropertyRow(Localization.Loc.T("Map.Row.Spawn"), Localization.Loc.T("Map.SpawnPart.Note")));
            }

            if (state?.GetTransformOverride(sel.Item.Reference, sel.Component.Name) is not null)
            {
                rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.State"), Localization.Loc.T("Map.State.Changed")));
            }
        }
        else if (state?.IsDeleted(RefOf(sel)) == true)
        {
            rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.State"), Localization.Loc.T("Map.State.Deleted")));
        }
        else if (state?.IsAdded(RefOf(sel)) == true)
        {
            rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.State"), Localization.Loc.T("Map.State.AddedInstance")));
        }
        else if (state?.GetInstanceOverride(RefOf(sel)) is not null)
        {
            rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.State"), Localization.Loc.T("Map.State.Moved")));
        }

        return rows;

        static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>World transforms of every moved instance of the loaded actors.</summary>
    private Dictionary<InstanceKey, FTransform> CollectInstanceTransforms(EditState? state)
    {
        var moved = new Dictionary<InstanceKey, FTransform>();
        if (state is null)
        {
            return moved;
        }

        var byReference = _pristineActors.ToDictionary(a => a.Reference, ActorRef.Comparer);
        // Added instances (copied trees) are drawn the same way: the viewport clones a sibling of the component for a key it has no node for.
        foreach (var (instance, value) in state.InstanceOverrides.Concat(state.AddedInstances.Keys.Select(i => (i, state.GetAddedInstanceTransform(i)!.Value))))
        {
            if (byReference.TryGetValue(instance.ActorRef, out var item) && item.Actor.FindComponent(instance.Component) is { } component)
            {
                moved[InstanceKey.Of(item.SelectableId, instance.Component, instance.Index)] = value.ToTransform() * component.WorldTransform;
            }
        }

        foreach (var (actor, name, value) in state.TransformOverrides)
        {
            if (name.Length > 0 && byReference.TryGetValue(actor, out var item) && item.Actor.FindComponent(name) is { } piece && piece.ExportIndex != item.Actor.RootComponent)
            {
                var index = piece.SplineMesh is null ? InstanceKey.Part : InstanceKey.Segment;
                moved[InstanceKey.Of(item.SelectableId, name, index)] = value.ToTransform() * SpaceOf(new SelectedInstance(item, null, piece));
            }
        }

        return moved;
    }

    /// <summary>Applies a drag of the selected instance; false when the drag was not about an instance.</summary>
    private bool TryApplyInstanceDrag(uint selectableId, FTransform world)
    {
        if (SelectedInstanceInfo() is not { } sel || sel.Item.SelectableId != selectableId)
        {
            return false;
        }

        ApplyInstanceTransform(sel, TransformValue.FromTransform(world.GetRelativeTransform(SpaceOf(sel))));
        return true;
    }

    private void ApplyInstanceTransform(SelectedInstance sel, TransformValue value)
    {
        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Localization.Loc.T("History.NoProject"), Localization.Loc.T("Map.NoProject.Moves"));
            return;
        }

        if (value.IsNearlyEqual(CurrentInstanceTransform(sel)))
        {
            return;
        }

        try
        {
            EditOp op = sel.Instance is null
                ? EditOpFactory.SetTransform(sel.Item.Level, sel.Item.Actor, value, project.State, sel.Component.Name)
                : EditOpFactory.SetInstanceTransform(sel.Item.Level, sel.Item.Actor, sel.Instance.ComponentName, sel.Instance.InstanceIndex, value, project.State);
            var entry = _services.Projects.Apply(op);
            _services.Notifications.Info(Localization.Loc.T("Map.Moved"), entry.Op.Describe());
            RefreshEdits();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.MoveFailed"), ex.Message);
        }
    }

    private void DeleteInstance(SelectedInstance sel)
    {
        if (sel.Instance is null)
        {
            // ponytail: a segment is removed by scaling it to zero (drawn and collided as nothing, written by the existing
            // component transform patch); a real component delete op when a mod needs the export gone.
            ApplyInstanceTransform(sel, CurrentInstanceTransform(sel) with { Scale = new FVector(0f, 0f, 0f) });
            return;
        }

        try
        {
            // An instance the project added is taken out of the project again (like an added actor), a stored one collapsed.
            var state = _services.Projects.Current?.State;
            EditOp op = state?.AddedInstances.GetValueOrDefault(RefOf(sel)) is { } added
                ? new RemoveAddedInstanceOp(added.Target, added)
                : EditOpFactory.DeleteInstance(sel.Item.Level, sel.Item.Actor, sel.Instance.ComponentName, sel.Instance.InstanceIndex, state);
            var entry = _services.Projects.Apply(op);
            _services.Notifications.Info(Localization.Loc.T("Map.Deleted"), entry.Op.Describe());
            if (op is RemoveAddedInstanceOp)
            {
                SelectedInstanceKey = null;
                RefreshEdits();
                return;
            }

            RefreshHiddenIds();
            ShowSelection(sel.Item);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.DeleteFailed"), ex.Message);
        }
    }

    /// <summary>
    /// A copy of the instance 2 m along +X: a new instance of its own foliage/ISM component when the level stores it (a
    /// tree that is chopped and harvested like the stock ones), else its mesh as a new StaticMeshActor (a part).
    /// </summary>
    private void DuplicateInstance(SelectedInstance sel)
    {
        if (_services.Projects.Current is not { } project || MeshOf(sel) is not { } mesh)
        {
            return;
        }

        try
        {
            var world = TransformValue.FromTransform(CurrentInstanceTransform(sel).ToTransform() * SpaceOf(sel));
            var shifted = world with { Location = new FVector(world.Location.X + 200f, world.Location.Y, world.Location.Z) };
            if (StoredInstanceOf(sel) is { } source)
            {
                AddInstance(sel.Item, source.Component, shifted, project, Localization.Loc.T("Map.Duplicated"));
                return;
            }

            // It collides as the tree, rock or part it was copied from (a tree mesh's own default lets players through).
            var op = EditOpFactory.AddStaticMeshActor(sel.Item.Level, mesh, shifted, project.State) with { CollisionProfile = sel.Component.CollisionProfile };
            var entry = _services.Projects.Apply(op);
            _services.Notifications.Info(Localization.Loc.T("Map.Duplicated"), entry.Op.Describe());
            RefreshEdits();
            SelectedActor = AllActors.FirstOrDefault(a => a.IsAdded && a.Name == op.NewName) ?? SelectedActor;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.DuplicateFailed"), ex.Message);
        }
    }

    /// <summary>The selected instance's reference when its component is an instanced mesh component the level stores (an instance can be added to it), else null.</summary>
    private static InstanceRef? StoredInstanceOf(SelectedInstance sel) =>
        sel.Instance is not null && !sel.Item.IsAdded && sel.Component is { IsInstanced: true, IsSynthesized: false } ? RefOf(sel) : null;

    /// <summary>Journals a new instance of <paramref name="component"/> of <paramref name="item"/> at <paramref name="world"/> and selects it.</summary>
    private void AddInstance(ActorItemViewModel item, string component, TransformValue world, Level.Projects.Project project, string title)
    {
        var space = item.Actor.FindComponent(component)!.WorldTransform;
        var op = EditOpFactory.AddInstance(item.Level, item.Actor, component, TransformValue.FromTransform(world.ToTransform().GetRelativeTransform(space)), project.State);
        var entry = _services.Projects.Apply(op);
        _services.Notifications.Info(title, entry.Op.Describe());
        RefreshEdits();
        SelectedActor = item;
        SelectedInstanceKey = InstanceKey.Of(item.SelectableId, op.Target.Component, op.Target.Index);
    }

    /// <summary>Remembers the selected instance's mesh and transform for Paste.</summary>
    private void CopyInstance(SelectedInstance sel)
    {
        if (MeshOf(sel) is not { } mesh)
        {
            return;
        }

        _copiedInstance = (mesh, TransformValue.FromTransform(CurrentInstanceTransform(sel).ToTransform() * SpaceOf(sel)), sel.Component.CollisionProfile, StoredInstanceOf(sel));
        CopiedActor = null;
        OnPropertyChanged(nameof(HasCopiedActor));
        OnPropertyChanged(nameof(PasteTip));
        PasteCommand.NotifyCanExecuteChanged();
        _services.Notifications.Info(Localization.Loc.T("Assets.Copied"), Localization.Loc.F("Map.CopiedMesh", ShortName(mesh)));
    }

    /// <summary>Pastes a copied instance as a new StaticMeshActor at the aim point; false when no instance was copied.</summary>
    private bool TryPasteInstance()
    {
        if (_copiedInstance is not { } copied || PreparedScene is not { } scene || _services.Projects.Current is not { } project)
        {
            return false;
        }

        var target = SelectedActor?.Level ?? scene.Documents.FirstOrDefault(d => !d.Name.StartsWith("Landscape_", StringComparison.OrdinalIgnoreCase)) ?? scene.Documents[0];
        var at = AimPointProvider?.Invoke() ?? copied.World.Location;
        try
        {
            // Into its own level: a new instance of the same component (a tree like the stock ones); elsewhere a mesh actor.
            if (copied.Source is { } source && string.Equals(target.PackagePath, source.Level, StringComparison.OrdinalIgnoreCase) && PristineOf(source.ActorRef) is { } owner)
            {
                AddInstance(owner, source.Component, copied.World with { Location = at }, project, Localization.Loc.T("Map.Pasted"));
                return true;
            }

            var op = EditOpFactory.AddStaticMeshActor(target, copied.Mesh, copied.World with { Location = at }, project.State) with { CollisionProfile = copied.Collision };
            var entry = _services.Projects.Apply(op);
            _services.Notifications.Info(Localization.Loc.T("Map.Pasted"), entry.Op.Describe());
            RefreshEdits();
            SelectedActor = AllActors.FirstOrDefault(a => a.IsAdded && a.Name == op.NewName) ?? SelectedActor;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.PasteFailed"), ex.Message);
        }

        return true;
    }

    private static string? MeshOf(SelectedInstance sel) => sel.Instance is null ? sel.Component.StaticMeshPath : sel.Instance.StaticMeshPath;

    /// <summary>One selected ISM/HISM/foliage instance, or (Instance null) one spline mesh segment.</summary>
    private sealed record SelectedInstance(ActorItemViewModel Item, ActorInstance? Instance, ComponentRecord Component);
}
