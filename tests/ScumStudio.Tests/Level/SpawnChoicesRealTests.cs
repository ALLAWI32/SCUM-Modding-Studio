using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "when I select a spawn — the sentry robot's or anything that spawns — give me lists of what can go there and let
/// me replace it; and the percentage of every zombie". Real game files only (<c>SCUM_PAKS</c>, key from this PC's store):
/// the lists come from the game's data, and every choice and chance is written and read back.
/// </summary>
public sealed class SpawnChoicesRealTests(ITestOutputHelper output)
{
    private const string Barracks = "/Game/ConZ_Files/Maps/The_Island/D_0_Military_Barracks";
    private const string Sentry2 = "/Game/ConZ_Files/Characters/Mechanoids/Sentry/BP_Sentry2.BP_Sentry2_C";

    private static AssetCatalog? Open() =>
        Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;

    [Fact]
    public void ASentrySpawnerSpawnsAnotherRobot()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return; // not asked for
        }

        var sentries = SpawnChoices.SentryClasses(catalog);
        output.WriteLine(string.Join(", ", sentries));
        Assert.Contains("/Game/ConZ_Files/Characters/Mechanoids/Sentry/BP_Sentry.BP_Sentry_C", sentries);
        Assert.Contains(Sentry2, sentries);
        Assert.DoesNotContain(sentries, s => s.Contains("AIController", StringComparison.Ordinal));

        // The barracks' first spawner (and a copy of it made in the same request) spawn the second robot; the others keep theirs.
        var barracks = ModdableAssets.ReadPackage(catalog, Barracks);
        var request = new LevelEditRequest
        {
            Copies = [new ActorCopy("SentrySpawner_0", "SentrySpawner_0_Copy", new(new(-861946.6f, 503722.1f, 60305.04f), default, ScumStudio.Core.Mathematics.FVector.One))],
            SoftClasses = [new SoftClassPatch("SentrySpawner_0", SpawnChoices.SentryClass, Sentry2), new SoftClassPatch("SentrySpawner_0_Copy", SpawnChoices.SentryClass, Sentry2)],
        };
        var (bytes, report) = LevelPackageEditor.Apply(barracks, request);
        Assert.Empty(report.Warnings);
        var written = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, Barracks);
        Assert.Equal(Sentry2, SentryClassOf(written, "SentrySpawner_0"));
        Assert.Equal(Sentry2, SentryClassOf(written, "SentrySpawner_0_Copy"));
        Assert.Equal("/Game/ConZ_Files/Characters/Mechanoids/Sentry/BP_Sentry.BP_Sentry_C", SentryClassOf(written, "SentrySpawner_1"));
    }

    [Fact]
    public void AZonesEncountersPresetsAndCharactersAreListedWithTheirWeightsAndWrittenBack()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        var zone = SpawnChoices.PackageNamed(catalog, SpawnChoices.ZonesFolder, "MTZ_Settlement_Large");
        Assert.Equal("/Game/ConZ_Files/Encounters/EncounterZones/Medium_Threat/MTZ_Settlement_Large", zone);
        var lists = SpawnChoices.ForZone(catalog, zone!);
        foreach (var list in lists)
        {
            output.WriteLine($"{list.Level} {list.Category} {list.Name}: {string.Join(", ", list.Entries.Select(e => $"{e.Name}={e.Weight}"))}");
        }

        // The zone: zombies 75, armed NPCs 25 (EncounterData weights).
        var encounters = lists[0];
        Assert.Equal(0, encounters.Level);
        Assert.Equal([75f, 25f], encounters.Entries.Select(e => e.Weight));
        Assert.Equal(["MTZ_Settlement_Large_Character_Encounter", "MTZ_Settlement_Large_NPC_Encounter"], encounters.Entries.Select(e => e.Name));
        Assert.True(encounters.IsEditable);

        // Each encounter's presets (zombies; drifters and guards), each preset's character Blueprints.
        Assert.Contains(lists, l => l.Level == 1 && l.Category == SpawnCategory.Zombies && l.Entries.Count > 0);
        Assert.Contains(lists, l => l.Level == 1 && l.Category == SpawnCategory.Npcs && l.Entries.Any(e => e.Name.StartsWith("Drifter_Class", StringComparison.Ordinal)));
        var zombies = Assert.Single(lists, l => l.Level == 2 && l.Name == "Zombies_Civilian_All");
        Assert.Equal(SpawnCategory.Zombies, zombies.Category);
        Assert.Equal(7, zombies.Entries.Count);
        Assert.Contains(zombies.Entries, e => e.Name == "BP_Zombie_Civilian_Fat_Male" && e.Weight == 100f);

        // A character's weight and an encounter's weight are stored values: patched in place and read back.
        foreach (var (list, path, value) in new[] { (zombies, zombies.Entries[1].Path!, "250"), (encounters, encounters.Entries[1].Path!, "60") })
        {
            var package = ModdableAssets.ReadPackage(catalog, list.Package);
            var patched = TunablePatcher.Apply(package, [new TunableEdit(list.Export, path, value)]);
            var again = CookedPackage.Parse(patched.Bytes.UAsset, patched.Bytes.UExp, null, list.Package);
            Assert.Equal(value, TunableReader.Read(again).Single(t => t.Export == list.Export && t.Path == path).Value);
            Assert.Equal(TunableReader.Read(package).Count, TunableReader.Read(again).Count);
        }
    }

    [Fact]
    public void ABiomesAnimalsAreListedWithTheirSharesAndWrittenBack()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        var biome = SpawnChoices.PackageNamed(catalog, SpawnChoices.BiomesFolder, "BD_Village")!;
        var lists = SpawnChoices.ForBiome(catalog, biome);
        foreach (var list in lists)
        {
            output.WriteLine($"{list.Name}: {string.Join(", ", list.Entries.Select(e => $"{e.Name}={e.Weight} ({e.Path})"))}");
        }

        Assert.Equal(2, lists.Count);
        Assert.Equal([2, 5], lists.Select(l => l.Entries.Count));
        Assert.All(lists, l => Assert.True(l.IsEditable));
        Assert.Contains(lists[0].Entries, e => e.Name == "BP_Rabbit" && e.Weight == 0.5f);
        Assert.StartsWith("PopulationData#1.AnimalSpawnData[", lists[1].Entries[0].Path, StringComparison.Ordinal);

        var package = ModdableAssets.ReadPackage(catalog, biome);
        var path = lists[1].Entries[2].Path!;
        var patched = TunablePatcher.Apply(package, [new TunableEdit(lists[1].Export, path, "0.75")]);
        var again = CookedPackage.Parse(patched.Bytes.UAsset, patched.Bytes.UExp, null, biome);
        Assert.Equal("0.75", TunableReader.Read(again).Single(t => t.Path == path).Value);
    }

    [Fact]
    public void ASpawnPlaceSpawnsAnotherGroupZoneOrBiome()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        var package = ModdableAssets.ReadPackage(catalog, SpawnPlaces.StaticDataPath);
        var places = SpawnPlaces.Read(package);
        SpawnPlace First(SpawnPlaceKind kind) => places.First(p => p.Kind == kind);
        string Other(SpawnPlace place) => places.Where(p => p.Kind == place.Kind && p.Group.Length > 0 && p.Group != place.Group).Select(p => p.Group).First();
        var (car, zone, area) = (First(SpawnPlaceKind.Vehicle), First(SpawnPlaceKind.Zone), First(SpawnPlaceKind.Animal));
        var (group, settings, biome) = (Other(car), Other(zone), Other(area));
        output.WriteLine($"{car.ActorName} {car.Group} -> {group}; {zone.ActorName} {zone.Group} -> {settings}; {area.ActorName} {area.Group} -> {biome}");

        // The three choices, a copy of the car point that carries the new group, and one that has its own.
        var copyAt = new ScumStudio.Level.Model.TransformValue(new(1f, 2f, 3f), default, ScumStudio.Core.Mathematics.FVector.One);
        var boat = places.First(p => p.Kind == SpawnPlaceKind.Vehicle && p.Group != car.Group && p.Group != group).Group;
        var (bytes, report) = SpawnPlacesEditor.Apply(package, new SpawnPlacesEditRequest
        {
            Choices = new Dictionary<(SpawnPlaceKind, int), string> { [(car.Kind, car.Index)] = group, [(zone.Kind, zone.Index)] = settings, [(area.Kind, area.Index)] = biome },
            Added = [(car.Kind, car.Index, copyAt), (car.Kind, car.Index, copyAt)],
            AddedChoices = new Dictionary<int, string> { [1] = boat },
        });
        Assert.Empty(report.Warnings);
        Assert.Equal(3, report.Chosen);
        var read = SpawnPlaces.Read(CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SpawnPlaces.StaticDataPath));
        Assert.Equal(group, read.Single(p => p.Kind == car.Kind && p.Index == car.Index).Group);
        Assert.Equal(settings, read.Single(p => p.Kind == zone.Kind && p.Index == zone.Index).Group);
        Assert.Equal(biome, read.Single(p => p.Kind == area.Kind && p.Index == area.Index).Group);
        var copies = read.Where(p => p.Kind == SpawnPlaceKind.Vehicle).TakeLast(2).Select(p => p.Group).ToList();
        Assert.Equal([group, boat], copies);
        Assert.Equal(places.Count + 2, read.Count);

        // A group, zone or biome the island data does not have is refused, not guessed.
        var (_, refused) = SpawnPlacesEditor.Apply(package, new SpawnPlacesEditRequest { Choices = new Dictionary<(SpawnPlaceKind, int), string> { [(zone.Kind, zone.Index)] = "MTZ_Nowhere" } });
        Assert.Single(refused.Warnings);
        Assert.Equal(0, refused.Chosen);
    }

    private static string? SentryClassOf(CookedPackage package, string actor)
    {
        var index = package.Exports.ToList().FindIndex(e => package.ResolveName(e.ObjectName) == actor);
        Assert.True(index >= 0, actor);
        return (package.ReadProperties(index).Find(SpawnChoices.SentryClass)?.Value as SoftObjectValue)?.AssetPath;
    }
}
