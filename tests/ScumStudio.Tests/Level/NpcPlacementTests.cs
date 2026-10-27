using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.Level;

/// <summary>
/// "Place NPCs" (the Spawns page's NPC wizard): a group of posts around a clicked point and an NPC area over them, in one
/// undo step, with no weight list shown to the user. The robot replacement shares the plan (<c>NpcGuardsTests</c>).
/// </summary>
public sealed class NpcPlacementTests
{
    private const string Zone = "/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_POI_Military_Base";
    private const string Copy = Zone + "_Own";

    private static readonly NpcGuardType Guards = new(
        "/Game/ConZ_Files/Encounters/EncounterClasses/Medium_Threat/MTZ_POI_Military_Base_NPC_Encounter.MTZ_POI_Military_Base_NPC_Encounter_C",
        ["BP_Guard_Lvl_3", "BP_Guard_Lvl_4"], Zone);

    private static readonly SpawnWeightList Rows = new(Zone, "MTZ_POI_Military_Base", SpawnCategory.Other, 0,
    [
        new SpawnWeight("MTZ_POI_Military_Base_Character_Encounter", "EncounterData[0].EncounterWeight", 65f),
        new SpawnWeight("MTZ_POI_Military_Base_NPC_Encounter", "EncounterData[1].EncounterWeight", 35f),
    ]);

    private static readonly CloneAssetOp ZoneCopy = new(ZoneCopies.Kind, Zone, Copy, [new PackagePair(Zone, Copy)]);

    [Fact]
    public void PostsStandOnACircleAroundThePickedPoint()
    {
        var center = new FVector(1000f, -2000f, 300f);

        Assert.Equal([center], NpcPlacement.PostsAround(center, 1));
        Assert.Equal([center], NpcPlacement.PostsAround(center, 5, 0f)); // no radius: one post

        var four = NpcPlacement.PostsAround(center, 4, 1000f);
        Assert.Equal(4, four.Count);
        Assert.All(four, p => Assert.True(MathF.Abs(FVector.Distance(p, center) - 1000f) < 0.5f && p.Z == center.Z, p.ToString()));
        Assert.True(FVector.Distance(four[0], new FVector(2000f, -2000f, 300f)) < 0.5f); // the first post is east of the centre
        Assert.True(FVector.Distance(four[2], new FVector(0f, -2000f, 300f)) < 0.5f);

        Assert.Equal(NpcPlacement.MaxPosts, NpcPlacement.PostsAround(center, 500).Count);
        Assert.Single(NpcPlacement.PostsAround(center, -3)); // at least one
    }

    [Fact]
    public void AClickedPointBecomesOneGroupAndAnAreaWithTheChosenNpcsOnlyInOneUndoStep()
    {
        var center = new FVector(-861946.6f, 503222.1f, 60305f);
        var posts = NpcPlacement.PostsAround(center, 3);

        var ops = NpcPlacement.Plan([new NpcPostGroup(posts)], Guards, ZoneCopy, Rows, []);

        // Nothing is removed (no robot), one group of three posts stands where the user clicked, the first post on its origin.
        Assert.Empty(ops.OfType<DeleteActorOp>());
        var group = Assert.Single(ops.OfType<AddSpawnPlaceOp>(), a => a.Kind == SpawnPlaceKind.CharacterGroup);
        Assert.Equal(posts[0], group.Transform.Location);
        Assert.Equal(3, group.Points!.Count);
        Assert.Equal(posts, group.PostsAt(group.Transform));

        // The area's settings are a copy where only the chosen NPCs keep a weight: the user never sees a weight.
        var weight = Assert.Single(ops.OfType<SetAssetValueOp>(), s => s.ValueKind == "Float");
        Assert.Equal(("EncounterData[0].EncounterWeight", "65", "0"), (weight.Path, weight.Old, weight.New));
        Assert.Contains(ops.OfType<SetAssetValueOp>(), s => s.Path == "SingleEncounterPerZone" && s.New == "true");
        var zone = Assert.Single(ops.OfType<AddSpawnPlaceOp>(), a => a.Kind == SpawnPlaceKind.Zone); // no stock zone given: a zone of its own
        Assert.Equal(Copy, zone.Setting);
        Assert.All(posts, p => Assert.NotEmpty(SpawnChoices.ZonesAt([SpawnPlaces.At(SpawnPlaceKind.Zone, zone.Setting, zone.Transform)], p)));

        // One step; its undo leaves nothing behind.
        var batch = new BatchOp("Place NPCs", ops);
        var state = EditState.Replay([batch]);
        Assert.Equal(2, SpawnPlaces.WithEdits([], state).Count);
        state.Apply(batch.Inverse());
        Assert.Empty(state.AddedActors);
        Assert.Empty(state.AssetClones);
    }

    [Fact]
    public void SeveralGroupsKeepTheirOwnNamesAndShareOneArea()
    {
        var a = NpcPlacement.PostsAround(new FVector(0f, 0f, 0f), 2);
        var b = NpcPlacement.PostsAround(new FVector(20000f, 0f, 0f), 2);

        var ops = NpcPlacement.Plan([new NpcPostGroup(a), new NpcPostGroup(b)], Guards, ZoneCopy, Rows, ["NewNpcGroup_0001"]);

        var groups = ops.OfType<AddSpawnPlaceOp>().Where(g => g.Kind == SpawnPlaceKind.CharacterGroup).ToList();
        Assert.Equal(["NewNpcGroup_0002", "NewNpcGroup_0003"], groups.Select(g => g.NewName));
        Assert.Single(ops.OfType<CloneAssetOp>()); // one copy of the settings, one area over both groups
    }

    [Fact]
    public void NoPostsOrANpcKindTheZoneHasNoRowForIsRefused()
    {
        Assert.Throws<ArgumentException>(() => NpcPlacement.Plan([], Guards, ZoneCopy, Rows, []));
        Assert.Throws<ArgumentException>(() => NpcPlacement.Plan([new NpcPostGroup([])], Guards, ZoneCopy, Rows, []));
        var other = Guards with { EncounterClass = "/Game/X/Y_NPC_Encounter.Y_NPC_Encounter_C" };
        Assert.Throws<ArgumentException>(() => NpcPlacement.Plan([new NpcPostGroup([FVector.Zero])], other, ZoneCopy, Rows, []));
    }
}
