using System.Numerics;
using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;

namespace ScumStudio.Level.Export;

public sealed partial class ProjectExporter
{
    /// <summary>
    /// The footprints (with their heights) of a placed or moved actor as written whose grass and spawns are cleared
    /// (<see cref="EditState.ClearsGrass"/>), none otherwise. A copied Blueprint is written without its class's mesh components (a stock one stores them in the
    /// level, 42 of 42 in A_4_Farm_04): they are taken from the actor it was copied from (or itself where it stood), read
    /// with its class components, and put at its root as written.
    /// </summary>
    private Func<string, ActorRecord, IEnumerable<GrassClearing.Volume>> GrassFootprints(EditState state, AssetCatalog catalog, Func<string, BendMesh?> meshes)
    {
        Cue4ParseLevelReader? expanded = null;
        var documents = new Dictionary<string, LevelDocument?>(StringComparer.OrdinalIgnoreCase);
        return (level, actor) =>
        {
            var reference = new ActorRef(level, actor.Name);
            if (!state.ClearsGrass(reference))
            {
                return [];
            }

            var own = GrassClearing.Volumes(actor, m => meshes(m)?.Bounds);
            var source = state.AddedActors.GetValueOrDefault(reference) switch
            {
                AddBlueprintActorOp b => b.Source,
                DuplicateActorOp d => d.Source,
                null => reference,
                _ => null,
            };
            if (own.Count > 0 || !actor.ClassPath.EndsWith("_C", StringComparison.Ordinal) || source is null || state.IsAdded(source))
            {
                return own; // ponytail: a copy of a copied Blueprint has no stock source to read its class meshes from
            }

            if (!documents.TryGetValue(source.Level, out var document))
            {
                try
                {
                    document = LevelDocument.Load(expanded ??= new Cue4ParseLevelReader(catalog, null, _logger), source.Level);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException)
                {
                    document = null;
                }

                documents[source.Level] = document;
            }

            return document?.FindActor(source.Actor) is { } stood
                ? GrassClearing.Volumes(stood with
                {
                    Components = stood.Components.Select(c => c with { WorldTransform = c.WorldTransform.GetRelativeTransform(stood.WorldTransform) * actor.WorldTransform }).ToList(),
                }, m => meshes(m)?.Bounds)
                : own;
        };
    }

    /// <summary>
    /// "Clear grass under it" (see <see cref="GrassClearing"/>): every landscape tile under a footprint becomes a changed
    /// level. Its bushes and grass instances standing inside a footprint are deleted (written like the editor's instance
    /// deletes, on top of the tile as this export already wrote it, if it did), and its components' cooked grass densities
    /// are zeroed under the footprints in place. Nothing is journaled: the setting on the objects is the edit.
    /// </summary>
    private void ClearGrassUnder(AssetCatalog catalog, Cue4ParseLevelReader reader, List<Vector2[]> footprints, string staging,
        List<ExportedLevel> levels, List<string> warnings, CancellationToken cancellationToken)
    {
        var boxes = footprints.Select(p => (Poly: p, Min: new Vector2(p.Min(v => v.X), p.Min(v => v.Y)), Max: new Vector2(p.Max(v => v.X), p.Max(v => v.Y)))).ToList();
        using var written = AssetCatalog.OpenLoose(staging);
        var writtenReader = new Cue4ParseLevelReader(written, reader.Options, _logger);
        foreach (var (tile, _) in GroundHeights.LandscapeTiles(catalog).Where(t => boxes.Any(b =>
                     t.Tile.BoundsMin.X <= b.Max.X && b.Min.X <= t.Tile.BoundsMax.X && t.Tile.BoundsMin.Y <= b.Max.Y && b.Min.Y <= t.Tile.BoundsMax.Y)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The tile as this export already wrote it (instance edits of the project), else the game's.
                var already = written.TryGetPackageFile(tile, out var stagedFile);
                var source = already ? written : catalog;
                var file = already ? stagedFile : catalog.TryGetPackageFile(tile, out var stock) ? stock : null;
                if (file is null)
                {
                    continue;
                }

                // The foliage: bushes and grass whose base stands inside a footprint. Read first: the reader maps SCUM's own
                // foliage classes before the package is loaded and cached (the landscape read below would cache it without).
                var document = LevelDocument.Load(already ? writtenReader : reader, tile, cancellationToken);
                var foliage = new EditState();
                foreach (var actor in document.Actors)
                {
                    foreach (var instance in actor.InstanceTransforms)
                    {
                        var at = new Vector2(instance.WorldTransform.Translation.X, instance.WorldTransform.Translation.Y);
                        if (actor.FindComponent(instance.ComponentName) is { } component && GrassClearing.IsClearedFoliage(component)
                            && boxes.Any(b => at.X >= b.Min.X && at.X <= b.Max.X && at.Y >= b.Min.Y && at.Y <= b.Max.Y && GrassClearing.Contains(b.Poly, at)))
                        {
                            foliage.Apply(new DeleteInstanceOp(new InstanceRef(tile, actor.Name, instance.ComponentName, instance.InstanceIndex)));
                        }
                    }
                }

                // The grass: each component's footprints in its quad coordinates.
                var perComponent = new Dictionary<string, (int Stride, List<Vector2[]> Polygons)>(StringComparer.Ordinal);
                foreach (var surface in LandscapeExtractor.Extract(catalog, tile, new LandscapeExtractOptions { ReadGrass = false, ComputeNormals = false, PackedNormals = false })
                             .SelectMany(p => p.Components).Select(c => c.Surface).OfType<LandscapeSurface>())
                {
                    var reach = new Vector2(surface.QuadSizeCm);
                    var near = boxes.Where(b => b.Min.X <= surface.WorldMaxXY.X + reach.X && surface.WorldMinXY.X - reach.X <= b.Max.X
                                                && b.Min.Y <= surface.WorldMaxXY.Y + reach.Y && surface.WorldMinXY.Y - reach.Y <= b.Max.Y).ToList();
                    if (near.Count > 0)
                    {
                        perComponent[surface.Name] = (surface.SampleCount, near.Select(b => b.Poly.Select(v =>
                        {
                            surface.TryGetQuadCoordinates(v.X, v.Y, out var qx, out var qy);
                            return new Vector2(qx, qy);
                        }).ToArray()).ToList());
                    }
                }

                if (perComponent.Count == 0 && foliage.IsEmpty)
                {
                    continue;
                }

                var (uasset, uexp, ubulk) = ReadPackageFiles(source, file);
                var request = foliage.IsEmpty ? null : PlanLevel(foliage, tile, document, warnings);
                var (bytes, report) = request is null
                    ? (new PackageBytes(uasset, uexp), new LevelEditReport
                    {
                        LevelExport = "PersistentLevel", ActorsBefore = document.Actors.Count, ActorsAfter = document.Actors.Count,
                        RemovedActors = [], PatchedTransforms = [], AddedNames = [], Warnings = [],
                    })
                    : LevelPackageEditor.Apply(CookedPackage.Parse(uasset, uexp, ubulk, tile), request);

                var cleared = 0;
                var package = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, tile);
                for (var i = 0; i < package.Exports.Count; i++)
                {
                    if (package.GetExportClassName(i) != "LandscapeComponent" || package.GetObjectName(i + 1) is not { } name
                        || !perComponent.TryGetValue(name, out var target))
                    {
                        continue;
                    }

                    var block = package.ReadProperties(i);
                    var payload = bytes.UExp.AsSpan(block.UExpBaseOffset, block.PayloadLength);
                    if (GrassClearing.ReadGrassMaps(payload, block.EndOffset, target.Stride) is not { } maps)
                    {
                        warnings.Add($"{tile}: {name}: its grass data is not laid out as UE 4.27 cooks it; the grass under the placed objects there stays.");
                        continue;
                    }

                    cleared += GrassClearing.Clear(payload, maps, GrassClearing.ClearedSamples(target.Stride, target.Polygons));
                }

                if (cleared == 0 && request is null)
                {
                    continue;
                }

                var virtualPath = file.Path.Replace('\\', '/');
                var dot = virtualPath.LastIndexOf('.');
                var target0 = Path.Combine([staging, .. virtualPath[..dot].Split('/', StringSplitOptions.RemoveEmptyEntries)]);
                Directory.CreateDirectory(Path.GetDirectoryName(target0)!);
                File.WriteAllBytes(target0 + virtualPath[dot..], bytes.UAsset);
                File.WriteAllBytes(target0 + ".uexp", bytes.UExp);
                if (ubulk is not null && !already)
                {
                    File.WriteAllBytes(target0 + ".ubulk", ubulk);
                }

                var previous = levels.FindIndex(l => SameLevel(l.PackagePath, tile));
                var merged = report with
                {
                    ClearedGrassSamples = cleared,
                    DeletedInstances = report.DeletedInstances + (previous >= 0 ? levels[previous].Report.DeletedInstances : 0),
                };
                if (previous >= 0)
                {
                    levels[previous] = levels[previous] with { Report = merged with { RemovedActors = levels[previous].Report.RemovedActors, PatchedTransforms = levels[previous].Report.PatchedTransforms } };
                }
                else
                {
                    levels.Add(new ExportedLevel(tile, virtualPath, merged));
                }

                _logger.LogInformation("{Level}: grass cleared under placed objects ({Samples} density sample(s), {Instances} bush/grass instance(s)).",
                    tile, cleared, report.DeletedInstances);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                warnings.Add($"{tile}: the grass under the placed objects could not be cleared ({ex.Message}).");
            }
        }
    }
}
