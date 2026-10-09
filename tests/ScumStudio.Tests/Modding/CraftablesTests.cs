using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Import;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Craftables (owner: "any table, chair, wall or house as a craftable that players place like the game's furniture"): the
/// recipe rules, the project file, and (real game files, <c>SCUM_PAKS</c>) the Craftables pak read back.
/// </summary>
public sealed class CraftablesTests
{
    [Fact]
    public void SuggestedRecipesFollowTheGamesOwnAmounts()
    {
        // A 3 m wooden piece costs what the game's wooden wall costs; the improvised table (about 1 m) costs 3 planks.
        var wall = RecipeRules.Suggest(CraftMaterial.Wood, 3);
        Assert.Contains(new CraftIngredient("CI_Plank", 9), wall);
        Assert.Contains(new CraftIngredient("CI_Group_Logs", 4), wall);
        Assert.Contains(new CraftIngredient("CI_Group_Blade", 6, true), wall);
        Assert.Contains(new CraftIngredient("CI_Plank", 3), RecipeRules.Suggest(CraftMaterial.Wood, 1));

        // Never free, never absurd: a stool is a quarter wall at least, a house three walls at most; tools never grow.
        Assert.All(RecipeRules.Suggest(CraftMaterial.Cement, 0.1f), i => Assert.True(i.Amount >= 1));
        var house = RecipeRules.Suggest(CraftMaterial.Cement, 40);
        Assert.Contains(new CraftIngredient("CI_Sand_Bag", 243), house);
        Assert.Contains(new CraftIngredient("CI_Group_BluntTool", 19, true), house);
        Assert.Contains(new CraftIngredient("CI_Brick", 31), RecipeRules.Suggest(CraftMaterial.Brick, 3));
        Assert.Contains(new CraftIngredient("CI_Metal_Scrap", 8), RecipeRules.Suggest(CraftMaterial.Metal, 3));
        Assert.Contains(new CraftIngredient("CI_Group_Rags", 4), RecipeRules.Suggest(CraftMaterial.Fabric, 1));

        // Higher skill needs less, like the game's wall of wood (9 → 5).
        Assert.Equal(new[] { 9, 9, 7, 6, 5 }, RecipeIngredients.PerSkill(9));
        Assert.Equal(new[] { 1, 1, 1, 1, 1 }, RecipeIngredients.PerSkill(1));
    }

    [Fact]
    public void MaterialKindAndSourceAreGuessedFromTheAsset()
    {
        Assert.Equal(CraftMaterial.Metal, RecipeRules.GuessMaterial(["/Game/X/SM_Fuel_Pump_01", "MI_Metal_Rust"]));
        Assert.Equal(CraftMaterial.Brick, RecipeRules.GuessMaterial(["SM_House_Brick_Wall"]));
        Assert.Equal(CraftMaterial.Fabric, RecipeRules.GuessMaterial(["SM_Sofa_02"]));
        Assert.Equal(CraftMaterial.Wood, RecipeRules.GuessMaterial(["SM_Something"]));
        Assert.Equal(CraftKind.Power, RecipeRules.GuessKind("/Game/X/SM_Solar_Panel_01"));
        Assert.Equal(CraftKind.Station, RecipeRules.GuessKind("/Game/X/SM_Workbench_02"));
        Assert.Equal(CraftKind.Furniture, RecipeRules.GuessKind("/Game/X/SM_Table_01"));
        Assert.True(RecipeRules.IsAllowedSource("/Game/ConZ_Files/Models/Objects/Indoor/SM_Table_01", "StaticMesh"));
        Assert.True(RecipeRules.IsAllowedSource("/Game/ConZ_Files/Models/Buildings/BP_House", "BlueprintGeneratedClass"));
        Assert.False(RecipeRules.IsAllowedSource("/Game/ConZ_Files/Characters/Animals/Wolf/BP_Wolf", "BlueprintGeneratedClass"));
        Assert.False(RecipeRules.IsAllowedSource("/Game/ConZ_Files/Vehicles/Wheeled/BPC_Rager", "BlueprintGeneratedClass"));
        Assert.False(RecipeRules.IsAllowedSource("/Game/X/SK_Thing", "SkeletalMesh"));
        Assert.Equal("SS_Oak_table_2", CraftablesFile.TokenOf(" Oak table #2 "));
    }

    [Fact]
    public void NamesInOtherScriptsGetAssetNamesOfTheirOwn()
    {
        // The owner names things in Arabic: two Arabic names must not both become "SS_" (craft-7); the token stays the same
        // from one export to the next, and a long name still makes a valid asset name.
        var table = CraftablesFile.TokenOf("طاولة");
        var chair = CraftablesFile.TokenOf("كرسي");
        Assert.NotEqual(table, chair);
        Assert.True(CloneFamilyPlanner.IsValidName(table) && CloneFamilyPlanner.IsValidName(chair), table + " " + chair);
        Assert.Equal(table, CraftablesFile.TokenOf(" طاولة "));
        Assert.True(CloneFamilyPlanner.IsValidName(CraftablesFile.TokenOf(new string('x', 80))));

        // A name that had a valid token keeps it (players' placed objects are saved by class path): a long Latin name is not
        // cut, a mixed name keeps its Latin token.
        var longName = new string('L', 50);
        Assert.Equal("SS_" + longName, CraftablesFile.TokenOf(longName));
        Assert.Equal("SS_Table", CraftablesFile.TokenOf("Table طاولة"));
    }

    [Fact]
    public async Task AnExportWithoutCraftablesRemovesTheOldCraftablesPak()
    {
        // craft-1: craftables removed (or the box unticked) must not leave the old pak, whose registry would hide the project pak's.
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftstale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "game", "SCUM", "Content"));
        var client = Path.Combine(dir, "out", ProjectExporter.RoleFolder(ProjectSourceRole.Client));
        Directory.CreateDirectory(client);
        var pak = Path.Combine(client, "pakchunk901-MyModCraftables_P.pak");
        var project = Path.Combine(client, "pakchunk900-MyMod_P.pak");
        foreach (var file in new[] { pak, Path.ChangeExtension(pak, ".sig"), project })
        {
            File.WriteAllText(file, "old");
        }

        try
        {
            using var catalog = AssetCatalog.OpenLoose(Path.Combine(dir, "game"));
            Assert.Null(await CraftablesExporter.ExportAsync(new CraftablesFile(), "MyMod", catalog, new ExportOptions { OutputDirectory = Path.Combine(dir, "out") }, ProjectSourceRole.Client));
            Assert.False(File.Exists(pak));
            Assert.False(File.Exists(Path.ChangeExtension(pak, ".sig")));
            Assert.True(File.Exists(project));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheProjectFileRoundTrips()
    {
        var file = new CraftablesFile
        {
            Items =
            [
                new Craftable { Name = "Oak Table", Source = "/Game/A/SM_T", Mesh = "/Game/A/SM_T", Ingredients = [new("CI_Plank", 3)], Station = "Bench" },
                new Craftable { Name = "Sun", Source = "/Game/A/SM_S", Mesh = "/Game/A/SM_S", Kind = CraftKind.Power, Power = new(25, 300, 0, true) },
            ],
        };
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            file.Save(dir);
            var back = CraftablesFile.Load(dir);
            Assert.Equal(file.ToJson(), back.ToJson());
            Assert.Contains("\"Power\"", File.ReadAllText(Path.Combine(dir, CraftablesFile.FileName)), StringComparison.Ordinal);
            new CraftablesFile().Save(dir);
            Assert.Empty(CraftablesFile.Load(dir).Items);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Two craftables from the owner's request (a table mesh and a chair Blueprint), a station the chair needs and a solar
    /// generator: the Craftables pak is built, and every recipe, element, item and registry record reads back.
    /// </summary>
    [Fact]
    public async Task TheCraftablesPakReadsBack()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        const string table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
        const string chair = "/Game/ConZ_Files/BaseBuilding/BaseElements/AsianDecorPack/BP_Chair_Asian";
        var chairMesh = CraftablesPlanner.MeshOf(catalog, chair);
        Assert.NotNull(chairMesh);
        Assert.Equal(table, CraftablesPlanner.MeshOf(catalog, table));
        var craftables = new CraftablesFile
        {
            Items =
            [
                new Craftable { Name = "Oak Table", Source = table, Mesh = table, SizeMeters = 1.6f },
                new Craftable { Name = "Tea Chair", Source = chair, Mesh = chairMesh!, Ingredients = [new("CI_Plank", 2), new("CI_Nails", 4), new("CI_Group_Blade", 1, true)], Station = "Carpenter Bench" },
                new Craftable { Name = "Carpenter Bench", Source = table, Mesh = table, Kind = CraftKind.Station, NeedsPower = true },
                new Craftable { Name = "Sun Panel", Source = table, Mesh = table, Kind = CraftKind.Power, Material = CraftMaterial.Metal, Power = new(25, 300, 0, DayOnly: true) },
            ],
        };

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftpak-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await CraftablesExporter.ExportAsync(craftables, "MyMod", catalog, new ExportOptions { OutputDirectory = dir }, ProjectSourceRole.Client);
            Assert.NotNull(result);
            Assert.Equal("MyModCraftables", result!.ModName);
            Assert.Equal("pakchunk901-MyModCraftables_P.pak", Path.GetFileName(result.PakPath));
            Assert.True(File.Exists(result.PakPath) && File.Exists(result.SigPath));
            Assert.All(result.Warnings, w => Assert.True(w.Contains("not exported", StringComparison.Ordinal), w)); // only the two native-only settings
            Assert.Equal(2, result.Warnings.Count);

            CookedPackage Staged(string package)
            {
                var asset = Assert.Single(result.Assets, a => string.Equals(a.PackagePath, package, StringComparison.OrdinalIgnoreCase));
                var file = Path.Combine([result.StagingDirectory, .. asset.VirtualPath.Split('/')]);
                return CookedPackage.Parse(File.ReadAllBytes(file), File.ReadAllBytes(Path.ChangeExtension(file, ".uexp")), null, package);
            }

            const string recipes = "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/";
            const string elements = "/Game/ConZ_Files/BaseBuilding/BaseElements/";

            // The table: its own recipe making its own element that shows the table mesh; ingredients from the rules.
            var tableRecipe = Staged(recipes + "CR_SS_Oak_Table");
            var props = tableRecipe.ReadProperties(RecipeIngredients.RecipeExport(tableRecipe));
            Assert.Equal(elements + "BP_SS_Oak_Table.BP_SS_Oak_Table_C", Assert.IsType<SoftObjectValue>(props.Find("Product")!.Value).AssetPath);
            Assert.Equal(RecipeRules.Suggest(CraftMaterial.Wood, 1.6f), RecipeIngredients.Read(tableRecipe));
            var element = Staged(elements + "BP_SS_Oak_Table");
            Assert.Contains(element.Imports, i => element.ResolveName(i.ObjectName) == table);
            Assert.DoesNotContain(element.Imports, i => element.ResolveName(i.ObjectName).Contains("SM_Chair_Improvised_Wood", StringComparison.Ordinal));

            // The chair: the owner's ingredients plus the station's own tag as a tool; the station's item carries that tag.
            Assert.Equal(
                new CraftIngredient[] { new("CI_Plank", 2), new("CI_Nails", 4), new("CI_Group_Blade", 1, true), new("CI_SS_Carpenter_Bench", 1, true) },
                RecipeIngredients.Read(Staged(recipes + "CR_SS_Tea_Chair")));
            var stationEs = Staged("/Game/ConZ_Files/Items/Equipment/Active_Items/SS_Carpenter_Bench_ES");
            Assert.Contains(stationEs.Names, n => n == "CI_SS_Carpenter_Bench");
            Assert.DoesNotContain(stationEs.Names, n => n == "CI_Group_Machinery");
            Staged(RecipeIngredients.TagFolder + "/CI_SS_Carpenter_Bench");

            // The solar generator: radius 25 m, output 300, no fuel, made by its own item recipe.
            var powerEs = Staged("/Game/ConZ_Files/Items/Equipment/SS_Sun_Panel_ES");
            var setup = Enumerable.Range(0, powerEs.Exports.Count).Single(i => powerEs.GetExportClassName(i) == "RangedResourceProviderEntityComponentContinousAmountSetup");
            Assert.Equal(2500f, Assert.IsType<FloatValue>(powerEs.ReadProperties(setup).Find("InfluenceAreaRadius")!.Value).Value);
            Assert.Equal(300f, Assert.IsType<FloatValue>(powerEs.ReadProperties(setup).Find("ResourceAmount")!.Value).Value);
            var powerRecipe = Staged("/Game/ConZ_Files/Items/Crafting/Recipes/Items/CR_SS_Sun_Panel");
            Assert.Equal("/Game/ConZ_Files/Items/Equipment/SS_Sun_Panel.SS_Sun_Panel_C",
                Assert.IsType<SoftObjectValue>(powerRecipe.ReadProperties(RecipeIngredients.RecipeExport(powerRecipe)).Find("Product")!.Value).AssetPath);

            // The game's reader (CUE4Parse) loads the new recipe with its caption and four ingredient slots.
            using (var loose = AssetCatalog.OpenLoose(result.StagingDirectory))
            {
                var recipe = loose.LoadObject<UObject>(recipes + "CR_SS_Tea_Chair.CR_SS_Tea_Chair");
                Assert.Equal("Tea Chair", recipe.GetOrDefault<FText>("Caption").Text);
                Assert.Equal(4, recipe.GetOrDefault<UScriptArray>("Ingredients").Properties.Count);
                Assert.Single(recipe.GetOrDefault<UScriptArray>("CraftingMetadata").Properties); // the category; the chair's skin link is dropped
                Assert.Equal(elements + "BP_SS_Tea_Chair.BP_SS_Tea_Chair_C", recipe.GetOrDefault<FSoftObjectPath>("Product").AssetPathName.Text);
            }

            // Registered like the game's own: recipes as PlaceableCraftingRecipe / ItemCraftingRecipe, elements as BaseBuilding.
            var registry = AssetRegistryFile.Load(Path.Combine(result.StagingDirectory, "SCUM", "AssetRegistry.bin"));
            string? TypeOf(string objectPath) => registry.Assets.Where(a => a.ObjectPath == objectPath)
                .Select(a => registry.GetTags(a).FirstOrDefault(t => t.Key == "PrimaryAssetType").Value?.Text).FirstOrDefault();
            Assert.Equal("PlaceableCraftingRecipe", TypeOf(recipes + "CR_SS_Oak_Table.CR_SS_Oak_Table"));
            Assert.Equal("BaseBuilding", TypeOf(elements + "BP_SS_Oak_Table.BP_SS_Oak_Table_C"));
            Assert.Equal("ItemCraftingRecipe", TypeOf("/Game/ConZ_Files/Items/Crafting/Recipes/Items/CR_SS_Sun_Panel.CR_SS_Sun_Panel"));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Any item of the game becomes craftable (owner: "craft something in exchange for some things"): the page's Add makes a
    /// firearm and a DLC knife Item craftables, and each gets an item recipe of its own, in its crafting menu section, making
    /// the game's own item class from the owner's ingredients.
    /// </summary>
    [Fact]
    public async Task ItemsOfTheGameGetRecipesOfTheirOwn()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        const string rifle = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";
        const string knife = "/SCUM/Plugins/GameFeatures/ApexHunterPack/Content/Gameplay/Items/Blueprints/1H_Apex_Knife";
        Assert.False(CraftablesPlanner.IsItem(catalog, "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01"));
        var added = ScumStudio.App.ViewModels.CraftablesPageViewModel.Describe(catalog, rifle);
        Assert.Equal(CraftKind.Item, added?.Kind);
        Assert.Equal(CraftKind.Item, ScumStudio.App.ViewModels.CraftablesPageViewModel.Describe(catalog, knife)?.Kind);
        CraftIngredient[] rifleRecipe = [new("CI_Metal_Scrap", 12), new("CI_Wire", 2), new("CI_Group_Toolbox", 1, true)];
        var craftables = new CraftablesFile
        {
            Items =
            [
                added! with { Name = "Rebel Rifle", Ingredients = rifleRecipe },
                new Craftable { Name = "Hunter Knife", Source = knife, Mesh = knife, Kind = CraftKind.Item, Ingredients = [new("CI_Group_Stone", 2)] },
            ],
        };

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftitems-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await CraftablesExporter.ExportAsync(craftables, "MyMod", catalog, new ExportOptions { OutputDirectory = dir }, ProjectSourceRole.Client);
            Assert.NotNull(result);
            Assert.Empty(result!.Warnings);

            CookedPackage Staged(string package)
            {
                var asset = Assert.Single(result.Assets, a => string.Equals(a.PackagePath, package, StringComparison.OrdinalIgnoreCase));
                var file = Path.Combine([result.StagingDirectory, .. asset.VirtualPath.Split('/')]);
                return CookedPackage.Parse(File.ReadAllBytes(file), File.ReadAllBytes(Path.ChangeExtension(file, ".uexp")), null, package);
            }

            static string Product(CookedPackage p) => Assert.IsType<SoftObjectValue>(p.ReadProperties(RecipeIngredients.RecipeExport(p)).Find("Product")!.Value).AssetPath;
            static string Category(CookedPackage p)
            {
                var index = Enumerable.Range(0, p.Exports.Count).Single(i => p.GetExportClassName(i) == "CraftingMetadata_RecipeCategory");
                var tag = Assert.IsType<StructValue>(p.ReadProperties(index).Find("CraftingCategoryTag")!.Value);
                return Assert.IsType<NameValue>(tag.Properties.Single(t => t.Name == "TagName").Value).Value;
            }

            // Only the two recipes are new: the items are the game's own (nothing cloned, nothing else registered).
            const string recipes = "/Game/ConZ_Files/Items/Crafting/Recipes/Items/";
            Assert.Equal(2, result.Assets.Count);
            var rifleCr = Staged(recipes + "CR_SS_Rebel_Rifle");
            Assert.Equal(rifle + ".Weapon_AK47_C", Product(rifleCr));
            Assert.Equal(rifleRecipe, RecipeIngredients.Read(rifleCr));
            Assert.Equal("CraftingCategory.Items.RangedWeapons.Firearms", Category(rifleCr));
            var tags = rifleCr.Imports.Where(i => rifleCr.ResolveName(i.ClassName) == "CraftingIngredientTag").ToList();
            Assert.All(tags, i => Assert.True(catalog.PackageExists(rifleCr.ResolveName(rifleCr.Imports[-i.OuterIndex - 1].ObjectName)), rifleCr.ResolveName(i.ObjectName)));

            // The DLC knife is named by the plugin's mount path, as the game's own DLC recipes name their products.
            var knifeCr = Staged(recipes + "CR_SS_Hunter_Knife");
            Assert.Equal("/ApexHunterPack/Gameplay/Items/Blueprints/1H_Apex_Knife.1H_Apex_Knife_C", Product(knifeCr));
            Assert.Equal("CraftingCategory.Items.MeleeWeapons", Category(knifeCr));

            // The game's reader (CUE4Parse) loads the recipe.
            using (var loose = AssetCatalog.OpenLoose(result.StagingDirectory))
            {
                var recipe = loose.LoadObject<UObject>(recipes + "CR_SS_Rebel_Rifle.CR_SS_Rebel_Rifle");
                Assert.Equal(rifle + ".Weapon_AK47_C", recipe.GetOrDefault<FSoftObjectPath>("Product").AssetPathName.Text);
                Assert.Equal(3, recipe.GetOrDefault<UScriptArray>("Ingredients").Properties.Count);
            }

            // Registered like the game's item recipes, so the crafting menu lists them.
            var registry = AssetRegistryFile.Load(Path.Combine(result.StagingDirectory, "SCUM", "AssetRegistry.bin"));
            string? TypeOf(string objectPath) => registry.Assets.Where(a => a.ObjectPath == objectPath)
                .Select(a => registry.GetTags(a).FirstOrDefault(t => t.Key == "PrimaryAssetType").Value?.Text).FirstOrDefault();
            Assert.Equal("ItemCraftingRecipe", TypeOf(recipes + "CR_SS_Rebel_Rifle.CR_SS_Rebel_Rifle"));
            Assert.Equal("ItemCraftingRecipe", TypeOf(recipes + "CR_SS_Hunter_Knife.CR_SS_Hunter_Knife"));
            Assert.All(result.Registered, r => Assert.Contains("/CR_SS_", r.ObjectPath, StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    /// craft-5: a mistyped ingredient tag is left out instead of importing a package that exists nowhere, two stations with
    /// the same name no longer stop the export, and a recipe asks for a station only when that station is built.
    /// </summary>
    [Fact]
    public void UnknownTagsAndMissingStationsAreLeftOutOfRecipes()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        const string table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
        var plan = CraftablesPlanner.Plan(catalog,
        [
            new Craftable { Name = "Stool", Source = table, Mesh = table, Ingredients = [new("CI_Plnk", 2), new("CI_Plank", 3)], Station = "Bench" },
            new Craftable { Name = "Lamp", Source = table, Mesh = table, Ingredients = [new("CI_Plank", 1)], Station = "Broken Bench" },
            new Craftable { Name = "Bench", Source = table, Mesh = table, Kind = CraftKind.Station },
            new Craftable { Name = "bench", Source = table, Mesh = table, Kind = CraftKind.Station },
            new Craftable { Name = "Broken Bench", Source = table, Mesh = "/Game/ConZ_Files/Nowhere/SM_Nothing", Kind = CraftKind.Station },
        ]);

        string Recipe(string name) => plan.Entries.Single(e => e.Name == name).Recipe;
        Assert.Equal(new CraftIngredient[] { new("CI_Plank", 3), new("CI_SS_Bench", 1, true) }, plan.Request.Ingredients[Recipe("Stool")]);
        Assert.Equal(new CraftIngredient[] { new("CI_Plank", 1) }, plan.Request.Ingredients[Recipe("Lamp")]);
        Assert.DoesNotContain(plan.Entries, e => e.Name is "bench" or "Broken Bench");
        Assert.Contains(plan.Warnings, w => w.StartsWith("Stool:", StringComparison.Ordinal) && w.Contains("CI_Plnk", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.StartsWith("bench:", StringComparison.Ordinal));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Lamp:", StringComparison.Ordinal) && w.Contains("Broken Bench", StringComparison.Ordinal));
    }

    /// <summary>Review: stations are planned after the station they need at any depth (C needs B needs A, listed C, B, A).</summary>
    [Fact]
    public void StationsArePlannedAfterTheStationTheyNeed()
    {
        Craftable Station(string name, string? needs) => new() { Name = name, Source = "/Game/A/SM_" + name, Mesh = "/Game/A/SM_" + name, Kind = CraftKind.Station, Station = needs };
        var chair = new Craftable { Name = "Chair", Source = "/Game/A/SM_Chair", Mesh = "/Game/A/SM_Chair", Station = "Forge" };
        var order = CraftablesPlanner.BuildOrder([chair, Station("Forge", "Anvil"), Station("Anvil", "Bench"), Station("Bench", null), Station("Loop", "Loop")]);
        Assert.Equal(["Bench", "Anvil", "Forge", "Loop", "Chair"], order.Select(c => c.Name));
    }

    /// <summary>
    /// Review of craft-items: an item is a Blueprint class with a Blueprint entity setup beside it. The world meshes with an
    /// <c>SM_x_ES</c> mesh twin (132 of them) and the player character became broken Item craftables; an item's recipe
    /// template follows its native class, so spears, the chainsaw, the sledgehammer, the DLC tomahawk and a DLC charm
    /// are melee weapons and attachments, not firearms.
    /// </summary>
    [Fact]
    public void ItemsAreBlueprintsWithAnEntitySetupInTheirOwnFamily()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        const string palace = "/Game/ConZ_Files/Models/Buildings/Abandoned_City/Buildings/Palace_Energetik/SM_Palace_Energetik_Main";
        const string river = "/Game/ConZ_Files/Landscape/Rivers/River_C_3_4";
        const string prisoner = "/Game/ConZ_Files/Characters/Prisoner/Blueprints/BP_Prisoner";
        const string rifle = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";
        foreach (var notItem in new[] { palace, river, prisoner })
        {
            Assert.True(catalog.PackageExists(notItem + "_ES"), notItem);
            Assert.False(CraftablesPlanner.IsItem(catalog, notItem), notItem);
        }

        Assert.NotEqual(CraftKind.Item, ScumStudio.App.ViewModels.CraftablesPageViewModel.Describe(catalog, palace)?.Kind);
        Assert.True(CraftablesPlanner.IsItem(catalog, rifle));
        var items = CraftablesPlanner.Items(catalog);
        Assert.Contains(rifle, items);
        Assert.DoesNotContain(palace, items);
        Assert.DoesNotContain(prisoner, items);
        Assert.InRange(items.Count, 2_000, 4_000);

        string Template(string item)
        {
            var path = CraftablesPlanner.GamePath(item);
            return PackageMap.Leaf(CraftablesPlanner.ItemTemplate(catalog, path)) + " (" + CraftablesPlanner.NativeClassOf(catalog, path) + ")";
        }

        Assert.StartsWith("CR_Improvised_Weapon_Handgun ", Template(rifle), StringComparison.Ordinal);
        foreach (var melee in new[]
        {
            "/Game/ConZ_Files/Items/Weapons/Improvised_Wooden_Spear", "/Game/ConZ_Files/Items/Weapons/New_Melee/Chainsaw",
            "/Game/ConZ_Files/Items/Weapons/New_Melee/Sledgehammer",
            "/SCUM/Plugins/GameFeatures/SpecialistStalkerPack/Content/Gameplay/Weapons/Blueprints/SpecialistStalker_Tomahawk",
        })
        {
            Assert.StartsWith("CR_Improvised_Knife ", Template(melee), StringComparison.Ordinal);
        }

        Assert.StartsWith("CR_Improvised_Flashlight ", Template("/SCUM/Plugins/GameFeatures/WoodlandHunterPack/Content/Gameplay/Weapons/Blueprints/Attachments/Charms/BP_WeaponCharm_BoneWhistle"), StringComparison.Ordinal);
        Assert.StartsWith("CR_Improvised_Bow_20 ", Template("/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Compound_Bow_Desert"), StringComparison.Ordinal);
        Assert.StartsWith("CR_Wooden_Arrow ", Template("/Game/ConZ_Files/Items/Ammunition/Arrows/Metal_Arrow"), StringComparison.Ordinal);
        Assert.StartsWith("CR_Improvised_Boots ", Template("/Game/ConZ_Files/Items/Clothes/Ghillie_Suits/Improvised/Improvised_Ghillie_Jacket"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Review of craft-import: an imported model ships only when its craftable is planned (one left out, here for a token
    /// another craftable has, ships nothing), and its files are listed in the result's assets.
    /// </summary>
    [Fact]
    public async Task OnlyPlannedImportsShipTheirFiles()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        const string table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftimports-" + Guid.NewGuid().ToString("N"));
        string Import(string token)
        {
            var file = Path.Combine(dir, "project", "imports", token, "SCUM", "Content", "ScumStudio", "Imports", token, "SM_" + token + ".uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, [1, 2, 3]);
            File.WriteAllBytes(Path.ChangeExtension(file, ".uexp"), [4]);
            return UeCook.ImportsRoot + "/" + token + "/SM_" + token;
        }

        try
        {
            var craftables = new CraftablesFile
            {
                ProjectDirectory = Path.Combine(dir, "project"),
                Items =
                [
                    new Craftable { Name = "Oak Table", Source = table, Mesh = table, Ingredients = [new("CI_Plank", 3)] },
                    new Craftable { Name = "Oak-Table", Source = "x.obj", Mesh = Import("SS_Left"), Imported = "imports/SS_Left", Ingredients = [new("CI_Plank", 3)] },
                    new Craftable { Name = "Crate", Source = "y.obj", Mesh = Import("SS_Kept"), Imported = "imports/SS_Kept", Ingredients = [new("CI_Plank", 2)] },
                ],
            };
            var result = await CraftablesExporter.ExportAsync(craftables, "MyMod", catalog, new ExportOptions { OutputDirectory = Path.Combine(dir, "out"), WritePak = false }, ProjectSourceRole.Client);
            Assert.NotNull(result);
            Assert.Contains(result!.Warnings, w => w.StartsWith("Oak-Table: left out", StringComparison.Ordinal));
            var imports = Path.Combine(result.StagingDirectory, "SCUM", "Content", "ScumStudio", "Imports");
            Assert.False(Directory.Exists(Path.Combine(imports, "SS_Left")));
            Assert.True(File.Exists(Path.Combine(imports, "SS_Kept", "SM_SS_Kept.uasset")));
            Assert.Contains(result.Assets, a => a.PackagePath == UeCook.ImportsRoot + "/SS_Kept/SM_SS_Kept" && a.VirtualPath == "SCUM/Content/ScumStudio/Imports/SS_Kept/SM_SS_Kept.uasset");
            Assert.DoesNotContain(result.Assets, a => a.PackagePath.Contains("SS_Left", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
