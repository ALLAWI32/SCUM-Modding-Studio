using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Craftables on the real game files (<c>SCUM_PAKS</c> and the key; skipped otherwise): a DLC mesh is imported by the game's
/// mount path (N032), the station and power templates show the craftable's mesh (F168), and an item recipe cloned from a
/// numbered template is named after the craftable (F038).
/// </summary>
public sealed class CraftablesRealTests
{
    private const string Table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";

    private static AssetCatalog Open() => AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });

    [PaksFact]
    public async Task ADlcMeshIsImportedByTheGamesMountPath()
    {
        using var catalog = Open();
        var index = catalog.Index ?? catalog.BuildIndex(resolveClasses: false);
        var mesh = index.Entries.Select(e => e.PackagePath)
            .FirstOrDefault(p => p.StartsWith("/SCUM/Plugins/GameFeatures/", StringComparison.OrdinalIgnoreCase)
                && p[(p.LastIndexOf('/') + 1)..].StartsWith("SM_", StringComparison.OrdinalIgnoreCase) && RecipeRules.IsAllowedSource(p, "StaticMesh"));
        Assert.True(mesh is not null, "no DLC static mesh outside weapons and characters: N032 cannot happen with these paks");

        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftdlc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new CraftablesFile { Items = [new Craftable { Name = "Dlc Prop", Source = mesh!, Mesh = mesh!, Ingredients = [new("CI_Plank", 2)] }] };
            var result = await CraftablesExporter.ExportAsync(file, "MyMod", catalog, new ExportOptions { OutputDirectory = dir, WritePak = false }, ProjectSourceRole.Client);
            var asset = Assert.Single(result!.Assets, a => a.PackagePath.EndsWith("/BP_SS_Dlc_Prop", StringComparison.Ordinal));
            var staged = Path.Combine([result.StagingDirectory, .. asset.VirtualPath.Split('/')]);
            var element = CookedPackage.Parse(File.ReadAllBytes(staged), File.ReadAllBytes(Path.ChangeExtension(staged, ".uexp")), null, asset.PackagePath);
            var names = element.Imports.Select(i => element.ResolveName(i.ObjectName)).ToList();
            Assert.Contains(CraftablesPlanner.GamePath(mesh!), names);
            Assert.DoesNotContain(names, n => n.StartsWith("/SCUM/Plugins/", StringComparison.OrdinalIgnoreCase));
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
    public void StationAndPowerRedirectTheirMesh()
    {
        using var catalog = Open();
        var plan = CraftablesPlanner.Plan(catalog,
        [
            new Craftable { Name = "Bench", Source = Table, Mesh = Table, Kind = CraftKind.Station, Ingredients = [new("CI_Plank", 4)] },
            new Craftable { Name = "Dynamo", Source = Table, Mesh = Table, Kind = CraftKind.Power, Ingredients = [new("CI_Metal_Scrap", 4)] },
        ]);

        Assert.Equal(["Bench", "Dynamo"], plan.Entries.Select(e => e.Name));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("keeps the stock look", StringComparison.Ordinal));
        Assert.All(plan.Request.Clones, c => Assert.Contains(c.Redirects, r => r.Value == Table));
    }

    /// <summary>The bow recipe template is the numbered FName ('CR_Improvised_Bow', 21); passes once the clone renames numbered leaves (B1).</summary>
    [PaksFact]
    public async Task ABowRecipeIsNamedAfterTheCraftable()
    {
        using var catalog = Open();
        const string bow = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Compound_Bow_Desert";
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftbow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new CraftablesFile { Items = [new Craftable { Name = "Desert Bow", Source = bow, Mesh = bow, Kind = CraftKind.Item, Ingredients = [new("CI_Plank", 2)] }] };
            var plan = CraftablesPlanner.Plan(catalog, file.Items);
            var entry = Assert.Single(plan.Entries);
            Assert.EndsWith("/CR_Improvised_Bow_20", plan.Request.Clones.Single().Template, StringComparison.Ordinal);

            var result = await CraftablesExporter.ExportAsync(file, "MyMod", catalog, new ExportOptions { OutputDirectory = dir, WritePak = false }, ProjectSourceRole.Client);
            var asset = Assert.Single(result!.Assets, a => a.PackagePath == entry.Recipe);
            var staged = Path.Combine([result.StagingDirectory, .. asset.VirtualPath.Split('/')]);
            var recipe = CookedPackage.Parse(File.ReadAllBytes(staged), File.ReadAllBytes(Path.ChangeExtension(staged, ".uexp")), null, asset.PackagePath);
            Assert.Equal(PackageMap.Leaf(entry.Recipe), recipe.ResolveName(recipe.Exports[RecipeIngredients.RecipeExport(recipe)].ObjectName));
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
