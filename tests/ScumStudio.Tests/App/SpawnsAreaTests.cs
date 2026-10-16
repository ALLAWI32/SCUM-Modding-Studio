using System.Diagnostics;
using System.Numerics;
using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using ScumStudio.Level.World;
using Xunit.Abstractions;
using static ScumStudio.Tests.App.SpawnsPageTests;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Spawns map over a made-up island (no paks): a sector lists only its places, new places, removals, a zone's own
/// settings and the ground fix are one journal entry each, a shared list names the threat zones it reaches, and an area
/// query over 25,000 places leaves the UI thread free.
/// </summary>
public sealed class SpawnsAreaTests(ITestOutputHelper output)
{
    private static readonly MapCell A0 = new('A', 0);
    private static readonly MapCell B1 = new('B', 1);

    [AvaloniaFact]
    public async Task SelectingACellListsOnlyItsPlaces()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var a0 = Center(A0);
        using var page = Open(ctx, new FakeSource { Places = [Car(0, A0), Car(1, A0), Car(2, B1), FakeSource.Zone(0, "HTZ_Test_Bunker", a0.X, a0.Y)] });

        page.Area = SpawnArea.Cell(A0);
        await page.AreaQuery;

        // Vehicles: the two cars of A_0, not the one of B_1 nor the zone.
        Assert.Equal([0, 1], page.AreaPlaces.Select(p => p.Place.Index));
        Assert.All(page.AreaPlaces, p => Assert.Equal(SpawnPlaceKind.Vehicle, p.Place.Kind));
        Assert.Equal(Loc.F("Spawns.Area.Count", 2, 0), page.AreaText);

        page.IsZombiesTab = true;
        Assert.Equal([SpawnPlaceKind.Zone], page.AreaPlaces.Select(p => p.Place.Kind));
        Assert.Equal(ScumStudio.App.Controls.SpawnMapLayers.Zombies | ScumStudio.App.Controls.SpawnMapLayers.Zones, page.ShownLayers);
    }

    [AvaloniaFact]
    public async Task NewPlaceRecordsOneAddSpawnPlaceOpAndUndoRemovesIt()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx, new FakeSource { Places = [Car(0, A0, "City"), Car(1, B1, "Boat")], Ground = 1234f });
        Assert.Equal(["Boat", "City"], page.SettingChoices);
        page.ChosenSetting = "City";
        var history = ctx.Services.Projects.History.Count;

        await page.AddPlaceAsync(Center(B1));

        Assert.Equal(history + 1, ctx.Services.Projects.History.Count);
        var place = Assert.IsType<AddSpawnPlaceOp>(Assert.Single(ctx.Services.Projects.Current!.State.AddedActors.Values));
        Assert.Equal((SpawnPlaces.NewName(SpawnPlaceKind.Vehicle, []), SpawnPlaceKind.Vehicle, "City"), (place.NewName, place.Kind, place.Setting));
        Assert.Equal(1234f + SpawnGroundCheck.VehicleLift, place.Transform.Location.Z);
        Assert.True(HeadlessUi.PumpUntil(() => page.Markers.Count == 3)); // a new place, not a copy, on the map

        ctx.Services.Projects.Undo();
        Assert.Empty(ctx.Services.Projects.Current!.State.AddedActors);
        Assert.True(HeadlessUi.PumpUntil(() => page.Markers.Count == 2));
    }

    [AvaloniaFact]
    public async Task RemoveManyIsOneBatch()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        using var page = Open(ctx, new FakeSource { Places = [Car(0, A0), Car(1, A0), Car(2, A0), Car(3, B1)] });
        page.Area = SpawnArea.Cell(A0);
        await page.AreaQuery;
        page.CheckAllCommand.Execute(null);
        Assert.Equal(3, page.Highlighted!.Count);
        var history = ctx.Services.Projects.History.Count;

        page.RemovePlacesCommand.Execute(null);

        Assert.Equal(history + 1, ctx.Services.Projects.History.Count);
        var state = ctx.Services.Projects.Current!.State;
        Assert.Equal([true, true, true, false], Enumerable.Range(0, 4).Select(i => state.IsDeleted(Actor(SpawnPlaceKind.Vehicle, i))));

        ctx.Services.Projects.Undo();
        Assert.All(Enumerable.Range(0, 4), i => Assert.False(ctx.Services.Projects.Current!.State.IsDeleted(Actor(SpawnPlaceKind.Vehicle, i))));
    }

    [AvaloniaFact]
    public async Task SharedPresetEditShowsTheUserCount()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");

        // Two threat zones use the bunker's settings, one another settings that draw from the same encounter.
        using var page = Open(ctx, new FakeSource
        {
            Places = [FakeSource.Zone(0, "HTZ_Test_Bunker", 0f, 0f), FakeSource.Zone(1, "HTZ_Test_Bunker", 100_000f, 0f), FakeSource.Zone(2, "HTZ_Test_Other", 200_000f, 0f)],
        });
        page.IsZombiesTab = true;
        page.SelectedEntry = page.Entries.Single(e => e.PackagePath == Bunker);
        Assert.True(HeadlessUi.PumpUntil(() => page.DetailCards.Count > 0 && page.HasTypes));

        // The zone's own encounter weights reach both zones of its settings; the encounter's presets reach all three.
        var what = page.DetailCards.Single(c => c.Title == Loc.T("Spawns.Card.What")).Rows.OfType<SpawnWeightRowViewModel>().ToList();
        Assert.Equal(Loc.F("Spawns.Shared.Warning", 2), what.Single(r => r.IsHeader).SharedText);
        Assert.Equal(Loc.F("Spawns.Shared.Warning", 3), page.TypeRows[0].SharedText);

        what.First(r => r.IsEntry).Percent = 50;
        var history = ctx.Services.Projects.History.Count;
        page.ApplyCommand.Execute(null);
        Assert.Equal(Loc.F("Spawns.Shared.Confirm", 2), page.SharedConfirmText);
        Assert.Equal(history, ctx.Services.Projects.History.Count);

        // A list the low-threat settings read reaches the whole island.
        var island = new SpawnWeightGroup(new SpawnWeightList(Bunker, "DA_EncounterZone_LowThreat", SpawnCategory.Other, 0, [new SpawnWeight("A", "EncounterData[0].EncounterWeight", 1f)]), 0, () => { }, island: true);
        _ = new SpawnWeightRowViewModel(island, null, "header", "detail", 0f);
        Assert.Equal(Loc.T("Spawns.Shared.Island"), island.Header!.SharedText);
    }

    [AvaloniaFact]
    public async Task OwnZoneCopyRecordsCloneAndChoiceAsOneBatch()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        var a0 = Center(A0);
        using var page = Open(ctx, new FakeSource { Places = [FakeSource.Zone(0, "HTZ_Test_Bunker", a0.X, a0.Y)] });
        page.IsZombiesTab = true;
        page.Area = SpawnArea.Cell(A0);
        await page.AreaQuery;
        page.SelectedPlace = Assert.Single(page.AreaPlaces);
        Assert.True(page.CanOwnCopy);
        Assert.Equal(Bunker, page.SelectedEntry?.PackagePath); // the zone's settings open
        var history = ctx.Services.Projects.History.Count;

        page.OwnCopyCommand.Execute(null);

        Assert.Equal(history + 1, ctx.Services.Projects.History.Count);
        var state = ctx.Services.Projects.Current!.State;
        var clone = Assert.Single(state.AssetClones);
        Assert.Equal((ZoneCopies.Kind, Bunker, Bunker + "_Own"), (clone.Kind, clone.Template, clone.NewPrimary));
        Assert.Equal(Bunker + "_Own", state.GetSpawnChoice(Actor(SpawnPlaceKind.Zone, 0), SpawnChoices.ZoneData));

        // The copy is listed, and its values are the bunker's, written under the copy's own package and export.
        Assert.True(HeadlessUi.PumpUntil(() => page.Entries.Any(e => e.PackagePath == Bunker + "_Own")));
        page.SelectedEntry = page.Entries.Single(e => e.PackagePath == Bunker + "_Own");
        Assert.True(HeadlessUi.PumpUntil(() => page.DetailCards.Count > 0));
        var chance = page.DetailCards.SelectMany(c => c.Rows).OfType<SpawnSliderViewModel>().First();
        Assert.All(chance.Targets, t => Assert.Equal((Bunker + "_Own", "HTZ_Test_Bunker_Own"), (t.Package, t.Tunable.Export)));

        ctx.Services.Projects.Undo();
        Assert.Empty(ctx.Services.Projects.Current!.State.AssetClones);
        Assert.Null(ctx.Services.Projects.Current!.State.GetSpawnChoice(Actor(SpawnPlaceKind.Zone, 0), SpawnChoices.ZoneData));
    }

    [AvaloniaFact]
    public async Task GroundFixAllIsOneBatch()
    {
        using var ctx = AppTestContext.Create(inline: false);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Spawns");
        var a0 = Center(A0);

        // Two zombie points 5 m under the ground; a car 20 cm under it is within the tolerance.
        using var page = Open(ctx, new FakeSource
        {
            Ground = 500f,
            Places =
            [
                FakeSource.Point(SpawnPlaceKind.Character, 0, "Exterior", a0.X, a0.Y),
                FakeSource.Point(SpawnPlaceKind.Character, 1, "Exterior", a0.X + 500f, a0.Y),
                FakeSource.Point(SpawnPlaceKind.Vehicle, 0, "City", a0.X, a0.Y + 500f, 480f),
            ],
        });

        await page.CheckGroundAsync();

        Assert.Equal(2, page.GroundFindings.Count);
        Assert.Equal(Loc.F("Spawns.Ground.Found", 2), page.GroundText);
        var history = ctx.Services.Projects.History.Count;
        page.FixAllGroundCommand.Execute(null);

        Assert.Equal(history + 1, ctx.Services.Projects.History.Count);
        var state = ctx.Services.Projects.Current!.State;
        Assert.All([0, 1], i => Assert.Equal(500f + SpawnGroundCheck.CharacterLift, state.GetTransformOverride(Actor(SpawnPlaceKind.Character, i))!.Value.Location.Z));
        Assert.Empty(page.GroundFindings);
        Assert.Equal(Loc.T("Spawns.Ground.None"), page.GroundText);
    }

    [AvaloniaFact]
    public async Task AreaQueryOnTwentyFiveThousandPlacesDoesNotBlockTheUiThread()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var random = new Random(25_000);
        var places = Enumerable.Range(0, 25_000).Select(i => FakeSource.Point(SpawnPlaceKind.Character, i, "Exterior",
            IslandMap.MinCm + (float)random.NextDouble() * IslandMap.SizeCm, IslandMap.MinCm + (float)random.NextDouble() * IslandMap.SizeCm)).ToList();
        using var page = Open(ctx, new FakeSource { Places = places });
        page.IsZombiesTab = true;
        await page.PlacesReady;
        page.Area = SpawnArea.Cell(new MapCell('Z', 4)); // warms the JIT
        await page.AreaQuery;

        var worst = TimeSpan.Zero;
        var listed = 0;
        foreach (var cell in new[] { A0, B1, new MapCell('C', 2), new MapCell('D', 3), new MapCell('Z', 0) })
        {
            var clock = Stopwatch.StartNew();
            page.Area = SpawnArea.Cell(cell);
            var setter = clock.Elapsed;
            await page.AreaQuery;
            worst = TimeSpan.FromTicks(Math.Max(worst.Ticks, Math.Max(setter.Ticks, page.LastAreaUiTime.Ticks)));
            listed += page.AreaPlaces.Count;
            Assert.All(page.AreaPlaces, p => Assert.Equal(cell, IslandMap.CellAt(p.Place.Transform.Translation.X, p.Place.Transform.Translation.Y)));
        }

        output.WriteLine($"25,000 places: {listed} listed in 5 cells, worst UI-thread time per selection {worst.TotalMilliseconds:0.00} ms");
        Assert.InRange(listed, 4_000, 6_000); // about a 25th of the places per cell
        Assert.True(worst < TimeSpan.FromMilliseconds(16), $"UI thread busy {worst.TotalMilliseconds:0.0} ms for one selection");
    }

    private static SpawnsPageViewModel Open(AppTestContext ctx, FakeSource source)
    {
        var page = new SpawnsPageViewModel(ctx.Services, source);
        Assert.True(HeadlessUi.PumpUntil(() => page.Markers.Count == source.Places.Count && page.Entries.Count > 0));
        return page;
    }


    private static Vector2 Center(MapCell cell)
    {
        var (minX, minY, maxX, maxY) = IslandMap.CellRect(cell);
        return new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);
    }

    private static SpawnPlace Car(int index, MapCell cell, string group = "City")
    {
        var at = Center(cell);
        return FakeSource.Point(SpawnPlaceKind.Vehicle, index, group, at.X + index * 100f, at.Y);
    }

    private static ActorRef Actor(SpawnPlaceKind kind, int index) => new(SpawnPlaces.StaticDataPath, SpawnPlaces.ActorName(kind, index));
}
