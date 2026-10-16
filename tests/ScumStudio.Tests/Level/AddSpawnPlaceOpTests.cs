using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.Level;

/// <summary>A new spawn place (not a copy) in the journal: stored, undone, moved and deleted like any added actor.</summary>
public sealed class AddSpawnPlaceOpTests
{
    private static readonly AddSpawnPlaceOp Car = new("NewVehicleSpawn_0001", SpawnPlaceKind.Vehicle, "City", new TransformValue(new FVector(10f, 20f, 30f), new FRotator(0f, 45f, 0f), FVector.One));

    [Fact]
    public void JsonRoundTrip()
    {
        var json = Car.ToJson();

        Assert.StartsWith("{\"op\":\"addSpawnPlace\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"vehicle\"", json, StringComparison.Ordinal);
        Assert.Equal(Car, EditOp.FromJson(json));
        Assert.Equal(Car, Car.Inverse().Inverse());
    }

    [Fact]
    public void ApplyThenInverseLeavesStateEmpty()
    {
        var state = new EditState();
        state.Apply(Car);
        Assert.True(state.IsAdded(Car.Created));
        Assert.Equal(Car.Transform, state.GetAddedTransform(Car.Created));
        Assert.Equal([SpawnPlaces.StaticDataPath], state.ChangedLevels);

        state.Apply(Car.Inverse());

        Assert.True(state.IsEmpty);
    }

    [Fact]
    public void MoveAndDeleteOfANewPlaceWork()
    {
        var state = EditState.Replay([Car]);
        var to = TransformValue.At(1f, 2f, 3f);
        state.Apply(new SetTransformOp(Car.Created, Car.Transform, to));
        state.Apply(new SetSpawnChoiceOp(Car.Created, SpawnChoices.VehicleGroup, null, "Boat"));

        var created = Assert.Single(SpawnPlacesEditor.Plan(state).Created);
        Assert.Equal(("NewVehicleSpawn_0001", "Boat", to), (created.Name, created.Setting, created.Transform));

        var delete = new DeleteActorOp(Car.Created);
        state.Apply(delete);
        Assert.Empty(SpawnPlacesEditor.Plan(state).Created);
        Assert.True(SpawnPlacesEditor.Plan(state).IsEmpty);

        state.Apply(delete.Inverse());
        Assert.Single(SpawnPlacesEditor.Plan(state).Created);

        // Removed (the add undone), it leaves nothing behind: no move, no choice.
        state.Apply(Car.Inverse());
        Assert.True(state.IsEmpty);
    }

    [Fact]
    public void DuplicateNameIsRefused()
    {
        var state = EditState.Replay([Car]);

        Assert.Contains("already exists", state.Validate(Car), StringComparison.Ordinal);
        Assert.NotNull(state.Validate(Car with { NewName = SpawnPlaces.ActorName(SpawnPlaceKind.Vehicle, 12) })); // a stock place's name
        Assert.NotNull(state.Validate(Car with { NewName = "NewVehicleSpawn_0002", Setting = " " }));
        Assert.NotNull(state.Validate(Car with { NewName = "NewVehicleSpawn_0002", Transform = TransformValue.At(float.NaN, 0f, 0f) }));
        Assert.Null(state.Validate(Car with { NewName = "NewVehicleSpawn_0002" }));
    }

    [Fact]
    public void NewNamesAreFreeAndNeverStock()
    {
        var name = SpawnPlaces.NewName(SpawnPlaceKind.Zone, ["NewThreatZone_0001", "newthreatzone_0002"]);

        Assert.Equal("NewThreatZone_0003", name);
        Assert.Null(SpawnPlaces.ParseName(name));
        Assert.All(Enum.GetValues<SpawnPlaceKind>(), k => Assert.Null(SpawnPlaces.ParseName(SpawnPlaces.NewName(k, []))));
    }

    [Fact]
    public void TagsRoundTripAndCompareByValue()
    {
        var tagged = new AddSpawnPlaceOp("NewTaggedSpawn_0001", SpawnPlaceKind.TaggedCharacter, "Interior", TransformValue.At(1f, 2f, 3f), ["Encounter.Police"]);

        var json = tagged.ToJson();
        var back = (AddSpawnPlaceOp)EditOp.FromJson(json);

        Assert.Contains("\"tags\":[\"Encounter.Police\"]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("tags", Car.ToJson(), StringComparison.Ordinal);
        Assert.Equal(tagged, back);
        Assert.NotSame(tagged.Tags, back.Tags);

        // Undo from the journal: its remove names an equal, not the same, add.
        var state = EditState.Replay([tagged]);
        Assert.Null(state.Validate(back.Inverse()));
        state.Apply(back.Inverse());
        Assert.True(state.IsEmpty);

        Assert.NotNull(state.Validate(Car with { Tags = ["Encounter.Police"] })); // only a tagged point has tags
        Assert.NotNull(state.Validate(tagged with { Tags = [" "] }));
    }

    [Fact]
    public void NewSpawnPlacesIsASnapshotThatEditsReplace()
    {
        var state = new EditState();
        state.Apply(Car);
        var before = state.NewSpawnPlaces;

        state.Apply(Car.Inverse());

        Assert.Equal([Car], before); // a reader on another thread keeps what it took
        Assert.Empty(state.NewSpawnPlaces);
        state.Apply(Car);
        Assert.Equal([Car], state.Clone().NewSpawnPlaces);
        Assert.Equal([Car], EditState.Replay([Car, new SetTransformOp(Car.Created, Car.Transform, TransformValue.At(1f, 1f, 1f))]).NewSpawnPlaces);
    }
}
