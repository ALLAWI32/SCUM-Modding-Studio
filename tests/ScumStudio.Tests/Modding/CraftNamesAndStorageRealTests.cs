using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The 2026-10-16 Craftables requests on the real game files (<c>SCUM_PAKS</c> and the key; skipped otherwise): HexTer's
/// own asset names and folders are what the export writes and registers; seviscache's crafted jacket and bait feeder are
/// the game's own item classes; defolt_0999's large fridge is the game's fridge spawned by a player-built cabinet element,
/// with its storage size set, and no world item spawner (D02).
/// </summary>
public sealed class CraftNamesAndStorageRealTests
{
    private const string Lamp = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
    private const string Jacket = "/Game/ConZ_Files/Items/Clothes/Jackets_Coats/Camouflage_Jacket_01";
    private const string BaitFeeder = "/Game/ConZ_Files/Items/Equipment/AnimalBaitFeederItem";
    private const string Placeables = "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/";
    private const string Elements = "/Game/ConZ_Files/BaseBuilding/BaseElements/";

    private static AssetCatalog Open() => AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    private static Task<(ExportResult Result, Func<string, CookedPackage> Staged)> ExportAsync(AssetCatalog catalog, string dir, params Craftable[] craftables) =>
        ExportAsync(catalog, dir, false, craftables);

    private static async Task<(ExportResult Result, Func<string, CookedPackage> Staged)> ExportAsync(AssetCatalog catalog, string dir, bool writePak, params Craftable[] craftables)
    {
        var result = await CraftablesExporter.ExportAsync(new CraftablesFile { Items = craftables }, "MyMod", catalog, new ExportOptions { OutputDirectory = dir, WritePak = writePak }, ProjectSourceRole.Client);
        Assert.NotNull(result);
        return (result!, package =>
        {
            var asset = Assert.Single(result!.Assets, a => string.Equals(a.PackagePath, package, StringComparison.OrdinalIgnoreCase));
            var file = Path.Combine([result.StagingDirectory, .. asset.VirtualPath.Split('/')]);
            return CookedPackage.Parse(File.ReadAllBytes(file), File.ReadAllBytes(Path.ChangeExtension(file, ".uexp")), null, package);
        });
    }

    private static string? Product(CookedPackage recipe) =>
        recipe.ReadProperties(RecipeIngredients.RecipeExport(recipe)).Find("Product")?.Value is ScumStudio.Formats.Properties.SoftObjectValue { AssetPath: { } path } ? path : null;

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [PaksFact]
    public async Task ACustomNameAndFolderAreWhatTheExportWritesAndRegisters()
    {
        using var catalog = Open();

        // The game itself keeps recipes and elements in sub-folders of the folders it scans (its Asian decor pack).
        var index = catalog.Index ?? catalog.BuildIndex(resolveClasses: false);
        Assert.Contains(index.Entries, e => e.PackagePath.StartsWith(Placeables + "AsianDecorDLC/CR_", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(index.Entries, e => e.PackagePath.StartsWith(Elements + "AsianDecorPack/BP_", StringComparison.OrdinalIgnoreCase));

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftnames-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (result, staged) = await ExportAsync(catalog, dir, true,
                new Craftable { Name = "Reading Lamp", Token = "HexTer_Reading_Lamp", Folder = "HexTer/Lights", Exported = false, Source = Lamp, Mesh = Lamp, Ingredients = [new("CI_Plank", 2)] },
                new Craftable { Name = "Camo Jacket", Token = "HexTer_Camo_Jacket", Folder = "Clothes", Exported = false, Source = Jacket, Mesh = Jacket, Kind = CraftKind.Item, Ingredients = [new("CI_Group_Rags", 4)] });

            const string recipe = Placeables + "HexTer/Lights/CR_HexTer_Reading_Lamp";
            const string element = Elements + "HexTer/Lights/BP_HexTer_Reading_Lamp";
            const string jacketRecipe = "/Game/ConZ_Files/Items/Crafting/Recipes/Items/Clothes/CR_HexTer_Camo_Jacket";
            var lampRecipe = staged(recipe);
            Assert.Equal("CR_HexTer_Reading_Lamp", lampRecipe.ResolveName(lampRecipe.Exports[RecipeIngredients.RecipeExport(lampRecipe)].ObjectName));
            Assert.Equal(element + ".BP_HexTer_Reading_Lamp_C", Product(lampRecipe));
            var built = staged(element);
            Assert.Contains(Enumerable.Range(0, built.Exports.Count), i => built.ResolveName(built.Exports[i].ObjectName) == "BP_HexTer_Reading_Lamp_C");
            Assert.Equal(Jacket + ".Camouflage_Jacket_01_C", Product(staged(jacketRecipe)));

            // The registry the game reads lists them at their new paths.
            Assert.Contains(result.Registered, r => r.ObjectPath.StartsWith(recipe + ".", StringComparison.Ordinal));
            Assert.Contains(result.Registered, r => r.ObjectPath.StartsWith(element + ".", StringComparison.Ordinal));
            Assert.Contains(result.Registered, r => r.ObjectPath.StartsWith(jacketRecipe + ".", StringComparison.Ordinal));
            Assert.DoesNotContain(result.Assets, a => a.PackagePath.Contains("/CR_SS_", StringComparison.Ordinal));

            // Read back from the written pak (CUE4Parse, as the game mounts it): the recipe and element at their folders.
            using var pak = AssetCatalog.OpenPaks(result.PakPath!);
            Assert.Contains(pak.GetExports(recipe), e => e.Name == "CR_HexTer_Reading_Lamp");
            Assert.Contains(pak.GetExports(element), e => e.ClassName == "BlueprintGeneratedClass" && e.Name == "BP_HexTer_Reading_Lamp_C");
            Assert.True(pak.PackageExists(jacketRecipe));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [PaksFact]
    public async Task CraftToBagMakesTheGamesOwnJacketAndBaitFeeder()
    {
        using var catalog = Open();
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftreal-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(CraftablesPlanner.IsItem(catalog, BaitFeeder)); // its setup is AnimalBaitFeeder_ES, not a name twin
            Assert.Equal("ClothesItem", CraftablesPlanner.NativeClassOf(catalog, Jacket));
            Assert.Equal("AnimalBaitFeederItem", CraftablesPlanner.NativeClassOf(catalog, BaitFeeder));
            var (result, staged) = await ExportAsync(catalog, dir,
                new Craftable { Name = "Camo Jacket", Source = Jacket, Mesh = Jacket, Kind = CraftKind.Item, Ingredients = [new("CI_Group_Rags", 4)] },
                new Craftable { Name = "Bait Feeder", Source = BaitFeeder, Mesh = BaitFeeder, Kind = CraftKind.Item, Ingredients = [new("CI_Metal_Scrap", 3)] });

            // The recipe's product is the game's own class: crafted, it is the real jacket (worn as usual) and the real feeder.
            Assert.Equal(Jacket + ".Camouflage_Jacket_01_C", Product(staged("/Game/ConZ_Files/Items/Crafting/Recipes/Items/CR_SS_Camo_Jacket")));
            Assert.Equal(BaitFeeder + ".AnimalBaitFeederItem_C", Product(staged("/Game/ConZ_Files/Items/Crafting/Recipes/Items/CR_SS_Bait_Feeder")));
            Assert.True(catalog.IsStock(Jacket) && catalog.IsStock(BaitFeeder));
            Assert.DoesNotContain(result.Assets, a => a.PackagePath.StartsWith(Jacket, StringComparison.OrdinalIgnoreCase) || a.PackagePath.StartsWith(BaitFeeder, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [PaksFact]
    public async Task ALargeFridgeIsTheGamesFridgeOnAPlayerBuiltElement()
    {
        using var catalog = Open();
        Assert.True(ItemStorage.IsPlacedContainer(catalog, ItemStorage.Fridge));
        Assert.Equal("RefrigeratorItem", CraftablesPlanner.NativeClassOf(catalog, ItemStorage.Fridge));
        Assert.Equal(["30", "15"], ItemStorage.Of(catalog, ItemStorage.Fridge).Where(t => t.Path is "NumRows" or "NumColumns").Select(t => t.Value));
        var carrier = ElementFamilies.Of(catalog, ItemStorage.Carrier);
        Assert.NotNull(carrier);
        Assert.Equal("BaseBuildingItemComponent", carrier!.NativeClass);

        // Picked from the gallery, the game's fridge becomes that element spawning a copy of the fridge, built (not crafted).
        var described = CraftablesPageViewModel.Describe(catalog, ItemStorage.Fridge)!;
        Assert.Equal((CraftKind.Element, CraftUse.Build, carrier.Element), (described.Kind, described.Use, described.Element!.Template));
        Assert.Equal(ItemStorage.Fridge, described.Element.Storage!.Item);
        var fridge = described with
        {
            Token = "SS_Big_Fridge",
            Folder = "Storage",
            Exported = false,
            Element = described.Element with { Storage = described.Element.Storage with { Values = new Dictionary<string, string> { ["NumRows"] = "60", ["NumColumns"] = "20", ["MaxContainedWeight"] = "2000" } } },
        };

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftfridge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var plan = CraftablesPlanner.Plan(catalog, [fridge]);
            var entry = Assert.Single(plan.Entries);
            Assert.Equal("/Game/ConZ_Files/Items/Equipment/Storage/SS_Big_Fridge", entry.Item);
            Assert.Contains(plan.Request.Clones.Single().Packages, p => p.Key == ItemStorage.Fridge && p.Value == entry.Item);
            Assert.DoesNotContain(plan.Request.Clones.Single().Packages, p => p.Key == carrier.ItemClass); // the cabinet's own item is not shipped

            var (result, staged) = await ExportAsync(catalog, dir, fridge);
            var element = staged(entry.Product);
            var names = element.Imports.Select(i => element.ResolveName(i.ObjectName)).ToList();
            Assert.Contains("SS_Big_Fridge_C", names); // the element spawns the fridge copy
            Assert.Contains(entry.Item, names);
            Assert.DoesNotContain(names, n => n.StartsWith(PackageMap.Leaf(carrier.ItemClass!), StringComparison.Ordinal));
            Assert.Contains("BaseBuildingItemComponent", element.Imports.Select(i => element.ResolveName(i.ObjectName)).Concat(ParentNames(catalog, carrier.Element)));

            // The copy is the game's fridge class (cooling and power are its native code), with the size set.
            var item = staged(entry.Item!);
            Assert.Contains("RefrigeratorItem", item.Imports.Select(i => item.ResolveName(i.ObjectName)));
            var setup = TunableReader.Read(staged(entry.Item + "_ES")).Where(t => ItemStorage.Editable.Contains(t.Path)).ToDictionary(t => t.Path, t => t.Value);
            Assert.Equal(("60", "20"), (setup["NumRows"], setup["NumColumns"]));
            Assert.Equal(2000f, float.Parse(setup["MaxContainedWeight"], System.Globalization.CultureInfo.InvariantCulture));

            // D02: a player builds and owns it like a chest; nothing in the pak is a world item spawner.
            foreach (var asset in result.Assets.Where(a => a.PackagePath.Contains("/Storage/", StringComparison.Ordinal)))
            {
                var package = staged(asset.PackagePath);
                Assert.DoesNotContain(package.Imports, i => package.ResolveName(i.ObjectName).Contains("WorldItemSpawner", StringComparison.Ordinal));
            }
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // The element's parent chain may name the native class instead of the element itself.
    private static IEnumerable<string> ParentNames(AssetCatalog catalog, string element) =>
        CraftablesPlanner.NativeClassOf(catalog, element) is { } native ? [native] : [];
}
