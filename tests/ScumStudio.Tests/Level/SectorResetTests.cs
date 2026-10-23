using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Projects;
using ScumStudio.Level.Spawns;
using ScumStudio.Level.World;
using static ScumStudio.Tests.Level.EditOpTests;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Discord 2026-10-19, pheidian: "how do I revert one area back to default (one sector, or part of sector)?" (he hand-edited
/// the journal). Reset a sector to stock takes back every change in one sector as ONE journal edit, of every kind the map
/// makes, leaves the other sectors, the island-wide data it cannot place and the vehicle/item edits alone, and Undo (in
/// memory and after the journal is read again) puts back exactly what was there. No game files.
/// </summary>
public sealed class SectorResetTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/B_1_Farm";
    private const string Tile = "/Game/ConZ_Files/Maps/The_Island/Landscape_A_0_1c";
    private const string Water = "/Game/ConZ_Files/Maps/The_Island/WaterSplines";
    private const string Lamp = "/Game/BP/BP_Lamp.BP_Lamp_C";
    private static readonly MapCell A0 = new('A', 0);
    private static readonly MapCell B1 = new('B', 1);

    /// <summary>A world point (cm) inside <paramref name="cell"/>, <paramref name="dx"/>/<paramref name="dy"/> metres from its centre.</summary>
    private static (float X, float Y) In(MapCell cell, float dx = 0, float dy = 0)
    {
        var (minX, minY, maxX, maxY) = IslandMap.CellRect(cell);
        return (((minX + maxX) / 2f) + (dx * 100f), ((minY + maxY) / 2f) + (dy * 100f));
    }

    private static TransformValue At(MapCell cell, float dx = 0) => TransformValue.At(In(cell, dx).X, In(cell, dx).Y, 1000f);

    [Fact]
    public void EveryKindOfChangeInTheSectorIsTakenBackAsOneEditThatUndoPutsBack()
    {
        using var temp = new LevelTempDirectory();
        var folder = temp.Combine("Reset.ssproj");
        var rock = new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, At(A0));
        var tree = new InstanceRef(Outpost, "InstancedFoliageActor_0", "FoliageISM_0", 500);
        var addTree = new AddInstanceOp(tree, Somewhere);
        var copy = new DuplicateActorOp(new ActorRef(Outpost, "House_2"), "House_2_Copy", Somewhere);
        var place = new AddSpawnPlaceOp("NewVehicleSpawn_0001", SpawnPlaceKind.Vehicle, "City", At(A0, 50));
        var farPlace = new AddSpawnPlaceOp("NewVehicleSpawn_0002", SpawnPlaceKind.Vehicle, "City", At(B1));
        var zone = new ActorRef(SpawnPlaces.StaticDataPath, SpawnPlaces.ActorName(SpawnPlaceKind.Zone, 222));
        var (hx, hy) = In(A0, 30, 30);
        var hole = new TerrainHole(Tile, TerrainHoleShape.Square, hx, hy, 300f);
        var (zx, zy) = In(A0, -40, 10);
        var subZone = new SubZone(new FVector(zx - 1000f, zy - 1000f, 0f), new FVector(zx + 1000f, zy + 1000f, 5000f), "HTZ_Military_TV_Bunker_NPC");
        var points = new[] { new SpawnPoint(0, TransformValue.Identity), new SpawnPoint(1, TransformValue.At(100, 0, 0)) };
        EditOp[] inside =
        [
            new DeleteActorOp(new ActorRef(Outpost, "House_1")),
            new DeleteComponentsOp(new ActorRef(Outpost, "Fireplace_2"), ["Fire", "Smoke"]),
            new DeleteInstanceOp(new InstanceRef(Outpost, "InstancedFoliageActor_0", "FoliageISM_0", 3)),
            new SetTransformOp(new ActorRef(Outpost, "Barn_3"), TransformValue.Identity, Somewhere),
            new SetTransformOp(new ActorRef(Outpost, "Barn_3"), TransformValue.Identity, Elsewhere, "Door"),
            new SetInstanceTransformOp(new InstanceRef(Outpost, "InstancedFoliageActor_0", "FoliageISM_0", 4), TransformValue.Identity, Somewhere),
            new BendActorOp(new ActorRef(Outpost, "Wall_5"), 0f, 20f, NewSway1: 30f, NewLegs: 150f),
            new SwaySegmentOp(new ActorRef(Tile, "LandscapeSplinesComponent_0"), "SplineMesh_1", 0f, 0f, 50f, -50f),
            new SetSpawnPointsOp(new ActorRef(Outpost, "Spawner_6"), null, "_spawnPoints", points, [points[1]]),
            new ReplaceMeshOp(new ActorRef(Outpost, "Wall_7"), null, "/Game/A/SM_Wall.SM_Wall", "/Game/A/SM_Fence.SM_Fence"),
            new SetLootOp(new ActorRef(Outpost, "Cabinet_8"), null, new LootSetting(true, ["/Game/Loot/Examine_A.Examine_A_C"]), new LootSetting(false, [])),
            new SetClearGrassOp(new ActorRef(Outpost, "Barn_3"), null, true),
            new SetSpawnChoiceOp(new ActorRef(Outpost, "Sentry_9"), "SentryClass", null, "/Game/Sentries/BP_Sentry2.BP_Sentry2_C"),
            new BatchOp("Paint 2 rocks", [rock, addTree]), // a brush stroke: one History row of its own
            new SetTransformOp(rock.Created, At(A0), Elsewhere),
            new BendActorOp(rock.Created, 0f, -15f),
            new SetInstanceTransformOp(tree, Somewhere, Elsewhere),
            new DeleteActorOp(rock.Created),
            copy,
            new SetLootOp(copy.Created, null, new LootSetting(true, []), new LootSetting(false, [])),
            place,
            new TerrainHoleOp(hole),
            new AddSubZoneOp(zone, subZone),
        ];
        EditOp[] outside =
        [
            new DeleteActorOp(new ActorRef(Farm, "Silo_1")),
            new SetTransformOp(new ActorRef(Farm, "Silo_2"), TransformValue.Identity, Somewhere),
            new DeleteActorOp(new ActorRef(Water, "WaterSpline_3")), // island-wide, its place unknown: it stays
            farPlace,
            new SetAssetValueOp("/Game/ConZ_Files/Vehicles/Kinglet/BP_Kinglet", "Default__BP_Kinglet_C", "MaxSpeed", "Float", "100", "120"),
        ];

        using var project = Project.Create(folder, "Reset");
        foreach (var op in outside.Take(2).Concat(inside).Concat(outside.Skip(2)))
        {
            project.Apply(op);
        }

        var before = Snapshot(project.State);
        var plan = project.State.PlanSectorReset(A0);

        Assert.NotNull(plan.Op);
        Assert.Equal("Reset sector A_0 to stock", plan.Op.Title);
        Assert.Equal(0, plan.KeptCopySources);
        Assert.Equal(1, plan.Unplaced); // the water spline
        Assert.Equal(new Dictionary<SectorResetKind, int>
        {
            [SectorResetKind.RestoredObjects] = 1,
            [SectorResetKind.RestoredParts] = 2,
            [SectorResetKind.RestoredInstances] = 1,
            [SectorResetKind.MovedBack] = 2, // the barn (root and door count once) and one tree
            [SectorResetKind.Shapes] = 2, // the wall's bend and the road piece's sway
            [SectorResetKind.Spawners] = 2, // spawn points and the sentry's class
            [SectorResetKind.Meshes] = 1,
            [SectorResetKind.Loot] = 1, // the cabinet; the copy's loot goes with the copy
            [SectorResetKind.Grass] = 1,
            [SectorResetKind.RemovedObjects] = 3, // the painted rock, the copy and the new spawn place
            [SectorResetKind.RemovedInstances] = 1,
            [SectorResetKind.Holes] = 1,
            [SectorResetKind.SubZones] = 1,
        }, plan.Counts.OrderBy(c => c.Key).ToDictionary());
        Assert.Equal(19, plan.Total);

        // One History row; the sector is as the game has it, everything else as it was.
        var rows = project.History.Count;
        project.Apply(plan.Op);
        Assert.Equal(rows + 1, project.History.Count);
        Assert.Equal(Snapshot(EditState.Replay(outside)), Snapshot(project.State));
        Assert.Null(project.State.PlanSectorReset(A0).Op);
        Assert.NotNull(project.State.PlanSectorReset(B1).Op);

        // Undo puts back exactly what was there; Redo takes it again; the journal read again agrees.
        project.Undo();
        Assert.Equal(before, Snapshot(project.State));
        project.Redo();
        Assert.Equal(Snapshot(EditState.Replay(outside)), Snapshot(project.State));
        project.Undo();
        project.Dispose();
        using var again = Project.Open(folder);
        Assert.Empty(again.ReplayProblems);
        Assert.Equal(before, Snapshot(again.State));
        again.Redo();
        Assert.Equal(Snapshot(EditState.Replay(outside)), Snapshot(again.State));
    }

    [Fact]
    public void AnAddedObjectACopyElsewhereWasMadeFromStaysWithItsEdits()
    {
        var bench = new AddStaticMeshActorOp(Outpost, "SM_Bench_Added", Rock, At(A0));
        var lamp = new AddBlueprintActorOp(Outpost, "BP_Lamp_Added", Lamp, new ActorRef(Outpost, "BP_Lamp_1"), At(A0));
        var lampCopy = new AddBlueprintActorOp(Farm, "BP_Lamp_Added2", Lamp, lamp.Created, At(B1)); // an older version copied copies
        var state = EditState.Replay([bench, lamp, new SetTransformOp(lamp.Created, At(A0), Elsewhere), lampCopy]);

        var plan = state.PlanSectorReset(A0);

        Assert.Equal(1, plan.KeptCopySources);
        Assert.Equal(1, plan.Counts[SectorResetKind.RemovedObjects]);
        Assert.False(plan.Counts.ContainsKey(SectorResetKind.MovedBack));
        state.Apply(plan.Op!);
        Assert.False(state.IsAdded(bench.Created));
        Assert.True(state.IsAdded(lamp.Created));
        Assert.Equal(Elsewhere, state.GetTransformOverride(lamp.Created));
    }

    [Fact]
    public void IslandWideObjectsBelongToTheSectorTheyStandIn()
    {
        var spline = new ActorRef(Water, "WaterSpline_3");
        var placed = new AddStaticMeshActorOp(Water, "SM_Buoy_Added", Rock, At(A0));
        var movedPlace = new ActorRef(SpawnPlaces.StaticDataPath, SpawnPlaces.ActorName(SpawnPlaceKind.Vehicle, 12));
        var chosen = new ActorRef(SpawnPlaces.StaticDataPath, SpawnPlaces.ActorName(SpawnPlaceKind.Vehicle, 13));
        var state = EditState.Replay(
        [
            new DeleteActorOp(spline),
            placed,
            new SetTransformOp(movedPlace, At(A0), At(B1)), // moved from A_0 to B_1: it belongs where the game has it
            new SetSpawnChoiceOp(chosen, "VehicleLevelSpawnGroup", null, "Village"),
        ]);

        // Without a place for the stock ones: the new object by its own place, the moved one by where it stood.
        var unknown = state.PlanSectorReset(A0);
        Assert.Equal(2, unknown.Unplaced);
        Assert.Equal(1, unknown.Counts[SectorResetKind.RemovedObjects]);
        Assert.Equal(1, unknown.Counts[SectorResetKind.MovedBack]);
        Assert.False(unknown.Counts.ContainsKey(SectorResetKind.RestoredObjects));

        // With them (the map knows loaded objects and the island's spawn places).
        (float X, float Y)? Locate(ActorRef actor) => actor == spline ? In(A0, 100) : actor == chosen ? In(B1) : null;
        var known = state.PlanSectorReset(A0, Locate);
        Assert.Equal(0, known.Unplaced);
        Assert.Equal(1, known.Counts[SectorResetKind.RestoredObjects]);
        Assert.False(known.Counts.ContainsKey(SectorResetKind.Spawners));
        Assert.Equal(1, state.PlanSectorReset(B1, Locate).Counts[SectorResetKind.Spawners]);
    }

    [Fact]
    public void ASectorWithoutChangesHasNothingToReset()
    {
        var state = EditState.Replay([new DeleteActorOp(new ActorRef(Farm, "Silo_1"))]);
        var plan = state.PlanSectorReset(A0);
        Assert.Null(plan.Op);
        Assert.Empty(plan.Counts);
        Assert.Equal(0, plan.Total);
        Assert.Null(new EditState().PlanSectorReset(B1).Op);
    }

    // Everything the state holds, including what Describe leaves out (loot, grass, spawn choices, holes, added places).
    private static string Snapshot(EditState state) =>
        state.Describe()
        + string.Join('\n', state.LootOverrides.Select(l => $"loot {l.Actor}.{l.Component} {l.Loot.Lootable} {string.Join(',', l.Loot.Presets)}").Order(StringComparer.Ordinal))
        + string.Join('\n', state.SpawnChoices.Select(c => $"choice {c.Actor}.{c.Property} {c.Value}").Order(StringComparer.Ordinal))
        + string.Join('\n', state.TerrainHoles.Select(h => h.ToString()).Order(StringComparer.Ordinal))
        + string.Join('\n', state.NewSpawnPlaces.Select(p => p.NewName).Order(StringComparer.Ordinal))
        + string.Join('\n', new[] { "Barn_3", "SM_Rock_Added" }.Select(a => $"grass {a} {state.GetClearGrass(new ActorRef(Outpost, a))}"));
}
