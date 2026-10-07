using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Pak;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner (A_4_Farm_04 / B_4_Outpost): "objects move a little between the tool and the game", "the lakes I placed never
/// show", "the traders of the outpost I deleted still stand there". What the studio writes is read back the way the game
/// reads it (CUE4Parse over the staged package). Real game files only (<c>SCUM_PAKS</c>, key from this PC's store).
/// </summary>
public sealed class ExportFidelityRealTests
{
    private const string Farm = MapSlice.MapsPath + "A_4_Farm_04";
    private const string Outpost = MapSlice.MapsPath + "B_4_Outpost";
    private const string Rock = "/Game/ConZ_Files/Landscape/Rocks/Coastal_Rock_08/Coastal_Rock_08d.Coastal_Rock_08d";
    private const string LakeUnderside = "/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_C_1_FN.LakeWaterSurface_C_1_FN";
    private const string LakeTop = "/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_C_1.LakeWaterSurface_C_1";
    private const string ArmsDealer = "BP_Outpost_Armory_NPCInteractionBox_2"; // the armory's trade post (its arms dealer)
    private const string QuestBook = "BP_QuestBook_Armory";
    private const string Banker = "BP_Outpost_Bank_open_NPC_InteractionBoxes";

    private static readonly Cue4ParseLevelReaderOptions Plain = new() { ExpandBlueprintComponents = false, ExpandChildActors = false, ReadInstances = false };

    private static string? Paks => Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks) ? paks : null;

    private static AssetCatalog Open(string paks, string? overlay = null) =>
        AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore(), LooseOverlays = overlay is null ? [] : [overlay] });

    [Fact]
    public async Task APlacedRockLandsInTheGameWhereTheStudioShowsIt()
    {
        if (Paks is not { } paks)
        {
            return; // not asked for
        }

        // The owner's rock: turned on every axis, scaled unevenly, on the farm's coast (world = relative for a level actor).
        var studio = new TransformValue(new FVector(569143.7f, -229438.2f, 202.03f), new FRotator(-6.6666665f, -179.36f, 0.8333333f), new FVector(0.785f, 0.91f, 0.91f));
        using var catalog = Open(paks);
        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Rock.ssproj"), "Rock");
        var op = new AddStaticMeshActorOp(Farm, "Coastal_Rock_08d_Added", Rock, studio);
        project.Apply(op);

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        var level = Assert.Single(result.Levels);
        Assert.Equal([op.NewName], level.Report.AddedActors);
        // The only line about it is the collision check: this coastal rock is a shell whose open underside stands above the
        // farm's ground there (RockCollisionRealTests), so players could get inside it.
        Assert.All(result.Warnings.Where(w => w.Contains(op.NewName, StringComparison.Ordinal)), w => Assert.Contains("is hollow and open underneath", w, StringComparison.Ordinal));

        // Read back as the game reads it: the actor stands exactly where the viewport drew it (same record, same matrix).
        using var written = Open(paks, result.StagingDirectory);
        var actor = LevelDocument.Load(new Cue4ParseLevelReader(written, Plain), Farm).FindActor(op.NewName)!;
        var expected = studio.ToTransform(); // MapPageViewModel.RefreshAddedActors draws transform.ToTransform()
        var actual = actor.WorldTransform;
        Assert.True(FVector.Distance(expected.Translation, actual.Translation) < 1f, $"off by {FVector.Distance(expected.Translation, actual.Translation)} cm");
        Assert.True(expected.Rotation.AngularDistance(actual.Rotation) * 180f / MathF.PI < 0.01f, $"turned by {expected.Rotation.AngularDistance(actual.Rotation) * 180f / MathF.PI} deg");
        Assert.True(FVector.Distance(expected.Scale3D, actual.Scale3D) < 1e-4f, actual.Scale3D.ToString());
        Assert.True(actor.Root!.Relative.IsNearlyEqual(studio, 1e-3f), actor.Root.Relative.ToString());
        Assert.Null(actor.Root.AttachParent);
        var (a, b) = (expected.ToMatrixWithScale(), actual.ToMatrixWithScale());
        foreach (var (x, y) in new[] { (a.M11, b.M11), (a.M12, b.M12), (a.M13, b.M13), (a.M21, b.M21), (a.M22, b.M22), (a.M23, b.M23), (a.M31, b.M31), (a.M32, b.M32), (a.M33, b.M33), (a.M41, b.M41), (a.M42, b.M42), (a.M43, b.M43) })
        {
            Assert.Equal(x, y, 0.1f);
        }

        // No tile offset to add: SCUM stores a tile's bounds absolute (position 0), and the export grew them over the rock.
        var tile = WorldTileInfo.TryRead(CookedPackage.Load(Path.Combine([result.StagingDirectory, .. level.VirtualPath.Split('/')])))!;
        Assert.Equal((0, 0, 0), tile.Position);
        Assert.True(tile.BoundsMin.X <= studio.Location.X && studio.Location.X <= tile.BoundsMax.X && tile.BoundsMin.Y <= studio.Location.Y && studio.Location.Y <= tile.BoundsMax.Y,
            $"{studio.Location} outside {tile.BoundsMin}..{tile.BoundsMax}");
    }

    [Fact]
    public void TheUndersideOfALakeIsRefusedAndItsTopSurfaceOffered()
    {
        if (Paks is not { } paks)
        {
            return; // not asked for
        }

        using var catalog = Open(paks);
        // The owner placed the *_FN twins of the lakes: every face looks down, the material is the underwater view.
        Assert.True(FarModels.IsUndersideMesh(LakeUnderside, catalog));
        Assert.Contains(MeshExtractor.DescribeStaticMesh(catalog.LoadObject<UStaticMesh>(LakeUnderside)).Materials, m => FarModels.IsUnderwaterMaterial(m.MaterialPath));
        Assert.Equal(LakeTop, FarModels.TopSideOf(LakeUnderside, catalog));
        Assert.False(FarModels.IsUndersideMesh(LakeTop, catalog)); // MI_WaterSurface_Lake, faces up
        Assert.False(FarModels.IsUndersideMesh(Rock, catalog));

        // An older project that still holds one: the export report says why it is not in the game.
        var state = new EditState();
        state.Apply(new AddStaticMeshActorOp(Farm, "LakeWaterSurface_C_1_FN_Added", LakeUnderside, TransformValue.Identity));
        var warnings = new List<string>();
        var request = ProjectExporter.PlanLevel(state, Farm, null, warnings);
        Assert.Single(request.StaticMeshAdds);
        Assert.Single(warnings, w => w.Contains("LakeWaterSurface_C_1_FN_Added", StringComparison.Ordinal) && w.Contains("underside of the water", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADeletedTradePostLeavesNoLiveObjectInThePackage()
    {
        if (Paks is not { } paks)
        {
            return; // not asked for
        }

        using var catalog = Open(paks);
        var before = LevelDocument.Load(new Cue4ParseLevelReader(catalog, Plain), Outpost);
        var barber = before.FindActor(ArmsDealer)!;
        Assert.EndsWith("_NPCInteractionBox_C", barber.ClassName, StringComparison.Ordinal); // the post whose code places the trader
        var parts = barber.Components.Count(c => !c.IsSynthesized);

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Outpost.ssproj"), "Outpost");
        project.Apply(new DeleteActorOp(new ActorRef(Outpost, ArmsDealer)));
        project.Apply(new DeleteActorOp(new ActorRef(Outpost, QuestBook)));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        var level = Assert.Single(result.Levels);
        Assert.Equal(2, level.Report.RemovedActors.Count);
        Assert.True(level.Report.NeutralizedExports >= 2 + parts, $"{level.Report.NeutralizedExports} objects made inert");
        Assert.DoesNotContain(result.Warnings, w => w.Contains(ArmsDealer, StringComparison.Ordinal) || w.Contains(QuestBook, StringComparison.Ordinal));

        // In the package: the trade post, the book and everything under them are plain Objects (no Blueprint code to run
        // on load, no trader to spawn); the banker's post, never deleted, is untouched; the outpost's list forgot the post.
        var package = CookedPackage.Load(Path.Combine([result.StagingDirectory, .. level.VirtualPath.Split('/')]));
        var levelIndex = LevelPackageEditor.FindLevelExport(package);
        int Export(string name) => Enumerable.Range(0, package.Exports.Count).Single(i => package.Exports[i].OuterIndex == levelIndex + 1 && package.ResolveName(package.Exports[i].ObjectName) == name);
        foreach (var gone in new[] { Export(ArmsDealer), Export(QuestBook) })
        {
            Assert.Equal("Object", package.GetExportClassName(gone));
            Assert.Empty(package.ReadProperties(gone).Properties);
            var under = Enumerable.Range(0, package.Exports.Count).Where(i => package.Exports[i].OuterIndex == gone + 1).ToList();
            Assert.All(under, i => Assert.Equal("Object", package.GetExportClassName(i)));
            if (gone == Export(ArmsDealer))
            {
                Assert.True(under.Count >= 1, "the post's NPCInteractionBox components went with it");
            }
        }

        var banker = Export(Banker);
        Assert.EndsWith("_C", package.GetExportClassName(banker), StringComparison.Ordinal);
        Assert.NotEmpty(package.ReadProperties(banker).Properties);
        var manager = Enumerable.Range(0, package.Exports.Count).Single(i => package.GetExportClassName(i).Contains("TradeOutpostManager", StringComparison.Ordinal));
        var posts = Assert.IsType<ArrayValue>(package.ReadProperties(manager).Find("_assignedTradePosts")!.Value).Items.OfType<ObjectValue>().ToList();
        Assert.Contains(posts, p => p.Index == 0);
        Assert.DoesNotContain(posts, p => p.Index == Export(ArmsDealer) + 1);

        // The game's reader still reads the level: the post and the book are gone, the banker stays, nothing else moved.
        using var written = Open(paks, result.StagingDirectory);
        var after = LevelDocument.Load(new Cue4ParseLevelReader(written, Plain), Outpost);
        Assert.Equal(before.Actors.Count - 2, after.Actors.Count);
        Assert.Null(after.FindActor(ArmsDealer));
        Assert.Null(after.FindActor(QuestBook));
        Assert.Equal(before.FindActor(Banker)!.ClassName, after.FindActor(Banker)!.ClassName);
        Assert.Equal(before.Actors.Where(a => a.Name is not ArmsDealer and not QuestBook).Select(a => a.Name), after.Actors.Select(a => a.Name));
        Assert.Equal(before.Actors.Where(a => a.Name is not ArmsDealer and not QuestBook).Select(a => a.WorldTransform.Translation), after.Actors.Select(a => a.WorldTransform.Translation));
    }
}
