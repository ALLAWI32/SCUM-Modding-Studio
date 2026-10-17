using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Crafting;
using ScumStudio.Tests.Pak;

namespace ScumStudio.Tests.Modding;

/// <summary>
/// HexTer (2026-10-12, "customize the names of crafting recipes and furniture items ... before exporting the mod"): a
/// craftable's asset name and folder are chosen until its first export, checked against the FName number rule, the other
/// craftables and the game, written by the planner, and fixed once an export recorded it.
/// </summary>
public sealed class CraftableNamesTests
{
    [Fact]
    public void NamesFollowTheEngineRules()
    {
        Assert.Equal(CraftNameProblem.None, CraftableNames.Check("HexTer_Chair", [], null).Problem);
        Assert.Equal(CraftNameProblem.Characters, CraftableNames.Check("Hex Chair", [], null).Problem);
        Assert.Equal(CraftNameProblem.Characters, CraftableNames.Check("1Chair", [], null).Problem);
        Assert.Equal(CraftNameProblem.Characters, CraftableNames.Check("Стул", [], null).Problem);
        Assert.Equal(CraftNameProblem.Length, CraftableNames.Check(new string('A', 65), [], null).Problem);
        Assert.Equal((CraftNameProblem.Number, "Shelf_v2"), CraftableNames.Check("Shelf_2", [], null));
        Assert.Equal(CraftNameProblem.None, CraftableNames.Check("Shelf2", [], null).Problem);
    }

    [Fact]
    public void ANameClashesWithAnotherCraftableOrTheGame()
    {
        var bench = new Craftable { Name = "Bench", Token = "Bench", Source = "x", Mesh = "x" };
        Assert.Equal((CraftNameProblem.Craftable, "Bench"), CraftableNames.Check("bench", [bench], null));
        Assert.Equal((CraftNameProblem.Craftable, "Bench"), CraftableNames.Check("Bench_ES", [bench], null)); // a station's item setup
        var game = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CR_Chair_Improvised_Wood"] = "/Game/ConZ_Files/Items/Crafting/Recipes/Placeables/CR_Chair_Improvised_Wood" };
        Assert.Equal((CraftNameProblem.Game, game["CR_Chair_Improvised_Wood"]), CraftableNames.Check("Chair_Improvised_Wood", [], game));
    }

    [Fact]
    public void FoldersAreCheckedAndNormalized()
    {
        Assert.Equal("Furniture/Lights", CraftableNames.NormalizeFolder(@" \Furniture\\Lights/ "));
        Assert.Null(CraftableNames.NormalizeFolder(" / "));
        Assert.True(CraftableNames.IsValidFolder(null));
        Assert.True(CraftableNames.IsValidFolder("HexTer/Lights"));
        Assert.Equal(CraftNameProblem.Number, CraftableNames.CheckFolder("Lights_2"));
        Assert.Equal(CraftNameProblem.Characters, CraftableNames.CheckFolder("My Lights"));
        Assert.Equal(CraftNameProblem.Length, CraftableNames.CheckFolder("A/B/C/D"));
    }

    [Fact]
    public void ExportedIsStoredAndOldFilesAreLocked()
    {
        var file = CraftablesFile.Parse("""{ "Items": [ { "Name": "Old", "Token": "SS_Old", "Source": "x", "Mesh": "x" } ] }""");
        Assert.True(Assert.Single(file.Items).IsLocked); // a file from before 3.3: it may have been exported
        var fresh = file.Items[0] with { Exported = false, Folder = "Furniture" };
        Assert.False(fresh.IsLocked);
        var again = Assert.Single(CraftablesFile.Parse((file with { Items = [fresh] }).ToJson()).Items);
        Assert.Equal((false, "Furniture"), (again.Exported, again.Folder));
        Assert.DoesNotContain("IsLocked", file.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExportMarkAlwaysStays()
    {
        using var temp = new TempDirectory();
        var chair = CraftFixture.Chair("Chair", "SS_Chair") with { Exported = false };
        new CraftablesFile { Items = [chair] }.Save(temp.Path);

        Assert.Equal(["SS_Chair"], CraftablesFile.MarkExported(temp.Path, ["SS_Chair", "SS_Gone"]));
        Assert.True(Assert.Single(CraftablesFile.Load(temp.Path).Items).Exported);

        // A page that read the file before the export saves its own list: the mark is kept.
        new CraftablesFile { Items = [chair with { Name = "Wooden Chair" }] }.Save(temp.Path);
        var saved = Assert.Single(CraftablesFile.Load(temp.Path).Items);
        Assert.Equal(("Wooden Chair", true), (saved.Name, saved.Exported));
        Assert.Empty(CraftablesFile.MarkExported(temp.Path, ["SS_Chair"]));
    }

    [Fact]
    public void TheFolderHoldsTheRecipeAndTheElement()
    {
        using var temp = new TempDirectory();
        using var catalog = AssetCatalog.OpenLoose(CraftFixture.WriteLoose(temp.Path, CraftFixture.Stock()));
        var plan = CraftablesPlanner.Plan(catalog,
        [
            CraftFixture.Chair("Lamp", "HexTer_Lamp") with { Folder = "HexTer/Lights", Exported = false },
            CraftFixture.Chair("Bench", "HexTer_Bench") with { Kind = CraftKind.Station, Folder = "Stations", Exported = false },
            CraftFixture.Chair("Old Chair", "SS_Old_Chair"), // before 3.3: beside the templates, as exported then
        ]);

        Assert.DoesNotContain(plan.Warnings, w => w.Contains("left out", StringComparison.Ordinal));
        Assert.Equal(3, plan.Entries.Count);
        var bench = plan.Entries.Single(e => e.Name == "Bench");
        Assert.Equal(CraftFixture.Placeables + "Stations/CR_HexTer_Bench", bench.Recipe);
        Assert.Equal(CraftFixture.Elements + "Stations/BP_HexTer_Bench", bench.Product);
        Assert.Equal("/Game/ConZ_Files/Items/Equipment/Active_Items/Stations/HexTer_Bench", bench.Item);
        Assert.Equal(RecipeIngredients.TagFolder + "/CI_HexTer_Bench", bench.Tag); // tags stay where recipes look for them
        var lamp = plan.Entries.Single(e => e.Name == "Lamp");
        Assert.Equal((CraftFixture.Placeables + "HexTer/Lights/CR_HexTer_Lamp", CraftFixture.Elements + "HexTer/Lights/BP_HexTer_Lamp", "HexTer_Lamp"), (lamp.Recipe, lamp.Product, lamp.Token));
        var old = plan.Entries.Single(e => e.Name == "Old Chair");
        Assert.Equal((CraftFixture.Placeables + "CR_SS_Old_Chair", CraftFixture.Elements + "BP_SS_Old_Chair"), (old.Recipe, old.Product));
    }

    [Fact]
    public void ANameTheGameHasInAnotherFolderStopsTheExport()
    {
        using var temp = new TempDirectory();
        using var catalog = AssetCatalog.OpenLoose(CraftFixture.WriteLoose(temp.Path, CraftFixture.Stock()));

        // BP_Chair_Improvised_Wood exists beside the template: in a folder of its own the clone would still clash by name.
        var clash = Assert.Throws<CraftableExportException>(() => CraftablesPlanner.Plan(catalog, [CraftFixture.Chair("Copy", "Chair_Improvised_Wood") with { Folder = "Mine", Exported = false }]));
        Assert.Contains("give the craftable another asset name", clash.Message, StringComparison.Ordinal);
        Assert.Throws<CraftableExportException>(() => CraftablesPlanner.Plan(catalog, [CraftFixture.Chair("Bad", "SS_Bad") with { Folder = "My Folder" }]));
    }

    [Fact]
    public void ANewCraftableGetsAFolderFromWhatItIs()
    {
        Craftable Of(string source, CraftKind kind) => new() { Name = "x", Source = source, Mesh = source, Kind = kind };
        Assert.Equal("Beds", CraftableNames.SuggestFolder(Of("/Game/ConZ_Files/BaseBuilding/BaseElements/BP_Bed_Improvised", CraftKind.Element)));
        Assert.Equal("Lights", CraftableNames.SuggestFolder(Of("/Game/ConZ_Files/Models/Lamp/SM_Floor_Lamp_01", CraftKind.Furniture)));
        Assert.Equal("Stations", CraftableNames.SuggestFolder(Of(CraftFixture.Table, CraftKind.Station)));
        Assert.Equal("Furniture", CraftableNames.SuggestFolder(Of(CraftFixture.Table, CraftKind.Furniture)));
        Assert.Equal("Storage", CraftableNames.SuggestFolder(Of(ItemStorage.Fridge, CraftKind.Element) with { Element = new ElementSettings { Template = ItemStorage.Carrier, Storage = new StorageSettings { Item = ItemStorage.Fridge } } }));

        // The game keeps no item recipe in a sub-folder: an item's recipe stays beside the game's unless one is chosen.
        Assert.Null(CraftableNames.SuggestFolder(Of("/Game/ConZ_Files/Items/Clothes/Jackets_Coats/Camouflage_Jacket_01", CraftKind.Item)));
        Assert.Null(CraftableNames.SuggestFolder(Of(CraftFixture.Table, CraftKind.Power)));
    }

    [Fact]
    public void ABuiltElementTakesTheBuildingSections()
    {
        // A bed, a chest or the large fridge is built from the game's placeable recipe: "Made in" says built, and only the
        // building sections of the crafting menu are offered.
        Assert.True(CraftCategories.IsPlaceable(CraftKind.Element));
        var fridge = new Craftable { Name = "Big Fridge", Source = ItemStorage.Fridge, Mesh = ItemStorage.Fridge, Kind = CraftKind.Element };
        CraftCategory[] sections =
        [
            new("CraftingCategory.BaseBuilding.Storage", "Storage", "Base building", true, 1, null, []),
            new("CraftingCategory.Items.Food", "Food", "Items", false, 1, null, []),
        ];
        Assert.Null(CraftCategories.Refusal(fridge, "CraftingCategory.BaseBuilding.Storage", sections));
        Assert.NotNull(CraftCategories.Refusal(fridge, "CraftingCategory.Items.Food", sections));
    }

    [Fact]
    public async Task AWrittenPakMarksItsCraftablesExported()
    {
        using var temp = new TempDirectory();
        using var catalog = AssetCatalog.OpenLoose(CraftFixture.WriteLoose(Path.Combine(temp.Path, "game"), CraftFixture.Stock()));
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
        new CraftablesFile { Items = [CraftFixture.Chair("Lamp", "HexTer_Lamp") with { Folder = "Lights", Exported = false }] }.Save(project);

        // A staging-only run ships nothing: nothing is fixed yet.
        await CraftablesExporter.ExportAsync(CraftablesFile.Load(project), "MyMod", catalog, new ExportOptions { OutputDirectory = Path.Combine(temp.Path, "dry"), WritePak = false }, ProjectSourceRole.Client);
        Assert.False(Assert.Single(CraftablesFile.Load(project).Items).Exported);

        var result = await CraftablesExporter.ExportAsync(CraftablesFile.Load(project), "MyMod", catalog, new ExportOptions { OutputDirectory = Path.Combine(temp.Path, "out") }, ProjectSourceRole.Client);
        Assert.True(File.Exists(result!.PakPath));
        Assert.Contains(result.Assets, a => a.PackagePath == CraftFixture.Placeables + "Lights/CR_HexTer_Lamp");
        var marked = Assert.Single(CraftablesFile.Load(project).Items);
        Assert.True(marked.Exported);
        Assert.True(marked.IsLocked);
    }
}
