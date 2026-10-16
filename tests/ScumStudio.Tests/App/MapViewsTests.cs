using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.App.Views;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.World;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>seviscache #5: Esc during a Shape, scale or legs handle drag puts the shape back and journals nothing.</summary>
public sealed class MapCancelShapeTests
{
    [Fact]
    public async Task CancellingAHandleDragJournalsNothingAndPutsTheShapeBack()
    {
        using var ctx = AppTestContext.Create();
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        using var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Cancel");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
        var house = map.AllActors.Single(a => a.Name == "StaticMeshActor_1");
        map.SelectedActor = house;
        var root = map.SelectedRootWorld!.Value;
        Assert.Equal(2, map.ShapeLength, 3); // the house is at scale 2

        // The view raises a scale handle's drag (the page marks the drag first), then Esc.
        map.IsShapeDragging = true;
        map.DragScale(root with { Scale3D = root.Scale3D * 1.5f });
        Assert.Equal(3, map.ShapeLength, 3);
        Assert.True(map.ActorTransforms.ContainsKey(house.SelectableId), "previewed bigger");
        map.CancelShape();

        Assert.Equal(2, map.ShapeLength, 3);
        Assert.False(map.IsShapeDragging);
        Assert.False(map.ActorTransforms.ContainsKey(house.SelectableId), "drawn as the level has it again");
        await Task.Delay(800); // the half-second journaling the drag had queued
        Assert.Empty(ctx.Services.Projects.History);

        // Let go instead of Esc: one journaled step, as before.
        map.IsShapeDragging = true;
        map.DragScale(root with { Scale3D = root.Scale3D * 1.5f });
        map.IsShapeDragging = false;
        map.CommitShape();
        Assert.Single(ctx.Services.Projects.History);
    }
}

/// <summary>
/// seviscache #4: Ctrl+click and Shift+click in the World tree choose several levels, which load together (exactly those,
/// nothing streaming in); a plain click still loads one. Isolate hides the island's terrain and stops streaming. Two
/// synthetic 1 km cells side by side (A_0 with a POI level and a landscape tile, B_0 with a tile), as in <see cref="MapLoadModeTests"/>.
/// </summary>
[Collection(LanguageCollection.Name)] // compares localized lines
public sealed class MapMultiSelectTests
{
    private const float CellCm = 100_000f;
    private static readonly MapCell A = new('A', 0);
    private static readonly MapCell B = new('B', 0);

    [Fact]
    public async Task TheLevelsOfASelectionAreItsContentLevelsOnceWithTheTilesLast()
    {
        using var ctx = AppTestContext.Create();
        using var map = await OpenTwoCellsAsync(ctx);
        var world = map.World!;
        var nodes = Flatten(map.Nodes).ToList();
        var tileB = nodes.Single(n => n.Package?.Name == "Landscape_B_0_1");
        var poi = nodes.Single(n => n.Package?.PackagePath == SyntheticLevels.LevelPath);
        var cellA = nodes.Single(n => n.Cell == A && n.Package is null);
        var categories = nodes.Where(n => n.Package is null && n.Cell is null).ToList();
        var persistent = nodes.Where(n => n.Package is { Kind: WorldPackageKind.Persistent }).ToList();

        Assert.Equal([poi.Package!.PackagePath, tileB.Package!.PackagePath], MapPageViewModel.LevelsOf(world, [tileB, poi]));
        Assert.Equal(MapPageViewModel.CellPackages(world, A).Append(tileB.Package.PackagePath),
            MapPageViewModel.LevelsOf(world, [cellA, poi, tileB, .. categories, .. persistent])); // the POI once; categories and the persistent level add nothing
        Assert.Empty(MapPageViewModel.LevelsOf(world, [.. categories, .. persistent]));
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task CtrlAndShiftClicksInTheTreeLoadExactlyTheChosenLevels()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm, map) = await ShowTwoCellsAsync(ctx);
        try
        {
            var nodes = Flatten(map.Nodes).ToList();
            var poi = nodes.Single(n => n.Package?.PackagePath == SyntheticLevels.LevelPath);
            var tileA = nodes.Single(n => n.Package?.Name == "Landscape_A_0_1");
            var tileB = nodes.Single(n => n.Package?.Name == "Landscape_B_0_1");
            var button = HeadlessUi.FindNamed<Button>(window, "ShowSelectedButton")!;

            // A plain click: that level alone, at once.
            Click(window, poi);
            await map.LevelLoadCompletion;
            Assert.Equal([poi.Package!.PackagePath], Shown(map));
            Assert.False(button.IsVisible);

            // Ctrl+click adds a level of the other cell: after the pause both load, and nothing else.
            Click(window, tileB, RawInputModifiers.Control);
            Assert.Equal(2, map.SelectedNodes.Count);
            Assert.Equal([poi.Package.PackagePath], Shown(map)); // not yet: a few clicks in a row make one load
            Assert.True(button.IsVisible);
            Assert.Equal(Loc.F("Map.Levels.ShowSelected", 2), button.Content);
            Assert.Equal(Loc.T("Map.Levels.ShowSelected.Tip"), ToolTip.GetTip(button));
            await WaitForSelectionLoadAsync(map);
            Assert.Equal([poi.Package.PackagePath, tileB.Package!.PackagePath], Shown(map).Order());
            Assert.False(map.IsWorldMode); // exactly these: nothing streams in around them

            // Ctrl+click on one of them takes it out again.
            Click(window, poi, RawInputModifiers.Control);
            await WaitForSelectionLoadAsync(map);
            Assert.Equal([tileB.Package.PackagePath], Shown(map));

            // Shift+click: everything from the last click to this one.
            Click(window, poi);
            await map.LevelLoadCompletion;
            Click(window, tileB, RawInputModifiers.Shift);
            await WaitForSelectionLoadAsync(map);
            var chosen = MapPageViewModel.LevelsOf(map.World!, map.SelectedNodes);
            Assert.Contains(poi.Package.PackagePath, chosen);
            Assert.Contains(tileB.Package.PackagePath, chosen);
            Assert.Equal(chosen.Order(), Shown(map).Order());

            // A plain click afterwards loads one level again.
            Click(window, tileA);
            await map.LevelLoadCompletion;
            Assert.Equal([tileA.Package!.PackagePath], Shown(map));
            Assert.Single(map.SelectedNodes);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task IsolateHidesTheIslandAndStopsStreamingUntilWholeIsland()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm, map) = await ShowTwoCellsAsync(ctx);
        try
        {
            var viewport = HeadlessUi.FindNamed<LevelViewport>(window, "Viewport3d")!;
            var isolate = HeadlessUi.FindNamed<ToggleButton>(window, "IsolateButton")!;
            await map.OpenWholeIslandAsync();
            map.UpdateWorldCamera(new FVector(CellCm * 0.5f, CellCm * 0.5f, 0f));
            await map.LevelLoadCompletion;
            Assert.True(viewport.ShowBackdrop);
            HeadlessUi.Pump();
            Assert.True(isolate.IsEffectivelyVisible);
            Assert.Equal(Loc.T("Map.Isolate.Tip"), ToolTip.GetTip(isolate));

            isolate.IsChecked = true;
            HeadlessUi.Pump();
            Assert.True(map.Isolate);
            Assert.False(viewport.ShowBackdrop); // the terrain is hidden (it stays uploaded)
            Assert.Equal(Loc.T("Map.World.Isolated"), map.WorldStatus);
            var shown = Shown(map);
            map.UpdateWorldCamera(new FVector(CellCm * 1.5f, CellCm * 0.5f, 0f)); // deep into B_0
            map.UpdateWorldCamera(new FVector(CellCm * 1.6f, CellCm * 0.5f, 0f));
            Assert.False(map.IsLoadingLevel);
            Assert.Equal(shown, Shown(map));

            // Whole island: the terrain again, and the island streams around the camera.
            await map.OpenWholeIslandAsync();
            HeadlessUi.Pump();
            Assert.False(map.Isolate);
            Assert.False(isolate.IsChecked);
            Assert.True(viewport.ShowBackdrop);
            map.UpdateWorldCamera(new FVector(CellCm * 1.6f, CellCm * 0.5f, 0f));
            await map.LevelLoadCompletion;
            Assert.Contains(Shown(map), p => p.EndsWith("Landscape_B_0_1", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    /// <summary>A click on <paramref name="node"/>'s row in the World tree (its parents are expanded first).</summary>
    private static void Click(Window window, WorldTreeNode node, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        HeadlessUi.Pump();
        var item = HeadlessUi.Find<TreeViewItem>(window).Single(i => ReferenceEquals(i.DataContext, node));
        var header = item.GetVisualDescendants().OfType<Control>().First(c => c.Name is "PART_HeaderPresenter" or "PART_Header");
        var at = header.TranslatePoint(new Point(Math.Min(20, header.Bounds.Width / 2), header.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left, modifiers);
        window.MouseUp(at, MouseButton.Left, modifiers);
        HeadlessUi.Pump();
    }

    /// <summary>Waits out the pause after a Ctrl or Shift click, then for the load it started.</summary>
    private static async Task WaitForSelectionLoadAsync(MapPageViewModel map)
    {
        await Task.Delay(MapPageViewModel.SelectionPause + TimeSpan.FromMilliseconds(400));
        HeadlessUi.Pump();
        await map.LevelLoadCompletion;
    }

    private static async Task<(MainWindow Window, MainWindowViewModel ViewModel, MapPageViewModel Map)> ShowTwoCellsAsync(AppTestContext ctx)
    {
        await WriteTwoCellsAsync(ctx.Combine("game"));
        await ctx.Services.Workspace.OpenLooseAsync(ctx.Combine("game"), ProgressSink.Null);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1600, 1000);
        var map = (MapPageViewModel)vm.NavigateTo("map")!;
        map.RenderQuality = ScumStudio.Core.Settings.RenderQuality.Balanced; // 300 m: one cell at a time
        await map.LoadCompletion;
        foreach (var node in Flatten(map.Nodes))
        {
            node.IsExpanded = true;
        }

        HeadlessUi.Pump();
        Assert.Equal([A, B], map.Cells);
        return (window, vm, map);
    }

    private static async Task<MapPageViewModel> OpenTwoCellsAsync(AppTestContext ctx)
    {
        await WriteTwoCellsAsync(ctx.Combine("game"));
        var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(ctx.Combine("game"), ProgressSink.Null);
        await map.LoadCompletion;
        return map;
    }

    private static async Task WriteTwoCellsAsync(string game)
    {
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true, Tile(A));
        foreach (var cell in new[] { A, B })
        {
            var name = $"Landscape_{cell}_1";
            await SyntheticLevels.BuildLevel(Tile(cell)).WriteAsync(Path.Combine([game, .. (SyntheticLevels.MapsFolder + "/" + name).Split('/')]), ".umap");
        }
    }

    private static WorldTileInfo Tile(MapCell cell)
    {
        var x = (cell.Column - 'A') * CellCm;
        return new WorldTileInfo((0, 0, 0), new FVector(x, 0f, -10_000f), new FVector(x + CellCm, CellCm, 10_000f), true,
            new WorldTileLayer("Landscape", 0, (0, 0), 0, false), false, "None", [], 0);
    }

    private static List<string> Shown(MapPageViewModel map) => map.PreparedScene?.Documents
        .Select(d => d.PackagePath)
        .Where(p => p.Contains("/Maps/The_Island/", StringComparison.Ordinal))
        .ToList() ?? [];

    private static IEnumerable<WorldTreeNode> Flatten(IEnumerable<WorldTreeNode> nodes) => nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
}
