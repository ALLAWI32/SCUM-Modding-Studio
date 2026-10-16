using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>The game's own ingredient tags and the items that satisfy them, read from the paks (SCUM_PAKS).</summary>
public sealed class CraftIngredientsRealTests
{
    private const string Weapons = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/";

    /// <summary>
    /// Lead plates are their own tag, the M16 too, the improvised workbench counts as machinery, the AK-47 only as a group;
    /// the bait feeder (a setup not named after it) is an item; every tag of the folder is indexed and shows its item.
    /// </summary>
    [Fact]
    public void TheGamesItemsResolveToTheirTags()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var index = IngredientIndex.Of(catalog);
        var items = CraftablesPlanner.Items(catalog);
        string Item(string leaf) => Assert.Single(items, i => PackageMap.Leaf(i) == leaf);

        var lead = index.Resolve(Item("Lead_Plate"));
        Assert.Equal((IngredientMatch.OwnTag, "CI_Lead_Plate"), (lead.Match, lead.Tag));
        Assert.NotNull(index.Tag("CI_Weapon_M16")); // the game has no Weapon_M16 item (its M16 is Weapon_M16A4, which the tag does not name)
        var bench = index.Resolve("/Game/ConZ_Files/Items/Equipment/Active_Items/Improvised_Workbench");
        Assert.Contains("CI_Group_Machinery", bench.Groups.Append(bench.Tag));
        Assert.NotEqual(IngredientMatch.NotItem, index.Resolve(Weapons + "Weapon_AK47").Match); // an item; the real AK-47 setup lists no tag of its own

        var feeder = Assert.Single(ItemSetups.Of(catalog).NotNamedAfterItem, s => PackageMap.Leaf(s.Value) == "AnimalBaitFeeder_Ridgeline_ES");
        Assert.True(CraftablesPlanner.IsItem(catalog, feeder.Key));
        Assert.Contains(feeder.Key, items);

        var folder = (catalog.Index ?? catalog.BuildIndex(resolveClasses: false)).Entries
            .Count(e => e.PackagePath.StartsWith(RecipeIngredients.TagFolder + "/", StringComparison.OrdinalIgnoreCase) && e.Name.StartsWith("CI_", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(index.Tags.Count, folder, folder + 16); // every tag of the folder, and the few DLC packs keep in their own
        Assert.EndsWith("/Lead_Plate", index.Tag("CI_Lead_Plate")?.Representative, StringComparison.Ordinal);
        Assert.Equal(PackageMap.Leaf(CraftablesPlanner.ItemTagTemplate), index.Tag("CI_Lead_Plate")?.Name);
    }
}
