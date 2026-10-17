using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding;
using ScumStudio.Modding.Crafting;
using static ScumStudio.Tests.Modding.ElementFixture;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Owner (2026-10-17, his "Base Cabin 03"): a base element the game ships without a recipe is built as itself (a copy of the
/// chair's recipe makes it), "Needs a flag" writes <c>_canBePlacedWithoutFlag</c>, and a large furniture craftable is placed
/// like the game's large buildings (synthetic game, <see cref="ElementFixture"/>; the real cabin in
/// <see cref="CraftBuildPlaceRealTests"/>).
/// </summary>
public sealed class CraftBuildPlaceTests
{
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

    [Fact]
    public void AnElementWithoutARecipeBorrowsTheChairsAndIsBuiltAsItself()
    {
        using var game = new ElementFixture();
        var family = ElementFamilies.Of(game.Catalog, Lonely);
        Assert.NotNull(family);
        Assert.True(family!.BorrowsRecipe);
        Assert.Equal(ChairRecipe, family.Recipe);
        Assert.Equal([Lonely], family.Levels.Select(l => l.Element));
        Assert.False(ElementFamilies.Of(game.Catalog, Bed)!.BorrowsRecipe);

        var statue = new Craftable
        {
            Name = "Garden Statue",
            Source = Lonely,
            Mesh = Meshes + "SM_Statue",
            Kind = CraftKind.Element,
            Element = new ElementSettings { Template = Lonely },
            Ingredients = [new("CI_Plank", 5)],
        };
        var plan = CraftablesPlanner.Plan(game.Catalog, [statue]);
        var entry = Assert.Single(plan.Entries);
        var clone = Assert.Single(plan.Request.Clones);
        Assert.Equal([ChairRecipe, Lonely], clone.Packages.Select(p => p.Key));
        Assert.Equal(Placeables + "CR_SS_Garden_Statue", entry.Recipe);
        Assert.Equal(Elements + "BP_SS_Garden_Statue", entry.Product);
        Assert.Contains(new KeyValuePair<string, string>(Chair, Elements + "BP_SS_Garden_Statue"), clone.Redirects);

        var result = AssetModBuilder.Build(game.Catalog, plan.Request);
        var recipe = Built(result, entry.Recipe);
        Assert.Equal(Elements + "BP_SS_Garden_Statue.BP_SS_Garden_Statue_C",
            Assert.IsType<SoftObjectValue>(recipe.ReadProperties(RecipeIngredients.RecipeExport(recipe)).Find("Product")!.Value).AssetPath);
        Assert.Equal([new CraftIngredient("CI_Plank", 5)], RecipeIngredients.Read(recipe));
        var element = Defaults(Built(result, entry.Product));
        Assert.Equal(100f, Assert.IsType<FloatValue>(element.Find("_healthPoints")!.Value).Value); // the statue's, not the chair's 300
        Assert.Equal("Garden Statue", Assert.IsType<TextValue>(element.Find("_name")!.Value).SourceString);
        Assert.Null(CraftablesPlanner.OwnIcon(game.Catalog, statue)); // the chair's picture is not the statue's
    }

    [Fact]
    public void NeedsAFlagWritesTheElementsFlagOnEveryLevel()
    {
        using var game = new ElementFixture();
        Craftable Bed(bool? needs) => new()
        {
            Name = "Camp Bed",
            Source = ElementFixture.Bed,
            Mesh = BedMesh,
            Kind = CraftKind.Element,
            Element = new ElementSettings { Template = ElementFixture.Bed, Levels = [new Dictionary<string, string> { ["_canBePlacedWithoutFlag"] = "false" }] },
            Ingredients = [new("CI_Plank", 6)],
            NeedsFlag = needs,
        };

        // The game's (a C++ default of false): nothing to write; the craftable's switch wins over a level's old value.
        var same = CraftablesPlanner.Plan(game.Catalog, [Bed(true)]);
        Assert.DoesNotContain(same.Request.Edits.Values.SelectMany(e => e), e => e.Path == "_canBePlacedWithoutFlag");

        var plan = CraftablesPlanner.Plan(game.Catalog, [Bed(false)]);
        Assert.All(new[] { "BP_SS_Camp_Bed", "BP_SS_Camp_Bed_L1" }, leaf =>
            Assert.Single(plan.Request.Edits[Elements + leaf], e => e.Path == "_canBePlacedWithoutFlag" && e.Value == "true"));
        var result = AssetModBuilder.Build(game.Catalog, plan.Request);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("_canBePlacedWithoutFlag", StringComparison.Ordinal));
        foreach (var leaf in new[] { "BP_SS_Camp_Bed", "BP_SS_Camp_Bed_L1" })
        {
            var tag = Defaults(Built(result, Elements + leaf)).Find("_canBePlacedWithoutFlag")!;
            Assert.Equal("BoolProperty", tag.Type);
            Assert.True(Assert.IsType<BoolValue>(tag.Value).Value);
        }

        // The stock bed is not touched.
        Assert.DoesNotContain(result.Packages, p => string.Equals(p.PackagePath, ElementFixture.Bed, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ALargeFurnitureIsPlacedLikeTheGamesLargeBuildings()
    {
        using var game = new ElementFixture();
        Craftable Cabin(float size) => new()
        {
            Name = "Log House",
            Source = OtherMesh,
            Mesh = OtherMesh,
            Kind = CraftKind.Furniture,
            SizeMeters = size,
            Ingredients = [new("CI_Plank", 50)],
        };

        var small = CraftablesPlanner.Plan(game.Catalog, [Cabin(1.2f)]);
        Assert.DoesNotContain(small.Request.Edits.Values.SelectMany(e => e), e => e.Path is "_placementHeightType" or "_mustBePlacedUpright");

        var plan = CraftablesPlanner.Plan(game.Catalog, [Cabin(15)]);
        var entry = Assert.Single(plan.Entries);
        var result = AssetModBuilder.Build(game.Catalog, plan.Request);
        var defaults = Defaults(Built(result, entry.Product));
        var height = defaults.Find("_placementHeightType")!;
        Assert.Equal("EnumProperty", height.Type);
        Assert.Equal(CraftablesPlanner.HighestPointHeight, Assert.IsType<EnumValue>(height.Value).Value);
        Assert.True(Assert.IsType<BoolValue>(defaults.Find("_mustBePlacedUpright")!.Value).Value);
        Assert.Equal(300f, Assert.IsType<FloatValue>(defaults.Find("_healthPoints")!.Value).Value);

        // The game's reader (CUE4Parse) reads the new tags.
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-buildplace-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var package in result.Packages)
            {
                await package.Bytes.WriteAsync(Path.Combine(dir, Path.ChangeExtension(package.HeaderFilePath(), null)));
            }

            using var loose = AssetCatalog.OpenLoose(dir);
            var cdo = loose.LoadObject<UObject>($"{entry.Product}.Default__BP_SS_Log_House_C");
            Assert.Equal(CraftablesPlanner.HighestPointHeight, cdo.GetOrDefault<FName>("_placementHeightType").Text);
            Assert.True(cdo.GetOrDefault<bool>("_mustBePlacedUpright"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(1000f, 100f, 1000f)]
    [InlineData(1000f, 200f, 500f)]
    [InlineData(1000f, 50f, 2000f)]
    [InlineData(1000f, 0f, 1000f)]
    public void RepairCostScalesTheHealthOneUseRestores(float game, float percent, float expected) =>
        Assert.Equal(expected, CraftablesPlanner.HealthPerRepairUse(game, percent), 3);
}
