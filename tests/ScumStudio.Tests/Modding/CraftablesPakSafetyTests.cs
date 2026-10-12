using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Export;
using ScumStudio.Level.Import;
using ScumStudio.Level.Projects;
using ScumStudio.Modding;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// P0 #1 / #21: the Craftables pak only adds new packages (F034 cluster, N064), and one clone that cannot be built neither
/// stops the others (N031) nor leaves them pointing at a package that does not ship (N017). Synthetic game files, so these
/// run without the paks.
/// </summary>
public sealed class CraftablesPakSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-craftsafety-" + Guid.NewGuid().ToString("N"));
    private readonly AssetRegistryFile _stock;
    private readonly AssetCatalog _catalog;

    public CraftablesPakSafetyTests()
    {
        _stock = SyntheticGame.WriteGame(Path.Combine(_dir, "game"));
        _catalog = AssetCatalog.OpenLoose(Path.Combine(_dir, "game"));
    }

    public void Dispose()
    {
        _catalog.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AssetModRequest Request(params ClonePlan[] plans) =>
        new(plans, new Dictionary<string, IReadOnlyList<TunableEdit>>()) { BaseRegistry = _stock.Save() };

    private static ClonePlan Plan(string newPrimary, params (string Old, string New)[] packages) =>
        new(packages[0].Old, newPrimary, packages.Select(p => new KeyValuePair<string, string>(p.Old, p.New)).ToList());

    [Fact]
    public void PowerCraftableShipsNoTradeTable()
    {
        // A Power craftable is a clone of the game's generator, which the traders sell (its row is in Table_TradeableDesc).
        var sun = Plan("/Game/T/SS_Sun", (SyntheticGame.Gen, "/Game/T/SS_Sun"), (SyntheticGame.GenEs, "/Game/T/SS_Sun_ES"));
        var sold = AssetModBuilder.Build(_catalog, Request(sun));
        Assert.Contains(sold.Packages, p => p.PackagePath == DataTableEdits.TradeableTable && !p.IsClone); // the main pak sells clones

        var crafted = AssetModBuilder.Build(_catalog, Request(sun) with { SellClones = false });
        Assert.Empty(crafted.Warnings);
        Assert.Equal(["/Game/T/SS_Sun", "/Game/T/SS_Sun_ES"], crafted.Packages.Select(p => p.PackagePath));
        Assert.All(crafted.Packages, p => Assert.True(p.IsClone, p.PackagePath));
        ExportSafety.AssertOnlyAdds(_catalog, crafted.Packages.Select(p => p.PackagePath));
        ExportSafety.AssertRegistrySuperset(_stock, AssetRegistryFile.Parse(crafted.AssetRegistry!), crafted.Registered.Select(r => r.ObjectPath));
    }

    [Fact]
    public void CraftablesRegistryKeepsTheProjectPaksRecords()
    {
        // N106: the Craftables pak loads over the project's pak, so its registry is the project pak's (stock plus the
        // project's clones) plus the craftables'.
        var main = AssetModBuilder.Build(_catalog, new AssetModRequest(
            [Plan("/Game/T/BP_Main", (SyntheticGame.Gen, "/Game/T/BP_Main"), (SyntheticGame.GenEs, "/Game/T/BP_Main_ES"))],
            new Dictionary<string, IReadOnlyList<TunableEdit>>()));
        var crafted = AssetModBuilder.Build(_catalog, Request(Plan("/Game/T/SS_Seat", (SyntheticGame.Gen, "/Game/T/SS_Seat")))
            with { BaseRegistry = main.AssetRegistry, SellClones = false });

        Assert.NotEmpty(main.Registered);
        Assert.NotEmpty(crafted.Registered);
        ExportSafety.AssertRegistrySuperset(_stock, AssetRegistryFile.Parse(crafted.AssetRegistry!),
            main.Registered.Concat(crafted.Registered).Select(r => r.ObjectPath));
    }

    [Fact]
    public void NonCloneAtAnExistingPathIsRefused()
    {
        Assert.Empty(CraftablesExporter.AllowedStockPaths); // reviewed: nothing at a game path may ship in the Craftables pak
        Assert.Equal([DataTableEdits.TradeableTable, SyntheticGame.Gen],
            CraftablesExporter.GamePathsIn(_catalog, [DataTableEdits.TradeableTable, "/Game/T/SS_New", SyntheticGame.Gen, "/Game/ConZ_Files/Economy/table_tradeabledesc"]));
    }

    [Fact]
    public void OneBadCloneDoesNotStopTheOthers()
    {
        var good = Plan("/Game/T/BP_Good", (SyntheticGame.Gen, "/Game/T/BP_Good"), (SyntheticGame.GenEs, "/Game/T/BP_Good_ES"));
        var bad = Plan("/Game/T/BP_Taken", (SyntheticGame.Bad, "/Game/T/BP_Taken")); // its name table has BP_Taken already
        var result = AssetModBuilder.Build(_catalog, Request(bad, good) with { SellClones = false });

        Assert.Equal(["/Game/T/BP_Good", "/Game/T/BP_Good_ES"], result.Packages.Select(p => p.PackagePath));
        var warning = Assert.Single(result.Warnings);
        Assert.StartsWith("/Game/T/BP_Taken: not created", warning, StringComparison.Ordinal);
        Assert.Equal(["/Game/T/BP_Good.BP_Good", "/Game/T/BP_Good.BP_Good_C", "/Game/T/BP_Good_ES.BP_Good_ES_C"], result.Registered.Select(r => r.ObjectPath));
    }

    [Fact]
    public void UnreadableMemberKeepsItsStockPath()
    {
        // The member the game files lack is not cloned, so the clones keep importing its stock path, never its new one.
        var lone = Plan("/Game/T/BP_Lone", (SyntheticGame.Gen, "/Game/T/BP_Lone"), (SyntheticGame.GenEs, "/Game/T/BP_Lone_ES"), (SyntheticGame.Missing, "/Game/T/BP_Lone_Missing"));
        var orphan = Plan("/Game/T/BP_Orphan", ("/Game/T/BP_NotThere", "/Game/T/BP_Orphan"), (SyntheticGame.GenEs, "/Game/T/BP_Orphan_ES"));
        var result = AssetModBuilder.Build(_catalog, Request(lone, orphan) with { SellClones = false });

        Assert.Equal(["/Game/T/BP_Lone", "/Game/T/BP_Lone_ES"], result.Packages.Select(p => p.PackagePath)); // no orphan setup without its Blueprint
        var built = result.Packages.Single(p => p.PackagePath == "/Game/T/BP_Lone");
        var names = CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, null, built.PackagePath).Names;
        Assert.Contains(SyntheticGame.Missing, names);
        Assert.DoesNotContain("/Game/T/BP_Lone_Missing", names);
        Assert.Contains("/Game/T/BP_Lone_ES", names);
        Assert.Contains(result.Warnings, w => w.StartsWith(SyntheticGame.Missing + ": not cloned", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("/Game/T/BP_Orphan: not created", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Registered, r => r.ObjectPath.Contains("Orphan", StringComparison.Ordinal) || r.ObjectPath.Contains("Missing", StringComparison.Ordinal));
    }

    [Fact]
    public void ARedirectedStockItemKeepsItsRecords()
    {
        // An Item craftable's copied recipe makes the chosen stock item: the template's product is redirected to it. Only the
        // copy is registered; the game's records of both items stay as they are.
        var recipe = new ClonePlan(SyntheticGame.Gen, "/Game/T/CR_SS_Rifle", [new(SyntheticGame.Gen, "/Game/T/CR_SS_Rifle")])
        {
            Redirects = [new(SyntheticGame.Stock1, "/Game/S/BP_Stock2")],
        };
        var result = AssetModBuilder.Build(_catalog, Request(recipe) with { SellClones = false });

        Assert.Equal(["/Game/T/CR_SS_Rifle.CR_SS_Rifle", "/Game/T/CR_SS_Rifle.CR_SS_Rifle_C"], result.Registered.Select(r => r.ObjectPath));
        ExportSafety.AssertRegistrySuperset(_stock, AssetRegistryFile.Parse(result.AssetRegistry!), result.Registered.Select(r => r.ObjectPath));
    }

    [Fact]
    public void AStationThatIsNotBuiltLeavesTheRecipesThatNeedIt()
    {
        // A station whose clone fails ships no tag: the recipes that ask for it lose that ingredient (they would import a
        // package that does not ship), and a recipe that asked for nothing else is left out with its craftable.
        const string shopTag = RecipeIngredients.TagFolder + "/CI_SS_Shop";
        var shop = Plan("/Game/T/BP_Taken", (SyntheticGame.Bad, "/Game/T/BP_Taken"), (SyntheticGame.GenEs, shopTag));
        var table = Plan("/Game/T/CR_SS_Table", (SyntheticGame.Recipe, "/Game/T/CR_SS_Table"));
        var stool = Plan("/Game/T/CR_SS_Stool", (SyntheticGame.Recipe, "/Game/T/CR_SS_Stool"));
        var result = AssetModBuilder.Build(_catalog, Request(shop, table, stool) with
        {
            SellClones = false,
            Ingredients = new Dictionary<string, IReadOnlyList<CraftIngredient>>
            {
                ["/Game/T/CR_SS_Table"] = [new("CI_Plank", 3), new("CI_SS_Shop", 1, IsTool: true)],
                ["/Game/T/CR_SS_Stool"] = [new("CI_SS_Shop", 1, IsTool: true)],
            },
        });

        var built = Assert.Single(result.Packages);
        Assert.Equal("/Game/T/CR_SS_Table", built.PackagePath);
        var recipe = CookedPackage.Parse(built.Bytes.UAsset, built.Bytes.UExp, null, built.PackagePath);
        Assert.DoesNotContain(shopTag, recipe.Names);
        Assert.Equal([new CraftIngredient("CI_Plank", 3)], RecipeIngredients.Read(recipe));
        Assert.Contains(result.Warnings, w => w.StartsWith("/Game/T/CR_SS_Table: CI_SS_Shop left out", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("/Game/T/CR_SS_Stool: not created", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportedPathOutsideImportsIsLeftOut()
    {
        var project = Path.Combine(_dir, "project");
        string Model(string token, string file)
        {
            var path = Path.Combine([project, UeCook.ImportsFolder, token, .. file.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1]);
            return UeCook.ImportsFolder + "/" + token;
        }

        var ok = Model("SS_Ok", "SCUM/Content/ScumStudio/Imports/SS_Ok/SM_SS_Ok.uasset");
        Assert.Equal(Path.Combine(project, "imports", "SS_Ok"), CraftablesExporter.ImportedFolder(project, ok, out _));
        var stockPath = Model("SS_Stock", "SCUM/Content/ConZ_Files/Economy/Table_TradeableDesc.uasset");
        var otherToken = Model("SS_Other", "SCUM/Content/ScumStudio/Imports/SS_Ok/SM_SS_Ok.uasset");
        Directory.CreateDirectory(Path.Combine(_dir, "outside", "SCUM"));
        foreach (var bad in new[] { stockPath, otherToken, "imports/../../outside", "../outside", "imports/SS_Ok/SCUM", "imports", "imports/Nope" })
        {
            Assert.Null(CraftablesExporter.ImportedFolder(project, bad, out var problem));
            Assert.NotEmpty(problem);
        }

        Assert.Null(CraftablesExporter.ImportedFolder(null, ok, out _));
    }

    [Fact]
    public async Task NothingBuiltKeepsThePreviousPak()
    {
        // A Craftables export that builds nothing stops with every reason and leaves the pak players already have.
        var output = Path.Combine(_dir, "out");
        var role = Path.Combine(output, ProjectExporter.RoleFolder(ProjectSourceRole.Client));
        Directory.CreateDirectory(role);
        var pak = Path.Combine(role, CraftablesExporter.PakFileName("MyMod", 900));
        File.WriteAllText(pak, "old");
        File.WriteAllText(Path.ChangeExtension(pak, ".sig"), "old");
        var project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(Path.Combine(project, "outside", "SCUM"));
        var craftables = new CraftablesFile
        {
            ProjectDirectory = project,
            Items =
            [
                // An item: a placed craftable without its mesh stops the export by itself (CraftableExportException).
                new Craftable { Name = "Gone Table", Source = "/Game/X/SM_Gone", Mesh = "/Game/X/SM_Gone", Kind = CraftKind.Item, Ingredients = [new("CI_Plank", 3)] },
                new Craftable { Name = "Escaped", Source = "x.obj", Mesh = "/Game/ScumStudio/Imports/SS_Escaped/SM_SS_Escaped", Imported = "../outside", Ingredients = [new("CI_Plank", 3)] },
            ],
        };

        var staged = Path.Combine(role, CraftablesExporter.StagingFolderName, "SCUM", "AssetRegistry.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "old");

        // The check before the project's pak is written stops the same way, so neither pak of the cook is written.
        var early = Assert.Throws<InvalidOperationException>(() => CraftablesExporter.Check(craftables, _catalog));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CraftablesExporter.ExportAsync(craftables, "MyMod", _catalog, new ExportOptions { OutputDirectory = output, PakChunkIndex = 900 }, ProjectSourceRole.Client));
        Assert.Equal(error.Message, early.Message);
        Assert.StartsWith("No craftable was built.", error.Message, StringComparison.Ordinal);
        Assert.Contains("Gone Table: left out", error.Message, StringComparison.Ordinal);
        Assert.Contains("Escaped: left out (its imported model ../outside is not imports/<name>", error.Message, StringComparison.Ordinal);
        Assert.Equal("old", File.ReadAllText(pak));
        Assert.True(File.Exists(Path.ChangeExtension(pak, ".sig")));
        Assert.Equal("old", File.ReadAllText(staged)); // refused before anything is written
        CraftablesExporter.Check(new CraftablesFile(), _catalog);
    }
}
