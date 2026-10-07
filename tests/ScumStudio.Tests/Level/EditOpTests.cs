using System.Text.Json;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Serialization;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.Level;

public sealed class EditOpTests
{
    internal const string Outpost = "/Game/ConZ_Files/Maps/The_Island/A_0_Outpost";
    internal const string Port = "/Game/ConZ_Files/Maps/The_Island/A_0_Small_Port";
    internal const string Rock = "/Game/ConZ_Files/Models/Rocks/SM_Rock.SM_Rock";

    internal static readonly TransformValue Somewhere = new(new FVector(100.5f, -20.25f, 3), new FRotator(0, 90, 0), FVector.One);
    internal static readonly TransformValue Elsewhere = new(new FVector(0.1f, 0.2f, 0.3f), new FRotator(10, -45.5f, 5), new FVector(1, 2, 0.5f));

    /// <summary>One instance of every operation type.</summary>
    public static IEnumerable<object[]> AllOps()
    {
        var house = new ActorRef(Outpost, "StaticMeshActor_12");
        var instance = new InstanceRef(Outpost, "InstancedFoliageActor_0", "FoliageISM_0", 42);
        var match = new KindMatch(MatchBy.StaticMesh, Rock);
        var scope = EditScope.ForCell(new ScumStudio.Level.World.MapCell('A', 0));
        EditOp[] ops =
        [
            new DeleteActorOp(house),
            new RestoreActorOp(house),
            new DeleteAllOfKindOp(match, scope, [house, new ActorRef(Port, "Rock_2")], [instance]),
            new RestoreAllOfKindOp(new KindMatch(MatchBy.Class, "/Game/BP/BP_Lamp.BP_Lamp_C"), EditScope.Island, [house], []),
            new DuplicateActorOp(house, "StaticMeshActor_12_Copy", Somewhere),
            new SetTransformOp(house, Somewhere, Elsewhere),
            new SetTransformOp(house, Somewhere, Elsewhere, "Door"),
            new SetInstanceTransformOp(instance, Somewhere, Elsewhere),
            new DeleteInstanceOp(instance),
            new RestoreInstanceOp(instance),
            new AddInstanceOp(instance with { Index = 3000 }, Somewhere),
            new RemoveAddedInstanceOp(instance with { Index = 3000 }, new AddInstanceOp(instance with { Index = 3000 }, Somewhere)),
            new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Elsewhere),
            new AddBlueprintActorOp(Port, "BP_Lamp_Added", "/Game/BP/BP_Lamp.BP_Lamp_C", new ActorRef(Outpost, "BP_Lamp_C_3"), Somewhere),
            new AddBlueprintActorOp(Port, "Asian_Chest_Added", "/Script/SCUM.WorldItemSpawner", new ActorRef(Outpost, "WorldItemSpawner_1"), Somewhere)
            {
                Item = "/Game/ConZ_Files/Items/Equipment/Active_Items/Asian_Chest.Asian_Chest_C",
            },
            new RemoveAddedActorOp(new ActorRef(Port, "SM_Rock_Added"), new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Elsewhere)),
            new BatchOp("Plant 2 rocks", [new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Elsewhere), new DeleteActorOp(house)]),
            new SetSpawnPointsOp(new ActorRef(Outpost, "SentrySpawner_0"), null, "PatrolPoints", [new SpawnPoint(0, Somewhere), new SpawnPoint(1, Elsewhere)], [new SpawnPoint(1, Elsewhere), new SpawnPoint(1, Somewhere)]),
            new SetSpawnPointsOp(new ActorRef(Outpost, "ItemSpawnerGroup_1"), "SpawnerComponent", "SpawnerMarkers", [new SpawnPoint(0, Somewhere)], []),
            new ReplaceMeshOp(house, null, Rock, "/Game/ConZ_Files/Models/Rocks/SM_Boulder.SM_Boulder"),
            new ReplaceMeshOp(new ActorRef(Port, "LandscapeStreamingProxy_0"), "SplineMeshComponent_12", Rock, "/Game/ConZ_Files/Models/Roads/SM_Road_02.SM_Road_02"),
        ];
        return ops.Select(o => new object[] { o });
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public void InverseOfInverseIsTheOperation(EditOp op)
    {
        var inverse = op.Inverse();
        Assert.NotEqual(op, inverse);
        Assert.Equal(op, inverse.Inverse());
        Assert.Equal(op.GetTouchedLevels(), inverse.GetTouchedLevels());
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public void RoundTripsThroughJsonWithOpDiscriminator(EditOp op)
    {
        var json = JsonSerializer.Serialize(op, LevelJson.Compact);
        Assert.StartsWith("{\"op\":\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', json);
        var back = JsonSerializer.Deserialize<EditOp>(json, LevelJson.Compact);
        Assert.Equal(op, back);
        Assert.Equal(json, op.ToJson());
        Assert.Equal(op, EditOp.FromJson(json));
        Assert.Equal(json, JsonSerializer.Serialize(back, LevelJson.Compact));

        var inverseJson = JsonSerializer.Serialize(op.Inverse(), LevelJson.Compact);
        Assert.Equal(op.Inverse(), JsonSerializer.Deserialize<EditOp>(inverseJson, LevelJson.Compact));
    }

    [Theory]
    [MemberData(nameof(AllOps))]
    public void DescribesItself(EditOp op)
    {
        Assert.False(string.IsNullOrWhiteSpace(op.Describe()));
        Assert.NotEmpty(op.GetTouchedLevels());
    }

    [Fact]
    public void ABatchAppliesAndUndoesAsOneStep()
    {
        // An AI building a village: one journal entry, one Ctrl+Z.
        var batch = new BatchOp("Build a camp",
        [
            new AddStaticMeshActorOp(Port, "Tent_Added", Rock, Somewhere),
            new AddStaticMeshActorOp(Port, "Tent_Added2", Rock, Elsewhere),
        ]);
        var state = new EditState();
        state.Apply(batch);
        Assert.Equal(2, state.AddedActors.Count);
        state.Apply(batch.Inverse());
        Assert.Empty(state.AddedActors);

        // Two children creating the same actor are rejected before anything changes.
        Assert.NotNull(state.Validate(new BatchOp("Twice", [new AddStaticMeshActorOp(Port, "X", Rock, Somewhere), new AddStaticMeshActorOp(Port, "X", Rock, Elsewhere)])));
    }

    [Fact]
    public void ABatchIsCheckedAgainstItsOwnEarlierEditsAndSkipsWhatAnActorDeleteCovers()
    {
        // The brush adds a tree, then its whole actor: the owner's first bulk delete was journaled, then stopped at that
        // tree ("already deleted") with 2000 edits unapplied. Now the instance delete is covered by the actor delete.
        var boathouse = new ActorRef(Outpost, "BP_River_Boathouse_5");
        var plank = new InstanceRef(Outpost, "BP_River_Boathouse_5", "HierarchicalInstancedStaticMesh5", 1);
        var house = new ActorRef(Outpost, "StaticMeshActor_12");
        var batch = new BatchOp("Deleted 3 objects", [new DeleteActorOp(boathouse), new DeleteInstanceOp(plank), new DeleteActorOp(house)]);
        var state = new EditState();
        Assert.Null(state.Validate(batch));
        state.Apply(batch);
        Assert.Equal(2, state.DeletedActors.Count);
        Assert.Empty(state.DeletedInstances); // the plank went with its actor
        Assert.True(state.IsDeleted(plank));
        state.Apply(batch.Inverse());
        Assert.True(state.IsEmpty);

        // An instance deleted before the batch stays deleted when the batch is undone (the batch never deleted it).
        state.Apply(new DeleteInstanceOp(plank));
        state.Apply(batch);
        state.Apply(batch.Inverse());
        Assert.Equal(plank, Assert.Single(state.DeletedInstances));
        Assert.Empty(state.DeletedActors);
        state.Apply(new RestoreInstanceOp(plank));
        Assert.True(state.IsEmpty);

        // An edit that an earlier edit of the same batch makes impossible is refused up front, nothing half applied.
        var conflict = new BatchOp("Half", [new DeleteActorOp(house), new SetTransformOp(house, Somewhere, Elsewhere)]);
        Assert.Contains("is deleted", state.Validate(conflict)!, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => state.Apply(conflict));
        Assert.True(state.IsEmpty);
    }

    [Fact]
    public void UsesReadableJson()
    {
        var op = new SetTransformOp(new ActorRef(Outpost, "Crate_1"), TransformValue.At(1, 2, 3), new TransformValue(new FVector(4, 5, 6), new FRotator(0, 90, 0), new FVector(2, 2, 2)));
        var json = JsonSerializer.Serialize<EditOp>(op, LevelJson.Compact);
        Assert.Equal(
            "{\"op\":\"setTransform\",\"target\":{\"level\":\"" + Outpost + "\",\"actor\":\"Crate_1\"},"
            + "\"old\":{\"location\":[1,2,3],\"rotation\":[0,0,0],\"scale\":[1,1,1]},"
            + "\"new\":{\"location\":[4,5,6],\"rotation\":[0,90,0],\"scale\":[2,2,2]}}",
            json);

        var add = JsonSerializer.Serialize<EditOp>(new DuplicateActorOp(new ActorRef(Outpost, "A"), "A_Copy", TransformValue.Identity), LevelJson.Compact);
        Assert.DoesNotContain("created", add, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnknownOperations()
    {
        Assert.ThrowsAny<JsonException>(() => EditOp.FromJson("{\"op\":\"explode\",\"target\":{}}"));
        Assert.ThrowsAny<JsonException>(() => EditOp.FromJson("{\"target\":{\"level\":\"x\",\"actor\":\"y\"}}"));
        Assert.ThrowsAny<JsonException>(() => EditOp.FromJson("not json"));
        Assert.ThrowsAny<JsonException>(() => EditOp.FromJson("null"));
    }

    [Fact]
    public void ReportsTargetsAndTouchedLevels()
    {
        var bulk = new DeleteAllOfKindOp(new KindMatch(MatchBy.StaticMesh, Rock), EditScope.Island,
            [new ActorRef(Outpost, "A"), new ActorRef(Port, "B"), new ActorRef(Outpost.ToUpperInvariant(), "C")],
            [new InstanceRef(Port, "F", "ISM", 1)]);
        Assert.Equal(new[] { Outpost, Port }, bulk.GetTouchedLevels());
        Assert.Null(bulk.GetPrimaryTarget());
        Assert.Contains("SM_Rock", bulk.Describe(), StringComparison.Ordinal);
        Assert.Contains("3 actor(s), 1 instance(s)", bulk.Describe(), StringComparison.Ordinal);

        var transplant = new AddBlueprintActorOp(Port, "BP_Lamp_Added", "/Game/BP/BP_Lamp.BP_Lamp_C", new ActorRef(Outpost, "BP_Lamp_C_3"), Somewhere);
        Assert.Equal(new[] { Port }, transplant.GetTouchedLevels()); // the source level is only read
        Assert.Equal(new ActorRef(Port, "BP_Lamp_Added"), transplant.GetPrimaryTarget());

        // A journal written before copies could change their item still loads (no item: the source's), and a copied world
        // item spawner says what it is and what it spawns.
        Assert.Null(Assert.IsType<AddBlueprintActorOp>(EditOp.FromJson(transplant.ToJson())).Item);
        Assert.DoesNotContain("\"item\"", transplant.ToJson(), StringComparison.Ordinal);
        var spawner = transplant with { ClassPath = "/Script/SCUM.WorldItemSpawner", Item = "/Game/Items/Asian_Chest.Asian_Chest_C" };
        Assert.StartsWith("Add WorldItemSpawner (Asian_Chest) as ", spawner.Describe(), StringComparison.Ordinal);

        var instance = new DeleteInstanceOp(new InstanceRef(Outpost, "Foliage", "ISM", 7));
        Assert.Equal(new ActorRef(Outpost, "Foliage"), instance.GetPrimaryTarget());
        Assert.Equal("A_0_Outpost/Foliage/ISM[7]", instance.Target.ToString());
    }

    [Fact]
    public void RefsCompareCaseInsensitivelyWithTheirComparer()
    {
        Assert.True(ActorRef.Comparer.Equals(new ActorRef(Outpost, "Crate"), new ActorRef(Outpost.ToLowerInvariant(), "CRATE")));
        Assert.True(InstanceRef.Comparer.Equals(new InstanceRef(Outpost, "F", "ISM", 1), new InstanceRef(Outpost, "f", "ism", 1)));
        Assert.False(InstanceRef.Comparer.Equals(new InstanceRef(Outpost, "F", "ISM", 1), new InstanceRef(Outpost, "F", "ISM", 2)));
    }

    [Fact]
    public void TransformValueConvertsToAndFromFTransform()
    {
        var value = new TransformValue(new FVector(10, 20, 30), new FRotator(15, 30, 45), new FVector(1, 2, 3));
        var back = TransformValue.FromTransform(value.ToTransform());
        Assert.True(value.IsNearlyEqual(back, 1e-3f));
        Assert.Equal(value.Location, back.Location);
        Assert.Equal(value.Scale, back.Scale);
        Assert.Equal(FTransform.Identity, TransformValue.Identity.ToTransform());
    }
}
