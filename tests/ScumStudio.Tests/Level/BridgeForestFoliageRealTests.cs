using System.Buffers.Binary;
using System.Numerics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;
using BoundingBox = ScumStudio.Core.Geometry.BoundingBox;
using CookedPackage = ScumStudio.Formats.Packages.CookedPackage;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// "Flickering trees and bushes near the bridge" (Discord defolt_0999, 2026-10-12, 3.1.0): a long bridge placed across the
/// forest of a Z_4 landscape tile, exported with "Clear grass under it" on, and the tile read back the way the game reads
/// its foliage HISMs (cluster trees against their instances, the cooked draw copy against the reorder table). Real game
/// files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class BridgeForestFoliageRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Tile = MapSlice.MapsPath + "Landscape_Z_4_2";
    private const string Host = MapSlice.MapsPath + "Z_4_Road_Work_01";
    private const string Bridge = "/Game/ConZ_Files/Models/Road/DrTudmanBridge/DrT_B_Meshes/SM_Tudman_Bridge_01.SM_Tudman_Bridge_01";

    [Fact]
    public async Task ABridgeAcrossZ4ForestClearsTheGrassAndLeavesTheFoliageAsCooked()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var tile = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Tile); // first: maps SCUM's foliage classes
        var bounds = new BendSupport(catalog);

        // The densest tree stand of the tile (100 m cells), and the bridge laid across it.
        var trees = tile.Actors.SelectMany(a => a.InstanceTransforms.Where(i => a.FindComponent(i.ComponentName)?.StaticMeshPath?.Contains("/Trees/", StringComparison.OrdinalIgnoreCase) == true)).ToList();
        var cell = trees.GroupBy(t => ((int)MathF.Floor(t.WorldTransform.Translation.X / 10_000f), (int)MathF.Floor(t.WorldTransform.Translation.Y / 10_000f))).MaxBy(g => g.Count())!;
        var at = new FVector(cell.Average(t => t.WorldTransform.Translation.X), cell.Average(t => t.WorldTransform.Translation.Y), cell.Max(t => t.WorldTransform.Translation.Z) + 500f);
        var mesh = bounds.Describe(Bridge)!.Bounds;
        output.WriteLine($"{trees.Count} tree instances; densest cell {cell.Count()} at ({at.X:0}, {at.Y:0}); bridge local bounds {mesh.Min} .. {mesh.Max}");

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Flicker.ssproj"), "Flicker");
        var host = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Host);
        var add = EditOpFactory.AddStaticMeshActor(host, Bridge, new TransformValue(at, new FRotator(0, 0, 0), FVector.One), project.State);
        project.Apply(add);
        Assert.True(project.State.ClearsGrass(add.Created));
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        foreach (var l in result.Levels)
        {
            output.WriteLine($"written {l.PackagePath}: deleted {l.Report.DeletedInstances}, grass {l.Report.ClearedGrassSamples}");
        }

        foreach (var w in result.Warnings)
        {
            output.WriteLine("warning: " + w);
        }

        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var after = LevelDocument.Load(new Cue4ParseLevelReader(written), Tile);
        var stockPackage = ModdableAssets.ReadPackage(catalog, Tile);
        var writtenPackage = ModdableAssets.ReadPackage(written, Tile);
        // The forest is far out of the road works' reach (its level streams within 50 m): the bridge went into the forest's
        // own terrain tile (FarPlacement), its actor and mesh component appended behind the tile's cooked exports.
        Assert.Equal(Tile, Assert.Single(result.FarPlaced).To.Level);
        Assert.DoesNotContain(result.Levels, l => l.PackagePath == Host);
        Assert.Equal(stockPackage.Exports.Count + 2, writtenPackage.Exports.Count);
        output.WriteLine($"far models: {string.Join(", ", result.FarModels)}");
        output.WriteLine($"tile stock:   {TileInfo(stockPackage)}");
        output.WriteLine($"tile written: {TileInfo(writtenPackage)}");
        output.WriteLine($"host stock:   {TileInfo(ModdableAssets.ReadPackage(catalog, Host))}");
        var level = Assert.Single(result.Levels, l => l.PackagePath == Tile);
        Assert.True(level.Report.ClearedGrassSamples > 0, "the grass under the bridge was not cleared");
        Assert.Equal(0, level.Report.DeletedInstances);

        var problems = new List<string>();
        var changed = new List<string>();
        var collapsedTrees = 0;
        var collapsed = 0;
        foreach (var actor in tile.Actors.Where(a => a.InstanceTransforms.Count > 0))
        {
            var actorAfter = after.Actors.Single(a => a.Name == actor.Name);
            foreach (var group in actor.InstanceTransforms.GroupBy(i => i.ComponentName))
            {
                var component = actor.FindComponent(group.Key)!;
                var index = Enumerable.Range(0, writtenPackage.Exports.Count).Single(i => writtenPackage.ResolveName(writtenPackage.Exports[i].ObjectName) == group.Key
                    && writtenPackage.GetExportClassName(i) == stockPackage.GetExportClassName(i));
                var stockBytes = stockPackage.GetExportBytes(index);
                var writtenBytes = writtenPackage.GetExportBytes(index);
                var now = actorAfter.InstanceTransforms.Where(i => i.ComponentName == group.Key).OrderBy(i => i.InstanceIndex).ToList();
                var gone = now.Count(i => MathF.Abs(i.LocalTransform.Scale3D.X) < 0.01f) - group.Count(i => MathF.Abs(i.LocalTransform.Scale3D.X) < 0.01f);
                collapsed += gone;
                var isTree = component.StaticMeshPath?.Contains("/Trees/", StringComparison.OrdinalIgnoreCase) == true;
                collapsedTrees += isTree ? gone : 0;
                var meshBox = component.StaticMeshPath is { } m ? bounds.Describe(m)?.Bounds : null;
                if (meshBox is null)
                {
                    continue;
                }

                var stockReport = Check(stockPackage, index, group.OrderBy(i => i.InstanceIndex).Select(i => i.LocalTransform).ToList(), meshBox.Value);
                var writtenReport = Check(writtenPackage, index, now.Select(i => i.LocalTransform).ToList(), meshBox.Value);
                if (gone > 0 || !stockBytes.AsSpan().SequenceEqual(writtenBytes))
                {
                    changed.Add(group.Key);
                    output.WriteLine($"{component.ClassName} {group.Key} {component.StaticMeshPath?.Split('/')[^1]}: {group.Count()} inst, {gone} collapsed, bytes {stockBytes.Length}->{writtenBytes.Length}");
                    output.WriteLine($"   stock:   {stockReport.Summary}");
                    output.WriteLine($"   written: {writtenReport.Summary}");
                }

                problems.AddRange(writtenReport.Problems.Except(stockReport.Problems).Select(p => $"{group.Key}: {p}"));
            }
        }

        output.WriteLine($"{collapsed} instance(s) collapsed, {collapsedTrees} of them trees; {changed.Count} foliage component(s) rewritten; {problems.Count} new problem(s)");
        foreach (var p in problems.Take(40))
        {
            output.WriteLine("  " + p);
        }

        // Every cluster tree holds its instances, no tree went, and the tile's foliage is exactly as the game cooked it
        // (3.1.0 collapsed 60 instances here and rewrote 11 whole components without their draw copy: the blinking).
        Assert.Empty(problems);
        Assert.Equal(0, collapsedTrees);
        Assert.Equal(0, collapsed);
        Assert.Empty(changed);
    }

    private static string TileInfo(CookedPackage package) => ScumStudio.Formats.Packages.WorldTileInfo.TryRead(package) is { } t ? $"{t} lods [{string.Join(", ", t.LodList.Select(l => l.RelativeStreamingDistance))}]" : "no tile info";

    private sealed record HismReport(string Summary, List<string> Problems);

    /// <summary>The HISM data the game reads after the instance array: render copy, cluster tree and reorder table, checked as UE's BuildTree makes them.</summary>
    private static HismReport Check(CookedPackage package, int index, IReadOnlyList<FTransform> known, BoundingBox mesh)
    {
        var problems = new List<string>();
        var payload = package.GetExportBytes(index);
        var block = package.ReadProperties(index);
        var (offset, size) = LevelPackageEditor.FindInstanceArray(payload, block.EndOffset, known) ?? throw new InvalidOperationException("instance array not found");
        var count = known.Count;
        var arrayEnd = offset + (size * count);
        var customSize = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(arrayEnd));
        var customCount = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(arrayEnd + 4));
        var sizeAt = arrayEnd + 8 + (customSize * customCount);
        var copyBytes = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(sizeAt));
        var at = sizeAt + 8 + (int)copyBytes;
        var nodeSize = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at));
        var nodes = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(at + 4));
        var reorder = block.Find("InstanceReorderTable")?.Value is ArrayValue table ? table.Items.OfType<IntValue>().Select(v => v.Value).ToList() : null;
        int Int(string name) => block.Find(name)?.Value is IntValue v ? v.Value : -1;
        var summary = $"copy {copyBytes} B, tree {nodes} x {nodeSize}, reorder {reorder?.Count.ToString() ?? "none"}, NumBuiltInstances {Int("NumBuiltInstances")}, NumBuiltRenderInstances {Int("NumBuiltRenderInstances")}, OcclusionLayerNumNodes {Int("OcclusionLayerNumNodes")}, trailing {payload.Length - (at + 8 + (nodes * nodeSize))} B";
        if (nodeSize != 64 || nodes <= 0)
        {
            return new HismReport(summary + " (no tree)", problems);
        }

        if (reorder is not null && (reorder.Count != count || reorder.Where(r => r >= 0).Distinct().Count() != reorder.Count(r => r >= 0) || reorder.Any(r => r >= count)))
        {
            problems.Add($"reorder table is not a permutation of {count}");
        }

        var drawToArray = new int[count];
        Array.Fill(drawToArray, -1);
        for (var i = 0; i < count; i++)
        {
            var r = reorder is not null && i < reorder.Count ? reorder[i] : i;
            if (r >= 0 && r < count)
            {
                drawToArray[r] = i;
            }
        }

        // The cooked draw copy (what the game draws when it is kept): its origins in draw order against the array through the table.
        if (copyBytes > 0)
        {
            var copyAt = sizeAt + 8;
            var originsAt = -1;
            for (var o = copyAt + 8; o + 8 <= copyAt + copyBytes; o += 4)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(o)) == 16 && BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(o + 4)) == count)
                {
                    originsAt = o + 8;
                    break;
                }
            }

            int viaTable = 0, identity = 0;
            var head = new List<string>();
            for (var i = 0; originsAt >= 0 && i < count; i++)
            {
                Vector3 Origin(int d) => new(BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(originsAt + (d * 16))), BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(originsAt + (d * 16) + 4)), BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(originsAt + (d * 16) + 8)));
                var p = new Vector3(known[i].Translation.X, known[i].Translation.Y, known[i].Translation.Z);
                var r = reorder is not null && i < reorder.Count ? reorder[i] : i;
                viaTable += r >= 0 && r < count && Vector3.Distance(Origin(r), p) < 1f ? 1 : 0;
                identity += Vector3.Distance(Origin(i), p) < 1f ? 1 : 0;
            }

            summary += $"; copy header [{string.Join(", ", Enumerable.Range(0, 4).Select(k => BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(copyAt + (k * 4)))))}], origins at +{originsAt - copyAt}: {viaTable}/{count} match through the table, {identity}/{count} in array order";
            if (originsAt < 0 || viaTable != count)
            {
                problems.Add($"cooked draw copy order differs from the reorder table ({viaTable}/{count})");
            }
        }

        (Vector3 Min, Vector3 Max, int FirstChild, int LastChild, int First, int Last) Node(int n)
        {
            var b = at + 8 + (n * 64);
            Vector3 V(int o) => new(BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(b + o)), BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(b + o + 4)), BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(b + o + 8)));
            int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(b + o));
            return (V(0), V(16), I(12), I(28), I(32), I(36));
        }

        static bool Inside(Vector3 min, Vector3 max, Vector3 boxMin, Vector3 boxMax, float slack) =>
            min.X >= boxMin.X - slack && min.Y >= boxMin.Y - slack && min.Z >= boxMin.Z - slack && max.X <= boxMax.X + slack && max.Y <= boxMax.Y + slack && max.Z <= boxMax.Z + slack;

        var root = Node(0);
        if (root.First != 0 || root.Last != count - 1)
        {
            problems.Add($"root covers {root.First}..{root.Last}, not 0..{count - 1}");
        }

        for (var n = 0; n < nodes; n++)
        {
            var node = Node(n);
            if (node.FirstChild >= 0)
            {
                for (var c = node.FirstChild; c <= node.LastChild && c < nodes; c++)
                {
                    var child = Node(c);
                    if (!Inside(child.Min, child.Max, node.Min, node.Max, 1f) || child.First < node.First || child.Last > node.Last)
                    {
                        problems.Add($"node {n} does not contain child {c}");
                    }
                }

                continue;
            }

            for (var d = node.First; d <= node.Last && d < count; d++)
            {
                var i = d >= 0 ? drawToArray[d] : -1;
                if (i < 0)
                {
                    continue;
                }

                var t = known[i];
                var lo = new Vector3(float.MaxValue);
                var hi = new Vector3(float.MinValue);
                for (var k = 0; k < 8; k++)
                {
                    var corner = new FVector((k & 1) == 0 ? mesh.Min.X : mesh.Max.X, (k & 2) == 0 ? mesh.Min.Y : mesh.Max.Y, (k & 4) == 0 ? mesh.Min.Z : mesh.Max.Z);
                    var p = t.TransformPosition(corner);
                    lo = Vector3.Min(lo, new Vector3(p.X, p.Y, p.Z));
                    hi = Vector3.Max(hi, new Vector3(p.X, p.Y, p.Z));
                }

                if (!Inside(lo, hi, node.Min, node.Max, 2f + (0.01f * (hi - lo).Length())))
                {
                    problems.Add($"leaf {n} misses instance {i} (draw {d})");
                }
            }
        }

        return new HismReport(summary, problems);
    }
}
