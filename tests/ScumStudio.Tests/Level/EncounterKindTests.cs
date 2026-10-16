using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Export;
using ScumStudio.Level.Spawns;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// igor8802: "assign NPCs instead of zombies". What an encounter spawns comes from the character presets it draws from
/// (<c>Zombies_*</c>, <c>Drifter_*</c> / <c>Guard_*</c>, …), not from its class name (audit F443): synthetic encounter and
/// zone packages, no game files.
/// </summary>
public sealed class EncounterKindTests(ITestOutputHelper output)
{
    private const string Guard = SyntheticSpawnData.Presets + "NPCs/Guard_Bunker.Guard_Bunker";
    private const string Drifter = SyntheticSpawnData.Presets + "NPCs/Drifter_Bunker.Drifter_Bunker";
    private const string Civilians = SyntheticSpawnData.Presets + "Zombies/Zombies_Civilian_All.Zombies_Civilian_All";

    [Fact]
    public void KindComesFromThePresetsNotTheClassName()
    {
        using var data = new SyntheticSpawnData();
        data.Encounter("MTZ_Bunker_Encounter2", null, (Guard, 2f), (Drifter, 1f));
        data.Encounter("HTZ_Civilian_HotSpot_Character_Encounter_NPC", null, (Civilians, 1f));
        data.Zone("Medium_Threat/MTZ_Bunker", ("MTZ_Bunker_Encounter2", 1f));
        using var catalog = data.Open();

        // The name says nothing (the old rule's answer: Other, a numbered "Encounter 1" on the page) …
        Assert.Equal(SpawnCategory.Other, SpawnChoices.CategoryOf("MTZ_Bunker_Encounter2"));
        Assert.Equal(SpawnCategory.Npcs, SpawnChoices.KindOf(catalog, Class("MTZ_Bunker_Encounter2")));

        // … or the wrong thing: this one is named NPC and spawns zombies.
        Assert.Equal(SpawnCategory.Npcs, SpawnChoices.CategoryOf("HTZ_Civilian_HotSpot_Character_Encounter_NPC"));
        Assert.Equal(SpawnCategory.Zombies, SpawnChoices.KindOf(catalog, Class("HTZ_Civilian_HotSpot_Character_Encounter_NPC")));

        // The zone's weight lists say it too.
        var lists = SpawnChoices.ForZone(catalog, SyntheticSpawnData.Zones + "Medium_Threat/MTZ_Bunker");
        var encounter = Assert.Single(lists, l => l.Level == 1);
        Assert.Equal(SpawnCategory.Npcs, encounter.Category);
        Assert.Equal(["Guard_Bunker", "Drifter_Bunker"], encounter.Entries.Select(e => e.Name));
        Assert.Equal("_possibleCharacters[0].Value", encounter.Entries[0].Path);
    }

    [Fact]
    public void ZombieAndNpcEncountersAreToldApart()
    {
        using var data = new SyntheticSpawnData();
        data.Encounter("MTZ_Village_Encounter", null, (Civilians, 1f));
        data.Encounter("MTZ_Village_Encounter3", null, (Drifter, 1f));
        data.Encounter("MTZ_Village_Encounter3_Child", "MTZ_Village_Encounter3"); // stores no presets: its parent's apply
        data.Encounter("MTZ_Mixed_Encounter", null, (Civilians, 1f), (Guard, 1f));
        data.Encounter("Cargo_Drop_Encounter", null); // spawns no characters
        data.Zone("Medium_Threat/MTZ_Village", ("MTZ_Village_Encounter", 3f), ("MTZ_Village_Encounter3_Child", 1f));
        using var catalog = data.Open();

        Assert.Equal(SpawnCategory.Zombies, SpawnChoices.KindOf(catalog, Class("MTZ_Village_Encounter")));
        Assert.Equal(SpawnCategory.Npcs, SpawnChoices.KindOf(catalog, Class("MTZ_Village_Encounter3")));
        Assert.Equal(SpawnCategory.Npcs, SpawnChoices.KindOf(catalog, Class("MTZ_Village_Encounter3_Child")));
        Assert.Equal(SpawnCategory.Other, SpawnChoices.KindOf(catalog, Class("MTZ_Mixed_Encounter")));

        // The catalogue a zone's own settings pick from: by kind, each with its presets; the cargo drop is not in it.
        var catalogue = SpawnChoices.EncounterCatalog(catalog);
        Assert.DoesNotContain(catalogue, e => e.Name == "Cargo_Drop_Encounter");
        Assert.Equal(["MTZ_Village_Encounter", "MTZ_Village_Encounter3", "MTZ_Village_Encounter3_Child", "MTZ_Mixed_Encounter"], catalogue.Select(e => e.Name));
        Assert.Equal([SpawnCategory.Zombies, SpawnCategory.Npcs, SpawnCategory.Npcs, SpawnCategory.Other], catalogue.Select(e => e.Kind));
        Assert.Equal([Drifter], catalogue.Single(e => e.Name == "MTZ_Village_Encounter3_Child").Presets);

        // The child's row details its parent's weights, and the zone's real shares follow: 75 % zombies, 25 % NPCs.
        var lists = SpawnChoices.ForZone(catalog, SyntheticSpawnData.Zones + "Medium_Threat/MTZ_Village");
        var child = lists.Single(l => l.Level == 1 && l.Of == "MTZ_Village_Encounter3_Child");
        Assert.Equal("MTZ_Village_Encounter3", child.Name);
        var chances = SpawnChances.Effective(lists);
        Assert.Equal(75d, chances.Single(c => c.Kind == SpawnCategory.Zombies).Percent, 6);
        Assert.Equal(25d, chances.Single(c => c.Kind == SpawnCategory.Npcs).Percent, 6);

        // Usage: which zones draw from an encounter, and who shares a preset ("changes every place that uses it").
        var zones = SpawnChoices.ZoneEncounters(catalog);
        Assert.Equal([Class("MTZ_Village_Encounter"), Class("MTZ_Village_Encounter3_Child")], zones["MTZ_Village"]);
        SpawnPlace[] places = [Zone(0, "MTZ_Village"), Zone(1, "MTZ_Village"), Zone(2, "MTZ_Other")];
        var village = SpawnChoices.ZonesUsing(places, "MTZ_Village");
        Assert.Equal((2, false), (village.Places, village.WholeIsland));
        Assert.Empty(village.Unplaced);
        Assert.Equal(2, SpawnChoices.EncounterUsers(places, zones)[Class("MTZ_Village_Encounter")].Places);
        var users = SpawnChoices.PresetUsers(places, catalogue, zones, Drifter);
        Assert.Equal([Class("MTZ_Village_Encounter3"), Class("MTZ_Village_Encounter3_Child")], users.Encounters);
        Assert.Equal(["MTZ_Village"], users.ZoneSettings);
        Assert.Equal(2, users.Use.Places);
        Assert.False(users.Use.WholeIsland);
    }

    [Fact]
    public void TheWholeIslandsSettingsAreNotCountedAsAFewPlaces()
    {
        // Review: the settings of the island outside the zones (DA_EncounterZone_LowThreat) are named by no threat zone, so
        // a preset they use counted only the village's 2 zones: "changes every place that uses it (2)" for the whole island.
        using var data = new SyntheticSpawnData();
        data.Encounter("MTZ_Village_Encounter", null, (Civilians, 1f));
        data.Encounter("LowThreat_Encounter", null, (Civilians, 1f));
        data.Encounter("MTZ_Village_NPC_Encounter", null, (Drifter, 1f));
        data.Zone("Medium_Threat/MTZ_Village", ("MTZ_Village_Encounter", 1f));
        data.Zone(SpawnChoices.LowThreatZone, ("LowThreat_Encounter", 1f));
        data.Zone("Medium_Threat/MTZ_Village_LPC", ("MTZ_Village_NPC_Encounter", 1f));
        using var catalog = data.Open();
        var catalogue = SpawnChoices.EncounterCatalog(catalog);
        var zones = SpawnChoices.ZoneEncounters(catalog);
        SpawnPlace[] places = [Zone(0, "MTZ_Village"), Zone(1, "MTZ_Village")];

        var civilians = SpawnChoices.PresetUsers(places, catalogue, zones, Civilians);
        Assert.Equal([SpawnChoices.LowThreatZone, "MTZ_Village"], civilians.ZoneSettings);
        Assert.Equal((2, true), (civilians.Use.Places, civilians.Use.WholeIsland));
        Assert.Empty(civilians.Use.Unplaced);
        Assert.True(SpawnChoices.ZonesUsing(places, SpawnChoices.LowThreatZone).WholeIsland);
        Assert.True(SpawnChoices.IsWholeIsland(SyntheticSpawnData.Zones + SpawnChoices.LowThreatZone));
        Assert.True(SpawnChoices.EncounterUsers(places, zones)[Class("LowThreat_Encounter")].WholeIsland);

        // Settings no zone names are listed by name, not counted as 0 places.
        var drifters = SpawnChoices.PresetUsers(places, catalogue, zones, Drifter);
        Assert.Equal(0, drifters.Use.Places);
        Assert.False(drifters.Use.WholeIsland);
        Assert.Equal(["MTZ_Village_LPC"], drifters.Use.Unplaced);
    }

    [Fact]
    public void CountValuesAreTheStoredAmounts()
    {
        using var data = new SyntheticSpawnData();
        data.Encounter("MTZ_Village_Encounter", null, (Civilians, 1f));
        data.Encounter("MTZ_Village_Encounter_Child", "MTZ_Village_Encounter");
        using var catalog = data.Open();

        var counts = SpawnChoices.CountValues(catalog, Class("MTZ_Village_Encounter"));
        var count = Assert.Single(counts);
        Assert.Equal(("_maxNumberOfCharacters", "4"), (count.Value.Path, count.Value.Value));
        Assert.Equal(SyntheticSpawnData.Encounters + "MTZ_Village_Encounter", count.Package);
        Assert.DoesNotContain(counts, c => c.Value.Path == "_spawnRadius");
    }

    /// <summary>
    /// The game's own zones (real game files, <c>SCUM_PAKS</c>): every zone settings package's encounters get a kind from
    /// their presets, the catalogue holds zombie and NPC encounters, the zone shares sum to 100 % and the count values found
    /// are listed for review.
    /// </summary>
    [Fact]
    public void EveryZoneOfTheGameHasKindsFromItsPresets()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var zones = SpawnChoices.ZoneEncounters(catalog);
        output.WriteLine($"{zones.Count} zone settings");
        Assert.InRange(zones.Count, 70, 200);
        var catalogue = SpawnChoices.EncounterCatalog(catalog);
        Assert.Contains(catalogue, e => e.Kind == SpawnCategory.Zombies);
        Assert.Contains(catalogue, e => e.Kind == SpawnCategory.Npcs);
        var guessed = 0;
        foreach (var (zone, encounters) in zones)
        {
            foreach (var encounter in encounters)
            {
                var kind = SpawnChoices.KindOf(catalog, encounter);
                guessed += catalogue.Any(e => string.Equals(e.EncounterClass, encounter, StringComparison.OrdinalIgnoreCase)) ? 0 : 1;
                output.WriteLine($"{zone}: {SpawnChoices.ClassName(encounter)} = {kind}; counts: {string.Join(", ", SpawnChoices.CountValues(catalog, encounter).Select(c => $"{c.Value.Path}={c.Value.Value}"))}");
            }
        }

        output.WriteLine($"{guessed} encounter row(s) without readable presets (kind from the name)");
        var low = SpawnChoices.ForZone(catalog, SpawnChoices.ZonesFolder + SpawnChoices.LowThreatZone);
        var anyZone = zones.Keys.Select(z => SpawnChoices.ForZone(catalog, ZonePath(catalog, z))).First(l => l.Count > 1);
        Assert.Equal(100d, SpawnChances.Effective(anyZone).Sum(c => c.Percent), 3);
        output.WriteLine($"low threat lists: {low.Count}");

        // The 3D preview of a type: the characters of the zombie and NPC encounters have a model.
        var models = new ScumStudio.Viewport.SpawnModels(catalog);
        foreach (var kind in new[] { SpawnCategory.Zombies, SpawnCategory.Npcs })
        {
            var character = catalogue.Where(e => e.Kind == kind).SelectMany(e => e.CharacterClasses).First();
            output.WriteLine($"{kind}: {character} -> mesh {models.MeshOfClass(character)}, figure {models.FigureOfClass(character)}");
            Assert.NotNull(models.FigureOfClass(character));
            Assert.NotNull(models.MeshOfClass(character));
        }

        // The under-ground check over the whole island with a bounded tile cache.
        var places = SpawnPlaces.ReadFrom(catalog);
        var ground = new GroundHeights(catalog);
        var findings = SpawnGroundCheck.Run(places, SpawnGroundCheck.OutsideUnderground(SpawnGroundCheck.UndergroundBoxes(catalog)), ground.At, null, CancellationToken.None);
        output.WriteLine($"{findings.Count} of {places.Count} places below the ground ({ground.TileReads} tile reads, {ground.LoadedTiles} kept)");
        foreach (var group in findings.GroupBy(f => (f.Place.Kind, f.Place.Group)))
        {
            output.WriteLine($"{group.Key.Kind} {group.Key.Group}: {group.Count()} (deepest {group.Max(f => f.Depth):0} cm)");
        }

        Assert.True(ground.LoadedTiles <= GroundHeights.DefaultMaxTiles);
    }

    private static string ZonePath(AssetCatalog catalog, string name) => catalog.PackageFiles
        .Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
        .First(p => p.StartsWith(SpawnChoices.ZonesFolder, StringComparison.OrdinalIgnoreCase) && p.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));

    private static string Class(string name) => $"{SyntheticSpawnData.Encounters}{name}.{name}_C";

    private static SpawnPlace Zone(int index, string settings) =>
        new(SpawnPlaceKind.Zone, index, new ScumStudio.Core.Mathematics.FTransform(ScumStudio.Core.Mathematics.FVector.Zero), settings, 5000f, 5000f);
}
