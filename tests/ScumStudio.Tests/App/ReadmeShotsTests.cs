using Avalonia.Headless.XUnit;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// The README's pictures: every page of the app on the real game files, rendered offscreen. Runs only when
/// <c>SCUMSTUDIO_README_SHOTS</c> (output folder) and <c>SCUM_PAKS</c> (the game's Paks folder) are set; the key is looked
/// up online like the setup does, so nothing of this PC's own settings is used.
/// </summary>
public sealed class ReadmeShotsTests
{
    private static string? Output => Environment.GetEnvironmentVariable("SCUMSTUDIO_README_SHOTS");

    private static string? Paks => Environment.GetEnvironmentVariable("SCUM_PAKS");

    [AvaloniaFact]
    public async Task TakeTheReadmePictures()
    {
        if (Output is not { Length: > 0 } output || Paks is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        Environment.SetEnvironmentVariable(HeadlessUi.ScreenshotsVariable, output);
        using var ctx = AppTestContext.Create(inline: false, keyFinder: OnlineKeyFinder.FindAsync);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 900);
        try
        {
            // First run: the setup looks the key up online, tests it on the game files and stores it.
            vm.OpenSetup();
            Assert.True(HeadlessUi.PumpUntil(() => ctx.Services.Keys.HasKey && !vm.Setup!.IsFindingKey, TimeSpan.FromMinutes(2)));
            HeadlessUi.SaveScreenshot(window, "setup");
            vm.Setup!.SaveAndCloseCommand.Execute(null);
            await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
            HeadlessUi.Pump();

            // Weapons and vehicles: the stored values of a stock rifle and car.
            var weapons = (WeaponsPageViewModel)vm.NavigateTo("weapons")!;
            await weapons.LoadCompletion;
            Assert.True(await weapons.SelectAsync("Weapon_AK47"));
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "weapons");

            var vehicles = (VehiclesPageViewModel)vm.NavigateTo("vehicles")!;
            await vehicles.LoadCompletion;
            Assert.True(await vehicles.SelectAsync("BPC_WolfsWagen"));
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "vehicles");

            // Assets: the objects by category with their pictures, then every file with a texture open.
            var assets = (AssetsPageViewModel)vm.NavigateTo("assets")!;
            Assert.True(HeadlessUi.PumpUntil(() => assets.LoadCompletion.IsCompleted, TimeSpan.FromMinutes(5)));
            await Search(assets, "Tudman");
            assets.SelectedItem = assets.Items.FirstOrDefault(i => i.Entry.ClassName == "StaticMesh") ?? assets.Items[0];
            await assets.DetailsCompletion;
            HeadlessUi.PumpUntil(() => assets.Items.Count(i => i.Thumbnail is not null) >= Math.Min(12, assets.Items.Count), TimeSpan.FromMinutes(2));
            HeadlessUi.SaveScreenshot(window, "objects");

            assets.IsObjectsView = false;
            await Search(assets, "WolfsWagen T_");
            assets.SelectedItem = assets.Items.FirstOrDefault(i => i.Entry.Name.StartsWith("T_", StringComparison.Ordinal) && i.Entry.Name.EndsWith("_D", StringComparison.Ordinal))
                ?? assets.Items[0];
            await assets.DetailsCompletion;
            HeadlessUi.PumpUntil(() => assets.Items.Count(i => i.Thumbnail is not null) >= Math.Min(12, assets.Items.Count), TimeSpan.FromMinutes(2));
            HeadlessUi.SaveScreenshot(window, "assets");

            // Map: a sublevel of the outpost with its entities and properties.
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            HeadlessUi.Pump();
            var cell = map.Nodes.First(n => n.Title == "Cell A_0");
            cell.IsExpanded = true;
            map.SelectedNode = cell.Children.SelectMany(c => c.Children).First(n => n.Package?.Name == "A_0_Outpost_Exterior");
            await map.LevelLoadCompletion;
            HeadlessUi.Pump();
            map.SelectedActorId = map.Actors.First(a => a.HasMesh).SelectableId;
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "map");

            vm.NavigateTo("settings");
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "settings");

            vm.NavigateTo("projects");
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "projects");
        }
        finally
        {
            window.Close();
            vm.Dispose();
            Environment.SetEnvironmentVariable(HeadlessUi.ScreenshotsVariable, null);
        }
    }

    // The box shows the text; its own delayed search runs out first, then the list is final.
    private static async Task Search(AssetsPageViewModel assets, string text)
    {
        assets.SearchText = text;
        var until = DateTime.UtcNow.AddSeconds(2);
        HeadlessUi.PumpUntil(() => DateTime.UtcNow > until, TimeSpan.FromSeconds(5));
        await assets.SearchAsync(text);
        HeadlessUi.Pump();
        Assert.NotEmpty(assets.Items);
    }
}
