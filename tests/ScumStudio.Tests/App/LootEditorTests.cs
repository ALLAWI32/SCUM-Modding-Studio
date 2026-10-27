using Avalonia.Headless.XUnit;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views;
using ScumStudio.Modding.ServerMods;

namespace ScumStudio.Tests.App;

/// <summary>The loot editor window: a game preset becomes a zone's changed preset, with groups and a chance, written to the server.</summary>
[Collection(LanguageCollection.Name)]
public sealed class LootEditorTests
{
    private const string Crate = """
        {
        	"Nodes": [ { "Rarity": "Uncommon", "Ids": [ "ItemLootTreeNodes.Workshop.Crafting" ] } ],
        	"QuantityMin": 1,
        	"QuantityMax": 2,
        	"ShouldFilterItemsByZone": true,
        	"ShouldApplyLocationSpecificProbabilityModifier": true,
        	"ShouldApplyLocationSpecificDamageModifier": true
        }
        """;

    private static LootOverrideStore Prepare(AppTestContext ctx)
    {
        var server = ServerPageTests.FakeServer(ctx, withUe4ss: true);
        var store = new LootOverrideStore(server);
        Directory.CreateDirectory(store.DefaultFolder);
        File.WriteAllText(Path.Combine(store.DefaultFolder, "Street-Military-Examine_Crate.json"), Crate);
        return store;
    }

    [AvaloniaFact]
    public void AGamePresetBecomesAZonesPresetWithOwnGroupsAndAChance()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var store = Prepare(ctx);
        var vm = new LootEditorViewModel(ctx.Services);
        Assert.True(vm.HasServer);
        Assert.True(vm.HasDefaults);
        Assert.Equal(["Street-Military-Examine_Crate"], vm.DefaultRows);

        vm.ZoneName = "Dungeon";
        vm.X1 = "452300";
        vm.Y1 = "-238600";
        vm.X2 = "460300";
        vm.Y2 = "-230600";
        vm.SelectedDefault = vm.DefaultRows[0];
        vm.AddPresetCommand.Execute(null);

        Assert.True(vm.HasPreset);
        Assert.False(vm.FilterByZone); // a new changed preset starts with the zone rules off
        Assert.Equal("100", vm.ProbabilityText);
        var first = Assert.Single(vm.Groups);
        Assert.Equal("Uncommon", first.Rarity);

        vm.QuantityMax = 3;
        first.Rarity = "Common";
        first.EntriesText = "ItemLootTreeNodes.Workshop.Other.FoodAndDrink\nItemLootTreeNodes.Workshop.Other.Flares";
        vm.AddGroupCommand.Execute(null);
        vm.SelectedGroup!.Rarity = "Rare";
        vm.SelectedGroup.EntriesText = "ItemLootTreeNodes.Military.Weapons.Rifles\nKeyCard";
        Assert.Equal("×4", vm.SelectedGroup.WeightText);
        Assert.True(vm.CanSave);

        vm.SaveCommand.Execute(null);

        var saved = store.ReadOverride("Dungeon", "Street-Military-Examine_Crate");
        Assert.NotNull(saved);
        Assert.Equal(100, saved.Probability);
        Assert.Equal((1, 3), (saved.QuantityMin, saved.QuantityMax));
        Assert.False(saved.FilterByZone);
        Assert.Equal(["Common:ItemLootTreeNodes.Workshop.Other.FoodAndDrink", "Common:ItemLootTreeNodes.Workshop.Other.Flares",
            "Rare:ItemLootTreeNodes.Military.Weapons.Rifles", "Rare:KeyCard"], saved.Groups.SelectMany(g => g.Entries.Select(e => g.Rarity + ":" + e)));
        Assert.Equal(new LootZone(460300, -230600, 452300, -238600), store.ReadZone("Dungeon"));
        Assert.Contains("Dungeon", vm.Zones);
        Assert.Contains(LootOverrideStore.ReloadCommand, vm.Status, StringComparison.Ordinal);

        // A preset with nothing to give is refused with a reason and nothing is written.
        vm.Groups.Clear();
        vm.SaveCommand.Execute(null);
        Assert.NotEmpty(vm.Status);
        Assert.Equal(3, store.ReadOverride("Dungeon", "Street-Military-Examine_Crate")!.Groups.Count); // two tag groups and the single item

        vm.RemovePresetCommand.Execute(null);
        Assert.Empty(store.PresetsOf("Dungeon"));
        vm.RemoveZoneCommand.Execute(null);
        Assert.Empty(store.Zones());
    }

    [AvaloniaFact]
    public void WithoutExportedPresetsTheEditorSaysHowToGetThem()
    {
        using var ctx = AppTestContext.Create(inline: false);
        ServerPageTests.FakeServer(ctx, withUe4ss: true);
        var vm = new LootEditorViewModel(ctx.Services);
        Assert.True(vm.HasServer);
        Assert.False(vm.HasDefaults);
        Assert.Empty(vm.DefaultRows);
        Assert.False(vm.CanSave);
    }

    [AvaloniaFact]
    public void TheWindowOpensWithTheEditorAndTheZone()
    {
        using var ctx = AppTestContext.Create(inline: false);
        Prepare(ctx);
        var vm = new LootEditorViewModel(ctx.Services);
        var window = new LootEditorWindow { DataContext = vm };
        try
        {
            window.Show();
            vm.ZoneName = "Dungeon";
            vm.X1 = "452300";
            vm.Y1 = "-238600";
            vm.X2 = "460300";
            vm.Y2 = "-230600";
            vm.SelectedDefault = vm.DefaultRows[0];
            vm.AddPresetCommand.Execute(null);
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "window-loot-editor");
        }
        finally
        {
            window.Close();
        }
    }
}
