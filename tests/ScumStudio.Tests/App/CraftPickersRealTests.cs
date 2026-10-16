using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;
using ScumStudio.Tests.Rendering;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Craftables page's 3D pickers on the real game files (<c>SCUM_PAKS</c>): the ingredient picker lists the lead plate in
/// its catalogue category with its 3D picture and its own tag, and the improvised bed becomes an element with the game's
/// levels and its respawn point. Pictures to <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
[Collection(GlCollection.Name)] // its 3D pictures render with OpenGL: never beside another GL test
public sealed class CraftPickersRealTests
{
    private const string LeadPlate = "/Game/ConZ_Files/Items/Equipment/Active_Items/Lead_Plate";
    private const string Bed = "/Game/ConZ_Files/BaseBuilding/BaseElements/BP_Bed_Improvised";

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ThePickerAndTheElementEditorReadTheGame()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks) || GlTestEnvironment.SkipReason is not null)
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create(inline: false);
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Pickers");
            Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Projects.Current is not null, TimeSpan.FromSeconds(30)));
            var page = Assert.IsType<CraftablesPageViewModel>(vm.NavigateTo("craftables"));
            HeadlessUi.Pump();

            // The bed of the game: an element cloned with its own family, not the improvised chair.
            var catalog = ctx.Services.Workspace.Catalog!;
            var described = CraftablesPageViewModel.Describe(catalog, Bed)!;
            Assert.Equal(CraftKind.Element, described.Kind);
            Assert.Equal(Bed, described.Element!.Template);
            await page.AddAsync(Bed);
            var bed = Assert.Single(page.Items);
            Assert.True(HeadlessUi.PumpUntil(() => bed.Element is not null, TimeSpan.FromMinutes(3)), bed.ElementNote ?? "no element family");
            Assert.True(bed.Element!.IsRespawnPoint);
            Assert.Contains(bed.Element.Levels[0].Values, v => v.Key == "_healthPoints");
            Assert.False(bed.CanBoth); // a bed spawns no item
            HeadlessUi.SaveScreenshot(window, "craftables-bed-element");

            // The ingredient picker: the lead plate in its category, with its 3D picture and its own tag.
            _ = page.PickIngredientCommand.ExecuteAsync(null);
            var picker = page.Picker;
            Assert.True(HeadlessUi.PumpUntil(() => picker.LoadCompletion.IsCompleted && picker.Roots.Count >= 2, TimeSpan.FromMinutes(5)), "the picker listed nothing");
            var electronics = picker.Roots[0].Children.Single(c => c.Ids.Contains("items")).Children.Single(c => c.Ids.Contains("items.electronics"));
            picker.SelectedCategory = electronics;
            var lead = picker.Tiles.Single(t => t.Item == LeadPlate);
            picker.Query = "Lead_Plate";
            Assert.True(HeadlessUi.PumpUntil(() => picker.Tiles.Any(t => t.Item == LeadPlate) && picker.Tiles.Count < 20, TimeSpan.FromSeconds(30)), picker.ResultText);
            Assert.Contains(picker.Tiles, t => t.Group == "CI_Lead_Plate");
            lead = picker.Tiles.Single(t => t.Item == LeadPlate);
            Assert.True(HeadlessUi.PumpUntil(() => lead.Thumbnail is not null, TimeSpan.FromSeconds(90)), "no 3D picture of the lead plate");
            picker.SelectCommand.Execute(lead);
            Assert.True(HeadlessUi.PumpUntil(() => picker.ResolveCompletion.IsCompleted && picker.Lookup is not null, TimeSpan.FromMinutes(3)), "the lead plate's tags were not read");
            Assert.Equal(ItemTagState.OwnTag, picker.Lookup!.State);
            Assert.Equal("CI_Lead_Plate", picker.Lookup.Tag);
            HeadlessUi.SaveScreenshot(window, "craftables-ingredient-picker");
            picker.OkCommand.Execute(null);
            Assert.True(HeadlessUi.PumpUntil(() => bed.Ingredients.Any(i => i.Item == LeadPlate), TimeSpan.FromSeconds(10)));
            Assert.Contains(page.File.Items.Single().Ingredients, i => i.Item == LeadPlate && i.Tag == "CI_Lead_Plate" && !i.AddTag);
        }
        finally
        {
            window.Close();
        }
    }
}
