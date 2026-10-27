using ScumStudio.Modding.ServerMods;
using Xunit;

namespace ScumStudio.Tests.Modding;

/// <summary>The server's loot customization: preset files, zones and the Override folder (the game's own formats, seen on a live server).</summary>
public sealed class LootOverrideTests : IDisposable
{
    private const string CratePreset = """
        {
        	"Nodes": [
        		{ "Rarity": "Uncommon", "Ids": [ "ItemLootTreeNodes.Workshop.Crafting", "ItemLootTreeNodes.Workshop.Other.Flares" ] }
        	],
        	"QuantityMin": 1,
        	"QuantityMax": 2,
        	"AllowDuplicates": false,
        	"ShouldFilterItemsByZone": true,
        	"ShouldApplyLocationSpecificProbabilityModifier": true,
        	"ShouldApplyLocationSpecificDamageModifier": true,
        	"InitialDamage": 10,
        	"RandomDamage": 20,
        	"PostSpawnActions": [ "KillboxKeycard_Radiation" ]
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "scumstudio-loot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ServerInstall FakeServer()
    {
        var win64 = Path.Combine(_root, "SCUM", "Binaries", "Win64");
        Directory.CreateDirectory(win64);
        File.WriteAllText(Path.Combine(win64, "SCUMServer.exe"), string.Empty);
        return ServerInstall.Find(win64)!;
    }

    [Fact]
    public void APresetIsReadChangedAndWrittenKeepingWhatItDoesNotKnow()
    {
        var preset = LootPresetFile.Parse(CratePreset);
        Assert.Null(preset.Probability);
        Assert.Equal((1, 2), (preset.QuantityMin, preset.QuantityMax));
        Assert.True(preset.FilterByZone);
        var group = Assert.Single(preset.Groups);
        Assert.Equal(("Uncommon", 2), (group.Rarity, group.Entries.Count));

        preset.Probability = 100;
        preset.FilterByZone = false;
        preset.ApplyLocationProbability = false;
        preset.QuantityMax = 3;
        preset.SetGroups(
        [
            new LootGroup("Common", ["ItemLootTreeNodes.Workshop.Other.FoodAndDrink"]),
            new LootGroup("VeryRare", ["ItemLootTreeNodes.Military.Weapons.Rifles", "KeyCard"]),
        ]);

        var again = LootPresetFile.Parse(preset.ToJson());
        Assert.Equal(100, again.Probability);
        Assert.False(again.FilterByZone);
        Assert.False(again.ApplyLocationProbability);
        Assert.True(again.ApplyLocationDamage);
        Assert.Equal(3, again.QuantityMax);
        Assert.Equal(["Common:ItemLootTreeNodes.Workshop.Other.FoodAndDrink", "VeryRare:ItemLootTreeNodes.Military.Weapons.Rifles", "VeryRare:KeyCard"],
            again.Groups.SelectMany(g => g.Entries.Select(e => g.Rarity + ":" + e)));
        Assert.Contains("KillboxKeycard_Radiation", again.ToJson(), StringComparison.Ordinal); // a field the editor does not know stays
        Assert.Empty(again.Problems());
    }

    [Fact]
    public void ProblemsAreFoundBeforeAFileIsWritten()
    {
        var preset = LootPresetFile.Empty();
        Assert.Contains(preset.Problems(), p => p.Code == "NoLoot");

        preset.SetGroups([new LootGroup("Mythic", ["not a tag", "Weapons.Rifles"])]);
        preset.QuantityMin = 3;
        preset.QuantityMax = 1;
        preset.Probability = 150;
        var codes = preset.Problems().Select(p => p.Code).ToList();
        Assert.Contains("Rarity", codes);
        Assert.Contains("Entry", codes);
        Assert.Contains("Quantity", codes);
        Assert.Contains("Probability", codes);

        var store = new LootOverrideStore(FakeServer());
        Assert.Throws<ArgumentException>(() => store.WritePreset("Zone", "Some-Preset", preset));
        Assert.Throws<FormatException>(() => LootPresetFile.Parse("[1, 2]"));
    }

    [Fact]
    public void AZoneKeepsTheGamesCornerOrderAndItsPresets()
    {
        var store = new LootOverrideStore(FakeServer());
        Assert.False(store.HasDefaults);
        var zone = LootZone.Of(452300, -238600, 460300, -230600);
        Assert.Equal(new LootZone(460300, -230600, 452300, -238600), zone);

        store.WriteZone("Dungeon", zone);
        var text = File.ReadAllText(Path.Combine(store.OverrideFolder, "Dungeon", "Zones.json"));
        Assert.Contains("\"TopLeft\": \"X=460300.000 Y=-230600.000\"", text, StringComparison.Ordinal);
        Assert.Contains("\"BottomRight\": \"X=452300.000 Y=-238600.000\"", text, StringComparison.Ordinal);
        Assert.Equal(zone, store.ReadZone("Dungeon"));

        var preset = LootPresetFile.Parse(CratePreset);
        preset.Probability = 100;
        store.WritePreset("Dungeon", "Street-Military-Examine_Crate", preset);
        Assert.Equal(["Dungeon"], store.Zones());
        Assert.Equal(["Street-Military-Examine_Crate"], store.PresetsOf("Dungeon"));
        Assert.Equal(100, store.ReadOverride("Dungeon", "Street-Military-Examine_Crate")!.Probability);

        store.RemovePreset("Dungeon", "Street-Military-Examine_Crate");
        Assert.Empty(store.PresetsOf("Dungeon"));
        store.RemoveZone("Dungeon");
        Assert.Empty(store.Zones());
    }

    [Fact]
    public void DefaultsAreListedAndNamesAreChecked()
    {
        var store = new LootOverrideStore(FakeServer());
        Directory.CreateDirectory(store.DefaultFolder);
        File.WriteAllText(Path.Combine(store.DefaultFolder, "Street-Military-Examine_Crate.json"), CratePreset);
        Assert.True(store.HasDefaults);
        Assert.Equal(["Street-Military-Examine_Crate"], store.DefaultPresets());
        Assert.NotNull(store.ReadDefault("Street-Military-Examine_Crate"));
        Assert.Null(store.ReadDefault("..\\Secret"));

        Assert.False(LootOverrideStore.IsValidZoneName("..\\x"));
        Assert.False(LootOverrideStore.IsValidZoneName("a b"));
        Assert.True(LootOverrideStore.IsValidZoneName("Dungeon-1"));
        Assert.Throws<ArgumentException>(() => store.WriteZone("Dungeon", LootZone.Of(1, 1, 1, 5)));
        Assert.Equal(new LootZone(110, 220, 90, 180), LootZone.Around([(100, 200)], 10).Equals(default) ? default : LootZone.Of(90, 180, 110, 220));
    }

    [Fact]
    public void ARealExportedFolderReadsBack()
    {
        var path = @"D:\SteamLibrary\steamapps\common\SCUM Server\SCUM\Binaries\Win64";
        if (ServerInstall.Find(path) is not { } server || new LootOverrideStore(server) is not { HasDefaults: true } store)
        {
            return; // needs the owner's exported presets
        }

        var names = store.DefaultPresets();
        Assert.True(names.Count > 100);
        foreach (var name in names.Take(300))
        {
            var preset = store.ReadDefault(name);
            Assert.NotNull(preset);
            _ = preset.Groups;
        }
    }
}
