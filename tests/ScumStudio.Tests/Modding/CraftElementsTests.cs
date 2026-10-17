using System.Text.RegularExpressions;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using static ScumStudio.Tests.Modding.ElementFixture;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Owner (2026-10-12): "a bed must behave like a normal bed: he can set its health, craft it from the items he chooses, and
/// respawn at it". An element craftable is a clone of the game's own element family (recipe, element, upgrade chain,
/// spawned item), not of the improvised chair; its values are written per level (synthetic game, <see cref="ElementFixture"/>).
/// </summary>
public sealed class CraftElementsTests
{
    private static Craftable Element(string name, string source, string? mesh = null, CraftUse? use = null, params Dictionary<string, string>[] levels) => new()
    {
        Name = name,
        Source = source,
        Mesh = mesh ?? (source == Chest ? ChestMesh : BedMesh),
        Kind = CraftKind.Element,
        Use = use,
        Element = new ElementSettings { Template = source == Chest ? Chest : Bed, Levels = levels },
        Ingredients = [new("CI_Plank", 6)],
    };

    private static CookedPackage Built(AssetModBuildResult result, string path)
    {
        var built = Assert.Single(result.Packages, p => string.Equals(p.PackagePath, path, StringComparison.OrdinalIgnoreCase));
        return CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, built.UBulk, path);
    }

    private static PropertyBlock Defaults(CookedPackage package)
    {
        var name = $"Default__{Leaf(package.BasePath!)}_C";
        return package.ReadProperties(Enumerable.Range(0, package.Exports.Count).Single(i => package.ResolveName(package.Exports[i].ObjectName) == name));
    }

    private static (string Package, string Object) Imported(CookedPackage package, PropertyValue? value)
    {
        var import = package.Imports[-Assert.IsType<ObjectValue>(value).Index - 1];
        return (package.ResolveName(package.Imports[-import.OuterIndex - 1].ObjectName), package.ResolveName(import.ObjectName));
    }

    private static string Product(CookedPackage recipe) =>
        Assert.IsType<SoftObjectValue>(recipe.ReadProperties(RecipeIngredients.RecipeExport(recipe)).Find("Product")!.Value).AssetPath;

    [Fact]
    public void ABedIsClonedFromTheBedNotTheChair()
    {
        using var game = new ElementFixture();
        var family = ElementFamilies.Of(game.Catalog, Bed);
        Assert.NotNull(family);
        Assert.True(family!.IsRespawnPoint);
        Assert.Equal([Bed, BedUpgrade], family.Levels.Select(l => l.Element));
        Assert.Equal("1000", family.Levels[0].Values.Single(v => v.Path == "_healthPoints").Value);
        Assert.Equal("true", family.Levels[0].Values.Single(v => v.Path == "_isUsedForRespawn").Value);

        var plan = CraftablesPlanner.Plan(game.Catalog, [Element("Camp Bed", Bed)]);
        var entry = Assert.Single(plan.Entries);
        var clone = Assert.Single(plan.Request.Clones);
        Assert.Equal([BedRecipe, Bed, BedUpgradeRecipe, BedUpgrade], clone.Packages.Select(p => p.Key));
        Assert.DoesNotContain(clone.Packages, p => p.Key.Contains("Chair", StringComparison.Ordinal));
        Assert.Equal(Placeables + "CR_SS_Camp_Bed", entry.Recipe);
        Assert.Equal(Elements + "BP_SS_Camp_Bed", entry.Product);
        Assert.Null(entry.Item);
        Assert.Equal([new CraftIngredient("CI_Plank", 6)], plan.Request.Ingredients[entry.Recipe]);
    }

    [Fact]
    public void TheClonedBedKeepsItsRespawnFlagAndItsUpgradeChain()
    {
        using var game = new ElementFixture();
        var plan = CraftablesPlanner.Plan(game.Catalog, [Element("Camp Bed", Bed)]);
        var result = AssetModBuilder.Build(game.Catalog, plan.Request);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("not cloned", StringComparison.Ordinal) || w.Contains("skipped", StringComparison.Ordinal) || w.Contains("not written", StringComparison.Ordinal));

        var bed = Built(result, Elements + "BP_SS_Camp_Bed");
        var defaults = Defaults(bed);
        Assert.True(Assert.IsType<BoolValue>(defaults.Find("_isUsedForRespawn")!.Value).Value);
        Assert.Equal("Camp Bed", Assert.IsType<TextValue>(defaults.Find("_name")!.Value).SourceString);
        Assert.Equal((Placeables + "CR_SS_Camp_Bed_Up1", "CR_SS_Camp_Bed_Up1"), Imported(bed, defaults.Find("_upgradeRecipe")!.Value));

        // Each recipe makes its own level; the level-0 recipe has the user's ingredients, the upgrade keeps the game's.
        var recipe = Built(result, Placeables + "CR_SS_Camp_Bed");
        Assert.Equal(Elements + "BP_SS_Camp_Bed.BP_SS_Camp_Bed_C", Product(recipe));
        Assert.Equal([new CraftIngredient("CI_Plank", 6)], RecipeIngredients.Read(recipe));
        var upgrade = Built(result, Placeables + "CR_SS_Camp_Bed_Up1");
        Assert.Equal(Elements + "BP_SS_Camp_Bed_L1.BP_SS_Camp_Bed_L1_C", Product(upgrade));
        Assert.Equal([new CraftIngredient("CI_Plank", 4)], RecipeIngredients.Read(upgrade));
        Assert.Equal("Camp Bed (2)", Assert.IsType<TextValue>(Defaults(Built(result, Elements + "BP_SS_Camp_Bed_L1")).Find("_name")!.Value).SourceString);
    }

    [Fact]
    public void ElementValuesAreWrittenPerLevel()
    {
        using var game = new ElementFixture();
        var craftable = Element("Camp Bed", Bed, levels:
        [
            new() { ["_healthPoints"] = "500", ["_isUsedForRespawn"] = "maybe" },
            new() { ["_healthPoints"] = "3000", ["_hoursToDecay"] = "24" },
        ]);
        var plan = CraftablesPlanner.Plan(game.Catalog, [craftable]);
        Assert.Contains(plan.Warnings, w => w.Contains("_hoursToDecay of level 1 inherits the game's value", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("_isUsedForRespawn of level 0 is not a valid value", StringComparison.Ordinal));

        // Reset is a key left out: no edit, the game's bytes.
        Assert.Equal(["_name", "_healthPoints"], plan.Request.Edits[Elements + "BP_SS_Camp_Bed"].Select(e => e.Path));
        var result = AssetModBuilder.Build(game.Catalog, plan.Request);
        var level0 = Defaults(Built(result, Elements + "BP_SS_Camp_Bed"));
        Assert.Equal(500f, Assert.IsType<FloatValue>(level0.Find("_healthPoints")!.Value).Value);
        Assert.Equal(72f, Assert.IsType<FloatValue>(level0.Find("_hoursToDecay")!.Value).Value);
        Assert.True(Assert.IsType<BoolValue>(level0.Find("_isUsedForRespawn")!.Value).Value);
        var level1 = Defaults(Built(result, Elements + "BP_SS_Camp_Bed_L1"));
        Assert.Equal(3000f, Assert.IsType<FloatValue>(level1.Find("_healthPoints")!.Value).Value);
        Assert.Null(level1.Find("_hoursToDecay"));
    }

    [Fact]
    public void NoMeshRedirectWhenTheElementsOwnMeshIsKept()
    {
        using var game = new ElementFixture();
        var plan = CraftablesPlanner.Plan(game.Catalog, [Element("Camp Bed", Bed, BedUpgradeMesh)]);
        Assert.Empty(Assert.Single(plan.Request.Clones).Redirects);
    }

    [Fact]
    public void AnotherMeshIsRedirectedOnEveryLevel()
    {
        using var game = new ElementFixture();
        var plan = CraftablesPlanner.Plan(game.Catalog, [Element("Camp Bed", Bed, OtherMesh)]);
        var clone = Assert.Single(plan.Request.Clones);
        Assert.Equal(new KeyValuePair<string, string>[] { new(BedMesh, OtherMesh), new(BedUpgradeMesh, OtherMesh) }, clone.Redirects);
        var result = AssetModBuilder.Build(game.Catalog, plan.Request);
        foreach (var level in new[] { "BP_SS_Camp_Bed", "BP_SS_Camp_Bed_L1" })
        {
            var element = Built(result, Elements + level);
            Assert.Equal((OtherMesh, Leaf(OtherMesh)), Imported(element, Defaults(element).Find("StaticMesh")!.Value));
            Assert.DoesNotContain(element.Names, n => n.Contains("SM_Bed_Improvised", StringComparison.Ordinal));
        }

        // The bed's weather mask is no look of its own (renaming it too would put SM_Cot_Field twice in one name table).
        var bed = Built(result, Elements + "BP_SS_Camp_Bed");
        Assert.Equal((WeatherMesh, Leaf(WeatherMesh)), Imported(bed, Defaults(bed).Find("_weatherMaskMesh")!.Value));

        // A chest with mesh variations keeps its own look, or is left out saying why.
        var chest = CraftablesPlanner.Plan(game.Catalog, [Element("Camp Chest", Chest, OtherMesh)]);
        Assert.Empty(chest.Entries);
        Assert.Contains(chest.Warnings, w => w.StartsWith("Camp Chest: left out", StringComparison.Ordinal) && w.Contains("1 mesh variation(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void AChestKeepsItsItemAndBothAddsAnItemRecipe()
    {
        using var game = new ElementFixture();
        var family = ElementFamilies.Of(game.Catalog, Chest)!;
        Assert.Equal("BaseBuildingItemComponent", family.NativeClass);
        Assert.Equal(ChestItem, family.ItemClass);
        Assert.False(family.IsRespawnPoint);

        var plan = CraftablesPlanner.Plan(game.Catalog, [Element("Camp Chest", Chest, use: CraftUse.Both), Element("Camp Bed", Bed, use: CraftUse.Both)]);
        var chest = plan.Entries.Single(e => e.Name == "Camp Chest");
        Assert.Equal(Containers + "SS_Camp_Chest", chest.Item);
        Assert.Equal("/Game/ConZ_Files/Items/Crafting/Recipes/Items/CR_SS_Camp_Chest_Item", chest.ItemRecipe);
        Assert.Equal([new CraftIngredient("CI_Plank", 6)], plan.Request.Ingredients[chest.Recipe]);
        Assert.Equal([new CraftIngredient("CI_Plank", 6)], plan.Request.Ingredients[chest.ItemRecipe!]);

        // The bed spawns no item: it is built only, and says so.
        Assert.Null(plan.Entries.Single(e => e.Name == "Camp Bed").ItemRecipe);
        Assert.Contains(plan.Warnings, w => w.StartsWith("Camp Bed:", StringComparison.Ordinal) && w.Contains("only built", StringComparison.Ordinal));

        var result = AssetModBuilder.Build(game.Catalog, plan.Request);
        var element = Built(result, Elements + "BP_SS_Camp_Chest");
        Assert.Equal((Containers + "SS_Camp_Chest", "SS_Camp_Chest_C"), Imported(element, Defaults(element).Find("_itemClass")!.Value));
        Assert.Equal(Containers + "SS_Camp_Chest.SS_Camp_Chest_C", Product(Built(result, chest.ItemRecipe!)));
        Assert.Equal([new CraftIngredient("CI_Plank", 6)], RecipeIngredients.Read(Built(result, chest.ItemRecipe!)));
        Assert.Equal("Camp Chest", Assert.IsType<TextValue>(Defaults(Built(result, Containers + "SS_Camp_Chest_ES")).Find("Caption")!.Value).SourceString);
    }

    [Fact]
    public void OnlyNewPathsAreWritten()
    {
        using var game = new ElementFixture();
        var plan = CraftablesPlanner.Plan(game.Catalog, [Element("Camp Chest", Chest, use: CraftUse.Both), Element("Camp Bed", Bed, OtherMesh)]);
        Assert.Equal(2, plan.Entries.Count);
        Assert.All(plan.Request.Clones.SelectMany(c => c.Packages), p => Assert.False(game.Catalog.PackageExists(p.Value), p.Value));
        var result = AssetModBuilder.Build(game.Catalog, plan.Request);
        Assert.Equal(plan.Request.Clones.Sum(c => c.Packages.Count), result.Packages.Count);
        Assert.All(result.Packages, p =>
        {
            Assert.True(p.IsClone, p.PackagePath);
            Assert.False(game.Catalog.PackageExists(p.PackagePath), p.PackagePath);
        });
    }

    [Fact]
    public void DlcAndModularElementsAreNotOffered()
    {
        using var game = new ElementFixture();
        Assert.Null(ElementFamilies.WhyNot(game.Catalog, Bed));
        Assert.Contains("DLC", ElementFamilies.WhyNot(game.Catalog, Dlc), StringComparison.Ordinal);
        Assert.Contains("modular", ElementFamilies.WhyNot(game.Catalog, Wall), StringComparison.Ordinal);
        Assert.Null(ElementFamilies.WhyNot(game.Catalog, Lonely)); // no recipe of its own: it borrows the chair's (CraftBuildPlaceTests)
        Assert.Contains("not a base element", ElementFamilies.WhyNot(game.Catalog, Meshes + "SM_Statue"), StringComparison.Ordinal);
        Assert.All(new[] { Dlc, Wall }, s => Assert.Null(ElementFamilies.Of(game.Catalog, s)));

        // Planned anyway, each is left out with its reason; a source that now shows another element is left out too.
        var plan = CraftablesPlanner.Plan(game.Catalog, [Element("Wall", Wall, Meshes + "SM_Wall_Wood"), Element("Moved", ChestMesh)]);
        Assert.Empty(plan.Entries);
        Assert.Contains(plan.Warnings, w => w.StartsWith("Wall: left out", StringComparison.Ordinal) && w.Contains("modular", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Moved: left out", StringComparison.Ordinal) && w.Contains("add it again", StringComparison.Ordinal));
    }

    [Fact]
    public void ElementFromItsMesh()
    {
        using var game = new ElementFixture();
        Assert.Equal(Bed, ElementFamilies.Of(game.Catalog, BedMesh)?.Element);
        Assert.Equal(Chest, ElementFamilies.Of(game.Catalog, ChestMesh)?.Element);

        // An upgrade level, or its mesh, is the family from its start.
        Assert.Equal(Bed, ElementFamilies.Of(game.Catalog, BedUpgrade)?.Element);
        Assert.Equal(Bed, ElementFamilies.Of(game.Catalog, BedUpgradeMesh)?.Element);
        Assert.Equal(2, ElementFamilies.Of(game.Catalog, BedUpgradeMesh)?.Levels.Count);
    }

    [Fact]
    public void NewNamesKeepTheNameRules()
    {
        using var game = new ElementFixture();
        var plan = CraftablesPlanner.Plan(game.Catalog, [Element("Camp Chest", Chest, use: CraftUse.Both), Element("Camp Bed", Bed)]);
        var leaves = plan.Request.Clones.SelectMany(c => c.Packages).Select(p => PackageMap.Leaf(p.Value)).ToList();
        Assert.Equal(9, leaves.Count);
        Assert.All(leaves, leaf =>
        {
            Assert.DoesNotMatch(new Regex(@"_\d+$"), leaf); // the engine reads a trailing _<digits> as an FName number
            Assert.True(CloneFamilyPlanner.IsValidName(leaf), leaf);
        });
    }
}
