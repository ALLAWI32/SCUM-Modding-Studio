using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Tests.Level;

/// <summary>
/// r39 (igor8802: "When replacing robots with NPCs, NPCs do not appear in the game"): the NPC area of a replaced robot is a
/// sub-zone of the place's own threat zone, as the game's NPC-only areas are. The edit that adds it: its journal record, its
/// undo, what it may name, and how the map's places and a page's part of the project show it. No game files.
/// </summary>
public sealed class SubZoneOpTests
{
    private const string Copy = "/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_POI_Military_Base_Own";
    private static readonly ActorRef Base = new(SpawnPlaces.StaticDataPath, SpawnPlaces.ActorName(SpawnPlaceKind.Zone, 222));
    private static readonly SubZone Area = new(new FVector(-864500f, 500700f, 57800f), new FVector(-859400f, 511300f, 62800f), Copy);

    [Fact]
    public void ItIsJournaledAndItsUndoTakesItAway()
    {
        var add = new AddSubZoneOp(Base, Area);
        Assert.Equal(new RemoveSubZoneOp(Base, Area), add.Inverse());
        Assert.Equal(add, add.Inverse().Inverse());
        Assert.Equal([SpawnPlaces.StaticDataPath], add.GetTouchedLevels());
        Assert.Equal(Base, add.GetPrimaryTarget());
        Assert.Contains("MTZ_POI_Military_Base_Own, priority 1", add.Describe(), StringComparison.Ordinal);
        Assert.Contains("\"op\":\"addSubZone\"", add.ToJson(), StringComparison.Ordinal);
        Assert.Equal(add, EditOp.FromJson(add.ToJson()));
        Assert.Equal(add.Inverse(), EditOp.FromJson(add.Inverse().ToJson()));

        var folder = Path.Combine(Path.GetTempPath(), "ss-subzone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "journal.jsonl");
            using (var journal = Journal.Open(path))
            {
                journal.Append(new BatchOp("Robot out", [add]));
                journal.Append(add.Inverse());
                journal.Undo();
            }

            using var back = Journal.Open(path);
            Assert.Equal(add, Assert.IsType<BatchOp>(Assert.Single(back.Applied).Op).Ops.Single());
            Assert.Equal(add.Inverse(), Assert.Single(back.RedoStack).Op);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        var state = EditState.Replay([add]);
        Assert.False(state.IsEmpty);
        Assert.Equal([(Base, Area)], state.SubZones);
        Assert.Equal([SpawnPlaces.StaticDataPath], state.ChangedLevels);
        Assert.Contains("sub-zone", state.Describe(), StringComparison.Ordinal);
        state.Apply(add.Inverse());
        Assert.True(state.IsEmpty);
        Assert.Empty(state.SubZones);
    }

    [Fact]
    public void OnlyAStockThreatZoneTakesOneAndNeverTwice()
    {
        var state = new EditState();
        Assert.NotNull(state.Validate(new AddSubZoneOp(new ActorRef(SpawnPlaces.StaticDataPath, "NewThreatZone_0001"), Area)));
        Assert.NotNull(state.Validate(new AddSubZoneOp(new ActorRef(SpawnPlaces.StaticDataPath, SpawnPlaces.ActorName(SpawnPlaceKind.Vehicle, 3)), Area)));
        Assert.NotNull(state.Validate(new AddSubZoneOp(Base with { Level = "/Game/ConZ_Files/Maps/The_Island/D_0_Military_Barracks" }, Area)));
        Assert.NotNull(state.Validate(new AddSubZoneOp(Base, Area with { Setting = " " })));
        Assert.NotNull(state.Validate(new AddSubZoneOp(Base, Area with { Min = Area.Max, Max = Area.Min })));
        Assert.NotNull(state.Validate(new AddSubZoneOp(Base, Area with { Max = new FVector(float.NaN, 0f, 0f) })));
        Assert.NotNull(state.Validate(new RemoveSubZoneOp(Base, Area)));

        state.Apply(new AddSubZoneOp(Base, Area));
        Assert.NotNull(state.Validate(new AddSubZoneOp(Base, Area)));
        Assert.Null(state.Validate(new AddSubZoneOp(Base, Area with { Priority = 0 })));
        Assert.NotNull(state.Validate(new RemoveSubZoneOp(Base, Area with { Priority = 0 })));

        // A removed zone takes no new sub-zone.
        var gone = EditState.Replay([new DeleteActorOp(Base)]);
        Assert.NotNull(gone.Validate(new AddSubZoneOp(Base, Area)));
    }

    [Fact]
    public void TheMapShowsItOnItsZoneAndOnlyTheSpawnsPartCarriesIt()
    {
        var zone = new SpawnPlace(SpawnPlaceKind.Zone, 222, new FTransform(FQuat.Identity, new FVector(-866634.6f, 526406.1f, 60856f), FVector.One), "MTZ_POI_Military_Base", 19694f, 31965f);
        var other = zone with { Index = 7 };
        var state = EditState.Replay([new AddSubZoneOp(Base, Area)]);

        var places = SpawnPlaces.WithEdits([zone, other], state);
        Assert.Equal([Area], places.Single(p => p.Index == 222).SubZones);
        Assert.Empty(places.Single(p => p.Index == 7).SubZones);

        // The Spawns page's part of the project has it; the Map's levels do not.
        Assert.Single(state.Where(SpawnPlaces.IsStaticData, _ => true, holes: false).SubZones);
        Assert.Empty(state.Where(l => !SpawnPlaces.IsStaticData(l), _ => true, holes: true).SubZones);

        // The export's request names the zone by its number; a removed zone drops it with a warning.
        Assert.Equal([(222, Area)], SpawnPlacesEditor.Plan(state).SubZones);
        var warnings = new List<string>();
        state.Apply(new DeleteActorOp(Base));
        Assert.Empty(SpawnPlacesEditor.Plan(state, warnings).SubZones);
        Assert.Contains(warnings, w => w.Contains("goes with the removed zone", StringComparison.Ordinal));
    }

    [Fact]
    public void ASharedProjectKeepsItAsItIs()
    {
        var add = new AddSubZoneOp(Base, Area);
        Assert.Contains(typeof(AddSubZoneOp), EditOpRemap.KnownTypes);
        Assert.Contains(typeof(RemoveSubZoneOp), EditOpRemap.KnownTypes);
        Assert.Equal(add, EditOpRemap.Rename(add, new Dictionary<ActorRef, ActorRef>(), new Dictionary<InstanceRef, InstanceRef>(), new Dictionary<string, string>()));
    }
}
