using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScumStudio.App.Localization;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.World;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "load only this sector". The Map asks how to load instead of opening the whole island by itself; one sector
/// loads in one phase and then nothing else streams in until Load more; Cancel stops it without locking. Two synthetic
/// 1 km cells side by side (A_0: x 0 to 1 km, B_0: x 1 to 2 km), each a landscape tile, A_0 also a POI level.
/// </summary>
[Collection(LanguageCollection.Name)] // compares localized status lines: never while another test switches the language
public sealed class MapLoadModeTests
{
    private const float CellCm = 100_000f;
    private static readonly MapCell A = new('A', 0);
    private static readonly MapCell B = new('B', 0);
    private static readonly FVector CentreA = new(CellCm * 0.5f, CellCm * 0.5f, 0f);
    private static readonly FVector CentreB = new(CellCm * 1.5f, CellCm * 0.5f, 0f);

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ASectorLoadStopsStreaming()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var map = await OpenTwoCellsAsync(ctx);
        await map.LoadSectorAsync(A);
        Assert.True(map.IsSectorLocked);
        Assert.Equal(Loc.F("Map.LoadMode.Locked", A), map.WorldStatus);
        var shown = Shown(map);
        Assert.Equal(MapPageViewModel.CellPackages(map.World!, A).Order(), shown.Order());

        // The view's timer: the camera where the sector came up, then flown deep into B, then a small step on.
        map.UpdateWorldCamera(CentreA);
        map.UpdateWorldCamera(CentreB);
        map.UpdateWorldCamera(CentreB + new FVector(map.Quality.StreamRadiusCm * 0.3f, 0f, 0f));

        Assert.False(map.IsLoadingLevel);
        Assert.Equal(Loc.F("Map.LoadMode.Locked", A), map.WorldStatus);
        Assert.Equal(shown, Shown(map));
    }

    /// <summary>The gap a sector closes: in island mode a cell picked from the tree holds only until the camera moves 25 % of the radius.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task InIslandModeAPickedCellStreamsOnOnceTheCameraMoves()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var map = await OpenTwoCellsAsync(ctx);
        await map.OpenWholeIslandAsync();
        map.SelectedNode = map.Nodes.Single(n => n.Cell == A);
        await map.LevelLoadCompletion;
        Assert.Equal(MapPageViewModel.CellPackages(map.World!, A).Order(), Shown(map).Order());

        var near = new FVector(CellCm - 5_000f, CellCm * 0.5f, 0f);
        map.UpdateWorldCamera(near);
        Assert.False(map.IsLoadingLevel); // held where it came up
        map.UpdateWorldCamera(near + new FVector(map.Quality.StreamRadiusCm * 0.3f, 0f, 0f));

        Assert.True(map.IsLoadingLevel);
        Assert.Equal(Loc.F("Map.World.Streaming", 1), map.WorldStatus);
        await map.LevelLoadCompletion;
        Assert.Contains(Shown(map), p => p.EndsWith("Landscape_B_0_1", StringComparison.Ordinal));
    }

    /// <summary>
    /// Review: after flying from A_0 into B_0 on the whole island, "Only this sector" loaded A_0 (the sector the Map had
    /// opened on) and paused streaming, leaving the camera over B_0 with nothing loaded. It takes the cell the camera is in.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task OnlyThisSectorTakesTheSectorTheCameraIsIn()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var map = await OpenTwoCellsAsync(ctx);
        await map.OpenWholeIslandAsync();
        map.UpdateWorldCamera(CentreA);
        await map.LevelLoadCompletion;
        map.UpdateWorldCamera(CentreB);
        await map.LevelLoadCompletion;
        Assert.Equal(B, map.SectorCell);
        Assert.Equal(B, map.NextSector); // shown beside the switch
        var shots = new List<CameraShot>();
        map.FrameSectorRequested += shots.Add;

        await map.ChooseSectorCommand.ExecuteAsync(null);

        Assert.True(map.IsSectorLocked);
        Assert.Equal(MapPageViewModel.CellPackages(map.World!, B).Order(), Shown(map).Order());
        Assert.Equal(Loc.F("Map.LoadMode.Locked", B), map.WorldStatus);
        Assert.Empty(shots); // the camera is in it already
    }

    /// <summary>
    /// A cell picked in the tree on the whole island is the one the switch loads, though world mode left the camera in
    /// A_0; once it is in, the view is asked to put the camera on it (with streaming paused it faced empty terrain).
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task OnlyThisSectorTakesTheCellPickedInTheTree()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var map = await OpenTwoCellsAsync(ctx);
        await map.OpenWholeIslandAsync();
        map.UpdateWorldCamera(CentreA);
        await map.LevelLoadCompletion;
        map.SelectedNode = map.Nodes.Single(n => n.Cell == B);
        await map.LevelLoadCompletion;
        map.UpdateWorldCamera(CentreA); // world mode keeps the camera where it was
        Assert.Equal(B, map.SectorCell);
        var shots = new List<CameraShot>();
        map.FrameSectorRequested += shots.Add;

        await map.ChooseSectorCommand.ExecuteAsync(null);

        Assert.True(map.IsSectorLocked);
        Assert.Equal(MapPageViewModel.CellPackages(map.World!, B).Order(), Shown(map).Order());
        Assert.Equal(Loc.F("Map.LoadMode.Locked", B), map.WorldStatus);
        var shot = Assert.Single(shots);
        Assert.Equal(B, MapPageViewModel.CellAt(map.World!, shot.LookAt.X, shot.LookAt.Y));
        Assert.Equal(CentreB.X, shot.LookAt.X, 1f);
        Assert.True(shot.Eye.Z - shot.LookAt.Z > CellCm * 0.5f, $"{shot.Eye} looks at {shot.LookAt}"); // high enough to see the whole cell
        Assert.Null(map.NextSector); // the picker shows it now
    }

    /// <summary>
    /// A single level picked in the tree ends the sector and is framed on its own: the picker then shows that level's cell,
    /// and the sector that was locked loads again when picked again (it was taken as still loaded).
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ASingleLevelPickedInSectorModeEndsTheSector()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var map = await OpenTwoCellsAsync(ctx);
        await map.LoadSectorAsync(B);
        Assert.True(map.IsSectorLocked);

        map.SelectedNode = Flatten(map.Nodes).First(n => n.Package?.PackagePath.EndsWith("/A_0_TestLevel", StringComparison.Ordinal) == true);
        await map.LevelLoadCompletion;
        Assert.False(map.IsWorldMode);
        Assert.False(map.IsSectorLocked);
        Assert.Equal(A, map.SectorCell);

        map.SectorCell = B;
        await map.LevelLoadCompletion;

        Assert.True(map.IsWorldMode);
        Assert.True(map.IsSectorLocked);
        Assert.Equal(Loc.F("Map.LoadMode.Locked", B), map.WorldStatus);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task LoadMoreResumesStreaming()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var map = await OpenTwoCellsAsync(ctx);
        await map.LoadSectorAsync(A);
        map.UpdateWorldCamera(CentreB);
        Assert.False(map.IsLoadingLevel);
        Assert.True(map.LoadMoreCommand.CanExecute(null));

        map.LoadMoreCommand.Execute(null);

        Assert.Equal(MapLoadMode.Island, map.LoadMode);
        Assert.False(map.IsSectorLocked);
        Assert.True(map.IsLoadingLevel); // at once, from the last camera: no waiting for the next tick
        Assert.Equal(Loc.F("Map.World.Streaming", 1), map.WorldStatus);
        await map.LevelLoadCompletion;
        Assert.Equal(["Landscape_B_0_1"], Shown(map).Select(p => p[(p.LastIndexOf('/') + 1)..]));
        Assert.Equal(Loc.F("Map.World.Around", 1), map.WorldStatus);
    }

    /// <summary>
    /// F400/F457: the progress was made on the worker, so its reports ran on the thread pool and a late one overwrote the
    /// load's final line. Made on the UI thread, every report arrives there and the last line is the summary.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task LoadProgressIsReportedOnTheUiThread()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var map = await OpenTwoCellsAsync(ctx);
        var onUi = new ConcurrentQueue<bool>();
        var steps = new ConcurrentQueue<LevelLoadProgress>();
        map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MapPageViewModel.LoadStatus) or nameof(MapPageViewModel.LoadProgress))
            {
                onUi.Enqueue(Dispatcher.UIThread.CheckAccess());
            }

            if (e.PropertyName == nameof(MapPageViewModel.LoadProgress))
            {
                steps.Enqueue(map.LoadProgress);
            }
        };

        await map.LoadLevelsAsync(MapPageViewModel.CellPackages(map.World!, A), landscapeStep: 4);
        HeadlessUi.PumpUntil(() => false, TimeSpan.FromMilliseconds(300)); // reports still queued would land now

        Assert.NotEmpty(onUi);
        Assert.All(onUi, Assert.True);
        Assert.Contains(steps, s => s.HasTotal && s.Done > 0 && s.Done <= s.Total);
        var scene = map.PreparedScene!;
        Assert.Equal(Loc.F("Map.LoadSummary", map.AllActors.Count, scene.Meshes.Count, scene.MissingMeshes.Count, scene.Terrain.Count, scene.Elapsed.TotalSeconds), map.LoadStatus);
    }

    /// <summary>With the game's paks the Map opened the whole island by itself; now it asks, and builds nothing until answered.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheMapAsksInsteadOfOpeningTheWholeIsland()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var map = await OpenTwoCellsAsync(ctx, asPak: true);
        Assert.Equal(ScumStudio.Assets.Catalog.AssetSourceKind.Paks, ctx.Services.Workspace.Catalog!.SourceKind);
        HeadlessUi.PumpUntil(() => map.WorldBackdrop is not null, TimeSpan.FromSeconds(2));

        Assert.True(map.NeedsLoadChoice);
        Assert.Null(map.LoadMode);
        Assert.False(map.IsWorldMode);
        Assert.Null(map.WorldBackdrop);
        Assert.Null(map.PreparedScene);
        Assert.Equal([A, B], map.Cells);
        Assert.Equal(A, map.SectorCell); // no camera kept yet: the first sector

        await map.OpenWholeIslandCommand.ExecuteAsync(null);

        Assert.False(map.NeedsLoadChoice);
        Assert.Equal(MapLoadMode.Island, map.LoadMode);
        Assert.True(map.IsWorldMode);
        Assert.NotNull(map.WorldBackdrop);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task CancelStopsASectorLoadWithoutLocking()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var map = await OpenTwoCellsAsync(ctx);
        await map.OpenWholeIslandAsync(); // the island's terrain is built: the sector's levels start loading at once

        var loading = map.LoadSectorAsync(A);
        Assert.True(map.IsSectorLoading);
        Assert.True(map.IsLoadingLevel);
        Assert.True(map.CancelLoadCommand.CanExecute(null));
        map.CancelLoadCommand.Execute(null);
        await loading;
        HeadlessUi.PumpUntil(() => false, TimeSpan.FromMilliseconds(200));

        Assert.False(map.IsSectorLoading);
        Assert.False(map.IsSectorLocked);
        Assert.False(map.IsLoadingLevel);
        Assert.Null(map.PreparedScene);
        Assert.Equal(Loc.F("Map.LoadMode.Cancelled", A), map.WorldStatus);
        Assert.Equal(Loc.F("Map.LoadMode.Cancelled", A), map.LoadStatus); // no late "Preparing" line over it

        // Still only a sector: flying does not stream the island in behind the cancel.
        map.UpdateWorldCamera(CentreA);
        map.UpdateWorldCamera(CentreB);
        Assert.False(map.IsLoadingLevel);

        // And the next sector loads and locks as usual.
        await map.LoadSectorAsync(B);
        Assert.True(map.IsSectorLocked);
        Assert.Equal(["Landscape_B_0_1"], Shown(map).Select(p => p[(p.LastIndexOf('/') + 1)..]));
    }

    /// <summary>
    /// The two templates the Map page shows (header item and centre card): every control has a hover text, the picker
    /// lists both sectors, and the switch keeps its checked half when that half is clicked again.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TheTemplatesRenderWithTooltips()
    {
        using var ctx = AppTestContext.Create(inline: false);
        using var map = await OpenTwoCellsAsync(ctx);
        var item = new ContentControl { Content = map, ContentTemplate = Template("MapLoadModeItemTemplate") };
        var choice = new ContentControl { Content = map, ContentTemplate = Template("MapLoadChoiceTemplate"), Width = 320 };
        var window = new Window { Width = 1280, Height = 720, Content = new StackPanel { Spacing = 20, Margin = new Thickness(20), Children = { item, choice } } };
        window.Show();
        try
        {
            HeadlessUi.Pump();
            HeadlessUi.SaveScreenshot(window, "map-load-mode-choice");
            Assert.True(Named<StackPanel>(window, "LoadChoicePanel").IsEffectivelyVisible);
            Assert.True(Named<StackPanel>(window, "LoadModeStrip").IsEffectivelyVisible);
            Assert.Equal([A, B], Named<ComboBox>(window, "ChoiceSectorBox").Items.Cast<MapCell>());
            Assert.Equal(A, Named<ComboBox>(window, "ChoiceSectorBox").SelectedItem);

            var controls = window.GetVisualDescendants().Where(v => v is Button or ToggleButton or ComboBox or ProgressBar).Cast<Control>().ToList();
            Assert.True(controls.Count >= 9, string.Join(", ", controls.Select(c => c.Name)));
            Assert.All(controls, c => Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(c) as string), c.Name));

            await map.OpenWholeIslandAsync();
            HeadlessUi.Pump();
            Assert.False(Named<StackPanel>(window, "LoadChoicePanel").IsVisible);
            var island = Named<ToggleButton>(window, "LoadIslandButton");
            var sector = Named<ToggleButton>(window, "LoadSectorButton");
            Assert.True(island.IsChecked);
            Click(window, island); // the checked half again: stays checked
            HeadlessUi.Pump();
            Assert.True(island.IsChecked);
            Assert.False(sector.IsChecked);
            map.UpdateWorldCamera(CentreB); // flown into B_0: the switch says which sector it would load
            await map.LevelLoadCompletion;
            HeadlessUi.Pump();
            var next = Named<TextBlock>(window, "NextSectorText");
            Assert.True(next.IsEffectivelyVisible);
            Assert.Equal("B_0", next.Text);
            HeadlessUi.SaveScreenshot(window, "map-load-mode-island");

            Click(window, sector);
            await map.LevelLoadCompletion;
            HeadlessUi.Pump();
            Assert.True(map.IsSectorLocked);
            Assert.True(sector.IsChecked);
            Assert.False(island.IsChecked);
            Assert.True(Named<Button>(window, "LoadMoreButton").IsEffectivelyVisible);
            Assert.True(Named<ComboBox>(window, "SectorBox").IsEffectivelyVisible);
            Assert.Equal(B, Named<ComboBox>(window, "SectorBox").SelectedItem);
            Assert.False(next.IsVisible); // the picker shows it
            HeadlessUi.SaveScreenshot(window, "map-load-mode-locked");

            Click(window, sector); // the locked sector again: nothing reloads, still checked
            HeadlessUi.Pump();
            Assert.False(map.IsLoadingLevel);
            Assert.True(sector.IsChecked);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<MapPageViewModel> OpenTwoCellsAsync(AppTestContext ctx, bool asPak = false)
    {
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true, Tile(A));
        foreach (var cell in new[] { A, B })
        {
            var name = $"Landscape_{cell}_1";
            await SyntheticLevels.BuildLevel(Tile(cell)).WriteAsync(Path.Combine([game, .. (SyntheticLevels.MapsFolder + "/" + name).Split('/')]), ".umap");
        }

        var map = new MapPageViewModel(ctx.Services) { RenderQuality = ScumStudio.Core.Settings.RenderQuality.Balanced }; // 300 m: one cell at a time
        if (asPak)
        {
            var paks = ctx.Combine("Paks");
            Directory.CreateDirectory(paks);
            await new ScumStudio.Pak.Writing.PakWriter().WriteFromDirectoryAsync(game, Path.Combine(paks, "pakchunk0-WindowsNoEditor.pak"));
            await ctx.Services.Workspace.OpenPathAsync(paks, ProgressSink.Null);
        }
        else
        {
            await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        }

        await map.LoadCompletion;
        Assert.Equal([A, B], map.Cells);
        return map;
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

    private static IDataTemplate Template(string key)
    {
        Assert.True(Application.Current!.TryFindResource(key, out var found), key + " is not merged into the app's resources");
        return Assert.IsAssignableFrom<IDataTemplate>(found);
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    private static void Click(Window window, Control control)
    {
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
    }
}
