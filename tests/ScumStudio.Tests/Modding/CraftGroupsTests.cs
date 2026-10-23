using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The project's ingredient groups (r40, igor8802: "create, change, and view groups of ingredients"; redriver6155: "maybe let us
/// create new groups?") without the paks: the tag a name gives, the file, and the plan: a group a recipe asks for is a new tag
/// every member's stock entity setup lists; an unused group ships nothing; a member that cannot carry it is named and left out.
/// </summary>
public sealed class CraftGroupsTests : IDisposable
{
    private const string Group = "CI_Group_Cutting_Kit";
    private readonly IngredientFixture _game = new();

    public void Dispose() => _game.Dispose();

    [Theory]
    [InlineData("Cutting Kit", "CI_Group_Cutting_Kit")]
    [InlineData("  Tools / 2 ", "CI_Group_Tools_2_G")] // never an FName number
    [InlineData("Набор", "CI_Group_Custom")] // no Latin letter left
    [InlineData("Rifles", "CI_Group_Rifles_v2")] // the game has CI_Group_Rifles
    [InlineData("rifles 2", "CI_Group_rifles_2_G")]
    public void ANameGivesAValidTagNoOtherHas(string name, string tag)
    {
        var made = CraftGroups.TagFor(name, ["CI_Group_Rifles", "CI_Lead_Plate"]);
        Assert.Equal(tag, made);
        Assert.True(CraftGroups.IsValidTag(made), made);
        Assert.True(CloneFamilyPlanner.IsValidName(made));
        Assert.Equal("CI_Group_Cutting_Kit_v3", CraftGroups.TagFor("Cutting Kit", ["ci_group_cutting_kit", "CI_Group_Cutting_Kit_v2"]));
        Assert.True(CraftGroups.TagFor(new string('A', 200), []).Length <= CraftGroups.Prefix.Length + CraftGroups.MaxNameInTag);
        Assert.False(CraftGroups.IsValidTag("CI_Lead_Plate"));
        Assert.False(CraftGroups.IsValidTag("CI_Group_Kit_2"));
    }

    /// <summary>A file written before groups reads with none and writes none; groups round-trip.</summary>
    [Fact]
    public void GroupsRoundTripInTheProjectFile()
    {
        const string old = """{ "Items": [ { "Name": "Stool", "Source": "/Game/A/SM_Stool", "Mesh": "/Game/A/SM_Stool", "Ingredients": [ { "Tag": "CI_Plank", "Amount": 3 } ] } ] }""";
        var file = CraftablesFile.Parse(old);
        Assert.Empty(file.Groups);
        Assert.DoesNotContain("Groups", file.ToJson(), StringComparison.Ordinal);

        var grouped = file with { Groups = [new CraftGroup("Cutting Kit", Group, [IngredientFixture.Ak47, IngredientFixture.M16])] };
        Assert.Equal(grouped.Groups, CraftablesFile.Parse(grouped.ToJson()).Groups);
        Assert.True(CraftablesFile.Parse(grouped.ToJson()).Groups[0].Has(IngredientFixture.Ak47.ToUpperInvariant()));
        var stool = file.Items[0];
        Assert.Equal(["Stool"], CraftGroups.UsedBy([stool with { Ingredients = [new(Group, 1)] }], Group));
        Assert.Empty(CraftGroups.UsedBy([stool with { Ingredients = [new(Group, 1, Item: IngredientFixture.Ak47)] }], Group)); // an item, not the group
    }

    /// <summary>
    /// A recipe asks for the group: one new tag (a copy of the whole-items template: these items count no uses), captioned with
    /// the group's name and showing its first item; the AK-47's setup (inheriting its list) and the M16's (storing one) list it,
    /// the feeder too; the M16 also keeps its own tag. A path that is no item is named and left out.
    /// </summary>
    [Fact]
    public void ARecipeAskingForAGroupGivesItsTagToEveryMember()
    {
        var group = new CraftGroup("Cutting Kit", Group, [IngredientFixture.Ak47, IngredientFixture.M16, IngredientFixture.Feeder, "/Game/ConZ_Files/Nothing/Here"]);
        var plan = CraftablesPlanner.Plan(_game.Catalog, [Kit("Rifle Kit", new CraftIngredient(Group, 2), new("CI_Lead_Plate", 1))], groups: [group]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(new CraftIngredient[] { new(Group, 2), new("CI_Lead_Plate", 1) }, plan.Request.Ingredients[entry.Recipe]);
        var clone = Assert.Single(plan.Request.Clones, c => c.NewPrimary.EndsWith("/" + Group, StringComparison.Ordinal));
        Assert.Equal((CraftablesPlanner.ItemTagTemplate, RecipeIngredients.TagFolder + "/" + Group), (clone.Template, clone.NewPrimary));
        Assert.Contains(plan.Warnings, w => w.Contains("Group 'Cutting Kit': /Game/ConZ_Files/Nothing/Here is not an item of the game", StringComparison.Ordinal));
        var setups = new[] { IngredientFixture.Ak47 + "_ES", IngredientFixture.M16 + "_ES", IngredientFixture.FeederSetup };
        Assert.Equal(setups.Order(StringComparer.OrdinalIgnoreCase), plan.StockOverrides.Order(StringComparer.OrdinalIgnoreCase));
        Assert.All(setups, s => Assert.Equal([RecipeIngredients.TagPath(Group)], plan.Request.IngredientInstances[s]));

        var built = AssetModBuilder.Build(_game.Catalog, plan.Request);
        CookedPackage Built(string path) =>
            built.Packages.Single(p => string.Equals(p.PackagePath, path, StringComparison.OrdinalIgnoreCase)) is var p ? CookedPackage.Parse(p.Bytes.UAsset, p.Bytes.UExp, p.UBulk, path) : null!;

        var tag = Built(RecipeIngredients.TagFolder + "/" + Group);
        var props = tag.ReadProperties(Enumerable.Range(0, tag.Exports.Count).Single(i => tag.GetExportClassName(i) == "CraftingIngredientTag"));
        Assert.Equal("Cutting Kit", Assert.IsType<TextValue>(props.Find("ClassRepresentativeCaption")!.Value).SourceString);
        Assert.Equal(IngredientFixture.Ak47 + ".Weapon_AK47_C", Assert.IsType<SoftObjectValue>(props.Find("UIClassRepresentative")!.Value).AssetPath);
        Assert.Equal([new IngredientInstance(RecipeIngredients.TagPath("CI_Group_Rifles"), 1), new IngredientInstance(RecipeIngredients.TagPath(Group), 1)], Listed(Built(IngredientFixture.Ak47 + "_ES")));
        Assert.Equal(
            [new IngredientInstance(RecipeIngredients.TagPath("CI_Weapon_M16"), 1), new IngredientInstance(RecipeIngredients.TagPath("CI_Group_Rifles"), 1), new IngredientInstance(RecipeIngredients.TagPath(Group), 1)],
            Listed(Built(IngredientFixture.M16 + "_ES")));
        Assert.Equal([new IngredientInstance(RecipeIngredients.TagPath(Group), 1)], Listed(Built(IngredientFixture.FeederSetup)));
        Assert.Equal([new CraftIngredient(Group, 2), new CraftIngredient("CI_Lead_Plate", 1)], RecipeIngredients.Read(Built(entry.Recipe)));
    }

    /// <summary>An item given its own new tag and put in a group lists both, in one write of its setup.</summary>
    [Fact]
    public void AnItemWithItsOwnNewTagAndAGroupListsBoth()
    {
        var plan = CraftablesPlanner.Plan(_game.Catalog,
            [Kit("Rifle Kit", new CraftIngredient(string.Empty, 1, Item: IngredientFixture.Ak47, AddTag: true), new(Group, 1))],
            groups: [new CraftGroup("Cutting Kit", Group, [IngredientFixture.Ak47])]);

        Assert.Equal([IngredientFixture.Ak47 + "_ES"], plan.StockOverrides);
        Assert.Equal([RecipeIngredients.TagPath("CI_SSI_Weapon_AK47"), RecipeIngredients.TagPath(Group)], plan.Request.IngredientInstances[IngredientFixture.Ak47 + "_ES"]);
        Assert.Equal(2, plan.Request.Clones.Count(c => c.NewPrimary.StartsWith(RecipeIngredients.TagFolder + "/", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A group no recipe asks for ships nothing; one whose tag the game has, or with no member that can carry it, is left out of
    /// the recipe with the reason (an item craftable without any ingredient left is left out).
    /// </summary>
    [Fact]
    public void UnusedOrImpossibleGroupsShipNothing()
    {
        var unused = CraftablesPlanner.Plan(_game.Catalog, [Kit("Rifle Kit", new CraftIngredient("CI_Lead_Plate", 1))], groups: [new CraftGroup("Cutting Kit", Group, [IngredientFixture.Ak47])]);
        Assert.Empty(unused.StockOverrides);
        Assert.DoesNotContain(unused.Request.Clones, c => c.NewPrimary.Contains(Group, StringComparison.Ordinal));

        var taken = CraftablesPlanner.Plan(_game.Catalog, [Kit("Rifle Kit", new CraftIngredient("CI_Group_Rifles", 1), new("CI_Lead_Plate", 1))],
            groups: [new CraftGroup("Rifles", "CI_Group_Rifles", [IngredientFixture.M16])]);
        Assert.Contains(taken.Warnings, w => w.Contains("the group 'Rifles' (CI_Group_Rifles) is left out of the recipe", StringComparison.Ordinal) && w.Contains("already exists in the game files", StringComparison.Ordinal));
        Assert.Equal([new CraftIngredient("CI_Lead_Plate", 1)], taken.Request.Ingredients[Assert.Single(taken.Entries).Recipe]);
        Assert.Empty(taken.StockOverrides);

        var empty = CraftablesPlanner.Plan(_game.Catalog, [Kit("Rifle Kit", new CraftIngredient(Group, 1))], groups: [new CraftGroup("Cutting Kit", Group, ["/Game/ConZ_Files/Nothing/Here"])]);
        Assert.Empty(empty.Entries);
        Assert.Contains(empty.Warnings, w => w.Contains("none of its items can carry its tag", StringComparison.Ordinal));
    }

    /// <summary>The template follows the items: whole items here (no uses), and the registry gets a record of each new tag.</summary>
    [Fact]
    public void TheTemplateFollowsTheItems()
    {
        var index = IngredientIndex.Of(_game.Catalog);
        Assert.False(index.CountsUses(IngredientFixture.Ak47));
        Assert.Equal(CraftablesPlanner.ItemTagTemplate, CraftablesPlanner.TagTemplateFor(index, [IngredientFixture.Ak47, IngredientFixture.M16]));
        Assert.Equal(CraftablesPlanner.ItemTagTemplate, CraftablesPlanner.TagTemplateFor(index, []));
        Assert.Equal([IngredientFixture.LeadPlate], index.Members("CI_Lead_Plate"));
        Assert.Equal(new[] { IngredientFixture.Ak47, IngredientFixture.M16 }.Order(StringComparer.OrdinalIgnoreCase), index.Members("CI_Group_Rifles").Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(NewTagRefusal.NotItem, CraftablesPlanner.NewTagRefusalOf(_game.Catalog, index, "/Game/ConZ_Files/Nothing/Here"));
        Assert.Equal(NewTagRefusal.None, CraftablesPlanner.NewTagRefusalOf(_game.Catalog, index, IngredientFixture.Ak47));
    }

    private static Craftable Kit(string name, params CraftIngredient[] ingredients) =>
        new() { Name = name, Source = IngredientFixture.M16, Mesh = IngredientFixture.M16, Kind = CraftKind.Item, Ingredients = ingredients };

    private static IReadOnlyList<IngredientInstance> Listed(CookedPackage setup) =>
        IngredientIndex.StoredInstances(setup, Assert.IsType<ArrayValue>(ItemSetups.ClassDefaults(setup)!.Find(IngredientInstances.Property)!.Value));
}
