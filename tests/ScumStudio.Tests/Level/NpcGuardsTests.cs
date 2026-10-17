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

        // The guard zone's own settings: the zombie encounter weighs 0, the NPC one keeps its weight; the zone covers every post.
        Assert.Same(ZoneCopy, Assert.Single(ops.OfType<CloneAssetOp>()));
        var weight = Assert.Single(ops.OfType<SetAssetValueOp>());
        Assert.Equal((Copy, "MTZ_POI_Military_Base_Own", "EncounterData[0].EncounterWeight", "Float", "65", "0"), (weight.Package, weight.Export, weight.Path, weight.ValueKind, weight.Old, weight.New));
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
