using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Tests.Modding;
using ScumStudio.Tests.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// CwHedgehog (#feature-requests, 2026-10-17): "duplicate some in-game items, such as copper coins; at the moment only
/// weapons can be duplicated". Every inventory item (an item class with its <c>_ES</c> beside it) is listed on the Items
/// page and cloned like a weapon: its own class, entity setup and registry records. No game files: a small loose game.
/// </summary>
public sealed class ItemsPageTests
{
    private const string Folder = "/Game/ConZ_Files/Items/Equipment/Active_Items/";
    private const string Coins = Folder + "Copper_Coins";
    private const string Rifle = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";

    [Fact]
    public void EveryInventoryItemIsAnItem()
    {
        Assert.Equal((ModdableKind.Item, "Equipment"), Kind(Coins));
        Assert.Equal((ModdableKind.Item, "Medications"), Kind("/Game/ConZ_Files/Items/Medications/Painkillers_01"));
        Assert.Equal((ModdableKind.Item, "Weapons"), Kind("/Game/ConZ_Files/Items/Weapons/Attachments/Suppressors/WeaponSuppressor_PBS1")); // not a Weapon_
        Assert.Equal((ModdableKind.Item, "Ammunition"), Kind("/Game/ConZ_Files/Items/Ammunition/Arrows/Wooden_Arrow"));
        Assert.Equal((ModdableKind.Weapon, "Ranged_Weapons"), Kind(Rifle)); // the Weapons page keeps its kinds
        Assert.Equal((ModdableKind.Ammo, "Ammunition"), Kind("/Game/ConZ_Files/Items/Ammunition/Cal_7_62x39mm"));
        Assert.Equal((ModdableKind.Weapon, ModdableAssets.MeleeCategory), Kind("/Game/ConZ_Files/Items/Weapons/New_Melee/1H_KitchenKnife"));
        Assert.Null(ModdableAssets.Classify(Coins + "_ES"));
        Assert.Null(ModdableAssets.Classify("/Game/ConZ_Files/Items/SpawnerPresets2/Buildings/Bar/Examine_Cash_Register"));
        Assert.Null(ModdableAssets.Classify("/Game/ConZ_Files/Items/Item_Icons/ICO_Copper_Coins"));
        Assert.Null(ModdableAssets.Classify("/Game/ConZ_Files/Items/BP_ItemSpawningManager"));
        Assert.Null(ModdableAssets.Classify("/Game/ConZ_Files/Models/Items/SM_Copper_Coins"));
    }

    [Fact]
    public async Task TheItemsPageClonesCopperCoinsWithTheirOwnClassSetupAndRecords()
    {
        using var temp = new TempDirectory();
        var registry = WriteGame(temp.Path);
        using (var catalog = AssetCatalog.OpenLoose(temp.Path))
        {
            var found = ModdableAssets.Find(catalog);
            var coins = Assert.Single(found, a => a.Kind == ModdableKind.Item);
            Assert.Equal((Coins, Coins + "_ES"), (coins.PackagePath, coins.EntitySetupPath)); // not its mesh, which has no _ES
            Assert.Contains(found, a => a.Kind == ModdableKind.Weapon && a.PackagePath == Rifle);
        }

        using var ctx = AppTestContext.Create();
        using var page = new ItemsPageViewModel(ctx.Services);
        Assert.True(await page.OpenLooseFolderAsync(temp.Path));
        var listed = Assert.Single(page.AllItems); // the rifle is on the Weapons page
        Assert.Equal(("Copper_Coins", "Icon.Basket"), (listed.Name, listed.IconKey));
        Assert.Equal(["All", "Equipment"], page.Filters.Select(f => f.Label));
        Assert.True(await page.SelectAsync("Copper_Coins"));
        Assert.Equal("#SpawnItem Copper_Coins", page.SpawnCommandText);
        Assert.Equal(["Item", "Entity setup"], page.Parts.Select(p => p.Label));
        page.CloneName = "Hedgehog_Coins";
        Assert.Contains("#SpawnItem Hedgehog_Coins", page.ClonePreview, StringComparison.Ordinal);

        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Hedgehog coins");
        await page.CreateCloneAsync();
        var clone = Assert.Single(page.AllItems, i => i.IsClone);
        Assert.Same(clone, page.SelectedItem);
        Assert.Equal(("Hedgehog_Coins", "#SpawnItem Hedgehog_Coins", Folder + "Hedgehog_Coins_ES"), (clone.Name, page.SpawnCommandText, clone.Asset.EntitySetupPath));
        var state = ctx.Services.Projects.Current!.State;
        var op = Assert.Single(state.AssetClones);
        Assert.Equal([(Coins, Folder + "Hedgehog_Coins"), (Coins + "_ES", Folder + "Hedgehog_Coins_ES")], op.Packages.Select(p => (p.Old, p.New)));

        // The export: the clone's own class and setup, registered like the stock coins; the stock coins are not in the mod.
        using var game = AssetCatalog.OpenLoose(temp.Path);
        var result = AssetModBuilder.Build(game, ProjectExporter.BuildAssetRequest(state) with { BaseRegistry = registry, SellClones = false });
        Assert.Empty(result.Warnings);
        Assert.Equal([Folder + "Hedgehog_Coins", Folder + "Hedgehog_Coins_ES"], result.Packages.Select(p => p.PackagePath));
        Assert.All(result.Packages, p => Assert.True(p.IsClone));
        Assert.Equal(
            [
                (Folder + "Hedgehog_Coins.Hedgehog_Coins", "Item"), (Folder + "Hedgehog_Coins.Hedgehog_Coins_C", "Item"),
                (Folder + "Hedgehog_Coins_ES.Hedgehog_Coins_ES", "LWObjectSetup"), (Folder + "Hedgehog_Coins_ES.Hedgehog_Coins_ES_C", "LWObjectSetup"),
            ],
            result.Registered.OrderBy(r => r.ObjectPath, StringComparer.Ordinal).Select(r => (r.ObjectPath, r.PrimaryAssetType)));
    }

    [Fact]
    public void AnItemCloneCannotTakeTheNameOfAnItemInAnotherFolder()
    {
        using var temp = new TempDirectory();
        WriteGame(temp.Path);
        using var catalog = AssetCatalog.OpenLoose(temp.Path);

        // #SpawnItem Weapon_AK47 would name two items: the rifle and the coins' clone (another folder, so its path is free).
        var taken = Assert.Throws<ArgumentException>(() => CloneFamilyPlanner.PlanItem(catalog, Coins, "Weapon_AK47"));
        Assert.Contains(Rifle, taken.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => CloneFamilyPlanner.PlanItem(catalog, Coins, "weapon_ak47"));
        Assert.Equal(Folder + "Weapon_AK", CloneFamilyPlanner.PlanItem(catalog, Coins, "Weapon_AK").NewPrimary); // a name it only starts with is free
        Assert.Equal(Folder + "SM_Copper", CloneFamilyPlanner.PlanItem(catalog, Coins, "SM_Copper").NewPrimary);
    }

    private static (ModdableKind, string)? Kind(string path) => ModdableAssets.Classify(path) is { } a ? (a.Kind, a.Category) : null;

    /// <summary>The coins and their setup, their mesh (no setup), a rifle and its setup, and the registry records of the coins.</summary>
    private static byte[] WriteGame(string root)
    {
        SyntheticGame.Write(root, Coins, SyntheticGame.Package(Coins, [], ["Copper_Coins_C", "Default__Copper_Coins_C"]));
        SyntheticGame.Write(root, Coins + "_ES", SyntheticGame.Package(Coins + "_ES", [], [Coins + ".Copper_Coins_C", "Copper_Coins_ES_C"]));
        SyntheticGame.Write(root, Folder + "SM_Copper_Coins", SyntheticGame.Package(Folder + "SM_Copper_Coins"));
        SyntheticGame.Write(root, Rifle, SyntheticGame.Package(Rifle, [], ["Weapon_AK47_C"]));
        SyntheticGame.Write(root, Rifle + "_ES", SyntheticGame.Package(Rifle + "_ES", [], [Rifle + ".Weapon_AK47_C"]));
        var records = new List<SyntheticGame.Record>();
        foreach (var (package, type) in new[] { (Coins, "Item"), (Coins + "_ES", "LWObjectSetup") })
        {
            var leaf = package[(package.LastIndexOf('/') + 1)..];
            records.Add(new SyntheticGame.Record($"{package}.{leaf}", "Blueprint", SyntheticGame.PrimaryTags(type, leaf)));
            records.Add(new SyntheticGame.Record($"{package}.{leaf}_C", "BlueprintGeneratedClass", SyntheticGame.PrimaryTags(type, leaf)));
        }

        var registry = SyntheticGame.Registry([.. records]);
        SyntheticGame.Write(root, registry);
        return registry.Save();
    }
}
