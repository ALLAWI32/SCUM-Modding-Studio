using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Catalog;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "show me the real places where cars, planes, zombies spawn, on a map I can fly over, and let me move, delete and
/// copy them". Real game files only (<c>SCUM_PAKS</c>, key from this PC's store): the island's spawn places are read from
/// <c>The_Island_LevelStaticData</c>, become a level the map shows, and edits are written back exactly.
/// </summary>
public sealed class StaticSpawnPlacesRealTests
{
    private static AssetCatalog? Open() =>
        Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;

    [Fact]
    public void TheIslandsSpawnPlacesAreReadAndShownAsALevel()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return; // not asked for
        }

        var places = SpawnPlaces.Read(ModdableAssets.ReadPackage(catalog, SpawnPlaces.StaticDataPath));
        var counts = places.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(3491, counts[SpawnPlaceKind.Vehicle]);
        Assert.Equal(25802, counts[SpawnPlaceKind.Character]);
        Assert.Equal(295, counts[SpawnPlaceKind.TaggedCharacter]);
        Assert.Equal(294, counts[SpawnPlaceKind.Zone]);
        Assert.Equal(474, counts[SpawnPlaceKind.Animal]);
        Assert.Contains(places, p => p.Kind == SpawnPlaceKind.Vehicle && p.Group == "City");
        Assert.Contains(places, p => p.Kind == SpawnPlaceKind.Vehicle && p.Group == "CivilianAirplane");
        Assert.Contains(places, p => p.Kind == SpawnPlaceKind.Character && p.Group == "Interior");
        Assert.All(places.Where(p => p.Kind == SpawnPlaceKind.Zone), z => Assert.True(z.SizeX > 0 && z.SizeY > 0 && z.Group.Length > 0));

        // As a level: one actor per place, standing where the place is, a zone's size in its scale.
        var doc = LevelDocument.FromData(SpawnPlaces.ToLevelData(places));
        Assert.Equal(places.Count, doc.Actors.Count);
        var car = places.First(p => p.Kind == SpawnPlaceKind.Vehicle);
        var actor = doc.FindActor(car.ActorName)!;
        Assert.Equal(car.Transform.Translation.X, actor.WorldTransform.Translation.X, 0.01f);
        Assert.Equal(car.Group, actor.ClassPath);
        var zone = places.First(p => p.Kind == SpawnPlaceKind.Zone);
        Assert.Equal(zone.SizeX / 100f, doc.FindActor(zone.ActorName)!.WorldTransform.Scale3D.X, 0.001f);
    }

    [Fact]
    public void EditsAreWrittenBackAndReadAgain()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return; // not asked for
        }

        var package = ModdableAssets.ReadPackage(catalog, SpawnPlaces.StaticDataPath);
        var stock = SpawnPlaces.Read(package);
        SpawnPlace Stock(SpawnPlaceKind kind, int index) => stock.Single(p => p.Kind == kind && p.Index == index);

        // A zone "moved" to where it is: the recomputed boxes must equal the stock ones (same formula as the game data).
        var zone = Stock(SpawnPlaceKind.Zone, 3);
        var zoneValue = new TransformValue(zone.Transform.Translation, zone.Transform.Rotation.Rotator(), new FVector(zone.SizeX / 100f, zone.SizeY / 100f, 1f));
        var same = SpawnPlacesEditor.Apply(package, new SpawnPlacesEditRequest { Moved = new Dictionary<(SpawnPlaceKind, int), TransformValue> { [(SpawnPlaceKind.Zone, 3)] = zoneValue } });
        var sameBlock = CookedPackage.Parse(same.Bytes.UAsset, same.Bytes.UExp, null, SpawnPlaces.StaticDataPath);
        Assert.Equal(package.GetExportData(0).Length, sameBlock.GetExportData(0).Length);
        var differing = 0;
        var a = package.GetExportData(0).Span;
        var b = sameBlock.GetExportData(0).Span;
        for (var i = 0; i + 4 <= a.Length; i += 4)
        {
            if (Math.Abs(BitConverter.ToSingle(a.Slice(i, 4)) - BitConverter.ToSingle(b.Slice(i, 4))) > 0.5f)
            {
                differing++;
            }
        }

        Assert.Equal(0, differing); // nothing moved by more than half a centimetre

        // Delete a car place, move one, copy one (twice), make a zone twice as big.
        var moveTo = new TransformValue(new FVector(100f, 200f, 300f), new FRotator(0f, 90f, 0f), FVector.One);
        var copyAt = new TransformValue(new FVector(-5000f, 7000f, 1200f), FRotator.Zero, FVector.One);
        var request = new SpawnPlacesEditRequest
        {
            Deleted = [(SpawnPlaceKind.Vehicle, 5)],
            Moved = new Dictionary<(SpawnPlaceKind, int), TransformValue>
            {
                [(SpawnPlaceKind.Vehicle, 7)] = moveTo,
                [(SpawnPlaceKind.Zone, 3)] = zoneValue with { Scale = new FVector(zone.SizeX / 50f, zone.SizeY / 50f, 1f) },
            },
            Added = [(SpawnPlaceKind.Vehicle, 9, copyAt), (SpawnPlaceKind.Vehicle, 9, copyAt with { Location = new FVector(1f, 2f, 3f) })],
        };
        var (bytes, report) = SpawnPlacesEditor.Apply(package, request);
        Assert.Empty(report.Warnings);
        Assert.Equal((1, 2, 2), (report.Deleted, report.Moved, report.Added));

        var edited = SpawnPlaces.Read(CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SpawnPlaces.StaticDataPath));
        var cars = edited.Where(p => p.Kind == SpawnPlaceKind.Vehicle).ToList();
        Assert.Equal(3491 - 1 + 2, cars.Count);
        Assert.Equal(Stock(SpawnPlaceKind.Vehicle, 6).Transform.Translation, cars[5].Transform.Translation); // the one after the deleted one moved up
        Assert.Equal(moveTo.Location, cars[6].Transform.Translation);
        Assert.Equal(90f, cars[6].Transform.Rotation.Rotator().Yaw, 0.01f);
        Assert.Equal(copyAt.Location, cars[^2].Transform.Translation);
        Assert.Equal(Stock(SpawnPlaceKind.Vehicle, 9).Group, cars[^1].Group); // the copy keeps its group
        Assert.Equal(new FVector(1f, 2f, 3f), cars[^1].Transform.Translation);
        var bigger = edited.Single(p => p.Kind == SpawnPlaceKind.Zone && p.Index == 3);
        Assert.Equal(zone.SizeX * 2f, bigger.SizeX, 0.5f);
        Assert.Equal(zone.Group, bigger.Group);
        Assert.Equal(25802, edited.Count(p => p.Kind == SpawnPlaceKind.Character)); // untouched arrays stay as they are
    }

    /// <summary>The map's edits (move, delete, copy of a place actor) go through the project like any edit and Export mod writes them.</summary>
    [Fact]
    public async Task ExportWritesTheMapsSpawnPlaceEdits()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return; // not asked for
        }

        var places = SpawnPlaces.ReadFrom(catalog);
        var doc = LevelDocument.FromData(SpawnPlaces.ToLevelData(places, p => p.Kind == SpawnPlaceKind.Vehicle && p.Index < 20));
        var state = new ScumStudio.Level.Editing.EditState();
        var moved = doc.FindActor(SpawnPlaces.ActorName(SpawnPlaceKind.Vehicle, 3))!;
        state.Apply(ScumStudio.Level.Editing.EditOpFactory.SetTransform(doc, moved, TransformValue.At(11f, 22f, 33f), state));
        state.Apply(new ScumStudio.Level.Editing.DeleteActorOp(new ScumStudio.Level.Editing.ActorRef(SpawnPlaces.StaticDataPath, SpawnPlaces.ActorName(SpawnPlaceKind.Vehicle, 4))));
        var copy = ScumStudio.Level.Editing.EditOpFactory.Duplicate(doc, doc.FindActor(SpawnPlaces.ActorName(SpawnPlaceKind.Vehicle, 5))!, TransformValue.At(-1f, -2f, -3f), state);
        state.Apply(copy);

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-places-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await new ScumStudio.Level.Export.ProjectExporter().ExportAsync(state, "Places", catalog,
                new ScumStudio.Level.Export.ExportOptions { OutputDirectory = dir, WritePak = false }, ScumStudio.Level.Projects.ProjectSourceRole.Server);
            var stem = Path.Combine(result.StagingDirectory, "SCUM", "Content", "ConZ_Files", "Maps", "The_Island", "The_Island_LevelStaticData");
            var written = SpawnPlaces.Read(CookedPackage.Parse(File.ReadAllBytes(stem + ".uasset"), File.ReadAllBytes(stem + ".uexp"), null, SpawnPlaces.StaticDataPath));
            var cars = written.Where(p => p.Kind == SpawnPlaceKind.Vehicle).ToList();
            Assert.Equal(3491, cars.Count); // one deleted, one added
            Assert.Equal(new FVector(11f, 22f, 33f), cars[3].Transform.Translation);
            Assert.Equal(places.Single(p => p.Kind == SpawnPlaceKind.Vehicle && p.Index == 5).Transform.Translation, cars[4].Transform.Translation);
            Assert.Equal(new FVector(-1f, -2f, -3f), cars[^1].Transform.Translation);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
