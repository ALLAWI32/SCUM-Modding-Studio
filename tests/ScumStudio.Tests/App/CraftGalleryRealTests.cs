using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Craftables page's "Add from the game" gallery on the real game files (<c>SCUM_PAKS</c>; owner: "the list is empty,
/// why?"): the game's objects and inventory items in their categories with 3D pictures, and a click that adds one through
/// the page's Add. Picture to <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
public sealed class CraftGalleryRealTests
{
    private const string Chair = "/Game/ConZ_Files/BaseBuilding/BaseElements/BP_Chair_Improvised_Wood";
    private const string Rifle = "/Game/ConZ_Files/Items/Weapons/Ranged_Weapons/Weapon_AK47";

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheGalleryListsObjectsAndItemsAndAddsOne()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
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
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Gallery");
            Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Projects.Current is not null, TimeSpan.FromSeconds(30)));
            var page = Assert.IsType<CraftablesPageViewModel>(vm.NavigateTo("craftables"));
            HeadlessUi.Pump();

            // Nothing is craftable yet, so the gallery shows: world objects first, then the items.
            Assert.Empty(page.Items);
            Assert.True(page.IsGalleryOpen);
            var gallery = page.Gallery;
            Assert.True(HeadlessUi.PumpUntil(() => gallery.LoadCompletion.IsCompleted && gallery.Roots.Count >= 2, TimeSpan.FromMinutes(2)), "the gallery listed nothing");
            Assert.True(gallery.Tiles.Count > 100, gallery.ResultText);
            gallery.SelectedCategory = gallery.Roots[0].Children.First(c => c.Ids.Contains("basebuilding"));
            Assert.Contains(gallery.Tiles, t => t.Entry.PackagePath == Chair);
            gallery.SelectedCategory = gallery.Roots[1].Children.First(c => c.Ids.Contains("weapons"));
            Assert.Contains(gallery.Tiles, t => t.Entry.PackagePath == Rifle && t.ClassName == "Blueprint");
            Assert.True(HeadlessUi.PumpUntil(() => gallery.Tiles.Take(4).Any(t => t.Thumbnail is not null), TimeSpan.FromSeconds(90)), "no 3D picture of a weapon");
            HeadlessUi.SaveScreenshot(window, "craftables-gallery-weapons");

            // The search finds the chair; its tile on screen gets its 3D picture.
            gallery.Query = "Chair_Improvised_Wood";
            Assert.True(HeadlessUi.PumpUntil(() => gallery.Tiles.Any(t => t.Entry.PackagePath == Chair) && gallery.Tiles.Count < 20, TimeSpan.FromSeconds(30)), gallery.ResultText);
            var chair = gallery.Tiles.First(t => t.Entry.PackagePath == Chair);
            Assert.False(chair.IsAdded);
            Assert.True(HeadlessUi.PumpUntil(() => chair.Thumbnail is not null, TimeSpan.FromSeconds(90)), "no 3D picture");
            HeadlessUi.SaveScreenshot(window, "craftables-gallery");

            // A click adds it through the page's Add: the craftable with its suggested recipe opens, the tile is marked.
            await gallery.AddCommand.ExecuteAsync(chair);
            var added = Assert.Single(page.Items);
            Assert.Equal(Chair, added.Source);
            Assert.NotEmpty(added.Ingredients);
            Assert.Same(added, page.SelectedItem);
            Assert.False(page.IsGalleryOpen);
            Assert.True(chair.IsAdded);

            // Clicking it again opens the craftable instead of adding a second one.
            page.IsGalleryOpen = true;
            await gallery.AddCommand.ExecuteAsync(chair);
            Assert.Single(page.Items);
            Assert.False(page.IsGalleryOpen);
        }
        finally
        {
            window.Close();
        }
    }
}
