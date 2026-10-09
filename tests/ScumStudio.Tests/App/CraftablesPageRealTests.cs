using Avalonia.Controls;
using Avalonia.VisualTree;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Craftables page on the real game files (<c>SCUM_PAKS</c>): a table mesh added from the page's search, a chair
/// Blueprint from the Assets page's "Make craftable", a station the chair needs, 3D pictures, Undo, and Export mod
/// building the Craftables pak. Pictures to <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
public sealed class CraftablesPageRealTests
{
    private const string Table = "/Game/ConZ_Files/Models/Objects/Indoor/Armory/Table/SM_Table_01";
    private const string Chair = "/Game/ConZ_Files/BaseBuilding/BaseElements/AsianDecorPack/BP_Chair_Asian";

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task CraftablesAreAddedEditedAndExported()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks, ClientModsOutputFolder = ctx.Combine("exports") });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Crafty");
            Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Projects.Current is not null, TimeSpan.FromSeconds(30)));
            var project = ctx.Services.Projects.Current!;
            var page = Assert.IsType<CraftablesPageViewModel>(vm.NavigateTo("craftables"));
            HeadlessUi.Pump();

            // The page's search finds the table mesh; Add makes it a craftable with a suggested wooden recipe.
            page.AddQuery = "SM_Table_01";
            Assert.True(HeadlessUi.PumpUntil(() => page.AddResults.Any(r => r.PackagePath == Table), TimeSpan.FromMinutes(3)), "the search found no table");
            var hit = page.AddResults.First(r => r.PackagePath == Table);
            await page.AddSourceCommand.ExecuteAsync(hit);
            var table = Assert.Single(page.Items);
            Assert.Equal("Table 01", table.Name);
            Assert.NotEmpty(table.Ingredients);

            // The Assets page's "Make craftable" adds the chair Blueprint (its mesh comes from the Blueprint).
            vm.MakeCraftable(Chair);
            Assert.True(HeadlessUi.PumpUntil(() => page.Items.Count == 2, TimeSpan.FromSeconds(60)));
            var chair = page.Items[1];
            Assert.Equal("Chair Asian", chair.Name);

            // The table becomes a station the chair needs; renaming and the station are saved with the project.
            table.Name = "Carpenter Bench";
            table.Kind = CraftKind.Station;
            HeadlessUi.Pump();
            Assert.Contains("Carpenter Bench", page.Stations);
            page.SelectedItem = chair;
            chair.Station = "Carpenter Bench";
            chair.Ingredients[0].Amount = 77;
            HeadlessUi.Pump();
            var saved = CraftablesFile.Load(project.DirectoryPath);
            Assert.True(saved.Items.Any(c => c.Kind == CraftKind.Station && c.Name == "Carpenter Bench"), saved.ToJson());
            Assert.Equal(77, saved.Items[1].Ingredients[0].Amount);
            Assert.Equal("Carpenter Bench", saved.Items[1].Station);

            // Undo steps back one change at a time.
            page.UndoCommand.Execute(null);
            Assert.NotEqual(77, CraftablesFile.Load(project.DirectoryPath).Items[1].Ingredients[0].Amount);
            chair = page.Items[1];
            chair.Ingredients[0].Amount = 77;
            page.SelectedItem = chair;

            // 3D pictures of both meshes, then the page's picture.
            Assert.True(HeadlessUi.PumpUntil(() => page.Items.All(i => i.Thumbnail is not null), TimeSpan.FromSeconds(90)), "no 3D pictures");
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "CraftList");
            Assert.Equal(2, list.ItemCount);
            HeadlessUi.SaveScreenshot(window, "craftables-page");

            // Export mod: the project has no map edits, so only the Craftables pak is built (with its sig).
            var results = await ModExportService.ExportAsync(ctx.Services, new ModExportRequest(project, ctx.Combine("out"), null, false), ProgressSink.Null);
            var crafted = Assert.Single(results);
            Assert.Equal("CraftyCraftables", crafted.ModName);
            Assert.True(File.Exists(crafted.PakPath) && File.Exists(crafted.SigPath));
            Assert.Contains(crafted.Assets, a => a.PackagePath.EndsWith("/CR_SS_Chair_Asian", StringComparison.Ordinal));
            Assert.Contains(crafted.Assets, a => a.PackagePath.EndsWith("/CI_SS_Carpenter_Bench", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }
}
