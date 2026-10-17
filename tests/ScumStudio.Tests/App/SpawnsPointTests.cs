using System.Numerics;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using static ScumStudio.Tests.App.SpawnsPageTests;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "FULL control of each spawn place, not only picking a sector": the Spawns page over a made-up island (no paks).
/// A place picked on the map shows its details; a drag, a nudge, a turn, typed coordinates, a new setting and a delete are
/// one journal entry each, on the ground when it is known (indoor points keep their height), and undo takes them back.
/// </summary>
public sealed class SpawnsPointTests
{
    private const float Ground = 500f;

    [AvaloniaFact]
    public async Task APickedPlaceShowsItsDetails()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx, new FakeSource { Places = [Car(0, "City", 1000f, 2000f, 300f, 90f), Car(1, "Boat", 9000f, 0f, 0f)], Ground = Ground });
        Assert.Null(page.Point);

        page.SelectedMarkerId = 0;

        var point = page.Point!;
        Assert.True(page.HasPoint);
        Assert.Equal("VehicleSpawn_00000", point.Name);
        Assert.Equal(Loc.T("Spawns.Place.Kind.Vehicle"), point.KindText);
        Assert.Equal("City", point.Setting);
        Assert.Equal(["Boat", "City"], point.Choices);
        Assert.True(point.HasChoices);
        Assert.False(point.IsIndoor);
        Assert.Equal(Loc.F("Spawns.Point.Where", SpawnPointViewModel.Metres(1000f), SpawnPointViewModel.Metres(2000f), SpawnPointViewModel.Metres(300f), 90f.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture)), point.CoordinatesText);
        Assert.Equal(SpawnPointViewModel.Metres(1000f), point.XText);
    }

    [AvaloniaFact]
    public async Task ADragIsOneUndoStepOntoTheGround()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx, new FakeSource { Places = [Car(0, "City", 1000f, 2000f, 300f, 90f), Car(1, "Boat", 9000f, 0f, 0f)], Ground = Ground });
        page.SelectedMarkerId = 0;
        var history = ctx.Services.Projects.History.Count;

        page.MovePlaceCommand.Execute(new SpawnMapMove(0, new Vector2(1500f, 2500f)));
        await page.PointEdit;

        Assert.Equal(history + 1, ctx.Services.Projects.History.Count);
        var value = ctx.Services.Projects.Current!.State.GetTransformOverride(Actor(0))!.Value;
        Assert.Equal(new FVector(1500f, 2500f, Ground + SpawnGroundCheck.VehicleLift), value.Location);
        Assert.Equal(90f, value.Rotation.Yaw, 3);

        // The map and the card follow; the place stays picked.
        Assert.True(HeadlessUi.PumpUntil(() => page.Markers[0].X == 1500f && page.Point?.Value.Location.X == 1500f));
        Assert.Equal(0, page.SelectedMarkerId);

        ctx.Services.Projects.Undo();
        Assert.True(HeadlessUi.PumpUntil(() => page.Markers[0].X == 1000f && page.Point?.Value.Location == new FVector(1000f, 2000f, 300f)));
    }

    [AvaloniaFact]
    public async Task WithoutSnappingOrIndoorsAMoveKeepsTheHeight()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        var indoor = FakeSource.Point(SpawnPlaceKind.Character, 0, SpawnGroundCheck.Interior, 5000f, 0f, 700f);
        using var page = Open(ctx, new FakeSource { Places = [Car(0, "City", 1000f, 2000f, 300f), indoor], Ground = Ground });
        var state = ctx.Services.Projects.Current!.State;

        page.SnapToGround = false;
        page.SelectedMarkerId = 0;
        page.MovePlaceCommand.Execute(new SpawnMapMove(0, new Vector2(1500f, 2500f)));
        await page.PointEdit;
        Assert.Equal(new FVector(1500f, 2500f, 300f), state.GetTransformOverride(Actor(0))!.Value.Location);

        page.SnapToGround = true;
        page.IsZombiesTab = true;
        Assert.Null(page.Point); // the vehicle is not shown here
        page.SelectedMarkerId = 1;
        Assert.True(page.Point!.IsIndoor);
        Assert.True(page.Point.SpawnsFromZone);
        page.MovePlaceCommand.Execute(new SpawnMapMove(1, new Vector2(6000f, 100f)));
        await page.PointEdit;
        Assert.Equal(new FVector(6000f, 100f, 700f), state.GetTransformOverride(new ActorRef(SpawnPlaces.StaticDataPath, indoor.ActorName))!.Value.Location);
    }

    [AvaloniaFact]
    public async Task TurnsAndTypedCoordinatesAreOneStepEach()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx, new FakeSource { Places = [Car(0, "City", 1000f, 2000f, 300f, 90f)], Ground = Ground });
        var state = ctx.Services.Projects.Current!.State;
        page.SelectedMarkerId = 0;
        var history = ctx.Services.Projects.History.Count;

        page.TurnPlaceCommand.Execute(new SpawnMapTurn(0, 15f));
        Assert.Equal(105f, state.GetTransformOverride(Actor(0))!.Value.Rotation.Yaw, 3);
        Assert.True(HeadlessUi.PumpUntil(() => MathF.Abs(page.Point!.Value.Rotation.Yaw - 105f) < 0.01f));

        // X and Y typed, the height left alone: it goes onto the ground there.
        page.Point!.XText = "123.5";
        page.Point.YText = "-45.25";
        page.ApplyPointCommand.Execute(null);
        await page.PointEdit;
        Assert.Equal(new FVector(12350f, -4525f, Ground + SpawnGroundCheck.VehicleLift), state.GetTransformOverride(Actor(0))!.Value.Location);
        Assert.True(HeadlessUi.PumpUntil(() => page.Point?.Value.Location.X == 12350f));

        // A typed height and turn are used as they are.
        page.Point!.ZText = "12.34";
        page.Point.YawText = "-90";
        page.ApplyPointCommand.Execute(null);
        await page.PointEdit;
        var value = state.GetTransformOverride(Actor(0))!.Value;
        Assert.Equal(new FVector(12350f, -4525f, 1234f), value.Location);
        Assert.Equal(-90f, value.Rotation.Yaw, 3);
        Assert.Equal(history + 3, ctx.Services.Projects.History.Count);

        // Not a number: nothing recorded, the card says why.
        Assert.True(HeadlessUi.PumpUntil(() => page.Point?.Value.Location.Z == 1234f));
        page.Point!.XText = "abc";
        page.ApplyPointCommand.Execute(null);
        Assert.Equal(Loc.T("Spawns.Point.Bad"), page.Point.Error);
        Assert.Equal(history + 3, ctx.Services.Projects.History.Count);
    }

    [AvaloniaFact]
    public async Task ThePickedPlaceSpawnsSomethingElseIsDeletedAndShownOnTheMap()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx, new FakeSource { Places = [Car(0, "City", 1000f, 2000f, 300f), Car(1, "City", 9000f, 0f, 0f), Car(2, "Boat", 0f, 9000f, 0f)], Ground = Ground });
        var state = ctx.Services.Projects.Current!.State;
        SpawnPlace? opened = null;
        FVector? home = null;
        page.SetMapOpener((p, h) => (opened, home) = (p, h));
        page.SelectedMarkerId = 1;

        // Moved far away first: the Map gets where it is now, and where its document holds it (the game's place).
        page.MovePlaceCommand.Execute(new SpawnMapMove(1, new Vector2(300_000f, 0f)));
        await page.PointEdit;
        Assert.True(HeadlessUi.PumpUntil(() => page.Point?.Value.Location.X == 300_000f));
        Assert.True(page.ShowOnMapCommand.CanExecute(null));
        page.ShowOnMapCommand.Execute(null);
        Assert.Equal("VehicleSpawn_00001", opened?.ActorName);
        Assert.Equal(300_000f, opened!.Transform.Translation.X);
        Assert.Equal(new FVector(9000f, 0f, 0f), home);

        page.Point!.Setting = "Boat";
        Assert.Equal("Boat", state.GetSpawnChoice(Actor(1), SpawnChoices.VehicleGroup));
        Assert.True(HeadlessUi.PumpUntil(() => page.Point?.Place.Group == "Boat"));

        var history = ctx.Services.Projects.History.Count;
        page.DeletePlaceCommand.Execute(1); // the Delete key on the map
        Assert.Equal(history + 1, ctx.Services.Projects.History.Count);
        Assert.True(state.IsDeleted(Actor(1)));
        Assert.True(HeadlessUi.PumpUntil(() => page.Markers.Count == 2 && page.Point is null));

        ctx.Services.Projects.Undo();
        Assert.True(HeadlessUi.PumpUntil(() => page.Markers.Count == 3));
    }

    [AvaloniaFact]
    public async Task ANewPlaceIsPickedOnceTheMapHasIt()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx, new FakeSource { Places = [Car(0, "City", 1000f, 2000f, 300f)], Ground = Ground });
        page.ChosenSetting = "City";

        await page.AddPlaceAsync(new Vector2(3000f, 3000f));

        Assert.True(HeadlessUi.PumpUntil(() => page.Point?.Name == SpawnPlaces.NewName(SpawnPlaceKind.Vehicle, [])));
        Assert.Equal(1, page.SelectedMarkerId);
        Assert.Equal(new FVector(3000f, 3000f, Ground + SpawnGroundCheck.VehicleLift), page.Point!.Value.Location);
    }

    [AvaloniaFact]
    public async Task TheAreaListFollowsThePickedPlace()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        var (minX, minY, maxX, maxY) = ScumStudio.Level.World.IslandMap.CellRect(new ScumStudio.Level.World.MapCell('A', 0));
        var (x, y) = ((minX + maxX) / 2f, (minY + maxY) / 2f);
        using var page = Open(ctx, new FakeSource { Places = [Car(0, "City", x, y, 0f), Car(1, "City", x + 100f, y, 0f), Car(2, "City", 0f, 0f, 0f)], Ground = Ground });
        page.Area = SpawnArea.Cell(new ScumStudio.Level.World.MapCell('A', 0));
        await page.AreaQuery;
        Assert.Equal(2, page.AreaPlaces.Count);

        page.SelectedPlace = page.AreaPlaces[0]; // a row picked in the list is the picked place
        Assert.Equal(page.AreaPlaces[0].Id, page.SelectedMarkerId);
        Assert.Equal(page.AreaPlaces[0].Place.ActorName, page.Point?.Name);

        page.SelectedMarkerId = 2; // a place outside the area: no row stays picked
        Assert.Null(page.SelectedPlace);
        Assert.Equal("VehicleSpawn_00002", page.Point?.Name);

        page.SelectedMarkerId = page.AreaPlaces[1].Id;
        Assert.Same(page.AreaPlaces[1], page.SelectedPlace);

        // A new place inside the area: once the map and the list have it, it is the picked place and the picked row.
        page.ChosenSetting = "City";
        await page.AddPlaceAsync(new Vector2(x + 300f, y));
        var name = SpawnPlaces.NewName(SpawnPlaceKind.Vehicle, []);
        Assert.True(HeadlessUi.PumpUntil(() => page.Point?.Name == name && page.AreaPlaces.Count == 3));
        await page.AreaQuery;
        Assert.Equal(name, page.SelectedPlace?.Place.ActorName);
        Assert.Equal(page.SelectedPlace!.Id, page.SelectedMarkerId);
    }

    private static SpawnsPageViewModel Open(AppTestContext ctx, FakeSource source)
    {
        var page = new SpawnsPageViewModel(ctx.Services, source);
        Assert.True(HeadlessUi.PumpUntil(() => page.Markers.Count == source.Places.Count && page.Entries.Count > 0));
        return page;
    }

    private static SpawnPlace Car(int index, string group, float x, float y, float z, float yaw = 0f) =>
        new(SpawnPlaceKind.Vehicle, index, new FTransform(new FRotator(0f, yaw, 0f).Quaternion(), new FVector(x, y, z), FVector.One), group);

    private static ActorRef Actor(int index) => new(SpawnPlaces.StaticDataPath, SpawnPlaces.ActorName(SpawnPlaceKind.Vehicle, index));
}
