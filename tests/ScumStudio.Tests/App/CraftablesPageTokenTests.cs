using ScumStudio.App.ViewModels;
using ScumStudio.Modding.Crafting;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Craftables page and asset names (audit F037, N039; P0 #1): a file from before 3.2 gets its tokens stored when the
/// page opens it, a rename keeps the token, and a row the export would leave out for a taken token says so inline.
/// </summary>
public sealed class CraftablesPageTokenTests
{
    private const string Table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";

    [Fact]
    public async Task TheTokenIsStoredWhenThePageOpensTheFileAndARenameKeepsIt()
    {
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
        var path = Path.Combine(project.DirectoryPath, CraftablesFile.FileName);
        File.WriteAllText(path, $$"""{ "Items": [ { "Name": "Chair", "Source": "{{Table}}", "Mesh": "{{Table}}" } ] }""");

        using var page = new CraftablesPageViewModel(ctx.Services);
        var row = Assert.Single(page.Items);
        Assert.Equal("SS_Chair", row.Token);
        Assert.Equal("Asset name: SS_Chair", row.AssetName);
        Assert.Null(row.TokenTaken);
        Assert.Equal("SS_Chair", Assert.Single(CraftablesFile.Parse(File.ReadAllText(path)).Items).Token);
        Assert.Contains("\"Token\"", File.ReadAllText(path), StringComparison.Ordinal); // stored on disk

        row.Name = "Wooden Chair";
        var saved = Assert.Single(CraftablesFile.Load(project.DirectoryPath).Items);
        Assert.Equal(("Wooden Chair", "SS_Chair"), (saved.Name, saved.Token));
        Assert.Equal("Asset name: SS_Chair", row.AssetName);
    }

    [Fact]
    public async Task ARowLeftOutForATakenTokenSaysSoAndARenameGivesItItsOwn()
    {
        using var ctx = AppTestContext.Create();
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
        File.WriteAllText(Path.Combine(project.DirectoryPath, CraftablesFile.FileName), $$"""
            { "Items": [
              { "Name": "Chair", "Token": "SS_Chair", "Source": "{{Table}}", "Mesh": "{{Table}}" },
              { "Name": "Seat", "Token": "ss_chair", "Source": "{{Table}}", "Mesh": "{{Table}}" }
            ] }
            """);

        using var page = new CraftablesPageViewModel(ctx.Services);
        Assert.Null(page.Items[0].TokenTaken);
        Assert.Equal("Chair already uses the asset name ss_chair: this craftable would be left out of the export. Rename it to give it an asset name of its own.", page.Items[1].TokenTaken);

        page.Items[1].Name = "Seat Two";
        Assert.Equal("SS_Seat_Two", page.Items[1].Token);
        Assert.Null(page.Items[1].TokenTaken);
        Assert.Equal("SS_Chair", page.Items[0].Token); // the craftable that shipped keeps its token
        Assert.Equal(["SS_Chair", "SS_Seat_Two"], CraftablesFile.Load(project.DirectoryPath).Items.Select(i => i.Token));
    }
}
