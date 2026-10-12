using System.Numerics;
using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Landscape;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Tests.Level;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// The brush's paint mode (owner, 3.1.0: "with a short interval it plants at random, a huge number of trees very fast, in
/// some places and not in others, sometimes on half the road"). A synthetic island ground under the test level: flat at
/// Z 0 over ±150 m, a 10 m road band along X = 0 (Asphalt south of Y = 0, EraseFoliage north of it) and a corner dipped
/// 5 m under the sea (X and Y above 80 m).
/// </summary>
public sealed class BrushPaintTests
{
    private const string Tree = "/Game/ConZ_Files/Foliage/Trees/SM_TestTree";

    [Fact]
    public async Task ACursorOnATreeTopStillKeepsTheSpacing()
    {
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        var project = open.Project;

        for (var i = 0; i < 40; i++)
        {
            map.PaintOneAt(new FVector(30_00, 0, 0));
        }

        for (var i = 0; i < 40; i++)
        {
            map.PaintOneAt(new FVector(30_00, 0, 2500)); // the cursor rests on a canopy 25 m up
        }

        map.EndPaintStroke();
        var planted = Planted(project);
        Assert.NotEmpty(planted);
        AssertSpaced(planted, 300f);
    }

    [Fact]
    public async Task TheBrushNeverPlantsOnARoadOrInTheSea()
    {
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        var (project, field) = (open.Project, open.Field);

        Sweep(map);
        map.EndPaintStroke();
        var planted = Planted(project);
        Assert.Contains(planted, p => p.X < -500f);
        Assert.Contains(planted, p => p.X > 500f);
        Assert.All(planted, p => Assert.False(MathF.Abs(p.X) < 500f, $"{p} on the road"));
        Assert.All(planted, p => Assert.True(field.SampleHeight(p.X, p.Y) >= -1f, $"{p} in the sea"));
    }

    [Fact]
    public async Task TheTimerPlantsOneObjectPerTickNearestTheCursorUntilTheCircleIsFull()
    {
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        map.BrushRadius = 6;
        var full = 0;
        ctx.Services.Notifications.Toasts.CollectionChanged += (_, e) =>
            full += e.NewItems?.OfType<ScumStudio.App.ViewModels.ToastViewModel>().Count(t => t.Message.StartsWith("The circle is full", StringComparison.Ordinal)) ?? 0;

        var centre = new FVector(-3000, -3000, 0);
        var distances = new List<float>();
        for (var tick = 0; tick < 60; tick++)
        {
            var before = map.Clones.Count;
            map.PaintOneAt(centre);
            if (map.Clones.Count == before)
            {
                break; // full
            }

            Assert.Equal(before + 1, map.Clones.Count); // exactly one a tick
            var at = map.Clones[^1].RootWorld.Translation;
            distances.Add(Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(centre.X, centre.Y)));
        }

        var planted = map.Clones.Count;
        Assert.InRange(planted, 6, 40);
        Assert.True(distances[0] < 225f, $"first at {distances[0]:0} cm"); // within the cell under the cursor
        for (var i = 1; i < distances.Count; i++)
        {
            Assert.True(distances[i] >= distances[i - 1] - 1f, $"tick {i} at {distances[i]:0} cm after one at {distances[i - 1]:0} cm");
        }

        for (var tick = 0; tick < 10; tick++)
        {
            map.PaintOneAt(centre);
        }

        Assert.Equal(planted, map.Clones.Count);
        Assert.Equal(1, full);
        map.EndPaintStroke();
        var batch = Planted(open.Project);
        Assert.Equal(planted, batch.Count);
        AssertSpaced(batch, 300f);
    }

    [Fact]
    public async Task ThePreviewShowsExactlyWhatTheClickPlants()
    {
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        var centre = new FVector(300, -2000, 0); // over the road band's Asphalt half

        map.PreviewPaintAt(centre);
        var spots = map.PaintSpots;
        Assert.NotNull(spots);
        var shown = spots.Where(s => s.Plants).Select(s => s.At).ToList();
        Assert.Contains(spots, s => !s.Plants);
        Assert.All(spots.Where(s => !s.Plants), s => Assert.True(MathF.Abs(s.At.X) < 600f, $"{s.At} left out off the road"));

        map.PaintAt(centre);
        map.EndPaintStroke();
        var planted = Planted(open.Project);
        Assert.True(shown.Count > 20, $"{shown.Count} shown");
        Assert.Equal(shown.Count, planted.Count);
        Assert.All(planted, p => Assert.Contains(shown, s => FVector.Distance(s, p) < 1f));
    }

    [Fact]
    public async Task ThePreviewOfALargeCircleShowsEverySpotTheClickPlants()
    {
        // The tester's case: at 1 m spacing a 50 m circle has ~18k spots; the preview looked at 2000 of them and showed 1473 of 4092.
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        map.BrushRadius = 50;
        map.PaintSpacing = 1;
        map.PaintSpacingMax = 1;
        var centre = new FVector(-9000, -9000, 0);

        map.PreviewPaintAt(centre);
        var shown = map.PaintSpots!.Where(s => s.Plants).Select(s => s.At).ToList();
        Assert.Empty(map.PaintMarksText);
        map.PaintAt(centre);
        map.EndPaintStroke();

        var planted = Planted(open.Project);
        Assert.True(planted.Count > 3000, $"{planted.Count} planted");
        AssertSameSpots(shown, planted);
    }

    [Fact]
    public async Task ACircleWithMoreSpotsThanMarksSaysWhatTheClickPlants()
    {
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        map.PaintMarkCap = 40;
        var centre = new FVector(300, -2000, 0); // over the road band: plants and left-out spots

        map.PreviewPaintAt(centre);
        Assert.InRange(map.PaintSpots!.Count, 20, 40);
        var said = System.Text.RegularExpressions.Regex.Match(map.PaintMarksText, @"plants (\d+) and leaves out (\d+)");
        Assert.True(said.Success, map.PaintMarksText);
        map.PaintAt(centre);
        map.EndPaintStroke();

        Assert.Equal(said.Groups[1].Value, Planted(open.Project).Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(int.Parse(said.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) > 0);
        map.BrushPaint = false;
        Assert.Null(map.PaintSpots);
        Assert.Empty(map.PaintMarksText);
    }

    [Fact]
    public async Task TheTimersNextSpotIsMarkedWhenThereAreMoreSpotsThanMarks()
    {
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        map.PaintTimed = true;
        var sea = new FVector(9000, 9000, 0); // the nearest spots are water, the next tick's is 10 m off on the shore

        foreach (var cap in new[] { 4, 7, 10, 13 })
        {
            map.PaintMarkCap = cap;
            map.PreviewPaintAt(sea);
            Assert.InRange(map.PaintSpots!.Count, 2, cap);
            Assert.Single(map.PaintSpots!, s => s.Plants);
            Assert.NotEmpty(map.PaintMarksText);
        }
    }

    [Fact]
    public async Task AWideSpacingHoldsOnASteepSlope()
    {
        // The tester's case: 20 m spacing on a 60 % slope planted trees 17.7 m apart, 10.6 m apart in height.
        using var ctx = AppTestContext.Create();
        var field = Slope(0.6f);
        var open = await OpenAsync(ctx, field);
        using var map = open.Map;
        map.BrushRadius = 50;
        map.PaintSpacingMax = 20;
        map.PaintSpacing = 20;

        FVector? previous = null;
        for (var x = -10000f; x <= 10000f; x += 500f)
        {
            var at = new FVector(x, -2000f, 0f);
            map.PaintAt(at, previous);
            previous = at;
        }

        map.EndPaintStroke();
        var planted = Planted(open.Project);
        Assert.True(planted.Count > 20, $"{planted.Count} planted");
        Assert.All(planted, p => Assert.True(MathF.Abs(p.Z - field.SampleHeight(p.X, p.Y)!.Value) < 1f, $"{p} off the ground"));
        AssertSpaced(planted, 2000f);
    }

    [Fact]
    public async Task WithNoLandscapeTreesNeverStackWhateverTheCursorsHeight()
    {
        // A level opened without its landscape tile: the plane under the cursor is the ground, and with no ground to tell
        // heights apart the spacing holds at any height (the cursor moving onto a canopy 25 m up stacked trees again).
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        map.WorldBackdrop = null;
        var (ground, canopy) = (new FVector(-4000, 3000, 0), new FVector(-4000, 3000, 2500));

        map.PreviewPaintAt(ground);
        Assert.Contains(map.PaintSpots!, s => s.Plants);
        for (var i = 0; i < 40; i++)
        {
            map.PaintOneAt(ground);
        }

        for (var i = 0; i < 40; i++)
        {
            map.PaintOneAt(canopy);
        }

        map.EndPaintStroke();
        var planted = Planted(open.Project);
        Assert.All(planted.Take(40), p => Assert.Equal(0f, p.Z));
        AssertSpaced(planted, 300f);
    }

    [Fact]
    public async Task AMissingBlueprintInThePaletteDoesNotMakeEveryHoverGatherAgain()
    {
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        map.AddToPalette(new ReplaceCandidate(new ReplaceChoice("BP_Nowhere", "/Game/ConZ_Files/Blueprints/BP_Nowhere", true, false), null));
        var made = map.PaintStrokesMade;

        for (var i = 0; i < 10; i++)
        {
            map.PreviewPaintAt(new FVector(-4000 + (i * 100), 3000, 0));
        }

        // Once, and once more when its lookup ends: the stroke without it was made again on every move while it was missing.
        Assert.InRange(map.PaintStrokesMade - made, 1, 2);
    }

    [Fact]
    public async Task AMixedPaletteNeverGrowsATreeOutOfABush()
    {
        const string Bush = "/Game/ConZ_Files/Foliage/Bush/SM_TestBush";
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        map.AddToPalette(new ReplaceCandidate(new ReplaceChoice("SM_TestBush", Bush, false, false), null));
        map.PaintSpacing = 1; // 1-3 m: a spot every 50 cm, so a tree that passed over bushes often landed in one

        map.PaintAt(new FVector(-4000, 3000, 0));
        map.EndPaintStroke();

        var ops = Assert.IsType<BatchOp>(open.Project.Journal.Applied[^1].Op).Ops.OfType<AddStaticMeshActorOp>().ToList();
        var bushes = ops.Where(o => o.StaticMesh.Contains("/Bush/", StringComparison.Ordinal)).Select(o => o.Transform.Location).ToList();
        var trees = ops.Where(o => !o.StaticMesh.Contains("/Bush/", StringComparison.Ordinal)).Select(o => o.Transform.Location).ToList();
        Assert.NotEmpty(bushes);
        Assert.NotEmpty(trees);
        AssertSpaced(trees, 100f);
        AssertSpaced(bushes, 100f);
        Assert.All(trees, t => Assert.All(bushes, b =>
            Assert.True(Vector2.Distance(new Vector2(t.X, t.Y), new Vector2(b.X, b.Y)) >= SpacingIndex.UndergrowthClear - 0.1f, $"tree {t} in bush {b}")));
    }

    [Fact]
    public async Task ASecondStrokeKeepsItsDistanceFromTheFirst()
    {
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;

        map.PaintAt(new FVector(-4000, 3000, 0));
        map.EndPaintStroke();
        var first = Planted(open.Project);
        map.PaintAt(new FVector(-3000, 3000, 0));
        map.EndPaintStroke();
        var second = Planted(open.Project);

        Assert.NotEmpty(first);
        Assert.NotEmpty(second); // the half the first circle did not cover
        AssertSpaced([.. first, .. second], 300f);
    }

    [Fact]
    public async Task OneStrokeIsOneUndo()
    {
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        var (pristine, rows) = (map.AllActors.Count, ctx.Services.Projects.History.Count);

        Sweep(map);
        var shown = map.Clones.Count;
        Assert.Equal(rows, ctx.Services.Projects.History.Count); // drawn while painting, journaled when let go
        map.EndPaintStroke();

        Assert.Equal(rows + 1, ctx.Services.Projects.History.Count);
        Assert.Equal(shown, Assert.IsType<BatchOp>(Assert.Single(open.Project.Journal.Applied).Op).Ops.Count);
        Assert.Equal(pristine + shown, map.AllActors.Count);
        ctx.Services.Projects.Undo();
        Assert.Empty(open.Project.State.AddedActors);
        Assert.Equal(pristine, map.AllActors.Count);
    }

    [Fact]
    public async Task RandomTurnAndSizeOffPlantUprightAtSize1()
    {
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;
        map.PaintRandomTurn = false;
        map.PaintRandomSize = false;

        map.PaintAt(new FVector(-4000, 3000, 0));
        map.EndPaintStroke();
        var plain = Assert.IsType<BatchOp>(open.Project.Journal.Applied[^1].Op).Ops.OfType<AddStaticMeshActorOp>().ToList();
        Assert.NotEmpty(plain);
        Assert.All(plain, o => Assert.Equal(FRotator.Zero, o.Transform.Rotation));
        Assert.All(plain, o => Assert.Equal(FVector.One, o.Transform.Scale));

        map.PaintRandomTurn = true;
        map.PaintRandomSize = true;
        map.PaintOnObjects = true;
        map.PaintAt(new FVector(4000, -4000, 0));
        map.EndPaintStroke();
        var varied = Assert.IsType<BatchOp>(open.Project.Journal.Applied[^1].Op).Ops.OfType<AddStaticMeshActorOp>().ToList();
        Assert.Contains(varied, o => MathF.Abs(o.Transform.Rotation.Yaw) > 1f);
        Assert.Contains(varied, o => MathF.Abs(o.Transform.Scale.X - 1f) > 0.01f);
        Assert.Contains("\"OnObjects\": true", File.ReadAllText(Path.Combine(open.Project.DirectoryPath, MapPageViewModel.PaintPaletteFileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReportCountsRoadsAndWater()
    {
        using var ctx = AppTestContext.Create();
        var open = await OpenAsync(ctx);
        using var map = open.Map;

        Sweep(map);
        var report = System.Text.RegularExpressions.Regex.Match(map.PaintReportText, @"^(\d+) planted · (\d+) left out$");
        Assert.True(report.Success, map.PaintReportText);
        Assert.True(int.Parse(report.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) > 0);
        map.EndPaintStroke();

        var toast = Assert.Single(ctx.Services.Notifications.Toasts, t => t.Title.StartsWith("Planted", StringComparison.Ordinal));
        var road = System.Text.RegularExpressions.Regex.Match(toast.Message, @"(\d+) on roads or paths");
        var water = System.Text.RegularExpressions.Regex.Match(toast.Message, @"(\d+) in water");
        Assert.True(road.Success && int.Parse(road.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0, toast.Message);
        Assert.True(water.Success && int.Parse(water.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0, toast.Message);
        Assert.Equal(report.Groups[1].Value, Planted(open.Project).Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A diagonal sweep from the south-west over the road band and into the sea corner, 5 m a dab.</summary>
    private static void Sweep(MapPageViewModel map)
    {
        FVector? previous = null;
        for (var t = -4000f; t <= 13000f; t += 500f)
        {
            var at = new FVector(t, t, 0f);
            map.PaintAt(at, previous);
            previous = at;
        }
    }

    private static async Task<(MapPageViewModel Map, Project Project, TerrainHeightField Field)> OpenAsync(AppTestContext ctx, TerrainHeightField? ground = null)
    {
        var game = ctx.Combine("game");
        SyntheticLevels.WriteContent(game, withBlueprintPackage: true);
        var map = new MapPageViewModel(ctx.Services);
        await ctx.Services.Workspace.OpenLooseAsync(game, ProgressSink.Null);
        await map.LoadCompletion;
        var project = await ctx.Services.Projects.CreateAsync(ctx.Combine("projects"), "Brush");
        await map.LoadLevelsAsync([SyntheticLevels.LevelPath]);
        var field = ground ?? Island();
        map.WorldBackdrop = new PreparedLevelScene([], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, TextureImage>(), [], [], [], TimeSpan.Zero)
        {
            HeightField = field,
            SeaLevelCm = ground is null ? 0f : -100_000f,
        };
        map.AddToPalette(new ReplaceCandidate(new ReplaceChoice("SM_TestTree", Tree, false, false), null));
        map.BrushSelect = true;
        map.BrushPaint = true;
        map.BrushRadius = 15;
        map.PaintSpacingMax = 3;
        map.PaintSpacing = 3;
        return (map, project, field);
    }

    /// <summary>One 300 m component of 1 m quads centred on the origin: road band, sea corner, forest elsewhere.</summary>
    internal static TerrainHeightField Island()
    {
        const int Quads = 300;
        const int N = Quads + 1;
        static float X(int i) => -15000f + (100f * i);
        var heights = new float[N * N];
        var forest = new byte[N * N];
        var asphalt = new byte[N * N];
        var erase = new byte[N * N];
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var i = (y * N) + x;
                var road = MathF.Abs(X(x)) <= 500f;
                heights[i] = X(x) > 8000f && X(y) > 8000f ? -500f : 0f;
                asphalt[i] = road && X(y) < 0f ? (byte)255 : (byte)0;
                erase[i] = road && X(y) >= 0f ? (byte)255 : (byte)0;
                forest[i] = (byte)(255 - asphalt[i]);
            }
        }

        var toWorld = Matrix4x4.CreateScale(100f, 100f, 1f) * Matrix4x4.CreateTranslation(-15000f, -15000f, 0f);
        var surface = new LandscapeSurface("Island", (0, 0), Quads, toWorld, heights);
        var layers = new LandscapeComponentLayers("Island", (0, 0), Quads,
            [new LandscapeLayerWeights("Forest_Ground", null, true, forest), new LandscapeLayerWeights("Asphalt", null, true, asphalt),
                new LandscapeLayerWeights("EraseFoliage", null, false, erase)], null, [], 3, []);
        return new TerrainHeightField([(surface, layers)]);
    }

    /// <summary>The same 300 m of forest rising <paramref name="slope"/> per metre eastwards, without a road or the sea.</summary>
    internal static TerrainHeightField Slope(float slope)
    {
        const int Quads = 300;
        const int N = Quads + 1;
        var heights = new float[N * N];
        var forest = new byte[N * N];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] = slope * 100f * (i % N);
            forest[i] = 255;
        }

        var toWorld = Matrix4x4.CreateScale(100f, 100f, 1f) * Matrix4x4.CreateTranslation(-15000f, -15000f, 0f);
        return new TerrainHeightField([(new LandscapeSurface("Slope", (0, 0), Quads, toWorld, heights),
            new LandscapeComponentLayers("Slope", (0, 0), Quads, [new LandscapeLayerWeights("Forest_Ground", null, true, forest)], null, [], 1, []))]);
    }

    /// <summary>Asserts that the marked plants and the planted objects are the same places (within 1 cm).</summary>
    private static void AssertSameSpots(IReadOnlyList<FVector> shown, IReadOnlyList<FVector> planted)
    {
        Assert.Equal(shown.Count, planted.Count);
        var grid = shown.ToLookup(s => ((int)MathF.Floor(s.X), (int)MathF.Floor(s.Y)));
        Assert.All(planted, p => Assert.True(
            Enumerable.Range(-1, 3).SelectMany(dx => Enumerable.Range(-1, 3).SelectMany(dy => grid[((int)MathF.Floor(p.X) + dx, (int)MathF.Floor(p.Y) + dy)]))
                .Any(s => FVector.Distance(s, p) < 1f), $"{p} planted but not shown"));
    }

    /// <summary>Where the last journaled stroke put its objects.</summary>
    private static List<FVector> Planted(Project project) =>
        [.. Assert.IsType<BatchOp>(project.Journal.Applied[^1].Op).Ops.OfType<AddStaticMeshActorOp>().Select(o => o.Transform.Location)];

    private static void AssertSpaced(IReadOnlyList<FVector> planted, float least)
    {
        for (var i = 0; i < planted.Count; i++)
        {
            for (var j = i + 1; j < planted.Count; j++)
            {
                var d = Vector2.Distance(new Vector2(planted[i].X, planted[i].Y), new Vector2(planted[j].X, planted[j].Y));
                Assert.True(d >= least - 0.1f, $"{planted[i]} and {planted[j]} are {d:0} cm apart");
            }
        }
    }
}
