using System.Buffers.Binary;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "I walk through some trees". Foliage keeps a cooked copy of its trees for drawing next to the array collision is
/// built from; a moved or deleted tree was drawn where it was, with no collision there. The export drops that copy (the
/// game rebuilds it from the edited array) and grows the culling clusters over where a tree went. Real game files only.
/// </summary>
public sealed class FoliageRenderRealTests
{
    private const string Level = "/Game/ConZ_Files/Maps/The_Island/Landscape_A_4_1c";

    [Fact]
    public async Task AMovedOrDeletedTreeIsDrawnWhereTheEditPutIt()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Level);
        var foliage = document.Actors.Single(a => a.Name == "InstancedFoliageActor_0");
        const string oaks = "FoliageInstancedTree_25"; // 9 instances
        var pristine = foliage.InstanceTransforms.Where(i => i.ComponentName == oaks).OrderBy(i => i.InstanceIndex).ToList();
        var moved = TransformValue.FromTransform(pristine[2].LocalTransform);
        moved = moved with { Location = moved.Location + new FVector(30_000f, -20_000f, 0f) }; // 360 m away

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-foliage-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var project = await Project.CreateAsync(Path.Combine(dir, "project"), "Trees");
            project.Apply(EditOpFactory.SetInstanceTransform(document, foliage, oaks, 2, moved, project.State));
            project.Apply(EditOpFactory.DeleteInstance(document, foliage, oaks, 5));
            var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), WritePak = false });

            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var package = ModdableAssets.ReadPackage(written, Level);
            var index = Enumerable.Range(0, package.Exports.Count).Single(i => package.ResolveName(package.Exports[i].ObjectName) == oaks);
            var payload = package.GetExportData(index).ToArray();
            var block = package.ReadProperties(index);
            var known = pristine.Select(p => p.LocalTransform).ToList();
            known[2] = moved.ToTransform();
            var (offset, size) = LevelPackageEditor.FindInstanceArray(payload, block.EndOffset, known) ?? throw new InvalidOperationException("instance array not found");
            var arrayEnd = offset + (size * pristine.Count);

            // No cooked draw copy left: the game builds it from the edited array.
            Assert.Null(InstanceRenderData.Layout(payload, arrayEnd, pristine.Count));
            Assert.Equal(0L, BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(arrayEnd + 8)));

            // The root cluster (and the component's bounds) reach the moved oak.
            var clusters = arrayEnd + 16;
            Assert.Equal(64, BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(clusters)));
            var rootMin = Vector(payload, clusters + 8);
            var rootMax = Vector(payload, clusters + 8 + 16);
            Assert.InRange(moved.Location.X, rootMin.X, rootMax.X);
            Assert.InRange(moved.Location.Y, rootMin.Y, rootMax.Y);

            // The game's reader loads it, with the oak moved and the deleted one collapsed.
            var component = written.LoadPackage(Level).GetExports().OfType<UInstancedStaticMeshComponent>().Single(c => c.Name == oaks);
            var t = component.PerInstanceSMData![2].TransformData;
            Assert.Equal(moved.Location.X, (float)t.Translation.X, 1f);
            Assert.True(component.PerInstanceSMData[5].TransformData.Scale3D.X < 0.001);
            Assert.Equal(pristine[0].LocalTransform.Translation.X, (float)component.PerInstanceSMData[0].TransformData.Translation.X, 1f);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static FVector Vector(byte[] b, int at) =>
        new(BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 4)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at + 8)));
}
