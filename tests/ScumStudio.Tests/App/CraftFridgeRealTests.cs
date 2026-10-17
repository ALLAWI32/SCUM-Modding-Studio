using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak;
using ScumStudio.Tests.Rendering;

namespace ScumStudio.Tests.App;

/// <summary>
/// defolt_0999's large fridge on the Craftables page with the real game files (<c>SCUM_PAKS</c>): the game's fridge picked
/// from the gallery becomes the cabinet element spawning the fridge, its Storage card shows the fridge's 30 x 15 and 500
/// with Reset, the fridge note, and no Both; a new size is saved. Picture to <c>SCUMSTUDIO_SCREENSHOTS</c>.
/// </summary>
[Collection(GlCollection.Name)] // its 3D pictures render with OpenGL: never beside another GL test
public sealed class CraftFridgeRealTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheGamesFridgeBecomesALargeFridgeToBuild()
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
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Fridges");
            Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Projects.Current is not null, TimeSpan.FromSeconds(30)));
            var page = Assert.IsType<CraftablesPageViewModel>(vm.NavigateTo("craftables"));
            HeadlessUi.Pump();

            await page.AddAsync(ItemStorage.Fridge);
            var fridge = Assert.Single(page.Items);
            Assert.Equal(CraftKind.Element, fridge.Kind);
            Assert.False(fridge.IsLocked); // new: its asset name and folder can still change
            Assert.Equal("Storage", fridge.Folder);
            Assert.True(HeadlessUi.PumpUntil(() => fridge.Element is not null, TimeSpan.FromMinutes(3)), fridge.ElementNote ?? "no element family");
            var element = fridge.Element!;
            Assert.True(element.HasStorage && element.IsFridge && element.SpawnsOther);
            Assert.False(fridge.CanBoth); // it fits in no bag
            Assert.True(fridge.IsBuilding);
            Assert.DoesNotContain("Chair", fridge.Sections[0].Label, StringComparison.Ordinal); // its default section is the cabinet recipe's
            Assert.Equal(["30", "15"], element.Storage.Take(2).Select(s => s.Stock));

            element.Storage[0].Number = 60;
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "craftables-large-fridge");
            var saved = Assert.Single(CraftablesFile.Load(ctx.Services.Projects.Current!.DirectoryPath).Items);
            Assert.Equal(ItemStorage.Fridge, saved.Element!.Storage!.Item);
            Assert.Equal("60", saved.Element.Storage.Values["NumRows"]);
            Assert.Equal(ItemStorage.Carrier, saved.Element.Template);
        }
        finally
        {
            window.Close();
        }
    }
}
