using ScumStudio.Assets.Catalog;
using ScumStudio.Tests.Fixtures;

namespace ScumStudio.Tests.Assets;

/// <summary>The Dump feature: the embedded catalogue and the raw-file extraction of chosen categories.</summary>
public sealed class AssetDumperTests
{
    [Fact]
    public void CatalogueHasTheTopCategoriesAndSelectsSubCategories()
    {
        Assert.Equal(26, AssetDumper.Tree.Count);
        Assert.Contains(AssetDumper.Tree, n => n.Id == "weapons" && n.Title == "Weapons");
        Assert.True(AssetDumper.Packages.Count > 100_000);
        var weapons = AssetDumper.Select(["weapons"]);
        Assert.Contains(weapons, p => p.PackagePath.EndsWith("/Weapon_RPK-74", StringComparison.Ordinal));
        Assert.All(weapons, p => Assert.True(p.Node == "weapons" || p.Node.StartsWith("weapons.", StringComparison.Ordinal), p.Node));
    }

    [FixturesFact]
    public void DumpWritesEveryFileOfAPackageAndAManifest()
    {
        using var catalog = AssetCatalog.OpenLoose(FixturePaths.OrigRoot);
        var rpk = AssetDumper.Packages.Single(p => p.PackagePath == "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_RPK-74");
        var output = Path.Combine(Path.GetTempPath(), "scumstudio-dump-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = AssetDumper.Dump(catalog, [rpk, new DumpPackage("/Game/Does/Not/Exist", "dev", "Blueprint")], output);
            Assert.Equal(1, result.Packages);
            Assert.Equal(1, result.Missing);
            Assert.True(result.Files >= 2); // .uasset + .uexp
            Assert.True(File.Exists(Path.Combine(output, "SCUM", "Content", "ConZ_Files", "Items", "Weapons", "Ranged_Weapons", "Weapon_RPK-74.uexp")));
            Assert.Contains("/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_RPK-74,", File.ReadAllText(result.ManifestPath), StringComparison.Ordinal);
            Assert.Equal(result.Files, AssetDumper.Dump(catalog, [rpk], output).Files); // second run skips what is there
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }
}

/// <summary>The Dump window's tree: ticking a category ticks its sub-categories, and only the top-most ticked ids are dumped.</summary>
public sealed class DumpTreeTests
{
    [Fact]
    public void TickingACategoryTicksItsChildrenAndDumpsItOnce()
    {
        var weapons = new ScumStudio.App.ViewModels.DumpNodeViewModel(AssetDumper.Tree.Single(n => n.Id == "weapons"));
        Assert.Empty(weapons.CheckedIds());
        weapons.Children[0].IsChecked = true;
        Assert.Equal([weapons.Children[0].Node.Id], weapons.CheckedIds());
        weapons.IsChecked = true;
        Assert.All(weapons.Children, c => Assert.True(c.IsChecked));
        Assert.Equal(["weapons"], weapons.CheckedIds());
    }
}
