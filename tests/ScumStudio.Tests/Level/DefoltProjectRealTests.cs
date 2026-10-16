using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using CookedPackage = ScumStudio.Formats.Packages.CookedPackage;

namespace ScumStudio.Tests.Level;

/// <summary>
/// D05 on the reporter's own project (Discord defolt_0999, "flickering trees and bushes", 2026-10-13): his journal is
/// replayed and exported, then every foliage/HISM component his edits changed in <c>Landscape_Z_4_1b</c> and <c>_1d</c> is
/// read back the way UE 4.27 loads it (<see cref="HismTail"/>, as <see cref="FoliageEditRenderDataRealTests"/> does): the
/// cooked draw copy is there and equal to the instances, every instance sits inside its cluster box, the reorder table is
/// a permutation. Runs only when <c>SCUM_DEFOLT_PROJECT</c> names his project folder (or a folder holding several project
/// folders) and <c>SCUM_PAKS</c> the game; his data is a user's project and is never committed.
/// </summary>
public sealed class DefoltProjectRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly string[] Tiles = [MapSlice.MapsPath + "Landscape_Z_4_1b", MapSlice.MapsPath + "Landscape_Z_4_1d"];

    [Fact]
    public async Task HisFoliageEditsKeepAValidDrawCopyAndClusterTree()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks)
            || Environment.GetEnvironmentVariable("SCUM_DEFOLT_PROJECT") is not { Length: > 0 } root || !Directory.Exists(root))
        {
            return; // needs the game and his project
        }

        var projects = Project.Exists(root) ? new[] { root } : Directory.GetDirectories(root).Where(Project.Exists).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(projects);
        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var meshes = new BendSupport(catalog);
        var problems = new List<string>();
        foreach (var source in projects)
        {
            // A copy: his folder is never opened for writing.
            using var temp = new LevelTempDirectory();
            var copy = temp.Combine(Path.GetFileName(source));
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            }

            using var project = Project.OpenReadOnly(copy);
            var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
            output.WriteLine($"{Path.GetFileName(source)}: {result.Levels.Count} level(s) rewritten, {result.Warnings.Count} warning(s)");
            foreach (var w in result.Warnings)
            {
                output.WriteLine("  warning: " + w);
            }

            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            foreach (var tile in Tiles)
            {
                if (!result.Levels.Any(l => string.Equals(l.PackagePath, tile, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"{Path.GetFileName(source)}: {tile} was not rewritten");
                    continue;
                }

                CheckTile(tile, catalog, written, meshes, problems);
            }
        }

        foreach (var p in problems)
        {
            output.WriteLine("  " + p);
        }

        Assert.Empty(problems);
    }

    private void CheckTile(string tile, AssetCatalog catalog, AssetCatalog written, BendSupport meshes, List<string> problems)
    {
        var stockDoc = LevelDocument.Load(new Cue4ParseLevelReader(catalog), tile);
        var afterDoc = LevelDocument.Load(new Cue4ParseLevelReader(written), tile);
        var stockPackage = ModdableAssets.ReadPackage(catalog, tile);
        var writtenPackage = ModdableAssets.ReadPackage(written, tile);
        int touched = 0, untouched = 0, skipped = 0, outside = 0, instances = 0, edited = 0, copies = 0, stockOdd = 0;
        foreach (var actor in stockDoc.Actors.Where(a => a.InstanceTransforms.Count > 0))
        {
            var actorAfter = afterDoc.Actors.SingleOrDefault(a => a.Name == actor.Name);
            foreach (var group in actor.InstanceTransforms.GroupBy(i => i.ComponentName))
            {
                var stockIndex = Find(stockPackage, group.Key, actor.Name);
                if (stockIndex < 0 || actorAfter is null)
                {
                    outside++; // a component of the actor's blueprint (not stored in the level), or an actor his edits deleted
                    continue;
                }

                var index = Find(writtenPackage, group.Key, actor.Name);
                if (index < 0)
                {
                    problems.Add($"{tile} {actor.Name}.{group.Key}: not found after the export");
                    continue;
                }

                if (stockPackage.GetExportBytes(stockIndex).AsSpan().SequenceEqual(writtenPackage.GetExportBytes(index)))
                {
                    untouched++;
                    continue;
                }

                var className = stockPackage.GetExportClassName(stockIndex);
                if (!className.Contains("Foliage", StringComparison.Ordinal) && !className.Contains("Hierarchical", StringComparison.Ordinal))
                {
                    skipped++; // a plain ISM has no cluster tree
                    output.WriteLine($"  {tile.Split('/')[^1]} {actor.Name}.{group.Key} ({className}): changed, not a HISM");
                    continue;
                }

                touched++;
                var component = actor.FindComponent(group.Key);
                var mesh = component?.StaticMeshPath is { } path ? meshes.Describe(path)?.Bounds : null;
                var before = group.OrderBy(i => i.InstanceIndex).Select(i => i.LocalTransform).ToList();
                var now = actorAfter.InstanceTransforms.Where(i => i.ComponentName == group.Key).OrderBy(i => i.InstanceIndex).Select(i => i.LocalTransform).ToList();
                if (before.Count != now.Count)
                {
                    problems.Add($"{tile} {group.Key}: {before.Count} instances became {now.Count}");
                    continue;
                }

                var changed = Enumerable.Range(0, now.Count).Count(i => !now[i].Equals(before[i], 0.01f));
                var stock = Check(stockPackage, stockIndex, before, mesh, requireCopy: false);
                var after = Check(writtenPackage, index, now, mesh, requireCopy: true);
                output.WriteLine($"  {tile.Split('/')[^1]} {group.Key} ({className}, {component?.StaticMeshPath?.Split('/')[^1]}): {now.Count} inst, {changed} edited; stock {stock.Summary}; written {after.Summary}");
                if (stock.Problems.Count > 0)
                {
                    stockOdd++;
                    output.WriteLine("    the game's own data: " + string.Join("; ", stock.Problems.Take(3)));
                }

                problems.AddRange(after.Problems.Select(p => $"{tile.Split('/')[^1]} {group.Key}: {p}"));
                instances += now.Count;
                edited += changed;
                copies += after.CopyBytes > 0 ? 1 : 0;
            }
        }

        output.WriteLine($"{tile.Split('/')[^1]}: {touched} HISM component(s) changed ({instances} instances, {edited} edited, {copies} with a draw copy), "
            + $"{untouched} unchanged, {skipped} changed non-HISM, {outside} not in the level (blueprint-owned or actor deleted), {stockOdd} with stock oddities; exports {stockPackage.Exports.Count} -> {writtenPackage.Exports.Count}");
    }

    private static int Find(CookedPackage package, string component, string actor)
    {
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (package.ResolveName(package.Exports[i].ObjectName) == component && package.GetObjectName(package.Exports[i].OuterIndex) == actor)
            {
                return i;
            }
        }

        return -1;
    }

    private static HismTail.Report Check(CookedPackage package, int index, IReadOnlyList<FTransform> instances, ScumStudio.Core.Geometry.BoundingBox? mesh, bool requireCopy)
    {
        var payload = package.GetExportBytes(index);
        var block = package.ReadProperties(index);
        var (offset, size) = LevelPackageEditor.FindInstanceArray(payload, block.EndOffset, instances) ?? throw new InvalidOperationException("instance array not found");
        var reorder = block.Find("InstanceReorderTable")?.Value is ArrayValue table ? table.Items.OfType<IntValue>().Select(v => v.Value).ToList() : null;
        return HismTail.Check(payload, offset + (size * instances.Count), instances, reorder, mesh, requireCopy);
    }
}
