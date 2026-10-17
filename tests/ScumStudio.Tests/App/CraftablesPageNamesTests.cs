using ScumStudio.App.ViewModels;
using ScumStudio.Modding.Crafting;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Tests.App;

/// <summary>
/// HexTer's own asset names and folders on the Craftables page: typed until the first export (a name the engine would split
/// or another craftable has is shown, not used), fixed once the project file says the craftable was exported, and an old
/// project's craftables keep their names.
/// </summary>
public sealed class CraftablesPageNamesTests
{
    private const string Table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";

    private static string Row(string name, string token, string? exported) =>
        $$"""{ "Name": "{{name}}", "Token": "{{token}}", "Source": "{{Table}}", "Mesh": "{{Table}}"{{(exported is null ? string.Empty : ", \"Exported\": " + exported)}} }""";

    [Fact]
    public async Task ANewCraftableTakesItsOwnNameAndFolder()
    {
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
        File.WriteAllText(Path.Combine(project.DirectoryPath, CraftablesFile.FileName), $$"""{ "Items": [ {{Row("Chair", "SS_Chair", "false")}}, {{Row("Bench", "SS_Bench", "false")}} ] }""");
        using var page = new CraftablesPageViewModel(ctx.Services);
        var chair = page.Items[0];
        Assert.False(chair.IsLocked);
        Assert.True(chair.CanRename);

        chair.AssetNameText = "HexTer_Chair_2";
        Assert.Equal("SS_Chair", chair.Token); // the engine would read 'HexTer_Chair' number 3
        Assert.Contains("HexTer_Chair_v2", chair.NameProblem, StringComparison.Ordinal);
        chair.AssetNameText = "ss_bench";
        Assert.Contains("Bench", chair.NameProblem, StringComparison.Ordinal);
        Assert.Equal("SS_Chair", chair.Token);

        chair.AssetNameText = "HexTer_Chair";
        chair.FolderText = "Furniture/Seats";
        Assert.Null(chair.NameProblem);
        Assert.Equal("Recipe file: Furniture/Seats/CR_HexTer_Chair", chair.PathPreview);
        chair.FolderText = "Seats 2";
        Assert.NotNull(chair.FolderProblem);
        Assert.Equal("Furniture/Seats", chair.Folder);

        var saved = CraftablesFile.Load(project.DirectoryPath).Items[0];
        Assert.Equal(("HexTer_Chair", "Furniture/Seats", false), (saved.Token, saved.Folder, saved.Exported));
    }

    [Fact]
    public async Task AnExportedOrOldCraftableKeepsItsName()
    {
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
        var path = Path.Combine(project.DirectoryPath, CraftablesFile.FileName);
        File.WriteAllText(path, $$"""{ "Items": [ {{Row("Old", "SS_Old", null)}}, {{Row("Shipped", "SS_Shipped", "true")}}, {{Row("New", "SS_New", "false")}} ] }""");
        using var page = new CraftablesPageViewModel(ctx.Services);
        Assert.Equal([true, true, false], page.Items.Select(i => i.IsLocked));

        page.Items[0].AssetNameText = "Renamed";
        page.Items[1].FolderText = "Furniture";
        page.Items[0].Name = "Old Chair"; // the name shown in game still changes
        Assert.Equal(("SS_Old", null), (page.Items[0].Token, page.Items[1].Folder));
        var old = CraftablesFile.Load(project.DirectoryPath).Items[0];
        Assert.Equal(("Old Chair", "SS_Old", (bool?)null), (old.Name, old.Token, old.Exported));

        // An export marks the third on disk meanwhile: the page's next save keeps the mark and locks the row; Undo cannot step
        // back over the export.
        CraftablesFile.MarkExported(project.DirectoryPath, ["SS_New"]);
        page.Items[2].Name = "New Chair";
        Assert.True(page.Items[2].IsLocked);
        Assert.True(CraftablesFile.Load(project.DirectoryPath).Items[2].Exported);
        Assert.False(page.UndoCommand.CanExecute(null));
        page.Items[2].AssetNameText = "Other";
        Assert.Equal("SS_New", page.Items[2].Token);
    }

    [Fact]
    public async Task UndoNeverStepsBackOverAnExport()
    {
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
        File.WriteAllText(Path.Combine(project.DirectoryPath, CraftablesFile.FileName), $$"""{ "Items": [ {{Row("Chair", "SS_Chair", "false")}} ] }""");
        using var page = new CraftablesPageViewModel(ctx.Services);
        page.Items[0].AssetNameText = "HexTer_Chair";

        // Exported from another page meanwhile: Undo would bring back SS_Chair, a name the pak does not have.
        CraftablesFile.MarkExported(project.DirectoryPath, ["HexTer_Chair"]);
        page.UndoCommand.Execute(null);
        var saved = Assert.Single(CraftablesFile.Load(project.DirectoryPath).Items);
        Assert.Equal(("HexTer_Chair", true), (saved.Token, saved.Exported));
        Assert.True(page.Items[0].IsLocked);
    }

    [Fact]
    public void TheStorageCardKeepsTheFridgeAndItsSize()
    {
        // defolt_0999's large fridge: the cabinet element spawning the game's fridge, with the rows, columns and weight it holds.
        var family = new ElementFamily("/Game/E/BP_Cabinet", "/Game/R/CR_Cabinet", "BaseBuildingItemComponent", "/Game/I/Cabinet", [new ElementLevel("/Game/R/CR_Cabinet", "/Game/E/BP_Cabinet", [])], false);
        Tunable[] storage =
        [
            new("EntityGridInventoryComponentSetup_0", "NumRows", TunableKind.UInt, "30"),
            new("EntityGridInventoryComponentSetup_0", "NumColumns", TunableKind.UInt, "15"),
            new("EntityGridInventoryComponentSetup_0", "MaxContainedWeight", TunableKind.Float, "500.00003"),
        ];
        var saved = new ElementSettings { Template = family.Element, Storage = new StorageSettings { Item = ItemStorage.Fridge, Values = new Dictionary<string, string> { ["NumRows"] = "60" } } };
        var editor = new ElementEditorViewModel(family, saved, () => { }, storage, isFridge: true);

        Assert.True(editor.HasStorage && editor.IsFridge && editor.SpawnsOther);
        Assert.Equal(["Rows", "Columns", "Max weight"], editor.Storage.Select(s => s.Label));
        Assert.Equal(60m, editor.Storage[0].Number);
        editor.Storage[2].Number = 2000;
        var model = editor.ToModel();
        Assert.Equal(ItemStorage.Fridge, model.Storage!.Item);
        Assert.Equal(new Dictionary<string, string> { ["NumRows"] = "60", ["MaxContainedWeight"] = "2000" }, model.Storage.Values);

        // A chest keeps its own item: without a change it stores nothing.
        Assert.Null(new ElementEditorViewModel(family, null, () => { }, storage).ToModel().Storage);
    }
}
