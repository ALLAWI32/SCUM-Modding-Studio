using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Registry;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Real game files, r39 (Discord 2026-10-18): KitchenStove is one of the 246 items the game hides from #SpawnItem
/// (class defaults <c>_visibleToAdminCommands = false</c>, registry tag HiddenFromAdminCommands). Its clone is made to be
/// spawned: the clone's class defaults say true and its records carry no such tag; the game's own record keeps it.
/// </summary>
public sealed class AdminVisibleCloneRealTests
{
    private const string Stove = "/Game/ConZ_Files/Items/Crafting/Survival/Items/KitchenStove";

    [PaksFact]
    public void ACloneOfAHiddenItemIsVisibleToTheSpawnCommands()
    {
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions
        {
            AesKey = AesKeyText.FromEnvironmentOrStore(),
            PakFileFilter = OwnPaks.Filter(OwnPaks.ProjectNames("AdminVisible")), // never this PC's installed studio paks
        });
        var template = ModdableAssets.ReadPackage(catalog, Stove);
        var stock = Assert.Single(TunableReader.Read(template), t => t.Path == AssetModBuilder.VisibleToAdminCommands && t.Export.StartsWith("Default__", StringComparison.Ordinal));
        Assert.False(TunableValue.ParseBool(stock.Value)); // the premise: the game hides it

        var plan = CloneFamilyPlanner.PlanItem(catalog, Stove, "KitchenStove_Mod");
        var result = AssetModBuilder.Build(catalog, new AssetModRequest([plan], new Dictionary<string, IReadOnlyList<TunableEdit>>()));

        Assert.DoesNotContain(result.Warnings, w => w.Contains(AssetModBuilder.VisibleToAdminCommands, StringComparison.Ordinal));
        Assert.True(result.Packages.Count > 0, "nothing built: " + string.Join(" | ", result.Warnings));
        var clone = Assert.Single(result.Packages, p => string.Equals(p.PackagePath, plan.NewPrimary, StringComparison.OrdinalIgnoreCase));
        var package = CookedPackage.Parse(clone.Bytes.UAsset, clone.Bytes.UExp, clone.UBulk, plan.NewPrimary);
        var all = TunableReader.Read(package);
        var flag = all.FirstOrDefault(t => t.Path == AssetModBuilder.VisibleToAdminCommands && t.Export.StartsWith("Default__", StringComparison.Ordinal));
        var dump = "exports: " + string.Join(", ", TunableReader.ExportKeys(package)) + "\napplied: "
            + string.Join(" | ", result.Applied.Select(a => a.Package + " " + a.Key + " " + a.Old + "->" + a.New)) + "\nwarnings: " + string.Join(" | ", result.Warnings)
            + "\nvalues: " + string.Join(" | ", all.Where(t => t.Export.StartsWith("Default__", StringComparison.Ordinal)).Take(40).Select(t => t.Key + "=" + t.Value));
        Assert.True(flag is not null && TunableValue.ParseBool(flag.Value), dump);
        Assert.Contains(result.Applied, a => string.Equals(a.Package, plan.NewPrimary, StringComparison.OrdinalIgnoreCase) && a.Key.Contains(AssetModBuilder.VisibleToAdminCommands, StringComparison.Ordinal));

        Assert.NotNull(result.AssetRegistry);
        var registry = AssetRegistryFile.Parse(result.AssetRegistry);
        Dictionary<string, string> Tags(string objectPath) =>
            registry.GetTags(registry.Assets.Single(a => a.ObjectPath == objectPath)).ToDictionary(t => t.Key, t => t.Value.Text);
        Assert.True(Tags(Stove + ".KitchenStove").ContainsKey(RegistryCloner.HiddenFromAdminCommandsTag));
        Assert.False(Tags(plan.NewPrimary + ".KitchenStove_Mod").ContainsKey(RegistryCloner.HiddenFromAdminCommandsTag));
        Assert.False(Tags(plan.NewPrimary + ".KitchenStove_Mod_C").ContainsKey(RegistryCloner.HiddenFromAdminCommandsTag));
    }
}
