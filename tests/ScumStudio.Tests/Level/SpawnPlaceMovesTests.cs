using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "click a single spawn point, drag it, rotate it". <see cref="SpawnPlaceMoves"/> builds the one edit of a place
/// moved or turned by hand from what the project has for it: a zone keeps its size, a new place moves from where it was
/// added, a turn changes the yaw only, and the export's view of the places (<see cref="SpawnPlaces.WithEdits"/>) follows.
/// </summary>
public sealed class SpawnPlaceMovesTests
{
    private static readonly SpawnPlace Car = new(SpawnPlaceKind.Vehicle, 7, new FTransform(new FRotator(0f, 30f, 0f).Quaternion(), new FVector(1000f, 2000f, 300f), FVector.One), "City");

    private static readonly SpawnPlace Zone = new(SpawnPlaceKind.Zone, 2, new FTransform(new FRotator(0f, 10f, 0f).Quaternion(), new FVector(-5000f, 4000f, 0f), new FVector(1f, 1f, 1f)), "MTZ_Village", 2000f, 1000f);

    [Fact]
    public void AMoveIsOneSetTransformFromWhereThePlaceStands()
    {
        var state = new EditState();

        var op = SpawnPlaceMoves.Move(Car, state, 1500f, 2500f, 320f)!;

        Assert.Equal(new ActorRef(SpawnPlaces.StaticDataPath, "VehicleSpawn_00007"), op.Target);
        Assert.Equal(new FVector(1000f, 2000f, 300f), op.Old.Location);
        Assert.Equal(new FVector(1500f, 2500f, 320f), op.New.Location);
        Assert.Equal(30f, op.New.Rotation.Yaw, 3);
        state.Apply(op);
        var moved = SpawnPlaces.WithEdits([Car], state).Single();
        Assert.Equal(new FVector(1500f, 2500f, 320f), moved.Transform.Translation);

        // The next move starts from there; a move to where it is changes nothing.
        Assert.Equal(new FVector(1500f, 2500f, 320f), SpawnPlaceMoves.Move(moved, state, 0f, 0f)!.Old.Location);
        Assert.Null(SpawnPlaceMoves.Move(moved, state, 1500f, 2500f));

        state.Apply(op.Inverse());
        Assert.Equal(new FVector(1000f, 2000f, 300f), SpawnPlaces.WithEdits([Car], state).Single().Transform.Translation);
    }

    [Fact]
    public void AMovedZoneKeepsItsSizeAndTurn()
    {
        var state = new EditState();

        state.Apply(SpawnPlaceMoves.Move(Zone, state, 0f, 0f)!);

        var moved = SpawnPlaces.WithEdits([Zone], state).Single();
        Assert.Equal((0f, 0f), (moved.Transform.Translation.X, moved.Transform.Translation.Y));
        Assert.Equal((2000f, 1000f), (moved.SizeX, moved.SizeY));
        Assert.Equal(10f, TransformValue.FromTransform(moved.Transform).Rotation.Yaw, 2);
    }

    [Fact]
    public void ATurnChangesTheYawOnlyAndWrapsAround()
    {
        var state = new EditState();

        var op = SpawnPlaceMoves.Turn(Car, state, 345f)!;

        Assert.Equal(15f, op.New.Rotation.Yaw, 3); // 30 + 345 = 375 = 15
        Assert.Equal(op.Old.Location, op.New.Location);
        Assert.Equal(op.Old.Scale, op.New.Scale);
        Assert.Null(SpawnPlaceMoves.Turn(Car, state, 0f));
    }

    [Fact]
    public void ANewPlaceMovesFromWhereItWasAdded()
    {
        var state = new EditState();
        var added = new AddSpawnPlaceOp("NewVehicleSpawn_0001", SpawnPlaceKind.Vehicle, "City", new TransformValue(new FVector(10f, 20f, 30f), new FRotator(0f, 45f, 0f), FVector.One));
        state.Apply(added);
        var place = SpawnPlaces.WithEdits([], state).Single();

        var op = SpawnPlaceMoves.Move(place, state, 110f, 220f, 40f, 90f)!;
        state.Apply(op);

        Assert.Equal(added.Transform, op.Old);
        Assert.Equal(new TransformValue(new FVector(110f, 220f, 40f), new FRotator(0f, 90f, 0f), FVector.One), state.GetAddedTransform(added.Created));
        var request = SpawnPlacesEditor.Plan(state);
        Assert.Equal(new FVector(110f, 220f, 40f), Assert.Single(request.Created).Transform.Location);
    }

    [Fact]
    public void OnlyIndoorPointsKeepTheirHeight()
    {
        static SpawnPlace Point(SpawnPlaceKind kind, string group) => new(kind, 0, FTransform.Identity, group);

        Assert.Equal(120f, SpawnPlaceMoves.GroundHeight(Point(SpawnPlaceKind.Vehicle, "City"), 100f));
        Assert.Equal(105f, SpawnPlaceMoves.GroundHeight(Point(SpawnPlaceKind.Character, "Exterior"), 100f));
        Assert.Null(SpawnPlaceMoves.GroundHeight(Point(SpawnPlaceKind.Character, SpawnGroundCheck.Interior), 100f));
        Assert.Null(SpawnPlaceMoves.GroundHeight(Point(SpawnPlaceKind.TaggedCharacter, SpawnGroundCheck.Interior), 100f));
        Assert.Null(SpawnPlaceMoves.GroundHeight(Point(SpawnPlaceKind.Vehicle, "City"), null));
    }
}
