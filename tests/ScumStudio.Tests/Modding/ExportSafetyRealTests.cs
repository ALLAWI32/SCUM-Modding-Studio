using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Registry;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// The export law on the real game files (Desktop handoff §12.1 #4): the Craftables pak and a clone's registry only add.
/// The source is the game's own paks (no <c>_P</c> pak), as an export reads them.
/// </summary>
public sealed class ExportSafetyRealTests
{
    private static AssetCatalog OpenGame() => AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions
    {
        AesKey = AesKeyText.FromEnvironmentOrStore(),
        PakFileFilter = name => !PackageOrigins.IsModContainer(name),
    });

    [PaksFact]
    public async Task TheCraftablesPakOnlyAdds()
    {
        using var catalog = OpenGame();
        const string table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
        const string rifle = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";
        var craftables = new CraftablesFile
        {
            Items =
            [
                new Craftable { Name = "Safety Table", Source = table, Mesh = table, SizeMeters = 1.6f },
                new Craftable { Name = "Safety Bench", Source = table, Mesh = table, Kind = CraftKind.Station },
                new Craftable { Name = "Safety Sun", Source = table, Mesh = table, Kind = CraftKind.Power, Material = CraftMaterial.Metal, Power = new(25, 300, 0, DayOnly: false) },

                // Its copied recipe makes the stock rifle (the template's product is redirected to it): the rifle's records stay.
                new Craftable { Name = "Safety Rifle", Source = rifle, Mesh = rifle, Kind = CraftKind.Item, Ingredients = [new("CI_Metal_Scrap", 12)] },
            ],
        };

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftsafe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await CraftablesExporter.ExportAsync(craftables, "SafetyMod", catalog, new ExportOptions { OutputDirectory = dir, WritePak = false }, ProjectSourceRole.Client);
            Assert.NotNull(result);
            Assert.Contains(result!.Assets, a => a.PackagePath.EndsWith("/CR_SS_Safety_Rifle", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Registered, r => r.ObjectPath.StartsWith(rifle + ".", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.Assets, a => a.PackagePath == DataTableEdits.TradeableTable);
            ExportSafety.AssertOnlyAdds(catalog, result.Assets.Select(a => a.PackagePath));
            var stock = AssetRegistryFile.Parse(RegistryMerge.ReadStock(catalog)!);
            var shipped = AssetRegistryFile.Load(Path.Combine(result.StagingDirectory, "SCUM", "AssetRegistry.bin"));
            ExportSafety.AssertRegistrySuperset(stock, shipped, result.Registered.Select(r => r.ObjectPath));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [PaksFact]
    public void TheMainPakRegistryIsStockPlusAdditions()
    {
        using var catalog = OpenGame();
        var plan = CloneFamilyPlanner.PlanItem(catalog, "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_RPK-74", "Weapon_RPK-74_Safety");
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([plan], new Dictionary<string, IReadOnlyList<TunableEdit>>()));

        Assert.NotNull(result.AssetRegistry);
        Assert.NotEmpty(result.Registered);
        ExportSafety.AssertOnlyAdds(catalog, result.Packages.Where(p => p.IsClone).Select(p => p.PackagePath));
        var stock = AssetRegistryFile.Parse(RegistryMerge.ReadStock(catalog)!);
        ExportSafety.AssertRegistrySuperset(stock, AssetRegistryFile.Parse(result.AssetRegistry), result.Registered.Select(r => r.ObjectPath));
    }
}
