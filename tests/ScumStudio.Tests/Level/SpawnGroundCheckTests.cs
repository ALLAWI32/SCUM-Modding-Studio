using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.Level;

/// <summary>
/// seviscache: "simple shapes revealed spawners below the terrain, maybe that's why people can't find vehicles" (#14). The
/// under-ground check on a flat fake terrain at Z 1000: what it finds, what it leaves alone, and the snap edits with their
/// one undo.
/// </summary>
public sealed class SpawnGroundCheckTests
{
    private const float Ground = 1000f;

    [Fact]
    public void VehicleFortyCentimetresUnderIsFound()
    {
        var car = Place(SpawnPlaceKind.Vehicle, 1, Ground - 40f, "City");
        var found = Assert.Single(Run(car));
        Assert.Same(car, found.Place);
        Assert.Equal(Ground, found.Ground);
        Assert.Equal(40f, found.Depth, 3);
    }

    [Fact]
    public void TwentyUnderIsNot()
    {
        Assert.Empty(Run(Place(SpawnPlaceKind.Vehicle, 1, Ground - 20f, "City"), Place(SpawnPlaceKind.Character, 2, Ground - 45f, "Exterior")));

        // A zombie point is found from 50 cm down, a car from 30 cm.
        Assert.Single(Run(Place(SpawnPlaceKind.Character, 2, Ground - 60f, "Exterior")));
    }

    [Fact]
    public void InteriorPointsAreIgnored()
    {
        // Cellars and bunkers are below the terrain on purpose; so are points inside an underground level's box, and points
        // over the sea (no terrain there).
        var cellar = Place(SpawnPlaceKind.Character, 1, Ground - 400f, SpawnGroundCheck.Interior);
        var bunker = Place(SpawnPlaceKind.TaggedCharacter, 2, Ground - 900f, "Exterior", x: 50_000f);
        var sea = Place(SpawnPlaceKind.Vehicle, 3, -500f, "Boat", x: -90_000f);
        var keep = SpawnGroundCheck.OutsideUnderground([(new FVector(40_000f, -10_000f, -5_000f), new FVector(60_000f, 10_000f, 500f))]);
        var findings = SpawnGroundCheck.Run([cellar, bunker, sea], keep, (x, _) => x < -80_000f ? null : Ground, null, CancellationToken.None);
        Assert.Empty(findings);
    }

    [Fact]
    public void ZonesAreNeverSnapped()
    {
        var zone = Place(SpawnPlaceKind.Zone, 1, Ground - 5_000f, "MTZ_Village");
        var area = Place(SpawnPlaceKind.Animal, 2, Ground - 5_000f, "Forest");
        Assert.Empty(Run(zone, area));
        Assert.Empty(SpawnGroundCheck.SnapOps([new SpawnGroundFinding(zone, zone.Transform.Translation.Z, Ground, 5_000f)], new EditState()));
    }

    [Fact]
    public void SnapOpsPutVehiclesTwentyAboveAndCharactersFiveAbove()
    {
        var car = Place(SpawnPlaceKind.Vehicle, 7, Ground - 300f, "City");
        var zombie = Place(SpawnPlaceKind.Character, 8, Ground - 120f, "Exterior");
        var ops = SpawnGroundCheck.SnapOps(Run(car, zombie), new EditState()).Cast<SetTransformOp>().ToList();

        Assert.Equal(2, ops.Count);
        var carOp = ops.Single(o => o.Target.Actor == car.ActorName);
        var zombieOp = ops.Single(o => o.Target.Actor == zombie.ActorName);
        Assert.Equal(SpawnPlaces.StaticDataPath, carOp.Target.Level);
        Assert.Equal(Ground + 20f, carOp.New.Location.Z);
        Assert.Equal(Ground + 5f, zombieOp.New.Location.Z);

        // Only the height changes; the old value is where the place stands now.
        Assert.Equal(car.Transform.Translation, carOp.Old.Location);
        Assert.Equal(carOp.Old.Location with { Z = Ground + 20f }, carOp.New.Location);
        Assert.Equal(carOp.Old.Rotation, carOp.New.Rotation);
        Assert.Equal(FVector.One, carOp.New.Scale);
    }

    [Fact]
    public void SnapKeepsTheStoredScaleAndAnAddedPlacesOwnTransform()
    {
        // Review: a snap wrote scale 1 over a point's stored scale (a byte change nobody asked for, and an Old that is not
        // what the Map holds). A place the project added starts from its own transform, not from the game's.
        var scale = new FVector(1.5f, 1.5f, 2f);
        var stock = Place(SpawnPlaceKind.Vehicle, 7, Ground - 300f, "City");
        var car = stock with { Transform = stock.Transform with { Scale3D = scale } };
        var added = Place(SpawnPlaceKind.Vehicle, 9000, Ground - 100f, "City", x: 3000f);
        var state = new EditState();
        var placed = new TransformValue(new FVector(3000f, 0f, Ground - 100f), new FRotator(0f, 90f, 0f), new FVector(2f, 2f, 2f));
        state.Apply(new DuplicateActorOp(Ref(car), added.ActorName, placed));

        var ops = SpawnGroundCheck.SnapOps(Run(car, added), state).Cast<SetTransformOp>().ToList();
        var carOp = ops.Single(o => o.Target.Actor == car.ActorName);
        Assert.Equal(scale, carOp.Old.Scale);
        Assert.Equal(scale, carOp.New.Scale);
        var addedOp = ops.Single(o => o.Target.Actor == added.ActorName);
        Assert.Equal(placed, addedOp.Old);
        Assert.Equal(placed with { Location = placed.Location with { Z = Ground + 20f } }, addedOp.New);
    }

    [Fact]
    public void OneUndoOfTheBatchRestoresAll()
    {
        var places = Enumerable.Range(0, 5).Select(i => Place(i % 2 == 0 ? SpawnPlaceKind.Vehicle : SpawnPlaceKind.Character, i, Ground - 100f - (i * 10f), "Exterior", x: i * 1000f)).ToList();
        var state = new EditState();
        var batch = new BatchOp("Snap to ground", SpawnGroundCheck.SnapOps(Run([.. places]), state));
        Assert.Equal(5, batch.Ops.Count);

        state.Apply(batch);
        Assert.All(places, p => Assert.True(state.GetTransformOverride(Ref(p))!.Value.Location.Z > Ground));

        state.Apply(batch.Inverse());
        Assert.All(places, p => Assert.Equal(p.Transform.Translation.Z, state.GetTransformOverride(Ref(p))?.Location.Z ?? p.Transform.Translation.Z));
    }

    [Fact]
    public void PlacesAreVisitedSquareBySquareAndCanBeCancelled()
    {
        // Far apart first, then near: the ground is asked square by square, so a tile cache reads each tile once.
        var asked = new List<float>();
        var places = new[] { 90_000f, 10_000f, 95_000f, 12_000f }.Select((x, i) => Place(SpawnPlaceKind.Vehicle, i, Ground - 100f, "City", x: x)).ToList();
        SpawnGroundCheck.Run(places, _ => true, (x, _) => { asked.Add(x); return Ground; }, null, CancellationToken.None);
        Assert.Equal([10_000f, 12_000f, 90_000f, 95_000f], asked);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => SpawnGroundCheck.Run(places, _ => true, (_, _) => Ground, null, cancel.Token));
    }

    [Fact]
    public void GroundHeightsKeepOnlyTheMostRecentTiles()
    {
        // Three 1 km tiles in a row and room for two: asked tile by tile each is read once; going back to a dropped one reads it again.
        var read = new List<string>();
        var tiles = Enumerable.Range(0, 3).Select(i => ($"/Game/Landscape_{i}", new ScumStudio.Formats.Packages.WorldTileInfo((i, 0, 0),
            new FVector(i * 100_000f, 0f, -1000f), new FVector((i + 1) * 100_000f, 100_000f, 1000f), true, default!, false, string.Empty, [], 0))).ToList();
        var heights = new ScumStudio.Level.Export.GroundHeights(() => tiles, path => { read.Add(path); return []; }, maxTiles: 2);

        foreach (var x in new[] { 10f, 20f, 150_000f, 160_000f, 250_000f, 260_000f })
        {
            Assert.Null(heights.At(x, 50_000f)); // a tile with no landscape component there: no ground
        }

        Assert.Equal(["/Game/Landscape_0", "/Game/Landscape_1", "/Game/Landscape_2"], read);
        Assert.Equal(2, heights.LoadedTiles);
        heights.At(50f, 50_000f);
        Assert.Equal(4, heights.TileReads);
        Assert.Equal(2, heights.LoadedTiles);
        Assert.Null(heights.At(-5f, 50_000f)); // outside every tile: nothing read
        Assert.Equal(4, heights.TileReads);
    }

    [Fact]
    public void AnUnreadableLandscapeTileHasNoGroundInsteadOfFailingTheCheck()
    {
        // Audit F190: a mod's tile whose header the reader rejects threw out of the tile list (and the whole export with it).
        using var data = new SyntheticSpawnData();
        var stem = Path.Combine(data.Root, "SCUM", "Content", "ConZ_Files", "Maps", "The_Island", "Landscape_A_0_1");
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        File.WriteAllBytes(stem + ".umap", [0x13, 0x37, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        File.WriteAllBytes(stem + ".uexp", [0, 0, 0, 0]);
        using var catalog = data.Open();

        Assert.True(catalog.TryGetPackageFile("/Game/ConZ_Files/Maps/The_Island/Landscape_A_0_1", out _));
        Assert.Empty(ScumStudio.Level.Export.GroundHeights.LandscapeTiles(catalog));
        Assert.Null(new ScumStudio.Level.Export.GroundHeights(catalog).At(-900_000f, -580_000f));
    }

    private static IReadOnlyList<SpawnGroundFinding> Run(params SpawnPlace[] places) =>
        SpawnGroundCheck.Run(places, _ => true, (_, _) => Ground, null, CancellationToken.None);

    private static SpawnPlace Place(SpawnPlaceKind kind, int index, float z, string group, float x = 0f) =>
        new(kind, index, new FTransform(new FVector(x, 0f, z)), group, kind is SpawnPlaceKind.Zone or SpawnPlaceKind.Animal ? 5_000f : 0f, kind is SpawnPlaceKind.Zone or SpawnPlaceKind.Animal ? 5_000f : 0f);

    private static ActorRef Ref(SpawnPlace place) => new(SpawnPlaces.StaticDataPath, place.ActorName);
}
