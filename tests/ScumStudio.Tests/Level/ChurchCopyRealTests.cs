using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "I put the church near the farm and it is blurred, merged with a car, and I walk through it" (he had placed the
/// church's far-view model). The real church is two Blueprint actors (outside and inside) in C_3_Church: copied into
/// another level (Ctrl+C / Ctrl+V) they arrive whole, with the full-detail shell that collides. Real game files only.
/// </summary>
public sealed class ChurchCopyRealTests
{
    private const string Source = "/Game/ConZ_Files/Maps/The_Island/C_3_Church";
    private const string Target = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";

    [Fact]
    public async Task TheRealChurchCopiesIntoAnotherLevelWhole()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // needs the real game files
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var church = LevelDocument.Load(new Cue4ParseLevelReader(catalog), Source);
        var outside = church.Actors.Single(a => a.Name == "BP_CC_A_EXT_MASTER");
        var inside = church.Actors.Single(a => a.Name == "BP_CC_A_INT_01_2");
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-church-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var project = await Project.CreateAsync(Path.Combine(dir, "project"), "Church");
            var at = new ScumStudio.Core.Mathematics.FVector(320_000f, -488_700f, 10_550f);
            foreach (var actor in new[] { outside, inside })
            {
                var offset = actor.WorldTransform.Translation - outside.WorldTransform.Translation;
                project.Apply(new AddBlueprintActorOp(Target, actor.Name + "_Added", actor.ClassPath, new ActorRef(Source, actor.Name),
                    TransformValue.FromTransform(actor.WorldTransform with { Translation = at + offset })));
            }

            var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), WritePak = false });
            Assert.DoesNotContain(result.Warnings, w => w.Contains("Added", StringComparison.Ordinal));

            using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
            var exports = written.LoadPackage(Target).GetExports().ToList();
            foreach (var actor in new[] { outside, inside })
            {
                // Every stored part arrives (the shell, the full-detail exterior that collides as its triangles, the inside).
                var copied = exports.Where(e => e.Outer?.Name == actor.Name + "_Added").Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Assert.All(actor.Components.Where(c => !c.IsSynthesized), c => Assert.Contains(c.Name, copied));
            }

            Assert.Contains(outside.Components, c => c.StaticMeshPath?.EndsWith(".SM_CC_A_01_Ext", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(exports, e => e.Name.Contains("Distant", StringComparison.Ordinal));
            Assert.Contains(exports.OfType<UInstancedStaticMeshComponent>(), c => c.Outer?.Name == "BP_CC_A_INT_01_2_Added" && c.PerInstanceSMData is { Length: > 0 }); // benches, candles
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
