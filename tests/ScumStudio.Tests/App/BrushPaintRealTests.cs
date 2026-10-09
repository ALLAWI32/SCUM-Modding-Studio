using Avalonia.VisualTree;
using System.Diagnostics;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "choose the tree types, sweep the brush and it plants them the way the game's own forest looks". On the real
/// farm A_3_Farm_01 with its landscape tiles, a 15 m brush with two of the tile's own foliage trees swept 20 m over open
/// ground plants new instances of the tile's foliage (they chop like stock trees), each with its foot on the terrain, no
/// two closer than the spacing and none on a tree already there; the export grows those foliage components by exactly
/// as many trees. Real game files only (<c>SCUM_PAKS</c>).
/// </summary>
public sealed class BrushPaintRealTests
{
    private const string Farm = "/Game/ConZ_Files/Maps/The_Island/A_3_Farm_01";
    private const float Spacing = 400f;

    private readonly ITestOutputHelper _output;

    public BrushPaintRealTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ATwentyMetreStrokePlantsTheTilesOwnTreesOnTheGroundSpacedApart()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var ctx = AppTestContext.Create();
        ctx.Services.Keys.Set(AesKeyText.FromEnvironmentOrStore()!);
        ctx.Services.UpdateSettings(s => s with { GamePaksFolder = paks });
        await ctx.Services.Workspace.ConnectAsync(ProgressSink.Null);
        var catalog = ctx.Services.Workspace.Catalog!;
        using var map = new MapPageViewModel(ctx.Services);
        await map.LoadCompletion;
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Forest");
        await map.LoadLevelsAsync([Farm]);
        var farm = map.AllActors.First(a => a.Name == "BP_Greenhouse_A_EXT_Ruined_01_2").Actor.WorldTransform.Translation;
        var world = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog, null);
        var tiles = MapPageViewModel.LevelsAround(world, farm, 2000f).Where(p => p.Contains("/Landscape_", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(tiles);
        await map.LoadLevelsAsync([Farm, .. tiles]);
        var scene = map.PreparedScene!;
        Assert.NotNull(scene.HeightField);

        // The tile with the most foliage trees near the farm, and its two most common trees.
        static bool IsTree(string? mesh) => mesh?.Contains("/Foliage/", StringComparison.OrdinalIgnoreCase) == true && mesh.Contains("/Trees/", StringComparison.OrdinalIgnoreCase);
        var near = map.AllActors.Where(a => a.Level.Name.StartsWith("Landscape_", StringComparison.OrdinalIgnoreCase))
            .GroupBy(a => a.Level)
            .Select(g => (Tile: g.Key, Trees: g.SelectMany(a => a.Actor.InstanceTransforms).Where(i => IsTree(i.StaticMeshPath)).ToList()))
            .ToList();
        var (tile, own) = near.Where(t => t.Trees.Select(i => i.StaticMeshPath).Distinct().Count() >= 2)
            .MaxBy(t => t.Trees.Count(i => Flat(i.WorldTransform.Translation, farm) < 30_000f));
        Assert.True(tile is not null, "no tile with two kinds of trees: " + string.Join(", ", near.Select(t => $"{t.Tile.Name} {t.Trees.Count}")));
        var trees = own.GroupBy(i => i.StaticMeshPath!).OrderByDescending(g => g.Count()).Take(2).Select(g => g.Key).ToList();
        _output.WriteLine($"tile {tile.Name}, palette {string.Join(", ", trees)}");

        // Open ground within 300 m of the farm, inside the tile: the 20 m stretch with the fewest things standing near it.
        var tileTrees = map.AllActors.Where(a => ReferenceEquals(a.Level, tile)).SelectMany(a => a.Actor.InstanceTransforms).Select(i => i.WorldTransform.Translation).ToList();
        var (minX, maxX, minY, maxY) = (tileTrees.Min(t => t.X) + 10_000f, tileTrees.Max(t => t.X) - 10_000f, tileTrees.Min(t => t.Y) + 10_000f, tileTrees.Max(t => t.Y) - 10_000f);
        var standing = scene.Placements.Where(p => !p.MeshPath.Contains("/Grass/", StringComparison.OrdinalIgnoreCase)).Select(p => p.World.Translation).ToList();
        var best = (Count: int.MaxValue, At: FVector.Zero);
        for (var x = farm.X - 30_000f; x <= farm.X + 30_000f; x += 2_500f)
        {
            for (var y = farm.Y - 30_000f; y <= farm.Y + 30_000f; y += 2_500f)
            {
                if (x < minX || x > maxX || y < minY || y > maxY || scene.HeightField!.SampleHeight(x, y) is not { } z)
                {
                    continue;
                }

                var at = new FVector(x, y, z);
                var count = standing.Count(p => MathF.Abs(p.X - x) < 3_000f && MathF.Abs(p.Y - y) < 2_500f);
                if (count < best.Count)
                {
                    best = (count, at);
                }
            }
        }

        Assert.NotEqual(int.MaxValue, best.Count);
        _output.WriteLine($"stroke round {best.At} ({best.Count} things within reach)");

        foreach (var mesh in trees)
        {
            var package = AssetPaths.SplitObjectPath(mesh).PackagePath;
            map.AddToPalette(new ReplaceCandidate(new ReplaceChoice(package[(package.LastIndexOf('/') + 1)..], package, false, false), null));
        }

        map.BrushSelect = true;
        map.BrushPaint = true;
        map.BrushRadius = 15;
        map.PaintSpacing = Spacing / 100f;

        // The stroke: 20 m along X, a dab every 50 cm on the terrain.
        var before = map.AllActors.SelectMany(a => a.Actor.InstanceTransforms).Where(i => IsTree(i.StaticMeshPath) || i.StaticMeshPath?.Contains("/Bush/", StringComparison.OrdinalIgnoreCase) == true)
            .Where(i => Across(scene, i.StaticMeshPath!, i.WorldTransform) >= 100f)
            .Select(i => i.WorldTransform.Translation).Where(t => Flat(t, best.At) < 3_500f).ToList();
        var rows = ctx.Services.Projects.History.Count;
        var applied = project.Journal.Applied.Count;
        var times = new List<double>();
        var watch = Stopwatch.StartNew();
        FVector? previous = null;
        for (var step = 0; step <= 40; step++)
        {
            var x = best.At.X - 1_000f + (step * 50f);
            var at = new FVector(x, best.At.Y, scene.HeightField!.SampleHeight(x, best.At.Y) ?? best.At.Z);
            watch.Restart();
            map.PaintAt(at, previous);
            times.Add(watch.Elapsed.TotalMilliseconds);
            previous = at;
        }

        watch.Restart();
        map.EndPaintStroke();
        var commit = watch.Elapsed.TotalMilliseconds;
        var first = times[0];
        times.RemoveAt(0);
        times.Sort();
        _output.WriteLine($"first dab {first:0.0} ms (obstacles and ground loaded), then median {times[times.Count / 2]:0.00} ms, p95 {times[(int)(times.Count * 0.95)]:0.00} ms, max {times[^1]:0.00} ms; commit {commit:0} ms");

        // One History row; every object a new instance of the tile's own foliage.
        Assert.Equal(rows + 1, ctx.Services.Projects.History.Count);
        var batch = Assert.IsType<BatchOp>(project.Journal.Applied[^1].Op);
        var adds = batch.Ops.Select(o => Assert.IsType<AddInstanceOp>(o)).ToList();
        _output.WriteLine($"{adds.Count} trees planted into {string.Join(", ", adds.GroupBy(a => a.Target.Component).Select(g => $"{g.Key} +{g.Count()}"))}");
        Assert.True(adds.Count > 5, $"{adds.Count} planted");
        Assert.All(adds, a => Assert.Equal(tile.PackagePath, a.Target.Level));
        var planted = new List<FVector>();
        foreach (var add in adds)
        {
            var holder = map.AllActors.Single(a => !a.IsAdded && ActorRef.Comparer.Equals(a.Reference, add.Target.ActorRef));
            Assert.Contains(holder.Actor.FindComponent(add.Target.Component)!.StaticMeshPath, trees);
            var at = map.InstanceTransforms[InstanceKey.Of(holder.SelectableId, add.Target.Component, add.Target.Index)].Translation;
            var ground = scene.HeightField!.SampleHeight(at.X, at.Y)!.Value;
            Assert.True(MathF.Abs(at.Z - ground) < 5f, $"{at}: {at.Z - ground:0.0} cm off the terrain");
            planted.Add(at);
        }

        for (var i = 0; i < planted.Count; i++)
        {
            for (var j = i + 1; j < planted.Count; j++)
            {
                Assert.True(Flat(planted[i], planted[j]) >= Spacing - 1f, $"{planted[i]} and {planted[j]} are {Flat(planted[i], planted[j]):0} cm apart");
            }

            Assert.All(before, t => Assert.True(Flat(planted[i], t) >= Spacing - 1f, $"{planted[i]} is {Flat(planted[i], t):0} cm from the tree at {t}"));
        }

        // The export appends them to the tile's foliage: the game's reader counts the stored trees and the planted ones.
        var result = await new ProjectExporter().ExportAsync(project, catalog, new ExportOptions { OutputDirectory = ctx.Combine("out"), WritePak = false });
        var level = Assert.Single(result.Levels);
        Assert.Equal(adds.Count, level.Report.AddedInstances);
        using var written = AssetCatalog.OpenLoose(result.StagingDirectory);
        var components = written.LoadPackage(tile.PackagePath).GetExports().OfType<UInstancedStaticMeshComponent>().ToList();
        foreach (var group in adds.GroupBy(a => a.Target.Component))
        {
            var stored = tile.FindActor(group.First().Target.Actor)!.FindComponent(group.Key)!.Instances.Count;
            var component = components.Single(c => c.Name == group.Key);
            Assert.Equal(stored + group.Count(), component.PerInstanceSMData!.Length);
            var last = group.MaxBy(a => a.Target.Index)!;
            var t = component.PerInstanceSMData[last.Target.Index].TransformData.Translation;
            Assert.Equal(last.Transform.Location.X, (float)t.X, 1f);
            Assert.Equal(last.Transform.Location.Y, (float)t.Y, 1f);
            Assert.Equal(last.Transform.Location.Z, (float)t.Z, 1f);
        }

        // Undo takes the whole stroke away.
        ctx.Services.Projects.Undo();
        Assert.Empty(project.State.AddedInstances);
        Assert.Equal(applied, project.Journal.Applied.Count);
        Assert.True(times[times.Count / 2] < 2.0, $"median {times[times.Count / 2]:0.00} ms per move");

        // A second stroke starts without the first one's hitch (the brush's map of the levels is kept).
        watch.Restart();
        map.PaintAt(best.At with { Y = best.At.Y + 3_000f });
        var again = watch.Elapsed.TotalMilliseconds;
        map.EndPaintStroke();
        _output.WriteLine($"second stroke's first dab {again:0.0} ms");

        // Timer mode (owner: "a timer: every so many seconds it adds a tree where the brush is"): the brush held still, each
        // tick plants one object inside the circle, the spacing still kept; the stroke is one History row.
        var still = best.At with { Y = best.At.Y - 3_000f };
        still = still with { Z = scene.HeightField!.SampleHeight(still.X, still.Y) ?? still.Z };
        rows = ctx.Services.Projects.History.Count;
        for (var tick = 0; tick < 6; tick++)
        {
            map.PaintOneAt(still);
        }

        map.EndPaintStroke();
        Assert.Equal(rows + 1, ctx.Services.Projects.History.Count);
        var timed = Assert.IsType<BatchOp>(project.Journal.Applied[^1].Op).Ops.Select(o => Assert.IsType<AddInstanceOp>(o)).ToList();
        Assert.InRange(timed.Count, 4, 6);
        var ticks = timed.Select(add =>
        {
            var holder = map.AllActors.Single(a => !a.IsAdded && ActorRef.Comparer.Equals(a.Reference, add.Target.ActorRef));
            return map.InstanceTransforms[InstanceKey.Of(holder.SelectableId, add.Target.Component, add.Target.Index)].Translation;
        }).ToList();
        _output.WriteLine($"timer: {timed.Count} objects in 6 ticks at {string.Join(", ", ticks.Select(t => $"({t.X - still.X:0}, {t.Y - still.Y:0})"))} cm from the brush");
        Assert.All(ticks, t => Assert.True(Flat(t, still) <= 1_500f + 1f, $"{t} is {Flat(t, still):0} cm from the brush"));
        for (var i = 0; i < ticks.Count; i++)
        {
            for (var j = i + 1; j < ticks.Count; j++)
            {
                Assert.True(Flat(ticks[i], ticks[j]) >= Spacing - 1f, $"{ticks[i]} and {ticks[j]} are {Flat(ticks[i], ticks[j]):0} cm apart");
            }
        }
    }

    /// <summary>
    /// The Paint menu as the owner sees it (screenshot with <c>SCUMSTUDIO_SCREENSHOTS</c>): Brush › Paint shows the palette
    /// button; its menu lists the game's trees, bushes and rocks in the picking card, a pick lands in the palette with its
    /// picture and a remove button, and the palette file is written into the project folder.
    /// </summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task ThePaintMenuPicksThePaletteFromTheGamesPlants()
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
            var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Palette");
            await map.LoadLevelsAsync([Farm]);
            map.BrushSelect = true;
            map.BrushPaint = true;
            HeadlessUi.Pump();

            var button = HeadlessUi.FindNamed<Avalonia.Controls.Button>(window, "PaintPaletteButton")!;
            Assert.True(button.IsEffectivelyVisible);
            button.Flyout!.ShowAt(button);
            HeadlessUi.Pump();
            var panel = (Avalonia.Controls.StackPanel)((Avalonia.Controls.Flyout)button.Flyout).Content!;
            var all = map.PaintPicker.All;
            Assert.Contains(all, c => c.Choice.PackagePath.Contains("/Foliage/Continental/Trees/", StringComparison.Ordinal));
            Assert.Contains(all, c => c.Choice.PackagePath.Contains("/Bush/", StringComparison.Ordinal));
            Assert.Contains(all, c => c.Choice.PackagePath.Contains("/Landscape/Rocks/", StringComparison.Ordinal));
            Assert.DoesNotContain(all, c => c.Choice.PackagePath.Contains("Cave_", StringComparison.Ordinal) || c.Choice.PackagePath.Contains("Debris", StringComparison.Ordinal));

            // The card drops its list down; two picks land in the palette, the card stays ready for the next one.
            var card = HeadlessUi.Find<Avalonia.Controls.Primitives.ToggleButton>(panel).Single(t => t.Name == "PickerCard");
            card.IsChecked = true;
            HeadlessUi.Pump();
            Assert.True(map.PaintPicker.IsOpen);
            map.PaintPicker.Search = "Oak";
            HeadlessUi.Pump();
            map.PaintPicker.PickCommand.Execute(map.PaintPicker.Items[0]);
            map.PaintPicker.Search = "Rock";
            HeadlessUi.Pump();
            map.PaintPicker.PickCommand.Execute(map.PaintPicker.Items[0]);
            HeadlessUi.Pump();
            Assert.Null(map.PaintPicker.Selected);
            Assert.Equal(2, map.PaintPalette.Count);
            Assert.Equal(map.PaintPalette.Count, HeadlessUi.FindNamed<Avalonia.Controls.ItemsControl>(panel, "PaintPaletteList")!.ItemCount);
            Assert.Equal(ScumStudio.App.Localization.Loc.F("Map.Paint.Palette", 2), map.PaintPaletteText);
            var scroll = panel.FindAncestorOfType<Avalonia.Controls.ScrollViewer>();
            Assert.True(scroll is null || scroll.Extent.Width <= scroll.Viewport.Width + 0.5, "the menu is clipped");
            HeadlessUi.SaveScreenshot(window, "map-brush-paint");

            // Remove takes one out; the file in the project folder follows.
            map.RemovePaintItemCommand.Execute(map.PaintPalette[1]);
            var file = Path.Combine(project.DirectoryPath, MapPageViewModel.PaintPaletteFileName);
            Assert.True(File.Exists(file));
            Assert.Contains(map.PaintPalette[0].Choice.ObjectPath, File.ReadAllText(file), StringComparison.Ordinal);
            Assert.Single(map.PaintPalette);
        }
        finally
        {
            window.Close();
        }
    }

    private static float Flat(FVector a, FVector b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>How wide a placed mesh is on the ground (0 when not loaded).</summary>
    private static float Across(PreparedLevelScene scene, string mesh, FTransform world) =>
        scene.Meshes.TryGetValue(mesh, out var asset) && !asset.Mesh.Bounds.IsEmpty
            ? MathF.Max(asset.Mesh.Bounds.Size.X * MathF.Abs(world.Scale3D.X), asset.Mesh.Bounds.Size.Y * MathF.Abs(world.Scale3D.Y))
            : 0f;
}
