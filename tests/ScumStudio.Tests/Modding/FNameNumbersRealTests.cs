using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// FName numbers on the real game files (<c>SCUM_PAKS</c> and the key): what the studio writes for numbered meshes, the
/// numbered bow recipe template and the AK-15 mount matches the cooker's (text, number) form.
/// </summary>
public sealed partial class FNameNumbersRealTests
{
    [PaksFact]
    public void AFurnitureCraftableOnANumberedMeshImportsItSplit()
    {
        using var catalog = Open();
        var mesh = Find(catalog, p => p.Contains("/Models/", StringComparison.OrdinalIgnoreCase) && FNameRef.Split(PackageMap.Leaf(p)).Number != 0
                                      && PackageMap.Leaf(p).StartsWith("SM_", StringComparison.OrdinalIgnoreCase));
        var plan = CraftablesPlanner.Plan(catalog, [new Craftable { Name = "Numbered Block", Source = mesh, Mesh = mesh }]);
        var entry = Assert.Single(plan.Entries);
        var built = AssetModBuilder.Build(catalog, plan.Request);
        var element = Parse(built.Packages.Single(p => string.Equals(p.PackagePath, entry.Product, StringComparison.OrdinalIgnoreCase)));

        var import = Assert.Single(element.Imports, im => string.Equals(im.ObjectName.Format(element.Names), mesh, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(FNameRef.Split(mesh), (element.Names[import.ObjectName.Index], import.ObjectName.Number));
        Assert.Empty(PackageLint.Check(element));
    }

    [PaksFact]
    public void TheBowRecipeCloneIsNamedAfterItsNewLeaf()
    {
        using var catalog = Open();
        var bow = Find(catalog, p => p.Contains("/Items/Weapons/", StringComparison.OrdinalIgnoreCase) && BowLeaf().IsMatch(PackageMap.Leaf(p))
                                     && CraftablesPlanner.ItemTemplate(catalog, p).EndsWith("/CR_Improvised_Bow_20", StringComparison.OrdinalIgnoreCase));
        var plan = CraftablesPlanner.Plan(catalog, [new Craftable { Name = "Gold Bow", Source = bow, Mesh = bow, Kind = CraftKind.Item }]);
        var entry = Assert.Single(plan.Entries);
        var built = AssetModBuilder.Build(catalog, plan.Request);
        var recipe = Parse(built.Packages.Single(p => string.Equals(p.PackagePath, entry.Recipe, StringComparison.OrdinalIgnoreCase)));

        Assert.Contains(recipe.Exports, e => e.OuterIndex == 0 && (recipe.Names[e.ObjectName.Index], e.ObjectName.Number) == (PackageMap.Leaf(entry.Recipe), 0));
        Assert.DoesNotContain(recipe.Exports, e => e.ObjectName.Format(recipe.Names) == "CR_Improvised_Bow_20");
    }

    [PaksFact]
    public void TheAk15MountImportedIntoTheAk47IsTheMagazinesFName()
    {
        using var catalog = Open();
        var magazine = ModdableAssets.ReadPackage(catalog, Find(catalog, p => PackageMap.Leaf(p).Equals("Magazine_AK15", StringComparison.OrdinalIgnoreCase)));
        var imports = magazine.Imports.ToList();
        var mount = Assert.Single(imports, im => im.OuterIndex == 0 && im.ObjectName.Format(magazine.Names).Contains("AK15_762", StringComparison.OrdinalIgnoreCase));
        var mountPath = mount.ObjectName.Format(magazine.Names);
        var cls = imports.First(im => im.OuterIndex == -(imports.IndexOf(mount) + 1) && im.ClassName.Format(magazine.Names) == "BlueprintGeneratedClass");

        var ak47 = ModdableAssets.ReadPackage(catalog, Find(catalog, p => PackageMap.Leaf(p).Equals("Weapon_AK47", StringComparison.OrdinalIgnoreCase)));
        var tables = PackageTables.Of(ak47, 0);
        var index = tables.Import(cls.ClassPackage.Format(magazine.Names), cls.ClassName.Format(magazine.Names), mountPath + "." + cls.ObjectName.Format(magazine.Names));
        var package = tables.Imports[-tables.Imports[-index - 1].OuterIndex - 1];

        Assert.Equal((magazine.Names[mount.ObjectName.Index], mount.ObjectName.Number), (tables.Names[package.ObjectName.Index], package.ObjectName.Number));
    }

    private static AssetCatalog Open() =>
        AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    private static string Find(AssetCatalog catalog, Func<string, bool> match) =>
        catalog.PackageFiles.Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName)).First(match);

    private static CookedPackage Parse(BuiltAssetPackage package) =>
        CookedPackage.Parse(package.Bytes.UAsset, package.Bytes.UExp, package.UBulk, package.PackagePath);

    [GeneratedRegex(@"_Bow(_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BowLeaf();
}
