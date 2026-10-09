using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
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
}
