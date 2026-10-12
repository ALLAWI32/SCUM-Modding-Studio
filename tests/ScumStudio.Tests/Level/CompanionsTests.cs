using System.Diagnostics;
using System.Numerics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;

namespace ScumStudio.Tests.Level;

/// <summary>
/// <see cref="Companions"/>: what goes with a deleted object (the owner: "When I delete it, EVERYTHING of it must be
/// deleted: the fire object itself, its sound, its particles, everything").
/// </summary>
public sealed class CompanionsTests
{
    private const string Camp = SyntheticCampLevel.LevelPath;
    private const string Test = "/Game/ConZ_Files/Maps/The_Island/A_0_Finder";
    private const string Water = "/Game/ConZ_Files/Maps/The_Island/WaterSplines";
    private const string LakeTop = "/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_C_1.LakeWaterSurface_C_1";
    private const string LakeUnder = "/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_C_1_FN.LakeWaterSurface_C_1_FN";
    private const string Rock = "/Game/ConZ_Files/Models/Rocks/SM_Rock.SM_Rock";

    private static BoundingBox? Bounds(string mesh) =>
        mesh.Contains("/Landscape/Lake/", StringComparison.Ordinal) ? new BoundingBox(new Vector3(-2000, -2000, -10), new Vector3(2000, 2000, 10))
        : mesh.Contains("SM_Big_House", StringComparison.Ordinal) ? new BoundingBox(new Vector3(-1000), new Vector3(1000))
        : new BoundingBox(new Vector3(-100), new Vector3(100));

    [Fact]
    public void FireplaceTakesItsLightEmitterSoundAndDecalOnly()
    {
        var doc = LoadCamp();
        var report = Companions.Find([doc], [new DeleteSeed(new ActorRef(Camp, "SM_Fireplace_1"))], new EditState(), Bounds);

        var on = report.Companions.Where(c => c.DefaultOn).Select(c => c.Actor.Actor).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Ash_Decal_1", "FireLight_1", "Fire_Sound_1", "Glow_1", "P_Fire_1", "Rock_1"], on);
        Assert.All(report.Companions, c => Assert.Null(c.Component));
        Assert.All(report.Companions, c => Assert.Equal("SM_Fireplace_1", c.Seed.Actor));
        Assert.Equal(CompanionRule.Decal, One(report, "Ash_Decal_1").Rule);
        Assert.Equal(CompanionRule.AtTheSpot, One(report, "Fire_Sound_1").Rule);
        Assert.Equal(CompanionRule.AtTheSpot, One(report, "P_Fire_1").Rule);
        var glow = One(report, "Glow_1");
        Assert.True(glow is { Rule: CompanionRule.Attached, Locked: true, HasOwnMesh: false });
        var rock = One(report, "Rock_1");
        Assert.True(rock is { Rule: CompanionRule.Attached, Locked: false, DefaultOn: true, HasOwnMesh: true });

        // The camp's ambience sound is nearer to the house (70 cm) than to the fireplace (130 cm): the house's, kept.
        var ambience = One(report, "Camp_Ambience");
        Assert.False(ambience.DefaultOn);
        Assert.Equal(new ActorRef(Camp, "BP_House_1"), ambience.BelongsTo);
        Assert.Equal(130, ambience.DistanceCm, 0.5f);
        Assert.Equal(7, report.Companions.Count);
        Assert.DoesNotContain(report.Companions, c => c.Actor.Actor is "Lamp_1" or "DirectionalLight_1" or "WorldSettings" or "Smoke_Volume" or "BP_House_1");
        Assert.Empty(report.Notes);

        // The emitter deleted on its own (an effect pin) takes nothing: the light and the sound are the fireplace's.
        Assert.Empty(Companions.Find([doc], [new DeleteSeed(new ActorRef(Camp, "P_Fire_1"))], new EditState(), Bounds).Companions);
    }

    [Fact]
    public void AFireInsideAKeptHouseGoesWithItsFireplaceButALampPostsLightStays()
    {
        var b = new FakeLevelBuilder(Test);
        Mesh(b, "SM_Big_House_1", "/Game/ConZ_Files/Buildings/SM_Big_House.SM_Big_House", new FVector(0, 0, 0));
        Mesh(b, "SM_Fireplace_1", Rock, new FVector(300, 0, 0));
        Effect(b, "Fire_Sound_1", "/Script/AkAudio.AkAmbientSound", "AkComponent", new FVector(520, 0, 0));
        var post = b.Actor("SM_LampPost_1", "/Script/Engine.StaticMeshActor");
        var postRoot = b.Component(post, "StaticMeshComponent0", "StaticMeshComponent", new FVector(300, 600, 0), mesh: Rock);
        b.Root(post, postRoot);
        var light = b.Actor("Lamp_Light_1", "/Script/Engine.PointLight");
        b.Root(light, b.Component(light, "LightComponent0", "PointLightComponent", new FVector(0, -400, 0), attachTo: postRoot));
        var doc = LevelDocument.FromData(b.Build());

        var report = Companions.Find([doc], [new DeleteSeed(new ActorRef(Test, "SM_Fireplace_1"))], new EditState(), Bounds);

        // The sound is 120 cm off the fireplace and inside the house's bounds: the house is around both, the fire's it is.
        Assert.True(One(report, "Fire_Sound_1") is { DefaultOn: true, BelongsTo: null });

        // The lamp post's light stands nearer to the fireplace (100 cm) than to its post (300 cm), but hangs from the post.
        var lamp = One(report, "Lamp_Light_1");
        Assert.False(lamp.DefaultOn);
        Assert.Equal(new ActorRef(Test, "SM_LampPost_1"), lamp.BelongsTo);
    }

    [Fact]
    public void StoredChildActorsRecurseAndCyclesTerminate()
    {
        var b = new FakeLevelBuilder(Test);
        var gate = b.Actor("BP_Gate_1", "/Game/Gates/BP_Gate.BP_Gate_C", blueprint: true);
        var gateMesh = b.Component(gate, "Frame", "StaticMeshComponent", new FVector(0, 0, 0), mesh: Rock);
        var doorSpawner = b.Component(gate, "DoorSpawner", "ChildActorComponent", new FVector(0, 100, 0), attachTo: gateMesh);
        b.Root(gate, gateMesh);
        var door = b.Actor("Door_1", "/Game/Gates/BP_Door.BP_Door_C", blueprint: true);
        var doorMesh = b.Component(door, "Leaf", "StaticMeshComponent", new FVector(0, 100, 0), mesh: Rock);
        var handleSpawner = b.Component(door, "HandleSpawner", "ChildActorComponent", attachTo: doorMesh);
        b.Root(door, doorMesh);
        var handle = b.Actor("Handle_1", "/Game/Gates/BP_Handle.BP_Handle_C", blueprint: true);
        var handleRoot = b.Component(handle, "Knob", "StaticMeshComponent", new FVector(0, 120, 0), mesh: Rock);
        b.Root(handle, handleRoot);
        var loopA = b.Actor("Loop_A", "/Script/Engine.PointLight");
        var loopARoot = b.Component(loopA, "LightComponent0", "PointLightComponent", new FVector(5000, 0, 0));
        b.Root(loopA, loopARoot);
        var loopB = b.Actor("Loop_B", "/Script/Engine.PointLight");
        var loopBRoot = b.Component(loopB, "LightComponent0", "PointLightComponent", new FVector(5000, 0, 0), attachTo: loopARoot);
        b.Root(loopB, loopBRoot);
        b.Attach(loopARoot, loopBRoot);

        // The door hangs from the gate's spawner, the handle from the door's, and the door's leaf is attached to the
        // handle: a cycle the walk must end.
        b.Attach(doorMesh, handleRoot);
        var data = b.Build();
        data = data with
        {
            Exports = data.Exports.Select(e => e.Index == door ? e with { ParentComponent = doorSpawner }
                : e.Index == handle ? e with { ParentComponent = handleSpawner }
                : e.Index == doorSpawner ? e with { ChildActor = door }
                : e.Index == handleSpawner ? e with { ChildActor = handle }
                : e).ToList(),
        };
        var doc = LevelDocument.FromData(data);

        var report = Companions.Find([doc], [new DeleteSeed(new ActorRef(Test, "BP_Gate_1"))], new EditState(), Bounds);

        Assert.Equal(["Door_1", "Handle_1"], report.Companions.Select(c => c.Actor.Actor).Order(StringComparer.Ordinal));
        Assert.All(report.Companions, c => Assert.True(c is { Rule: CompanionRule.ChildActor, Locked: true, DefaultOn: true }));
        var dependents = Attachments.Dependents(doc, new HashSet<string>(["bp_gate_1"]));
        Assert.Equal(["Door_1", "Handle_1"], dependents.Select(d => d.Actor.Name));
        Assert.Equal("Door_1", dependents[1].Parent.Name);
        Assert.Empty(Attachments.Dependents(doc, new HashSet<string>(["Nobody"])));

        // Only the gate's spawner goes (a component of a kept gate): the door and its handle still go, from the gate.
        var bySpawner = Attachments.Dependents(doc, new HashSet<string>(), new HashSet<int>([doorSpawner]));
        Assert.Equal(["Door_1", "Handle_1"], bySpawner.Select(d => d.Actor.Name));
        Assert.True(bySpawner[0] is { Kind: DependentKind.ChildActor, Parent.Name: "BP_Gate_1" } && bySpawner[0].ViaComponent == doorSpawner);

        // A cycle of two lights attached to each other: deleting one takes the other, and the walk ends.
        var loop = Attachments.Dependents(doc, new HashSet<string>(["Loop_A"]));
        Assert.Equal(["Loop_B"], loop.Select(d => d.Actor.Name));
        Assert.Equal(DependentKind.Attached, loop[0].Kind);
    }

    [Fact]
    public void APartOfAKeptHouseTakesFireFireLightFireSoundButNotPorchLight()
    {
        var doc = LoadCamp();
        var house = new ActorRef(Camp, "BP_House_1");

        var report = Companions.Find([doc], [new DeleteSeed(house, "Fireplace")], new EditState(), Bounds);

        Assert.Equal(["Fire", "FireLight", "FireSound"], report.Companions.Select(c => c.Component!).Order(StringComparer.Ordinal));
        Assert.All(report.Companions, c => Assert.True(c.Actor == house && c is { Rule: CompanionRule.OwnEffect, DefaultOn: true } && c.Seed == house));
        Assert.True(report.Companions.Single(c => c.Component == "Fire").Locked); // attached under the fireplace
        Assert.False(report.Companions.Single(c => c.Component == "FireLight").Locked);
        Assert.Equal("AkComponent", report.Companions.Single(c => c.Component == "FireSound").ClassName);

        // With the walls deleted as well, the porch light (by the walls) goes with them, not with the fireplace.
        var both = Companions.Find([doc], [new DeleteSeed(house, "Fireplace"), new DeleteSeed(house, "Walls")], new EditState(), Bounds);
        Assert.Equal(house, both.Companions.Single(c => c.Component == "PorchLight").Seed);

        // A fireplace part whose house already lost its walls: nothing of the house is nearer, the same three go.
        var state = new EditState();
        state.Apply(new SetTransformOp(house, TransformValue.Identity, TransformValue.Identity with { Scale = new FVector(0, 0, 0) }, "Walls"));
        Assert.Equal(3, Companions.Find([doc], [new DeleteSeed(house, "Fireplace")], state, Bounds).Companions.Count);
    }

    [Fact]
    public void ASynthesizedEffectGivesAClassEffectStaysNote()
    {
        var b = new FakeLevelBuilder(Test);
        var cabin = b.Actor("BP_Cabin_1", "/Game/Cabins/BP_Cabin.BP_Cabin_C", blueprint: true);
        var root = b.Component(cabin, "DefaultSceneRoot", location: new FVector(0, 0, 0));
        b.Component(cabin, "Walls", "StaticMeshComponent", new FVector(0, -500, 0), attachTo: root, mesh: Rock);
        var stove = b.Component(cabin, "Stove", "StaticMeshComponent", new FVector(0, 300, 0), attachTo: root, mesh: Rock);
        b.Root(cabin, root);
        var data = b.Build();
        data = data with
        {
            SynthesizedComponents =
            [
                new LevelExportData
                {
                    Index = -2, Name = "StoveFire", ClassName = "ParticleSystemComponent", ClassPath = "/Script/Engine.ParticleSystemComponent",
                    OuterIndex = cabin, IsComponent = true, IsSceneComponent = true, IsSynthesized = true, AttachParent = stove,
                    TemplatePath = "/Game/Cabins/BP_Cabin.BP_Cabin_C:StoveFire_GEN_VARIABLE",
                },
            ],
        };
        var doc = LevelDocument.FromData(data);
        Assert.True(doc.FindActor("BP_Cabin_1")!.FindComponent("StoveFire")!.IsSynthesized);
        var cabinRef = new ActorRef(Test, "BP_Cabin_1");

        var report = Companions.Find([doc], [new DeleteSeed(cabinRef, "Stove")], new EditState(), Bounds);

        Assert.Empty(report.Companions);
        Assert.Equal([new CompanionNote(CompanionNoteKind.ClassEffectStays, cabinRef, "StoveFire")], report.Notes);
    }

    [Fact]
    public void WaterTopAndUndersideAreTwins()
    {
        var b = new FakeLevelBuilder(Water);
        Mesh(b, "Lake_Top", LakeTop, new FVector(50000, 50000, 100));
        Mesh(b, "Lake_Under", LakeUnder, new FVector(50000, 50000, 100.5f));
        Mesh(b, "Other_Lake_Under", "/Game/ConZ_Files/Landscape/Lake/LakeWaterSurface_C_2_FN.LakeWaterSurface_C_2_FN", new FVector(50000, 50000, 100));
        Mesh(b, "Far_Under", LakeUnder, new FVector(90000, 50000, 100));
        Effect(b, "Lake_Swim", "/Script/Engine.PhysicsVolume", "BrushComponent", new FVector(50000, 50000, -150));
        Effect(b, "Wide_Swim", "/Script/Engine.PhysicsVolume", "BrushComponent", new FVector(52000, 50000, -150));
        Effect(b, "Lake_Trigger", "/Script/Engine.TriggerVolume", "BrushComponent", new FVector(50000, 50000, -150));
        LevelDocument Swim(LevelData data) => LevelDocument.FromData(data with
        {
            Exports = data.Exports.Select(e => e.Name is "Lake_Swim" or "Wide_Swim" ? e with { WaterVolume = true } : e).ToList(),
        });
        var doc = Swim(b.Build());

        var fromUnder = Companions.Find([doc], [new DeleteSeed(new ActorRef(Water, "Lake_Under"))], new EditState(), Bounds);
        var fromTop = Companions.Find([doc], [new DeleteSeed(new ActorRef(Water, "Lake_Top"))], new EditState(), Bounds);

        var twin = Assert.Single(fromUnder.Companions);
        Assert.True(twin is { Rule: CompanionRule.WaterTwin, Locked: true, DefaultOn: true, Component: null });
        Assert.Equal("Lake_Top", twin.Actor.Actor);
        Assert.Equal("Lake_Under", Assert.Single(fromTop.Companions).Actor.Actor);

        // The swim volume lies under the lake (5 m deep, so most of it is outside the surface's reach) and goes with it; a
        // trigger there is offered, off. One twice the lake's length is shared with the water that stays.
        var options = new CompanionOptions
        {
            VolumeBounds = a => a.Name switch
            {
                "Lake_Swim" or "Lake_Trigger" => new BoundingBox(new Vector3(48100, 48100, -400), new Vector3(51900, 51900, 100)),
                "Wide_Swim" => new BoundingBox(new Vector3(48100, 48100, -400), new Vector3(55900, 51900, 100)),
                _ => null,
            },
        };
        var withVolumes = Companions.Find([doc], [new DeleteSeed(new ActorRef(Water, "Lake_Under"))], new EditState(), Bounds, options);
        Assert.True(One(withVolumes, "Lake_Swim") is { Rule: CompanionRule.Volume, Locked: false, DefaultOn: true });
        Assert.Equal("Lake_Under", One(withVolumes, "Lake_Swim").Seed.Actor);
        Assert.True(One(withVolumes, "Lake_Trigger") is { Rule: CompanionRule.Volume, DefaultOn: false });
        Assert.False(One(withVolumes, "Wide_Swim").DefaultOn);
        Assert.Equal(["Lake_Trigger", "Wide_Swim"], withVolumes.Notes.Where(n => n.Kind == CompanionNoteKind.SharedKept).Select(n => n.Actor.Actor).Order(StringComparer.Ordinal));

        // The same swim volume by a pier (not water) stays.
        Mesh(b, "Pier_1", Rock, new FVector(50000, 50000, 150));
        var pier = Companions.Find([Swim(b.Build())], [new DeleteSeed(new ActorRef(Water, "Pier_1"))], new EditState(), Bounds, options);
        Assert.False(One(pier, "Lake_Swim").DefaultOn);

        // A water body made of one actor's two components: the other side is that component.
        var c = new FakeLevelBuilder(Water);
        var lake = c.Actor("BP_Lake_1", "/Game/Water/BP_Lake.BP_Lake_C", blueprint: true);
        var lakeRoot = c.Component(lake, "DefaultSceneRoot", location: new FVector(0, 0, 0));
        c.Component(lake, "Top", "StaticMeshComponent", new FVector(0, 0, 0), attachTo: lakeRoot, mesh: LakeTop);
        c.Component(lake, "Under", "StaticMeshComponent", new FVector(0, 0, 0), attachTo: lakeRoot, mesh: LakeUnder);
        c.Component(lake, "Bank", "StaticMeshComponent", new FVector(3000, 0, 0), attachTo: lakeRoot, mesh: Rock);
        c.Root(lake, lakeRoot);
        var parts = Companions.Find([LevelDocument.FromData(c.Build())], [new DeleteSeed(new ActorRef(Water, "BP_Lake_1"), "Under")], new EditState(), Bounds);
        Assert.Equal("Top", Assert.Single(parts.Companions, x => x.Rule == CompanionRule.WaterTwin).Component);
    }

    [Fact]
    public void ACampfireSoundByALakeIsNotDefaultOnForTheLake()
    {
        var b = new FakeLevelBuilder(Water);
        Mesh(b, "Lake_Under", LakeUnder, new FVector(0, 0, 0));
        Effect(b, "Campfire_Sound", "/Script/AkAudio.AkAmbientSound", "AkComponent", new FVector(2050, 0, 0));
        Effect(b, "Lake_Ambience", "/Script/AkAudio.AkAmbientSound", "AkComponent", new FVector(2050, 100, 0));
        Effect(b, "Foam_1", "/Script/Engine.Emitter", "ParticleSystemComponent", new FVector(1000, 0, 0));
        var doc = LevelDocument.FromData(b.Build());

        var report = Companions.Find([doc], [new DeleteSeed(new ActorRef(Water, "Lake_Under"))], new EditState(), Bounds);

        Assert.False(One(report, "Campfire_Sound").DefaultOn);
        Assert.Null(One(report, "Campfire_Sound").BelongsTo);
        Assert.True(One(report, "Lake_Ambience").DefaultOn);
        Assert.True(One(report, "Foam_1").DefaultOn);
    }

    [Fact]
    public void TradePostsSpawnersSentriesLevelScriptAndVolumesAreNeverTaken()
    {
        var b = new FakeLevelBuilder(Test);
        Mesh(b, "SM_Fireplace_1", Rock, new FVector(0, 0, 0));
        var post = Effect(b, "BP_TradePost_1", "/Game/Trade/BP_TradePost.BP_TradePost_C", "PointLightComponent", new FVector(50, 0, 0), blueprint: true);
        b.Traders(post, new TraderMarker(FTransform.Identity, "A_0_Armory", "Armorer", string.Empty, string.Empty));
        Effect(b, "SentrySpawner2_1", "/Script/ConZ.SentrySpawner2", "PointLightComponent", new FVector(0, 50, 0));
        Effect(b, "BP_FireSpawner_1", "/Game/Spawn/BP_FireSpawner.BP_FireSpawner_C", "ParticleSystemComponent", new FVector(0, -50, 0), blueprint: true);
        var stash = Effect(b, "BP_Stash_Light_1", "/Game/Loot/BP_Stash_Light.BP_Stash_Light_C", "PointLightComponent", new FVector(-50, 0, 0), blueprint: true);
        var script = Effect(b, "A_0_Finder_C_0", "/Game/Maps/A_0_Finder.A_0_Finder_C", "ParticleSystemComponent", new FVector(0, 0, 10), blueprint: true);
        Effect(b, "DirectionalLight_1", "/Script/Engine.DirectionalLight", "DirectionalLightComponent", new FVector(0, 0, 100));
        Effect(b, "SkyLight_1", "/Script/Engine.SkyLight", "SkyLightComponent", new FVector(0, 0, 100));
        Effect(b, "ExponentialHeightFog_1", "/Script/Engine.ExponentialHeightFog", "ExponentialHeightFogComponent", new FVector(0, 0, 100));
        Effect(b, "WorldSettings", "/Script/Engine.WorldSettings", "SceneComponent", new FVector(0, 0, 0));
        Effect(b, "Fire_Volume", "/Script/Engine.TriggerVolume", "BrushComponent", new FVector(0, 0, 0));
        Effect(b, "Outpost_Volume", "/Script/Engine.PostProcessVolume", "BrushComponent", new FVector(0, 0, 0));
        Effect(b, "Outpost_Trigger", "/Script/Engine.TriggerVolume", "BrushComponent", new FVector(0, 0, 0));
        Effect(b, "Kept_Light","/Script/Engine.PointLight", "PointLightComponent", new FVector(0, 0, 50));
        var data = b.Build();
        data = data with
        {
            LevelScriptActorIndex = script,
            Exports = data.Exports.Select(e => e.OuterIndex == stash && e.IsComponent
                ? e with { SpawnMarkers = [new SpawnMarker(FTransform.Identity, "World_Shelf", 50, 1, 1)] }
                : e).ToList(),
        };
        var doc = LevelDocument.FromData(data);
        var seed = new DeleteSeed(new ActorRef(Test, "SM_Fireplace_1"));

        var report = Companions.Find([doc], [seed], new EditState(), Bounds);
        Assert.Equal(["Kept_Light"], report.Companions.Select(c => c.Actor.Actor));

        // A deleted spawner warns that what it spawned (and players keep) stays.
        var stashRef = new ActorRef(Test, "BP_Stash_Light_1");
        Assert.Equal([new CompanionNote(CompanionNoteKind.SpawnedItemsStay, stashRef, null)], Companions.Find([doc], [new DeleteSeed(stashRef)], new EditState(), Bounds).Notes);

        // Volumes only with bounds, never on: one inside the fireplace's reach, one outpost-wide (shared, kept). The
        // outpost's post process is never offered.
        var options = new CompanionOptions
        {
            VolumeBounds = a => a.Name switch
            {
                "Fire_Volume" => new BoundingBox(new Vector3(-120), new Vector3(120)),
                "Outpost_Volume" or "Outpost_Trigger" => new BoundingBox(new Vector3(-20000, -20000, -500), new Vector3(20000, 20000, 3000)),
                _ => null,
            },
        };
        var withVolumes = Companions.Find([doc], [seed], new EditState(), Bounds, options);
        var volumes = withVolumes.Companions.Where(c => c.Rule == CompanionRule.Volume).ToList();
        Assert.Equal(["Fire_Volume", "Outpost_Trigger"], volumes.Select(c => c.Actor.Actor).Order(StringComparer.Ordinal));
        Assert.All(volumes, v => Assert.False(v.DefaultOn));
        Assert.DoesNotContain(withVolumes.Companions, c => c.Actor.Actor == "Outpost_Volume");
        Assert.Equal([new CompanionNote(CompanionNoteKind.SharedKept, new ActorRef(Test, "Outpost_Trigger"), null)], withVolumes.Notes);
    }

    [Fact]
    public void ATradePostSentrySpawnerOrVolumeHungOnTheFireplaceIsOfferedNotTaken()
    {
        var b = new FakeLevelBuilder(Test);
        var fireplace = b.Actor("SM_Fireplace_1", "/Script/Engine.StaticMeshActor");
        var fireRoot = b.Component(fireplace, "StaticMeshComponent0", "StaticMeshComponent", new FVector(0, 0, 0), mesh: Rock);
        b.Root(fireplace, fireRoot);
        var (post, postRoot) = Hung(b, "BP_TradePost_1", "/Game/Trade/BP_TradePost.BP_TradePost_C", "SceneComponent", new FVector(50, 0, 0), fireRoot, blueprint: true);
        b.Traders(post, new TraderMarker(FTransform.Identity, "A_0_Armory", "Armorer", string.Empty, string.Empty));
        Hung(b, "SentrySpawner2_1", "/Script/ConZ.SentrySpawner2", "SceneComponent", new FVector(0, 50, 0), fireRoot);
        Hung(b, "Trigger_Volume_1", "/Script/Engine.TriggerVolume", "BrushComponent", new FVector(0, 0, 0), fireRoot);
        Hung(b, "Swim_Volume_1", "/Script/Engine.PhysicsVolume", "BrushComponent", new FVector(0, 0, -100), fireRoot);
        Hung(b, "Fire_Light_1", "/Script/Engine.PointLight", "PointLightComponent", new FVector(0, 0, 60), fireRoot);
        Hung(b, "Post_Lamp_1", "/Script/Engine.PointLight", "PointLightComponent", new FVector(0, 0, 150), postRoot);
        var data = b.Build();
        var doc = LevelDocument.FromData(data with { Exports = data.Exports.Select(e => e.Name == "Swim_Volume_1" ? e with { WaterVolume = true } : e).ToList() });

        var report = Companions.Find([doc], [new DeleteSeed(new ActorRef(Test, "SM_Fireplace_1"))], new EditState(), Bounds);

        Assert.True(One(report, "Fire_Light_1") is { Rule: CompanionRule.Attached, Locked: true, DefaultOn: true });
        foreach (var kept in new[] { "BP_TradePost_1", "SentrySpawner2_1", "Trigger_Volume_1" })
        {
            Assert.True(One(report, kept) is { Rule: CompanionRule.Attached, Locked: false, DefaultOn: false }, kept);
        }

        // The swim volume hung on the deleted thing fills it: offered on. The trade post's lamp stays with the post.
        Assert.True(One(report, "Swim_Volume_1") is { Rule: CompanionRule.Attached, Locked: false, DefaultOn: true });
        Assert.DoesNotContain(report.Companions, c => c.Actor.Actor == "Post_Lamp_1");
        Assert.Equal(5, report.Companions.Count);
        Assert.All(report.Notes, n => Assert.Equal(CompanionNoteKind.AttachedKept, n.Kind));
        Assert.Equal(["BP_TradePost_1", "SentrySpawner2_1", "Trigger_Volume_1"], report.Notes.Select(n => n.Actor.Actor).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AlreadyDeletedActorsAreSkipped()
    {
        var doc = LoadCamp();
        var state = new EditState();
        state.Apply(new DeleteActorOp(new ActorRef(Camp, "FireLight_1")));
        state.Apply(new DeleteActorOp(new ActorRef(Camp, "Rock_1")));
        state.Apply(new DeleteActorOp(new ActorRef(Camp, "BP_House_1")));

        var report = Companions.Find([doc], [new DeleteSeed(new ActorRef(Camp, "SM_Fireplace_1"))], state, Bounds);

        Assert.DoesNotContain(report.Companions, c => c.Actor.Actor is "FireLight_1" or "Rock_1" or "BP_House_1");

        // The house is gone, so the camp ambience is nobody else's: it goes with the fireplace.
        Assert.True(One(report, "Camp_Ambience") is { DefaultOn: true, BelongsTo: null });
        Assert.Equal(["Ash_Decal_1", "Camp_Ambience", "Fire_Sound_1", "Glow_1", "P_Fire_1"], report.Companions.Select(c => c.Actor.Actor).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FindIsFastOnALoadedIsland()
    {
        var b = new FakeLevelBuilder(Test);
        var seeds = new List<DeleteSeed>();
        for (var i = 0; i < 25_000; i++)
        {
            var at = new FVector(i % 160 * 2500f, i / 160 * 2500f, 0);
            Mesh(b, "SM_Camp_" + i, Rock, at);
            Effect(b, "Light_" + i, "/Script/Engine.PointLight", "PointLightComponent", at + new FVector(0, 0, 80));
            if (i % 12 == 0 && seeds.Count < 2000)
            {
                seeds.Add(new DeleteSeed(new ActorRef(Test, "SM_Camp_" + i)));
            }
        }

        var doc = LevelDocument.FromData(b.Build());
        Assert.Equal(50_000, doc.Actors.Count);
        Assert.Equal(2000, seeds.Count);
        Companions.Find([doc], seeds.Take(10).ToList(), new EditState(), Bounds); // warm-up (JIT)

        var watch = Stopwatch.StartNew();
        var report = Companions.Find([doc], seeds, new EditState(), Bounds);
        watch.Stop();

        Assert.Equal(2000, report.Companions.Count(c => c.DefaultOn));
        Assert.True(watch.ElapsedMilliseconds < 500, $"Find took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void CampLevelReadsBack()
    {
        var doc = LoadCamp();

        Assert.Equal(
            ["Ash_Decal_1", "BP_House_1", "Camp_Ambience", "DirectionalLight_1", "FireLight_1", "Fire_Sound_1", "Glow_1", "Lamp_1", "P_Fire_1", "Rock_1", "SM_Fireplace_1", "Smoke_Volume", "WorldSettings"],
            doc.Actors.Select(a => a.Name).Order(StringComparer.Ordinal));
        var sound = doc.FindActor("Fire_Sound_1")!;
        Assert.Equal("AkAmbientSound", sound.ClassName);
        Assert.Equal("AkComponent", sound.Root!.ClassName);
        Near(new FVector(100, 0, 0), sound.Root.WorldTransform.Translation);
        Near(new FVector(0, 0, 60), doc.FindActor("FireLight_1")!.WorldTransform.Translation);
        Near(new FVector(0, 0, 600), doc.FindActor("Glow_1")!.WorldTransform.Translation);
        Assert.Equal(ActorKind.Volume, doc.FindActor("Smoke_Volume")!.Kind);

        var house = doc.FindActor("BP_House_1")!;
        Near(new FVector(400, 300, 0), house.FindComponent("Fireplace")!.WorldTransform.Translation);
        Near(new FVector(400, 300, 60), house.FindComponent("FireLight")!.WorldTransform.Translation);
        Near(new FVector(400, 300, 0), house.FindComponent("FireSound")!.WorldTransform.Translation);
        Near(new FVector(400, -80, 200), house.FindComponent("PorchLight")!.WorldTransform.Translation);
        Assert.Equal(house.FindComponent("Fireplace")!.ExportIndex, house.FindComponent("Fire")!.AttachParent);
        Assert.Equal(SyntheticCampLevel.FireplaceMesh + ".SM_Fireplace_01", doc.FindActor("SM_Fireplace_1")!.StaticMeshPath);

        // The rock's root hangs from another actor's component (an export index of the fireplace's mesh).
        Assert.Equal(doc.FindActor("SM_Fireplace_1")!.RootComponent, doc.FindActor("Rock_1")!.Root!.AttachParent);
        Near(new FVector(50, 0, 0), doc.FindActor("Rock_1")!.WorldTransform.Translation);
    }

    [Fact]
    public void ActorRolesTellEffectsFromTheLevelAndFromDrawnThings()
    {
        Assert.True(ActorRoles.IsEffectClass("AkAmbientSound"));
        Assert.True(ActorRoles.IsEffectClass("BP_Campfire_Fire_C"));
        Assert.False(ActorRoles.IsEffectClass("StaticMeshActor"));
        Assert.True(ActorRoles.IsGlobalEffect("SkyLight"));
        Assert.False(ActorRoles.IsGlobalEffect("PointLight"));
        Assert.True(ActorRoles.IsStructural("WorldSettings"));
        foreach (var effect in new[] { "PointLightComponent", "BP_FlickerLightComponent_C", "ParticleSystemComponent", "NiagaraComponent", "AudioComponent", "AkComponent", "DecalComponent", "BP_HeatSourceComponent_C" })
        {
            Assert.True(ActorRoles.IsEffectComponent(Part(effect)), effect);
        }

        foreach (var other in new[] { "DirectionalLightComponent", "SkyLightComponent", "StaticMeshComponent", "SceneComponent", "BoxComponent" })
        {
            Assert.False(ActorRoles.IsEffectComponent(Part(other)), other);
        }

        // A volumetric fog Blueprint draws only its editor sphere: nothing the game shows.
        var fog = new ActorRecord(2, "BP_Fog_1", "/Game/Fog/BP_Fog.BP_Fog_C", 3, [Part("StaticMeshComponent", "/Engine/EditorMeshes/EditorSphere.EditorSphere")],
            FTransform.Identity, ActorKind.Blueprint, null, []) { ClassName = "BP_Fog_C" };
        Assert.False(ActorRoles.HasDrawnMesh(fog));
        Assert.True(ActorRoles.HasDrawnMesh(fog with { Components = [Part("StaticMeshComponent", Rock)] }));
    }

    private static ComponentRecord Part(string className, string? mesh = null) =>
        new(3, "Part", className, true, null, FTransform.Identity, FTransform.Identity, mesh, []);

    private static Companion One(CompanionReport report, string actor) => Assert.Single(report.Companions, c => c.Actor.Actor == actor);

    private static void Near(FVector expected, FVector actual) => Assert.True((expected - actual).Size() < 0.01f, $"expected {expected}, got {actual}");

    private static LevelDocument LoadCamp()
    {
        using var temp = new LevelTempDirectory();
        SyntheticCampLevel.WriteCampContent(temp.Path);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        return LevelDocument.Load(new Cue4ParseLevelReader(catalog), Camp);
    }

    private static int Mesh(FakeLevelBuilder b, string name, string mesh, FVector at)
    {
        var actor = b.Actor(name, "/Script/Engine.StaticMeshActor");
        b.Root(actor, b.Component(actor, "StaticMeshComponent0", "StaticMeshComponent", at, mesh: mesh));
        return actor;
    }

    private static int Effect(FakeLevelBuilder b, string name, string classPath, string rootClass, FVector at, bool blueprint = false)
    {
        var actor = b.Actor(name, classPath, blueprint);
        b.Root(actor, b.Component(actor, rootClass + "0", rootClass, at));
        return actor;
    }

    private static (int Actor, int Root) Hung(FakeLevelBuilder b, string name, string classPath, string rootClass, FVector relative, int parent, bool blueprint = false)
    {
        var actor = b.Actor(name, classPath, blueprint);
        var root = b.Component(actor, rootClass + "0", rootClass, relative, attachTo: parent);
        b.Root(actor, root);
        return (actor, root);
    }
}
