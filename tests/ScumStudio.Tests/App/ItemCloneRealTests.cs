using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Objects.Core.i18N;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Level.Export;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Registry;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// CwHedgehog (#feature-requests, 2026-10-17 03:06): "It would be great if we could duplicate some in-game items, such as
/// copper coins. At the moment, it seems that only weapons can be duplicated." On the Items page the copper coins
/// (<c>Equipment/Active_Items/Copper_Coins</c>, class parent <c>EquipmentItem</c>) and a consumable (painkillers, a blister
/// of 8 uses that the traders sell) are cloned under new names, their common values edited, and the export read back with
/// the game's reader (CUE4Parse): each clone has its own class, entity setup with the edited values and registry records,
/// the painkillers their own trader row; the stock items are unchanged and not in the mod. Real game files only (<c>SCUM_PAKS</c>,
/// key from this PC's store). What only the game can prove: that <c>#SpawnItem Hedgehog_Coins</c> spawns them.
/// </summary>
public sealed class ItemCloneRealTests
{
    private const string Coins = "/Game/ConZ_Files/Items/Equipment/Active_Items/Copper_Coins";
    private const string Pills = "/Game/ConZ_Files/Items/Medications/Painkillers_01";
    private const string NewCoins = "/Game/ConZ_Files/Items/Equipment/Active_Items/Hedgehog_Coins";
    private const string NewPills = "/Game/ConZ_Files/Items/Medications/Hedgehog_Painkillers";

    [PaksFact]
    public async Task CopperCoinsAndPainkillersAreClonedWithTheirOwnClassSetupAndRecords()
    {
        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = RealPaks.Folder });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Hedgehog items");
        using var page = new ItemsPageViewModel(ctx.Services);
        await page.LoadCompletion;
        var game = ctx.Services.Workspace.Catalog!;

        // Every inventory item, not only weapons: the samples and their categories; the weapons stay on their page.
        Assert.True(page.AllItems.Count > 2000, $"{page.AllItems.Count} items");
        Assert.Contains(page.AllItems, i => i.PackagePath == Coins && i.Asset.Category == "Equipment" && i.Asset.EntitySetupPath == Coins + "_ES");
        Assert.Contains(page.AllItems, i => i.PackagePath == Pills && i.Asset.Category == "Medications");
        Assert.DoesNotContain(page.AllItems, i => i.Name == "Weapon_AK47");
        Assert.Contains(page.Filters, f => f.Label == "Food");

        // A resource: its stack size is the entity setup's StackableEntityComponentSetup MaxQuantity, a key value.
        Assert.True(await page.SelectAsync("Nails"));
        Assert.True(await page.SelectPartAsync("Entity setup"));
        var stack = page.FindRow("MaxQuantity")!;
        Assert.Equal(("StackableEntityComponentSetup_0", "10"), (stack.Tunable.Export, stack.StockValue));
        Assert.Contains(page.Groups.SelectMany(g => g.Rows), r => ReferenceEquals(r, stack));

        // 1. Copper coins: a new name, in-game name, description, weight and (inherited, written into the clone) health.
        Assert.True(await page.SelectAsync("Copper_Coins"));
        Assert.Equal(["Item", "Entity setup"], page.Parts.Select(p => p.Label)); // the traders do not sell coins
        await page.PreviewCompletion;
        Assert.True(page.Preview is { Parts.Count: > 0 }, page.PreviewText); // the 3D view shows the coins' mesh
        Assert.NotNull(InventoryIcons.FindIconPath(game, Coins + "_ES")); // and the list their inventory icon
        page.CloneName = "Hedgehog_Coins";
        page.CloneCaption = "Hedgehog Coins";
        await page.CreateCloneAsync();
        Assert.Equal(("Hedgehog_Coins", "#SpawnItem Hedgehog_Coins", "Hedgehog Coins"), (page.SelectedItem!.Name, page.SpawnCommandText, page.Caption));
        await page.PreviewCompletion;
        Assert.True(page.Preview is { Parts.Count: > 0 }, page.PreviewText); // a clone shows its template's mesh
        Assert.True(await page.SelectPartAsync("Entity setup"));
        Assert.Equal(NewCoins + "_ES", page.SelectedPart!.PackagePath);
        Set(page, "Weight", "30", "5");
        Set(page, "Description", null, "Hedgehog's lucky coins.");
        Set(page, "MaxHealth", string.Empty, "250");
        page.ApplyChanges();

        // 2. Painkillers: uses per blister, weight, name and the trader's price of the clone's own row.
        Assert.True(await page.SelectAsync("Painkillers_01"));
        Assert.Equal(["Item", "Entity setup", "Trader"], page.Parts.Select(p => p.Label));
        page.CloneName = "Hedgehog_Painkillers";
        page.CloneCaption = "Hedgehog Painkillers";
        await page.CreateCloneAsync();
        Assert.Equal("#SpawnItem Hedgehog_Painkillers", page.SpawnCommandText);
        Assert.True(await page.SelectPartAsync("Entity setup"));
        Assert.Equal("DiscreteUsageItemEntityComponentSetup_0", page.FindRow("MaxQuantity")!.Tunable.Export);
        Set(page, "MaxQuantity", "8", "20");
        Set(page, "Weight", "8", "4");
        page.ApplyChanges();
        Assert.True(await page.SelectPartAsync("Trader"));
        Assert.Equal("Rows[Hedgehog_Painkillers_C].BasePurchasePrice", page.FindRow("BasePurchasePrice")!.Tunable.Path);
        Set(page, "BasePurchasePrice", "2400", "1000");
        page.ApplyChanges();
        Assert.Equal(2, ctx.Services.Projects.Current!.State.AssetClones.Count);

        var result = await new ProjectExporter().ExportAsync(ctx.Services.Projects.Current!, game,
            new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Hedgehog", StringComparison.Ordinal));
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);

        // Own classes (parents as the stock items'), own setups that name them, the edited values.
        Assert.Equal("/Script/SCUM.EquipmentItem", ParentOf(written, NewCoins));
        Assert.Equal(ParentOf(game, Pills), ParentOf(written, NewPills));
        Assert.Contains(NewCoins + ".Hedgehog_Coins_C", ModdableAssets.ReadPackage(written, NewCoins + "_ES").Names);
        Assert.DoesNotContain(ModdableAssets.ReadPackage(written, NewCoins + "_ES").Names, n => n.StartsWith(Coins + ".", StringComparison.Ordinal));
        var coins = Cdo(written, NewCoins + "_ES", "Default__Hedgehog_Coins_ES_C");
        Assert.Equal((5f, 250f), (coins.GetOrDefault<float>("Weight", -1f), coins.GetOrDefault<float>("MaxHealth", -1f)));
        Assert.Equal("Hedgehog Coins", coins.GetOrDefault<FText>("Caption")?.Text);
        Assert.Equal("Hedgehog's lucky coins.", coins.GetOrDefault<FText>("Description")?.Text);
        Assert.Equal(2, coins.GetOrDefault<byte>("GridInventoryRowSpan")); // the rest as the stock coins
        var pills = Cdo(written, NewPills + "_ES", "Default__Hedgehog_Painkillers_ES_C");
        Assert.Equal((4f, "Hedgehog Painkillers"), (pills.GetOrDefault<float>("Weight", -1f), pills.GetOrDefault<FText>("Caption")?.Text));
        Assert.Equal(20, Cdo(written, NewPills + "_ES", "DiscreteUsageItemEntityComponentSetup_0").GetOrDefault<int>("MaxQuantity", -1));

        // The painkillers' clone has its own trader row; the stock row is as it was.
        var table = written.LoadObject<UDataTable>(DataTableEdits.TradeableTable + ".Table_TradeableDesc");
        int Price(string row) => table.RowMap.Single(r => r.Key.Text == row).Value.GetOrDefault<int>("BasePurchasePrice", -1);
        Assert.Equal((1000, 2400), (Price("Hedgehog_Painkillers_C"), Price("Painkillers_01_C")));
        Assert.DoesNotContain(table.RowMap, r => r.Key.Text.StartsWith("Hedgehog_Coins", StringComparison.Ordinal));

        // Registry: the stock records unchanged, plus exactly the clones' item and entity-setup records.
        var shipped = AssetRegistryFile.Load(Path.Combine(result.StagingDirectory, "SCUM", "AssetRegistry.bin"));
        ExportSafety.AssertRegistrySuperset(AssetRegistryFile.Parse(RegistryMerge.ReadStock(game)!), shipped, result.Registered.Select(r => r.ObjectPath));
        foreach (var (package, type) in new[] { (NewCoins, "Item"), (NewCoins + "_ES", "LWObjectSetup"), (NewPills, "Item"), (NewPills + "_ES", "LWObjectSetup") })
        {
            var leaf = package[(package.LastIndexOf('/') + 1)..];
            foreach (var objectPath in new[] { $"{package}.{leaf}", $"{package}.{leaf}_C" })
            {
                var record = Assert.Single(shipped.Assets, a => a.ObjectPath == objectPath);
                var tags = shipped.GetTags(record).ToDictionary(t => t.Key, t => t.Value.Text, StringComparer.Ordinal);
                Assert.Equal((type, leaf), (tags["PrimaryAssetType"], tags["PrimaryAssetName"]));
            }
        }

        // The stock items are not in the mod and the game's copies are as they were.
        var shippedPaths = result.Assets.Select(a => a.PackagePath).ToList();
        Assert.Contains(NewCoins, shippedPaths);
        Assert.Contains(NewPills + "_ES", shippedPaths);
        foreach (var stock in new[] { Coins, Coins + "_ES", Pills, Pills + "_ES" })
        {
            Assert.DoesNotContain(stock, shippedPaths);
            Assert.False(written.PackageExists(stock), stock);
        }

        ExportSafety.AssertOnlyAdds(game, shippedPaths, new HashSet<string>([DataTableEdits.TradeableTable], StringComparer.OrdinalIgnoreCase));
        var stockCoins = Cdo(game, Coins + "_ES", "Default__Copper_Coins_ES_C");
        Assert.Equal((30f, -1f), (stockCoins.GetOrDefault<float>("Weight", -1f), stockCoins.GetOrDefault<float>("MaxHealth", -1f)));
        Assert.Equal(8, Cdo(game, Pills + "_ES", "DiscreteUsageItemEntityComponentSetup_0").GetOrDefault<int>("MaxQuantity", -1));
    }

    /// <summary>Sets the row <paramref name="name"/> of the selected part (checking its stock value when given).</summary>
    private static void Set(ModulePageViewModel page, string name, string? stock, string value)
    {
        var row = page.FindRow(name);
        Assert.True(row is not null, $"{page.SelectedPart?.PackagePath} has no {name}");
        Assert.True(row!.Tunable.CanEdit, $"{name} is read-only");
        if (stock is not null)
        {
            Assert.Equal(stock, row.StockValue);
        }

        row.Value = value;
        Assert.Null(row.Error);
    }

    private static UObject Cdo(AssetCatalog catalog, string package, string export) =>
        catalog.LoadPackage(package).GetExports().Single(e => e.Name == export);

    /// <summary>The parent class of the package's Blueprint class (<c>/Script/SCUM.EquipmentItem</c>), read from its header.</summary>
    private static string ParentOf(AssetCatalog catalog, string package)
    {
        var cooked = ModdableAssets.ReadPackage(catalog, package);
        var leaf = package[(package.LastIndexOf('/') + 1)..];
        var cls = Enumerable.Range(0, cooked.Exports.Count).Single(i => cooked.ResolveName(cooked.Exports[i].ObjectName) == leaf + "_C");
        Assert.Equal("BlueprintGeneratedClass", cooked.GetExportClassName(cls));
        return cooked.GetFullPath(cooked.Exports[cls].SuperIndex);
    }
}
