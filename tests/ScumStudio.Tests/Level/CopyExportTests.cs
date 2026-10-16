using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.Spawns;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Edits of copies reach the game (seviscache, P1 #4: a duplicate lost what was done to it; igor8802, P1 #7: a duplicated
/// sentry's route could not be changed; the owner, #23: pasted and bent buildings lost their materials). The plan targets
/// the copy's own exports, and the package editor finds them among the exports the request appends.
/// </summary>
public sealed class CopyExportTests
{
    private const string Engine = "/Script/Engine";
    private const string Level = "/Game/ConZ_Files/Maps/The_Island/A_0_Synth_Copies";
    private const string FenceMesh = "/Game/X/SM_Fence.SM_Fence";
    private const string WallMesh = "/Game/X/SM_Wall.SM_Wall";
    private const string BrickMesh = "/Game/X/SM_Brick.SM_Brick";
    private const string PaintA = "/Game/X/MI_A.MI_A";
    private const string Boulder = "/Game/ConZ_Files/Models/Rocks/SM_Boulder.SM_Boulder";

    private static readonly TransformValue Gone = new(FVector.Zero, FRotator.Zero, FVector.Zero);

    /// <summary>A house (scene root and a fence part), a sentry with a patrol path, a repainted wall.</summary>
    private static LevelDocument Document()
    {
        var b = new FakeLevelBuilder(Level);
        var house = b.Actor("House_1", "/Game/X/BP_House.BP_House_C", blueprint: true);
        var root = b.Component(house, "DefaultSceneRoot", location: new FVector(100, 0, 0));
        b.Root(house, root);
        b.Component(house, "Fence", "StaticMeshComponent", location: new FVector(0, 50, 0), attachTo: root, mesh: FenceMesh);
        var sentry = b.Actor("Sentry_1", "/Game/X/BP_Sentry.BP_Sentry_C", blueprint: true);
        b.Root(sentry, b.Component(sentry, "Root", location: new FVector(0, 0, 0)));
        var trees = b.Actor("Trees_1", "/Script/Engine.Actor");
        b.Root(trees, b.Component(trees, "Trees", "InstancedStaticMeshComponent", mesh: FenceMesh, instances: [new FTransform(new FVector(0, 0, 0)), new FTransform(new FVector(300, 0, 0))]));
        var wall = b.Actor("Wall_1", "/Script/Engine.StaticMeshActor");
        var wallMesh = b.Component(wall, "StaticMeshComponent0", "StaticMeshComponent", location: new FVector(500, 0, 0), mesh: WallMesh);
        b.Root(wall, wallMesh);
        var data = b.Build();
        return LevelDocument.FromData(data with
        {
            Exports = data.Exports.Select(e => e.Index == wallMesh ? e with { OverrideMaterials = [null, PaintA] } : e).ToList(),
        });
    }

    private static BendMesh Solid() =>
        new(new BoundingBox(new System.Numerics.Vector3(-200, -20, 0), new System.Numerics.Vector3(200, 20, 300)), null,
            [new ScumStudio.Assets.Meshes.CollisionBox(new FVector(0, 0, 150), FRotator.Zero, new FVector(200, 20, 150))], new FGuid(1, 2, 3, 4));

    [Fact]
    public void APartDeletedOnACopyIsPatchedIntoTheCopy()
    {
        // F078: the part's scale-0 edit on a copy was skipped without a word.
        var state = new EditState();
        state.Apply(new DuplicateActorOp(new ActorRef(Level, "House_1"), "House_1_Copy", TransformValue.At(900, 0, 0)));
        state.Apply(new SetTransformOp(new ActorRef(Level, "House_1_Copy"), TransformValue.At(0, 50, 0), Gone, "Fence"));
        var warnings = new List<string>();

        var request = ProjectExporter.PlanLevel(state, Level, Document(), warnings);

        Assert.Empty(warnings);
        var patch = Assert.Single(request.Transforms);
        Assert.Equal(("House_1_Copy", "Fence"), (patch.Actor, patch.Component));
        Assert.Equal(FVector.Zero, patch.Value.Scale);
        Assert.Contains(request.Copies, c => c.NewName == "House_1_Copy");
    }

    [Fact]
    public void AMeshReplacedOnACopyIsPatchedIntoTheCopy()
    {
        var state = new EditState();
        state.Apply(new DuplicateActorOp(new ActorRef(Level, "House_1"), "House_1_Copy", TransformValue.At(900, 0, 0)));
        state.Apply(new ReplaceMeshOp(new ActorRef(Level, "House_1_Copy"), "Fence", FenceMesh, BrickMesh));
        var warnings = new List<string>();

        var request = ProjectExporter.PlanLevel(state, Level, Document(), warnings);

        Assert.Empty(warnings);
        var patch = Assert.Single(request.Meshes);
        Assert.Equal(("House_1_Copy", "Fence", BrickMesh), (patch.Actor, patch.Component, patch.StaticMesh));
    }

    [Fact]
    public void ADuplicatedSentrysRouteIsWrittenIntoTheCopy()
    {
        // igor8802 (P1 #7): the route was journaled, then dropped with "cannot be written yet".
        var state = new EditState();
        state.Apply(new DuplicateActorOp(new ActorRef(Level, "Sentry_1"), "Sentry_1_Copy", TransformValue.At(0, 900, 0)));
        IReadOnlyList<SpawnPoint> route = [new SpawnPoint(1, TransformValue.At(500, 0, 0)), new SpawnPoint(0, TransformValue.At(0, 600, 0))];
        state.Apply(new SetSpawnPointsOp(new ActorRef(Level, "Sentry_1_Copy"), null, SpawnPointArrays.PatrolPoints,
            [new SpawnPoint(0, TransformValue.At(0, 0, 0)), new SpawnPoint(1, TransformValue.At(100, 0, 0))], route));
        var warnings = new List<string>();

        var request = ProjectExporter.PlanLevel(state, Level, Document(), warnings);

        Assert.DoesNotContain(warnings, w => w.Contains("cannot be written yet", StringComparison.Ordinal));
        Assert.Empty(warnings);
        var patch = Assert.Single(request.SpawnPoints);
        Assert.Equal(("Sentry_1_Copy", null, SpawnPointArrays.PatrolPoints), (patch.Actor, patch.Component, patch.Array));
        Assert.Equal(route, patch.Points);
    }

    [Fact]
    public void AnInstanceDeletedOnACopyIsPatchedWithItsSourcesList()
    {
        var state = new EditState();
        state.Apply(new DuplicateActorOp(new ActorRef(Level, "Trees_1"), "Trees_1_Copy", TransformValue.At(0, 0, 900)));
        state.Apply(new DeleteInstanceOp(new InstanceRef(Level, "Trees_1_Copy", "Trees", 1)));
        var warnings = new List<string>();

        var request = ProjectExporter.PlanLevel(state, Level, Document(), warnings);

        Assert.Empty(warnings);
        var patch = Assert.Single(request.Instances);
        Assert.Equal(("Trees_1_Copy", "Trees", 1, (FTransform?)null), (patch.Actor, patch.Component, patch.Index, patch.Local));
        Assert.Equal(2, Assert.Single(request.InstanceHints, h => h.Actor == "Trees_1_Copy").Instances.Count);
    }

    [Fact]
    public void ABentCopyGivenAnotherMeshBendsAsThatMeshWithoutTheOldMaterials()
    {
        // F076: the bent copy went back to its source's mesh.
        var state = new EditState();
        var copy = new ActorRef(Level, "Wall_1_Copy");
        state.Apply(new DuplicateActorOp(new ActorRef(Level, "Wall_1"), copy.Actor, TransformValue.At(900, 0, 0)));
        state.Apply(new BendActorOp(copy, 0f, 30f));
        state.Apply(new ReplaceMeshOp(copy, null, WallMesh, BrickMesh));
        var warnings = new List<string>();

        var request = ProjectExporter.PlanLevel(state, Level, Document(), warnings, null, null, _ => Solid());

        Assert.Empty(warnings);
        Assert.Empty(request.Copies);
        Assert.Empty(request.Meshes); // bent: created with its mesh, nothing left to patch
        var bent = Assert.Single(request.StaticMeshAdds);
        Assert.Equal(("Wall_1_Copy", BrickMesh), (bent.NewName, bent.StaticMesh));
        Assert.NotNull(bent.Spline);
        Assert.Null(bent.OverrideMaterials); // the new mesh draws its own, as a replaced stored mesh does
    }

    [Fact]
    public void BentAndAddedMeshActorsKeepTheirMaterials()
    {
        // The owner (#23): a bent or pasted building came back with its mesh's stock textures (F054, F096).
        var state = new EditState();
        state.Apply(new BendActorOp(new ActorRef(Level, "Wall_1"), 0f, 30f));
        state.Apply(new DuplicateActorOp(new ActorRef(Level, "Wall_1"), "Wall_1_Copy", TransformValue.At(900, 0, 0)));
        state.Apply(new BendActorOp(new ActorRef(Level, "Wall_1_Copy"), 0f, 20f));
        state.Apply(new AddStaticMeshActorOp(Level, "Pasted_1", WallMesh, TransformValue.At(0, 0, 900)) { OverrideMaterials = ["/Game/X/MI_B.MI_B", null] });
        var warnings = new List<string>();

        var request = ProjectExporter.PlanLevel(state, Level, Document(), warnings, null, null, _ => Solid());

        Assert.Empty(warnings);
        Assert.Equal<string?>([null, PaintA], request.StaticMeshAdds.Single(a => a.NewName == "Wall_1_Bent").OverrideMaterials!);
        Assert.Equal<string?>([null, PaintA], request.StaticMeshAdds.Single(a => a.NewName == "Wall_1_Copy").OverrideMaterials!);
        Assert.Equal<string?>(["/Game/X/MI_B.MI_B", null], request.StaticMeshAdds.Single(a => a.NewName == "Pasted_1").OverrideMaterials!);
        Assert.Null(request.StaticMeshAdds.Single(a => a.NewName == "Pasted_1").Spline);
    }

    [Fact]
    public void APartsMeshOnAnActorExportedAsOneNewMeshIsReported()
    {
        // An added or bent mesh actor is created with its root's mesh only: a mesh named for one of its parts is not dropped silently.
        var state = new EditState();
        state.Apply(new AddStaticMeshActorOp(Level, "Pasted_1", WallMesh, TransformValue.At(0, 0, 900)));
        state.Apply(new ReplaceMeshOp(new ActorRef(Level, "Pasted_1"), "StaticMeshComponent0", WallMesh, BrickMesh));
        state.Apply(new DuplicateActorOp(new ActorRef(Level, "Wall_1"), "Wall_1_Copy", TransformValue.At(900, 0, 0)));
        state.Apply(new BendActorOp(new ActorRef(Level, "Wall_1_Copy"), 0f, 20f));
        state.Apply(new ReplaceMeshOp(new ActorRef(Level, "Wall_1_Copy"), "StaticMeshComponent0", WallMesh, BrickMesh));
        state.Apply(new BendActorOp(new ActorRef(Level, "Wall_1"), 0f, 30f));
        state.Apply(new ReplaceMeshOp(new ActorRef(Level, "Wall_1"), "StaticMeshComponent0", WallMesh, BrickMesh));
        var warnings = new List<string>();

        var request = ProjectExporter.PlanLevel(state, Level, Document(), warnings, null, null, _ => Solid());

        Assert.Empty(request.Meshes);
        Assert.All(request.StaticMeshAdds, a => Assert.Equal(WallMesh, a.StaticMesh));
        Assert.Equal(3, warnings.Count);
        foreach (var actor in new[] { "Pasted_1", "Wall_1_Copy", "Wall_1" })
        {
            Assert.Single(warnings, w => w.Contains($"/{actor}.StaticMeshComponent0:", StringComparison.Ordinal) && w.EndsWith("this part's mesh was not written.", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void AMalformedMaterialPathIsReportedAndAnEmptySlotIsNot()
    {
        // A null slot keeps the mesh's own material by design; a path that names no object is an error and says so.
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_Tile", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        p.SetPayload(world, p.Properties(native: w => { w.I32(level); w.I32(0); w.I32(0); }));
        p.SetPayload(level, p.Properties(native: w => LevelTail(w, [])));
        var built = p.Build();
        var package = CookedPackage.Parse(built.UAsset, built.UExp);

        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            StaticMeshAdds = [new StaticMeshActorAdd("Painted_1", WallMesh, TransformValue.Identity) { OverrideMaterials = [null, "MI_NoPackage", string.Empty] }],
            AssetClass = _ => "Material",
        });

        Assert.Equal(2, report.Warnings.Count);
        Assert.Contains(report.Warnings, w => w.Contains("'MI_NoPackage' is not a material object path", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("'' is not a material object path", StringComparison.Ordinal));
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var component = Child(edited, Child(edited, level, "Painted_1") + 1, "StaticMeshComponent0");
        var slots = ((ArrayValue)edited.ReadProperties(component).Find("OverrideMaterials")!.Value).Items.Cast<ObjectValue>().Select(o => o.Index).ToList();
        Assert.Equal([0, 0, 0], slots);
    }

    [Fact]
    public void ACopysPartAndMeshEditsAreWrittenIntoTheCopyOnly()
    {
        // F075: the copy's exports were never found by the patches (they looked in the package as read).
        var package = Synthetic();
        var levelIndex = LevelPackageEditor.FindLevelExport(package);
        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Copies = [new ActorCopy("StaticMeshActor_1", "StaticMeshActor_1_Copy", TransformValue.At(3000, 0, 0)), new ActorCopy("Rocks_Actor", "Rocks_Actor_Copy", null)],
            Transforms = [new TransformPatch("StaticMeshActor_1_Copy", "Door", Gone)],
            Meshes = [new MeshPatch("StaticMeshActor_1_Copy", null, Boulder)],
            Instances = [new InstancePatch("Rocks_Actor_Copy", "Rocks", 1, null)],
            InstanceHints = [new InstanceArrayHint("Rocks_Actor_Copy", "Rocks", [SyntheticLevels.Instance0, SyntheticLevels.Instance1])],
        });

        Assert.Empty(report.Warnings);
        Assert.Equal(1, report.DeletedInstances);
        Assert.Equal(["StaticMeshActor_1_Copy.Door", "StaticMeshActor_1_Copy.StaticMeshComponent0 (mesh)"], report.PatchedTransforms);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (i != levelIndex)
            {
                Assert.Equal(package.GetExportBytes(i), edited.GetExportBytes(i)); // the source is untouched
            }
        }

        var copy = Child(edited, LevelPackageEditor.FindLevelExport(edited) + 1, "StaticMeshActor_1_Copy");
        var door = edited.ReadProperties(Child(edited, copy + 1, "Door"));
        Assert.True(door.Find("RelativeScale3D")?.Value is VectorValue { X: 0, Y: 0, Z: 0 }, "the copy's door is scaled to nothing");
        Assert.Equal(0, ((ObjectValue)door.Find("StaticMesh")!.Value).Index); // a part deleted on the copy loses its mesh too
        var rootIndex = Child(edited, copy + 1, "StaticMeshComponent0");
        var root = edited.ReadProperties(rootIndex);
        var mesh = (ObjectValue)root.Find("StaticMesh")!.Value;
        Assert.Equal(Boulder, edited.GetFullPath(mesh.Index));
        Assert.Null(root.Find("OverrideMaterials"));
        Assert.Contains(mesh.Index, CreateBeforeSerialize(edited, rootIndex));

        var document = LoadThroughCue4Parse(bytes);
        Assert.Equal(Boulder, document.FindActor("StaticMeshActor_1_Copy")!.StaticMeshPath);
        Assert.Equal(SyntheticLevels.RockPackage + ".SM_Rock", document.FindActor("StaticMeshActor_1")!.StaticMeshPath);
        Assert.Equal(FVector.Zero, document.FindActor("StaticMeshActor_1_Copy")!.FindComponent("Door")!.Relative.Scale);
    }

    [Fact]
    public void ACopysPatrolPathIsRewrittenAndTheSourcesKept()
    {
        // N050: the copy's route edit reached no export.
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_Tile", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var sentry = p.Export("Sentry_1", p.ScriptClass("/Script/ConZ", "SentrySpawner"), level);
        var root = p.Export("Root", p.ScriptClass(Engine, "SceneComponent"), sentry);
        p.SetPayload(world, p.Properties(native: w => { w.I32(level); w.I32(0); w.I32(0); }));
        p.SetPayload(level, p.Properties(native: w => LevelTail(w, [sentry])));
        p.SetPayload(sentry, p.Properties(t =>
        {
            t.Object("RootComponent", root);
            t.StructArray("PatrolPoints", "SentryPatrolPoint",
                e => e.Vector("LocationRelativeToSentry", 100, 0, 0),
                e => e.Vector("LocationRelativeToSentry", 200, 0, 0));
        }));
        p.SetPayload(root, p.Properties(t => t.Vector("RelativeLocation", 1, 2, 3)));
        var built = p.Build();
        var package = CookedPackage.Parse(built.UAsset, built.UExp);

        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Copies = [new ActorCopy("Sentry_1", "Sentry_1_Copy", TransformValue.At(5000, 0, 0))],
            SpawnPoints = [new SpawnPointsPatch("Sentry_1_Copy", null, SpawnPointArrays.PatrolPoints,
                [new SpawnPoint(1, TransformValue.At(700, 0, 0)), new SpawnPoint(0, TransformValue.At(0, 800, 0)), new SpawnPoint(0, TransformValue.At(0, 0, 900))])],
        });

        Assert.Empty(report.Warnings);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        Assert.Equal(package.GetExportBytes(sentry - 1), edited.GetExportBytes(sentry - 1));
        var copy = Child(edited, level, "Sentry_1_Copy");
        var points = (ArrayValue)edited.ReadProperties(copy).Find("PatrolPoints")!.Value;
        Assert.Equal(
            [new FVector(700, 0, 0), new FVector(0, 800, 0), new FVector(0, 0, 900)],
            points.Items.Cast<StructValue>().Select(s => s.Properties[0].Value is VectorValue v ? new FVector(v.X, v.Y, v.Z) : FVector.One).ToList());
        Assert.Equal(2, ((ArrayValue)edited.ReadProperties(sentry - 1).Find("PatrolPoints")!.Value).Items.Count);
    }

    [Fact]
    public void AnAddedMeshActorWritesItsMaterialsWithTheLevelsOwnImports()
    {
        // F096: [null, a material the level imports] gives two slots, 0 and that import; an unknown class is never guessed.
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_Tile", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var paint = p.ObjectImport("/Game/X/MI_A", Engine, "MaterialInstanceConstant", "MI_A");
        p.SetPayload(world, p.Properties(native: w => { w.I32(level); w.I32(0); w.I32(0); }));
        p.SetPayload(level, p.Properties(native: w => LevelTail(w, [])));
        var built = p.Build();
        var package = CookedPackage.Parse(built.UAsset, built.UExp);

        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            StaticMeshAdds =
            [
                new StaticMeshActorAdd("Painted_1", WallMesh, TransformValue.Identity) { OverrideMaterials = [null, PaintA] },
                new StaticMeshActorAdd("Painted_2", WallMesh, TransformValue.Identity) { OverrideMaterials = ["/Game/X/M_New.M_New", "/Game/X/T_Odd.T_Odd"] },
            ],
            AssetClass = path => path == "/Game/X/M_New" ? "Material" : path == "/Game/X/T_Odd" ? "Texture2D" : null,
        });

        Assert.Equal(["Painted_1", "Painted_2"], report.AddedActors);
        var warning = Assert.Single(report.Warnings);
        Assert.Contains("T_Odd", warning, StringComparison.Ordinal);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var first = Child(edited, Child(edited, level, "Painted_1") + 1, "StaticMeshComponent0");
        var slots = ((ArrayValue)edited.ReadProperties(first).Find("OverrideMaterials")!.Value).Items.Cast<ObjectValue>().Select(o => o.Index).ToList();
        Assert.Equal([0, paint], slots);
        Assert.Equal(1, edited.Imports.Count(i => edited.ResolveName(i.ObjectName) == "MI_A")); // reused, not added again
        Assert.Contains(paint, CreateBeforeSerialize(edited, first));

        var second = Child(edited, Child(edited, level, "Painted_2") + 1, "StaticMeshComponent0");
        var created = ((ArrayValue)edited.ReadProperties(second).Find("OverrideMaterials")!.Value).Items.Cast<ObjectValue>().Select(o => o.Index).ToList();
        Assert.Equal("/Game/X/M_New.M_New", edited.GetFullPath(created[0]));
        Assert.Equal("Material", edited.ResolveName(edited.Imports[-created[0] - 1].ClassName));
        Assert.Equal(0, created[1]);
        Assert.Contains(created[0], CreateBeforeSerialize(edited, second));

        var document = LoadThroughCue4Parse(bytes, "A_0_Tile");
        Assert.Equal<string?>([null, PaintA], document.FindActor("Painted_1")!.Root!.OverrideMaterials!);
    }

    [Fact]
    public void LootWrittenOntoACopysEntryIsCreatedBeforeTheEntryIsRead()
    {
        // F055: the copy's ExamineAssetData named its new preset without listing it as a dependency.
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_Tile", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var conz = p.PackageImport("/Script/ConZ");
        var examineClass = p.Import("/Script/CoreUObject", "Class", conz, "ExamineAssetData");
        var examineDefault = p.Import("/Script/ConZ", "ExamineAssetData", conz, "Default__ExamineAssetData");
        var oldPreset = p.ObjectImport("/Game/Loot/Examine_Old", Engine, "BlueprintGeneratedClass", "Examine_Old_C");
        var rock = p.ObjectImport(SyntheticLevels.RockPackage, Engine, "StaticMesh", "SM_Rock");
        var crate = p.Export("Crate_1", p.ScriptClass(Engine, "StaticMeshActor"), level);
        var mesh = p.Export("StaticMeshComponent0", p.ScriptClass(Engine, "StaticMeshComponent"), crate);
        var entry = p.Export("ExamineAssetData_0", examineClass, mesh, template: examineDefault);
        p.SetPayload(world, p.Properties(native: w => { w.I32(level); w.I32(0); w.I32(0); }));
        p.SetPayload(level, p.Properties(native: w => LevelTail(w, [crate])));
        p.SetPayload(crate, p.Properties(t => t.Object("RootComponent", mesh)));
        p.SetPayload(mesh, p.Properties(t =>
        {
            t.Object("StaticMesh", rock);
            t.ObjectArray("AssetUserData", entry);
        }, w => w.I32(0)));
        p.SetPayload(entry, p.Properties(t => t.TaggedStruct("SpawnerPreset", "ItemSpawnerPresetWithOverrides", s => s.Object("Preset", oldPreset))));
        var built = p.Build();
        var package = CookedPackage.Parse(built.UAsset, built.UExp);

        var (bytes, report) = LevelPackageEditor.Apply(package, new LevelEditRequest
        {
            Copies = [new ActorCopy("Crate_1", "Crate_1_Copy", null)],
            Loot = [new LootPatch("Crate_1_Copy", "StaticMeshComponent0", new LootSetting(true, ["/Game/Loot/Examine_New.Examine_New_C"]))],
        });

        Assert.Empty(report.Warnings);
        var edited = CookedPackage.Parse(bytes.UAsset, bytes.UExp);
        var copyEntry = Child(edited, Child(edited, Child(edited, level, "Crate_1_Copy") + 1, "StaticMeshComponent0") + 1, "ExamineAssetData_0");
        Assert.True(copyEntry >= package.Exports.Count, "the copy's own entry");
        var preset = ((ObjectValue)((StructValue)edited.ReadProperties(copyEntry).Find("SpawnerPreset")!.Value).Properties[0].Value).Index;
        Assert.Equal("/Game/Loot/Examine_New.Examine_New_C", edited.GetFullPath(preset));
        Assert.Contains(preset, CreateBeforeSerialize(edited, copyEntry));
    }

    /// <summary>Minimal ULevel native data: Actors, FURL, Model, ModelComponents, LevelScriptActor, NavListStart/End.</summary>
    internal static void LevelTail(ByteWriter w, IReadOnlyList<int> actors)
    {
        w.I32(actors.Count);
        foreach (var a in actors)
        {
            w.I32(a);
        }

        w.FString("unreal");
        w.FString(string.Empty);
        w.FString("A_0_Tile");
        w.FString(string.Empty);
        w.I32(0);
        w.I32(7777);
        w.I32(1);
        w.I32(0); // Model
        w.I32(0); // ModelComponents
        w.I32(0); // LevelScriptActor
        w.I32(0); // NavListStart
        w.I32(0); // NavListEnd
    }

    /// <summary>The 0-based index of the export named <paramref name="name"/> directly under package index <paramref name="outer"/>.</summary>
    internal static int Child(CookedPackage package, int outer, string name) =>
        Enumerable.Range(0, package.Exports.Count).Single(i => package.Exports[i].OuterIndex == outer && package.ResolveName(package.Exports[i].ObjectName) == name);

    /// <summary>The create-before-serialize dependencies of an export.</summary>
    internal static IEnumerable<int> CreateBeforeSerialize(CookedPackage package, int exportIndex)
    {
        var entry = package.Exports[exportIndex];
        return entry.FirstExportDependency < 0 ? [] : package.ReadPreloadDependencies()
            .Skip(entry.FirstExportDependency + entry.SerializationBeforeSerializationDependencies)
            .Take(entry.CreateBeforeSerializationDependencies);
    }

    private static CookedPackage Synthetic()
    {
        var bytes = SyntheticLevels.BuildLevel();
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp);
    }

    private static LevelDocument LoadThroughCue4Parse(PackageBytes bytes, string levelName = "A_0_TestLevel")
    {
        using var temp = new LevelTempDirectory();
        var basePath = temp.Combine("SCUM", "Content", "ConZ_Files", "Maps", "The_Island", levelName);
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        bytes.WriteAsync(basePath, ".umap").GetAwaiter().GetResult();
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        return LevelDocument.Load(new Cue4ParseLevelReader(catalog), "/Game/ConZ_Files/Maps/The_Island/" + levelName);
    }
}
