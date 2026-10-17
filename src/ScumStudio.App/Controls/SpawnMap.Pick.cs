using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ScumStudio.App.Localization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.World;
using Vector = Avalonia.Vector;

namespace ScumStudio.App.Controls;

// One spawn place picked by hand (owner: "click a single vehicle spawn point, drag it, rotate it, nudge it with the keys,
// zoom far enough to place it within a metre, show coordinates"): the Select tool picks the place under a click, a drag
// from it moves it, the arrow keys nudge it and Q / E turn it. The control only previews: the move or turn is raised as a
// command when the drag or the keys end, and the page records it (one undo step) and sends new markers. Close in, a metre
// grid, a scale bar and the cursor's coordinates help to place a point exactly.
public sealed partial class SpawnMap
{
    /// <summary>The world point under the mouse ("B_2 · X 1234.56 m · Y -567.80 m"), empty when it is off the map.</summary>
    public static readonly DirectProperty<SpawnMap, string> CursorTextProperty =
        AvaloniaProperty.RegisterDirect<SpawnMap, string>(nameof(CursorText), o => o.CursorText);

    /// <summary>An arrow key nudges the picked place this far (cm); with Ctrl <see cref="FineNudgeCm"/>, with Shift <see cref="CoarseNudgeCm"/>.</summary>
    public const float NudgeCm = 100f;

    /// <summary>Ctrl + arrow: 10 cm.</summary>
    public const float FineNudgeCm = 10f;

    /// <summary>Shift + arrow: 10 m.</summary>
    public const float CoarseNudgeCm = 1000f;

    /// <summary>Q / E turn the picked place by this much (degrees); with Shift <see cref="FineTurnDegrees"/>.</summary>
    public const float TurnDegrees = 15f;

    /// <summary>Shift + Q / E: 1 degree.</summary>
    public const float FineTurnDegrees = 1f;

    // How close (screen px) to a place's centre a click must be to pick it.
    private const double PickRadius = 10;

    // How long (ms) a released move is drawn where it went while the page records it and sends the new markers.
    private const int PendingMs = 3000;

    private readonly HashSet<Key> _held = [];
    private (int Id, Vector2 To)? _pending;
    private (int Id, float Degrees, long Until)? _turned;
    private long _pendingUntil = long.MaxValue;
    private int _grabbed;
    private Vector2 _grabFrom;
    private int? _keysFor;
    private Vector2 _nudge;
    private float _turn;
    private string _cursorText = string.Empty;

    /// <inheritdoc cref="CursorTextProperty" />
    public string CursorText
    {
        get => _cursorText;
        private set => SetAndRaise(CursorTextProperty, ref _cursorText, value);
    }

    /// <summary>The screen point (control pixels) of the place <paramref name="id"/> as drawn, or null when it is not on the map.</summary>
    public Point? ScreenOf(int id) =>
        Markers is { } markers && markers.Count == _ids.Length && _indexOfId.TryGetValue(id, out var index)
            ? WorldToScreen(markers[index].X, markers[index].Y)
            : null;

    /// <summary>Centres the view on the world point (<paramref name="x"/>, <paramref name="y"/>) cm, zooming in to at least <paramref name="pixelsPerMetre"/>.</summary>
    public void CenterOn(float x, float y, double pixelsPerMetre)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        _scale = ClampScale(Math.Max(_scale, pixelsPerMetre * IslandMap.SizeCm / 100.0));
        var (u, v) = IslandMap.ToMap(x, y);
        _offset = new Point((size.Width / 2) - (u * _scale), (size.Height / 2) - (v * _scale));
        _userMoved = true;
        InvalidateVisual();
    }

    private void ShowCursor(Point at)
    {
        var world = ScreenToWorld(at);
        var cell = IslandMap.CellAt(world.X, world.Y)?.ToString() ?? "-";
        CursorText = Loc.F("Spawns.Map.Cursor", cell, Metres(world.X), Metres(world.Y));
    }

    private static string Metres(float cm) => (cm / 100.0).ToString("0.00", CultureInfo.CurrentCulture);

    /// <summary>The index of the shown place under <paramref name="at"/>: the nearest glyph within reach, else the smallest zone or area around it.</summary>
    private int? PlaceAt(Point at)
    {
        if (Markers is not { } markers || markers.Count != _ids.Length)
        {
            return null;
        }

        var (mask, world) = ((int)ShownLayers, ScreenToWorld(at));
        var smallest = (float)(SmallestEllipse / _scale);
        const int zones = (int)SpawnMapLayer.Zones;
        (int? Index, double Distance) glyph = (null, PickRadius);
        (int? Index, float Reach) area = (null, float.MaxValue);
        for (var i = 0; i < _ids.Length; i++)
        {
            int layer = _layers[i];
            if ((mask & (1 << layer)) == 0)
            {
                continue;
            }

            var drawnAsEllipse = _reach[i] >= smallest;
            if (!DrewCounts && !(layer == zones && drawnAsEllipse))
            {
                var distance = Distance(new Point(_offset.X + (_u[i] * _scale), _offset.Y + (_v[i] * _scale)), at);
                if (distance <= glyph.Distance)
                {
                    glyph = (i, distance);
                }
            }

            if (drawnAsEllipse && _reach[i] < area.Reach && Inside(markers[i], world))
            {
                area = (i, _reach[i]);
            }
        }

        return glyph.Index ?? area.Index;
    }

    // Inside the marker's ellipse (turned by its yaw)?
    private static bool Inside(SpawnMapMarker marker, Vector2 world)
    {
        var turn = -marker.Yaw * MathF.PI / 180f;
        var (dx, dy) = (world.X - marker.X, world.Y - marker.Y);
        var (lx, ly) = ((dx * MathF.Cos(turn)) - (dy * MathF.Sin(turn)), (dx * MathF.Sin(turn)) + (dy * MathF.Cos(turn)));
        return ((lx / marker.RadiusX) * (lx / marker.RadiusX)) + ((ly / marker.RadiusY) * (ly / marker.RadiusY)) <= 1f;
    }

    /// <summary>Where the place at <paramref name="index"/> is drawn: its marker, or where it was just moved while that is recorded.</summary>
    private Vector2 MarkerWorld(int index)
    {
        var marker = Markers![index];
        return _pending is { } moved && moved.Id == marker.Id && PendingAlive ? moved.To : new Vector2(marker.X, marker.Y);
    }

    // Which way the place at <paramref name="index"/> is drawn facing: its marker, turned by a turn just raised while that is recorded.
    private float MarkerYaw(int index)
    {
        var marker = Markers![index];
        return _turned is { } turned && turned.Id == marker.Id && Environment.TickCount64 < turned.Until ? marker.Yaw + turned.Degrees : marker.Yaw;
    }

    private bool PendingAlive => _drag == DragKind.Place || Environment.TickCount64 < _pendingUntil;

    private void Preview(int id, Vector2 to)
    {
        (_pending, _pendingUntil) = ((id, to), long.MaxValue);
        InvalidateVisual();
    }

    /// <summary>The move ends: drawn where it went for a moment and raised; the page's new markers take over.</summary>
    private void Commit(int id, Vector2 to)
    {
        (_pending, _pendingUntil) = ((id, to), Environment.TickCount64 + PendingMs);
        DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromMilliseconds(PendingMs + 50));
        var move = new SpawnMapMove(id, to);
        if (MoveCommand is { } command && command.CanExecute(move))
        {
            command.Execute(move);
        }

        InvalidateVisual();
    }

    private void DropPickPreview()
    {
        _pending = null;
        _turned = null;
        (_keysFor, _nudge, _turn) = (null, default, 0f);
        _held.Clear();
        InvalidateVisual();
    }

    /// <summary>The tip of the picked place's arrow (control pixels), where a drag turns it; null when none is picked or shown.</summary>
    public Point? TurnHandle
    {
        get
        {
            if (SelectedId is not { } id || Markers is not { } markers || markers.Count != _ids.Length || !_indexOfId.TryGetValue(id, out var index)
                || ((int)ShownLayers & (1 << _layers[index])) == 0)
            {
                return null;
            }

            var world = MarkerWorld(index);
            var yaw = MarkerYaw(index) * Math.PI / 180;
            return WorldToScreen(world.X, world.Y) + (new Vector(Math.Cos(yaw), Math.Sin(yaw)) * 22);
        }
    }

    private int? OnTurnHandle(Point at) => TurnHandle is { } tip && Distance(tip, at) <= GrabRadius + 2 ? SelectedId : null;

    // A drag of the arrow: the place faces the cursor, in whole degrees (Shift: 15 degree steps).
    private void TurnTowards(Point at, bool coarse)
    {
        if (_keysFor is not { } id || !_indexOfId.TryGetValue(id, out var index))
        {
            return;
        }

        var world = MarkerWorld(index);
        var centre = WorldToScreen(world.X, world.Y);
        var step = coarse ? TurnDegrees : FineTurnDegrees;
        var facing = Math.Round(Math.Atan2(at.Y - centre.Y, at.X - centre.X) * 180 / Math.PI / step) * step;
        _turn = UeMath.NormalizeAxis((float)facing - MarkerYaw(index));
        InvalidateVisual();
    }

    // Keys on the picked place: arrows nudge, Q / E turn (both raised once the keys are let go), Delete removes, Esc lets go.
    private bool PickKeyDown(Key key, KeyModifiers modifiers)
    {
        if (SelectedId is not { } id || !_indexOfId.TryGetValue(id, out _))
        {
            return false;
        }

        var (fine, coarse) = (modifiers.HasFlag(KeyModifiers.Control), modifiers.HasFlag(KeyModifiers.Shift));
        switch (key)
        {
            case Key.Escape:
                CommitKeys();
                SelectedId = null;
                return true;
            case Key.Delete:
                CommitKeys();
                if (DeleteCommand is { } delete && delete.CanExecute(id))
                {
                    delete.Execute(id);
                }

                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                Hold(id, key);
                var step = fine ? FineNudgeCm : coarse ? CoarseNudgeCm : NudgeCm;

                // The picture's axes are the world's (IslandMap: U = X, V = Y): right is +X, down is +Y.
                _nudge += key switch
                {
                    Key.Left => new Vector2(-step, 0),
                    Key.Right => new Vector2(step, 0),
                    Key.Up => new Vector2(0, -step),
                    _ => new Vector2(0, step),
                };
                InvalidateVisual();
                return true;
            case Key.Q or Key.E:
                Hold(id, key);
                _turn += (key == Key.Q ? -1f : 1f) * (coarse ? FineTurnDegrees : TurnDegrees);
                InvalidateVisual();
                return true;
            default:
                return false;
        }
    }

    private bool PickKeyUp(Key key)
    {
        if (!_held.Remove(key))
        {
            return false;
        }

        if (_held.Count == 0)
        {
            CommitKeys();
        }

        return true;
    }

    private void Hold(int id, Key key)
    {
        if (_keysFor != id)
        {
            CommitKeys();
            (_keysFor, _nudge, _turn) = (id, default, 0f);
        }

        _held.Add(key);
    }

    /// <summary>Raises what the held keys did to the picked place: one move and one turn.</summary>
    private void CommitKeys()
    {
        _held.Clear();
        if (_keysFor is not { } id)
        {
            return;
        }

        var (nudge, turn) = (_nudge, _turn);
        (_keysFor, _nudge, _turn) = (null, default, 0f);
        if (nudge != Vector2.Zero && _indexOfId.TryGetValue(id, out var index))
        {
            Commit(id, MarkerWorld(index) + nudge);
        }

        var turned = new SpawnMapTurn(id, turn);
        if (turn != 0f && TurnCommand is { } command && command.CanExecute(turned))
        {
            var before = _turned is { } t && t.Id == id && Environment.TickCount64 < t.Until ? t.Degrees : 0f;
            _turned = (id, before + turn, Environment.TickCount64 + PendingMs); // drawn so while the page records it
            DispatcherTimer.RunOnce(InvalidateVisual, TimeSpan.FromMilliseconds(PendingMs + 50));
            command.Execute(turned);
        }
    }

    // The picked place on top of the others: a ring, an arrow the way it faces, its glyph larger and, while it is moved, a
    // dashed line from where it was and its coordinates.
    private void DrawPicked(DrawingContext context)
    {
        if (SelectedId is not { } id || Markers is not { } markers || markers.Count != _ids.Length || !_indexOfId.TryGetValue(id, out var index)
            || ((int)ShownLayers & (1 << _layers[index])) == 0)
        {
            return;
        }

        var marker = markers[index];
        var keys = _keysFor == id;
        var world = MarkerWorld(index) + (keys ? _nudge : Vector2.Zero);
        var (from, at) = (WorldToScreen(marker.X, marker.Y), WorldToScreen(world.X, world.Y));
        var accent = Token("AccentBrush");
        var yaw = (MarkerYaw(index) + (keys ? _turn : 0f)) * Math.PI / 180;
        var moving = Distance(from, at) > 0.5;
        if (moving)
        {
            context.DrawLine(new Pen(accent, 1.5, new DashStyle([4, 3], 0)), from, at);
            context.DrawEllipse(null, new Pen(accent, 1), from, 5, 5);
        }

        if (marker.RadiusX > 0 && marker.RadiusY > 0 && (moving || keys))
        {
            var (rx, ry) = (marker.RadiusX * _scale / IslandMap.SizeCm, marker.RadiusY * _scale / IslandMap.SizeCm);
            using (context.PushTransform(Matrix.CreateRotation(yaw) * Matrix.CreateTranslation(at.X, at.Y)))
            {
                context.DrawEllipse(null, new Pen(accent, 2, new DashStyle([6, 4], 0)), default, rx, ry);
            }
        }

        context.DrawEllipse(Translucent(accent, 0.22), new Pen(accent, 2.5), at, 13, 13);
        var direction = new Vector(Math.Cos(yaw), Math.Sin(yaw));
        var tip = at + (direction * 26);
        var arrow = new Pen(accent, 2.5, lineCap: PenLineCap.Round);
        context.DrawLine(arrow, at + (direction * 13), tip);
        foreach (var side in new[] { 2.6, -2.6 })
        {
            var back = new Vector(Math.Cos(yaw + side), Math.Sin(yaw + side));
            context.DrawLine(arrow, tip, tip + (back * 7));
        }

        DrawGlyphs(context, new Pen(Token("ViewportBrush"), 1), [(at, (SpawnMapLayer)_layers[index], id)], 1.3);
        if (moving)
        {
            var label = new FormattedText(Loc.F("Spawns.Map.Moving", Metres(world.X), Metres(world.Y)), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, Token("TextBrush"));
            var chipAt = new Point(at.X + 18, at.Y + 14);
            context.DrawRectangle(Token("ViewportChromeBrush"), null, new Rect(chipAt.X - 4, chipAt.Y - 2, label.Width + 8, label.Height + 4), 3, 3);
            context.DrawText(label, chipAt);
        }
    }

    // Close in, a line every metre (every 10 m a little further out), so a point can be put within a metre.
    private void DrawFineGrid(DrawingContext context, Rect bounds)
    {
        var perMetre = PixelsPerMetre;
        var stepCm = perMetre >= 16 ? 100.0 : perMetre * 10 >= 16 ? 1000.0 : 0;
        if (stepCm == 0)
        {
            return;
        }

        var (a, b) = (ScreenToWorld(default), ScreenToWorld(new Point(bounds.Width, bounds.Height)));
        var pen = new Pen(Translucent(Token("TextBrush"), 0.14), 1);
        for (var x = Math.Ceiling(a.X / stepCm) * stepCm; x <= b.X; x += stepCm)
        {
            var sx = WorldToScreen((float)x, a.Y).X;
            context.DrawLine(pen, new Point(sx, 0), new Point(sx, bounds.Height));
        }

        for (var y = Math.Ceiling(a.Y / stepCm) * stepCm; y <= b.Y; y += stepCm)
        {
            var sy = WorldToScreen(a.X, (float)y).Y;
            context.DrawLine(pen, new Point(0, sy), new Point(bounds.Width, sy));
        }
    }

    // A scale bar in the bottom right corner: the roundest length (1, 2 or 5 times a power of ten metres) near 100 px.
    private void DrawReadout(DrawingContext context, Rect bounds)
    {
        var perMetre = PixelsPerMetre;
        if (perMetre <= 0 || bounds.Width < 160)
        {
            return;
        }

        var metres = NiceLength(100 / perMetre);
        var length = metres * perMetre;
        var text = Token("TextBrush");
        var label = new FormattedText($"{metres.ToString("0.##", CultureInfo.CurrentCulture)} {Loc.T("Spawns.Unit.Metres")}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 11, text);
        var (right, bottom) = (bounds.Width - 14, bounds.Height - 10);
        var left = right - length;
        context.DrawRectangle(Token("ViewportChromeBrush"), null, new Rect(left - 8, bottom - label.Height - 12, length + 16, label.Height + 18), 3, 3);
        var pen = new Pen(text, 2);
        context.DrawLine(pen, new Point(left, bottom - 4), new Point(right, bottom - 4));
        context.DrawLine(pen, new Point(left, bottom - 9), new Point(left, bottom));
        context.DrawLine(pen, new Point(right, bottom - 9), new Point(right, bottom));
        context.DrawText(label, new Point(left + ((length - label.Width) / 2), bottom - label.Height - 8));
    }

    /// <summary>The roundest length (1, 2 or 5 times a power of ten) not above <paramref name="most"/>.</summary>
    internal static double NiceLength(double most)
    {
        var power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(most, 1e-6))));
        return most >= 5 * power ? 5 * power : most >= 2 * power ? 2 * power : power;
    }
}
