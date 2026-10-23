using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Controls;
using ScumStudio.Level.Spawns;
using ScumStudio.Level.World;
using Xunit.Abstractions;
using Vector = Avalonia.Vector;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "a 2D map of the island inside the Spawns page, zoom with the mouse wheel, pan, where I select an area: a whole
/// sector, or any area I draw/resize myself". <see cref="SpawnMap"/> is that map; these drive it with the headless mouse.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class SpawnMapTests(ITestOutputHelper output)
{
    [AvaloniaFact]
    public void WheelZoomKeepsTheWorldPointUnderTheCursor()
    {
        var (window, map) = Show(SpawnMapTool.Pan);
        var at = new Point(310, 220);
        var under = map.ScreenToWorld(at);
        var scale = map.ViewScale;

        window.MouseMove(at);
        window.MouseWheel(at, new Vector(0, 3));
        Assert.True(map.ViewScale > scale * 1.9, $"zoomed in: {scale} -> {map.ViewScale}");
        Assert.InRange(Distance(map.WorldToScreen(under.X, under.Y), at), 0, 1);

        window.MouseWheel(at, new Vector(0, -1));
        Assert.InRange(Distance(map.WorldToScreen(under.X, under.Y), at), 0, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void DragPans()
    {
        var (window, map) = Show(SpawnMapTool.Pan);
        var (from, to) = (new Point(400, 300), new Point(460, 340));
        var grabbed = map.ScreenToWorld(from);

        Drag(window, from, to);
        Assert.InRange(Distance(map.WorldToScreen(grabbed.X, grabbed.Y), to), 0, 1);
        Assert.Null(map.Area);

        // The middle button pans with any tool.
        map.Tool = SpawnMapTool.Rectangle;
        window.MouseDown(to, MouseButton.Middle);
        window.MouseMove(from);
        window.MouseUp(from, MouseButton.Middle);
        Assert.InRange(Distance(map.WorldToScreen(grabbed.X, grabbed.Y), from), 0, 1);
        Assert.Null(map.Area);
        window.Close();
    }

    [AvaloniaFact]
    public void SectorClickSelectsThatCell()
    {
        var (window, map) = Show(SpawnMapTool.Sector);
        var cell = MapCell.Parse("B_3");
        var (minX, minY, maxX, maxY) = IslandMap.CellRect(cell);
        var at = map.WorldToScreen((minX + maxX) / 2, (minY + maxY) / 2);

        Click(window, at);
        Assert.Equal(SpawnArea.Cell(cell), map.Area);

        // A drag pans instead of selecting; a click off the island keeps the selection.
        Drag(window, map.WorldToScreen(minX + 1000, minY + 1000) + new Vector(0, -200), at);
        Assert.Equal(SpawnArea.Cell(cell), map.Area);
        Click(window, new Point(2, 2));
        Assert.Equal(SpawnArea.Cell(cell), map.Area);
        window.Close();
    }

    [AvaloniaFact]
    public void TheIslandIsShownAsTheGameShowsIt()
    {
        // Discord 2026-10-17, "the map is inverted": the game's spawn screen shows its map texture turned 180 degrees, D4 at
        // the top left and Z0 at the bottom right; +X runs to the left and +Y up.
        var (window, map) = Show(SpawnMapTool.Sector);
        var topLeft = map.WorldToScreen(IslandMap.MinCm + IslandMap.SizeCm, IslandMap.MinCm + IslandMap.SizeCm);
        var bottomRight = map.WorldToScreen(IslandMap.MinCm, IslandMap.MinCm);
        Assert.True(topLeft.X < bottomRight.X && topLeft.Y < bottomRight.Y, $"{topLeft} .. {bottomRight}");
        var fifth = (bottomRight.X - topLeft.X) / 5;

        Click(window, topLeft + new Vector(fifth / 2, fifth / 2));
        Assert.Equal(SpawnArea.Cell(MapCell.Parse("D_4")), map.Area);
        Click(window, topLeft + new Vector(fifth * 1.5, fifth / 2));
        Assert.Equal(SpawnArea.Cell(MapCell.Parse("D_3")), map.Area);
        Click(window, topLeft + new Vector(fifth / 2, fifth * 1.5));
        Assert.Equal(SpawnArea.Cell(MapCell.Parse("C_4")), map.Area);
        Click(window, bottomRight - new Vector(fifth / 2, fifth / 2));
        Assert.Equal(SpawnArea.Cell(MapCell.Parse("Z_0")), map.Area);

        var origin = map.WorldToScreen(0f, 0f);
        Assert.True(map.WorldToScreen(10_000f, 0f).X < origin.X, "+X is to the left");
        Assert.True(map.WorldToScreen(0f, 10_000f).Y < origin.Y, "+Y is up");
        window.Close();
    }

    [AvaloniaFact]
    public void RectangleDrawThenHandleDragResizes()
    {
        var (window, map) = Show(SpawnMapTool.Rectangle);
        var (a, b) = (new Point(300, 200), new Point(420, 290));
        Drag(window, a, b);
        var drawn = map.Area!;
        Assert.Equal(SpawnAreaShape.Rectangle, drawn.Shape);
        AssertBox(map, a, b, drawn);

        // The bottom-right handle moves that corner only.
        var c = new Point(480, 330);
        Drag(window, b, c);
        AssertBox(map, a, c, map.Area!);

        // The right edge's handle moves the right edge only.
        Drag(window, new Point(480, 265), new Point(520, 300));
        AssertBox(map, a, new Point(520, 330), map.Area!);

        // A drag inside moves it whole.
        Drag(window, new Point(350, 240), new Point(330, 230));
        AssertBox(map, new Point(280, 190), new Point(500, 320), map.Area!);
        Assert.False(map.IsDrawing);
        window.Close();
    }

    [AvaloniaFact]
    public void PolygonClosesOnDoubleClick()
    {
        var (window, map) = Show(SpawnMapTool.Polygon);
        Point[] corners = [new(300, 200), new(450, 210), new(470, 330), new(320, 340)];
        foreach (var p in corners)
        {
            Click(window, p);
        }

        Assert.True(map.IsDrawing);
        Assert.Null(map.Area);
        Click(window, corners[^1]); // the second click of a double click on the last corner
        Assert.False(map.IsDrawing);
        var polygon = map.Area!;
        Assert.Equal(SpawnAreaShape.Polygon, polygon.Shape);
        Assert.Equal(corners.Select(map.ScreenToWorld), polygon.Points);

        // Its corners can be dragged.
        Drag(window, corners[1], new Point(500, 180));
        Assert.Equal(map.ScreenToWorld(new Point(500, 180)), map.Area!.Points[1]);
        Assert.Equal(polygon.Points[0], map.Area.Points[0]);
        window.Close();
    }

    [AvaloniaFact]
    public void EscapeCancelsTheShapeInProgress()
    {
        var (window, map) = Show(SpawnMapTool.Polygon);
        Click(window, new Point(300, 200));
        Click(window, new Point(400, 220));
        Assert.True(map.IsDrawing);
        Key(window, PhysicalKey.Escape);
        Assert.False(map.IsDrawing);
        Assert.Null(map.Area);

        // A rectangle being drawn: Esc drops it and the old selection stays.
        map.Tool = SpawnMapTool.Rectangle;
        var kept = SpawnArea.Cell(MapCell.Parse("A_1"));
        map.Area = kept;
        var (minX, minY, maxX, maxY) = IslandMap.CellRect(MapCell.Parse("D_4"));
        var (from, to) = (map.WorldToScreen(minX + 20000, minY + 20000), map.WorldToScreen(maxX - 20000, maxY - 20000));
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(to);
        Assert.True(map.IsDrawing);
        Key(window, PhysicalKey.Escape);
        window.MouseUp(to, MouseButton.Left);
        Assert.False(map.IsDrawing);
        Assert.Equal(kept, map.Area);
        window.Close();
    }

    [AvaloniaFact]
    public void PlaceClickRaisesTheCommandWithTheWorldPoint()
    {
        var (window, map) = Show(SpawnMapTool.Place);
        Vector2? placed = null;
        map.PlaceCommand = new RelayCommand<Vector2>(p => placed = p);
        var at = new Point(380, 260);
        Click(window, at);
        Assert.Equal(map.ScreenToWorld(at), placed);
        window.Close();
    }

    [AvaloniaFact]
    public void ThirtyThousandMarkersRenderUnderSixteenMilliseconds()
    {
        var (window, map) = Show(SpawnMapTool.Pan);
        var random = new Random(5);
        var markers = new List<SpawnMapMarker>(30000);
        for (var i = 0; i < 30000; i++)
        {
            var (x, y) = (IslandMap.MinCm + (random.NextSingle() * IslandMap.SizeCm), IslandMap.MinCm + (random.NextSingle() * IslandMap.SizeCm));
            markers.Add(i % 100 == 0
                ? new SpawnMapMarker(i, x, y, SpawnMapLayer.Zones, 5000 + (random.NextSingle() * 40000), 5000 + (random.NextSingle() * 20000), random.NextSingle() * 360)
                : new SpawnMapMarker(i, x, y, (SpawnMapLayer)(i % 5)));
        }

        map.Picture = new WriteableBitmap(new PixelSize(2048, 2048), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        map.Markers = markers;
        map.HighlightedIds = markers.Where(m => m.Id % 7 == 0).Select(m => m.Id).ToHashSet();
        using var target = new RenderTargetBitmap(new PixelSize(800, 600));

        var whole = Time(map, target);
        Assert.True(map.DrewCounts, "the whole island shows counts");
        Assert.InRange(map.VisibleMarkerCount, 30000 - 300, 30000); // zones big enough on screen are ellipses, not counted

        map.ZoomAt(new Point(400, 300), 6);
        var close = Time(map, target);
        Assert.False(map.DrewCounts);
        Assert.InRange(map.VisibleMarkerCount, 100, SpawnMap.GlyphLimit);

        // Only the UI thread's part is held to the frame budget. The Skia draw is the render thread's, on the CPU here (no
        // GPU headless), and depends on the machine: it is logged, not asserted.
        var summary = $"30,000 places, 800 x 600: whole island (counts) built {whole.Recorded:F2} ms, drawn {whole.Drawn:F2} ms; zoomed x6 ({map.VisibleMarkerCount} glyphs) built {close.Recorded:F2} ms, drawn {close.Drawn:F2} ms";
        output.WriteLine(summary);
        if (!Ci.On) Assert.True(whole.Recorded < 16 && close.Recorded < 16, $"the UI thread builds a frame in under 16 ms: {summary}");
        window.Close();
    }

    // Best of 7 after a warm-up, the code's own cost (the other runs add whatever else the machine is doing): the frame as
    // the UI thread builds it (recorded; the render thread draws it; a recording takes no bitmaps, so without the picture,
    // one call), and the whole frame drawn by Skia on the CPU.
    private static (double Recorded, double Drawn) Time(SpawnMap map, RenderTargetBitmap target)
    {
        target.Render(map);
        var (recorded, drawn) = (new List<double>(), new List<double>());
        for (var i = 0; i < 7; i++)
        {
            var picture = map.Picture;
            map.Picture = null;
            var clock = Stopwatch.StartNew();
            using (var context = new Avalonia.Media.DrawingGroup().Open())
            {
                map.Render(context);
            }

            recorded.Add(clock.Elapsed.TotalMilliseconds);
            map.Picture = picture;
            clock.Restart();
            target.Render(map);
            drawn.Add(clock.Elapsed.TotalMilliseconds);
        }

        return (recorded.Min(), drawn.Min());
    }

    private static (Window Window, SpawnMap Map) Show(SpawnMapTool tool)
    {
        var map = new SpawnMap { Tool = tool };
        var window = new Window { Width = 800, Height = 600, Content = map };
        window.Show();
        HeadlessUi.Pump();
        map.Focus();
        return (window, map);
    }

    private static void AssertBox(SpawnMap map, Point a, Point b, SpawnArea area)
    {
        var (wa, wb) = (map.ScreenToWorld(a), map.ScreenToWorld(b));
        Assert.Equal(MathF.Min(wa.X, wb.X), area.MinX, 2f);
        Assert.Equal(MathF.Min(wa.Y, wb.Y), area.MinY, 2f);
        Assert.Equal(MathF.Max(wa.X, wb.X), area.MaxX, 2f);
        Assert.Equal(MathF.Max(wa.Y, wb.Y), area.MaxY, 2f);
    }

    private static void Click(Window window, Point at)
    {
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
    }

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        window.MouseMove(to);
        window.MouseUp(to, MouseButton.Left);
    }

    private static void Key(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
