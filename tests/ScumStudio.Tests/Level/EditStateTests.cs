using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using static ScumStudio.Tests.Level.EditOpTests;

namespace ScumStudio.Tests.Level;

public sealed class EditStateTests
{
    private static readonly ActorRef House = new(Outpost, "StaticMeshActor_12");
    private static readonly ActorRef Crate = new(Outpost, "Crate_1");
    private static readonly InstanceRef Pebble = new(Outpost, "InstancedFoliageActor_0", "FoliageISM_0", 3);

    /// <summary>A realistic editing session touching every operation type.</summary>
    private static List<EditOp> Session()
    {
        var duplicate = new DuplicateActorOp(House, "StaticMeshActor_12_Copy", Somewhere);
        var added = new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Elsewhere);
        var planted = new AddInstanceOp(Pebble with { Index = 9 }, Somewhere);
        return
        [
            new DeleteActorOp(Crate),
            new SetTransformOp(House, TransformValue.Identity, Somewhere),
            new SetTransformOp(House, Somewhere, Elsewhere),
            duplicate,
            new SetTransformOp(duplicate.Created, Somewhere, Elsewhere),
            new SetInstanceTransformOp(Pebble, TransformValue.Identity, Somewhere),
            new DeleteInstanceOp(Pebble with { Index = 4 }),
            planted,
            new RemoveAddedInstanceOp(planted.Target, planted),
            new AddInstanceOp(Pebble with { Index = 10 }, Somewhere),
            new SetInstanceTransformOp(Pebble with { Index = 10 }, Somewhere, Elsewhere),
            added,
            new DeleteAllOfKindOp(new KindMatch(MatchBy.StaticMesh, Rock), EditScope.ForLevel(Port),
                [new ActorRef(Port, "Rock_1"), new ActorRef(Port, "Rock_2")], [new InstanceRef(Port, "Foliage", "ISM", 0)]),
            new AddBlueprintActorOp(Port, "BP_Lamp_Added", "/Game/BP/BP_Lamp.BP_Lamp_C", new ActorRef(Outpost, "BP_Lamp_C_3"), Somewhere),
            new RemoveAddedActorOp(added.Created, added),
            new RestoreActorOp(Crate),
            new DeleteActorOp(new ActorRef(Port, "BP_Lamp_Added")),
        ];
    }

    [Fact]
    public void AReplacedMeshIsKeptPerComponentAndUndoneExactly()
    {
        const string boulder = "/Game/ConZ_Files/Models/Rocks/SM_Boulder.SM_Boulder";
        const string pebble = "/Game/ConZ_Files/Models/Rocks/SM_Pebble.SM_Pebble";
        var state = new EditState();
        var root = new ReplaceMeshOp(House, null, Rock, boulder);
        var door = new ReplaceMeshOp(House, "Door", Rock, pebble);
        state.Apply(root);
        state.Apply(door);
        Assert.Equal(boulder, state.GetMeshOverride(House));
        Assert.Equal(pebble, state.GetMeshOverride(House, "Door"));
        Assert.Null(state.GetMeshOverride(Crate));
        Assert.Equal([Outpost], state.ChangedLevels);

        // Out of date: the component no longer draws what the edit expects; a deleted actor takes none.
        Assert.NotNull(state.Validate(new ReplaceMeshOp(House, null, Rock, pebble)));
        Assert.Null(state.Validate(new ReplaceMeshOp(House, null, boulder, pebble)));
        state.Apply(new DeleteActorOp(House));
        Assert.NotNull(state.Validate(new ReplaceMeshOp(House, null, boulder, pebble)));
        state.Apply(new RestoreActorOp(House));

        // Back to the level's mesh: no override left; a replaced added actor forgets it when the actor goes.
        state.Apply(root.Inverse());
        state.Apply(door.Inverse());
        Assert.True(state.IsEmpty, state.Describe());
        var added = new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Elsewhere);
        state.Apply(added);
        state.Apply(new ReplaceMeshOp(added.Created, null, Rock, boulder));
        Assert.Equal(boulder, state.GetMeshOverride(added.Created));
        state.Apply(added.Inverse());
        Assert.True(state.IsEmpty, state.Describe());
    }

    [Fact]
    public void ApplyingInversesInReverseOrderRestoresTheEmptyState()
    {
        var state = new EditState();
        var snapshots = new List<string> { state.Describe() };
        var ops = Session();
        foreach (var op in ops)
        {
            state.Apply(op);
            snapshots.Add(state.Describe());
        }

        Assert.False(state.IsEmpty);
        for (var i = ops.Count - 1; i >= 0; i--)
        {
            state.Apply(ops[i].Inverse());
            Assert.Equal(snapshots[i], state.Describe());
        }

        Assert.True(state.IsEmpty);
        Assert.Equal(string.Empty, state.Describe());
    }

    [Fact]
    public void TracksTheNetEffect()
    {
        var state = EditState.Replay(Session());

        Assert.False(state.IsDeleted(Crate)); // deleted, then restored
        Assert.True(state.IsDeleted(new ActorRef(Port, "Rock_1")));
        Assert.True(state.IsDeleted(new ActorRef(Port, "BP_Lamp_Added")));
        Assert.True(state.IsDeleted(new InstanceRef(Port, "Foliage", "ISM", 0)));
        Assert.True(state.IsDeleted(Pebble with { Index = 4 }));
        Assert.False(state.IsDeleted(Pebble));
        Assert.Equal(Somewhere, state.GetInstanceOverride(Pebble));
        Assert.False(state.IsAdded(Pebble with { Index = 9 })); // planted, taken out again
        Assert.Null(state.GetInstanceOverride(Pebble with { Index = 9 }));
        Assert.Equal(Elsewhere, state.GetAddedInstanceTransform(Pebble with { Index = 10 }));
        Assert.Equal(Elsewhere, state.GetTransformOverride(House));
        Assert.Null(state.GetTransformOverride(House, "Door"));
        Assert.True(state.IsAdded(new ActorRef(Outpost, "staticmeshactor_12_copy")));
        Assert.Equal(Elsewhere, state.GetAddedTransform(new ActorRef(Outpost, "StaticMeshActor_12_Copy")));
        Assert.False(state.IsAdded(new ActorRef(Port, "SM_Rock_Added"))); // removed again
        Assert.Equal(Somewhere, state.GetAddedTransform(new ActorRef(Port, "BP_Lamp_Added")));
        Assert.Null(state.GetAddedTransform(House));
        Assert.Equal(new[] { Port, Outpost }.Order(StringComparer.OrdinalIgnoreCase), state.ChangedLevels);
    }

    [Fact]
    public void DropsOverridesThatReturnToTheirOriginalValue()
    {
        var state = new EditState();
        state.Apply(new SetTransformOp(House, TransformValue.Identity, Somewhere));
        state.Apply(new SetTransformOp(House, Somewhere, Elsewhere));
        state.Apply(new SetTransformOp(House, Elsewhere, TransformValue.Identity)); // moved back by hand
        Assert.True(state.IsEmpty);

        state.Apply(new SetTransformOp(House, TransformValue.Identity, TransformValue.Identity)); // no-op move
        Assert.True(state.IsEmpty);
    }

    [Fact]
    public void RejectsInvalidOperations()
    {
        var state = new EditState();
        Assert.NotNull(state.Validate(new RestoreActorOp(House)));
        Assert.NotNull(state.Validate(new RestoreInstanceOp(Pebble)));
        Assert.NotNull(state.Validate(new RemoveAddedActorOp(Crate, new DuplicateActorOp(House, "Crate_1", Somewhere))));
        Assert.NotNull(state.Validate(new RemoveAddedInstanceOp(Pebble, new AddInstanceOp(Pebble, Somewhere))));
        Assert.NotNull(state.Validate(new AddInstanceOp(Pebble with { Index = -1 }, Somewhere)));
        Assert.NotNull(state.Validate(new DeleteAllOfKindOp(new KindMatch(MatchBy.Class, "X"), EditScope.Island, [], [])));
        Assert.NotNull(state.Validate(new DeleteAllOfKindOp(new KindMatch(MatchBy.Class, "X"), EditScope.Island, [House, House], [])));
        Assert.NotNull(state.Validate(new AddStaticMeshActorOp(Port, "X", " ", Somewhere)));
        Assert.NotNull(state.Validate(new DuplicateActorOp(House, "", Somewhere)));

        state.Apply(new DeleteActorOp(House));
        Assert.NotNull(state.Validate(new DeleteActorOp(House)));
        Assert.NotNull(state.Validate(new SetTransformOp(House, TransformValue.Identity, Somewhere)));
        Assert.NotNull(state.Validate(new DuplicateActorOp(House, "Copy", Somewhere)));
        var ex = Assert.Throws<InvalidOperationException>(() => state.Apply(new DeleteActorOp(new ActorRef(Outpost.ToLowerInvariant(), "STATICMESHACTOR_12"))));
        Assert.Contains("already deleted", ex.Message, StringComparison.Ordinal);

        state.Apply(new SetTransformOp(Crate, TransformValue.Identity, Somewhere));
        Assert.NotNull(state.Validate(new SetTransformOp(Crate, TransformValue.Identity, Elsewhere))); // stale "old" value
        Assert.Null(state.Validate(new SetTransformOp(Crate, Somewhere, Elsewhere)));

        var dup = new DuplicateActorOp(Crate, "Crate_1_Copy", Somewhere);
        state.Apply(dup);
        Assert.NotNull(state.Validate(dup)); // name taken
        Assert.NotNull(state.Validate(new RemoveAddedActorOp(dup.Created, dup with { Transform = Elsewhere })));
        Assert.Null(state.Validate(new RemoveAddedActorOp(dup.Created, dup)));
    }

    [Fact]
    public void InstancesOfDeletedActorsCountAsDeleted()
    {
        var state = new EditState();
        state.Apply(new DeleteActorOp(Pebble.ActorRef));
        Assert.True(state.IsDeleted(Pebble));
        Assert.NotNull(state.Validate(new DeleteInstanceOp(Pebble)));
        Assert.NotNull(state.Validate(new SetInstanceTransformOp(Pebble, TransformValue.Identity, Somewhere)));
    }

    [Fact]
    public void RemovingAnAddedActorClearsItsEdits()
    {
        var state = new EditState();
        var add = new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Somewhere);
        state.Apply(add);
        state.Apply(new SetTransformOp(add.Created, Somewhere, Elsewhere));
        state.Apply(new RemoveAddedActorOp(add.Created, add));
        Assert.True(state.IsEmpty);
    }

    [Fact]
    public void RemovingAnAddedInstanceClearsItsEdits()
    {
        var state = new EditState();
        var add = new AddInstanceOp(Pebble with { Index = 9 }, Somewhere);
        state.Apply(add);
        Assert.NotNull(state.Validate(add)); // the index is taken
        state.Apply(new SetInstanceTransformOp(add.Target, Somewhere, Elsewhere));
        Assert.Equal(Elsewhere, state.GetAddedInstanceTransform(add.Target));
        state.Apply(new RemoveAddedInstanceOp(add.Target, add));
        Assert.True(state.IsEmpty);
    }

    [Fact]
    public void ReplayMatchesIncrementalApplication()
    {
        var ops = Session();
        var incremental = new EditState();
        ops.ForEach(incremental.Apply);
        Assert.Equal(incremental.Describe(), EditState.Replay(ops).Describe());
        Assert.Equal(new FVector(0.1f, 0.2f, 0.3f), incremental.GetTransformOverride(House)!.Value.Location);
    }

    /// <summary>
    /// Discord (seviscache): 175 x "Function does not accept floating point Not-a-Number values" once a drag had flung an
    /// object to the far side of the map. An edit that would put something at a NaN, an infinite or an off-map value is refused.
    /// </summary>
    [Fact]
    public void AnEditToANaNOrOffMapPlaceIsRefused()
    {
        var state = new EditState();
        var nan = new TransformValue(new FVector(float.NaN, 0, 0), FRotator.Zero, FVector.One);
        var far = TransformValue.At(1e11f, 0, 0);
        var turned = new TransformValue(FVector.Zero, new FRotator(0, float.PositiveInfinity, 0), FVector.One);
        Assert.NotNull(state.Validate(new SetTransformOp(House, TransformValue.Identity, nan)));
        Assert.NotNull(state.Validate(new DuplicateActorOp(House, "Copy_1", far)));
        Assert.NotNull(state.Validate(new SetInstanceTransformOp(Pebble, TransformValue.Identity, turned)));
        Assert.NotNull(state.Validate(new BatchOp("move", [new SetTransformOp(House, TransformValue.Identity, Somewhere), new SetTransformOp(Crate, TransformValue.Identity, nan)])));
        Assert.Null(state.Validate(new SetTransformOp(House, TransformValue.Identity, TransformValue.At(-262870, -27545, 36663))));
    }

    [Fact]
    public void DeleteComponentsThenUndoRestoresExactly()
    {
        // Owner: "if I delete a fire place, the fire stays burning" - the fire, light and sound of a building's fireplace part.
        var state = EditState.Replay(Session());
        var before = state.Describe();
        var fire = new DeleteComponentsOp(House, ["P_Fire", "FireLight"]);
        state.Apply(fire);

        Assert.True(state.IsDeleted(House, "p_fire"));
        Assert.True(state.IsDeleted(House, "FireLight"));
        Assert.False(state.IsDeleted(House, "Walls"));
        Assert.False(state.IsDeleted(House));
        Assert.Equal(2, state.DeletedComponents.Count);
        Assert.Contains(Outpost, state.ChangedLevels);
        Assert.Contains("delete-component " + House.ToString().ToLowerInvariant() + ".p_fire", state.Describe(), StringComparison.Ordinal);
        Assert.Equal(state.Describe(), state.Clone().Describe());

        state.Apply(fire.Inverse());
        Assert.Equal(before, state.Describe());
        Assert.Empty(state.DeletedComponents);

        var alone = new EditState();
        alone.Apply(fire);
        Assert.False(alone.IsEmpty);
        Assert.Equal([Outpost], alone.ChangedLevels);
        alone.Apply(fire.Inverse());
        Assert.True(alone.IsEmpty);
    }

    [Fact]
    public void RefusesDeletingTwiceAndRestoringAKeptComponent()
    {
        var state = new EditState();
        state.Apply(new DeleteComponentsOp(House, ["P_Fire"]));

        Assert.Contains("already deleted", state.Validate(new DeleteComponentsOp(House, ["Ak_Fire", "p_fire"])), StringComparison.Ordinal);
        Assert.Contains("not deleted", state.Validate(new RestoreComponentsOp(House, ["P_Fire", "FireLight"])), StringComparison.Ordinal);
        Assert.Contains("listed twice", state.Validate(new DeleteComponentsOp(House, ["FireLight", "firelight"])), StringComparison.Ordinal);
        Assert.NotNull(state.Validate(new DeleteComponentsOp(House, [])));
        Assert.NotNull(state.Validate(new DeleteComponentsOp(House, [" "])));
        Assert.Null(state.Validate(new RestoreComponentsOp(House, ["P_FIRE"])));

        // A deleted actor's parts go with it; an added copy's exports are its source's.
        state.Apply(new DeleteActorOp(Crate));
        Assert.Contains("is deleted", state.Validate(new DeleteComponentsOp(Crate, ["P_Fire"])), StringComparison.Ordinal);
        Assert.True(state.IsDeleted(Crate, "Anything"));
        var copy = new DuplicateActorOp(House, "House_Copy", Somewhere);
        state.Apply(copy);
        Assert.Contains("added", state.Validate(new DeleteComponentsOp(copy.Created, ["P_Fire"])), StringComparison.Ordinal);
    }

    [Fact]
    public void ABatchThatDeletesTheActorSkipsItsComponentOpBothWays()
    {
        // The finder's batch: the fireplace actor whole and, for another actor, its fire; one undo restores both.
        foreach (var batch in new[]
                 {
                     new BatchOp("Delete fireplace", [new DeleteActorOp(House), new DeleteComponentsOp(House, ["P_Fire"]), new DeleteComponentsOp(Crate, ["Ak_Fire"])]),
                     new BatchOp("Delete fireplace", [new DeleteComponentsOp(House, ["P_Fire"]), new DeleteActorOp(House), new DeleteComponentsOp(Crate, ["Ak_Fire"])]),
                 })
        {
            var state = new EditState();
            Assert.Null(state.Validate(batch));
            state.Apply(batch);
            Assert.True(state.IsDeleted(House));
            Assert.True(state.IsDeleted(Crate, "Ak_Fire"));
            Assert.False(state.IsDeleted(Crate));

            Assert.Null(state.Validate(batch.Inverse()));
            state.Apply(batch.Inverse());
            Assert.True(state.IsEmpty, state.Describe());
        }
    }
}
