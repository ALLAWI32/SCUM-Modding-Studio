using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The points of a spawner's stored point array (a sentry's patrol path, a spawner group's loot points; Discord
/// JimTheCoffeeGuy: "the tool shows the patrol points but does not let you move, add or remove them"): each point is a pin
/// of its own that the gizmo moves, Duplicate (or Copy) adds a point beside it, Delete removes it. Every edit is one
/// <see cref="SetSpawnPointsOp"/> with the whole list; the viewport redraws the actor's pins from the list as it is now
/// (<see cref="PinOverrides"/>), so a pin's key is the point's current index.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>The spawn point pins of actors whose point lists the project changed, by selectable id (drawn by the viewport).</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<uint, IReadOnlyList<ScenePlacement>> _pinOverrides = new Dictionary<uint, IReadOnlyList<ScenePlacement>>();

    /// <summary>True when one point of a spawner's point array is selected.</summary>
    public bool IsSpawnPointSelected => SelectedPointInfo() is not null;

    /// <summary>The selected spawn point with its array and the list as it is now, or null.</summary>
    private SelectedPoint? SelectedPointInfo() =>
        SelectedInstanceKey is { IsSpawnPoint: true } key && SelectedActor is { } item && item.SelectableId == key.SelectableId ? PointInfo(item, key) : null;

    private SelectedPoint? PointInfo(ActorItemViewModel item, InstanceKey key)
    {
        if (SpawnPointArrays.Find(item.Actor, key.Component) is not { } array)
        {
            return null;
        }

        var points = CurrentPoints(item, array);
        return key.Point >= 0 && key.Point < points.Count ? new SelectedPoint(item, array, key.Point, points) : null;
    }

    /// <summary>The points of <paramref name="array"/> as the project has them (its override, else as stored).</summary>
    private IReadOnlyList<SpawnPoint> CurrentPoints(ActorItemViewModel item, SpawnPointArray array) =>
        _services.Projects.Current?.State.GetSpawnPoints(item.Reference, array.Component, array.Array) ?? array.Points;

    /// <summary>
    /// The world transform a point's local place is relative to: the actor's root as it is now (patrol points, a spawner
    /// group's own component), or the storing component.
    /// </summary>
    private FTransform PointSpaceOf(SelectedPoint sel)
    {
        var component = sel.Array.Component is null ? null : sel.Item.Actor.FindComponent(sel.Array.Component);
        return component is null || component.ExportIndex == sel.Item.Actor.RootComponent
            ? RootWorldOf(sel.Item, CurrentRootTransform(sel.Item))
            : component.WorldTransform;
    }

    /// <summary>Properties of one spawn point: which point of which spawner, where it is.</summary>
    private IReadOnlyList<PropertyRow> DescribePoint(SelectedPoint sel, FTransform world)
    {
        var rows = new List<PropertyRow>
        {
            new(Localization.Loc.T("Map.Row.SpawnPoint"), Localization.Loc.F("Map.SpawnPoint.Of", sel.Index + 1, sel.Points.Count, sel.Item.Name)),
            new(Localization.Loc.T("Map.Row.Actor"), sel.Item.Name),
            new(Localization.Loc.T("Map.Row.Level"), sel.Item.Level.PackagePath),
            new(Localization.Loc.T("Map.Row.Location"), string.Create(CultureInfo.InvariantCulture, $"{world.Translation.X:0.#}, {world.Translation.Y:0.#}, {world.Translation.Z:0.#} cm")),
        };
        if (_services.Projects.Current?.State.GetSpawnPoints(sel.Item.Reference, sel.Array.Component, sel.Array.Array) is not null)
        {
            rows.Add(new PropertyRow(Localization.Loc.T("Map.Row.State"), Localization.Loc.T("Map.State.Changed")));
        }

        return rows;
    }

    /// <summary>Applies a drag of the selected spawn point; false when the drag was not about a point.</summary>
    private bool TryApplyPointDrag(uint selectableId, FTransform world)
    {
        if (SelectedPointInfo() is not { } sel || sel.Item.SelectableId != selectableId)
        {
            return false;
        }

        ApplyPointTransform(sel, TransformValue.FromTransform(world.GetRelativeTransform(PointSpaceOf(sel))));
        return true;
    }

    /// <summary>Moves the selected point to <paramref name="local"/> (relative to its owner).</summary>
    private void ApplyPointTransform(SelectedPoint sel, TransformValue local)
    {
        if (local.IsNearlyEqual(sel.Points[sel.Index].Local))
        {
            return;
        }

        var points = sel.Points.ToList();
        points[sel.Index] = points[sel.Index] with { Local = local };
        ApplyPoints(sel, points, Localization.Loc.T("Map.Moved"));
    }

    /// <summary>Removes the selected point; the spawner stays selected.</summary>
    private void DeletePoint(SelectedPoint sel)
    {
        var points = sel.Points.Where((_, i) => i != sel.Index).ToList();
        SelectedInstanceKey = null;
        ApplyPoints(sel, points, Localization.Loc.T("Map.Deleted"));
    }

    /// <summary>Adds a copy of the selected point one metre along its owner's X and selects it.</summary>
    private void DuplicatePoint(SelectedPoint sel)
    {
        var source = sel.Points[sel.Index];
        var points = sel.Points.Append(source with { Local = source.Local with { Location = source.Local.Location + new FVector(100f, 0f, 0f) } }).ToList();
        if (ApplyPoints(sel, points, Localization.Loc.T("Map.Duplicated")))
        {
            SelectedInstanceKey = InstanceKey.Of(sel.Item.SelectableId, sel.Array.Key, InstanceKey.SpawnPointBase - (points.Count - 1));
        }
    }

    private bool ApplyPoints(SelectedPoint sel, IReadOnlyList<SpawnPoint> points, string title)
    {
        if (_services.Projects.Current is not { } project)
        {
            _services.Notifications.Warning(Localization.Loc.T("History.NoProject"), Localization.Loc.T("Map.NoProject.Moves"));
            return false;
        }

        try
        {
            var entry = _services.Projects.Apply(EditOpFactory.SetSpawnPoints(sel.Item.Level, sel.Item.Actor, sel.Array.Component, points, project.State));
            _services.Notifications.Info(title, entry.Op.Describe());
            RefreshEdits();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Localization.Loc.T("Map.MoveFailed"), ex.Message);
            return false;
        }
    }

    /// <summary>The pins of every loaded actor whose point lists the project changed, drawn from the lists as they are now.</summary>
    private Dictionary<uint, IReadOnlyList<ScenePlacement>> CollectPinOverrides(EditState? state)
    {
        var pins = new Dictionary<uint, IReadOnlyList<ScenePlacement>>();
        if (state is null || PreparedScene is not { } scene)
        {
            return pins;
        }

        foreach (var group in state.SpawnPointOverrides.GroupBy(o => o.Actor, ActorRef.Comparer))
        {
            if (!_pristineByRef.TryGetValue(group.Key, out var item))
            {
                continue;
            }

            var document = scene.Documents.ToList().IndexOf(item.Level);
            if (document < 0)
            {
                continue;
            }

            var actor = item.Actor;
            foreach (var (_, component, _, points) in group)
            {
                actor = SpawnPointArrays.WithPoints(actor, component, points);
            }

            pins[item.SelectableId] = LevelScenePreparer.PinPlacements(actor, item.SelectableId, document).Where(p => p.SpawnPoint is not null).ToList();
        }

        return pins;
    }

    /// <summary>One selected spawn point: its spawner, its array, its index and the list as it is now.</summary>
    private sealed record SelectedPoint(ActorItemViewModel Item, SpawnPointArray Array, int Index, IReadOnlyList<SpawnPoint> Points);
}
