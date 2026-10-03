using System.Text.Json;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;

namespace ScumStudio.Tests.Level;

public sealed class LevelDocumentTests
{
    private const string LevelPath = "/Game/ConZ_Files/Maps/The_Island/A_0_Outpost";
    private const string RockMesh = "/Game/ConZ_Files/Models/Rocks/SM_Rock.SM_Rock";
    private const float Tolerance = 1e-3f;

    /// <summary>
    /// A level with nested and cross-actor attachments, absolute rotation, an ISM, lights, volumes, a Blueprint without a
    /// stored RootComponent and a few dangling references.
    /// </summary>
    private static (LevelData Data, Ids Ids) BuildSample()
    {
        var b = new FakeLevelBuilder(LevelPath);
        var ids = new Ids();

        // House: root at (1000, 0, 0), yaw 90, scale 2; a door 100 cm forward; a lamp 50 cm above the door, rolled 90, half size.
        ids.House = b.Actor("StaticMeshActor_House", "/Script/Engine.StaticMeshActor");
        ids.HouseRoot = b.Component(ids.House, "StaticMeshComponent0", "StaticMeshComponent",
            location: new FVector(1000, 0, 0), rotation: new FRotator(0, 90, 0), scale: new FVector(2, 2, 2), mesh: "/Game/ConZ_Files/Models/House/SM_House.SM_House");
        ids.Door = b.Component(ids.House, "Door", "StaticMeshComponent", location: new FVector(100, 0, 0), attachTo: ids.HouseRoot,
            mesh: "/Game/ConZ_Files/Models/House/SM_Door.SM_Door");
        ids.Lamp = b.Component(ids.House, "Lamp", "PointLightComponent", location: new FVector(0, 0, 50), rotation: new FRotator(0, 0, 90),
            scale: new FVector(0.5f, 0.5f, 0.5f), attachTo: ids.Door);
        ids.Compass = b.Component(ids.House, "Compass", "SceneComponent", location: new FVector(0, 10, 0), rotation: new FRotator(0, 45, 0),
            attachTo: ids.HouseRoot, absoluteRotation: true);
        b.Subobject(ids.HouseRoot, "BodySetup_0", "BodySetup");
        b.Component(ids.House, "Tick", "ActorComponent", scene: false);
        b.Root(ids.House, ids.HouseRoot);

        // Sign actor attached to the house's door (attached actors).
        ids.Sign = b.Actor("StaticMeshActor_Sign", "/Script/Engine.StaticMeshActor");
        ids.SignRoot = b.Component(ids.Sign, "StaticMeshComponent0", "StaticMeshComponent", location: new FVector(0, 0, 200),
            attachTo: ids.Door, mesh: "/Game/ConZ_Files/Models/House/SM_Sign.SM_Sign");
        b.Root(ids.Sign, ids.SignRoot);

        // Blueprint lamp without RootComponent: the root is inferred (DefaultSceneRoot preferred).
        ids.Blueprint = b.Actor("BP_Lamp_C_3", "/Game/ConZ_Files/Blueprints/Lights/BP_Lamp.BP_Lamp_C", blueprint: true);
        ids.BlueprintBulb = b.Component(ids.Blueprint, "Bulb", "StaticMeshComponent", location: new FVector(0, 0, 30), mesh: RockMesh);
        ids.BlueprintRoot = b.Component(ids.Blueprint, "DefaultSceneRoot", "SceneComponent", location: new FVector(-500, 250, 0));
        b.Attach(ids.BlueprintBulb, ids.BlueprintRoot);

        // Foliage-like ISM: instances in component space.
        ids.Rocks = b.Actor("InstancedFoliageActor_0", "/Script/Foliage.InstancedFoliageActor");
        ids.RocksRoot = b.Component(ids.Rocks, "DefaultSceneRoot", "SceneComponent", location: new FVector(0, 0, 100));
        ids.RocksIsm = b.Component(ids.Rocks, "FoliageInstancedStaticMeshComponent_0", "FoliageInstancedStaticMeshComponent",
            location: new FVector(10, 0, 0), attachTo: ids.RocksRoot, mesh: RockMesh,
            instances:
            [
                new FTransform(new FVector(1, 2, 3)),
                new FTransform(new FRotator(0, 90, 0), new FVector(100, 0, 0), new FVector(3, 3, 3)),
                new FTransform(FQuat.Identity, new FVector(-5, 5, 0), new FVector(1, 1, 2)),
            ]);
        b.Root(ids.Rocks, ids.RocksRoot);

        ids.Light = b.Actor("PointLight_1", "/Script/Engine.PointLight");
        b.Root(ids.Light, b.Component(ids.Light, "LightComponent0", "PointLightComponent", location: new FVector(5, 5, 5)));
        ids.Volume = b.Actor("BlockingVolume_2", "/Script/Engine.BlockingVolume");
        b.Root(ids.Volume, b.Component(ids.Volume, "BrushComponent0", "BrushComponent"));
        ids.Settings = b.Actor("WorldSettings", "/Script/Engine.WorldSettings");

        // Broken data: an attach cycle and dangling references.
        ids.Loop = b.Actor("StaticMeshActor_Loop", "/Script/Engine.StaticMeshActor");
        var loopA = b.Component(ids.Loop, "A", "SceneComponent", location: new FVector(1, 0, 0));
        var loopB = b.Component(ids.Loop, "B", "SceneComponent", location: new FVector(0, 1, 0), attachTo: loopA);
        b.Attach(loopA, loopB);
        b.Root(ids.Loop, loopA);
        ids.Broken = b.Actor("StaticMeshActor_Broken", "/Script/Engine.StaticMeshActor");
        b.Root(ids.Broken, 9999);
        b.DanglingActor(8888);
        b.Warning("reader warning");
        return (b.Build(), ids);
    }

    [Fact]
    public void BuildsActorsInLevelOrderWithKinds()
    {
        var (data, ids) = BuildSample();
        var doc = LevelDocument.FromData(data);

        Assert.Equal(LevelPath, doc.PackagePath);
        Assert.Equal("A_0_Outpost", doc.Name);
        Assert.Equal(
            new[]
            {
                "StaticMeshActor_House", "StaticMeshActor_Sign", "BP_Lamp_C_3", "InstancedFoliageActor_0", "PointLight_1", "BlockingVolume_2",
                "WorldSettings", "StaticMeshActor_Loop", "StaticMeshActor_Broken",
            },
            doc.Actors.Select(a => a.Name));
        Assert.Equal(ActorKind.StaticMeshActor, doc.FindActor("StaticMeshActor_House")!.Kind);
        Assert.Equal(ActorKind.Blueprint, doc.FindActor(ids.Blueprint)!.Kind);
        Assert.Equal(ActorKind.Light, doc.FindActor("pointlight_1")!.Kind);
        Assert.Equal(ActorKind.Volume, doc.FindActor("BlockingVolume_2")!.Kind);
        Assert.Equal(ActorKind.Other, doc.FindActor("WorldSettings")!.Kind);
        Assert.Equal(ActorKind.Other, doc.FindActor("InstancedFoliageActor_0")!.Kind);
        Assert.Equal(4, doc.CountByKind()[ActorKind.StaticMeshActor]);
        Assert.Equal("BP_Lamp_C", doc.FindActor(ids.Blueprint)!.ClassName);
        Assert.Equal("/Game/ConZ_Files/Blueprints/Lights/BP_Lamp.BP_Lamp_C", doc.FindActor(ids.Blueprint)!.ClassPath);
        Assert.Null(doc.FindActor("Nope"));
    }

    [Fact]
    public void CollectsOwnComponentsOnly()
    {
        var (data, ids) = BuildSample();
        var house = LevelDocument.FromData(data).FindActor(ids.House)!;

        // Components outered to the actor, in export order; the BodySetup (outer = component) is not one.
        Assert.Equal(new[] { "StaticMeshComponent0", "Door", "Lamp", "Compass", "Tick" }, house.Components.Select(c => c.Name));
        Assert.False(house.FindComponent("Tick")!.IsSceneComponent);
        Assert.Equal(ids.HouseRoot, house.RootComponent);
        Assert.Equal("StaticMeshComponent0", house.Root!.Name);
        Assert.False(house.RootInferred);
        Assert.Equal("/Game/ConZ_Files/Models/House/SM_House.SM_House", house.StaticMeshPath);
        Assert.Equal(ids.HouseRoot, house.FindComponent("door")!.AttachParent);
    }

    [Fact]
    public void ComposesWorldTransformsAlongAttachParentChains()
    {
        var (data, ids) = BuildSample();
        var doc = LevelDocument.FromData(data);
        var house = doc.FindActor(ids.House)!;

        var rootRel = new FTransform(new FRotator(0, 90, 0), new FVector(1000, 0, 0), new FVector(2, 2, 2));
        var doorRel = new FTransform(new FVector(100, 0, 0));
        var lampRel = new FTransform(new FRotator(0, 0, 90), new FVector(0, 0, 50), new FVector(0.5f, 0.5f, 0.5f));

        // Checked against Core math (child world = relative * parent world) ...
        var rootWorld = rootRel;
        var doorWorld = doorRel * rootWorld;
        var lampWorld = lampRel * doorWorld;
        AssertNear(rootWorld, house.WorldTransform);
        AssertNear(doorWorld, house.FindComponent("Door")!.WorldTransform);
        AssertNear(lampWorld, house.FindComponent("Lamp")!.WorldTransform);

        // ... and against hand-computed values: the door is 100 cm forward, scaled x2 and yawed 90 -> +200 on Y.
        AssertNear(new FVector(1000, 200, 0), house.FindComponent("Door")!.WorldTransform.Translation);
        AssertNear(new FVector(1000, 200, 100), house.FindComponent("Lamp")!.WorldTransform.Translation);
        AssertNear(new FVector(1, 1, 1), house.FindComponent("Lamp")!.WorldTransform.Scale3D);
        var lampRotator = house.FindComponent("Lamp")!.WorldTransform.Rotator();
        Assert.Equal(90f, lampRotator.Yaw, Tolerance);
        Assert.Equal(90f, lampRotator.Roll, Tolerance);

        // Relative transforms are kept as stored.
        AssertNear(doorRel, house.FindComponent("Door")!.RelativeTransform);
    }

    [Fact]
    public void HonoursAbsoluteRotationFlag()
    {
        var (data, ids) = BuildSample();
        var compass = LevelDocument.FromData(data).FindActor(ids.House)!.FindComponent("Compass")!;

        // Location follows the parent (10 cm right, x2, yaw 90 -> -20 on X), rotation stays world yaw 45.
        AssertNear(new FVector(980, 0, 0), compass.WorldTransform.Translation);
        Assert.Equal(45f, compass.WorldTransform.Rotator().Yaw, Tolerance);
        AssertNear(new FVector(2, 2, 2), compass.WorldTransform.Scale3D);
    }

    [Fact]
    public void ComposesAcrossActors()
    {
        var (data, ids) = BuildSample();
        var doc = LevelDocument.FromData(data);
        var door = doc.FindActor(ids.House)!.FindComponent("Door")!;
        var sign = doc.FindActor(ids.Sign)!;

        var expected = new FTransform(new FVector(0, 0, 200)) * door.WorldTransform;
        AssertNear(expected, sign.WorldTransform);
        AssertNear(new FVector(1000, 200, 400), sign.WorldTransform.Translation);
        Assert.Equal(ids.Door, sign.Root!.AttachParent);
    }

    [Fact]
    public void ComposesThroughTheAttachSocket()
    {
        // A component on a mesh socket sits at Relative * Socket * Parent (the ApexHunt display joints: socket scale 0.01,
        // relative scale 100, so the joint keeps its parent's size instead of growing 100x per link).
        var (data, ids) = BuildSample();
        var exports = data.Exports.ToArray();
        var socket = new FTransform(FQuat.Identity, new FVector(0, 0, 50), new FVector(0.01f, 0.01f, 0.01f));
        exports[ids.SignRoot] = exports[ids.SignRoot] with { RelativeScale3D = new FVector(100, 100, 100), AttachSocket = socket };
        var doc = LevelDocument.FromData(data with { Exports = exports });
        var door = doc.FindActor(ids.House)!.FindComponent("Door")!;
        var sign = doc.FindActor(ids.Sign)!;

        AssertNear(door.WorldTransform.Scale3D, sign.WorldTransform.Scale3D);
        AssertNear(new FTransform(FQuat.Identity, new FVector(0, 0, 200), new FVector(100, 100, 100)) * socket * door.WorldTransform, sign.WorldTransform);
    }

    [Fact]
    public void InfersMissingRootComponent()
    {
        var (data, ids) = BuildSample();
        var doc = LevelDocument.FromData(data);
        var bp = doc.FindActor(ids.Blueprint)!;

        Assert.True(bp.RootInferred);
        Assert.Equal(ids.BlueprintRoot, bp.RootComponent);
        AssertNear(new FVector(-500, 250, 0), bp.WorldTransform.Translation);
        AssertNear(new FVector(-500, 250, 30), bp.FindComponent("Bulb")!.WorldTransform.Translation);
        // The root has no mesh: the Blueprint shows its bulb as a representative mesh, but "of the kind" still matches
        // Blueprints by class or root mesh, not by a sub-mesh.
        Assert.Equal(RockMesh, bp.StaticMeshPath);
        Assert.False(EditOpFactory.Matches(bp, new KindMatch(MatchBy.StaticMesh, RockMesh)));
        Assert.Contains(doc.Warnings, w => w.Contains("BP_Lamp_C_3", StringComparison.Ordinal) && w.Contains("RootComponent", StringComparison.Ordinal));

        var settings = doc.FindActor(ids.Settings)!;
        Assert.Null(settings.RootComponent);
        Assert.Equal(FTransform.Identity, settings.WorldTransform);
    }

    [Fact]
    public void ExpandsInstancesToWorldSpace()
    {
        var (data, ids) = BuildSample();
        var doc = LevelDocument.FromData(data);
        var rocks = doc.FindActor(ids.Rocks)!;
        var ism = rocks.FindComponent("FoliageInstancedStaticMeshComponent_0")!;

        Assert.True(ism.IsInstanced);
        Assert.Equal(3, ism.Instances.Count);
        Assert.Equal(3, rocks.InstanceTransforms.Count);
        Assert.Equal(3, doc.InstanceCount);
        AssertNear(new FVector(10, 0, 100), ism.WorldTransform.Translation);
        for (var i = 0; i < 3; i++)
        {
            var instance = rocks.InstanceTransforms[i];
            Assert.Equal(i, instance.InstanceIndex);
            Assert.Equal(ids.RocksIsm, instance.ComponentExportIndex);
            Assert.Equal(RockMesh, instance.StaticMeshPath);
            AssertNear(ism.Instances[i] * ism.WorldTransform, instance.WorldTransform);
        }

        AssertNear(new FVector(11, 2, 103), rocks.InstanceTransforms[0].WorldTransform.Translation);
        AssertNear(new FVector(110, 0, 100), rocks.InstanceTransforms[1].WorldTransform.Translation);
        AssertNear(new FVector(3, 3, 3), rocks.InstanceTransforms[1].WorldTransform.Scale3D);
        Assert.Null(rocks.StaticMeshPath);
    }

    [Fact]
    public void SurvivesCyclesAndDanglingReferences()
    {
        var (data, ids) = BuildSample();
        var doc = LevelDocument.FromData(data);

        var loop = doc.FindActor(ids.Loop)!;
        Assert.Equal(2, loop.Components.Count);
        Assert.Contains(doc.Warnings, w => w.Contains("cycle", StringComparison.Ordinal));

        var broken = doc.FindActor(ids.Broken)!;
        Assert.Null(broken.RootComponent);
        Assert.Contains(doc.Warnings, w => w.Contains("9999", StringComparison.Ordinal));
        Assert.Contains(doc.Warnings, w => w.Contains("8888", StringComparison.Ordinal));
        Assert.Contains("reader warning", doc.Warnings);
    }

    [Fact]
    public void LoadsThroughReader()
    {
        var (data, _) = BuildSample();
        var reader = new FakeLevelReader().Add(data);
        var doc = LevelDocument.Load(reader, LevelPath);
        Assert.Equal(9, doc.Actors.Count);
        Assert.Equal(1, reader.ReadCount);
        Assert.Throws<FileNotFoundException>(() => LevelDocument.Load(reader, "/Game/Nope"));
    }

    [Fact]
    public async Task LoadsAsync()
    {
        var (data, _) = BuildSample();
        var doc = await LevelDocument.LoadAsync(new FakeLevelReader().Add(data), LevelPath);
        Assert.Equal(9, doc.Actors.Count);
    }

    [Fact]
    public void DumpsJson()
    {
        var (data, ids) = BuildSample();
        var doc = LevelDocument.FromData(data);

        using var json = JsonDocument.Parse(doc.ToJson());
        var root = json.RootElement;
        Assert.Equal(LevelPath, root.GetProperty("package").GetString());
        Assert.Equal(9, root.GetProperty("actorCount").GetInt32());
        Assert.Equal(3, root.GetProperty("instanceCount").GetInt32());
        Assert.Equal(4, root.GetProperty("kinds").GetProperty("StaticMeshActor").GetInt32());
        var actors = root.GetProperty("actors");
        Assert.Equal(9, actors.GetArrayLength());

        var sign = actors.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "StaticMeshActor_Sign");
        Assert.Equal("staticMeshActor", sign.GetProperty("kind").GetString());
        Assert.Equal("/Game/ConZ_Files/Models/House/SM_Sign.SM_Sign", sign.GetProperty("mesh").GetString());
        var location = sign.GetProperty("transform").GetProperty("location").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal(1000f, location[0], Tolerance);
        Assert.Equal(200f, location[1], Tolerance);
        Assert.Equal(400f, location[2], Tolerance);
        Assert.Equal("StaticMeshActor_House.Door", sign.GetProperty("components")[0].GetProperty("parent").GetString());
        var house = actors.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "StaticMeshActor_House");
        Assert.Equal("StaticMeshComponent0", house.GetProperty("components")[1].GetProperty("parent").GetString());
        Assert.Equal(ids.Door, house.GetProperty("components")[1].GetProperty("index").GetInt32());

        var rocks = actors.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "InstancedFoliageActor_0");
        Assert.False(rocks.TryGetProperty("instances", out _));
        using var withInstances = JsonDocument.Parse(doc.ToJson(includeInstances: true, indented: false));
        var rocks2 = withInstances.RootElement.GetProperty("actors").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "InstancedFoliageActor_0");
        Assert.Equal(3, rocks2.GetProperty("instances").GetArrayLength());
    }

    [Fact]
    public void ClassifiesActorClasses()
    {
        Assert.Equal(ActorKind.StaticMeshActor, LevelDocument.ClassifyActor("StaticMeshActor", false));
        Assert.Equal(ActorKind.Blueprint, LevelDocument.ClassifyActor("BP_PointLight_C", true));
        Assert.Equal(ActorKind.Light, LevelDocument.ClassifyActor("SpotLight", false));
        Assert.Equal(ActorKind.Light, LevelDocument.ClassifyActor("DirectionalLight", false));
        Assert.Equal(ActorKind.Volume, LevelDocument.ClassifyActor("PostProcessVolume", false));
        Assert.Equal(ActorKind.Volume, LevelDocument.ClassifyActor("LightmassImportanceVolume", false));
        Assert.Equal(ActorKind.Other, LevelDocument.ClassifyActor("InstancedFoliageActor", false));
        Assert.Equal(ActorKind.Other, LevelDocument.ClassifyActor("A_0_Outpost_C", false));
    }

    private static void AssertNear(FVector expected, FVector actual) =>
        Assert.True(expected.Equals(actual, Tolerance), $"expected {expected}, got {actual}");

    private static void AssertNear(FTransform expected, FTransform actual) =>
        Assert.True(expected.Equals(actual, Tolerance), $"expected {expected}, got {actual}");

    private sealed class Ids
    {
        public int House { get; set; }
        public int HouseRoot { get; set; }
        public int Door { get; set; }
        public int Lamp { get; set; }
        public int Compass { get; set; }
        public int Sign { get; set; }
        public int SignRoot { get; set; }
        public int Blueprint { get; set; }
        public int BlueprintBulb { get; set; }
        public int BlueprintRoot { get; set; }
        public int Rocks { get; set; }
        public int RocksRoot { get; set; }
        public int RocksIsm { get; set; }
        public int Light { get; set; }
        public int Volume { get; set; }
        public int Settings { get; set; }
        public int Loop { get; set; }
        public int Broken { get; set; }
    }
}
