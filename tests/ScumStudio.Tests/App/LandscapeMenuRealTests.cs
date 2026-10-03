using Avalonia.VisualTree;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Landscape menu and the shape panel of a house as the owner sees them (screenshots with <c>SCUMSTUDIO_SCREENSHOTS</c>):
/// picking a look marks it and journals one step. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class LandscapeMenuRealTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheLandscapeMenuSetsALookAndAHouseShowsNoBend()
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
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Looks");
            await map.LoadLevelsAsync(["/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01"]);
            HeadlessUi.Pump();

            var button = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "LandscapeButton");
            button.Flyout!.ShowAt(button);
            HeadlessUi.Pump();
            Assert.True(map.GroundLookOptions.Single(o => o.Look == GroundLook.Game).IsCurrent);
            map.GroundLookOptions.Single(o => o.Look == GroundLook.Snow).Choose.Execute(null);
            HeadlessUi.Pump();
            Assert.True(map.GroundLookOptions.Single(o => o.Look == GroundLook.Snow).IsCurrent);
            Assert.Single(ctx.Services.Projects.History);
            map.TreeToSwap = map.Trees.First(t => t.Name.Contains("Oak", StringComparison.OrdinalIgnoreCase));
            map.TreeSwapWith = map.Trees.First(t => t.Name.Contains("Pine", StringComparison.OrdinalIgnoreCase));
            map.SwapTreeCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.Single(map.TreeSwaps);
            HeadlessUi.SaveScreenshot(window, "map-landscape-menu");
            button.Flyout.Hide();

            // A house: no bend row, "Fit to ground" next to "Straighten".
            map.SelectedActor = map.AllActors.First(a => a.Actor.Kind == ActorKind.Blueprint && a.Name.Contains("House", StringComparison.OrdinalIgnoreCase));
            HeadlessUi.Pump();
            Assert.False(map.CanBend);
            var shape = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "ShapeButton");
            shape.Flyout!.ShowAt(shape);
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "map-house-shape");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }
}
