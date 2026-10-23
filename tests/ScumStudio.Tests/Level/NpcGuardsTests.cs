using System.Text.Json;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Serialization;
using ScumStudio.Level.Spawns;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Discord igor8802: "when creating patrols at points of interest, the robots can be replaced with NPCs". SCUM's armed NPCs
/// have no patrol in their AI, so a robot's route becomes guard posts (a group of zombie / NPC points) in a guard zone of
/// the chosen NPCs: the plan, its one undo step and the journal record. The real game files: <c>SentryGuardsRealTests</c>.
/// </summary>
public sealed class NpcGuardsTests
{
    private const string Barracks = "/Game/ConZ_Files/Maps/The_Island/D_0_Military_Barracks";
    private const string Zone = "/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_POI_Military_Base";
    private const string Copy = Zone + "_Own";

    private static readonly NpcGuardType Guards = new(
        "/Game/ConZ_Files/Encounters/EncounterClasses/Medium_Threat/MTZ_POI_Military_Base_NPC_Encounter.MTZ_POI_Military_Base_NPC_Encounter_C",
        ["BP_Guard_Lvl_3", "BP_Guard_Lvl_4", "BP_Guard_Lvl_5"], Zone);

    private static readonly SpawnWeightList Rows = new(Zone, "MTZ_POI_Military_Base", SpawnCategory.Other, 0,
    [
        new SpawnWeight("MTZ_POI_Military_Base_Character_Encounter", "EncounterData[0].EncounterWeight", 65f),
        new SpawnWeight("MTZ_POI_Military_Base_NPC_Encounter", "EncounterData[1].EncounterWeight", 35f),
    ]);

    private static readonly CloneAssetOp ZoneCopy = new(ZoneCopies.Kind, Zone, Copy, [new PackagePair(Zone, Copy)]);

    [Fact]
    public void PostsAreSpreadEvenlyAlongTheRouteWithBothEnds()
    {
        FVector[] path = [new(0f, 0f, 0f), new(1000f, 0f, 0f), new(1000f, 1000f, 0f)];

        Assert.Equal(path, NpcGuards.Posts(path, 3));
        var five = NpcGuards.Posts(path, 5);
        Assert.Equal(5, five.Count);
        Assert.True(FVector.Distance(five[1], new FVector(500f, 0f, 0f)) < 0.01f);
        Assert.True(FVector.Distance(five[3], new FVector(1000f, 500f, 0f)) < 0.01f);
        Assert.True(FVector.Distance(five[4], path[2]) < 0.01f);
        Assert.Equal([path[0]], NpcGuards.Posts(path, 1));
        Assert.Equal([path[0]], NpcGuards.Posts([path[0], path[0]], 4)); // a robot that never moves: one post
        Assert.Equal(SpawnPlaces.MaxGroupPosts, NpcGuards.Posts(path, 500).Count);
    }

    [Fact]
    public void ARobotBecomesGuardPostsInAGuardZoneOfItsOwnInOneUndoStep()
    {
        var robot = new ActorRef(Barracks, "SentrySpawner_0");
        var other = new ActorRef(Barracks, "SentrySpawner_3");
        SentryRoute[] routes =
        [
            new(robot, [new FVector(-861946.6f, 503222.1f, 60305f), new FVector(-861985.8f, 508791.6f, 60305f), new FVector(-862012.7f, 503866.8f, 60305f)]),
            new(other, [new FVector(-869275.6f, 508496.1f, 60339.5f), new FVector(-869314.8f, 512120.6f, 60339.5f)]),
        ];

        var ops = NpcGuards.Plan(routes, Guards, 3, ZoneCopy, Rows, ["NewNpcGroup_0001"]);

        // Each robot's spawner goes, a group of 3 posts stands on its route (the first post is where the group stands).
        Assert.Equal([robot, other], ops.OfType<DeleteActorOp>().Select(d => d.Target));
        var groups = ops.OfType<AddSpawnPlaceOp>().Where(a => a.Kind == SpawnPlaceKind.CharacterGroup).ToList();
        Assert.Equal(["NewNpcGroup_0002", "NewNpcGroup_0003"], groups.Select(g => g.NewName));
        Assert.All(groups, g => Assert.Equal((NpcGuards.PostType, 3), (g.Setting, g.Points!.Count)));
        Assert.Equal(NpcGuards.Posts(routes[0].Path, 3), groups[0].PostsAt(groups[0].Transform));

        // The guard zone's own settings: the zombie encounter weighs 0, the NPC one keeps its weight, one encounter at a time
        // (as the game's NPC-only settings; the stock settings do not store it); the zone covers every post. No stock zone
        // was given, so it is a zone of its own (the fallback).
        Assert.Same(ZoneCopy, Assert.Single(ops.OfType<CloneAssetOp>()));
        var weight = Assert.Single(ops.OfType<SetAssetValueOp>(), s => s.ValueKind == "Float");
        Assert.Equal((Copy, "MTZ_POI_Military_Base_Own", "EncounterData[0].EncounterWeight", "Float", "65", "0"), (weight.Package, weight.Export, weight.Path, weight.ValueKind, weight.Old, weight.New));
        var single = Assert.Single(ops.OfType<SetAssetValueOp>(), s => s.ValueKind == "Bool");
        Assert.Equal((Copy, "MTZ_POI_Military_Base_Own", "SingleEncounterPerZone", string.Empty, "true"), (single.Package, single.Export, single.Path, single.Old, single.New));
        Assert.Empty(ops.OfType<AddSubZoneOp>());
        var zone = Assert.Single(ops.OfType<AddSpawnPlaceOp>(), a => a.Kind == SpawnPlaceKind.Zone);
        Assert.Equal(Copy, zone.Setting);
        var ellipse = SpawnPlaces.At(SpawnPlaceKind.Zone, zone.Setting, zone.Transform);
        Assert.All(routes.SelectMany(r => r.Path), p => Assert.NotEmpty(SpawnChoices.ZonesAt([ellipse], p))); // the zone is an ellipse: the corners too

        // One step in the journal's state; its undo leaves nothing behind.
        var batch = new BatchOp("Replace", ops);
        var state = EditState.Replay([batch]);
        Assert.True(state.IsDeleted(robot) && state.IsDeleted(other));
        var places = SpawnPlaces.WithEdits([], state);
        Assert.Equal(2, places.Count(p => p.Kind == SpawnPlaceKind.CharacterGroup));
        var last = places.Last(p => p.Kind == SpawnPlaceKind.CharacterGroup);
        Assert.True(FVector.Distance(last.Points[^1], routes[1].Path[^1]) < 0.01f);
        Assert.Equal("MTZ_POI_Military_Base_Own", places.Single(p => p.Kind == SpawnPlaceKind.Zone).Group);
        state.Apply(batch.Inverse());
        Assert.False(state.IsDeleted(robot));
        Assert.Empty(state.AddedActors);
        Assert.Empty(state.AssetClones);
    }

    /// <summary>
    /// r39 (igor8802: "NPCs do not appear in the game"): where a stock threat zone holds every post, the guard area is a
    /// sub-zone of the smallest one, priority 1, as the game's own NPC-only areas (<c>HTZ_Military_TV_Bunker_NPC</c>), not a
    /// second zone laid inside the place's own zone (which no zone of the island does). A project's added zone, a removed one
    /// and one that misses a post do not take it.
    /// </summary>
    [Fact]
    public void TheGuardAreaIsASubZoneOfTheSmallestStockZoneThatHoldsEveryPost()
    {
        var robot = new ActorRef(Barracks, "SentrySpawner_0");
        SentryRoute[] routes = [new(robot, [new FVector(-861946.6f, 503222.1f, 60305f), new FVector(-861985.8f, 508791.6f, 60305f)])];
        SpawnPlace Zone(int index, float x, float y, float semiX, float semiY) =>
            new(SpawnPlaceKind.Zone, index, new FTransform(FQuat.Identity, new FVector(x, y, 60856f), FVector.One), "MTZ_POI_Military_Base", semiX, semiY);
        var barracks = Zone(222, -866634.6f, 526406.1f, 19694f, 31965f);
        var island = Zone(5, -866634.6f, 526406.1f, 90000f, 90000f); // holds the posts too, but is larger
        var beside = Zone(9, -861946.6f, 503222.1f, 1000f, 1000f); // holds only the first post
        var added = Zone(-1, -866634.6f, 526406.1f, 9000f, 30000f) with { Name = "NewThreatZone_0001" };
        var stored = new Dictionary<string, string> { ["SingleEncounterPerZone"] = "false" };

        var ops = NpcGuards.Plan(routes, Guards, 3, ZoneCopy, Rows, [], [island, beside, added, barracks], stored);

        Assert.DoesNotContain(ops.OfType<AddSpawnPlaceOp>(), a => a.Kind == SpawnPlaceKind.Zone);
        var sub = Assert.Single(ops.OfType<AddSubZoneOp>());
        Assert.Equal(new ActorRef(SpawnPlaces.StaticDataPath, "ThreatZone_00222"), sub.Zone);
        Assert.Equal((Copy, NpcGuards.SubZonePriority), (sub.SubZone.Setting, sub.SubZone.Priority));
        Assert.All(NpcGuards.Posts(routes[0].Path, 3), p => Assert.True(sub.SubZone.Contains(p), $"{p} not in {sub.SubZone}"));
        // The posts' box and the margin (the route is 55.7 m long and straight): at least the smallest size across it.
        Assert.True(MathF.Abs(sub.SubZone.Max.Y - sub.SubZone.Min.Y - (5569.5f + (2f * NpcGuards.ZoneMargin))) < 1f, sub.SubZone.ToString());
        Assert.True(MathF.Abs(sub.SubZone.Max.X - sub.SubZone.Min.X - (2f * NpcGuards.MinZoneHalfSize)) < 1f, sub.SubZone.ToString());
        Assert.True(MathF.Abs(sub.SubZone.Max.Z - sub.SubZone.Min.Z - (2f * NpcGuards.ZoneMargin)) < 1f, sub.SubZone.ToString());
        var single = Assert.Single(ops.OfType<SetAssetValueOp>(), s => s.Path == "SingleEncounterPerZone");
        Assert.Equal(("false", "true"), (single.Old, single.New)); // a stored value is changed, not added

        // One step; its undo leaves no sub-zone behind.
        var batch = new BatchOp("Robot out, NPC area in", ops);
        var state = EditState.Replay([batch]);
        Assert.Equal([(sub.Zone, sub.SubZone)], state.SubZones);
        Assert.Equal([sub.SubZone], SpawnPlaces.WithEdits([barracks], state).Single(p => p.Kind == SpawnPlaceKind.Zone).SubZones);
        state.Apply(batch.Inverse());
        Assert.Empty(state.SubZones);
        Assert.True(state.IsEmpty);

        // The place's zone removed by the project: the next smallest that holds every post takes it.
        var without = NpcGuards.Plan(routes, Guards, 3, ZoneCopy, Rows, [], SpawnPlaces.WithEdits([island, barracks], EditState.Replay([new DeleteActorOp(new ActorRef(SpawnPlaces.StaticDataPath, "ThreatZone_00222"))])));
        Assert.Equal("ThreatZone_00005", Assert.Single(without.OfType<AddSubZoneOp>()).Zone.Actor);
    }

    [Fact]
    public void AGroupMovesWithItsPostsAndKeepsThemInTheJournal()
    {
        var add = new AddSpawnPlaceOp("NewNpcGroup_0001", SpawnPlaceKind.CharacterGroup, "Exterior", new TransformValue(new FVector(100f, 0f, 0f), FRotator.Zero, FVector.One),
            Points: [FVector.Zero, new FVector(500f, 0f, 0f)]);

        // Turned 90 degrees and moved: the second post swings round the first.
        var posts = add.PostsAt(new TransformValue(new FVector(0f, 0f, 10f), new FRotator(0f, 90f, 0f), FVector.One));
        Assert.True(FVector.Distance(posts[0], new FVector(0f, 0f, 10f)) < 0.01f);
        Assert.True(FVector.Distance(posts[1], new FVector(0f, 500f, 10f)) < 0.01f);

        var json = JsonSerializer.Serialize<EditOp>(add, LevelJson.Compact);
        Assert.Contains("\"points\"", json, StringComparison.Ordinal);
        Assert.Contains("\"characterGroup\"", json, StringComparison.Ordinal);
        Assert.Equal(add, JsonSerializer.Deserialize<EditOp>(json, LevelJson.Compact));
    }

    [Fact]
    public void AGroupNeedsPostsAndOnlyAGroupHasThem()
    {
        var state = new EditState();
        Assert.NotNull(state.Validate(new AddSpawnPlaceOp("NewNpcGroup_0001", SpawnPlaceKind.CharacterGroup, "Exterior", TransformValue.Identity)));
        Assert.NotNull(state.Validate(new AddSpawnPlaceOp("NewZombieSpawn_0001", SpawnPlaceKind.Character, "Exterior", TransformValue.Identity, Points: [FVector.Zero])));
        Assert.NotNull(state.Validate(new AddSpawnPlaceOp("NewNpcGroup_0001", SpawnPlaceKind.CharacterGroup, "Exterior", TransformValue.Identity,
            Points: [.. Enumerable.Repeat(FVector.Zero, SpawnPlaces.MaxGroupPosts + 1)])));
        Assert.Null(state.Validate(new AddSpawnPlaceOp("NewNpcGroup_0001", SpawnPlaceKind.CharacterGroup, "Exterior", TransformValue.Identity, Points: [FVector.Zero])));
        Assert.Equal(("CharacterGroupPlace", "EncounterGroupedCharacterSpawnPoints"), (SpawnPlaces.ClassOf(SpawnPlaceKind.CharacterGroup), SpawnPlaces.ArrayOf(SpawnPlaceKind.CharacterGroup)));
        Assert.Equal((SpawnPlaceKind.CharacterGroup, 12), SpawnPlaces.ParseName("NpcGroup_00012"));
    }
}
