using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: the Map's panels aligned and balanced. The World tree, the 3D view and Properties start on one line; the entity
/// list and History share one row, start and end together and end with the World tree; the four panel headers are equally
/// tall. At every common screen size and after F11 (maximize) and back.
/// </summary>
[Collection(LanguageCollection.Name)] // finds the panels by their English titles
public sealed class MapBodyLayoutTests
{
    [Avalonia.Headless.XUnit.AvaloniaTheory]
    [InlineData(1280, 720)]
    [InlineData(1600, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public async Task ThePanelsLineUp(int width, int height)
    {
        using var ctx = AppTestContext.Create(inline: false);
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        await ctx.Services.Workspace.OpenLooseAsync(game, ScumStudio.Core.Abstractions.ProgressSink.Null);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, width, height);
        try
        {
            var map = (MapPageViewModel)vm.NavigateTo("map")!;
            await map.LoadCompletion;
            await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
            HeadlessUi.Pump();
            AssertLinedUp(window, $"{width}x{height}");

            window.KeyPress(Key.F11, RawInputModifiers.None, PhysicalKey.F11, null);
            HeadlessUi.Pump();
            var view = HeadlessUi.FindNamed<Border>(window, "Viewport")!;
            Assert.False(HeadlessUi.FindNamed<Border>(window, "LeftPanel")!.IsVisible, "F11 hides the World tree");
            // The left rail takes 200 px (54 when the window is narrow): the view fills the rest.
            var rail = width >= MainWindowViewModel.CompactHeaderWidth ? 200 : 54;
            Assert.True(view.Bounds.Width > width - rail - 100, $"maximized, the view is {view.Bounds.Width} wide");

            window.KeyPress(Key.F11, RawInputModifiers.None, PhysicalKey.F11, null);
            HeadlessUi.Pump();
            AssertLinedUp(window, $"{width}x{height} after F11 twice");
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    private static void AssertLinedUp(Window window, string at)
    {
        var world = Panel(window, "Map.World");
        var view = Bounds(HeadlessUi.FindNamed<Border>(window, "Viewport")!, window);
        var properties = Panel(window, "Map.Properties");
        var entities = Panel(window, "Map.Entities");
        var history = Panel(window, "Map.History");
        Assert.True(Near(world.Rect.Top, view.Top) && Near(view.Top, properties.Rect.Top),
            $"{at}: tops World {world.Rect.Top}, view {view.Top}, Properties {properties.Rect.Top}");
        Assert.True(Near(entities.Rect.Top, history.Rect.Top), $"{at}: Entities starts at {entities.Rect.Top}, History at {history.Rect.Top}");
        Assert.True(Near(entities.Rect.Bottom, history.Rect.Bottom) && Near(history.Rect.Bottom, world.Rect.Bottom),
            $"{at}: bottoms Entities {entities.Rect.Bottom}, History {history.Rect.Bottom}, World {world.Rect.Bottom}");
        var headers = new[] { world, properties, entities, history }.Select(p => p.Header).ToList();
        Assert.True(headers.All(h => Near(h, headers[0])), $"{at}: panel headers {string.Join(", ", headers)} tall");
    }

    /// <summary>The panel titled <paramref name="titleKey"/>: its bounds in the window and its header's height.</summary>
    private static (Rect Rect, double Header) Panel(Window window, string titleKey)
    {
        var title = HeadlessUi.Find<TextBlock>(window).First(t => t.Text == Loc.T(titleKey) && t.Classes.Contains("h3"));
        var header = title.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("panelHeader"));
        var panel = header.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("panel"));
        Assert.True(panel.IsEffectivelyVisible, titleKey);
        return (Bounds(panel, window), header.Bounds.Height);
    }

    private static Rect Bounds(Visual visual, Visual root) => new(visual.TranslatePoint(new Point(0, 0), root)!.Value, visual.Bounds.Size);

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.5;
}
