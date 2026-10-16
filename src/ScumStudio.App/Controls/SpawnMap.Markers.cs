using System.Globalization;
using Avalonia;
using Avalonia.Media;
using ScumStudio.Level.World;

namespace ScumStudio.App.Controls;

// The spawn places: zones and hunting areas as their real ellipse, single places as one glyph per layer (shape and colour
// both differ, so the layers read without colour), and above GlyphLimit visible places one count per layer for each
// patch of the map. The places are kept as picture positions in flat arrays when they are set, so a frame over 30,000 of
// them is one tight loop; only what is on screen is drawn and each layer's glyphs are one geometry.
public sealed partial class SpawnMap
{
    // Edge (screen px) of a patch of the map that gets one count per layer.
    private const double CountPatch = 128;

    // An ellipse smaller than this (screen px) is drawn as the layer's glyph.
    private const double SmallestEllipse = 3;

    // Theme tokens per layer, in SpawnMapLayer order.
    private static readonly string[] LayerBrushes = ["AxisZBrush", "DangerBrush", "WarningBrush", "SuccessBrush", "TextBrush", "KhakiBrush"];

    // Glyphs on a 12 px grid centred on the place: closed polygons, holes by the even-odd rule. In SpawnMapLayer order.
    private static readonly Point[][][] Glyphs =
    [
        // Vehicles: a car body on two wheels.
        [[new(-6, 1.5), new(-6, -1), new(-4, -1), new(-2, -4), new(3, -4), new(5, -1), new(6, -1), new(6, 1.5), new(5, 1.5), new(5, 3.5), new(3, 3.5), new(3, 1.5), new(-3, 1.5), new(-3, 3.5), new(-5, 3.5), new(-5, 1.5)]],

        // Zombies: a skull with two eye holes.
        [
            [new(-5, -1), new(-5, -4), new(-3, -6), new(3, -6), new(5, -4), new(5, -1), new(3, 1), new(3, 4), new(-3, 4), new(-3, 1)],
            [new(-3.5, -3.5), new(-1, -3.5), new(-1, -1.5), new(-3.5, -1.5)],
            [new(1, -3.5), new(3.5, -3.5), new(3.5, -1.5), new(1, -1.5)],
        ],

        // NPCs: a shield.
        [[new(-5, -5), new(5, -5), new(5, 0), new(0, 6), new(-5, 0)]],

        // Animals: a paw, its pad and four toes.
        [[new(-3, 1), new(0, -0.5), new(3, 1), new(3.5, 4.5), new(-3.5, 4.5)], Diamond(-4.5, -2, 1.6), Diamond(-1.6, -4.5, 1.6), Diamond(1.6, -4.5, 1.6), Diamond(4.5, -2, 1.6)],

        // Players: a flag on its pole.
        [[new(-4, 6), new(-4, -6), new(5, -3.5), new(-2.5, -1), new(-2.5, 6)]],

        // Zones too small to show their ellipse: a diamond.
        [Diamond(0, 0, 5)],
    ];

    private readonly List<(Point At, SpawnMapLayer Layer, int Id)> _visible = [];
    private readonly List<(Point At, SpawnMapLayer Layer, int Id)> _rings = [];
    private int _visibleCount;
    private readonly Dictionary<int, FormattedText> _countTexts = [];
    private IBrush? _countBrush;

    // The markers as picture positions (0..1), layers, ids and picture-space reach (0 for single places).
    private float[] _u = [];
    private float[] _v = [];
    private float[] _reach = [];
    private byte[] _layers = [];
    private int[] _ids = [];
    private int[] _ellipses = [];

    /// <summary>How many single places the last frame drew as glyphs or counted (after layers and the view were applied).</summary>
    public int VisibleMarkerCount => _visibleCount;

    /// <summary>True when the last frame drew counts instead of glyphs (more than <see cref="GlyphLimit"/> places in view).</summary>
    public bool DrewCounts { get; private set; }

    private static Point[] Diamond(double x, double y, double r) => [new(x, y - r), new(x + r, y), new(x, y + r), new(x - r, y)];

    private void PrepareMarkers(IReadOnlyList<SpawnMapMarker>? markers)
    {
        var count = markers?.Count ?? 0;
        (_u, _v, _reach, _layers, _ids) = (new float[count], new float[count], new float[count], new byte[count], new int[count]);
        var ellipses = new List<int>();
        for (var i = 0; i < count; i++)
        {
            var marker = markers![i];
            var (u, v) = IslandMap.ToMap(marker.X, marker.Y);
            (_u[i], _v[i], _layers[i], _ids[i]) = ((float)u, (float)v, (byte)marker.Layer, marker.Id);
            if (marker.RadiusX > 0 && marker.RadiusY > 0)
            {
                _reach[i] = MathF.Max(marker.RadiusX, marker.RadiusY) / IslandMap.SizeCm;
                ellipses.Add(i);
            }
        }

        _ellipses = [.. ellipses];
    }

    private void DrawZones(DrawingContext context, Rect bounds)
    {
        if (Markers is not { } markers || markers.Count != _ids.Length)
        {
            return;
        }

        var (mask, highlighted) = ((int)ShownLayers, HighlightedIds);
        var fills = new IBrush?[Glyphs.Length];
        var pens = new IPen?[Glyphs.Length];
        IPen? ring = null;
        foreach (var i in _ellipses)
        {
            var marker = markers[i];
            var layer = _layers[i];
            var (rx, ry) = (marker.RadiusX * _scale / IslandMap.SizeCm, marker.RadiusY * _scale / IslandMap.SizeCm);
            var reach = Math.Max(rx, ry);
            var at = new Point(_offset.X + (_u[i] * _scale), _offset.Y + (_v[i] * _scale));
            if ((mask & (1 << layer)) == 0 || reach < SmallestEllipse
                || at.X + reach < 0 || at.Y + reach < 0 || at.X - reach > bounds.Width || at.Y - reach > bounds.Height)
            {
                continue;
            }

            fills[layer] ??= Translucent(Token(LayerBrushes[layer]), layer == (int)SpawnMapLayer.Zones ? 0.18 : 0.1);
            pens[layer] ??= new Pen(Token(LayerBrushes[layer]), 1.5);
            var pen = highlighted?.Contains(_ids[i]) == true ? ring ??= new Pen(Token("AccentBrush"), 2.5) : pens[layer];
            using (context.PushTransform(Matrix.CreateRotation(marker.Yaw * Math.PI / 180) * Matrix.CreateTranslation(at.X, at.Y)))
            {
                context.DrawEllipse(fills[layer], pen, default, rx, ry);
            }
        }
    }

    // One pass over the places: what is in view is counted per layer and patch, kept for glyphs while there are few
    // enough, and kept for a ring when highlighted.
    private void DrawMarkers(DrawingContext context, Rect bounds)
    {
        (_visibleCount, DrewCounts) = (0, false);
        _visible.Clear();
        _rings.Clear();
        var ids = HighlightedIds is { Count: > 0 } highlighted ? highlighted : null;
        var mask = (int)ShownLayers;
        var (scale, ox, oy) = (_scale, _offset.X, _offset.Y);
        var (x0, y0, x1, y1) = (-8d, -8d, bounds.Width + 8, bounds.Height + 8);
        var (px0, py0) = ((int)Math.Floor((x0 - ox) / CountPatch), (int)Math.Floor((y0 - oy) / CountPatch));
        var columns = (int)Math.Floor((x1 - ox) / CountPatch) - px0 + 1;
        var rows = (int)Math.Floor((y1 - oy) / CountPatch) - py0 + 1;
        var counts = new int[columns * rows * Glyphs.Length];
        var smallest = (float)(SmallestEllipse / scale);
        const int zones = (int)SpawnMapLayer.Zones;
        for (var i = 0; i < _ids.Length; i++)
        {
            int layer = _layers[i];
            if ((mask & (1 << layer)) == 0 || (layer == zones && _reach[i] >= smallest))
            {
                continue; // hidden, or drawn as its ellipse
            }

            var x = ox + (_u[i] * scale);
            var y = oy + (_v[i] * scale);
            if (x < x0 || x > x1 || y < y0 || y > y1)
            {
                continue;
            }

            _visibleCount++;
            var patch = (((int)Math.Floor((y - oy) / CountPatch) - py0) * columns) + ((int)Math.Floor((x - ox) / CountPatch) - px0);
            counts[(patch * Glyphs.Length) + layer]++;
            if (_visible.Count <= GlyphLimit)
            {
                _visible.Add((new Point(x, y), (SpawnMapLayer)layer, _ids[i]));
            }

            if (ids is not null && _rings.Count <= GlyphLimit && ids.Contains(_ids[i]))
            {
                _rings.Add((new Point(x, y), (SpawnMapLayer)layer, _ids[i]));
            }
        }

        var outline = new Pen(Token("ViewportBrush"), 1);
        DrewCounts = _visibleCount > GlyphLimit;
        if (DrewCounts)
        {
            DrawCounts(context, counts, columns, rows, px0, py0);
        }

        if (_rings.Count > GlyphLimit)
        {
            return;
        }

        DrawGlyphs(context, outline, DrewCounts ? _rings : _visible, 1);
        if (_rings.Count > 0)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                foreach (var (at, _, _) in _rings)
                {
                    g.BeginFigure(new Point(at.X + 9, at.Y), false);
                    g.ArcTo(new Point(at.X - 9, at.Y), new Size(9, 9), 0, false, SweepDirection.Clockwise);
                    g.ArcTo(new Point(at.X + 9, at.Y), new Size(9, 9), 0, false, SweepDirection.Clockwise);
                    g.EndFigure(false);
                }
            }

            context.DrawGeometry(null, new Pen(Token("AccentBrush"), 2), geometry);
        }
    }

    private void DrawGlyphs(DrawingContext context, IPen? outline, List<(Point At, SpawnMapLayer Layer, int Id)> places, double scale)
    {
        for (var layer = 0; layer < Glyphs.Length; layer++)
        {
            StreamGeometry? geometry = null;
            StreamGeometryContext? g = null;
            foreach (var (at, of, _) in places)
            {
                if ((int)of != layer)
                {
                    continue;
                }

                if (g is null)
                {
                    geometry = new StreamGeometry();
                    g = geometry.Open();
                    g.SetFillRule(FillRule.EvenOdd);
                }

                foreach (var polygon in Glyphs[layer])
                {
                    g.BeginFigure(new Point(at.X + (polygon[0].X * scale), at.Y + (polygon[0].Y * scale)), true);
                    for (var i = 1; i < polygon.Length; i++)
                    {
                        g.LineTo(new Point(at.X + (polygon[i].X * scale), at.Y + (polygon[i].Y * scale)));
                    }

                    g.EndFigure(true);
                }
            }

            if (g is not null)
            {
                g.Dispose();
                context.DrawGeometry(Token(LayerBrushes[layer]), outline, geometry!);
            }
        }
    }

    // One chip per layer and patch: the layer's glyph and how many of its places the patch holds. Patches follow the
    // picture, so the chips move with it when panning.
    private void DrawCounts(DrawingContext context, int[] counts, int columns, int rows, int px0, int py0)
    {
        var (ox, oy) = (_offset.X, _offset.Y);
        var chip = Token("ViewportChromeBrush");
        var text = Token("TextBrush");
        var glyphs = new List<(Point At, SpawnMapLayer Layer, int Id)>();
        for (var patch = 0; patch < columns * rows; patch++)
        {
            var x = ox + (((patch % columns) + px0) * CountPatch) + 4;
            var y = oy + (((patch / columns) + py0) * CountPatch) + 4;
            for (var layer = 0; layer < Glyphs.Length; layer++)
            {
                var count = counts[(patch * Glyphs.Length) + layer];
                if (count == 0)
                {
                    continue;
                }

                var label = CountText(count, text);
                context.DrawRectangle(chip, null, new Rect(x, y, label.Width + 22, 15), 3, 3);
                context.DrawText(label, new Point(x + 17, y + 7.5 - (label.Height / 2)));
                glyphs.Add((new Point(x + 8, y + 7.5), (SpawnMapLayer)layer, -1));
                y += 16;
            }
        }

        DrawGlyphs(context, null, glyphs, 0.85);
    }

    private FormattedText CountText(int count, IBrush brush)
    {
        if (!ReferenceEquals(brush, _countBrush) || _countTexts.Count > 4096)
        {
            _countBrush = brush;
            _countTexts.Clear();
        }

        if (!_countTexts.TryGetValue(count, out var text))
        {
            _countTexts[count] = text = new FormattedText(count.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, 10, brush);
        }

        return text;
    }
}
