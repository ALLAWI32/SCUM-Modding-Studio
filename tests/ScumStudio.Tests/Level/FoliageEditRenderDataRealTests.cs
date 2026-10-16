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
using CookedPackage = ScumStudio.Formats.Packages.CookedPackage;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// "Flickering trees and bushes" after deleting or moving foliage (Discord defolt_0999, 2026-10-13, 3.2.0, Z_4): one bush
/// deleted and one tree moved and turned on a Z_4 tile, exported, and both components read back the way UE 4.27 loads a
/// HISM: the cooked draw copy must still be there and draw every instance where the array has it (through the reorder
/// table), and every cluster must hold its instances and its children. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class FoliageEditRenderDataRealTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Tile = MapSlice.MapsPath + "Landscape_Z_4_2";

    [Fact]
    public async Task ADeletedBushAndAMovedTreeKeepAValidDrawCopyAndClusterTree()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var tile = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Tile);
        var meshes = new BendSupport(catalog);
        var foliage = tile.Actors.Single(a => a.ClassName == "InstancedFoliageActor");
        int Count(string component) => foliage.InstanceTransforms.Count(i => i.ComponentName == component);
        string Largest(string cls) => foliage.Components.Where(c => c.ClassName == cls && c.StaticMeshPath is not null).MaxBy(c => Count(c.Name))!.Name;
        var bush = Largest("FoliageInstancedBush");
        var tree = Largest("FoliageInstancedTree");
        var bushIndex = Count(bush) / 2;
        var treeIndex = Count(tree) / 3;
        var pristineTree = foliage.InstanceTransforms.Single(i => i.ComponentName == tree && i.InstanceIndex == treeIndex).LocalTransform;
        var moved = TransformValue.FromTransform(new FTransform(
            pristineTree.Rotation * new FRotator(0, 90, 0).Quaternion(), pristineTree.Translation + new FVector(2_000f, -1_500f, 40f), pristineTree.Scale3D * 1.2f));
        output.WriteLine($"{Tile}: delete {bush}[{bushIndex}] of {Count(bush)}, move {tree}[{treeIndex}] of {Count(tree)} by (20 m, -15 m) turned 90 and scaled 1.2");

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Foliage.ssproj"), "Foliage");
        project.Apply(EditOpFactory.DeleteInstance(tile, foliage, bush, bushIndex, project.State));
        project.Apply(EditOpFactory.SetInstanceTransform(tile, foliage, tree, treeIndex, moved, project.State));
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        foreach (var w in result.Warnings)
        {
            output.WriteLine("warning: " + w);
        }

        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var after = LevelDocument.Load(new Cue4ParseLevelReader(written), Tile);
        var stockPackage = ModdableAssets.ReadPackage(catalog, Tile);
        var writtenPackage = ModdableAssets.ReadPackage(written, Tile);
        Assert.Equal(stockPackage.Exports.Count, writtenPackage.Exports.Count);
        var foliageAfter = after.Actors.Single(a => a.Name == foliage.Name);

        var problems = new List<string>();
        var changed = new List<string>();
        foreach (var group in foliage.InstanceTransforms.GroupBy(i => i.ComponentName))
        {
            var index = Enumerable.Range(0, writtenPackage.Exports.Count).Single(i => writtenPackage.ResolveName(writtenPackage.Exports[i].ObjectName) == group.Key
                && writtenPackage.GetExportClassName(i) == stockPackage.GetExportClassName(i));
            var stockBytes = stockPackage.GetExportBytes(index);
            var writtenBytes = writtenPackage.GetExportBytes(index);
            if (group.Key != bush && group.Key != tree)
            {
                if (!stockBytes.AsSpan().SequenceEqual(writtenBytes))
                {
                    changed.Add(group.Key);
                }

                continue;
            }

            var component = foliage.FindComponent(group.Key)!;
            var mesh = meshes.Describe(component.StaticMeshPath!)!.Bounds;
            var before = group.OrderBy(i => i.InstanceIndex).Select(i => i.LocalTransform).ToList();
            var now = foliageAfter.InstanceTransforms.Where(i => i.ComponentName == group.Key).OrderBy(i => i.InstanceIndex).Select(i => i.LocalTransform).ToList();
            Assert.Equal(before.Count, now.Count);
            var stock = Inspect(stockPackage, index, before, mesh, requireCopy: false);
            var edited = Inspect(writtenPackage, index, now, mesh, requireCopy: true);
            output.WriteLine($"{component.ClassName} {group.Key} {component.StaticMeshPath!.Split('/')[^1]}: {before.Count} inst, bytes {stockBytes.Length}->{writtenBytes.Length}, mesh {mesh.Min} .. {mesh.Max}");
            output.WriteLine($"   stock:   {stock.Report.Summary}");
            output.WriteLine($"            {stock.Props}");
            output.WriteLine($"   written: {edited.Report.Summary}");
            output.WriteLine($"            {edited.Props}");
            Assert.Empty(stock.Report.Problems); // the checker reads the game's own data as consistent
            problems.AddRange(edited.Report.Problems.Select(p => $"{group.Key}: {p}"));

            // Untouched instances draw exactly as cooked: same draw copy bytes for them (random and light map included).
            if (stock.Report.CopyBytes == edited.Report.CopyBytes && stock.Report.CopyBytes > 0)
            {
                var differing = DifferingCopyBytes(stockBytes, writtenBytes, stock.CopyAt, (int)stock.Report.CopyBytes);
                output.WriteLine($"            draw copy bytes changed: {differing}");
                Assert.InRange(differing, 1, 64);
            }

            // The moved tree is where the edit put it; the deleted bush is collapsed in place.
            if (group.Key == tree)
            {
                Assert.True(now[treeIndex].Equals(moved.ToTransform(), 0.5f), now[treeIndex].ToString());
            }
            else
            {
                Assert.InRange(now[bushIndex].Scale3D.X, 0f, 0.001f);
            }
        }

        foreach (var p in problems)
        {
            output.WriteLine("  " + p);
        }

        Assert.Empty(changed);
        Assert.Empty(problems);
    }

    private sealed record Inspection(HismTail.Report Report, string Props, int CopyAt);

    private static Inspection Inspect(CookedPackage package, int index, IReadOnlyList<FTransform> instances, ScumStudio.Core.Geometry.BoundingBox mesh, bool requireCopy)
    {
        var payload = package.GetExportBytes(index);
        var block = package.ReadProperties(index);
        var (offset, size) = LevelPackageEditor.FindInstanceArray(payload, block.EndOffset, instances) ?? throw new InvalidOperationException("instance array not found");
        var arrayEnd = offset + (size * instances.Count);
        var reorder = block.Find("InstanceReorderTable")?.Value is ArrayValue table ? table.Items.OfType<IntValue>().Select(v => v.Value).ToList() : null;
        var report = HismTail.Check(payload, arrayEnd, instances, reorder, mesh, requireCopy);
        var customSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(arrayEnd));
        var customCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(arrayEnd + 4));
        var props = string.Join("; ", block.Properties.Select(p => $"{p.Name} {Show(p.Value)}"));
        return new Inspection(report, $"element {size} B; {props}", arrayEnd + 8 + (customSize * customCount) + 8);
    }

    private static string Show(PropertyValue value) => value switch
    {
        ArrayValue a => $"[{a.Items.Count}]{(a.Items.Count > 0 && a.Items[0] is IntValue ? " " + string.Join(",", a.Items.Take(6).OfType<IntValue>().Select(v => v.Value)) : string.Empty)}",
        IntValue i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        FloatValue f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        BoolValue b => b.Value.ToString(),
        BoxValue b => $"({b.Min.X:0},{b.Min.Y:0},{b.Min.Z:0})..({b.Max.X:0},{b.Max.Y:0},{b.Max.Z:0}) valid {b.IsValid}",
        StructValue s => $"{s.StructName}{{{string.Join(", ", s.Properties.Select(p => $"{p.Name} {Show(p.Value)}"))}}}",
        ObjectValue o => o.Reference,
        _ => value.GetType().Name.Replace("Value", string.Empty, StringComparison.Ordinal),
    };

    private static int DifferingCopyBytes(byte[] stock, byte[] written, int copyAt, int bytes)
    {
        var n = 0;
        for (var i = 0; i < bytes; i++)
        {
            n += stock[copyAt + i] != written[copyAt + i] ? 1 : 0;
        }

        return n;
    }
}
