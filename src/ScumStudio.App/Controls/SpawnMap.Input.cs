using System.Numerics;
using Avalonia;
using Avalonia.Input;
using ScumStudio.Level.Spawns;
using ScumStudio.Level.World;

namespace ScumStudio.App.Controls;

// Mouse and keys: the wheel zooms about the cursor, a drag pans (any tool, or the middle/right button), and the tools pick
// an area. A shape being drawn, resized or moved is a draft until the button is released; Esc drops it.
public sealed partial class SpawnMap
{
    // A press that moves less than this is a click.
    private const double ClickSlop = 4;

    // How close (screen px) a handle or corner must be to be grabbed.
    private const double GrabRadius = 7;

    private DragKind _drag;
    private Point _pressAt;
    private Point _lastAt;
    private bool _moved;
    private bool _swallowRelease;
    private int _handle;
    private SpawnArea? _draft;
    private SpawnArea? _dragStart;
    private Vector2? _hover;
    private readonly List<Vector2> _polygon = [];

    private enum DragKind
    {
        None,
        Pan,
        Draw,
        Resize,
        Move,
        Corner,
        Place,
        Turn,
    }

    /// <summary>True while a shape is being drawn or changed (Esc drops it).</summary>
    public bool IsDrawing => _draft is not null || _polygon.Count > 0;

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var at = e.GetPosition(this);
        var button = e.GetCurrentPoint(this).Properties;
        (_pressAt, _lastAt, _moved, _swallowRelease) = (at, at, false, false);
        e.Pointer.Capture(this);
        e.Handled = true;
        if (!button.IsLeftButtonPressed)
        {
            _drag = button.IsMiddleButtonPressed || button.IsRightButtonPressed ? DragKind.Pan : DragKind.None;
            return;
        }

        _drag = DragKind.Pan;
        if (Tool == SpawnMapTool.Select && OnTurnHandle(at) is { } turning)
        {
            // The picked place's arrow: a drag turns it about its centre.
            CommitKeys();
            (_drag, _keysFor, _nudge, _turn) = (DragKind.Turn, turning, default, 0f);
        }
        else if (Tool == SpawnMapTool.Select && PlaceAt(at) is { } picked)
        {
            // A press on a place picks it; a drag from there moves it (where it is grabbed stays under the cursor).
            CommitKeys();
            SelectedId = _ids[picked];
            (_drag, _grabbed, _grabFrom) = (DragKind.Place, _ids[picked], MarkerWorld(picked));
        }
        else if (Tool == SpawnMapTool.Rectangle)
        {
            if (Area is { Shape: not SpawnAreaShape.Polygon } box && HandleAt(box, at) is { } handle)
            {
                (_drag, _handle, _dragStart) = (DragKind.Resize, handle, box);
            }
            else
            {
                _drag = Area is { Shape: not SpawnAreaShape.Polygon } inside && inside.Contains(ScreenToWorld(at).X, ScreenToWorld(at).Y)
                    ? DragKind.Move
                    : DragKind.Draw;
                _dragStart = Area;
            }
        }
        else if (Tool == SpawnMapTool.Polygon)
        {
            if (e.ClickCount >= 2 && _polygon.Count >= 3)
            {
                ClosePolygon();
                (_drag, _swallowRelease) = (DragKind.None, true);
            }
            else if (_polygon.Count == 0 && Area is { Shape: SpawnAreaShape.Polygon } polygon && CornerAt(polygon, at) is { } corner)
            {
                (_drag, _handle, _dragStart) = (DragKind.Corner, corner, polygon);
            }
        }
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        ShowCursor(at);
        if (_drag == DragKind.None)
        {
            if (_polygon.Count > 0)
            {
                _hover = ScreenToWorld(at);
                InvalidateVisual();
            }

            return;
        }

        if (!_moved && Distance(at, _pressAt) < ClickSlop)
        {
            return;
        }

        _moved = true;
        switch (_drag)
        {
            case DragKind.Pan:
                PanBy(at - _lastAt);
                break;
            case DragKind.Draw:
                var (a, b) = (ScreenToWorld(_pressAt), ScreenToWorld(at));
                _draft = SpawnArea.Rectangle(a.X, a.Y, b.X, b.Y);
                break;
            case DragKind.Resize:
                _draft = Resized(_dragStart!, _handle, at);
                break;
            case DragKind.Move:
                var shift = ScreenToWorld(at) - ScreenToWorld(_pressAt);
                _draft = SpawnArea.Rectangle(_dragStart!.MinX + shift.X, _dragStart.MinY + shift.Y, _dragStart.MaxX + shift.X, _dragStart.MaxY + shift.Y);
                break;
            case DragKind.Corner:
                var corners = _dragStart!.Points.ToArray();
                corners[_handle] = ScreenToWorld(at);
                _draft = SpawnArea.Polygon(corners);
                break;
            case DragKind.Place:
                Preview(_grabbed, _grabFrom + (ScreenToWorld(at) - ScreenToWorld(_pressAt)));
                break;
            case DragKind.Turn:
                TurnTowards(at, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                break;
        }

        _lastAt = at;
        e.Handled = true;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var (drag, click, at) = (_drag, !_moved, e.GetPosition(this));
        _drag = DragKind.None;
        e.Pointer.Capture(null);
        if (_swallowRelease || e.InitialPressMouseButton != MouseButton.Left)
        {
            _swallowRelease = false;
            return;
        }

        e.Handled = true;
        if (drag == DragKind.Pan && click)
        {
            Click(ScreenToWorld(at));
        }
        else if (drag == DragKind.Place && !click && _pending is { } moved)
        {
            Commit(moved.Id, moved.To);
        }
        else if (drag == DragKind.Turn)
        {
            CommitKeys(); // the turn, once
        }
        else if (drag is DragKind.Draw or DragKind.Resize or DragKind.Move or DragKind.Corner && _draft is { } draft)
        {
            Area = draft;
        }

        _draft = null;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag != DragKind.None)
        {
            if (_drag == DragKind.Place)
            {
                _pending = null; // the drag was taken away: the place stays where it was
            }
            else if (_drag == DragKind.Turn)
            {
                (_keysFor, _turn) = (null, 0f);
            }

            (_drag, _draft) = (DragKind.None, null);
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        CursorText = string.Empty;
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(e.GetPosition(this), Math.Pow(1.25, e.Delta.Y));
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && IsDrawing)
        {
            CancelShape();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _polygon.Count >= 3)
        {
            ClosePolygon();
            e.Handled = true;
        }
        else if (PickKeyDown(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (PickKeyUp(e.Key))
        {
            e.Handled = true;
        }
    }

    private void Click(Vector2 world)
    {
        switch (Tool)
        {
            case SpawnMapTool.Sector when IslandMap.CellAt(world.X, world.Y) is { } cell:
                Area = SpawnArea.Cell(cell);
                break;
            case SpawnMapTool.Polygon when _polygon.Count == 0 || Distance(WorldToScreen(_polygon[^1].X, _polygon[^1].Y), WorldToScreen(world.X, world.Y)) >= ClickSlop:
                _polygon.Add(world);
                InvalidateVisual();
                break;
            case SpawnMapTool.Place when PlaceCommand is { } command && command.CanExecute(world):
                command.Execute(world);
                break;
            case SpawnMapTool.Select:
                CommitKeys();
                SelectedId = null; // a click beside every place lets go of the picked one
                break;
        }
    }

    private void ClosePolygon()
    {
        Area = SpawnArea.Polygon(_polygon.ToArray());
        _polygon.Clear();
        _hover = null;
        InvalidateVisual();
    }

    private void CancelShape()
    {
        if (_drag is not (DragKind.None or DragKind.Pan))
        {
            _drag = DragKind.None;
        }

        _draft = null;
        _polygon.Clear();
        _hover = null;
        InvalidateVisual();
    }

    // The 8 handles of a box in screen space: corners and edge middles, clockwise from the top left.
    private Point[] Handles(SpawnArea box)
    {
        var (a, b) = (WorldToScreen(box.MinX, box.MinY), WorldToScreen(box.MaxX, box.MaxY));
        var (l, t, r, d) = (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        var (cx, cy) = ((l + r) / 2, (t + d) / 2);
        return [new(l, t), new(cx, t), new(r, t), new(r, cy), new(r, d), new(cx, d), new(l, d), new(l, cy)];
    }

    private int? HandleAt(SpawnArea box, Point at)
    {
        var handles = Handles(box);
        for (var i = 0; i < handles.Length; i++)
        {
            if (Distance(handles[i], at) <= GrabRadius)
            {
                return i;
            }
        }

        return null;
    }

    private int? CornerAt(SpawnArea polygon, Point at)
    {
        for (var i = 0; i < polygon.Points.Count; i++)
        {
            if (Distance(WorldToScreen(polygon.Points[i].X, polygon.Points[i].Y), at) <= GrabRadius)
            {
                return i;
            }
        }

        return null;
    }

    // The box with the edges of handle `handle` dragged to `at` (screen space, so it follows the picture's axes).
    private SpawnArea Resized(SpawnArea box, int handle, Point at)
    {
        var h = Handles(box);
        var (l, t, r, d) = (h[0].X, h[0].Y, h[4].X, h[4].Y);
        if (handle is 0 or 6 or 7)
        {
            l = at.X;
        }

        if (handle is 2 or 3 or 4)
        {
            r = at.X;
        }

        if (handle is 0 or 1 or 2)
        {
            t = at.Y;
        }

        if (handle is 4 or 5 or 6)
        {
            d = at.Y;
        }

        var (a, b) = (ScreenToWorld(new Point(l, t)), ScreenToWorld(new Point(r, d)));
        return SpawnArea.Rectangle(a.X, a.Y, b.X, b.Y);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
