using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Crafting;
using ScumStudio.Tests.Formats;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Materials per skill level (CwHedgehog: "adjust the materials required for each level of Engineering/Survival"): a
/// craftable's ingredient written with the user's five amounts, a game recipe's amounts set in place, and the file that
/// keeps both (an old file reads and writes as before).
/// </summary>
public sealed class SkillLevelsTests
{
    [Fact]
    public void RewriteWritesTheUsersAmountPerSkillLevel()
    {
        var package = CraftCategoriesTests.Recipe(new SyntheticPackage());
        CraftIngredient[] ingredients = [new("CI_Plank", 12, Levels: new LevelAmounts(12, 10, 8, 4, 2)), new("CI_Wood", 9)];

        var bytes = RecipeIngredients.Rewrite(package, ingredients);
        var written = RecipeIngredients.ReadSlots(CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, package.BasePath));

        Assert.Equal([new LevelAmounts(12, 10, 8, 4, 2), LevelAmounts.Auto(9)], written.Select(s => s.Amount));
        Assert.Equal(new LevelAmounts(9, 9, 7, 6, 5), LevelAmounts.Auto(9)); // the game's ratio when no level is set
        Assert.Equal(["CI_Plank"], written[0].Tags);
    }

    [Fact]
    public void ANoughtOrNegativeLevelIsWrittenAsOne()
    {
        Assert.Equal(new LevelAmounts(3, 1, 1, 1, 1), new CraftIngredient("CI_Plank", 3, Levels: new LevelAmounts(3, 0, -4, 0, 1)).PerLevel);
        Assert.Equal(new LevelAmounts(1, 1, 1, 1, 1), new CraftIngredient("CI_Plank", 0).PerLevel);
    }

    [Fact]
    public void SetAmountsChangesOnlyTheAmounts()
    {
        var package = CraftCategoriesTests.Recipe(new SyntheticPackage());
        Assert.Equal([new LevelAmounts(4, 4, 4, 4, 4), new LevelAmounts(2, 2, 2, 2, 2)], RecipeIngredients.ReadSlots(package).Select(s => s.Amount));

        var bytes = RecipeIngredients.SetAmounts(package, [new LevelAmounts(40, 30, 20, 15, 10), new LevelAmounts(5, 4, 3, 2, 0)]);
        var changed = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, package.BasePath);

        Assert.Equal([new LevelAmounts(40, 30, 20, 15, 10), new LevelAmounts(5, 4, 3, 2, 1)], RecipeIngredients.ReadSlots(changed).Select(s => s.Amount));

        // Same size, same tables; only the ten ints differ (AdditionalAmount, tags, purpose, the section and the skin stay).
        var before = package.GetExportData(0).ToArray();
        var after = changed.GetExportData(0).ToArray();
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(package.Names, changed.Names);
        Assert.Equal(package.Imports.Count, changed.Imports.Count);
        var differing = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
        Assert.NotEmpty(differing);
        Assert.All(differing, i => Assert.True(IsAmountByte(changed, i), $"byte {i} is not an amount"));
        Assert.Equal(package.GetExportData(1).ToArray(), changed.GetExportData(1).ToArray());
        var slots = Assert.IsType<ArrayValue>(changed.ReadProperties(0).Find("Ingredients")!.Value).Items.Cast<StructValue>();
        Assert.All(slots, s => Assert.Equal(1, Assert.IsType<IntValue>(Assert.IsType<StructValue>(s.Properties.Single(p => p.Name == "AdditionalAmount").Value).Properties[0].Value).Value));
    }

    [Fact]
    public void SetAmountsRefusesAnotherNumberOfSlots()
    {
        var package = CraftCategoriesTests.Recipe(new SyntheticPackage());
        var refused = Assert.Throws<InvalidOperationException>(() => RecipeIngredients.SetAmounts(package, [new LevelAmounts(1, 1, 1, 1, 1)]));
        Assert.Contains("2 ingredient slot(s), not 1", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileKeepsLevelsAndOverridesAndAnOldFileStaysAsItWas()
    {
        const string old = """{ "Items": [ { "Name": "Chair", "Token": "SS_Chair", "Source": "/Game/A", "Mesh": "/Game/A", "Ingredients": [ { "Tag": "CI_Plank", "Amount": 3 } ] } ] }""";
        var parsed = CraftablesFile.Parse(old);
        Assert.Empty(parsed.RecipeOverrides);
        Assert.Null(parsed.Items[0].Ingredients[0].Levels);
        Assert.DoesNotContain("RecipeOverrides", parsed.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("Levels", parsed.ToJson(), StringComparison.Ordinal);
        Assert.DoesNotContain("PerLevel", parsed.ToJson(), StringComparison.Ordinal);

        var file = parsed with
        {
            Items = [parsed.Items[0] with { Ingredients = [new("CI_Plank", 6, Levels: new LevelAmounts(6, 5, 4, 3, 2))] }],
            RecipeOverrides = [new StockRecipeOverride { Recipe = "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/CR_Improvised_Workbench", Slots = [new("CI_Plank", new LevelAmounts(9, 8, 7, 6, 5))] }],
        };
        var again = CraftablesFile.Parse(file.ToJson());

        Assert.Equal(new LevelAmounts(6, 5, 4, 3, 2), again.Items[0].Ingredients[0].Levels);
        var entry = Assert.Single(again.RecipeOverrides);
        Assert.Equal(file.RecipeOverrides[0].Recipe, entry.Recipe);
        Assert.Equal(file.RecipeOverrides[0].Slots, entry.Slots);
        Assert.False(again.IsEmpty);
        Assert.False(new CraftablesFile { RecipeOverrides = file.RecipeOverrides }.IsEmpty); // an override alone is exported
        Assert.True(new CraftablesFile().IsEmpty);
    }

    // True for a byte of one of the slots' five Amount ints (a value written equal to the old one counts too).
    private static bool IsAmountByte(CookedPackage package, int offset) =>
        Assert.IsType<ArrayValue>(package.ReadProperties(0).Find("Ingredients")!.Value).Items.Cast<StructValue>()
            .Select(s => Assert.IsType<StructValue>(s.Properties.Single(p => p.Name == "Amount").Value))
            .SelectMany(a => a.Properties).Any(p => offset >= p.ValueOffset && offset < p.ValueOffset + 4);
}
