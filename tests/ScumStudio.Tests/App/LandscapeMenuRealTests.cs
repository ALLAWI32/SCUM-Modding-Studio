using Avalonia.VisualTree;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Pak;

namespace ScumStudio.Tests.App;

/// <summary>
/// The Landscape menu and the shape panel of a house as the owner sees them (screenshots with <c>SCUMSTUDIO_SCREENSHOTS</c>):
/// picking a look marks it and journals one step; the tree swap is two picking cards over the game's tree types (pictures,
/// search) and Swap journals one step. Real game files only (<c>SCUM_PAKS</c>).
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

            // Trees: two picking cards over the game's tree types; the left one drops its list down, a search finds the oaks.
            Assert.Same(map.Trees, map.TreeToSwap.All);
            Assert.Same(map.Trees, map.TreeSwapWith.All);
            Assert.False(map.SwapTreeCommand.CanExecute(null));
            var panel = (Avalonia.Controls.StackPanel)((Avalonia.Controls.Flyout)button.Flyout).Content!;
            var cards = HeadlessUi.Find<Avalonia.Controls.Primitives.ToggleButton>(panel).Where(t => t.Name == "PickerCard").ToList();
            Assert.Equal(2, cards.Count);
            var scroll = panel.FindAncestorOfType<Avalonia.Controls.ScrollViewer>()!;
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, $"the cards are clipped: {scroll.Extent.Width} > {scroll.Viewport.Width}");
            cards[0].IsChecked = true;
            HeadlessUi.Pump();
            Assert.True(map.TreeToSwap.IsOpen);
            Assert.False(map.TreeSwapWith.IsOpen);
            var list = HeadlessUi.Find<Avalonia.Controls.Primitives.Popup>(panel).First(p => p.IsOpen).Child!.GetVisualDescendants().OfType<Avalonia.Controls.ItemsControl>().Single(l => l.Name == "PickerList");
            Assert.Equal(map.Trees.Count, list.ItemCount);
            map.TreeToSwap.Search = "Oak";
            HeadlessUi.Pump();
            Assert.NotEmpty(map.TreeToSwap.Items);
            Assert.All(map.TreeToSwap.Items, t => Assert.Contains("Oak", t.Name, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(map.TreeToSwap.Items.Count, list.ItemCount);
            var oak = map.TreeToSwap.Items[0];
            map.TreeToSwap.PickCommand.Execute(oak);
            HeadlessUi.Pump();
            Assert.Same(oak, map.TreeToSwap.Selected);
            Assert.False(map.TreeToSwap.IsOpen);
            Assert.False(map.SwapTreeCommand.CanExecute(null)); // one card still empty
            var pine = map.Trees.First(t => t.Name.Contains("Pine", StringComparison.OrdinalIgnoreCase));
            map.TreeSwapWith.PickCommand.Execute(pine);
            HeadlessUi.Pump();
            Assert.True(map.SwapTreeCommand.CanExecute(null));
            map.SwapTreeCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.Equal($"{oak.Name}  →  {pine.Name}", Assert.Single(map.TreeSwaps).Text);
            Assert.Equal(2, ctx.Services.Projects.History.Count);
            await map.LoadCompletion; // the swap previews the levels that use the tree; back to the farm
            await map.LoadLevelsAsync(["/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01"]);
            HeadlessUi.Pump();
            HeadlessUi.PumpUntil(() => oak.Thumbnail is not null && pine.Thumbnail is not null, TimeSpan.FromSeconds(90));
            Assert.Contains(cards[0].GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(), t => t.Text == oak.Name);
            Assert.Contains(cards[1].GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(), t => t.Text == pine.Name);
            HeadlessUi.SaveScreenshot(window, "map-landscape-menu");
            button.Flyout.Hide();

            // A house: no bend row, "Fit to ground" next to "Straighten".
            var houseItem = map.AllActors.FirstOrDefault(a => a.Actor.Kind == ActorKind.Blueprint && a.Name.Contains("House", StringComparison.OrdinalIgnoreCase));
            Assert.True(houseItem is not null, $"{map.AllActors.Count} actors: {string.Join(", ", map.AllActors.Take(14).Select(a => a.Name + "/" + a.Actor.Kind + "/" + a.Level.Name))}");
            map.SelectedActor = houseItem;
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
