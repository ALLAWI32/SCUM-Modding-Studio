using ScumStudio.Assets.Catalog;
using ScumStudio.Level.World;

namespace ScumStudio.Level.Editing;

/// <summary>What part of a project's edits is shared: map cells (<c>A_0</c>), the island-wide levels and the asset edits.</summary>
/// <param name="Sectors">Map cells, <c>A_0</c> style.</param>
/// <param name="IslandWide">The levels of no cell (the persistent level, water splines, threat zones …).</param>
/// <param name="Assets">The vehicle and item edits (clones, values, replaced assets), which belong to no level.</param>
public sealed record SectorChoice(IReadOnlySet<string> Sectors, bool IslandWide, bool Assets);

/// <summary>One group of a project's edits and how many edits a share of it carries.</summary>
/// <param name="Key">A map cell (<c>A_0</c>), <see cref="JournalSlice.IslandKey"/> or <see cref="JournalSlice.AssetsKey"/>.</param>
/// <param name="Edits">Edits in the group once compacted (see <see cref="JournalSlice.Take"/>).</param>
public sealed record SliceGroup(string Key, int Edits);

/// <summary>
/// Cuts the edits of a project's journal by map cell for sharing (owner: "share one sector, several sectors or the whole
/// map"). Bulk entries are flattened, a delete-all spanning levels is split per level, and what was made and taken away
/// again inside the slice is left out: the receiver applies the slice as one <see cref="BatchOp"/>, and a batch may not
/// create and remove the same actor (see <see cref="EditState.Validate"/>).
/// </summary>
public static class JournalSlice
{
    /// <summary>Key of the levels that belong to no map cell.</summary>
    public const string IslandKey = "island";

    /// <summary>Key of the edits of vehicles and items (no level).</summary>
    public const string AssetsKey = "assets";

    private const string WorldFolder = "/The_Island/";

    /// <summary>
    /// The map cell of a level package (<c>A_0</c>, from its name as <see cref="WorldNameParser"/> reads it: grid, landscape,
    /// TV base, abandoned city, HLOD and BuiltData names), or null for a level of the whole island.
    /// </summary>
    public static string? SectorOf(string levelPackage)
    {
        ArgumentNullException.ThrowIfNull(levelPackage);
        var package = AssetPaths.SplitObjectPath(levelPackage).PackagePath;
        var slash = package.LastIndexOf('/');
        var name = package[(slash + 1)..];
        var root = package.IndexOf(WorldFolder, StringComparison.OrdinalIgnoreCase);
        var folder = root >= 0 && slash >= root + WorldFolder.Length ? package[(root + WorldFolder.Length)..slash] : string.Empty;
        return WorldNameParser.ClassifyName(package, package, name, folder, isMap: true).Cell?.ToString();
    }

    /// <summary>The group an edit belongs to (a leaf edit: see <see cref="Take"/> for bulk ones).</summary>
    public static string KeyOf(EditOp op)
    {
        ArgumentNullException.ThrowIfNull(op);
        return op switch
        {
            CloneAssetOp or RemoveAssetCloneOp or SetAssetValueOp or ReplaceAssetOp => AssetsKey,
            AddStaticMeshActorOp add => LevelKey(add.Created.Level),
            AddBlueprintActorOp bp => LevelKey(bp.Created.Level),
            DuplicateActorOp dup => LevelKey(dup.Created.Level),
            RemoveAddedActorOp remove => LevelKey(remove.Target.Level),
            RemoveAddedInstanceOp remove => LevelKey(remove.Target.Level),
            _ => op.GetTouchedLevels() is [var level, ..] ? LevelKey(level) : op.GetTouchedAssets().Count > 0 ? AssetsKey : IslandKey,
        };
    }

    /// <summary>Edits per group of <paramref name="ops"/> (leaf edits), cells first in map order, then the island, then assets.</summary>
    public static IReadOnlyDictionary<string, int> Count(IEnumerable<EditOp> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);
        var counts = ops.SelectMany(Leaves).SelectMany(SplitPerLevel).GroupBy(KeyOf, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => Order(g.Key)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in counts)
        {
            result[group.Key] = group.Count();
        }

        return result;
    }

    /// <summary>The groups a project's applied edits fall into, with what a share of each would carry.</summary>
    public static IReadOnlyList<SliceGroup> Groups(IReadOnlyList<JournalEntry> applied)
    {
        ArgumentNullException.ThrowIfNull(applied);
        return Count(Compact(Flatten(applied))).Select(g => new SliceGroup(g.Key, g.Value)).ToList();
    }

    /// <summary>
    /// The edits of the chosen groups as one batch, in journal order. An actor or instance made and removed again is left
    /// out with every edit of it in between. A copy of an actor another group added pulls that group in (and a placed item
    /// of a cloned vehicle or item the assets), listed in <paramref name="pulledIn"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The slice is empty, or it does not apply to an untouched island.</exception>
    public static BatchOp Take(IReadOnlyList<JournalEntry> applied, SectorChoice choice, string title, out IReadOnlyList<string> pulledIn)
    {
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var ops = Compact(Flatten(applied));
        var keys = ops.Select(KeyOf).ToList();
        var chosen = new HashSet<string>(choice.Sectors.Select(s => MapCell.TryParse(s, out var cell) ? cell.ToString() : s), StringComparer.OrdinalIgnoreCase);
        if (choice.IslandWide)
        {
            chosen.Add(IslandKey);
        }

        if (choice.Assets)
        {
            chosen.Add(AssetsKey);
        }

        var pulled = new List<string>();
        for (var changed = true; changed;)
        {
            changed = false;
            var creators = new Dictionary<ActorRef, int>(ActorRef.Comparer);
            var clones = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < ops.Count; i++)
            {
                if (chosen.Contains(keys[i]))
                {
                    foreach (var need in Needs(ops[i], creators, clones).Where(n => !chosen.Contains(keys[n])))
                    {
                        chosen.Add(keys[need]);
                        pulled.Add(keys[need]);
                        changed = true;
                    }
                }

                Remember(ops[i], i, creators, clones);
            }
        }

        var batch = new BatchOp(title, ops.Where((_, i) => chosen.Contains(keys[i])).ToList());
        if (batch.Ops.Count == 0)
        {
            throw new InvalidDataException("The chosen sectors hold no edits.");
        }

        if (new EditState().Validate(batch) is { } error)
        {
            throw new InvalidDataException($"The chosen edits do not apply on their own: {error}");
        }

        pulledIn = pulled.OrderBy(Order).ThenBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        return batch;
    }

    /// <summary>Sort rank of a group key: cells in map order, then the island, then assets.</summary>
    public static int Order(string key) =>
        MapCell.TryParse(key, out var cell) && key.Length == 3 ? (MapCell.Columns.ToList().IndexOf(cell.Column) * MapCell.RowCount) + cell.Row
        : string.Equals(key, IslandKey, StringComparison.OrdinalIgnoreCase) ? 100 : 101;

    private static string LevelKey(string level) => SectorOf(level) ?? IslandKey;

    private static IEnumerable<EditOp> Leaves(EditOp op) => op is BatchOp batch ? batch.Ops.SelectMany(Leaves) : [op];

    // An economy edit (EconomyOverride.json, a project file) is no map or asset edit: it fell into the island group, so an
    // island-wide share carried it, although a share takes the economy as a file of its own (ShareImport.SharedEconomy).
    private static List<EditOp> Flatten(IReadOnlyList<JournalEntry> applied) =>
        applied.SelectMany(e => Leaves(e.Op)).Where(o => o is not EconomyOp).SelectMany(SplitPerLevel).ToList();

    /// <summary>A delete-all (or its restore) of several levels as one edit per level, each in its own cell's group.</summary>
    private static IEnumerable<EditOp> SplitPerLevel(EditOp op)
    {
        if (op is not (DeleteAllOfKindOp or RestoreAllOfKindOp) || op.GetTouchedLevels() is not { Count: > 1 } levels)
        {
            return [op];
        }

        var (actors, instances) = BulkTargets(op);
        var actorsBy = actors.ToLookup(a => a.Level, StringComparer.OrdinalIgnoreCase);
        var instancesBy = instances.ToLookup(i => i.Level, StringComparer.OrdinalIgnoreCase);
        return levels.Select(l => op switch
        {
            DeleteAllOfKindOp all => all with { Actors = actorsBy[l].ToList(), Instances = instancesBy[l].ToList() },
            RestoreAllOfKindOp all => (EditOp)(all with { Actors = actorsBy[l].ToList(), Instances = instancesBy[l].ToList() }),
            _ => op,
        }).ToList();
    }

    /// <summary>
    /// Leaves out every actor and instance created and removed again: its creator, its remover and each edit of it in
    /// between (a delete-all only loses that target).
    /// </summary>
    private static List<EditOp> Compact(List<EditOp> ops)
    {
        var drop = new bool[ops.Count];
        var trimActors = new Dictionary<int, HashSet<ActorRef>>();
        var trimInstances = new Dictionary<int, HashSet<InstanceRef>>();
        var actors = new Dictionary<ActorRef, Life>(ActorRef.Comparer);
        var instances = new Dictionary<InstanceRef, Life>(InstanceRef.Comparer);
        var owned = new Dictionary<ActorRef, List<InstanceRef>>(ActorRef.Comparer);

        for (var i = 0; i < ops.Count; i++)
        {
            switch (ops[i])
            {
                case AddStaticMeshActorOp or AddBlueprintActorOp or DuplicateActorOp:
                    actors[CreatedBy(ops[i])!] = new Life(i);
                    break;
                case AddInstanceOp add:
                    instances[add.Target] = new Life(i);
                    (owned.TryGetValue(add.Target.ActorRef, out var trees) ? trees : owned[add.Target.ActorRef] = []).Add(add.Target);
                    Attach(i, add.Target.ActorRef, null);
                    break;
                case RemoveAddedActorOp remove when actors.Remove(remove.Target, out var life):
                    life.Ops.Add(i);
                    End(life);
                    foreach (var tree in owned.Remove(remove.Target, out var ownedTrees) ? ownedTrees : [])
                    {
                        if (instances.Remove(tree, out var treeLife))
                        {
                            End(treeLife);
                        }
                    }

                    break;
                case RemoveAddedInstanceOp remove when instances.Remove(remove.Target, out var life):
                    life.Ops.Add(i);
                    End(life);
                    break;
                case DeleteAllOfKindOp or RestoreAllOfKindOp:
                    var (bulkActors, bulkInstances) = BulkTargets(ops[i]);
                    foreach (var actor in bulkActors)
                    {
                        Attach(i, actor, null, trim: true);
                    }

                    foreach (var instance in bulkInstances)
                    {
                        Attach(i, instance.ActorRef, instance, trim: true);
                    }

                    break;
                default:
                    if (ops[i].GetPrimaryTarget() is { } target)
                    {
                        Attach(i, target, InstanceTarget(ops[i]));
                    }

                    break;
            }
        }

        var result = new List<EditOp>(ops.Count);
        for (var i = 0; i < ops.Count; i++)
        {
            if (drop[i])
            {
                continue;
            }

            if (!trimActors.ContainsKey(i) && !trimInstances.ContainsKey(i))
            {
                result.Add(ops[i]);
                continue;
            }

            var goneActors = trimActors.GetValueOrDefault(i) ?? [];
            var goneInstances = trimInstances.GetValueOrDefault(i) ?? [];
            bool Keep(InstanceRef x) => !goneInstances.Contains(x) && !goneActors.Contains(x.ActorRef);
            var trimmed = ops[i] switch
            {
                DeleteAllOfKindOp all => all with { Actors = all.Actors.Where(a => !goneActors.Contains(a)).ToList(), Instances = all.Instances.Where(Keep).ToList() },
                RestoreAllOfKindOp all => (EditOp)(all with { Actors = all.Actors.Where(a => !goneActors.Contains(a)).ToList(), Instances = all.Instances.Where(Keep).ToList() }),
                _ => ops[i],
            };
            if (BulkTargets(trimmed) is not ({ Count: 0 }, { Count: 0 }))
            {
                result.Add(trimmed);
            }
        }

        return result;

        // An edit of an actor (or of one of its instances) made inside the slice belongs to that actor's life.
        void Attach(int op, ActorRef actor, InstanceRef? instance, bool trim = false)
        {
            if (actors.TryGetValue(actor, out var life))
            {
                if (trim)
                {
                    life.Trims.Add((op, actor, null));
                }
                else
                {
                    life.Ops.Add(op);
                }
            }

            if (instance is not null && instances.TryGetValue(instance, out var instanceLife))
            {
                if (trim)
                {
                    instanceLife.Trims.Add((op, null, instance));
                }
                else
                {
                    instanceLife.Ops.Add(op);
                }
            }
        }

        void End(Life life)
        {
            foreach (var op in life.Ops)
            {
                drop[op] = true;
            }

            foreach (var (op, actor, instance) in life.Trims)
            {
                if (actor is not null)
                {
                    (trimActors.TryGetValue(op, out var set) ? set : trimActors[op] = new HashSet<ActorRef>(ActorRef.Comparer)).Add(actor);
                }

                if (instance is not null)
                {
                    (trimInstances.TryGetValue(op, out var set) ? set : trimInstances[op] = new HashSet<InstanceRef>(InstanceRef.Comparer)).Add(instance);
                }
            }
        }
    }

    private static ActorRef? CreatedBy(EditOp op) => op switch
    {
        AddStaticMeshActorOp add => add.Created,
        AddBlueprintActorOp bp => bp.Created,
        DuplicateActorOp dup => dup.Created,
        _ => null,
    };

    private static InstanceRef? InstanceTarget(EditOp op) => op switch
    {
        SetInstanceTransformOp set => set.Target,
        DeleteInstanceOp delete => delete.Target,
        RestoreInstanceOp restore => restore.Target,
        _ => null,
    };

    private static (IReadOnlyList<ActorRef> Actors, IReadOnlyList<InstanceRef> Instances) BulkTargets(EditOp op) => op switch
    {
        DeleteAllOfKindOp all => (all.Actors, all.Instances),
        RestoreAllOfKindOp all => (all.Actors, all.Instances),
        _ => ([], []),
    };

    /// <summary>Indices of earlier edits <paramref name="op"/> cannot do without: the maker of the actor it copies, the clone it places.</summary>
    private static IEnumerable<int> Needs(EditOp op, Dictionary<ActorRef, int> creators, Dictionary<string, int> clones)
    {
        var sources = op switch
        {
            AddBlueprintActorOp bp => new[] { bp.Source },
            DuplicateActorOp dup => [dup.Source],
            _ => [],
        };
        foreach (var source in sources)
        {
            if (creators.TryGetValue(source, out var maker))
            {
                yield return maker;
            }
        }

        var used = op switch
        {
            AddBlueprintActorOp bp => new[] { bp.ClassPath, bp.Item },
            AddStaticMeshActorOp add => [add.StaticMesh],
            ReplaceMeshOp mesh => [mesh.New],
            _ => [],
        };
        foreach (var path in used.OfType<string>())
        {
            if (clones.TryGetValue(AssetPaths.SplitObjectPath(path).PackagePath, out var clone))
            {
                yield return clone;
            }
        }
    }

    private static void Remember(EditOp op, int index, Dictionary<ActorRef, int> creators, Dictionary<string, int> clones)
    {
        if (CreatedBy(op) is { } created)
        {
            creators[created] = index;
        }
        else if (op is RemoveAddedActorOp remove)
        {
            creators.Remove(remove.Target);
        }
        else if (op is CloneAssetOp clone)
        {
            foreach (var package in clone.Packages)
            {
                clones[package.New] = index;
            }
        }
        else if (op is RemoveAssetCloneOp { Original: CloneAssetOp removed })
        {
            foreach (var package in removed.Packages)
            {
                clones.Remove(package.New);
            }
        }
    }

    private sealed class Life(int creator)
    {
        public List<int> Ops { get; } = [creator];

        public List<(int Op, ActorRef? Actor, InstanceRef? Instance)> Trims { get; } = [];
    }
}
