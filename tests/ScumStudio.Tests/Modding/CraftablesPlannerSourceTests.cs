using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Crafting;
using ScumStudio.Tests.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// Where what a craftable imports comes from (audit N024, N027, N032, F168): a path the source already has stops the export
/// and names the pak, a DLC mesh is imported by the game's mount path, a placed craftable whose mesh or whole recipe is not
/// in every player's game stops the export while an item craftable or a single tag is left out, and a template that shows no
/// mesh is reported.
/// </summary>
public sealed class CraftablesPlannerSourceTests
{
    [Fact]
    public async Task AlreadyExistsIsAnErrorNamingTheContainer()
    {
        // P0 #1: an earlier Craftables pak in the folder the studio reads (or another mod with the same path) made every
        // craftable "already exist in the game files": it was left out and the new pak dropped what players had built.
        using var temp = new TempDirectory();
        var paks = temp.Combine("Paks");
        await CraftFixture.WritePakAsync(paks, "pakchunk0-WindowsNoEditor.pak", CraftFixture.Stock());
        var taken = CraftFixture.Placeables + "CR_SS_Chair";
        await CraftFixture.WritePakAsync(paks, "pakchunk901-OtherCraftables_P.pak", new Dictionary<string, PackageBytes> { [taken] = CraftFixture.Package("CR_SS_Chair") });
        using var catalog = AssetCatalog.OpenPaks(paks);

        var conflict = Assert.Throws<CraftableConflictException>(() => CraftablesPlanner.Plan(catalog, [CraftFixture.Chair("Chair")]));
        Assert.Equal("Chair", conflict.Craftable);
        Assert.Equal(taken, conflict.PackagePath);
        Assert.Equal("pakchunk901-OtherCraftables_P.pak", conflict.Container);
        Assert.Contains("pakchunk901-OtherCraftables_P.pak", conflict.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<InvalidOperationException>(conflict); // the export reports it as its error

        // The exporter lets it through: no Craftables pak without the craftables players already have.
        var settings = new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false };
        var file = new CraftablesFile { Items = [CraftFixture.Chair("Chair"), CraftFixture.Chair("Stool")] };
        await Assert.ThrowsAsync<CraftableConflictException>(() => CraftablesExporter.ExportAsync(file, "MyMod", catalog, settings, ProjectSourceRole.Client));
    }

    [Fact]
    public void DlcMeshRedirectUsesTheGamePath()
    {
        // N032: the catalog lists a DLC mesh under /SCUM/Plugins/GameFeatures/<Pack>/Content/; the game mounts it at /<Pack>/.
        using var temp = new TempDirectory();
        const string lamp = "/SCUM/Plugins/GameFeatures/LampPack/Content/Props/SM_Lamp";
        var stock = CraftFixture.Stock();
        stock[lamp] = CraftFixture.Package("SM_Lamp");
        using var catalog = AssetCatalog.OpenLoose(CraftFixture.WriteLoose(temp.Path, stock));
        Assert.True(catalog.PackageExists(lamp));

        var plan = CraftablesPlanner.Plan(catalog, [CraftFixture.Chair("Lamp", mesh: lamp)]);
        Assert.Empty(plan.Warnings);
        var clone = Assert.Single(plan.Request.Clones);
        Assert.Contains(new KeyValuePair<string, string>(CraftFixture.StockMesh, "/LampPack/Props/SM_Lamp"), clone.Redirects);
        Assert.DoesNotContain(clone.Redirects, r => r.Value.StartsWith("/SCUM/Plugins/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MeshOnlyInAThirdPartyPakStopsTheExport()
    {
        // N027: players without that mod pak would get an element with no mesh, or a recipe slot with no tag. Leaving the
        // sofa out would remove every sofa players placed if an earlier pak shipped it, so the export stops and names the pak.
        using var temp = new TempDirectory();
        var paks = temp.Combine("Paks");
        const string sofa = "/Game/Mods/Furniture/SM_Sofa";
        const string lamp = "/Game/Mods/Lights/SM_Lamp";
        await CraftFixture.WritePakAsync(paks, "pakchunk0-WindowsNoEditor.pak", CraftFixture.Stock());
        await CraftFixture.WritePakAsync(paks, "pakchunk99-Furniture_P.pak", new Dictionary<string, PackageBytes>
        {
            [sofa] = CraftFixture.Package("SM_Sofa"),
            [RecipeIngredients.TagFolder + "/CI_Velvet"] = CraftFixture.Package("CI_Velvet"),
        });
        var mod = CraftFixture.WriteLoose(temp.Combine("mods", "LightsMod"), new Dictionary<string, PackageBytes> { [lamp] = CraftFixture.Package("SM_Lamp") });
        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { LooseOverlays = [mod] });

        foreach (var kind in new[] { CraftKind.Furniture, CraftKind.Station, CraftKind.Power })
        {
            var stop = Assert.Throws<CraftableExportException>(() => CraftablesPlanner.Plan(catalog, [CraftFixture.Chair("Chair"), CraftFixture.Chair("Sofa", mesh: sofa) with { Kind = kind }]));
            Assert.Equal("Sofa", stop.Craftable);
            Assert.StartsWith($"Sofa: its mesh {sofa} is only in the mod pak pakchunk99-Furniture_P.pak", stop.Message, StringComparison.Ordinal);
        }

        var settings = new ExportOptions { OutputDirectory = temp.Combine("out"), WritePak = false };
        var file = new CraftablesFile { Items = [CraftFixture.Chair("Chair"), CraftFixture.Chair("Sofa", mesh: sofa)] };
        await Assert.ThrowsAsync<CraftableExportException>(() => CraftablesExporter.ExportAsync(file, "MyMod", catalog, settings, ProjectSourceRole.Client));

        // An item craftable ships only a recipe (what players crafted is the game's item): it is still left out.
        var plan = CraftablesPlanner.Plan(catalog,
        [
            new Craftable { Name = "Sofa Kit", Source = sofa, Mesh = sofa, Kind = CraftKind.Item, Ingredients = [new("CI_Plank", 3)] },
            CraftFixture.Chair("Chair") with { Ingredients = [new("CI_Plank", 3), new("CI_Velvet", 2)] },
            CraftFixture.Chair("Lamp", mesh: lamp),
        ]);

        Assert.DoesNotContain(plan.Entries, e => e.Name == "Sofa Kit");
        Assert.Contains(plan.Warnings, w => w.StartsWith("Sofa Kit: left out", StringComparison.Ordinal) && w.Contains("pakchunk99-Furniture_P.pak", StringComparison.Ordinal));
        var chair = plan.Entries.Single(e => e.Name == "Chair");
        Assert.Equal(new CraftIngredient[] { new("CI_Plank", 3) }, plan.Request.Ingredients[chair.Recipe]);
        Assert.Contains(plan.Warnings, w => w.StartsWith("Chair: the ingredient CI_Velvet", StringComparison.Ordinal) && w.Contains("pakchunk99-Furniture_P.pak", StringComparison.Ordinal));

        // An imported mod's mesh is kept: it ships with that mod in the project's main pak, which the warning says.
        Assert.Contains(plan.Entries, e => e.Name == "Lamp");
        Assert.Contains(plan.Warnings, w => w.StartsWith("Lamp:", StringComparison.Ordinal) && w.Contains("LightsMod", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMeshTheStudioDoesNotReadOrABareRecipeStopsTheExport()
    {
        // Review of p0r1: a mesh only another studio mod (pakchunk9xx, not read as game files) has is not found at all, and a
        // recipe whose every tag is left out would leave the craftable out as well.
        using var temp = new TempDirectory();
        var paks = temp.Combine("Paks");
        const string crate = "/Game/Mods/Crates/SM_Crate";
        await CraftFixture.WritePakAsync(paks, "pakchunk0-WindowsNoEditor.pak", CraftFixture.Stock());
        await CraftFixture.WritePakAsync(paks, "pakchunk950-Crates_P.pak", new Dictionary<string, PackageBytes>
        {
            [crate] = CraftFixture.Package("SM_Crate"),
            [RecipeIngredients.TagFolder + "/CI_Crate_Slat"] = CraftFixture.Package("CI_Crate_Slat"),
        });
        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { PakFileFilter = f => !f.StartsWith("pakchunk9", StringComparison.OrdinalIgnoreCase) });
        Assert.False(catalog.PackageExists(crate));

        var missing = Assert.Throws<CraftableExportException>(() => CraftablesPlanner.Plan(catalog, [CraftFixture.Chair("Crate", mesh: crate)]));
        Assert.StartsWith($"Crate: its mesh {crate} is not in the game files.", missing.Message, StringComparison.Ordinal);

        var bare = Assert.Throws<CraftableExportException>(() => CraftablesPlanner.Plan(catalog, [CraftFixture.Chair("Stool") with { Ingredients = [new("CI_Crate_Slat", 2)] }]));
        Assert.Equal("Stool", bare.Craftable);
        Assert.Contains("CI_Crate_Slat is not in the game files", bare.Message, StringComparison.Ordinal);

        // One tag of several is only left out of the recipe.
        var plan = CraftablesPlanner.Plan(catalog, [CraftFixture.Chair("Stool") with { Ingredients = [new("CI_Plank", 2), new("CI_Crate_Slat", 2)] }]);
        Assert.Equal(new CraftIngredient[] { new("CI_Plank", 2) }, plan.Request.Ingredients[Assert.Single(plan.Entries).Recipe]);
    }

    [Fact]
    public void StationWithoutAMeshImportWarns()
    {
        // F168: a template whose mesh is not a hard StaticMesh import keeps the stock look; the export now says so.
        using var temp = new TempDirectory();
        using var catalog = AssetCatalog.OpenLoose(CraftFixture.WriteLoose(temp.Path, CraftFixture.Stock()));
        var plan = CraftablesPlanner.Plan(catalog,
        [
            new Craftable { Name = "Bench", Source = CraftFixture.Table, Mesh = CraftFixture.Table, Kind = CraftKind.Station, Ingredients = [new("CI_Plank", 5)] },
            CraftFixture.Chair("Chair"),
        ]);

        Assert.Equal(["Bench", "Chair"], plan.Entries.Select(e => e.Name));
        Assert.Equal(["Bench: keeps the stock look (the template shows no static mesh import)."], plan.Warnings);
        var chair = plan.Request.Clones.Single(c => c.Template == CraftablesPlanner.FurnitureRecipe);
        Assert.Contains(new KeyValuePair<string, string>(CraftFixture.StockMesh, CraftFixture.Table), chair.Redirects);
    }
}
