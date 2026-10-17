using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Controls;
using ScumStudio.Level.Spawns;
using ScumStudio.Level.World;
using Vector = Avalonia.Vector;

namespace ScumStudio.Tests.App;

/// <summary>
/// Owner: "click a single vehicle spawn point on the 2D map to select it, drag it to move it, rotate it, zoom far enough to
/// place a point within a metre, show coordinates, keyboard nudge". The <see cref="SpawnMap"/>'s Select point tool, driven
/// with the headless mouse and keys: the control picks and previews, the page gets one move or turn when a drag or the
/// keys end.
/// </summary>
public sealed class SpawnMapPickTests
{
    // Three vehicle points in B_2 (100 m apart) and a 50 x 30 m zone around the third, turned 30 degrees.
    private static readonly SpawnMapMarker[] Markers =
    [
        new(0, 0f, 0f, SpawnMapLayer.Vehicles, Yaw: 90f),
        new(1, 10_000f, 0f, SpawnMapLayer.Vehicles),
        new(2, 20_000f, 0f, SpawnMapLayer.Vehicles),
        new(3, 20_000f, 30_000f, SpawnMapLayer.Zones, 5000f, 3000f, 30f),
    ];

    [AvaloniaFact]
    public void AClickPicksThePlaceUnderItAndAClickBesideLetsGo()
    {
        var (window, map, _) = Show();

        Click(window, map.ScreenOf(1)!.Value + new Vector(4, -3)); // a few pixels off its centre still picks it
        Assert.Equal(1, map.SelectedId);

        Click(window, map.ScreenOf(1)!.Value + new Vector(0, 60));
        Assert.Null(map.SelectedId);

        // Inside the zone's ellipse, away from any point: the zone.
        var zone = map.ScreenOf(3)!.Value;
        Click(window, zone + new Vector(20, 10));
        Assert.Equal(3, map.SelectedId);
        window.Close();
    }

    [AvaloniaFact]
    public void ADragMovesThePickedPlaceOnceAndOtherToolsDoNotPick()
    {
        var (window, map, page) = Show();
        var from = map.ScreenOf(0)!.Value;
        var to = from + new Vector(80, 40);

        Drag(window, from, to);

        Assert.Equal(0, map.SelectedId);
        var move = Assert.Single(page.Moves);
        Assert.Equal(0, move.Id);
        var expected = map.ScreenToWorld(to);
        Assert.Equal(expected.X, move.To.X, 1f);
        Assert.Equal(expected.Y, move.To.Y, 1f);

        // A drag from beside every place pans the map and moves nothing; the Sector tool never picks a place.
        Drag(window, from + new Vector(0, 150), from + new Vector(30, 180));
        Assert.Single(page.Moves);
        map.SelectedId = null;
        map.Tool = SpawnMapTool.Sector;
        Click(window, map.ScreenOf(2)!.Value);
        Assert.Null(map.SelectedId);
        window.Close();
    }

    [AvaloniaFact]
    public void ArrowKeysNudgeAndQETurnOnceTheKeysAreLetGo()
    {
        var (window, map, page) = Show();
        map.SelectedId = 1;

        // Held: two presses, one release, one move of 2 m to the right (the picture's right is +X).
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Empty(page.Moves);
        window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(new Vector2(10_200f, 0f), Assert.Single(page.Moves).To);

        // Shift 10 m, Ctrl 10 cm; each starts from where the last one put it while the page records it.
        Key(window, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
        Assert.Equal(new Vector2(10_200f, 1000f), page.Moves[^1].To);
        Key(window, PhysicalKey.ArrowUp, RawInputModifiers.Control);
        Assert.Equal(new Vector2(10_200f, 990f), page.Moves[^1].To);

        Key(window, PhysicalKey.Q, RawInputModifiers.None);
        Key(window, PhysicalKey.E, RawInputModifiers.Shift);
        Assert.Equal([new SpawnMapTurn(1, -15f), new SpawnMapTurn(1, 1f)], page.Turns);

        Key(window, PhysicalKey.Delete, RawInputModifiers.None);
        Assert.Equal([1], page.Deletes);
        Key(window, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Null(map.SelectedId);
        window.Close();
    }

    [AvaloniaFact]
    public void DraggingThePickedPlacesArrowTurnsItOnce()
    {
        var (window, map, page) = Show();
        map.SelectedId = 0; // faces 90 degrees: its arrow points down the map (+Y)
        var centre = map.ScreenOf(0)!.Value;
        var tip = map.TurnHandle!.Value;
        Assert.InRange(tip.Y - centre.Y, 20, 24);
        Assert.InRange(Math.Abs(tip.X - centre.X), 0, 0.01);

        // Dragged round to the right of it: it faces 0 degrees, a turn of -90 raised once on release; nothing moved.
        window.MouseMove(tip);
        window.MouseDown(tip, MouseButton.Left);
        window.MouseMove(centre + new Vector(30, 30));
        window.MouseMove(centre + new Vector(40, 0.2));
        Assert.Empty(page.Turns);
        window.MouseUp(centre + new Vector(40, 0.2), MouseButton.Left);

        Assert.Equal([new SpawnMapTurn(0, -90f)], page.Turns);
        Assert.Empty(page.Moves);
        Assert.Equal(0, map.SelectedId);

        // While the page records it, the arrow stays where it was turned (it does not jump back for a moment).
        var arrow = map.TurnHandle!.Value - centre;
        Assert.InRange(arrow.X, 20, 24);
        Assert.InRange(Math.Abs(arrow.Y), 0, 0.01);
        window.Close();
    }

    [AvaloniaFact]
    public void TheMapZoomsInToTwoCentimetresAPixelAndShowsTheCursorsCoordinates()
    {
        var (window, map, _) = Show();
        var at = map.ScreenOf(0)!.Value;
        for (var i = 0; i < 40; i++)
        {
            window.MouseWheel(at, new Vector(0, 3));
        }

        // Before: the closest zoom was 25 cm a pixel (4 px per metre). Now 50 px per metre: a point lands within 2 cm.
        Assert.Equal(100.0 / SpawnMap.ClosestCmPerPixel, map.PixelsPerMetre, 3);

        window.MouseMove(at + new Vector(50, 0)); // 1 m to the right of the point at 0, 0
        Assert.Contains(IslandMap.CellAt(100f, 0f)!.Value.ToString(), map.CursorText, StringComparison.Ordinal);
        Assert.Contains((1.0).ToString("0.00", System.Globalization.CultureInfo.CurrentCulture), map.CursorText, StringComparison.Ordinal);
        window.Close();
    }

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.9, 1.0)]
    [InlineData(4.99, 2.0)]
    [InlineData(73.0, 50.0)]
    [InlineData(1234.0, 1000.0)]
    [InlineData(0.07, 0.05)]
    public void TheScaleBarTakesARoundLength(double most, double expected) =>
        Assert.Equal(expected, SpawnMap.NiceLength(most), 6);

    private static (Window Window, SpawnMap Map, Recorder Page) Show()
    {
        var page = new Recorder();
        var map = new SpawnMap
        {
            Tool = SpawnMapTool.Select,
            Markers = Markers,
            MoveCommand = new RelayCommand<SpawnMapMove>(page.Moves.Add),
            TurnCommand = new RelayCommand<SpawnMapTurn>(page.Turns.Add),
            DeleteCommand = new RelayCommand<int>(page.Deletes.Add),
        };
        var window = new Window { Width = 800, Height = 600, Content = map };
        window.Show();
        HeadlessUi.Pump();
        map.ZoomTo(SpawnArea.Rectangle(-10_000f, -10_000f, 30_000f, 40_000f)); // about a pixel per metre: the places 100 px apart
        HeadlessUi.Pump();
        map.Focus();
        return (window, map, page);
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

    private static void Key(Window window, PhysicalKey key, RawInputModifiers modifiers)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
    }

    private sealed class Recorder
    {
        public List<SpawnMapMove> Moves { get; } = [];

        public List<SpawnMapTurn> Turns { get; } = [];

        public List<int> Deletes { get; } = [];
    }
}
