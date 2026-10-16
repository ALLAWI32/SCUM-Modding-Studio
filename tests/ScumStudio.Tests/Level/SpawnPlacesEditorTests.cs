using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The island's spawn places written into <c>The_Island_LevelStaticData</c> on a synthetic copy of its layout
/// (<see cref="SyntheticStaticData"/>): new places that are not clones (owner: "create a NEW spawn, not a clone"), a zone's
/// own settings asset, and the audit's write bugs F099, F107, F281 and F439.
/// </summary>
public sealed class SpawnPlacesEditorTests
{
    private const string Mine = "/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_Village_Mine";

    private static readonly ActorRef Zone0 = new(SpawnPlaces.StaticDataPath, SpawnPlaces.ActorName(SpawnPlaceKind.Zone, 0));

    /// <summary>The packages of the synthetic game files, and the project's copy of the village settings.</summary>
    private static bool Exists(string package) => package is SyntheticStaticData.Village or SyntheticStaticData.Biome or Mine;

    private static (CookedPackage After, SpawnPlacesReport Report, List<string> Warnings) Export(CookedPackage package, EditState state)
    {
        var warnings = new List<string>();
        var (bytes, report) = SpawnPlacesEditor.Apply(package, SpawnPlacesEditor.Plan(state, warnings), Exists);
        warnings.AddRange(report.Warnings);
        return (SyntheticStaticData.Parse(bytes), report, warnings);
    }

    private static EditState With(params EditOp[] ops) => EditState.Replay(ops);

    private static TransformValue At(float x, float y, float z, float yaw = 0f, float sx = 1f, float sy = 1f) => new(new FVector(x, y, z), new FRotator(0f, yaw, 0f), new FVector(sx, sy, 1f));

    [Fact]
    public void NewVehiclePlaceGetsItsOwnGroupNotTheTemplates()
    {
        var package = SyntheticStaticData.Build();
        var where = At(123f, -456f, 78f, yaw: 30f);

        var (after, report, warnings) = Export(package, With(new AddSpawnPlaceOp("NewVehicleSpawn_0001", SpawnPlaceKind.Vehicle, "City", where)));

        Assert.Empty(warnings);
        Assert.Equal(1, report.Created);
        var cars = SpawnPlaces.Read(after).Where(p => p.Kind == SpawnPlaceKind.Vehicle).ToList();
        Assert.Equal(4, cars.Count);
        Assert.Equal("City", cars[3].Group); // the layout comes from point 1 (group Default): the group is the new place's own
        Assert.Equal(where.Location, cars[3].Transform.Translation);
        Assert.Equal(30f, cars[3].Transform.Rotation.Rotator().Yaw, 0.01f);
        var before = SyntheticStaticData.Items(package, SpawnPlaceKind.Vehicle);
        var written = SyntheticStaticData.Items(after, SpawnPlaceKind.Vehicle);
        Assert.All(Enumerable.Range(0, 3), i => Assert.Equal(before[i], written[i]));
        Assert.Equal(SyntheticStaticData.Items(package, SpawnPlaceKind.Zone), SyntheticStaticData.Items(after, SpawnPlaceKind.Zone));
    }

    [Fact]
    public void NewZoneWritesSettingsSizeAndBoxes()
    {
        var package = SyntheticStaticData.Build();
        var where = At(5000f, -6000f, 100f, yaw: 30f, sx: 30f, sy: 10f); // 3000 x 1000 cm half axes

        var (after, report, warnings) = Export(package, With(new AddSpawnPlaceOp("NewThreatZone_0001", SpawnPlaceKind.Zone, "MTZ_Village", where)));

        Assert.Empty(warnings);
        var zone = SpawnPlaces.Read(after).Where(p => p.Kind == SpawnPlaceKind.Zone).Last();
        Assert.Equal((2, "MTZ_Village", 3000f, 1000f), (SpawnPlaces.Read(after).Count(p => p.Kind == SpawnPlaceKind.Zone) - 1, zone.Group, zone.SizeX, zone.SizeY));
        Assert.Equal(where.Location, zone.Transform.Translation);
        var item = SyntheticStaticData.Item(after, SpawnPlaceKind.Zone, 2);
        var yaw = 30f * MathF.PI / 180f;
        foreach (var (name, k) in new[] { ("_boundingBox", 1f), ("_zoneActivationBoundingBox", 1.5f), ("_LPCZoneActivationBoundingBox", 2f) })
        {
            var (a, b) = (3000f * k, 1000f * k);
            var ex = MathF.Sqrt(MathF.Pow(a * MathF.Cos(yaw), 2) + MathF.Pow(b * MathF.Sin(yaw), 2));
            var ey = MathF.Sqrt(MathF.Pow(a * MathF.Sin(yaw), 2) + MathF.Pow(b * MathF.Cos(yaw), 2));
            var box = Assert.IsType<BoxValue>(item.Properties.Single(p => p.Name == name).Value);
            Assert.Equal(5000f - ex, box.Min.X, 0.5f);
            Assert.Equal(-6000f + ey, box.Max.Y, 0.5f);
            Assert.Equal(100f, box.Min.Z, 0.01f);
        }
    }

    [Fact]
    public void NewCharacterPlaceGetsItsType()
    {
        var package = SyntheticStaticData.Build();

        var (after, _, warnings) = Export(package, With(
            new AddSpawnPlaceOp("NewZombieSpawn_0001", SpawnPlaceKind.Character, "Interior", At(1f, 2f, 3f)),
            new AddSpawnPlaceOp("NewTaggedSpawn_0001", SpawnPlaceKind.TaggedCharacter, "Interior", At(4f, 5f, 6f))));

        Assert.Empty(warnings);
        var read = SpawnPlaces.Read(after);
        Assert.Equal(["Exterior", "Interior", "Interior"], read.Where(p => p.Kind == SpawnPlaceKind.Character).Select(p => p.Group)); // laid out as point 0 (Exterior)
        Assert.Equal(["Exterior", "Interior"], read.Where(p => p.Kind == SpawnPlaceKind.TaggedCharacter).Select(p => p.Group));
        Assert.Equal(new FVector(4f, 5f, 6f), read.Last(p => p.Kind == SpawnPlaceKind.TaggedCharacter).Transform.Translation);
    }

    [Fact]
    public void ChooseAddsAMissingZoneSettingsImport()
    {
        var package = SyntheticStaticData.Build();
        const string mine = Mine;

        var (after, report, warnings) = Export(package, With(new SetSpawnChoiceOp(Zone0, SpawnChoices.ZoneData, null, mine)));

        Assert.Empty(warnings);
        Assert.Equal(1, report.Chosen);
        Assert.Equal(["MTZ_Village_Mine", "MTZ_Village"], SpawnPlaces.Read(after).Where(p => p.Kind == SpawnPlaceKind.Zone).Select(p => p.Group));
        Assert.Equal(package.Imports.Count + 2, after.Imports.Count); // its package and the object in it
        var settings = after.Imports.Count - 1;
        Assert.Equal(("EncounterZoneData", "/Script/ConZ"), (after.ResolveName(after.Imports[settings].ClassName), after.ResolveName(after.Imports[settings].ClassPackage)));
        Assert.Equal(mine, after.ResolveName(after.Imports[-after.Imports[settings].OuterIndex - 1].ObjectName));
        var export = after.Exports[0];
        var preload = after.ReadPreloadDependencies();
        Assert.Contains(-(settings + 1), preload.Skip(export.FirstExportDependency + export.SerializationBeforeSerializationDependencies).Take(export.CreateBeforeSerializationDependencies));
        Assert.Equal(SyntheticStaticData.Items(package, SpawnPlaceKind.Zone)[1], SyntheticStaticData.Items(after, SpawnPlaceKind.Zone)[1]);

        // A bare name the island data does not use cannot be found: refused, not guessed.
        var (_, _, refused) = Export(package, With(new SetSpawnChoiceOp(Zone0, SpawnChoices.ZoneData, null, "MTZ_Nowhere")));
        Assert.Contains(refused, w => w.Contains("MTZ_Nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public void GroupNameNotInTheNameTableIsRefusedWithAWarning()
    {
        var package = SyntheticStaticData.Build();

        var (after, report, warnings) = Export(package, With(new AddSpawnPlaceOp("NewVehicleSpawn_0001", SpawnPlaceKind.Vehicle, "Spaceship", At(1f, 2f, 3f))));

        Assert.Contains(warnings, w => w.Contains("NewVehicleSpawn_0001", StringComparison.Ordinal) && w.Contains("Spaceship", StringComparison.Ordinal));
        Assert.Equal(0, report.Created);
        Assert.Equal(3, SpawnPlaces.Read(after).Count(p => p.Kind == SpawnPlaceKind.Vehicle));
        Assert.Equal(package.Names.Count, after.Names.Count); // no new FName

        // A group the island data names (no point uses it) is fine.
        var (boat, _, none) = Export(package, With(new AddSpawnPlaceOp("NewVehicleSpawn_0001", SpawnPlaceKind.Vehicle, "Boat", At(1f, 2f, 3f))));
        Assert.Empty(none);
        Assert.Equal("Boat", SpawnPlaces.Read(boat).Last(p => p.Kind == SpawnPlaceKind.Vehicle).Group);
    }

    [Fact]
    public void PlaceWithoutStoredRotationWarnsInsteadOfDroppingTheTurn()
    {
        var package = SyntheticStaticData.Build();
        var request = new SpawnPlacesEditRequest
        {
            Moved = new Dictionary<(SpawnPlaceKind, int), TransformValue>
            {
                [(SpawnPlaceKind.Vehicle, 0)] = At(1f, 2f, 3f, yaw: 90f), // point 0 stores no rotation
                [(SpawnPlaceKind.Vehicle, 1)] = At(4f, 5f, 6f, yaw: 90f) with { Scale = new FVector(2f, 2f, 2f) },
            },
        };

        var (bytes, report) = SpawnPlacesEditor.Apply(package, request);

        var warning = Assert.Single(report.Warnings);
        Assert.Contains("VehicleSpawn_00000", warning, StringComparison.Ordinal);
        Assert.Contains("rotation", warning, StringComparison.Ordinal);
        Assert.Equal(1, report.Moved);
        var after = SyntheticStaticData.Parse(bytes);
        Assert.Equal(SyntheticStaticData.Items(package, SpawnPlaceKind.Vehicle)[0], SyntheticStaticData.Items(after, SpawnPlaceKind.Vehicle)[0]); // not half moved
        var moved = SpawnPlaces.Read(after).Single(p => p.Kind == SpawnPlaceKind.Vehicle && p.Index == 1);
        Assert.Equal((new FVector(4f, 5f, 6f), new FVector(2f, 2f, 2f)), (moved.Transform.Translation, moved.Transform.Scale3D)); // F439: a point's scale is written

        // Moved without a turn, the same point is written.
        var (straight, plain) = SpawnPlacesEditor.Apply(package, new SpawnPlacesEditRequest { Moved = new Dictionary<(SpawnPlaceKind, int), TransformValue> { [(SpawnPlaceKind.Vehicle, 0)] = At(7f, 8f, 9f) } });
        Assert.Empty(plain.Warnings);
        Assert.Equal(new FVector(7f, 8f, 9f), SpawnPlaces.Read(SyntheticStaticData.Parse(straight)).First(p => p.Kind == SpawnPlaceKind.Vehicle).Transform.Translation);
    }

    [Fact]
    public void AZoneOnlyTurnsAboutTheVertical()
    {
        var package = SyntheticStaticData.Build();
        var tilted = new TransformValue(new FVector(1f, 2f, 3f), new FRotator(10f, 20f, 0f), new FVector(40f, 25f, 1f));

        var (_, report) = SpawnPlacesEditor.Apply(package, new SpawnPlacesEditRequest { Moved = new Dictionary<(SpawnPlaceKind, int), TransformValue> { [(SpawnPlaceKind.Zone, 0)] = tilted } });

        Assert.Contains(report.Warnings, w => w.Contains("ThreatZone_00000", StringComparison.Ordinal) && w.Contains("pitch", StringComparison.Ordinal));
        Assert.Equal(1, report.Moved);
    }

    [Fact]
    public void IndexPastTheEndWarns()
    {
        var package = SyntheticStaticData.Build();
        var request = new SpawnPlacesEditRequest
        {
            Deleted = [(SpawnPlaceKind.Character, 9)],
            Moved = new Dictionary<(SpawnPlaceKind, int), TransformValue> { [(SpawnPlaceKind.Vehicle, 7)] = At(1f, 2f, 3f) },
        };

        var (bytes, report) = SpawnPlacesEditor.Apply(package, request);

        Assert.Contains(report.Warnings, w => w.Contains("ZombieSpawn_00009", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("VehicleSpawn_00007", StringComparison.Ordinal));
        Assert.Equal((0, 0), (report.Deleted, report.Moved));
        Assert.Equal(SpawnPlaces.Read(package).Count, SpawnPlaces.Read(SyntheticStaticData.Parse(bytes)).Count);
    }

    [Fact]
    public void AServerCookWithOtherCountsIsReported()
    {
        var client = SpawnPlacesEditor.Counts(SyntheticStaticData.Build());
        Assert.Equal(3, client[SpawnPlaceKind.Vehicle]);
        Assert.Null(SpawnPlacesEditor.CountMismatch(client, client));
        var server = new Dictionary<SpawnPlaceKind, int>(client) { [SpawnPlaceKind.Vehicle] = 4 };
        Assert.Contains("VehicleSpawnPoints", SpawnPlacesEditor.CountMismatch(client, server), StringComparison.Ordinal);
    }

    [Fact]
    public void AServerCookInAnotherOrderGetsNoneOfTheNumberedEdits()
    {
        var client = SyntheticStaticData.Build();
        var created = new NewSpawnPlace("NewVehicleSpawn_0001", SpawnPlaceKind.Vehicle, "City", At(1f, 2f, 3f));
        var request = new SpawnPlacesEditRequest
        {
            Deleted = [(SpawnPlaceKind.Vehicle, 1)],
            Moved = new Dictionary<(SpawnPlaceKind, int), TransformValue> { [(SpawnPlaceKind.Zone, 1)] = At(5f, 6f, 7f, sx: 10f, sy: 10f) },
            Created = [created],
        };
        var reference = SpawnPlacesEditor.Reference(client, request);
        Assert.Equal(2, reference.Places.Count);

        // The same lists: the request as it is.
        var warnings = new List<string>();
        Assert.Same(request, SpawnPlacesEditor.ForOtherCook(request, reference, client, warnings));
        Assert.Empty(warnings);

        // The same lengths, but another place at number 1 (audit F281: the lengths alone let this through).
        var (other, _) = SpawnPlacesEditor.Apply(client, new SpawnPlacesEditRequest { Moved = new Dictionary<(SpawnPlaceKind, int), TransformValue> { [(SpawnPlaceKind.Vehicle, 1)] = At(-9000f, 9000f, 0f) } });
        var server = SyntheticStaticData.Parse(other);
        Assert.Null(SpawnPlacesEditor.CountMismatch(reference.Counts, SpawnPlacesEditor.Counts(server)));
        var checkedRequest = SpawnPlacesEditor.ForOtherCook(request, reference, server, warnings);
        Assert.Equal((0, 0, 1), (checkedRequest.Deleted.Count, checkedRequest.Moved.Count, checkedRequest.Created.Count));
        Assert.Contains("VehicleSpawn_00001", Assert.Single(warnings), StringComparison.Ordinal);

        // Other lengths, and no client read at all: only the new places.
        var (longer, _) = SpawnPlacesEditor.Apply(client, new SpawnPlacesEditRequest { Added = [(SpawnPlaceKind.Vehicle, 2, At(1f, 1f, 1f))] });
        warnings.Clear();
        Assert.Equal([created], SpawnPlacesEditor.ForOtherCook(request, reference, SyntheticStaticData.Parse(longer), warnings).Created);
        Assert.Contains("VehicleSpawnPoints 3 / 4", Assert.Single(warnings), StringComparison.Ordinal);
        warnings.Clear();
        Assert.True(SpawnPlacesEditor.ForOtherCook(request, null, client, warnings) is { Deleted.Count: 0, Moved.Count: 0, Created.Count: 1 });
        Assert.Contains("not read in this export", Assert.Single(warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void ASettingsPackageThatDoesNotExistIsNotImported()
    {
        var package = SyntheticStaticData.Build();
        const string typo = "/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_Vilage_Mine";

        var (after, report, warnings) = Export(package, With(
            new SetSpawnChoiceOp(Zone0, SpawnChoices.ZoneData, null, typo),
            new AddSpawnPlaceOp("NewThreatZone_0001", SpawnPlaceKind.Zone, typo, At(1f, 2f, 3f, sx: 10f, sy: 10f))));

        Assert.Equal((0, 0), (report.Chosen, report.Created));
        Assert.Equal(2, warnings.Count(w => w.Contains(typo, StringComparison.Ordinal)));
        Assert.Equal(package.Imports.Count, after.Imports.Count);
        Assert.Equal(SyntheticStaticData.Items(package, SpawnPlaceKind.Zone), SyntheticStaticData.Items(after, SpawnPlaceKind.Zone));

        // Not asked whether it exists, the export adds no import either.
        var (_, blind) = SpawnPlacesEditor.Apply(package, new SpawnPlacesEditRequest { Choices = new Dictionary<(SpawnPlaceKind, int), string> { [(SpawnPlaceKind.Zone, 0)] = Mine } });
        Assert.Equal(0, blind.Chosen);
        Assert.Contains("not checked", Assert.Single(blind.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void NewTaggedPointGetsItsOwnTagsNotTheTemplates()
    {
        var package = SyntheticStaticData.Build();

        var (after, report, warnings) = Export(package, With(
            new AddSpawnPlaceOp("NewTaggedSpawn_0001", SpawnPlaceKind.TaggedCharacter, "Interior", At(1f, 2f, 3f), ["Encounter.Police"]),
            new AddSpawnPlaceOp("NewTaggedSpawn_0002", SpawnPlaceKind.TaggedCharacter, "Exterior", At(4f, 5f, 6f))));

        Assert.Empty(warnings);
        Assert.Equal(2, report.Created);
        string[] TagsOf(int index) => ((ArrayValue)SyntheticStaticData.Item(after, SpawnPlaceKind.TaggedCharacter, index).Properties.Single(p => p.Name == "Tags").Value).Items.Select(t => ((NameValue)t).Value).ToArray();
        Assert.Equal(["Encounter.Bunker"], TagsOf(0));
        Assert.Equal(["Encounter.Police"], TagsOf(1));
        Assert.Empty(TagsOf(2)); // none of the template's
        Assert.Equal(SyntheticStaticData.Items(package, SpawnPlaceKind.TaggedCharacter)[0], SyntheticStaticData.Items(after, SpawnPlaceKind.TaggedCharacter)[0]);
        Assert.Equal(new FVector(4f, 5f, 6f), SpawnPlaces.Read(after).Last(p => p.Kind == SpawnPlaceKind.TaggedCharacter).Transform.Translation);

        // A tag the island data does not name is refused (no new FName).
        var (none, refused, why) = Export(package, With(new AddSpawnPlaceOp("NewTaggedSpawn_0001", SpawnPlaceKind.TaggedCharacter, "Interior", At(1f, 2f, 3f), ["Encounter.Moon"])));
        Assert.Equal(0, refused.Created);
        Assert.Contains(why, w => w.Contains("Encounter.Moon", StringComparison.Ordinal));
        Assert.Equal(package.Names.Count, none.Names.Count);
    }

    [Fact]
    public void NewZoneLeavesItsTemplatesSubZonesAndApproachPointsBehind()
    {
        var package = SyntheticStaticData.Build();

        var (after, report, warnings) = Export(package, With(
            new AddSpawnPlaceOp("NewThreatZone_0001", SpawnPlaceKind.Zone, "MTZ_Village", At(1000f, 2000f, 30f, sx: 20f, sy: 10f)),
            new AddSpawnPlaceOp("NewThreatZone_0002", SpawnPlaceKind.Zone, Mine, At(3000f, 4000f, 50f, sx: 20f, sy: 10f))));

        Assert.Empty(warnings);
        Assert.Equal(2, report.Created);
        Assert.Equal(SyntheticStaticData.Items(package, SpawnPlaceKind.Zone), SyntheticStaticData.Items(after, SpawnPlaceKind.Zone).Take(2));
        foreach (var index in new[] { 2, 3 })
        {
            var zone = SyntheticStaticData.Item(after, SpawnPlaceKind.Zone, index);
            Assert.DoesNotContain(zone.Properties, p => p.Name is "_subZoneData" or "_flyingSpawnedCharacterApproachLocations");
            Assert.Equal(1.5f, ((FloatValue)zone.Properties.Single(p => p.Name == "_zoneActivationRadiusMultiplier").Value).Value); // as its template
        }

        // Low-population settings: a zone of stock settings keeps those of the zone it is laid out from; the project's own
        // settings (no stock zone uses them) serve at a low player count too.
        string Lpc(int index) => ((ObjectValue)SyntheticStaticData.Item(after, SpawnPlaceKind.Zone, index).Properties.Single(p => p.Name == "_lpcZoneData").Value).Reference;
        Assert.EndsWith("MTZ_Village", Lpc(2), StringComparison.Ordinal);
        Assert.EndsWith("MTZ_Village_Mine", Lpc(3), StringComparison.Ordinal);
        Assert.Equal(["MTZ_Village", "MTZ_Village", "MTZ_Village", "MTZ_Village_Mine"], SpawnPlaces.Read(after).Where(p => p.Kind == SpawnPlaceKind.Zone).Select(p => p.Group));
    }

    [Fact]
    public void ATemplateWithAMemberThisVersionDoesNotKnowIsNotUsed()
    {
        var package = SyntheticStaticData.Build(idOnVehicles: true);

        var (after, report, warnings) = Export(package, With(new AddSpawnPlaceOp("NewVehicleSpawn_0001", SpawnPlaceKind.Vehicle, "City", At(1f, 2f, 3f))));

        Assert.Equal(0, report.Created);
        Assert.Contains(warnings, w => w.Contains("NewVehicleSpawn_0001", StringComparison.Ordinal) && w.Contains("SpawnPointId", StringComparison.Ordinal));
        Assert.Equal(3, SpawnPlaces.Read(after).Count(p => p.Kind == SpawnPlaceKind.Vehicle));
    }

    private static (ActorRecord Actor, List<GrassClearing.Volume> Volumes) Placed(string mesh)
    {
        // A 40 x 30 x 12 m box standing on the ground at the origin.
        var bounds = new BoundingBox(new Vector3(-2000, -1500, 0), new Vector3(2000, 1500, 1200));
        var c = new ComponentRecord(1, "Mesh", "StaticMeshComponent", true, null, FTransform.Identity, FTransform.Identity, mesh, []);
        var actor = new ActorRecord(0, "Placed_1", "/Script/Engine.StaticMeshActor", 1, [c], FTransform.Identity, ActorKind.StaticMeshActor, mesh, []);
        var solid = GrassClearing.IsSolid(actor);
        return (actor, GrassClearing.Volumes(actor, _ => bounds).Select(v => v with { Solid = solid }).ToList());
    }

    private static readonly SpawnPlace CarOnTheFloor = new(SpawnPlaceKind.Vehicle, 0, new FTransform(FRotator.Zero, new FVector(100, 100, 10), FVector.One), "Default");

    [Fact]
    public void BlockKeepsPointsInsideAHollowAddedHouse()
    {
        var (house, volumes) = Placed("/Game/ConZ_Files/Models/Buildings/Hangar/SM_Hangar_01.SM_Hangar_01");
        Assert.False(GrassClearing.IsSolid(house));

        var request = new SpawnPlacesEditRequest();
        var result = SpawnPlacesEditor.Block(request, [CarOnTheFloor], volumes, out var blocked);

        Assert.Equal(0, blocked);
        Assert.Same(request, result);
    }

    [Fact]
    public void BlockRemovesPointsInsideAnAddedRock()
    {
        var (rock, volumes) = Placed("/Game/ConZ_Files/Landscape/Rocks/Cliffs/SM_Cliff_02b.SM_Cliff_02b");
        Assert.True(GrassClearing.IsSolid(rock));
        var names = new List<string>();

        var result = SpawnPlacesEditor.Block(new SpawnPlacesEditRequest(), [CarOnTheFloor], volumes, out var blocked, names);

        Assert.Equal(1, blocked);
        Assert.Contains((SpawnPlaceKind.Vehicle, 0), result.Deleted);
        Assert.Equal(["VehicleSpawn_00000"], names);
    }

    [Fact]
    public void WithEditsAppliesDeletesMovesCopiesAndNewPlaces()
    {
        var package = SyntheticStaticData.Build();
        var stock = SpawnPlaces.Read(package);
        ActorRef Ref(SpawnPlaceKind kind, int index) => new(SpawnPlaces.StaticDataPath, SpawnPlaces.ActorName(kind, index));
        var car1 = stock.Single(p => p.Kind == SpawnPlaceKind.Vehicle && p.Index == 1);
        var state = With(
            new DeleteActorOp(Ref(SpawnPlaceKind.Vehicle, 0)),
            new SetTransformOp(Ref(SpawnPlaceKind.Vehicle, 1), new TransformValue(car1.Transform.Translation, car1.Transform.Rotation.Rotator(), FVector.One), At(11f, 22f, 33f, yaw: 45f)),
            new DuplicateActorOp(Ref(SpawnPlaceKind.Vehicle, 2), "VehicleSpawn_00002_Copy", At(-1f, -2f, -3f)),
            new SetSpawnChoiceOp(new ActorRef(SpawnPlaces.StaticDataPath, "VehicleSpawn_00002_Copy"), SpawnChoices.VehicleGroup, null, "Boat"),
            new AddSpawnPlaceOp("NewThreatZone_0001", SpawnPlaceKind.Zone, SyntheticStaticData.Village, At(900f, 800f, 70f, sx: 12f, sy: 8f)),
            new AddSpawnPlaceOp("NewVehicleSpawn_0001", SpawnPlaceKind.Vehicle, "City", At(5f, 6f, 7f)));

        var places = SpawnPlaces.WithEdits(stock, state);

        var cars = places.Where(p => p.Kind == SpawnPlaceKind.Vehicle).ToList();
        Assert.Equal(["VehicleSpawn_00001", "VehicleSpawn_00002", "VehicleSpawn_00002_Copy", "NewVehicleSpawn_0001"], cars.Select(c => c.ActorName));
        Assert.Equal([1, 2, -1, -1], cars.Select(c => c.Index));
        Assert.Equal(["Default", "City", "Boat", "City"], cars.Select(c => c.Group));
        var zone = places.Last(p => p.Kind == SpawnPlaceKind.Zone);
        Assert.Equal(("NewThreatZone_0001", "MTZ_Village", 1200f, 800f), (zone.ActorName, zone.Group, zone.SizeX, zone.SizeY));

        // What the export writes, in the same order.
        var (after, _, warnings) = Export(package, state);
        Assert.Empty(warnings);
        var written = SpawnPlaces.Read(after);
        Assert.Equal(places.Count, written.Count);
        foreach (var (shown, stored) in places.Zip(written))
        {
            Assert.Equal((shown.Kind, shown.Group), (stored.Kind, stored.Group));
            Assert.True(FVector.Distance(shown.Transform.Translation, stored.Transform.Translation) < 0.01f, $"{shown.ActorName}: {shown.Transform.Translation} vs {stored.Transform.Translation}");
            Assert.Equal(shown.SizeX, stored.SizeX, 0.5f);
        }
    }
}
