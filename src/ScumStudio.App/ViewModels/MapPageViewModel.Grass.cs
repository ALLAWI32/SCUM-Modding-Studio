using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using ScumStudio.App.Localization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// "Clear grass under it" (owner and a Discord user, 2026-10-09: "I placed a bridge and the meadow grass grows through
/// it"): a checkbox for the placed or moved objects of the selection (one actor, or the whole actors of a multi-selection).
/// Every change is a <see cref="SetClearGrassOp"/>; the export does the clearing (see <see cref="GrassClearing"/>). The
/// view hides the bushes and grass instances that will go; it draws no landscape grass, so there is none to hide.
/// </summary>
public sealed partial class MapPageViewModel
{
    private bool _syncingGrass;
    private (IReadOnlyList<ActorItemViewModel>? Of, List<(ActorItemViewModel Item, ActorInstance Instance)> List) _clearableFoliage = (null, []);
    private readonly Dictionary<ActorRef, (int Shape, FTransform At, List<(Vector2[] Poly, Vector2 Min, Vector2 Max)> Shapes)> _footprints = new(ActorRef.Comparer);

    /// <summary>True when the selection has a placed or moved object.</summary>
    [ObservableProperty]
    private bool _hasGrassOption;

    /// <summary>On: the export clears the grass and bushes under the selected objects.</summary>
    [ObservableProperty]
    private bool _clearsGrass;

    /// <summary>The selected actors the setting applies to: added, moved or bent ones.</summary>
    private List<ActorItemViewModel> GrassTargets()
    {
        if (_services.Projects.Current?.State is not { } state)
        {
            return [];
        }

        IEnumerable<ActorItemViewModel> items = HasGroup
            ? GroupItems().Where(g => g.Instance is null).Select(g => g.Item)
            : SelectedActor is { } selected ? [selected] : [];
        return items.Where(i => !i.IsDeleted && (i.IsAdded || state.GetTransformOverride(i.Reference) is not null || state.Bends.ContainsKey(i.Reference)))
            .Distinct().ToList();
    }

    /// <summary>Shows the selection's setting (called with the selection's other panels and after every edit).</summary>
    private void RefreshGrassOption()
    {
        _syncingGrass = true;
        try
        {
            var targets = GrassTargets();
            var state = _services.Projects.Current?.State;
            HasGrassOption = targets.Count > 0;
            ClearsGrass = state is not null && targets.Count > 0 && targets.All(t => state.ClearsGrass(t.Reference));
        }
        finally
        {
            _syncingGrass = false;
        }
    }

    partial void OnClearsGrassChanged(bool value)
    {
        if (_syncingGrass || _services.Projects.Current?.State is not { } state)
        {
            return;
        }

        // Back to the default leaves no setting behind (on for added objects, off for moved ones).
        var ops = GrassTargets().Where(t => state.ClearsGrass(t.Reference) != value)
            .Select(t => (EditOp)new SetClearGrassOp(t.Reference, state.GetClearGrass(t.Reference), value == state.IsAdded(t.Reference) ? null : value))
            .ToList();
        if (ops.Count > 0)
        {
            try
            {
                var entry = _services.Projects.Apply(ops.Count == 1 ? ops[0] : new BatchOp(Loc.T("Map.Grass.Clear"), ops));
                _services.Notifications.Info(Loc.T("Map.Grass.Title"), entry.Op.Describe());
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                _services.Notifications.Error(Loc.T("Map.Grass.Title"), ex.Message);
            }
        }

        RefreshHiddenIds();
    }

    /// <summary>
    /// The bushes and grass instances of the loaded levels whose base stands under a placed or moved object that clears
    /// its grass (the export deletes them). ponytail: bent pieces count by their straight outline here; the export uses the bent one.
    /// </summary>
    private IEnumerable<InstanceKey> GrassClearedInstances(EditState state)
    {
        // The bushes and grass of the loaded levels first: no footprint is measured where there are none (a thousand edits stay quick).
        // Kept while the loaded levels stay the same (added objects are rebuilt on every edit, the game's bushes are not).
        // ponytail: a bush moved by hand shows its old spot here until the next load.
        if (!ReferenceEquals(_clearableFoliage.Of, _pristineActors))
        {
            _footprints.Clear();
            _clearableFoliage = (_pristineActors, _pristineActors.Where(a => a.Actor.InstanceTransforms.Count > 0)
                .SelectMany(a => a.Actor.InstanceTransforms.Where(i => a.Actor.FindComponent(i.ComponentName) is { } c && GrassClearing.IsClearedFoliage(c)).Select(i => (Item: a, Instance: i)))
                .ToList());
        }

        // An object's outline is measured again only when it moved or changed: one move of a thousand stays quick. Keyed by
        // its level and name with its meshes and stored places, which stay the same when an edit rebuilds the added objects.
        List<(Vector2[] Poly, Vector2 Min, Vector2 Max)> FootprintsOf(ActorItemViewModel a)
        {
            var (root, stored) = (WorldOf(a, null), StoredRoot(a));
            var shape = new HashCode();
            shape.Add(stored);
            foreach (var c in a.Actor.Components)
            {
                shape.Add(c.StaticMeshPath);
                shape.Add(c.WorldTransform);
            }

            var signature = shape.ToHashCode();
            if (_footprints.TryGetValue(a.Reference, out var known) && known.Shape == signature && known.At == root)
            {
                return known.Shapes;
            }

            var shapes = GrassClearing.Footprints(a.Actor with
                {
                    Components = a.Actor.Components.Select(c => c with { WorldTransform = c.WorldTransform.GetRelativeTransform(stored) * root }).ToList(),
                }, MeshBounds)
                .Select(p => (Poly: p, Min: new Vector2(p.Min(v => v.X), p.Min(v => v.Y)), Max: new Vector2(p.Max(v => v.X), p.Max(v => v.Y))))
                .ToList();
            _footprints[a.Reference] = (signature, root, shapes);
            return shapes;
        }

        var candidates = _clearableFoliage.List.Where(c => !c.Item.IsDeleted).ToList();
        if (candidates.Count == 0)
        {
            yield break;
        }

        var footprints = AllActors
            .Where(a => !a.IsDeleted && (a.IsAdded || state.GetTransformOverride(a.Reference) is not null || state.Bends.ContainsKey(a.Reference)) && state.ClearsGrass(a.Reference))
            .SelectMany(FootprintsOf)
            .ToList();
        if (footprints.Count == 0)
        {
            yield break;
        }

        // Outlines bucketed in 20 m cells: each bush is tested only against the outlines over its own cell.
        const float Cell = 2_000f;
        static (int, int) CellOf(float x, float y) => ((int)MathF.Floor(x / Cell), (int)MathF.Floor(y / Cell));
        var cells = new Dictionary<(int, int), List<int>>();
        for (var i = 0; i < footprints.Count; i++)
        {
            var ((x0, y0), (x1, y1)) = (CellOf(footprints[i].Min.X, footprints[i].Min.Y), CellOf(footprints[i].Max.X, footprints[i].Max.Y));
            for (var x = x0; x <= x1; x++)
            {
                for (var y = y0; y <= y1; y++)
                {
                    if (!cells.TryGetValue((x, y), out var list))
                    {
                        cells[(x, y)] = list = [];
                    }

                    list.Add(i);
                }
            }
        }

        foreach (var (item, instance) in candidates)
        {
            var at = new Vector2(instance.WorldTransform.Translation.X, instance.WorldTransform.Translation.Y);
            if (cells.TryGetValue(CellOf(at.X, at.Y), out var near)
                && near.Exists(i => at.X >= footprints[i].Min.X && at.X <= footprints[i].Max.X && at.Y >= footprints[i].Min.Y && at.Y <= footprints[i].Max.Y
                    && GrassClearing.Contains(footprints[i].Poly, at)))
            {
                yield return InstanceKey.Of(item.SelectableId, instance.ComponentName, instance.InstanceIndex);
            }
        }
    }
}
