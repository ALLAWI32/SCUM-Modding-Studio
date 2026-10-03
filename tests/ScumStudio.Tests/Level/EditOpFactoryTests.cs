using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;

namespace ScumStudio.Tests.Level;

public sealed class EditOpFactoryTests
{
    private const string Outpost = EditOpTests.Outpost;
    private const string Port = EditOpTests.Port;
    private const string Rock = EditOpTests.Rock;
    private const string LampClass = "/Game/ConZ_Files/Blueprints/Lights/BP_Lamp.BP_Lamp_C";

    private static LevelDocument Level(string path, bool withCopyName = false)
    {
        var b = new FakeLevelBuilder(path);
        for (var i = 1; i <= 2; i++)
        {
            var rock = b.Actor($"Rock_{i}", "/Script/Engine.StaticMeshActor");
            b.Root(rock, b.Component(rock, "StaticMeshComponent0", "StaticMeshComponent", location: new FVector(i * 100, 0, 0), mesh: Rock));
        }

        var wall = b.Actor("Wall_1", "/Script/Engine.StaticMeshActor");
        b.Root(wall, b.Component(wall, "StaticMeshComponent0", "StaticMeshComponent", mesh: "/Game/ConZ_Files/Models/SM_Wall.SM_Wall"));

        var lamp = b.Actor("BP_Lamp_C_1", LampClass, blueprint: true);
        b.Root(lamp, b.Component(lamp, "DefaultSceneRoot", location: new FVector(5, 6, 7), rotation: new FRotator(0, 30, 0)));

        var foliage = b.Actor("InstancedFoliageActor_0", "/Script/Foliage.InstancedFoliageActor");
        var root = b.Component(foliage, "DefaultSceneRoot");
        b.Root(foliage, root);
        b.Component(foliage, "Rocks", "HierarchicalInstancedStaticMeshComponent", attachTo: root, mesh: "/Game/ConZ_Files/Models/Rocks/SM_Rock",
            instances: [new FTransform(new FVector(1, 1, 1)), new FTransform(new FVector(2, 2, 2))]);
        b.Component(foliage, "Grass", "HierarchicalInstancedStaticMeshComponent", attachTo: root, mesh: "/Game/ConZ_Files/Foliage/SM_Grass.SM_Grass",
            instances: [new FTransform(new FVector(3, 3, 3))]);
        if (withCopyName)
        {
            var copy = b.Actor("Rock_1_Copy", "/Script/Engine.StaticMeshActor");
            b.Root(copy, b.Component(copy, "StaticMeshComponent0", "StaticMeshComponent", mesh: Rock));
        }

        return LevelDocument.FromData(b.Build());
    }

    [Fact]
    public void DeleteAllOfMeshCoversActorsAndInstancesAcrossLevels()
    {
        var levels = new[] { Level(Outpost), Level(Port) };
        var op = EditOpFactory.DeleteAllOfKind(levels, new KindMatch(MatchBy.StaticMesh, Rock), EditScope.ForCell(new MapCell('A', 0)));

        Assert.Equal(
            new[] { new ActorRef(Outpost, "Rock_1"), new ActorRef(Outpost, "Rock_2"), new ActorRef(Port, "Rock_1"), new ActorRef(Port, "Rock_2") },
            op.Actors);
        Assert.Equal(
            new[]
            {
                new InstanceRef(Outpost, "InstancedFoliageActor_0", "Rocks", 0), new InstanceRef(Outpost, "InstancedFoliageActor_0", "Rocks", 1),
                new InstanceRef(Port, "InstancedFoliageActor_0", "Rocks", 0), new InstanceRef(Port, "InstancedFoliageActor_0", "Rocks", 1),
            },
            op.Instances);
        Assert.Equal(new[] { Outpost, Port }, op.GetTouchedLevels());

        var actorsOnly = EditOpFactory.DeleteAllOfKind(levels, new KindMatch(MatchBy.StaticMesh, Rock), EditScope.Island, includeInstances: false);
        Assert.Empty(actorsOnly.Instances);
    }

    [Fact]
    public void DeleteAllSkipsWhatIsAlreadyDeleted()
    {
        var level = Level(Outpost);
        var state = new EditState();
        state.Apply(new DeleteActorOp(new ActorRef(Outpost, "Rock_1")));
        state.Apply(new DeleteInstanceOp(new InstanceRef(Outpost, "InstancedFoliageActor_0", "Rocks", 1)));

        var op = EditOpFactory.DeleteAllOfKind([level], new KindMatch(MatchBy.StaticMesh, "/Game/ConZ_Files/Models/Rocks/SM_Rock"), EditScope.ForLevel(Outpost), state);

        Assert.Equal(new[] { new ActorRef(Outpost, "Rock_2") }, op.Actors);
        Assert.Equal(new[] { new InstanceRef(Outpost, "InstancedFoliageActor_0", "Rocks", 0) }, op.Instances);
        Assert.Null(state.Validate(op));
    }

    [Fact]
    public void DeleteAllOfClassMatchesPathPackageOrShortName()
    {
        var level = Level(Outpost);
        foreach (var path in new[] { LampClass, "/Game/ConZ_Files/Blueprints/Lights/BP_Lamp", "bp_lamp_c" })
        {
            var op = EditOpFactory.DeleteAllOfKind([level], new KindMatch(MatchBy.Class, path), EditScope.ForLevel(Outpost));
            Assert.Equal(new[] { new ActorRef(Outpost, "BP_Lamp_C_1") }, op.Actors);
            Assert.Empty(op.Instances);
        }

        var smActors = EditOpFactory.DeleteAllOfKind([level], new KindMatch(MatchBy.Class, "/Script/Engine.StaticMeshActor"), EditScope.ForLevel(Outpost));
        Assert.Equal(3, smActors.Actors.Count);
    }

    [Fact]
    public void SetTransformTakesOldValueFromLevelThenFromState()
    {
        var level = Level(Outpost);
        var lamp = level.FindActor("BP_Lamp_C_1")!;
        var state = new EditState();

        var first = EditOpFactory.SetTransform(level, lamp, EditOpTests.Somewhere, state);
        Assert.Equal(new FVector(5, 6, 7), first.Old.Location);
        Assert.Equal(30f, first.Old.Rotation.Yaw, 1e-3f);
        state.Apply(first);

        var second = EditOpFactory.SetTransform(level, lamp, EditOpTests.Elsewhere, state);
        Assert.Equal(EditOpTests.Somewhere, second.Old);
        Assert.Null(state.Validate(second));

        Assert.Throws<ArgumentException>(() => EditOpFactory.SetTransform(level, lamp, EditOpTests.Elsewhere, state, "Missing"));
    }

    [Fact]
    public void InstanceHelpersValidateIndices()
    {
        var level = Level(Outpost);
        var foliage = level.FindActor("InstancedFoliageActor_0")!;
        var set = EditOpFactory.SetInstanceTransform(level, foliage, "Grass", 0, EditOpTests.Somewhere);
        Assert.Equal(new FVector(3, 3, 3), set.Old.Location);
        Assert.Equal(new InstanceRef(Outpost, "InstancedFoliageActor_0", "Grass", 0), EditOpFactory.DeleteInstance(level, foliage, "grass", 0).Target);
        Assert.Throws<ArgumentOutOfRangeException>(() => EditOpFactory.DeleteInstance(level, foliage, "Grass", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => EditOpFactory.DeleteInstance(level, foliage, "DefaultSceneRoot", 0));
    }

    [Fact]
    public void PicksUniqueNamesForNewActors()
    {
        var level = Level(Outpost, withCopyName: true);
        var state = new EditState();

        var duplicate = EditOpFactory.Duplicate(level, level.FindActor("Rock_1")!, state: state);
        Assert.Equal("Rock_1_Copy2", duplicate.NewName); // Rock_1_Copy exists in the level
        Assert.Equal(new FVector(100, 0, 0), duplicate.Transform.Location);
        state.Apply(duplicate);
        Assert.Equal("Rock_1_Copy3", EditOpFactory.Duplicate(level, level.FindActor("Rock_1")!, state: state).NewName);

        var add = EditOpFactory.AddStaticMeshActor(level, Rock, EditOpTests.Somewhere, state);
        Assert.Equal("SM_Rock_Added", add.NewName);
        state.Apply(add);
        Assert.Equal("SM_Rock_Added2", EditOpFactory.AddStaticMeshActor(level, Rock, EditOpTests.Somewhere, state).NewName);
    }

    [Fact]
    public void TransplantsOnlyBlueprints()
    {
        var target = Level(Port);
        var source = Level(Outpost);
        var op = EditOpFactory.AddBlueprintActor(target, source, source.FindActor("BP_Lamp_C_1")!, EditOpTests.Elsewhere);
        Assert.Equal(Port, op.Level);
        Assert.Equal("BP_Lamp_Added", op.NewName);
        Assert.Equal(LampClass, op.ClassPath);
        Assert.Equal(new ActorRef(Outpost, "BP_Lamp_C_1"), op.Source);
        Assert.Throws<ArgumentException>(() => EditOpFactory.AddBlueprintActor(target, source, source.FindActor("Rock_1")!, EditOpTests.Elsewhere));
    }

    [Fact]
    public void ComparesObjectPaths()
    {
        Assert.True(EditOpFactory.SameObject("/Game/X/SM_A", "/Game/X/SM_A.SM_A"));
        Assert.True(EditOpFactory.SameObject("/game/x/sm_a.SM_A", "/Game/X/SM_A.SM_A"));
        Assert.False(EditOpFactory.SameObject("/Game/X/SM_A", "/Game/Y/SM_A"));
        Assert.False(EditOpFactory.SameObject("/Game/X/SM_A.SM_A", "/Game/X/SM_A.SM_B"));
    }
}
