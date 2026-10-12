using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;
using ScumStudio.Tests.Formats;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The crafting menu sections of craftables (owner, 2026-10-12: "sofas/chairs/tables default to Furniture, houses to
/// Building"): labels, defaults and the class check on in-memory sections, the tag and the ingredients written into a
/// synthetic recipe, and (real game files, <c>SCUM_PAKS</c>) the sections read from the game's recipes and written into
/// the Craftables pak.
/// </summary>
public sealed class CraftCategoriesTests
{
    private const string Chairs = "CraftingCategory.Placeables.Furniture.Chairs";
    private const string Sofas = "CraftingCategory.Placeables.Furniture.Sofas";
    private const string Walls = "CraftingCategory.Placeables.BaseBuilding.Walls";
    private const string Benches = "CraftingCategory.Placeables.Crafting";
    private const string Melee = "CraftingCategory.Items.MeleeWeapons";
    private const string Misc = "CraftingCategory.Items.ToolsAndMisc.Misc";

    // Sections as the game's recipes would give them (the real tags of the placeables are read at run time).
    private static readonly IReadOnlyList<CraftCategory> Sections = CraftCategories.Merge(
    [
        Section(Chairs, true, "CR_Chair_Improvised_Wood"),
        Section(Chairs, true, "CR_Table_Improvised_Wood", 3),
        Section(Sofas, true, CraftCategories.SofaRecipe),
        Section(Walls, true, CraftCategories.WallRecipe),
        Section(Benches, true, "CR_Improvised_Workbench"),
        Section(Melee, false, "CR_Improvised_Knife"),
        Section(Misc, false, "CR_Small_Battery_Charger"),
    ]);

    [Fact]
    public void LabelsAreTidyEnglish()
    {
        // The item sections the planner's templates use (SCUM 1.3.3), as the crafting menu groups them.
        Assert.Equal("Ammunition › Arrows and bolts", CraftCategories.Label("CraftingCategory.Items.Ammunition.ArrowsAndBolts"));
        Assert.Equal("Ammunition › Bullets", CraftCategories.Label("CraftingCategory.Items.Ammunition.Bullets"));
        Assert.Equal("Ranged weapons › Attachments", CraftCategories.Label("CraftingCategory.Items.RangedWeapons.Attachments"));
        Assert.Equal("Ranged weapons › Bows and crossbows", CraftCategories.Label("CraftingCategory.Items.RangedWeapons.BowsAndCrossbows"));
        Assert.Equal("Ranged weapons › Firearms", CraftCategories.Label("CraftingCategory.Items.RangedWeapons.Firearms"));
        Assert.Equal("Melee weapons", CraftCategories.Label(Melee));
        Assert.Equal("Clothing › Clothes and accessories", CraftCategories.Label("CraftingCategory.Items.Clothing.ClothesAndAccessories"));
        Assert.Equal("Fishing", CraftCategories.Label("CraftingCategory.Items.Fishing"));
        Assert.Equal("Explosives", CraftCategories.Label("CraftingCategory.Items.Explosives"));
        Assert.Equal("Tools and misc › Misc", CraftCategories.Label(Misc));

        // Any other tag in plain words; acronyms stay.
        Assert.Equal("Base building › Walls", CraftCategories.Label(Walls));
        Assert.Equal("Base building", CraftCategories.Group(Walls));
        Assert.Equal("Sofas WFP", CraftCategories.Humanize("SofasWFP"));
        Assert.Equal("Crafting", CraftCategories.Label("CraftingCategory.Crafting"));

        // Building sections first, then by label; a tag's recipes are merged.
        Assert.Equal([Walls, Benches, Chairs, Sofas, Melee, Misc], Sections.Select(s => s.Tag));
        var chairs = Sections.Single(s => s.Tag == Chairs);
        Assert.Equal(["CR_Chair_Improvised_Wood", "CR_Table_Improvised_Wood"], chairs.Recipes);
        Assert.Equal(1, chairs.Priority);
    }

    [Fact]
    public void DefaultsFollowWhatTheCraftableIs()
    {
        Assert.Equal(Sofas, DefaultOf(Craftable("/Game/ConZ_Files/Models/Objects/Indoor/Sofa/SM_Sofa_Leather_01")));
        Assert.Equal(Chairs, DefaultOf(Craftable("/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01")));
        Assert.Equal(Walls, DefaultOf(Craftable("/Game/ConZ_Files/Models/Buildings/Village/House_01/SM_House_01")));
        Assert.True(CraftCategories.HasOwnDefault(Craftable("/Game/X/SM_Couch")));
        Assert.False(CraftCategories.HasOwnDefault(Craftable("/Game/X/SM_Table")));

        // Only furniture moves: a station, power and an item stay in their template's section.
        Assert.Equal(Benches, DefaultOf(Craftable("/Game/ConZ_Files/Models/Buildings/SM_Workshop", CraftKind.Station), CraftablesPlanner.StationRecipe));
        Assert.Equal(Misc, DefaultOf(Craftable("/Game/X/SM_Solar", CraftKind.Power), CraftablesPlanner.PowerRecipe));
        Assert.Equal(Melee, DefaultOf(Craftable("/Game/ConZ_Files/Items/Weapons/1H_Knife", CraftKind.Item), "/Game/ConZ_Files/Items/Crafting/Recipes/Items/CR_Improvised_Knife"));

        // A catalog without the wall keeps a house with its template; one without the chair has no default at all.
        var noWall = Sections.Where(s => s.Tag != Walls).ToList();
        Assert.Equal(Chairs, CraftCategories.DefaultFor(Craftable("/Game/ConZ_Files/Models/Buildings/SM_House"), noWall, CraftablesPlanner.FurnitureRecipe)?.Tag);
        Assert.Null(CraftCategories.DefaultFor(Craftable("/Game/X/SM_Table"), Sections.Where(s => s.Tag != Chairs).ToList(), CraftablesPlanner.FurnitureRecipe));
    }

    [Fact]
    public void AnItemSectionIsRefusedForAPlacedCraftable()
    {
        var sofa = Craftable("/Game/X/SM_Sofa") with { Name = "Red Sofa" };
        var refusal = CraftCategories.Refusal(sofa, Melee, Sections);
        Assert.Equal($"Red Sofa: the section {Melee} is not one of the game's building sections; it shows in its default section.", refusal);
        Assert.Null(CraftCategories.Refusal(sofa, Walls, Sections));
        Assert.Null(CraftCategories.Refusal(sofa with { Kind = CraftKind.Station }, Chairs, Sections));
        Assert.Contains("crafting sections", CraftCategories.Refusal(sofa with { Kind = CraftKind.Power }, Walls, Sections), StringComparison.Ordinal);
        Assert.Null(CraftCategories.Refusal(sofa with { Kind = CraftKind.Item }, Misc, Sections));
        Assert.NotNull(CraftCategories.Refusal(sofa, "CraftingCategory.Placeables.MyOwnSection", Sections)); // no new section names
    }

    [Fact]
    public void SetCategorySwapsTheTagName()
    {
        var synthetic = new SyntheticPackage();
        var package = Recipe(synthetic);
        var before = CraftCategories.Of(package);
        Assert.NotNull(before);
        Assert.Equal(Chairs, before!.Tag);
        Assert.True(before.Placeable);
        Assert.Equal(7, before.Priority);
        Assert.Equal("Furniture", before.Domain);
        Assert.Equal(["CR_Test"], before.Recipes);

        // A section of another domain, and a tag the engine stores with a number ("Tag_2" = "Tag" number 3).
        var walls = Sections.Single(s => s.Tag == Walls) with { Domain = "BaseBuilding" };
        var bytes = RecipeIngredients.SetCategory(package, walls);
        var moved = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, package.BasePath);
        var after = CraftCategories.Of(moved);
        Assert.Equal(Walls, after?.Tag);
        Assert.Equal("BaseBuilding", after?.Domain);
        Assert.Equal(7, after?.Priority);
        Assert.Equal(package.GetExportData(0).ToArray(), moved.GetExportData(0).ToArray()); // the recipe itself is untouched
        Assert.Equal(package.GetExportData(1).Length, moved.GetExportData(1).Length); // same size

        var numbered = RecipeIngredients.SetCategory(moved, walls with { Tag = "CraftingCategory.Test_2", Domain = null });
        var again = CookedPackage.Parse(numbered.UAsset, numbered.UExp, null, package.BasePath);
        var tag = CraftCategories.TagNameValue(again.ReadProperties(1))!;
        Assert.Equal("CraftingCategory.Test_2", tag.Value);
        Assert.Equal(new FNameRef(again.Names.ToList().IndexOf("CraftingCategory.Test"), 3), tag.Ref);
        Assert.Equal("BaseBuilding", CraftCategories.Of(again)?.Domain); // no domain given: the stored one stays
    }

    /// <summary>
    /// What an export does to a copied recipe: the skin link dropped, a new ingredient list (new tags imported, a known one
    /// reused) and another section, all in one package whose tables both edits add to.
    /// </summary>
    [Fact]
    public void RewriteWritesTheIngredientsAndTheSectionTogether()
    {
        var package = Recipe(new SyntheticPackage());
        var walls = Sections.Single(s => s.Tag == Walls) with { Domain = "BaseBuilding" };
        CraftIngredient[] ingredients = [new("CI_Plank", 12, false), new("CI_Wood", 3, false), new("CI_Hammer", 1, true)];

        var bytes = RecipeIngredients.Rewrite(package, ingredients, walls);
        var moved = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, package.BasePath);

        Assert.Equal(ingredients, RecipeIngredients.Read(moved));
        var section = CraftCategories.Of(moved);
        Assert.Equal(Walls, section?.Tag);
        Assert.Equal("BaseBuilding", section?.Domain);
        Assert.Equal(7, section?.Priority);
        Assert.Equal(package.GetExportData(1).Length, moved.GetExportData(1).Length);

        // Amounts per skill level written, the rest of the template slot copied; only the section link is left.
        var recipe = moved.ReadProperties(0);
        var slots = Assert.IsType<ArrayValue>(recipe.Find("Ingredients")!.Value).Items.Cast<StructValue>().ToList();
        Assert.Equal(RecipeIngredients.PerSkill(12), Levels(slots[0], "Amount"));
        Assert.All(slots, slot => Assert.Equal([1, 1, 1, 1, 1], Levels(slot, "AdditionalAmount")));
        Assert.All(slots, slot => Assert.True(Assert.IsType<BoolValue>(slot.Properties.Single(p => p.Name == "ReturnOnUncraft").Value).Value));
        Assert.Equal([2], Assert.IsType<ArrayValue>(recipe.Find("CraftingMetadata")!.Value).Items.Cast<ObjectValue>().Select(o => o.Index));

        // One import per tag, each created before the recipe is read; the sub-objects keep their own dependencies.
        int Import(string tag) => -(moved.Imports.ToList().FindIndex(i => moved.ResolveName(i.ObjectName) == tag) + 1);
        Assert.Single(moved.Imports, i => moved.ResolveName(i.ObjectName) == "CI_Wood");
        var hammer = moved.Imports[-Import("CI_Hammer") - 1];
        Assert.Equal("CraftingIngredientTag", moved.ResolveName(hammer.ClassName));
        Assert.Equal(RecipeIngredients.TagFolder + "/CI_Hammer", moved.ResolveName(moved.Imports[-hammer.OuterIndex - 1].ObjectName));
        Assert.Equal([-2, Import("CI_Wood"), Import("CI_Plank"), Import("CI_Hammer")], PackageTables.Of(moved, 0).DependencyGroups(0)[1]);
        Assert.Equal(PackageTables.Of(package, 1).DependencyGroups(1), PackageTables.Of(moved, 1).DependencyGroups(1));
        Assert.Equal(PackageTables.Of(package, 2).DependencyGroups(2), PackageTables.Of(moved, 2).DependencyGroups(2));

        // Written again from what was read, the package is the same bytes: nothing is left half edited.
        var again = PackageWriter.Build(PackageWriter.ToBuildInput(moved));
        Assert.Equal(bytes.UAsset, again.UAsset);
        Assert.Equal(bytes.UExp, again.UExp);
    }

    /// <summary>
    /// A sofa made furniture and moved to the wall's section, a table in the default section, a chair given an item
    /// section (refused): the staged recipes carry the sections and CUE4Parse loads them.
    /// </summary>
    [Fact]
    public async Task ChosenSectionsReachTheRecipes()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var sections = CraftCategories.Read(catalog);
        var chair = CraftCategories.SectionOf(sections, CraftablesPlanner.FurnitureRecipe)!;
        var wall = CraftCategories.SectionOf(sections, CraftCategories.WallRecipe)!;
        var item = sections.First(s => !s.Placeable);
        const string table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
        var craftables = new CraftablesFile
        {
            Items =
            [
                new Craftable { Name = "Big Sofa", Source = table, Mesh = table, Material = CraftMaterial.Fabric, Category = wall.Tag },
                new Craftable { Name = "Oak Table", Source = table, Mesh = table },
                new Craftable { Name = "Odd Chair", Source = table, Mesh = table, Category = item.Tag },
            ],
        };

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftsections-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await CraftablesExporter.ExportAsync(craftables, "MyMod", catalog, new ExportOptions { OutputDirectory = dir, WritePak = false }, ProjectSourceRole.Client);
            Assert.NotNull(result);
            Assert.Equal([$"Odd Chair: the section {item.Tag} is not one of the game's building sections; it shows in its default section."], result!.Warnings);

            const string recipes = "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/";
            CraftCategory Staged(string name)
            {
                var asset = Assert.Single(result.Assets, a => a.PackagePath == recipes + name);
                var file = Path.Combine([result.StagingDirectory, .. asset.VirtualPath.Split('/')]);
                return CraftCategories.Of(CookedPackage.Parse(File.ReadAllBytes(file), File.ReadAllBytes(Path.ChangeExtension(file, ".uexp")), null, recipes + name))!;
            }

            Assert.Equal(wall.Tag, Staged("CR_SS_Big_Sofa").Tag);
            Assert.Equal(wall.Domain, Staged("CR_SS_Big_Sofa").Domain);
            Assert.Equal(chair.Tag, Staged("CR_SS_Oak_Table").Tag);
            Assert.Equal(chair.Tag, Staged("CR_SS_Odd_Chair").Tag);

            using var loose = AssetCatalog.OpenLoose(result.StagingDirectory);
            var recipe = loose.LoadObject<UObject>(recipes + "CR_SS_Big_Sofa.CR_SS_Big_Sofa");
            Assert.Single(recipe.GetOrDefault<UScriptArray>("CraftingMetadata").Properties);
            Assert.True(recipe.GetOrDefault<UScriptArray>("Ingredients").Properties.Count > 0);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void TheSectionListComesFromTheGamesRecipes()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var sections = CraftCategories.Read(catalog);
        Assert.Same(sections, CraftCategories.Read(catalog)); // read once per catalog
        Assert.Contains(sections, s => s.Placeable);
        Assert.Contains(sections, s => !s.Placeable);
        Assert.All(sections, s => Assert.NotEmpty(s.Recipes));
        Assert.Empty(sections.Where(s => s.Placeable).Select(s => s.Tag).Intersect(sections.Where(s => !s.Placeable).Select(s => s.Tag)));
        Assert.Equal(sections.Count, sections.Select(s => s.Tag).Distinct(StringComparer.Ordinal).Count());
        Assert.True(CraftCategories.SectionOf(sections, CraftablesPlanner.FurnitureRecipe)?.Placeable);
        Assert.True(CraftCategories.SectionOf(sections, CraftCategories.WallRecipe)?.Placeable);
        Assert.Equal("CraftingCategory.Items.RangedWeapons.Firearms", CraftCategories.SectionOf(sections, "CR_Improvised_Weapon_Handgun")?.Tag);

        // Every section's sample recipe really carries its tag.
        foreach (var section in sections)
        {
            var sample = catalog.Index!.Entries.First(e => CraftCategories.IsStockRecipe(e.PackagePath) && e.PackagePath.EndsWith("/" + section.SampleRecipe, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(section.Tag, CraftCategories.Of(ModdableAssets.ReadPackage(catalog, sample.PackagePath))?.Tag);
        }
    }

    private static string? DefaultOf(Craftable craftable, string template = CraftablesPlanner.FurnitureRecipe) =>
        CraftCategories.DefaultFor(craftable, Sections, template)?.Tag;

    private static Craftable Craftable(string path, CraftKind kind = CraftKind.Furniture) =>
        new() { Name = "Thing", Source = path, Mesh = path, Kind = kind };

    private static CraftCategory Section(string tag, bool placeable, string recipe, int priority = 1) =>
        new(tag, CraftCategories.Label(tag), CraftCategories.Group(tag), placeable, priority, null, [recipe]);

    private static int[] Levels(StructValue slot, string member) =>
        Assert.IsType<StructValue>(slot.Properties.Single(p => p.Name == member).Value).Properties.Select(p => Assert.IsType<IntValue>(p.Value).Value).ToArray();

    /// <summary>
    /// A recipe package as the game cooks one: the recipe export (its <c>CraftingMetadata</c> links and two
    /// <c>FCraftingIngredientSlot</c>s of <c>CI_Wood</c>), its section sub-object (tag, domain, priority) and a skin link.
    /// </summary>
    private static CookedPackage Recipe(SyntheticPackage s)
    {
        const string wood = RecipeIngredients.TagFolder + "/CI_Wood";
        s.Names.AddRange(["/Script/SCUM", "PlaceableCraftingRecipe", "CraftingMetadata_RecipeCategory", "CR_Test", "CraftingMetadata_RecipeCategory_0",
            "CraftingCategoryTag", "GameplayTag", "TagName", Chairs, "DisplayDomain", "Furniture", "DisplayPriority", "ByteProperty",
            "CraftingMetadata_Skin_Base", "CraftingMetadata_Skin_Base_0", "CraftingMetadata", "Ingredients", "CraftingIngredientSlot", "AllowedTypes",
            "Amount", "AdditionalAmount", "PerSkillLevelIntegerData", "NoSkill", "Basic", "Medium", "Advanced", "AboveAdvanced", "Purpose",
            "ECraftingIngredientPurpose", "ECraftingIngredientPurpose::Material", "ReturnOnUncraft", "CraftingIngredientTag", wood, "CI_Wood"]);
        FNameRef N(string name) => new(s.N(name));

        void PerSkill(ByteWriter w, string name, int amount)
        {
            var levels = new ByteWriter();
            foreach (var level in new[] { "NoSkill", "Basic", "Medium", "Advanced", "AboveAdvanced" })
            {
                s.Tag(levels, level, "IntProperty", 4, x => x.U8(0));
                levels.I32(amount);
            }

            s.None(levels);
            s.Tag(w, name, "StructProperty", levels.Length, x => { x.FName(N("PerSkillLevelIntegerData")); x.Guid(default); x.U8(0); });
            w.Raw(levels.WrittenSpan);
        }

        var slots = new ByteWriter();
        foreach (var amount in new[] { 4, 2 })
        {
            s.Tag(slots, "AllowedTypes", "ArrayProperty", 8, x => { x.FName(N("ObjectProperty")); x.U8(0); });
            slots.I32(1);
            slots.I32(-6); // CI_Wood
            PerSkill(slots, "Amount", amount);
            PerSkill(slots, "AdditionalAmount", 1);
            s.Tag(slots, "Purpose", "EnumProperty", 8, x => { x.FName(N("ECraftingIngredientPurpose")); x.U8(0); });
            slots.FName(N("ECraftingIngredientPurpose::Material"));
            s.Tag(slots, "ReturnOnUncraft", "BoolProperty", 0, x => { x.U8(1); x.U8(0); });
            s.None(slots);
        }

        var ingredients = new ByteWriter();
        ingredients.I32(2);
        s.Tag(ingredients, "Ingredients", "StructProperty", slots.Length, x => { x.FName(N("CraftingIngredientSlot")); x.Guid(default); x.U8(0); });
        ingredients.Raw(slots.WrittenSpan);

        var recipe = new ByteWriter();
        s.Tag(recipe, "CraftingMetadata", "ArrayProperty", 12, x => { x.FName(N("ObjectProperty")); x.U8(0); });
        recipe.I32(2);
        recipe.I32(2); // the section
        recipe.I32(3); // the skin
        s.Tag(recipe, "Ingredients", "ArrayProperty", ingredients.Length, x => { x.FName(N("StructProperty")); x.U8(0); });
        recipe.Raw(ingredients.WrittenSpan);
        s.None(recipe);
        recipe.I32(0);

        var tag = new ByteWriter();
        s.Tag(tag, "TagName", "NameProperty", 8, x => x.U8(0));
        tag.FName(N(Chairs));
        s.None(tag);
        var category = new ByteWriter();
        s.Tag(category, "CraftingCategoryTag", "StructProperty", tag.Length, x => { x.FName(N("GameplayTag")); x.Guid(default); x.U8(0); });
        category.Raw(tag.WrittenSpan);
        s.Tag(category, "DisplayDomain", "NameProperty", 8, x => x.U8(0));
        category.FName(N("Furniture"));
        s.Tag(category, "DisplayPriority", "ByteProperty", 1, x => { x.FName(N("None")); x.U8(0); });
        category.U8(7);
        s.None(category);
        category.I32(0);

        var skin = new ByteWriter();
        s.None(skin);
        skin.I32(0);

        var bytes = PackageWriter.Build(new PackageBuildInput
        {
            Summary = SyntheticPackage.DefaultSummary(),
            Names = s.Names,
            Imports =
            [
                new(N("/Script/CoreUObject"), N("Package"), 0, N("/Script/SCUM")),
                new(N("/Script/CoreUObject"), N("Class"), -1, N("PlaceableCraftingRecipe")),
                new(N("/Script/CoreUObject"), N("Class"), -1, N("CraftingMetadata_RecipeCategory")),
                new(N("/Script/CoreUObject"), N("Class"), -1, N("CraftingMetadata_Skin_Base")),
                new(N("/Script/CoreUObject"), N("Package"), 0, N(wood)),
                new(N("/Script/SCUM"), N("CraftingIngredientTag"), -5, N("CI_Wood")),
            ],
            Exports =
            [
                new ExportEntry { ClassIndex = -2, ObjectName = N("CR_Test"), ObjectFlags = 0x1, IsAsset = 1, FirstExportDependency = 0, CreateBeforeSerializationDependencies = 2 },
                new ExportEntry { ClassIndex = -3, OuterIndex = 1, ObjectName = N("CraftingMetadata_RecipeCategory_0"), ObjectFlags = 0x1, FirstExportDependency = 2, CreateBeforeSerializationDependencies = 1, CreateBeforeCreateDependencies = 1 },
                new ExportEntry { ClassIndex = -4, OuterIndex = 1, ObjectName = N("CraftingMetadata_Skin_Base_0"), ObjectFlags = 0x1, FirstExportDependency = 4, CreateBeforeSerializationDependencies = 1, CreateBeforeCreateDependencies = 1 },
            ],
            ExportData = [recipe.ToArray(), category.ToArray(), skin.ToArray()],
            PreloadDependencies = [-2, -6, -3, 1, -4, 1],
        });
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/CR_Test");
    }
}
