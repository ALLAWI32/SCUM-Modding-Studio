using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Reading;
using ScumStudio.Pak.Inspection;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

public sealed class ProjectExporterTests
{
    private const string LevelPath = SyntheticLevels.LevelPath;
    private const string SaloonLevel = MapSlice.MapsPath + "A_0_Outpost_Ext_Saloon";

    [Theory]
    [InlineData("My Mod", "My_Mod")]
    [InlineData("  Town-Cleanup v2 ", "Town-Cleanup_v2")]
    [InlineData("مود", "ScumStudioMod")]
    [InlineData("", "ScumStudioMod")]
    [InlineData(null, "ScumStudioMod")]
    public void SanitizesModNames(string? input, string expected) =>
        Assert.Equal(expected, ProjectExporter.SanitizeModName(input));

    [Fact]
    public void NamesThePak() =>
        Assert.Equal("pakchunk900-My_Mod_P.pak", ProjectExporter.PakFileName("My Mod", 900));

    [Fact]
    public async Task TheLevelsStreamingAreaCoversWhatWasBuiltFarFromIt()
    {
        // Owner: "I walked onto my third bridge and the whole bridge vanished, I fell into the water". The game loads a
        // level only near the area in its tile info; a piece added 500 m away (and a group moved 300 m) were outside it.
        using var temp = new LevelTempDirectory();
        var content = temp.Combine("game");
        var tile = new WorldTileInfo((0, 0, 0), new FVector(-1000, -1000, -100), new FVector(1000, 1000, 500), true,
            new WorldTileLayer("City", 0, (0, 0), 20000, true), false, "None", [], 0);
        SyntheticLevels.WriteContent(content, withBlueprintPackage: true, tile);
        using var catalog = AssetCatalog.OpenLoose(content);

        using var project = Project.Create(temp.Combine("Far.ssproj"), "Far");
        project.Apply(new AddStaticMeshActorOp(LevelPath, "SM_Rock_Far", SyntheticLevels.RockPackage + ".SM_Rock",
            new TransformValue(new FVector(50000, 0, 0), FRotator.Zero, FVector.One)));
        project.Apply(new SetTransformOp(new ActorRef(LevelPath, "Rocks_Actor"), TransformValue.At(0, 0, 100), TransformValue.At(0, -30000, 0)));
        var rock = new ScumStudio.Core.Geometry.BoundingBox(new System.Numerics.Vector3(-50, -50, 0), new System.Numerics.Vector3(50, 50, 80));
        var result = await new ProjectExporter().ExportAsync(project, catalog,
            new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false, BendMeshes = _ => new BendMesh(rock) });

        var file = Directory.EnumerateFiles(result.StagingDirectory, "A_0_TestLevel.umap", SearchOption.AllDirectories).Single();
        var grown = WorldTileInfo.TryRead(CookedPackage.Load(file))!;
        Assert.Equal(50050f, grown.BoundsMax.X, 1f); // the far rock, its mesh included
        Assert.InRange(grown.BoundsMin.Y, -30200f, -30000f); // the moved group's instances (one is scaled 3x)
        Assert.Equal(-1000f, grown.BoundsMin.X); // the rest as it was
        Assert.Equal(tile.Layer, grown.Layer);
        Assert.Equal(tile.ZOrder, grown.ZOrder);
    }

    [Fact]
    public async Task ExportsDeletionsAndMovesToAStagedPak()
    {
        using var temp = new LevelTempDirectory();
        var content = temp.Combine("game");
        SyntheticLevels.WriteContent(content, withBlueprintPackage: true);
        using var catalog = AssetCatalog.OpenLoose(content);

        var moved = new TransformValue(new FVector(1, 2, 3), new FRotator(0, 90, 0), new FVector(2, 2, 2));
        using var project = Project.Create(temp.Combine("My Mod.ssproj"), "My Mod");
        project.Apply(new DeleteActorOp(new ActorRef(LevelPath, "StaticMeshActor_1")));
        project.Apply(new SetTransformOp(new ActorRef(LevelPath, "Rocks_Actor"), TransformValue.At(0, 0, 100), moved));
        project.Apply(new DeleteInstanceOp(new InstanceRef(LevelPath, "Rocks_Actor", "Rocks", 1)));
        var copyAt = new TransformValue(new FVector(-900, 250, 0), new FRotator(0, 0, 0), new FVector(1, 1, 1));
        project.Apply(new DuplicateActorOp(new ActorRef(LevelPath, "BP_Lamp_C_1"), "BP_Lamp_C_Copy", copyAt));
        project.Apply(new DuplicateActorOp(new ActorRef(LevelPath, "BP_Lamp_C_1"), "BP_Lamp_C_Gone", copyAt));
        project.Apply(new DeleteActorOp(new ActorRef(LevelPath, "BP_Lamp_C_Gone"))); // added then deleted: never created
        project.Apply(new AddStaticMeshActorOp(LevelPath, "SM_Rock_Added", SyntheticLevels.RockPackage + ".SM_Rock", copyAt));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out") });

        Assert.Equal(ProjectSourceRole.Client, result.Role);
        Assert.Equal("My_Mod", result.ModName);
        Assert.Equal(Path.Combine(temp.Combine("out"), "Client", "pakchunk900-My_Mod_P.pak"), result.PakPath);
        Assert.True(File.Exists(result.PakPath));
        Assert.Null(result.SigPath);
        var sigWarning = Assert.Single(result.Warnings);
        Assert.Contains(".sig", sigWarning, StringComparison.Ordinal);
        Assert.Equal(1, Assert.Single(result.Levels).Report.DeletedInstances);
        Assert.Equal(new[] { "BP_Lamp_C_Copy", "SM_Rock_Added" }, result.Levels[0].Report.AddedActors);
        Assert.True(File.Exists(result.ReportPath));
        Assert.Contains("StaticMeshActor_1", File.ReadAllText(result.ReportPath!), StringComparison.Ordinal);

        var staged = Path.Combine(result.StagingDirectory, "SCUM", "Content", "ConZ_Files", "Maps", "The_Island", "A_0_TestLevel");
        Assert.True(File.Exists(staged + ".umap"));
        Assert.True(File.Exists(staged + ".uexp"));

        var level = Assert.Single(result.Levels);
        Assert.Equal(LevelPath, level.PackagePath);
        Assert.Equal("SCUM/Content/ConZ_Files/Maps/The_Island/A_0_TestLevel.umap", level.VirtualPath);
        Assert.Equal(new[] { "StaticMeshActor_1" }, level.Report.RemovedActors);
        Assert.Equal(new[] { "Rocks_Actor.DefaultSceneRoot" }, level.Report.PatchedTransforms);
        Assert.Equal(1, result.RemovedActorCount);
        Assert.Equal(1, result.PatchedTransformCount);

        var index = PakInspector.ReadIndex(result.PakPath!);
        Assert.Equal(2, index.Entries.Count);
        Assert.Contains(index.Entries, e => e.Path.EndsWith("A_0_TestLevel.umap", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(index.Entries, e => e.Path.EndsWith("A_0_TestLevel.uexp", StringComparison.OrdinalIgnoreCase));

        using var overlay = AssetCatalog.OpenLoose(content, new AssetCatalogOptions { LooseOverlays = [result.StagingDirectory] });
        var document = LevelDocument.Load(new Cue4ParseLevelReader(overlay), LevelPath);
        Assert.Equal(new[] { "Rocks_Actor", "BP_Lamp_C_1", "BP_Lamp_C_Copy", "SM_Rock_Added" }, document.Actors.Select(a => a.Name));
        var addedMesh = document.FindActor("SM_Rock_Added")!;
        Assert.Equal("StaticMeshActor", addedMesh.ClassName);
        Assert.Equal(SyntheticLevels.RockPackage + ".SM_Rock", addedMesh.StaticMeshPath);
        Assert.True(addedMesh.Root!.Relative.IsNearlyEqual(copyAt), addedMesh.Root.Relative.ToString());
        var rocks = document.FindActor("Rocks_Actor")!;
        Assert.True(rocks.Root!.Relative.IsNearlyEqual(moved));
        var lampCopy = document.FindActor("BP_Lamp_C_Copy")!;
        Assert.Equal("BP_Lamp_C", lampCopy.ClassName);
        Assert.True(lampCopy.Root!.Relative.IsNearlyEqual(copyAt), lampCopy.Root.Relative.ToString());

        // Instance 1 is collapsed in place (tiny scale, same position); instance 0 is untouched.
        var instances = rocks.InstanceTransforms.OrderBy(i => i.InstanceIndex).ToList();
        Assert.Equal(2, instances.Count);
        Assert.True(instances[0].LocalTransform.Equals(SyntheticLevels.Instance0, 0.01f));
        Assert.InRange(instances[1].LocalTransform.Translation.X, 99.5f, 100.5f);
        Assert.InRange(instances[1].LocalTransform.Scale3D.X, 0f, 0.001f);
    }

    [Fact]
    public async Task RefusesAnEmptyProjectAndSkipsUnknownLevels()
    {
        using var temp = new LevelTempDirectory();
        var content = temp.Combine("game");
        SyntheticLevels.WriteContent(content, withBlueprintPackage: false);
        using var catalog = AssetCatalog.OpenLoose(content);
        using var project = Project.Create(temp.Combine("Empty.ssproj"), "Empty");
        var exporter = new ProjectExporter();
        var options = new ExportOptions { OutputDirectory = temp.Combine("out") };

        await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(project, catalog, options));

        project.Apply(new DeleteActorOp(new ActorRef(MapSlice.MapsPath + "B_9_Nowhere", "Actor_1")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(project, catalog, options));
        Assert.Contains("B_9_Nowhere", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlansChildActorsOfDeletedParentsAndRootComponents()
    {
        using var temp = new LevelTempDirectory();
        SyntheticLevels.WriteContent(temp.Path, withBlueprintPackage: true);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), LevelPath);

        var state = new EditState();
        state.Apply(new DeleteActorOp(new ActorRef(LevelPath, "BP_Lamp_C_1")));
        state.Apply(new SetTransformOp(new ActorRef(LevelPath, "Rocks_Actor"), TransformValue.At(0, 0, 100), TransformValue.At(9, 9, 9)));
        state.Apply(new SetTransformOp(new ActorRef(LevelPath, "StaticMeshActor_1"), TransformValue.Identity, TransformValue.At(1, 1, 1), "Door"));
        state.Apply(new AddStaticMeshActorOp(LevelPath, "SM_Added_1", SyntheticLevels.RockPackage + ".SM_Rock", TransformValue.Identity));
        state.Apply(new DeleteActorOp(new ActorRef("/Game/ConZ_Files/Maps/The_Island/Other", "Elsewhere")));

        var warnings = new List<string>();
        var request = ProjectExporter.PlanLevel(state, LevelPath, document, warnings);

        Assert.Equal(new[] { "BP_Lamp_C_1" }, request.DeleteActors);
        Assert.Equal(2, request.Transforms.Count);
        Assert.Contains(request.Transforms, t => t.Actor == "Rocks_Actor" && t.Component == "DefaultSceneRoot" && t.Value == TransformValue.At(9, 9, 9));
        Assert.Contains(request.Transforms, t => t.Actor == "StaticMeshActor_1" && t.Component == "Door");
        Assert.Empty(warnings);
        var add = Assert.Single(request.StaticMeshAdds);
        Assert.Equal("SM_Added_1", add.NewName);
        Assert.Equal(SyntheticLevels.RockPackage + ".SM_Rock", add.StaticMesh);
        Assert.Equal(TransformValue.Identity, add.Transform);
        Assert.Empty(request.Copies);
    }

    [Fact]
    public void PlansBentActorsAsSplineMeshActors()
    {
        using var temp = new LevelTempDirectory();
        SyntheticLevels.WriteContent(temp.Path, withBlueprintPackage: true);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var document = LevelDocument.Load(new Cue4ParseLevelReader(catalog), LevelPath);
        var house = new ActorRef(LevelPath, "StaticMeshActor_1");
        var added = new AddStaticMeshActorOp(LevelPath, "SM_Added_1", SyntheticLevels.RockPackage + ".SM_Rock", TransformValue.At(5, 5, 5));
        var copyAt = new TransformValue(new FVector(-900, 250, 0), FRotator.Zero, new FVector(1, 1, 1));

        var state = new EditState();
        state.Apply(added);
        state.Apply(new DuplicateActorOp(house, "House_Copy", copyAt));
        state.Apply(new BendActorOp(house, 0f, 45f));
        state.Apply(new BendActorOp(added.Created, 0f, -30f));
        state.Apply(new BendActorOp(new ActorRef(LevelPath, "House_Copy"), 0f, 90f));
        state.Apply(new BendActorOp(new ActorRef(LevelPath, "BP_Lamp_C_1"), 0f, 10f)); // a Blueprint: stays straight

        var bounds = new ScumStudio.Core.Geometry.BoundingBox(new System.Numerics.Vector3(-300, -20, 0), new System.Numerics.Vector3(300, 20, 200));
        var warnings = new List<string>();
        var solid = new BendMesh(bounds, null, [new ScumStudio.Assets.Meshes.CollisionBox(new FVector(0, 0, 100), FRotator.Zero, new FVector(600, 40, 200))], new ScumStudio.Formats.FGuid(1, 2, 3, 4));
        var request = ProjectExporter.PlanLevel(state, LevelPath, document, warnings, null, null, _ => solid);

        // The level house leaves and comes back bent (its scale 2 baked into the curve); copy and added mesh are created bent.
        Assert.Equal(new[] { "StaticMeshActor_1" }, request.DeleteActors);
        Assert.Empty(request.Copies);
        Assert.Equal(new[] { "House_Copy", "SM_Added_1", "StaticMeshActor_1_Bent" }, request.StaticMeshAdds.Select(a => a.NewName).Order(StringComparer.Ordinal));
        var bentHouse = request.StaticMeshAdds.Single(a => a.NewName == "StaticMeshActor_1_Bent");
        Assert.Equal(FVector.One, bentHouse.Transform.Scale);
        Assert.Equal(new FVector(1000, 0, 0), bentHouse.Transform.Location);
        Assert.Equal(BendShape.For(bounds, new FVector(2, 2, 2), 45f), bentHouse.Spline);
        Assert.All(request.StaticMeshAdds, a => Assert.NotNull(a.Spline));
        Assert.All(request.StaticMeshAdds, a => Assert.NotEmpty(a.Collision!)); // every bent piece is solid
        Assert.Contains(warnings, w => w.Contains("BP_Lamp_C_1", StringComparison.Ordinal));

        // Collision check: a mesh whose collision cannot be built bent (no shapes, or no body guid) is exported straight.
        warnings.Clear();
        request = ProjectExporter.PlanLevel(state, LevelPath, document, warnings, null, null, _ => new BendMesh(bounds));
        Assert.Empty(request.DeleteActors);
        Assert.All(request.StaticMeshAdds, a => Assert.Null(a.Spline));
        Assert.Contains(warnings, w => w.Contains("exported straight", StringComparison.Ordinal) && w.Contains("collision check", StringComparison.Ordinal));

        // A mesh whose materials cannot be drawn bent stays straight, with the reason.
        warnings.Clear();
        request = ProjectExporter.PlanLevel(state, LevelPath, document, warnings, null, null, _ => new BendMesh(bounds, "/Game/Foliage/M_Tree.M_Tree"));
        Assert.Empty(request.DeleteActors);
        Assert.All(request.StaticMeshAdds, a => Assert.Null(a.Spline));
        Assert.Contains(warnings, w => w.Contains("M_Tree", StringComparison.Ordinal));
    }

    [Fact]
    public void ABridgesFenceBendsInItsWorldPlaceAndRepeatsWhenMadeLonger()
    {
        // A bridge piece turned a quarter, its fence attached to it (as on Dr Tudman bridge): the fence's place is relative.
        const string level = "/Game/ConZ_Files/Maps/The_Island/A_0_Synth_Bridge";
        const string fenceMesh = "/Game/Road/SM_Bridge_Fence.SM_Bridge_Fence";
        var b = new FakeLevelBuilder(level);
        var bridge = b.Actor("Bridge_1", "/Script/Engine.StaticMeshActor");
        var deck = b.Component(bridge, "StaticMeshComponent0", "StaticMeshComponent", location: new FVector(10000, 5000, 3000), rotation: new FRotator(0, 90, 0), mesh: "/Game/Road/SM_Bridge.SM_Bridge");
        b.Root(bridge, deck);
        var fence = b.Actor("Fence_1", "/Script/Engine.StaticMeshActor");
        b.Root(fence, b.Component(fence, "StaticMeshComponent0", "StaticMeshComponent", location: new FVector(-2381, 0, -1), attachTo: deck, mesh: fenceMesh));
        var document = LevelDocument.FromData(b.Build());

        // Made about twice as long (its end pulled 16 m on): two fences end to end, not one stretched.
        var bounds = new ScumStudio.Core.Geometry.BoundingBox(new System.Numerics.Vector3(-791, -14, 0), new System.Numerics.Vector3(794, 14, 120));
        var state = new EditState();
        state.Apply(new BendActorOp(new ActorRef(level, "Fence_1"), 0f, 0f, NewEnd: new SplineEnd(new FVector(1600, 0, 0))));
        var warnings = new List<string>();
        var request = ProjectExporter.PlanLevel(state, level, document, warnings, null, null, _ => new BendMesh(bounds, null, [new ScumStudio.Assets.Meshes.CollisionBox(new FVector(1.5f, 0, 60), FRotator.Zero, new FVector(1585, 9, 120))], new ScumStudio.Formats.FGuid(1, 2, 3, 4)));

        Assert.Empty(warnings);
        Assert.Equal(new[] { "Fence_1" }, request.DeleteActors);
        Assert.Equal(new[] { "Fence_1_Bent", "Fence_1_Bent_2" }, request.StaticMeshAdds.Select(a => a.NewName));
        Assert.All(request.StaticMeshAdds, a => Assert.True(FVector.Distance(a.Transform.Location, new FVector(10000, 2619, 2999)) < 0.5f, $"{a.NewName} at {a.Transform.Location}"));
        var (first, second) = (request.StaticMeshAdds[0].Spline!, request.StaticMeshAdds[1].Spline!);
        Assert.True(FVector.Distance(first.StartPos, new FVector(-791, 0, 0)) < 0.5f, $"starts at {first.StartPos}");
        Assert.True(FVector.Distance(first.EndPos, second.StartPos) < 0.01f, "the two meet");
        Assert.True(FVector.Distance(second.EndPos, new FVector(794 + 1600, 0, 0)) < 0.5f, $"ends at {second.EndPos}");
        Assert.Equal(FVector.Distance(first.StartPos, first.EndPos), FVector.Distance(second.StartPos, second.EndPos), 1f);
    }

    [Fact]
    public async Task ExportsFromAPakSourceAndCopiesTheStockSig()
    {
        using var temp = new LevelTempDirectory();
        var game = temp.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);

        // A "stock" Paks folder: one pak with the game files plus two stock signatures (pakchunk44's must win).
        var paks = temp.Combine("Paks");
        Directory.CreateDirectory(paks);
        await new ScumStudio.Pak.Writing.PakWriter().WriteFromDirectoryAsync(game, Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"));
        File.WriteAllBytes(Path.Combine(paks, "pakchunk0-WindowsNoEditor.sig"), [9, 9, 9]);
        File.WriteAllBytes(Path.Combine(paks, "pakchunk44-WindowsNoEditor.sig"), [4, 4, 4, 4]);
        File.WriteAllBytes(Path.Combine(paks, "pakchunk777-OtherMod_P.sig"), [7]);

        using var catalog = AssetCatalog.OpenPaks(paks);
        Assert.Equal(paks, catalog.SourcePath);
        Assert.Equal(Path.Combine(paks, "pakchunk44-WindowsNoEditor.sig"), ProjectExporter.FindStockSig(catalog));

        using var project = Project.Create(temp.Combine("PakMod.ssproj"), "Pak Mod");
        project.Apply(new DeleteActorOp(new ActorRef(LevelPath, "StaticMeshActor_1")));
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out") });

        Assert.Equal(Path.ChangeExtension(result.PakPath!, ".sig"), result.SigPath);
        Assert.Equal(new byte[] { 4, 4, 4, 4 }, File.ReadAllBytes(result.SigPath!));
        Assert.DoesNotContain(result.Warnings, w => w.Contains(".sig", StringComparison.Ordinal));
        Assert.Equal(new[] { "StaticMeshActor_1" }, Assert.Single(result.Levels).Report.RemovedActors);

        // The mod pak alone is a valid source: CUE4Parse mounts it and reads the rewritten level.
        using var modOnly = AssetCatalog.OpenPaks(result.PakPath!);
        var document = LevelDocument.Load(new Cue4ParseLevelReader(modOnly), LevelPath);
        Assert.Equal(new[] { "Rocks_Actor", "BP_Lamp_C_1" }, document.Actors.Select(a => a.Name));
    }

    [MapSliceFact]
    public async Task BendsARealRoadPieceInPlace()
    {
        const string tile = MapSlice.MapsPath + "Landscape_A_0_1b";
        using var catalog = MapSlice.Open();
        var options = new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false, ReadInstances = false };
        var before = LevelDocument.Load(new Cue4ParseLevelReader(catalog, options), tile);
        var (owner, piece) = before.Actors.SelectMany(a => a.Components.Where(c => c.SplineMesh is not null && !c.IsSynthesized).Select(c => (a, c))).First();
        var expected = SplineSway.Apply(piece.SplineMesh!, 150f, -150f);

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Road.ssproj"), "Road");
        project.Apply(new SwaySegmentOp(new ActorRef(tile, owner.Name), piece.Name, 0f, 0f, 150f, -150f));
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        Assert.Contains($"{owner.Name}.{piece.Name} (spline)", Assert.Single(result.Levels).Report.PatchedTransforms);

        using var overlay = MapSlice.Open(result.StagingDirectory);
        var after = LevelDocument.Load(new Cue4ParseLevelReader(overlay, options), tile);
        var bent = after.FindActor(owner.Name)!.FindComponent(piece.Name)!.SplineMesh!;
        Assert.Equal(expected.StartPos, bent.StartPos); // ends stay on the neighbours
        Assert.Equal(expected.EndPos, bent.EndPos);
        Assert.True(FVector.Distance(expected.StartTangent, bent.StartTangent) < 0.01f && FVector.Distance(expected.EndTangent, bent.EndTangent) < 0.01f);
        Assert.Equal(piece.SplineMesh!.StartOffset, bent.StartOffset); // the rest of the curve is kept
        Assert.Equal(piece.SplineMesh.EndRoll, bent.EndRoll, 1e-6f);

        // The cached collision guid stays: without it the game rebuilds the collision from the mesh at load, and fails.
        var file = Directory.EnumerateFiles(result.StagingDirectory, "Landscape_A_0_1b.umap", SearchOption.AllDirectories).Single();
        var package = CookedPackage.Load(file);
        var index = Enumerable.Range(0, package.Exports.Count).Single(i => package.ResolveName(package.Exports[i].ObjectName) == piece.Name
            && package.ResolveName(package.Exports[package.Exports[i].OuterIndex - 1].ObjectName) == owner.Name);
        Assert.NotNull(package.ReadProperties(index).Find("CachedMeshBodySetupGuid"));
    }

    [MapSliceFact]
    public async Task CollapsesARealInstanceInTheOutpostExterior()
    {
        const string exterior = MapSlice.MapsPath + "A_0_Outpost_Exterior";
        using var catalog = MapSlice.Open();
        var options = new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false };
        var before = LevelDocument.Load(new Cue4ParseLevelReader(catalog, options), exterior);
        var owner = before.Actors.First(a => a.InstanceTransforms.Count >= 2 && a.ParentComponent is null);
        var first = owner.InstanceTransforms.OrderBy(i => i.InstanceIndex).First();
        var component = first.ComponentName;
        var count = owner.InstanceTransforms.Count(i => i.ComponentName == component);

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Foliage.ssproj"), "Foliage Test");
        project.Apply(new DeleteInstanceOp(new InstanceRef(exterior, owner.Name, component, first.InstanceIndex)));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        var level = Assert.Single(result.Levels);
        Assert.Equal(1, level.Report.DeletedInstances);
        Assert.DoesNotContain(result.Warnings, w => w.Contains(owner.Name, StringComparison.Ordinal));

        using var overlay = MapSlice.Open(result.StagingDirectory);
        var after = LevelDocument.Load(new Cue4ParseLevelReader(overlay, options), exterior);
        var rewritten = after.FindActor(owner.Name)!;
        var instances = rewritten.InstanceTransforms.Where(i => i.ComponentName == component).OrderBy(i => i.InstanceIndex).ToList();
        Assert.Equal(count, instances.Count);
        var collapsed = instances.Single(i => i.InstanceIndex == first.InstanceIndex);
        Assert.InRange(collapsed.LocalTransform.Scale3D.X, 0f, 0.001f);
        Assert.True(collapsed.LocalTransform.Translation.Equals(first.LocalTransform.Translation), "collapsed in place");
        var untouched = instances.First(i => i.InstanceIndex != first.InstanceIndex);
        var pristine = owner.InstanceTransforms.Single(i => i.ComponentName == component && i.InstanceIndex == untouched.InstanceIndex);
        Assert.True(untouched.LocalTransform.Equals(pristine.LocalTransform, 0.001f), "other instances unchanged");
        Assert.Equal(before.Actors.Count, after.Actors.Count);
    }

    [MapSliceFact]
    public async Task DuplicatesARealStaticMeshActorAndABuildingWithChildActors()
    {
        const string exterior = MapSlice.MapsPath + "A_0_Outpost_Exterior";
        using var catalog = MapSlice.Open();
        var options = new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false, ReadInstances = false };
        var before = LevelDocument.Load(new Cue4ParseLevelReader(catalog, options), exterior);
        var source = before.Actors.First(a => a.ClassName == "StaticMeshActor" && a.StaticMeshPath is not null && a.ParentComponent is null);
        var saloonBefore = LevelDocument.Load(new Cue4ParseLevelReader(catalog, options), SaloonLevel);
        var saloon = saloonBefore.Actors.First(a => a.Components.Any(c => c.ChildActor is not null));

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Copies.ssproj"), "Copies");
        var at = new TransformValue(source.Root!.Relative.Location + new FVector(0, 0, 300), source.Root.Relative.Rotation, source.Root.Relative.Scale);
        project.Apply(new DuplicateActorOp(new ActorRef(exterior, source.Name), source.Name + "_Copy", at));
        project.Apply(new DuplicateActorOp(new ActorRef(SaloonLevel, saloon.Name), saloon.Name + "_Copy", saloon.Root?.Relative ?? TransformValue.Identity));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        Assert.Equal(2, result.Levels.Count);
        var exteriorLevel = result.Levels.Single(l => l.PackagePath == exterior);
        Assert.Equal(new[] { source.Name + "_Copy" }, exteriorLevel.Report.AddedActors);
        var saloonLevel = result.Levels.Single(l => l.PackagePath == SaloonLevel);
        Assert.Equal(new[] { saloon.Name + "_Copy" }, saloonLevel.Report.AddedActors);
        var childActors = saloon.Components.Count(c => c.ChildActor is not null);
        Assert.Equal(saloonLevel.Report.ActorsBefore + 1 + childActors, saloonLevel.Report.ActorsAfter); // the building and its stored child actors
        Assert.DoesNotContain(result.Warnings, w => w.Contains(saloon.Name, StringComparison.Ordinal));
        using (var saloonOverlay = MapSlice.Open(result.StagingDirectory))
        {
            var saloonAfter = LevelDocument.Load(new Cue4ParseLevelReader(saloonOverlay, options), SaloonLevel);
            Assert.Equal(saloonBefore.Actors.Count + 1 + childActors, saloonAfter.Actors.Count);
            var saloonCopy = saloonAfter.FindActor(saloon.Name + "_Copy")!;
            Assert.Equal(saloon.ClassName, saloonCopy.ClassName);
            Assert.Equal(saloon.Components.Count, saloonCopy.Components.Count);
            Assert.Equal(childActors, saloonAfter.Actors.Count(a => a.ParentComponent is { } pc && saloonCopy.Components.Any(c => c.ExportIndex == pc)));
        }

        using var overlay = MapSlice.Open(result.StagingDirectory);
        var after = LevelDocument.Load(new Cue4ParseLevelReader(overlay, options), exterior);
        Assert.Equal(before.Actors.Count + 1, after.Actors.Count);
        var copy = after.FindActor(source.Name + "_Copy")!;
        Assert.Equal(source.ClassName, copy.ClassName);
        Assert.Equal(source.StaticMeshPath, copy.StaticMeshPath);
        Assert.Equal(source.Components.Count, copy.Components.Count);
        Assert.True(copy.Root!.Relative.IsNearlyEqual(at), copy.Root.Relative.ToString());
        Assert.True(after.FindActor(source.Name)!.Root!.Relative.IsNearlyEqual(source.Root.Relative));
        Assert.DoesNotContain(after.Warnings, w => w.Contains(copy.Name, StringComparison.Ordinal));
    }

    [MapSliceFact]
    public async Task AddsAStaticMeshActorWithNewImportsToARealLevel()
    {
        const string mesh = "/Game/ConZ_Files/Models/Objects/Outdoor/Cars/Car_Barricades/Car_Barricade_02.Car_Barricade_02";
        using var catalog = MapSlice.Open();
        var options = new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false, ReadInstances = false };
        var before = LevelDocument.Load(new Cue4ParseLevelReader(catalog, options), SaloonLevel);
        Assert.DoesNotContain(before.Actors, a => a.StaticMeshPath == mesh);

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Adds.ssproj"), "Adds");
        var at = new TransformValue(new FVector(-623000, -557500, 2600), new FRotator(0, 45, 0), new FVector(1, 1, 1));
        project.Apply(new AddStaticMeshActorOp(SaloonLevel, "Car_Barricade_02_Added", mesh, at));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        var level = Assert.Single(result.Levels);
        Assert.Equal(new[] { "Car_Barricade_02_Added" }, level.Report.AddedActors);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Car_Barricade_02_Added", StringComparison.Ordinal));

        var staged = Path.Combine([result.StagingDirectory, .. level.VirtualPath.Split('/')]);
        var rewritten = CookedPackage.Load(staged);
        Assert.Contains(rewritten.Imports, i => rewritten.ResolveName(i.ObjectName) == "Car_Barricade_02" && rewritten.ResolveName(i.ClassName) == "StaticMesh");
        Assert.NotNull(WorldTileInfo.TryRead(rewritten));

        using var overlay = MapSlice.Open(result.StagingDirectory);
        var after = LevelDocument.Load(new Cue4ParseLevelReader(overlay, options), SaloonLevel);
        Assert.Equal(before.Actors.Count + 1, after.Actors.Count);
        var added = after.FindActor("Car_Barricade_02_Added")!;
        Assert.Equal("StaticMeshActor", added.ClassName);
        Assert.Equal(mesh, added.StaticMeshPath);
        Assert.True(added.Root!.Relative.IsNearlyEqual(at), added.Root.Relative.ToString());
    }

    [Fact]
    public async Task CopiesABlueprintActorBetweenSyntheticLevelsRemappingNames()
    {
        using var temp = new LevelTempDirectory();
        var game = temp.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        const string other = "/Game/ConZ_Files/Maps/The_Island/A_0_Other";
        SyntheticLevels.WriteBlueprintOnlyLevel(game, "A_0_Other", SyntheticLevels.BlueprintPackage, "BP_Lamp_C_9", new FVector(10, 20, 30), new FRotator(0, 15, 0));
        using var catalog = AssetCatalog.OpenLoose(game);

        using var project = Project.Create(temp.Combine("Import.ssproj"), "Import");
        var at = new TransformValue(new FVector(-700, 80, 5), new FRotator(0, 30, 0), new FVector(1, 1, 1));
        project.Apply(new AddBlueprintActorOp(other, "BP_Lamp_C_Imported", SyntheticLevels.BlueprintPackage + ".BP_Lamp_C", new ActorRef(LevelPath, "BP_Lamp_C_1"), at));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        var level = Assert.Single(result.Levels);
        Assert.Equal(other, level.PackagePath);
        Assert.Equal(new[] { "BP_Lamp_C_Imported" }, level.Report.AddedActors);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("BP_Lamp_C_Imported", StringComparison.Ordinal));

        using var overlay = AssetCatalog.OpenLoose(game, new AssetCatalogOptions { LooseOverlays = [result.StagingDirectory] });
        var document = LevelDocument.Load(new Cue4ParseLevelReader(overlay), other);
        Assert.Equal(new[] { "BP_Lamp_C_9", "BP_Lamp_C_Imported" }, document.Actors.Select(a => a.Name));
        var imported = document.FindActor("BP_Lamp_C_Imported")!;
        Assert.Equal("BP_Lamp_C", imported.ClassName);
        Assert.Equal(new[] { "DefaultSceneRoot", "Bulb" }, imported.Components.Where(c => !c.IsSynthesized).Select(c => c.Name));
        Assert.True(imported.Root!.Relative.IsNearlyEqual(at), imported.Root.Relative.ToString());
        Assert.Equal(imported.Root.ExportIndex, imported.FindComponent("Bulb")!.AttachParent);
    }

    [MapSliceFact]
    public async Task PlacesTheSaloonBuildingIntoAnotherLevel()
    {
        const string exterior = MapSlice.MapsPath + "A_0_Outpost_Exterior";
        using var catalog = MapSlice.Open();
        var options = new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false, ReadInstances = false };
        var saloonLevel = LevelDocument.Load(new Cue4ParseLevelReader(catalog, options), SaloonLevel);
        var saloon = saloonLevel.Actors.First(a => a.Components.Any(c => c.ChildActor is not null));
        var childActors = saloon.Components.Count(c => c.ChildActor is not null);
        var before = LevelDocument.Load(new Cue4ParseLevelReader(catalog, options), exterior);

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Town.ssproj"), "Town");
        var at = new TransformValue(new FVector(-614500, -549500, 2290), new FRotator(0, 90, 0), new FVector(1, 1, 1));
        project.Apply(new AddBlueprintActorOp(exterior, "BP_SaloonOutpost_Town", saloon.ClassPath, new ActorRef(SaloonLevel, saloon.Name), at));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false });
        var level = Assert.Single(result.Levels);
        Assert.Equal(new[] { "BP_SaloonOutpost_Town" }, level.Report.AddedActors);
        Assert.Equal(level.Report.ActorsBefore + 1 + childActors, level.Report.ActorsAfter);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("BP_SaloonOutpost_Town", StringComparison.Ordinal));

        // Every copied export parses with the target name table and keeps the source's property names.
        var sourcePackage = CookedPackage.Parse(ReadFile(catalog, SaloonLevel, ".umap"), ReadFile(catalog, SaloonLevel, ".uexp"), null, SaloonLevel);
        var staged = Path.Combine([result.StagingDirectory, .. level.VirtualPath.Split('/')]);
        var rewritten = CookedPackage.Load(staged);
        var originalCount = CookedPackage.Parse(ReadFile(catalog, exterior, ".umap"), ReadFile(catalog, exterior, ".uexp"), null, exterior).Exports.Count;
        var sourceActor = sourcePackage.Exports.Select((e, i) => (e, i)).First(x => sourcePackage.ResolveName(x.e.ObjectName) == saloon.Name).i;
        var copiedActor = rewritten.Exports.Select((e, i) => (e, i)).First(x => rewritten.ResolveName(x.e.ObjectName) == "BP_SaloonOutpost_Town").i;
        Assert.Equal(sourcePackage.ReadProperties(sourceActor).Properties.Select(t => t.Name + ":" + t.Type), rewritten.ReadProperties(copiedActor).Properties.Select(t => t.Name + ":" + t.Type));
        for (var i = originalCount; i < rewritten.Exports.Count; i++)
        {
            var block = rewritten.ReadProperties(i);
            Assert.True(block.Properties.Count >= 0);
        }

        using var overlay = MapSlice.Open(result.StagingDirectory);
        var after = LevelDocument.Load(new Cue4ParseLevelReader(overlay, options), exterior);
        Assert.Equal(before.Actors.Count + 1 + childActors, after.Actors.Count);
        var placed = after.FindActor("BP_SaloonOutpost_Town")!;
        Assert.Equal(saloon.ClassName, placed.ClassName);
        Assert.Equal(saloon.Components.Count, placed.Components.Count);
        Assert.True(placed.Root!.Relative.IsNearlyEqual(at), placed.Root.Relative.ToString());
        Assert.Equal(childActors, after.Actors.Count(a => a.ParentComponent is { } pc && placed.Components.Any(c => c.ExportIndex == pc)));
        Assert.Equal(saloon.Components.Select(c => c.ClassName), placed.Components.Select(c => c.ClassName));
    }

    private static byte[] ReadFile(AssetCatalog catalog, string level, string extension)
    {
        Assert.True(catalog.TryGetPackageFile(level, out var file));
        var stem = file.Path[..file.Path.LastIndexOf('.')];
        return catalog.Provider.Files[stem + extension].Read();
    }

    [MapSliceFact]
    public async Task ExportsASaloonDeletionThatCue4ParseReadsBack()
    {
        using var catalog = MapSlice.Open();
        var options = new Cue4ParseLevelReaderOptions { ExpandBlueprintComponents = false, ExpandChildActors = false, ReadInstances = false };
        var before = LevelDocument.Load(new Cue4ParseLevelReader(catalog, options), SaloonLevel);
        var victim = before.Actors.First(a => a.ParentComponent is null);

        using var temp = new LevelTempDirectory();
        using var project = Project.Create(temp.Combine("Saloon.ssproj"), "Saloon Test");
        project.Apply(new DeleteActorOp(new ActorRef(SaloonLevel, victim.Name)));

        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = temp.Combine("out") });
        var level = Assert.Single(result.Levels);
        Assert.Equal(new[] { victim.Name }, level.Report.RemovedActors);
        Assert.Equal(level.Report.ActorsBefore - 1, level.Report.ActorsAfter);
        Assert.DoesNotContain(result.Warnings, w => w.Contains(SaloonLevel, StringComparison.OrdinalIgnoreCase) && !w.Contains(".sig", StringComparison.Ordinal));

        // The World Composition tile info survives the rewrite.
        Assert.True(catalog.TryGetPackageFile(SaloonLevel, out var file));
        var stem = file.Path[..file.Path.LastIndexOf('.')];
        var original = CookedPackage.Parse(file.Read(), catalog.Provider.Files[stem + ".uexp"].Read(), null, SaloonLevel);
        var staged = Path.Combine([result.StagingDirectory, .. file.Path.Split('/')]);
        var rewritten = CookedPackage.Load(staged);
        var tileBefore = WorldTileInfo.TryRead(original);
        var tileAfter = WorldTileInfo.TryRead(rewritten);
        Assert.NotNull(tileBefore);
        Assert.NotNull(tileAfter);
        Assert.Equal(tileBefore.Position, tileAfter.Position);
        Assert.Equal(tileBefore.Layer, tileAfter.Layer);
        Assert.Equal(tileBefore.ParentTilePackageName, tileAfter.ParentTilePackageName);
        Assert.Equal(original.Exports.Count, rewritten.Exports.Count);

        using var overlay = MapSlice.Open(result.StagingDirectory);
        var after = LevelDocument.Load(new Cue4ParseLevelReader(overlay, options), SaloonLevel);
        Assert.Equal(before.Actors.Count - 1, after.Actors.Count);
        Assert.Null(after.FindActor(victim.Name));
        Assert.Equal(before.Actors.Where(a => a.Name != victim.Name).Select(a => a.Name), after.Actors.Select(a => a.Name));

        var index = PakInspector.ReadIndex(result.PakPath!);
        Assert.Equal(2, index.Entries.Count);
        Assert.Contains(index.Entries, e => e.Path.EndsWith("A_0_Outpost_Ext_Saloon.umap", StringComparison.OrdinalIgnoreCase));
    }
}
