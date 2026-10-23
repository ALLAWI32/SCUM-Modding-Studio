using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Any item of the game as a recipe ingredient (owner: "what he pays for them also chosen in 3D, not typed"; CwHedgehog:
/// "Can I add items as new crafting materials?"): the item's own tag when it has one, else (only when the user allows it)
/// a new tag and that tag added to the item's stock entity setup.
/// </summary>
public sealed class CraftIngredientsTests : IDisposable
{
    private const string NewAkTag = RecipeIngredients.TagFolder + "/CI_SSI_Weapon_AK47";
    private readonly IngredientFixture _game = new();

    public void Dispose() => _game.Dispose();

    private AssetCatalog Catalog => _game.Catalog;

    private static Craftable Kit(string name, params CraftIngredient[] ingredients) =>
        new() { Name = name, Source = IngredientFixture.M16, Mesh = IngredientFixture.M16, Kind = CraftKind.Item, Ingredients = ingredients };

    /// <summary>Audit: 43 entity setups are not named after their item; their items never reached the gallery or the picker.</summary>
    [Fact]
    public void NonTwinEntitySetupsAreItems()
    {
        Assert.True(CraftablesPlanner.IsItem(Catalog, IngredientFixture.Feeder));
        Assert.Contains(IngredientFixture.Feeder, CraftablesPlanner.Items(Catalog));
        Assert.Equal(IngredientFixture.FeederSetup, ItemSetups.Of(Catalog).SetupOf(IngredientFixture.Feeder));
        Assert.False(CraftablesPlanner.IsItem(Catalog, IngredientFixture.FeederSetup));
        Assert.False(CraftablesPlanner.IsItem(Catalog, IngredientFixture.RifleSetup[..^3])); // a parent setup names no item
        Assert.Equal(IngredientMatch.NoTag, IngredientIndex.Of(Catalog).Resolve(IngredientFixture.Feeder).Match);
    }

    /// <summary>A picked item with a tag of its own is written as that tag; nothing of the game changes.</summary>
    [Fact]
    public void AnItemWithItsOwnTagBecomesThatTag()
    {
        var plan = CraftablesPlanner.Plan(Catalog, [Kit("Rifle Kit", new(string.Empty, 2, Item: IngredientFixture.LeadPlate), new(string.Empty, 1, IsTool: true, Item: IngredientFixture.M16))]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(
            new CraftIngredient[] { new("CI_Lead_Plate", 2, Item: IngredientFixture.LeadPlate), new("CI_Weapon_M16", 1, true, IngredientFixture.M16) },
            plan.Request.Ingredients[entry.Recipe]);
        Assert.Empty(plan.StockOverrides);
        Assert.Empty(plan.Request.IngredientInstances);
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("not a tag", StringComparison.Ordinal));
    }

    /// <summary>An item that only counts as a group (any rifle) or as nothing is left out unless the user allows a new tag.</summary>
    [Fact]
    public void GroupOnlyItemsAreLeftOutAndTheGroupsNamed()
    {
        var plan = CraftablesPlanner.Plan(Catalog, [Kit("Rifle Kit", new("CI_Lead_Plate", 1), new(string.Empty, 1, Item: IngredientFixture.Ak47), new(string.Empty, 1, Item: IngredientFixture.Feeder))]);

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(new CraftIngredient[] { new("CI_Lead_Plate", 1) }, plan.Request.Ingredients[entry.Recipe]);
        Assert.Contains(plan.Warnings, w => w.Contains("Weapon_AK47 has no ingredient tag of its own", StringComparison.Ordinal) && w.Contains("CI_Group_Rifles ('Rifles')", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.Contains("AnimalBaitFeederItem_Ridgeline (no ingredient tag lists it)", StringComparison.Ordinal));
        Assert.Empty(plan.StockOverrides);
        Assert.DoesNotContain(plan.Request.Clones, c => c.NewPrimary.Contains("/CI_SSI_", StringComparison.Ordinal));
        Assert.Equal(IngredientMatch.GroupOnly, IngredientIndex.Of(Catalog).Resolve(IngredientFixture.Ak47).Match);
    }

    /// <summary>
    /// Allowed, the AK-47 gets a tag of its own (a copy of <c>CI_Lead_Plate</c> showing the rifle, captioned like it) and its
    /// stock entity setup lists it after the group it inherited; two craftables that pick it share the one tag.
    /// </summary>
    [Fact]
    public void AddTagClonesATagAndAppendsTheEntitySetup()
    {
        var plan = CraftablesPlanner.Plan(Catalog,
        [
            Kit("Rifle Kit", new CraftIngredient(string.Empty, 3, Item: IngredientFixture.Ak47, AddTag: true)),
            Kit("Scrap Kit", new(string.Empty, 1, Item: IngredientFixture.Ak47, AddTag: true), new("CI_Lead_Plate", 2)),
        ]);

        Assert.Equal(2, plan.Entries.Count);
        Assert.Equal(new CraftIngredient[] { new("CI_SSI_Weapon_AK47", 3, Item: IngredientFixture.Ak47, AddTag: true) }, plan.Request.Ingredients[plan.Entries[0].Recipe]);
        var clone = Assert.Single(plan.Request.Clones, c => c.NewPrimary.Contains("/CI_SSI_", StringComparison.Ordinal));
        Assert.Equal((CraftablesPlanner.ItemTagTemplate, NewAkTag), (clone.Template, clone.NewPrimary));
        Assert.Equal([IngredientFixture.Ak47 + "_ES"], plan.StockOverrides);
        Assert.Equal([RecipeIngredients.TagPath("CI_SSI_Weapon_AK47")], plan.Request.IngredientInstances[IngredientFixture.Ak47 + "_ES"]);

        var built = AssetModBuilder.Build(Catalog, plan.Request);
        CookedPackage Built(string path, bool isClone)
        {
            var package = Assert.Single(built.Packages, p => string.Equals(p.PackagePath, path, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(isClone, package.IsClone);
            return CookedPackage.Parse(package.Bytes.UAsset, package.Bytes.UExp, package.UBulk, path);
        }

        // The new tag: a new package showing the rifle, captioned as the rifle's setup names it.
        Assert.False(Catalog.PackageExists(NewAkTag));
        var tag = Built(NewAkTag, isClone: true);
        var tagProps = tag.ReadProperties(Enumerable.Range(0, tag.Exports.Count).Single(i => tag.GetExportClassName(i) == "CraftingIngredientTag"));
        Assert.Equal(IngredientFixture.Ak47 + ".Weapon_AK47_C", Assert.IsType<SoftObjectValue>(tagProps.Find("UIClassRepresentative")!.Value).AssetPath);
        Assert.Equal("AK-47", Assert.IsType<TextValue>(tagProps.Find("ClassRepresentativeCaption")!.Value).SourceString);

        // The stock setup stored no list: it gets the inherited group first, then the new tag, each worth one.
        var es = Built(IngredientFixture.Ak47 + "_ES", isClone: false);
        var list = Assert.IsType<ArrayValue>(ItemSetupsDefaults(es).Find(IngredientInstances.Property)!.Value);
        Assert.Equal(
            [new IngredientInstance(RecipeIngredients.TagPath("CI_Group_Rifles"), 1), new IngredientInstance(RecipeIngredients.TagPath("CI_SSI_Weapon_AK47"), 1)],
            IngredientIndex.StoredInstances(es, list));
        Assert.Equal("AK-47", Assert.IsType<TextValue>(ItemSetupsDefaults(es).Find("Caption")!.Value).SourceString);

        // The recipes ask for the new tag.
        Assert.Equal([new CraftIngredient("CI_SSI_Weapon_AK47", 3)], RecipeIngredients.Read(Built(plan.Entries[0].Recipe, isClone: true)));
        Assert.Equal([new CraftIngredient("CI_SSI_Weapon_AK47", 1), new CraftIngredient("CI_Lead_Plate", 2)], RecipeIngredients.Read(Built(plan.Entries[1].Recipe, isClone: true)));
    }

    /// <summary>
    /// A setup that stores its list (the M16's) keeps it and gains one element at the end, whose Type is a new import of the
    /// tag (preloaded before the class default is read); every other export keeps its bytes, and a second append adds nothing.
    /// </summary>
    [Fact]
    public void AppendingKeepsTheStoredListAndEveryOtherExport()
    {
        var before = ModdableAssets.ReadPackage(Catalog, IngredientFixture.M16 + "_ES");
        var added = RecipeIngredients.TagPath("CI_SSI_Test");
        var bytes = IngredientInstances.Append(before, [added]);
        var after = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, before.BasePath);

        var list = Assert.IsType<ArrayValue>(ItemSetupsDefaults(after).Find(IngredientInstances.Property)!.Value);
        Assert.Equal(
            [new IngredientInstance(RecipeIngredients.TagPath("CI_Weapon_M16"), 1), new IngredientInstance(RecipeIngredients.TagPath("CI_Group_Rifles"), 1), new IngredientInstance(added, 1)],
            IngredientIndex.StoredInstances(after, list));
        var import = Assert.IsType<ObjectValue>(((StructValue)list.Items[^1]).Find("Type")!.Value).Index;
        Assert.Equal("CraftingIngredientTag", after.ResolveName(after.Imports[-import - 1].ClassName));
        Assert.Contains(import, after.ReadPreloadDependencies());
        var cdo = Enumerable.Range(0, after.Exports.Count).Single(i => after.ResolveName(after.Exports[i].ObjectName).StartsWith("Default__", StringComparison.Ordinal));
        Assert.All(Enumerable.Range(0, after.Exports.Count).Where(i => i != cdo), i => Assert.Equal(before.GetExportBytes(i), after.GetExportBytes(i)));

        var again = IngredientInstances.Append(after, [added]);
        var twice = CookedPackage.Parse(again.UAsset, again.UExp, null, before.BasePath);
        Assert.Equal(3, Assert.IsType<ArrayValue>(ItemSetupsDefaults(twice).Find(IngredientInstances.Property)!.Value).Items.Count);
    }

    /// <summary>P0 #1: the build only adds packages, except exactly the stock setups the user opted in to change.</summary>
    [Fact]
    public void OnlyTheOptedInEntitySetupIsAStockPath()
    {
        var plan = CraftablesPlanner.Plan(Catalog,
        [
            Kit("Rifle Kit", new(string.Empty, 1, Item: IngredientFixture.Ak47, AddTag: true), new(string.Empty, 1, Item: IngredientFixture.LeadPlate)),
            Kit("Plain Kit", new CraftIngredient(string.Empty, 1, Item: IngredientFixture.M16, AddTag: true)),
        ]);
        var built = AssetModBuilder.Build(Catalog, plan.Request);

        var atGamePaths = built.Packages.Where(p => Catalog.PackageExists(p.PackagePath)).Select(p => p.PackagePath).ToList();
        Assert.Equal(plan.StockOverrides, atGamePaths);
        Assert.Equal([IngredientFixture.Ak47 + "_ES"], atGamePaths); // the M16 has its own tag: its setup is not touched
        Assert.All(built.Packages.Where(p => !plan.StockOverrides.Contains(p.PackagePath)), p => Assert.True(p.IsClone, p.PackagePath));
    }

    /// <summary>
    /// The export writes the opted-in setup on the project pak's staged copy when that pak changed it (a Module-page edit),
    /// so the higher Craftables pak keeps that edit; without a staged copy it starts from the game's.
    /// </summary>
    [Fact]
    public async Task TheSetupKeepsTheProjectPaksEdits()
    {
        var output = Path.Combine(_game.Root, "out");
        var setupPath = IngredientFixture.Ak47 + "_ES";
        Assert.True(Catalog.TryGetPackageFile(setupPath, out var file));
        var staged = Path.Combine([ProjectExporter.ExportFolder(output, "MyMod", pages: false, ProjectSourceRole.Client), ProjectExporter.WorkFolderName, ProjectExporter.StagingFolderName, .. file.Path[..file.Path.LastIndexOf('.')].Split('/')]);
        var edited = TunablePatcher.Apply(ModdableAssets.ReadPackage(Catalog, setupPath), [new TunableEdit("Default__Weapon_AK47_ES_C", "Caption", "AK-47 Custom")]).Bytes;
        var craftables = new CraftablesFile { Items = [Kit("Rifle Kit", new CraftIngredient(string.Empty, 1, Item: IngredientFixture.Ak47, AddTag: true))] };

        async Task<CookedPackage> Export()
        {
            var result = await CraftablesExporter.ExportAsync(craftables, "MyMod", Catalog, new ExportOptions { OutputDirectory = output, WritePak = false }, ProjectSourceRole.Client);
            var asset = Assert.Single(result!.Assets, a => a.PackagePath == setupPath);
            Assert.False(asset.IsClone);
            var header = Path.Combine([result.StagingDirectory, .. asset.VirtualPath.Split('/')]);
            return CookedPackage.Parse(File.ReadAllBytes(header), File.ReadAllBytes(Path.ChangeExtension(header, ".uexp")), null, setupPath);
        }

        string Caption(CookedPackage p) => Assert.IsType<TextValue>(ItemSetupsDefaults(p).Find("Caption")!.Value).SourceString!;
        int Listed(CookedPackage p) => Assert.IsType<ArrayValue>(ItemSetupsDefaults(p).Find(IngredientInstances.Property)!.Value).Items.Count;

        var plain = await Export();
        Assert.Equal(("AK-47", 2), (Caption(plain), Listed(plain)));

        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await File.WriteAllBytesAsync(staged + ".uasset", edited.UAsset);
        await File.WriteAllBytesAsync(staged + ".uexp", edited.UExp);
        var kept = await Export();
        Assert.Equal(("AK-47 Custom", 2), (Caption(kept), Listed(kept)));
    }

    /// <summary>A new tag is a valid asset name, never ends in '_' and a number (an FName number) and is never a station's CI_SS_*.</summary>
    [Theory]
    [InlineData("/Game/A/Weapon_M16", "CI_SSI_Weapon_M16")]
    [InlineData("/Game/A/Improvised_Bow_20", "CI_SSI_Improvised_Bow_20_I")]
    [InlineData("/Game/A/12_Gauge_Birdshot", "CI_SSI_12_Gauge_Birdshot")]
    [InlineData("/Game/A/Wood-Plank_01.Wood-Plank_01_C", "CI_SSI_Wood_Plank_01_I")]
    [InlineData("/Game/A/__", "CI_SSI_Item")]
    [InlineData("/Game/A/A_Very_Long_Item_Name_That_Goes_On_And_On_Well_Past_The_Limit_7", null)]
    public void NewTagNamesKeepTheNameRules(string item, string? expected)
    {
        var name = IngredientIndex.NewTagName(item);
        if (expected is not null)
        {
            Assert.Equal(expected, name);
        }

        Assert.True(CloneFamilyPlanner.IsValidName(name), name);
        Assert.DoesNotMatch(new Regex("_[0-9]+$"), name);
        Assert.StartsWith("CI_SSI_", name, StringComparison.Ordinal);
        Assert.False(name.StartsWith("CI_SS_", StringComparison.Ordinal));
        Assert.Equal(name, IngredientIndex.NewTagName(item)); // stable
    }

    /// <summary>A project file written before items could be picked reads the same, and a picked item round-trips.</summary>
    [Fact]
    public void ItemIngredientsRoundTripInTheProjectFile()
    {
        const string old = """{ "Items": [ { "Name": "Stool", "Source": "/Game/A/SM_Stool", "Mesh": "/Game/A/SM_Stool", "Kind": "Furniture", "Ingredients": [ { "Tag": "CI_Plank", "Amount": 3, "IsTool": false }, { "Tag": "CI_Group_Blade", "Amount": 1, "IsTool": true } ] } ] }""";
        var file = CraftablesFile.Parse(old);
        var stool = Assert.Single(file.Items);
        Assert.Equal(new CraftIngredient[] { new("CI_Plank", 3), new("CI_Group_Blade", 1, true) }, stool.Ingredients);
        Assert.Null(stool.Element);
        Assert.Null(stool.Use);
        Assert.DoesNotContain("\"Item\"", file.ToJson(), StringComparison.Ordinal);

        var picked = file with { Items = [stool with { Ingredients = [new(string.Empty, 2, Item: IngredientFixture.Ak47, AddTag: true)], Use = CraftUse.Both }] };
        var back = Assert.Single(CraftablesFile.Parse(picked.ToJson()).Items);
        Assert.Equal(new CraftIngredient[] { new(string.Empty, 2, Item: IngredientFixture.Ak47, AddTag: true) }, back.Ingredients);
        Assert.Equal(CraftUse.Both, back.Use);
    }

    private static PropertyBlock ItemSetupsDefaults(CookedPackage package) =>
        package.ReadProperties(Enumerable.Range(0, package.Exports.Count).Single(i => package.ResolveName(package.Exports[i].ObjectName).StartsWith("Default__", StringComparison.Ordinal)));

    /// <summary>
    /// Review (craft2): an item setup an imported mod overrides is not the game's own, so its new tag is refused with a
    /// warning and nothing is shipped at a stock path (another mod's bytes are never shipped).
    /// </summary>
    [Fact]
    public void AnEntitySetupAModOverridesIsNotTheGames()
    {
        var overlay = Path.Combine(_game.Root, "mod");
        var file = Path.Combine([_game.Root, "SCUM", "Content", .. (IngredientFixture.Ak47 + "_ES")["/Game/".Length..].Split('/')]);
        var copy = Path.Combine([overlay, "SCUM", "Content", .. (IngredientFixture.Ak47 + "_ES")["/Game/".Length..].Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(file + ".uasset", copy + ".uasset");
        File.Copy(file + ".uexp", copy + ".uexp");
        using var modded = AssetCatalog.OpenLoose(_game.Root, new AssetCatalogOptions { LooseOverlays = [overlay] });

        var plan = CraftablesPlanner.Plan(modded, [Kit("Rifle Kit", new CraftIngredient(string.Empty, 1, Item: IngredientFixture.Ak47, AddTag: true))]);

        Assert.Empty(plan.StockOverrides);
        Assert.Contains(plan.Warnings, w => w.Contains("is not the game's own", StringComparison.Ordinal));
        Assert.Single(CraftablesPlanner.Plan(Catalog, [Kit("Rifle Kit", new CraftIngredient(string.Empty, 1, Item: IngredientFixture.Ak47, AddTag: true))]).StockOverrides);
    }

    /// <summary>
    /// Jenni (2026-10-13): every craftable showed its template's picture. The copied recipe shows the item's own inventory
    /// icon, or the picture the user picked; a picture the game does not ship is refused with a warning.
    /// </summary>
    [Fact]
    public void TheRecipeShowsTheItemsOwnPictureOrThePickedOne()
    {
        string? IconOf(Craftable craftable, out CraftablesPlan plan)
        {
            plan = CraftablesPlanner.Plan(Catalog, [craftable]);
            var recipe = plan.Entries.Single().Recipe;
            var package = AssetModBuilder.Build(Catalog, plan.Request).Packages.Single(p => p.PackagePath == recipe);
            var cooked = CookedPackage.Parse(package.Bytes.UAsset, package.Bytes.UExp, package.UBulk, recipe);
            return (cooked.ReadProperties(RecipeIngredients.RecipeExport(cooked)).Find(CraftablesPlanner.IconProperty)?.Value as SoftObjectValue)?.AssetPath;
        }

        var kit = Kit("M16 Kit", new CraftIngredient("CI_Lead_Plate", 1));
        Assert.Equal(IngredientFixture.RecipeIcon, CraftablesPlanner.RecipeIcon(Catalog, IngredientFixture.Recipe));
        Assert.Equal(IngredientFixture.M16Icon, CraftablesPlanner.OwnIcon(Catalog, kit));
        Assert.Equal(IngredientFixture.M16Icon, IconOf(kit, out _));

        var picked = IngredientFixture.LeadPlate + "." + PackageMap.Leaf(IngredientFixture.LeadPlate);
        Assert.Equal(picked, IconOf(kit with { Icon = IngredientFixture.LeadPlate }, out _));

        Assert.Equal(IngredientFixture.M16Icon, IconOf(kit with { Icon = "/Game/ConZ_Files/Missing/ICO_Nothing" }, out var refused));
        Assert.Contains(refused.Warnings, w => w.Contains("ICO_Nothing is not in the game files", StringComparison.Ordinal));

        var file = CraftablesFile.Parse(new CraftablesFile { Items = [kit with { Icon = IngredientFixture.LeadPlate }] }.ToJson());
        Assert.Equal(IngredientFixture.LeadPlate, file.Items.Single().Icon);
    }
}
