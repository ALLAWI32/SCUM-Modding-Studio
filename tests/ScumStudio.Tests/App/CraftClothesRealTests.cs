using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.Rendering;

namespace ScumStudio.Tests.App;

/// <summary>
/// Clothes in the Craftables gallery and the ingredient picker (owner, 2026-10-16) on the real game files: every clothing
/// item of the game, a DLC's too, is listed under "Clothing &amp; armour", and jackets, backpacks, vests, helmets and shoes
/// show their own textured 3D model.
/// </summary>
public sealed class CraftClothesRealTests
{
    private const string ApexJacket = "/SCUM/Plugins/GameFeatures/ApexHunterPack/Content/Gameplay/Items/Blueprints/Apex_Jacket_01";

    private static AssetCatalog Open() => AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [PaksFact]
    public void EveryClothingItemIsWithTheClothes()
    {
        using var catalog = Open();
        var index = catalog.Index ?? catalog.BuildIndex(resolveClasses: false);
        var items = CraftablesPlanner.Items(catalog);
        Assert.Contains(ApexJacket, items);

        // Before: the catalogue files a DLC jacket under its pack, so it was listed with the pack, not with the clothes.
        static bool IsClothing(string node) => node == "items.clothing" || node.StartsWith("items.clothing.", StringComparison.Ordinal);
        var before = CraftGalleryViewModel.Sort(index, items);
        Assert.DoesNotContain(before.Items.Concat(before.Other), i => i.Entry.PackagePath == ApexJacket && IsClothing(i.Package.Node));

        var sorted = CraftGalleryViewModel.Sort(index, items, p => CraftablesPlanner.NativeClassOf(catalog, p));
        Assert.DoesNotContain(sorted.Other, o => o.Entry.PackagePath == ApexJacket);
        var clothing = sorted.Items.Where(i => IsClothing(i.Package.Node)).Select(i => i.Entry.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(ApexJacket, clothing);
        foreach (var kind in new[] { "/Clothes/Jackets_Coats/", "/Clothes/Backpacks_Bags/", "/Clothes/Vests_Armor/", "/Clothes/Helmets/", "/Clothes/Footwear/" })
        {
            Assert.Contains(clothing, p => p.Contains(kind, StringComparison.OrdinalIgnoreCase));
        }

        // The group the gallery and the picker show has a clothing category with all of them.
        var group = AssetsPageViewModel.CategoryGroup("Pick up", AssetsPageViewModel.PickUpRoots, sorted.Items.Select(i => i.Package));
        var node = group.Children.Single(c => c.Ids.Contains("items")).Children.Single(c => c.Ids.Contains("items.clothing"));
        Assert.True(node.Count >= clothing.Count, $"{node.Count} in the category, {clothing.Count} clothing items");
    }
}

/// <summary>Clothes tiles of the gallery: their own textured 3D model (jackets, backpacks, vests, helmets, shoes, a DLC jacket).</summary>
[Collection(GlCollection.Name)] // its pictures render with OpenGL: never beside another GL test
public sealed class CraftClothesPictureRealTests
{
    private const string ApexJacket = "/SCUM/Plugins/GameFeatures/ApexHunterPack/Content/Gameplay/Items/Blueprints/Apex_Jacket_01";

    private static AssetCatalog Open() => AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [PaksFact]
    public async Task ClothesShowTheirOwnTexturedModel()
    {
        if (GlTestEnvironment.SkipReason is not null)
        {
            return; // no OpenGL here
        }

        using var catalog = Open();
        var index = catalog.Index ?? catalog.BuildIndex(resolveClasses: false);
        var directory = Path.Combine(Path.GetTempPath(), "scumstudio-thumbs", Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new ThumbnailService(directory, NullLogger.Instance);
            foreach (var item in new[]
                     {
                         "/Game/ConZ_Files/Items/Clothes/Jackets_Coats/Camouflage_Jacket_01",
                         "/Game/ConZ_Files/Items/Clothes/Backpacks_Bags/Hiking_Backpack_01_01",
                         "/Game/ConZ_Files/Items/Clothes/Vests_Armor/Event/Bulletproof_Vest_03_Event",
                         "/Game/ConZ_Files/Items/Clothes/Helmets/Military_Helmet_01_07",
                         "/Game/ConZ_Files/Items/Clothes/Footwear/Sneakers_02_03",
                         ApexJacket,
                     })
            {
                // The gallery's tile: the item's Blueprint drawn with its textures.
                var entry = index.Entries.First(e => string.Equals(e.PackagePath, item, StringComparison.OrdinalIgnoreCase)) with { ClassName = "Blueprint" };
                var png = await service.GetAssetAsync(catalog, entry);
                Assert.True(png is not null, $"no picture of {item}");
                var (rgba, width, height) = await ImageExport.LoadRgbaAsync(png!);
                var background = (rgba[0], rgba[1], rgba[2]);
                var drawn = Enumerable.Range(0, width * height).Where(i => (rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2]) != background).ToList();
                Assert.True(drawn.Count > width * height / 20, $"{item}: only {drawn.Count} pixels drawn");
                var colours = drawn.Select(i => (rgba[i * 4] / 16, rgba[(i * 4) + 1] / 16, rgba[(i * 4) + 2] / 16)).Distinct().Count();
                Assert.True(colours > 12, $"{item}: {colours} colours, not a textured model");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
