using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using static ScumStudio.Tests.Level.EditOpTests;

namespace ScumStudio.Tests.Level;

public sealed class JournalSliceTests
{
    internal const string Village = "/Game/ConZ_Files/Maps/The_Island/A_0_Village";
    internal const string Farm = "/Game/ConZ_Files/Maps/The_Island/B_1_Farm";
    internal const string Quarry = "/Game/ConZ_Files/Maps/The_Island/C_2_Quarry";
    internal const string Water = "/Game/ConZ_Files/Maps/The_Island/WaterSplines";
    internal const string Lamp = "/Game/BP/BP_Lamp.BP_Lamp_C";
    internal static readonly TransformValue Moved = new(new FVector(5, 6, 7), FRotator.Zero, FVector.One);

    private static readonly SectorChoice OnlyA0 = Choose("A_0");

    [Fact]
    public void ASectorSliceKeepsOnlyThatSectorsEditsInJournalOrder()
    {
        var applied = Journal(
            new DeleteActorOp(new ActorRef(Outpost, "House_1")),
            new SetTransformOp(new ActorRef(Farm, "Barn_2"), TransformValue.Identity, Somewhere),
            new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Somewhere),
            new DeleteActorOp(new ActorRef(Water, "WaterSpline_3")),
            new SetAssetValueOp("/Game/ConZ_Files/Vehicles/Kinglet/BP_Kinglet", "Default__BP_Kinglet_C", "MaxSpeed", "Float", "100", "120"),
            new SetTransformOp(new ActorRef(Outpost, "House_2"), TransformValue.Identity, Elsewhere));

        var batch = JournalSlice.Take(applied, OnlyA0, "Outpost", out var pulledIn);

        Assert.Empty(pulledIn);
        Assert.Equal([applied[0].Op, applied[2].Op, applied[5].Op], batch.Ops);
        Assert.Equal("Outpost", batch.Title);
        Assert.Equal([Water], JournalSlice.Take(applied, new SectorChoice(new HashSet<string>(), IslandWide: true, Assets: false), "x", out _).GetTouchedLevels());
        Assert.IsType<SetAssetValueOp>(Assert.Single(JournalSlice.Take(applied, new SectorChoice(new HashSet<string>(), false, Assets: true), "x", out _).Ops));
    }

    [Fact]
    public void CreateThenRemoveInsideTheSliceIsLeftOut()
    {
        var rock = new ActorRef(Outpost, "SM_Rock_Added");
        var add = new AddStaticMeshActorOp(Outpost, "SM_Rock_Added", Rock, Somewhere);
        var tree = new InstanceRef(Outpost, "InstancedFoliageActor_0", "FoliageISM_0", 500);
        var addTree = new AddInstanceOp(tree, Somewhere);
        var again = new AddStaticMeshActorOp(Outpost, "SM_Rock_Added", Rock, Elsewhere);
        var applied = Journal(
            add,
            new SetTransformOp(rock, Somewhere, Moved),
            new SetClearGrassOp(rock, null, false),
            addTree,
            new SetInstanceTransformOp(tree, Somewhere, Moved),
            new DeleteAllOfKindOp(new KindMatch(MatchBy.StaticMesh, Rock), EditScope.ForLevel(Outpost), [rock, new ActorRef(Outpost, "Rock_9")], [tree]),
            new RemoveAddedActorOp(rock, add),
            new RemoveAddedInstanceOp(tree, addTree),
            new DeleteActorOp(new ActorRef(Outpost, "House_1")),
            again);

        // Before: the journal's edits as one batch create and remove the same rock, which a batch refuses.
        var raw = new BatchOp("raw", applied.Select(e => e.Op).ToList());
        Assert.Contains("listed twice", new EditState().Validate(raw));

        var batch = JournalSlice.Take(applied, OnlyA0, "Outpost", out _);

        Assert.Null(new EditState().Validate(batch));
        Assert.Equal(3, batch.Ops.Count);
        var bulk = Assert.IsType<DeleteAllOfKindOp>(batch.Ops[0]);
        Assert.Equal([new ActorRef(Outpost, "Rock_9")], bulk.Actors);
        Assert.Empty(bulk.Instances);
        Assert.Equal(new DeleteActorOp(new ActorRef(Outpost, "House_1")), batch.Ops[1]);
        Assert.Equal(again, batch.Ops[2]);
        Assert.Equal(3, Assert.Single(JournalSlice.Groups(applied)).Edits);
    }

    [Fact]
    public void ADeleteAllOfKindAcrossSectorsIsSplitPerSector()
    {
        var match = new KindMatch(MatchBy.StaticMesh, Rock);
        var applied = Journal(new DeleteAllOfKindOp(match, EditScope.Island,
            [new ActorRef(Outpost, "Rock_1"), new ActorRef(Farm, "Rock_2"), new ActorRef(Village, "Rock_3")],
            [new InstanceRef(Outpost, "InstancedFoliageActor_0", "FoliageISM_0", 3), new InstanceRef(Farm, "InstancedFoliageActor_0", "FoliageISM_1", 4)]));

        var a0 = JournalSlice.Take(applied, OnlyA0, "A", out _);
        var b1 = JournalSlice.Take(applied, Choose("B_1"), "B", out _);

        Assert.Equal(
            [
                new DeleteAllOfKindOp(match, EditScope.Island, [new ActorRef(Outpost, "Rock_1")], [new InstanceRef(Outpost, "InstancedFoliageActor_0", "FoliageISM_0", 3)]),
                new DeleteAllOfKindOp(match, EditScope.Island, [new ActorRef(Village, "Rock_3")], []),
            ],
            a0.Ops);
        Assert.Equal([new DeleteAllOfKindOp(match, EditScope.Island, [new ActorRef(Farm, "Rock_2")], [new InstanceRef(Farm, "InstancedFoliageActor_0", "FoliageISM_1", 4)])], b1.Ops);
        Assert.Equal([new SliceGroup("A_0", 2), new SliceGroup("B_1", 1)], JournalSlice.Groups(applied));
    }

    [Fact]
    public void ACopyOfAnObjectAddedInAnotherSectorPullsThatSectorIn()
    {
        var lampInFarm = new AddBlueprintActorOp(Farm, "BP_Lamp_Added", Lamp, new ActorRef(Farm, "BP_Lamp_C_3"), Somewhere);
        var copyInOutpost = new AddBlueprintActorOp(Outpost, "BP_Lamp_Added", Lamp, lampInFarm.Created, Elsewhere);
        var applied = Journal(
            lampInFarm,
            new SetTransformOp(new ActorRef(Quarry, "Crusher_1"), TransformValue.Identity, Somewhere),
            copyInOutpost,
            new DeleteActorOp(new ActorRef(Farm, "Barn_2")));

        var batch = JournalSlice.Take(applied, OnlyA0, "Lamps", out var pulledIn);

        Assert.Equal(["B_1"], pulledIn);
        Assert.Equal([lampInFarm, copyInOutpost, applied[3].Op], batch.Ops);
    }

    [Fact]
    public void GroupsCountEditsPerSectorIslandAndAssets()
    {
        var add = new AddStaticMeshActorOp(Farm, "SM_Rock_Added", Rock, Somewhere);
        var applied = Journal(
            new SetAssetValueOp("/Game/ConZ_Files/Vehicles/Kinglet/BP_Kinglet", "Default__BP_Kinglet_C", "MaxSpeed", "Float", "100", "120"),
            new DeleteActorOp(new ActorRef(Water, "WaterSpline_3")),
            new BatchOp("Plant 2 rocks", [new AddStaticMeshActorOp(Outpost, "SM_Rock_Added", Rock, Somewhere), new AddStaticMeshActorOp(Port, "SM_Rock_Added", Rock, Elsewhere)]),
            add,
            new RemoveAddedActorOp(add.Created, add),
            new DeleteActorOp(new ActorRef(Farm, "Barn_2")),
            new DeleteActorOp(new ActorRef(Village, "House_4")),
            new DeleteActorOp(new ActorRef(Outpost, "House_1")),
            new ReplaceAssetOp("/Game/ConZ_Files/Textures/T_Grass", null, "/Game/ConZ_Files/Textures/T_Snow"));

        Assert.Equal(
            [new SliceGroup("A_0", 4), new SliceGroup("B_1", 1), new SliceGroup(JournalSlice.IslandKey, 1), new SliceGroup(JournalSlice.AssetsKey, 2)],
            JournalSlice.Groups(applied));
    }

    [Theory]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/A_0_Outpost", "A_0")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/A_0_Outpost.A_0_Outpost", "A_0")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/D_4_Samobor_01_Block_02_Interior_01", "D_4")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/Landscape_D_3_4d", "D_3")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/TV_Base_Z_1_Research_Facility_Audio", "Z_1")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/B2_TV_Base_Research_Facility_Entrance", "B_2")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/AbandonedCity_PripyatLike/C_0_AbandonedCity_01a_PalaceOfEnergetik_Int", "C_0")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/A_0_Outpost_Airfield_BuiltData", "A_0")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/Landscape_A_0_1c_BuiltData", "A_0")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/HLOD/A_0_Outpost_Exterior_0_HLOD", "A_0")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/Safe_Zone_D_2", "D_2")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/B3_Castle_Ruins", "B_3")]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/The_Island", null)]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/WaterSplines", null)]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/Threat_Zones", null)]
    [InlineData("/Game/ConZ_Files/Maps/The_Island/The_Island_LevelStaticData", null)]
    public void SectorOfReadsRealLevelNames(string level, string? sector) => Assert.Equal(sector, JournalSlice.SectorOf(level));

    internal static SectorChoice Choose(params string[] sectors) => new(sectors.ToHashSet(StringComparer.OrdinalIgnoreCase), IslandWide: false, Assets: false);

    /// <summary>The applied entries of a journal that made <paramref name="ops"/> in order (each must apply).</summary>
    internal static IReadOnlyList<JournalEntry> Journal(params EditOp[] ops)
    {
        var state = new EditState();
        var at = new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        return ops.Select((op, i) =>
        {
            state.Apply(op);
            return new JournalEntry(i + 1, at.AddMinutes(i), op);
        }).ToList();
    }
}
