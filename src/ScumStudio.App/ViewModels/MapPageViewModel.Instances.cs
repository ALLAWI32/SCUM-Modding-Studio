using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Single-instance editing: a click on a tree, rock or plank selects that one ISM/HISM/foliage instance (not the actor that
/// holds thousands of them), a click on a road, river bank or bridge piece selects that one spline mesh segment (Ctrl+click
/// takes the whole road), and moves, deletes and copies act on it alone. Terrain tiles and foliage containers cannot be moved.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>The selected instance (bound two-way to the viewport), or null when a whole actor is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedInstance))]
    private InstanceKey? _selectedInstanceKey;

    /// <summary>World transforms of instances the project moved (drawn by the viewport).</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<InstanceKey, FTransform> _instanceTransforms = new Dictionary<InstanceKey, FTransform>();

    /// <summary>A copied instance: Paste places its mesh as a new StaticMeshActor.</summary>
    private (string Mesh, TransformValue World)? _copiedInstance;

    /// <summary>True when one instance (not a whole actor) is selected.</summary>
    public bool HasSelectedInstance => SelectedInstanceInfo() is not null;

    partial void OnSelectedInstanceKeyChanged(InstanceKey? value)
    {
        ShowSelection(SelectedActor);
        if (value is { } key && SelectedActor is { } item && item.SelectableId == key.SelectableId && !_restoringSelection)
        {
            Remember(item); // another piece of the same road or forest
        }

        DeleteSelectedCommand.NotifyCanExecuteChanged();
        DuplicateSelectedCommand.NotifyCanExecuteChanged();
        CopySelectedCommand.NotifyCanExecuteChanged();
    }

    // ponytail: class-name list of containers that must stay where the level has them; extend when others turn up.
    /// <summary>Terrain tiles, foliage containers and level helpers: selecting them is fine, moving them would wreck the level.</summary>
    private static bool IsImmovable(ActorItemViewModel item) =>
        item.ClassName.StartsWith("Landscape", StringComparison.Ordinal)
        || item.ClassName is "InstancedFoliageActor" or "LevelBounds" or "WorldSettings" or "ConZWorldSettings";

    /// <summary>The selected instance with its actor and component, or null.</summary>
    private SelectedInstance? SelectedInstanceInfo() =>
        SelectedInstanceKey is { } key && SelectedActor is { } item && item.SelectableId == key.SelectableId ? InstanceInfo(item, key) : null;

    /// <summary>The instance (or spline segment) <paramref name="key"/> of <paramref name="item"/>, or null.</summary>
    private static SelectedInstance? InstanceInfo(ActorItemViewModel item, InstanceKey key)
    {
        if (key.InstanceIndex == InstanceKey.Segment)
        {
            return item.Actor.FindComponent(key.Component) is { } segment ? new SelectedInstance(item, null, segment) : null;
        }

        var instance = item.Actor.InstanceTransforms.FirstOrDefault(i =>
            i.InstanceIndex == key.InstanceIndex && string.Equals(i.ComponentName, key.Component, StringComparison.OrdinalIgnoreCase));
        return instance is not null && item.Actor.FindComponent(instance.ComponentName) is { } component ? new SelectedInstance(item, instance, component) : null;
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

    /// <summary>World transform the selection's local transform is relative to: the component (instances) or its parent (segments).</summary>
    private static FTransform SpaceOf(SelectedInstance sel) => sel.Instance is null
        ? sel.Component.RelativeTransform.Inverse() * sel.Component.WorldTransform
        : sel.Component.WorldTransform;

    /// <summary>Properties, gizmo and edit fields for the current selection (one instance, or the actor).</summary>
    private void ShowSelection(ActorItemViewModel? item)
    {
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
            sel.Instance is null
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
            if (state?.GetTransformOverride(sel.Item.Reference, sel.Component.Name) is not null)
            {
                rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.State"), Localization.Loc.T("Map.State.Changed")));
            }
        }
        else if (state?.IsDeleted(RefOf(sel)) == true)
        {
            rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.State"), Localization.Loc.T("Map.State.Deleted")));
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
        foreach (var (instance, value) in state.InstanceOverrides)
        {
            if (byReference.TryGetValue(instance.ActorRef, out var item) && item.Actor.FindComponent(instance.Component) is { } component)
            {
                moved[InstanceKey.Of(item.SelectableId, instance.Component, instance.Index)] = value.ToTransform() * component.WorldTransform;
            }
        }

        foreach (var (actor, name, value) in state.TransformOverrides)
        {
            if (name.Length > 0 && byReference.TryGetValue(actor, out var item) && item.Actor.FindComponent(name) is { SplineMesh: not null } segment)
            {
                moved[InstanceKey.Of(item.SelectableId, name, InstanceKey.Segment)] = value.ToTransform() * SpaceOf(new SelectedInstance(item, null, segment));
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
            var entry = _services.Projects.Apply(EditOpFactory.DeleteInstance(sel.Item.Level, sel.Item.Actor, sel.Instance.ComponentName, sel.Instance.InstanceIndex));
            _services.Notifications.Info(Localization.Loc.T("Map.Deleted"), entry.Op.Describe());
            RefreshHiddenIds();
            ShowSelection(sel.Item);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.DeleteFailed"), ex.Message);
        }
    }

    /// <summary>A copy of the instance's mesh as a new StaticMeshActor in the same level, 2 m along +X.</summary>
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
            var op = EditOpFactory.AddStaticMeshActor(sel.Item.Level, mesh, shifted, project.State);
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

    /// <summary>Remembers the selected instance's mesh and transform for Paste.</summary>
    private void CopyInstance(SelectedInstance sel)
    {
        if (MeshOf(sel) is not { } mesh)
        {
            return;
        }

        _copiedInstance = (mesh, TransformValue.FromTransform(CurrentInstanceTransform(sel).ToTransform() * SpaceOf(sel)));
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
            var op = EditOpFactory.AddStaticMeshActor(target, copied.Mesh, copied.World with { Location = at }, project.State);
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
