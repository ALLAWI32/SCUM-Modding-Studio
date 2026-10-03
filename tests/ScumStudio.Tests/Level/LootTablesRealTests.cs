using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.Spawns;
using ScumStudio.Pak;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Owner: "the spawner does not tell me what spawns here". Real game files only (<c>SCUM_PAKS</c>, key from this PC's
/// store): a hangar shelf's loot point leads to its preset, the preset to the airfield loot group, the group to its items;
/// a blue pin's group to the vehicles that appear on it.
/// </summary>
public sealed class LootTablesRealTests
{
    private const string HangarShelf = "/Game/ConZ_Files/Items/SpawnerPresets2/Buildings/Airfield_Hangar/World_Shelf.World_Shelf_C";
    private readonly ITestOutputHelper _output;

    public LootTablesRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void AHangarShelfLeadsToTheAirfieldItemsAndAGroupToItsVehicles()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

        // The loot point stores which World_Shelf it uses (many folders have one).
        var level = LevelDocument.Load(new Cue4ParseLevelReader(catalog, new Cue4ParseLevelReaderOptions()), "/Game/ConZ_Files/Maps/The_Island/A_4_Airfield");
        var markers = level.FindActor("BP_Airplane_Hangar2_2")!.Components.SelectMany(c => c.SpawnMarkers).ToList();
        Assert.Equal(25, markers.Count);
        _output.WriteLine("points: " + string.Join(", ", markers.Select(m => $"{m.Probability}% x{m.MinQuantity}-{m.MaxQuantity}").Distinct()));
        Assert.All(markers, m => Assert.Equal(HangarShelf, m.PresetPath));

        var loot = LootTables.Read(catalog);
        _output.WriteLine($"{loot.NodeCount} loot tree nodes");
        Assert.True(loot.NodeCount > 1000, $"{loot.NodeCount} nodes");
        var preset = loot.Preset(HangarShelf)!;
        Assert.Equal(20f, preset.Probability);
        Assert.Equal(new LootNode("ItemLootTreeNodes.Airfield", "Uncommon"), Assert.Single(preset.Nodes));

        var items = loot.ItemsUnder("ItemLootTreeNodes.Airfield");
        _output.WriteLine(string.Join(", ", items.Select(i => $"{i.Name} ({i.Branch}, {i.Rarity})")));
        Assert.Contains(new LootItem("Car_Battery", "Tools", "Rare"), items);
        Assert.Contains(new LootItem("Pilot_Glasses", "Clothes.Head", "Common"), items);
        Assert.DoesNotContain(items, i => i.Name is "Tools" or "Clothes"); // branches are not items

        var vehicles = VehicleSpawnGroups.Read(catalog);
        _output.WriteLine(string.Join("; ", vehicles.Groups.Select(g => g + ": " + string.Join(", ", vehicles.VehiclesOf(g).Select(v => v.Vehicle)))));
        Assert.Contains(vehicles.VehiclesOf("City"), v => v.Vehicle == "Rager");
        Assert.NotEmpty(vehicles.VehiclesOf("CivilianAirplane"));
    }
}
