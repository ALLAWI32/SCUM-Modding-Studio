using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using ScumStudio.Level.World;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Level.Export;

/// <summary>
/// Loot of the components an export writes: the loot editor's settings and the loot copies carry (written as the
/// component's examine entries), "No loot inside" of placed objects, and the mesh assets' own loot (see
/// <see cref="MeshLoot"/>), which the game finds whatever the component says: a component whose loot is changed and whose
/// mesh carries loot draws a loot-free copy of that mesh, so in game it gives exactly what the studio shows.
/// </summary>
public sealed partial class ProjectExporter
{
    /// <summary>The component of a <c>StaticMeshActor</c> the export creates for an added mesh.</summary>
    private const string StaticMeshActorComponent = "StaticMeshComponent0";

    /// <summary>Depth of child actors followed inside a placed object (a building's locker's own child actors).</summary>
    private const int MaxChildActorDepth = 4;

    /// <summary>
    /// "No loot inside" of the placed actors of <paramref name="level"/>: every searchable part of the copy (its own
    /// components and those of the child actors stored with it, as <c>ChildActorComponent/Component</c>) gets no loot, and
    /// every item spawner component of it no loot points. Loot and point edits of those parts give way. Returns the mesh of
    /// each emptied part (for <see cref="SettleLoot"/>).
    /// </summary>
    private static Dictionary<(string Actor, string Component), string?> PlanNoLoot(
        EditState state, string level, LevelDocument? document, Func<string, LevelDocument?>? sourceDocuments, HashSet<string> deleted, MeshLoot? meshLoot,
        List<LootPatch> loot, List<SpawnPointsPatch> spawnPoints, List<string> warnings)
    {
        var meshes = new Dictionary<(string Actor, string Component), string?>(ActorComponentKey.Comparer);
        foreach (var actor in state.NoLootActors.Where(a => SameLevel(a.Level, level) && !state.IsDeleted(a) && !deleted.Contains(a.Actor))
                     .OrderBy(a => a.Actor, StringComparer.OrdinalIgnoreCase))
        {
            var emptied = new List<(string Component, string? Mesh)>();
            var markers = new List<string>();
            var points = 0;
            var leftOut = new List<string>();
            var unstored = 0;
            switch (state.AddedActors.GetValueOrDefault(actor))
            {
                case AddStaticMeshActorOp add:
                    // A placed mesh: searchable when it carries loot it was copied with, or its model does (or that is not known).
                    var placedMesh = state.GetMeshOverride(actor) ?? add.StaticMesh;
                    if (add.Loot is { Count: > 0 } || meshLoot?.Of(placedMesh).HasExamineData != false)
                    {
                        emptied.Add((StaticMeshActorComponent, placedMesh));
                    }

                    break;
                case DuplicateActorOp duplicate:
                    if (!Walk(document, document?.FindActor(duplicate.Source.Actor)))
                    {
                        continue;
                    }

                    break;
                case AddBlueprintActorOp blueprint:
                    var other = sourceDocuments?.Invoke(blueprint.Source.Level);
                    if (!Walk(other, other?.FindActor(blueprint.Source.Actor)))
                    {
                        continue;
                    }

                    break;
                default:
                    continue;
            }

            // Loot set on the copy (or carried from its source) gives way; a part it was set on is emptied too.
            var set = loot.Where(l => string.Equals(l.Actor, actor.Actor, StringComparison.OrdinalIgnoreCase)).Select(l => l.Component)
                .Where(c => !emptied.Any(e => string.Equals(e.Component, c, StringComparison.OrdinalIgnoreCase))).ToList();
            loot.RemoveAll(l => string.Equals(l.Actor, actor.Actor, StringComparison.OrdinalIgnoreCase));
            foreach (var (component, mesh) in emptied)
            {
                loot.Add(new LootPatch(actor.Actor, component, new LootSetting(false, [])));
                meshes[(actor.Actor, component)] = mesh;
            }

            loot.AddRange(set.Select(component => new LootPatch(actor.Actor, component, new LootSetting(false, []))));

            foreach (var component in markers)
            {
                spawnPoints.RemoveAll(s => string.Equals(s.Actor, actor.Actor, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Component, component, StringComparison.OrdinalIgnoreCase));
                spawnPoints.Add(new SpawnPointsPatch(actor.Actor, component, SpawnPointArrays.SpawnerMarkers, []));
            }

            // A line for the report when there was loot to take out, or some of it stays.
            if (emptied.Count + set.Count + points + leftOut.Count + unstored > 0)
            {
                warnings.Add($"{actor}: no loot inside: {emptied.Count + set.Count} searchable part(s) emptied, {points} loot point(s) taken out."
                    + (leftOut.Count > 0 ? $" Left as the game has them (not stored in the level, or a fixed item's spawner): {string.Join(", ", leftOut.Take(6))}{(leftOut.Count > 6 ? " …" : string.Empty)}." : string.Empty)
                    + (unstored > 0 ? $" {unstored} child object(s) (doors, lockers …) are not stored in the level but made from their class when it loads; any loot they have is their class's." : string.Empty));
            }

            // The copied object's searchable parts and loot points, its stored child actors' as Child/Component.
            bool Walk(LevelDocument? doc, ActorRecord? source)
            {
                if (doc is null || source is null)
                {
                    warnings.Add($"{actor}: the object it copies could not be read, so its loot was left as the game has it (No loot inside not written).");
                    return false;
                }

                if (string.Equals(source.ClassPath, PlacedBlueprintIndex.WorldItemSpawnerClass, StringComparison.Ordinal))
                {
                    warnings.Add($"{actor}: an item spawner is loot itself; No loot inside does not apply to it (delete it instead).");
                    return false;
                }

                Parts(doc, source, string.Empty, 0);
                return true;
            }

            void Parts(LevelDocument doc, ActorRecord record, string prefix, int depth)
            {
                foreach (var c in record.Components)
                {
                    var path = prefix + c.Name;
                    var isRoot = string.Equals(c.Name, record.Root?.Name, StringComparison.OrdinalIgnoreCase);
                    var mesh = prefix.Length == 0 ? state.GetMeshOverride(actor, c.Name) ?? (isRoot ? state.GetMeshOverride(actor) : null) ?? c.StaticMeshPath : c.StaticMeshPath;
                    var searchable = c.IsStaticMeshComponent && (c.LootPresets.Count > 0 || meshLoot?.Of(mesh).HasExamineData == true);
                    if (c.IsSynthesized)
                    {
                        if (searchable || c.SpawnMarkers.Count > 0)
                        {
                            leftOut.Add(path);
                        }

                        continue;
                    }

                    if (c.ChildActor is { } child && depth < MaxChildActorDepth && doc.FindActor(child) is { } childRecord)
                    {
                        Parts(doc, childRecord, path + "/", depth + 1);
                    }
                    else if (c.ChildActor is null && c.ChildActorClassPath is { Length: > 0 } childClass && !childClass.StartsWith("/Script/", StringComparison.Ordinal))
                    {
                        unstored++; // a Blueprint child (a locker, a door) the game makes from its class
                    }

                    if (c.SpawnMarkers.Count > 0)
                    {
                        if (c.SpawnMarkers.Any(m => m.ItemClassPath is not null))
                        {
                            leftOut.Add(path); // a fixed item's spawner (a workbench, a generator): that is the object itself
                        }
                        else
                        {
                            markers.Add(path);
                            points += c.SpawnMarkers.Count;
                        }
                    }

                    if (searchable)
                    {
                        emptied.Add((path, mesh));
                    }
                }
            }
        }

        return meshes;
    }

    /// <summary>
    /// Each loot patch of <paramref name="loot"/> written against its mesh's own loot (<see cref="MeshLoot.Settle"/>): the
    /// component's entries, and the loot-free copy of its mesh where that mesh carries loot of its own (a mesh patch of the
    /// component, or the copy of the user's replaced mesh). A patch that changes nothing is dropped. A copy that cannot be
    /// built leaves the component its entries and its mesh, with a warning (the mesh's own loot then still spawns). Without
    /// <paramref name="meshLoot"/> the patches stay as they are.
    /// </summary>
    private static List<LootPatch> SettleLoot(
        EditState state, string level, LevelDocument? document, Func<string, LevelDocument?>? sourceDocuments, MeshLoot? meshLoot,
        IReadOnlyDictionary<(string Actor, string Component), string?> knownMeshes, List<LootPatch> loot, List<MeshPatch> meshPatches, List<string> warnings)
    {
        if (meshLoot is null)
        {
            return loot;
        }

        var settled = new List<LootPatch>(loot.Count);
        foreach (var patch in loot)
        {
            var reference = new ActorRef(level, patch.Actor);
            string? mesh;
            IReadOnlyList<string>? stored = null;
            if (knownMeshes.TryGetValue((patch.Actor, patch.Component), out var known))
            {
                mesh = known;
            }
            else if (state.AddedActors.GetValueOrDefault(reference) is AddStaticMeshActorOp add)
            {
                mesh = state.GetMeshOverride(reference) ?? add.StaticMesh;
            }
            else if (StoredTarget(state, reference, document, sourceDocuments)?.Record is { } record && record.FindComponent(patch.Component) is { } component)
            {
                var isRoot = string.Equals(component.Name, record.Root?.Name, StringComparison.OrdinalIgnoreCase);
                mesh = state.GetMeshOverride(reference, component.Name) ?? (isRoot ? state.GetMeshOverride(reference) : null) ?? component.StaticMeshPath;
                stored = component.LootPresets;
            }
            else
            {
                settled.Add(patch); // not found: the level editor says so when it writes it
                continue;
            }

            var (entries, copy, noCopy) = meshLoot.Settle(mesh, patch.Loot.Lootable ? patch.Loot.Presets : []);
            if (noCopy)
            {
                warnings.Add($"{reference}.{patch.Component}: its model {mesh} carries loot of its own and no copy of it without that loot could be built "
                    + $"({meshLoot.FailureOf(mesh!)}); in game the model's loot may spawn as well as what was set.");
            }

            if (copy is null && entries.Count == 0 && stored is { Count: 0 })
            {
                continue; // no entries on the component and its mesh gives exactly what is wanted: nothing to write
            }

            settled.Add(patch with { Loot = new LootSetting(entries.Count > 0, entries) });
            if (copy is null)
            {
                continue;
            }

            var at = meshPatches.FindIndex(m => string.Equals(m.Actor, patch.Actor, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(m.Component, patch.Component, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                meshPatches[at] = meshPatches[at] with { StaticMesh = copy }; // the replaced mesh's copy, written as the replacement is
            }
            else
            {
                meshPatches.Add(new MeshPatch(patch.Actor, patch.Component, copy) { IsLootFreeCopy = true });
            }
        }

        return settled;
    }

    /// <summary>
    /// Writes the loot-free copies the levels point at (built from the cook's own meshes while the levels were planned,
    /// <see cref="MeshLoot.Built"/>) into <paramref name="staging"/>, next to the stock mesh (<c>SM_X_NoLoot</c>).
    /// </summary>
    private static async Task<List<ExportedAsset>> WriteLootFreeMeshesAsync(
        Assets.Catalog.AssetCatalog catalog, MeshLoot meshLoot, string staging, List<string> warnings, CancellationToken cancellationToken)
    {
        var written = new List<ExportedAsset>();
        foreach (var (mesh, built) in meshLoot.Built.OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stockPackage = PackageMap.Normalize(mesh);
            try
            {
                if (!catalog.TryGetPackageFile(stockPackage, out var file))
                {
                    warnings.Add($"{mesh}: not found in {catalog.DisplayName}, so its loot-free copy was not written.");
                    continue;
                }

                var stockFile = file.Path.Replace('\\', '/');
                var extension = stockFile[stockFile.LastIndexOf('.')..];
                var virtualPath = stockFile[..(stockFile.LastIndexOf('/') + 1)] + PackageMap.Leaf(built.NewPackagePath) + extension;
                var target = Path.Combine([staging, .. virtualPath[..^extension.Length].Split('/', StringSplitOptions.RemoveEmptyEntries)]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await built.Bytes.WriteAsync(target, extension, cancellationToken).ConfigureAwait(false);
                if (built.UBulk is not null)
                {
                    await File.WriteAllBytesAsync(target + ".ubulk", built.UBulk, cancellationToken).ConfigureAwait(false);
                }

                written.Add(new ExportedAsset(built.NewPackagePath, virtualPath, IsClone: true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"{mesh}: its loot-free copy could not be written ({ex.Message}).");
            }
        }

        return written;
    }

    /// <summary>Case-insensitive (actor, component) keys.</summary>
    private sealed class ActorComponentKey : IEqualityComparer<(string Actor, string Component)>
    {
        public static readonly ActorComponentKey Comparer = new();

        public bool Equals((string Actor, string Component) x, (string Actor, string Component) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Actor, y.Actor) && StringComparer.OrdinalIgnoreCase.Equals(x.Component, y.Component);

        public int GetHashCode((string Actor, string Component) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Actor), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Component));
    }
}
