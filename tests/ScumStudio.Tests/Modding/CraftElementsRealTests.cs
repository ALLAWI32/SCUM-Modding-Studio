using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;
using ScumStudio.Modding;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The element families of the real game (SCUM 1.3.3, <c>SCUM_PAKS</c>, key from this PC's store): the improvised bed is a
/// respawn point with an upgrade, the chest and the electric lamp spawn their items, and a built bed clone reads back with
/// the game's reader (CUE4Parse).
/// </summary>
public sealed class CraftElementsRealTests
{
    private const string Elements = "/Game/ConZ_Files/BaseBuilding/BaseElements/";
    private const string Bed = Elements + "BP_Bed_Improvised";
    private const string BedMesh = "/Game/ConZ_Files/Models/Objects/Outdoor/Base_Building_Elements/Craftable_Furniture/Bed/Improvised_Bed/SM_Bed_Improvised";
    private const string ActiveItems = "/Game/ConZ_Files/Items/Equipment/Active_Items/";

    [Fact]
    public void TheGamesBedChestAndLampAreFamiliesOfTheirOwn()
    {
        if (Open() is not { } catalog)
        {
            return; // not asked for
        }

        using (catalog)
        {
            var bed = ElementFamilies.Of(catalog, Bed);
            Assert.True(bed is not null, ElementFamilies.WhyNot(catalog, Bed));
            Assert.Equal("/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/CR_Bed_Improvised", bed!.Recipe);
            Assert.Equal(Bed, CraftablesPlanner.ProductPackage(catalog, bed.Recipe));
            Assert.Equal("BaseBuildingComponent", bed.NativeClass);
            Assert.True(bed.IsRespawnPoint);
            Assert.Contains(bed.Levels[0].Values, v => v.Path == "_healthPoints");
            Assert.Contains(bed.Levels[0].Values, v => v.Path == "_hoursToDecay");
            Assert.Contains(bed.Levels, l => l.Element == Elements + "BP_Bed_Improvised_Upgrade");
            Assert.Equal(Bed, ElementFamilies.Of(catalog, BedMesh)?.Element);

            foreach (var (element, item) in new[] { ("BP_Base_Improvised_Wooden_Chest", "Improvised_Wooden_Chest"), ("BP_Base_Improvised_Electric_Lamp_Wall", "Improvised_Electric_Lamp_Wall") })
            {
                var family = ElementFamilies.Of(catalog, Elements + element);
                Assert.True(family is not null, ElementFamilies.WhyNot(catalog, Elements + element));
                Assert.Equal("BaseBuildingItemComponent", family!.NativeClass);
                Assert.Equal(ActiveItems + item, family.ItemClass);
                Assert.True(catalog.PackageExists(family.ItemClass + "_ES"), family.ItemClass);
            }
        }
    }

    [Fact]
    public async Task ABuiltBedCloneReadsBackWithTheGamesReader()
    {
        if (Open() is not { } catalog)
        {
            return; // not asked for
        }

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftbed-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (catalog)
            {
                var craftable = new Craftable
                {
                    Name = "Camp Bed",
                    Source = Bed,
                    Mesh = BedMesh,
                    Kind = CraftKind.Element,
                    Element = new ElementSettings { Template = Bed, Levels = [new Dictionary<string, string> { ["_healthPoints"] = "4321" }] },
                    Ingredients = [new("CI_Plank", 4)],
                };
                var plan = CraftablesPlanner.Plan(catalog, [craftable]);
                Assert.Single(plan.Entries);
                var result = AssetModBuilder.Build(catalog, plan.Request);
                Assert.All(result.Packages, p => Assert.False(catalog.PackageExists(p.PackagePath), p.PackagePath));
                foreach (var package in result.Packages)
                {
                    await package.Bytes.WriteAsync(Path.Combine(dir, Path.ChangeExtension(package.HeaderFilePath(), null)));
                }
            }

            using var loose = AssetCatalog.OpenLoose(dir);
            var defaults = loose.LoadObject<UObject>(Elements + "BP_SS_Camp_Bed.Default__BP_SS_Camp_Bed_C");
            Assert.True(defaults.GetOrDefault<bool>("_isUsedForRespawn"));
            Assert.Equal(4321f, defaults.GetOrDefault<float>("_healthPoints"));
            var upgrade = loose.LoadObject<UObject>("/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/CR_SS_Camp_Bed_Up1.CR_SS_Camp_Bed_Up1");
            Assert.Equal(Elements + "BP_SS_Camp_Bed_L1.BP_SS_Camp_Bed_L1_C", upgrade.GetOrDefault<FSoftObjectPath>("Product").AssetPathName.Text);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static AssetCatalog? Open() =>
        Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;
}
