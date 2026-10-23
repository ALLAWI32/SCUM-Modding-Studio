using ScumStudio.Level.World;

namespace ScumStudio.Level.Editing;

/// <summary>What a reset of a sector takes back, by kind (the counts the Map page shows before it runs).</summary>
public enum SectorResetKind
{
    /// <summary>Deleted objects of the game that come back.</summary>
    RestoredObjects,

    /// <summary>Deleted trees, bushes, rocks and other instances of the game that come back.</summary>
    RestoredInstances,

    /// <summary>Deleted parts of objects that stayed (a fireplace's fire) that come back.</summary>
    RestoredParts,

    /// <summary>Objects and instances of the game moved, turned or scaled that go back to their place.</summary>
    MovedBack,

    /// <summary>Objects the project added (placed, copied, pasted, painted, prefabs, traders, new spawn places) that are removed.</summary>
    RemovedObjects,

    /// <summary>Trees and other instances the project added (copied or painted) that are removed.</summary>
    RemovedInstances,

    /// <summary>Bends and shaped road, rail or bridge pieces that are straightened.</summary>
    Shapes,

    /// <summary>Replaced meshes that are put back.</summary>
    Meshes,

    /// <summary>Loot settings that are put back.</summary>
    Loot,

    /// <summary>"Clear grass under it" settings that are put back.</summary>
    Grass,

    /// <summary>What spawners spawn and their spawn points, put back.</summary>
    Spawners,

    /// <summary>Holes in the ground that are filled.</summary>
    Holes,

    /// <summary>Sub-zones of threat zones that are removed.</summary>
    SubZones,
}

/// <summary>A reset of one sector (see <see cref="EditState.PlanSectorReset"/>): the one edit that does it and what it takes back.</summary>
/// <param name="Cell">The sector.</param>
/// <param name="Op">The edit (one journal step, undone as one), or null when nothing in the sector is changed.</param>
/// <param name="Counts">What it takes back, by kind (kinds with nothing are left out).</param>
/// <param name="KeptCopySources">Objects the project added in the sector that stay: copies elsewhere were made from them.</param>
/// <param name="Unplaced">Changes of island-wide data (no sector of their own) whose place is not known: they stay.</param>
public sealed record SectorResetPlan(MapCell Cell, BatchOp? Op, IReadOnlyDictionary<SectorResetKind, int> Counts, int KeptCopySources, int Unplaced)
{
    /// <summary>Everything it takes back.</summary>
    public int Total => Counts.Values.Sum();
}

public sealed partial class EditState
{
    /// <summary>
    /// The edit that puts one sector of the island back as the game has it (Discord, pheidian: "how do I revert one area
    /// back to default (one sector)?"): every change the project made there is taken back, each by the edit that undoes it
    /// (a restore, a move back, a removal of what was added …), all in ONE <see cref="BatchOp"/>, so History shows one row
    /// and Undo puts everything back as it was. What lies in the sector: everything in its levels (the cell in a level's name,
    /// <see cref="JournalSlice.SectorOf"/>), and of the island-wide data (the spawn places, …) what stands in it — the place
    /// of a new object or spawn place, of a sub-zone, of a hole, or of a game object as <paramref name="locate"/> finds it
    /// (null: unknown, it stays and is counted in <see cref="SectorResetPlan.Unplaced"/>). Vehicle, item and economy edits
    /// belong to no place and stay. An object the project added in the sector that a copy elsewhere was made from stays.
    /// </summary>
    /// <param name="cell">The sector.</param>
    /// <param name="locate">Where a game object stands (world X/Y, cm), when known.</param>
    /// <param name="title">The History row's text (default "Reset sector D_2 to stock").</param>
    public SectorResetPlan PlanSectorReset(MapCell cell, Func<ActorRef, (float X, float Y)?>? locate = null, string? title = null)
    {
        var sector = cell.ToString();
        var levelCells = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var actorIn = new Dictionary<ActorRef, bool?>(ActorRef.Comparer);
        var unplaced = new HashSet<object>();

        // In the sector: a level of its cell, or, for a level of the whole island, a known place inside the cell (null: unknown).
        bool? LevelIn(string level, Func<(float X, float Y)?> where)
        {
            if (!levelCells.TryGetValue(level, out var of))
            {
                of = levelCells[level] = JournalSlice.SectorOf(level);
            }

            return of is not null ? string.Equals(of, sector, StringComparison.OrdinalIgnoreCase)
                : where() is { } p ? IslandMap.CellAt(p.X, p.Y) == cell
                : null;
        }

        bool ActorIn(ActorRef actor)
        {
            if (!actorIn.TryGetValue(actor, out var known))
            {
                known = actorIn[actor] = LevelIn(actor.Level, () =>
                    GetAddedTransform(actor) is { } added ? (added.Location.X, added.Location.Y)
                    : locate?.Invoke(actor) is { } found ? found
                    : _transforms.TryGetValue((actor, string.Empty), out var moved) ? (moved.Base.Location.X, moved.Base.Location.Y)
                    : null);
            }

            if (known is null)
            {
                unplaced.Add(actor);
            }

            return known == true;
        }

        // What the project added in the sector, less what a copy outside it was made from (and what that one was made from).
        var removed = new HashSet<ActorRef>(_added.Keys.Where(ActorIn), ActorRef.Comparer);
        var kept = 0;
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (actor, op) in _added)
            {
                if (removed.Contains(actor) || SourceOf(op) is not { } source || !removed.Remove(source))
                {
                    continue;
                }

                kept++;
                changed = true;
            }
        }

        bool Resets(ActorRef actor) => ActorIn(actor) && (!_added.ContainsKey(actor) || removed.Contains(actor));
        bool Stock(ActorRef actor) => !_added.ContainsKey(actor);
        var counts = new Dictionary<SectorResetKind, int>();
        void Count(SectorResetKind kind, int n = 1)
        {
            if (n > 0)
            {
                counts[kind] = counts.GetValueOrDefault(kind) + n;
            }
        }

        // Each edit below is valid once the ones before it are applied (a batch is checked that way): objects come back before
        // their parts, instances and transforms are set, and what was added goes last, once it is as it was made.
        var ops = new List<EditOp>();
        foreach (var actor in _deletedActors.Where(Resets).OrderBy(a => a.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new RestoreActorOp(actor));
            if (Stock(actor))
            {
                Count(SectorResetKind.RestoredObjects);
            }
        }

        foreach (var group in _deletedComponents.Where(k => Resets(k.Actor)).GroupBy(k => k.Actor, ActorRef.Comparer))
        {
            var parts = group.Select(k => k.Component).Order(StringComparer.OrdinalIgnoreCase).ToList();
            ops.Add(new RestoreComponentsOp(group.Key, parts));
            Count(SectorResetKind.RestoredParts, parts.Count);
        }

        foreach (var instance in _deletedInstances.Where(i => Resets(i.ActorRef)).OrderBy(i => i.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new RestoreInstanceOp(instance));
            if (Stock(instance.ActorRef) && !_addedInstances.ContainsKey(instance))
            {
                Count(SectorResetKind.RestoredInstances);
            }
        }

        var moved = new HashSet<ActorRef>(ActorRef.Comparer);
        foreach (var ((actor, component), value) in _transforms.Where(t => Resets(t.Key.Actor)).OrderBy(t => t.Key.Actor.ToString() + "." + t.Key.Component, StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new SetTransformOp(actor, value.Current, value.Base, component.Length == 0 ? null : component));
            if (Stock(actor) && moved.Add(actor))
            {
                Count(SectorResetKind.MovedBack);
            }
        }

        foreach (var (instance, value) in _instanceTransforms.Where(t => Resets(t.Key.ActorRef)).OrderBy(t => t.Key.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new SetInstanceTransformOp(instance, value.Current, value.Base));
            if (Stock(instance.ActorRef) && !_addedInstances.ContainsKey(instance))
            {
                Count(SectorResetKind.MovedBack);
            }
        }

        foreach (var (actor, bend) in _bends.Where(b => Resets(b.Key)).OrderBy(b => b.Key.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new BendActorOp(actor, bend.Degrees, 0f, bend.Sway1, bend.Sway2, 0f, 0f, bend.Start, bend.End, default, default, bend.Legs, 0f));
            if (Stock(actor))
            {
                Count(SectorResetKind.Shapes);
            }
        }

        foreach (var ((actor, component), shape) in _segmentSways.Where(s => Resets(s.Key.Actor)).OrderBy(s => s.Key.Actor.ToString() + "." + s.Key.Component, StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new SwaySegmentOp(actor, component, shape.Sway1, shape.Sway2, 0f, 0f, shape.Start, shape.End, default, default));
            if (Stock(actor))
            {
                Count(SectorResetKind.Shapes);
            }
        }

        foreach (var ((actor, component, array), points) in _spawnPoints.Where(p => Resets(p.Key.Actor)).OrderBy(p => $"{p.Key.Actor}.{p.Key.Component}.{p.Key.Array}", StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new SetSpawnPointsOp(actor, component.Length == 0 ? null : component, array, points.Current, points.Base));
            if (Stock(actor))
            {
                Count(SectorResetKind.Spawners);
            }
        }

        foreach (var ((actor, component), mesh) in _meshes.Where(m => Resets(m.Key.Actor)).OrderBy(m => m.Key.Actor.ToString() + "." + m.Key.Component, StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new ReplaceMeshOp(actor, component.Length == 0 ? null : component, mesh.Current, mesh.Base));
            if (Stock(actor))
            {
                Count(SectorResetKind.Meshes);
            }
        }

        // A copy set back to its stock loot keeps the setting (F204: else it would get its source's): nothing to take back there.
        foreach (var ((actor, component), loot) in _loot.Where(l => Resets(l.Key.Actor) && !l.Value.Base.Equals(l.Value.Current))
                     .OrderBy(l => l.Key.Actor.ToString() + "." + l.Key.Component, StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new SetLootOp(actor, component.Length == 0 ? null : component, loot.Current, loot.Base));
            if (Stock(actor))
            {
                Count(SectorResetKind.Loot);
            }
        }

        foreach (var (actor, on) in _grass.Where(g => Resets(g.Key)).OrderBy(g => g.Key.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new SetClearGrassOp(actor, on, null));
            if (Stock(actor))
            {
                Count(SectorResetKind.Grass);
            }
        }

        foreach (var ((actor, property), value) in _choices.Where(c => Resets(c.Key.Actor)).OrderBy(c => c.Key.Actor.ToString() + "." + c.Key.Property, StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new SetSpawnChoiceOp(actor, property, value, null));
            if (Stock(actor))
            {
                Count(SectorResetKind.Spawners);
            }
        }

        foreach (var (instance, add) in _addedInstances.Where(a => Resets(a.Key.ActorRef)).OrderBy(a => a.Key.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new RemoveAddedInstanceOp(instance, add));
            Count(SectorResetKind.RemovedInstances);
        }

        foreach (var (actor, add) in _added.Where(a => removed.Contains(a.Key)).OrderBy(a => a.Key.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new RemoveAddedActorOp(actor, add));
            Count(SectorResetKind.RemovedObjects);
        }

        foreach (var hole in _holes.Where(h => IslandMap.CellAt(h.X, h.Y) == cell).ToList())
        {
            ops.Add(new TerrainHoleOp(hole, Cut: false));
            Count(SectorResetKind.Holes);
        }

        foreach (var (zone, subZone) in _subZones.Where(s => IslandMap.CellAt((s.SubZone.Min.X + s.SubZone.Max.X) / 2f, (s.SubZone.Min.Y + s.SubZone.Max.Y) / 2f) == cell).ToList())
        {
            ops.Add(new RemoveSubZoneOp(zone, subZone));
            Count(SectorResetKind.SubZones);
        }

        var batch = ops.Count == 0 ? null : new BatchOp(title ?? $"Reset sector {sector} to stock", ops);
        return new SectorResetPlan(cell, batch, counts, kept, unplaced.Count);

        static ActorRef? SourceOf(EditOp op) => op switch
        {
            DuplicateActorOp d => d.Source,
            AddBlueprintActorOp b => b.Source,
            _ => null,
        };
    }
}
