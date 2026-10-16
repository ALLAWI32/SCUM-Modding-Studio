using System.Globalization;
using System.Numerics;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Rendering;
using ScumStudio.Level.Spawns;
using ScumStudio.Level.World;
using Vector = Avalonia.Vector;

namespace ScumStudio.App.Controls;

/// <summary>What a marker on the <see cref="SpawnMap"/> stands for; each layer has its own glyph and colour.</summary>
public enum SpawnMapLayer
{
    /// <summary>Vehicle spawn places (car glyph).</summary>
    Vehicles,

    /// <summary>Zombie spawn places (skull glyph).</summary>
    Zombies,

    /// <summary>Armed NPC spawn places (shield glyph).</summary>
    Npcs,

    /// <summary>Animal spawn places and hunting areas (paw glyph).</summary>
    Animals,

    /// <summary>Player spawn places (flag glyph).</summary>
    Players,

    /// <summary>Threat zones (their ellipse; a diamond when too small to see).</summary>
    Zones,
}

/// <summary>The layers the <see cref="SpawnMap"/> shows.</summary>
[Flags]
public enum SpawnMapLayers
{
    /// <summary>None.</summary>
    None = 0,

    /// <summary><see cref="SpawnMapLayer.Vehicles"/>.</summary>
    Vehicles = 1 << SpawnMapLayer.Vehicles,

    /// <summary><see cref="SpawnMapLayer.Zombies"/>.</summary>
    Zombies = 1 << SpawnMapLayer.Zombies,

    /// <summary><see cref="SpawnMapLayer.Npcs"/>.</summary>
    Npcs = 1 << SpawnMapLayer.Npcs,

    /// <summary><see cref="SpawnMapLayer.Animals"/>.</summary>
    Animals = 1 << SpawnMapLayer.Animals,

    /// <summary><see cref="SpawnMapLayer.Players"/>.</summary>
    Players = 1 << SpawnMapLayer.Players,

    /// <summary><see cref="SpawnMapLayer.Zones"/>.</summary>
    Zones = 1 << SpawnMapLayer.Zones,

    /// <summary>Every layer.</summary>
    All = Vehicles | Zombies | Npcs | Animals | Players | Zones,
}

/// <summary>What the left mouse button does on the <see cref="SpawnMap"/>.</summary>
public enum SpawnMapTool
{
    /// <summary>Drag pans.</summary>
    Pan,

    /// <summary>A click selects the map cell under it; a drag pans.</summary>
    Sector,

    /// <summary>A drag draws a rectangle; its handles resize it and a drag inside moves it.</summary>
    Rectangle,

    /// <summary>Clicks add corners, a double click or Enter closes the polygon; its corners can be dragged.</summary>
    Polygon,

    /// <summary>A click raises <see cref="SpawnMap.PlaceCommand"/> with the world point.</summary>
    Place,
}

/// <summary>One spawn place on the <see cref="SpawnMap"/> (world cm); a zone or hunting area also has its half sizes and yaw (degrees).</summary>
public readonly record struct SpawnMapMarker(int Id, float X, float Y, SpawnMapLayer Layer, float RadiusX = 0, float RadiusY = 0, float Yaw = 0);

/// <summary>
/// The island seen from above for the Spawns page: the island picture (<see cref="Picture"/>, laid out by
/// <see cref="IslandMap"/>) under the map cells and the spawn places, one glyph and colour per layer. The wheel zooms
/// about the cursor, a drag pans, and the <see cref="Tool"/> picks an area: a cell, a rectangle or a polygon
/// (<see cref="Area"/>, two-way). Above <see cref="GlyphLimit"/> visible places the glyphs give way to counts per layer.
/// </summary>
public sealed partial class SpawnMap : Control, ICustomHitTest
{
    /// <summary>The island picture (covers <see cref="IslandMap"/>'s square), or null for the grid alone.</summary>
    public static readonly StyledProperty<IImage?> PictureProperty =
        AvaloniaProperty.Register<SpawnMap, IImage?>(nameof(Picture));

    /// <summary>The spawn places drawn (a new list replaces them; the list is read when it is set).</summary>
    public static readonly StyledProperty<IReadOnlyList<SpawnMapMarker>?> MarkersProperty =
        AvaloniaProperty.Register<SpawnMap, IReadOnlyList<SpawnMapMarker>?>(nameof(Markers));

    /// <summary>The layers drawn.</summary>
    public static readonly StyledProperty<SpawnMapLayers> ShownLayersProperty =
        AvaloniaProperty.Register<SpawnMap, SpawnMapLayers>(nameof(ShownLayers), SpawnMapLayers.All);

    /// <summary>The selected area (world cm), or null.</summary>
    public static readonly StyledProperty<SpawnArea?> AreaProperty =
        AvaloniaProperty.Register<SpawnMap, SpawnArea?>(nameof(Area), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>What the left mouse button does.</summary>
    public static readonly StyledProperty<SpawnMapTool> ToolProperty =
        AvaloniaProperty.Register<SpawnMap, SpawnMapTool>(nameof(Tool), SpawnMapTool.Sector);

    /// <summary>Ids of the markers drawn with a ring (the places of the selected area, a search hit).</summary>
    public static readonly StyledProperty<IReadOnlySet<int>?> HighlightedIdsProperty =
        AvaloniaProperty.Register<SpawnMap, IReadOnlySet<int>?>(nameof(HighlightedIds));

    /// <summary>Raised by a click with <see cref="SpawnMapTool.Place"/>; the parameter is the world point (<see cref="Vector2"/>, cm).</summary>
    public static readonly StyledProperty<ICommand?> PlaceCommandProperty =
        AvaloniaProperty.Register<SpawnMap, ICommand?>(nameof(PlaceCommand));

    /// <summary>Most single places drawn as glyphs; more visible places are drawn as counts.</summary>
    public const int GlyphLimit = 3000;

    // Closest zoom: one screen pixel per 25 cm.
    private const double MaxScale = IslandMap.SizeCm / 25.0;

    private double _scale = 1;
    private Point _offset;
    private bool _userMoved;
    private readonly Dictionary<MapCell, FormattedText> _labels = [];
    private IBrush? _labelBrush;

    static SpawnMap()
    {
        AffectsRender<SpawnMap>(PictureProperty, MarkersProperty, ShownLayersProperty, AreaProperty, HighlightedIdsProperty);
        ClipToBoundsProperty.OverrideDefaultValue<SpawnMap>(true);
        FocusableProperty.OverrideDefaultValue<SpawnMap>(true);
    }

    /// <inheritdoc cref="PictureProperty" />
    public IImage? Picture
    {
        get => GetValue(PictureProperty);
        set => SetValue(PictureProperty, value);
    }

    /// <inheritdoc cref="MarkersProperty" />
    public IReadOnlyList<SpawnMapMarker>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    /// <inheritdoc cref="ShownLayersProperty" />
    public SpawnMapLayers ShownLayers
    {
        get => GetValue(ShownLayersProperty);
        set => SetValue(ShownLayersProperty, value);
    }

    /// <inheritdoc cref="AreaProperty" />
    public SpawnArea? Area
    {
        get => GetValue(AreaProperty);
        set => SetValue(AreaProperty, value);
    }

    /// <inheritdoc cref="ToolProperty" />
    public SpawnMapTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    /// <inheritdoc cref="HighlightedIdsProperty" />
    public IReadOnlySet<int>? HighlightedIds
    {
        get => GetValue(HighlightedIdsProperty);
        set => SetValue(HighlightedIdsProperty, value);
    }

    /// <inheritdoc cref="PlaceCommandProperty" />
    public ICommand? PlaceCommand
    {
        get => GetValue(PlaceCommandProperty);
        set => SetValue(PlaceCommandProperty, value);
    }

    /// <summary>Screen pixels across the whole picture (the zoom).</summary>
    public double ViewScale => _scale;

    /// <summary>The screen point (control pixels) of the world point (<paramref name="x"/>, <paramref name="y"/>) cm.</summary>
    public Point WorldToScreen(float x, float y)
    {
        var (u, v) = IslandMap.ToMap(x, y);
        return new Point(_offset.X + (u * _scale), _offset.Y + (v * _scale));
    }

    /// <summary>The world point (cm) under the screen point <paramref name="point"/> (control pixels).</summary>
    public Vector2 ScreenToWorld(Point point)
    {
        var (x, y) = IslandMap.ToWorld((point.X - _offset.X) / _scale, (point.Y - _offset.Y) / _scale);
        return new Vector2(x, y);
    }

    /// <summary>Shows the whole island.</summary>
    public void ZoomToFit()
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        _scale = FitScale;
        _offset = new Point((size.Width - _scale) / 2, (size.Height - _scale) / 2);
        _userMoved = false;
        InvalidateVisual();
    }

    /// <summary>Frames <paramref name="area"/> with a margin.</summary>
    public void ZoomTo(SpawnArea area)
    {
        ArgumentNullException.ThrowIfNull(area);
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        var (u0, v0) = IslandMap.ToMap(area.MinX, area.MinY);
        var (u1, v1) = IslandMap.ToMap(area.MaxX, area.MaxY);
        var (du, dv) = (Math.Max(Math.Abs(u1 - u0), 1e-4), Math.Max(Math.Abs(v1 - v0), 1e-4));
        _scale = ClampScale(Math.Min(size.Width / (du * 1.2), size.Height / (dv * 1.2)));
        _offset = new Point((size.Width / 2) - (((u0 + u1) / 2) * _scale), (size.Height / 2) - (((v0 + v1) / 2) * _scale));
        _userMoved = true;
        InvalidateVisual();
    }

    /// <summary>Zooms by <paramref name="factor"/> about the screen point <paramref name="at"/>: the world point under it stays there.</summary>
    public void ZoomAt(Point at, double factor)
    {
        var (u, v) = ((at.X - _offset.X) / _scale, (at.Y - _offset.Y) / _scale);
        _scale = ClampScale(_scale * factor);
        _offset = new Point(at.X - (u * _scale), at.Y - (v * _scale));
        _userMoved = true;
        InvalidateVisual();
    }

    /// <summary>Moves the picture by <paramref name="delta"/> screen pixels (some of the island stays in view).</summary>
    public void PanBy(Vector delta)
    {
        var size = Bounds.Size;
        const double keep = 48;
        _offset = new Point(
            Math.Clamp(_offset.X + delta.X, keep - _scale, Math.Max(keep - _scale, size.Width - keep)),
            Math.Clamp(_offset.Y + delta.Y, keep - _scale, Math.Max(keep - _scale, size.Height - keep)));
        _userMoved = true;
        InvalidateVisual();
    }

    /// <inheritdoc />
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Token("ViewportBrush"), bounds);
        var square = new Rect(_offset, new Size(_scale, _scale));
        if (Picture is { } picture && picture.Size.Width > 0 && picture.Size.Height > 0)
        {
            context.DrawImage(picture, new Rect(picture.Size), square);
        }
        else
        {
            context.FillRectangle(Token("SurfaceBrush"), square);
        }

        DrawZones(context, bounds);
        DrawGrid(context, bounds);
        DrawMarkers(context, bounds);
        DrawSelection(context);
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_userMoved)
        {
            ZoomToFit();
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToolProperty)
        {
            CancelShape();
        }
        else if (change.Property == MarkersProperty)
        {
            PrepareMarkers(Markers);
        }
    }

    private double FitScale => Math.Max(1, Math.Min(Bounds.Width, Bounds.Height) * 0.96);

    private double ClampScale(double scale) => Math.Clamp(scale, FitScale * 0.5, Math.Max(FitScale, MaxScale));

    private IBrush Token(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    private static IBrush Translucent(IBrush brush, double opacity) =>
        new SolidColorBrush(brush is ISolidColorBrush solid ? solid.Color : Colors.Gray, opacity);

    private void DrawGrid(DrawingContext context, Rect bounds)
    {
        var line = new Pen(Translucent(Token("KhakiBrush"), 0.55), 1);
        for (var k = 0; k <= 5; k++)
        {
            var at = k * _scale / 5;
            context.DrawLine(line, new Point(_offset.X + at, _offset.Y), new Point(_offset.X + at, _offset.Y + _scale));
            context.DrawLine(line, new Point(_offset.X, _offset.Y + at), new Point(_offset.X + _scale, _offset.Y + at));
        }

        if (_scale / 5 < 36)
        {
            return;
        }

        var chip = Token("ViewportChromeBrush");
        var text = Token("TextBrush");
        foreach (var cell in MapCell.All)
        {
            var (minX, minY, maxX, maxY) = IslandMap.CellRect(cell);
            var (a, b) = (WorldToScreen(minX, minY), WorldToScreen(maxX, maxY));
            var corner = new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
            if (!bounds.Intersects(new Rect(corner, new Size(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)))))
            {
                continue;
            }

            var label = CellLabel(cell, text);
            var at = new Point(Math.Max(corner.X, 0) + 6, Math.Max(corner.Y, 0) + 6);
            context.DrawRectangle(chip, null, new Rect(at.X - 4, at.Y - 2, label.Width + 8, label.Height + 4), 3, 3);
            context.DrawText(label, at);
        }
    }

    private void DrawSelection(DrawingContext context)
    {
        var accent = Token("AccentBrush");
        var outline = new Pen(accent, 2);
        if ((_draft ?? Area) is { } area)
        {
            context.DrawGeometry(Token("AccentSoftBrush"), outline, Outline(area.Points, close: true));
            var handle = Token("ViewportBrush");
            if (Tool == SpawnMapTool.Rectangle && area.Shape != SpawnAreaShape.Polygon)
            {
                foreach (var p in Handles(area))
                {
                    context.DrawRectangle(accent, new Pen(handle, 1), new Rect(p.X - 4, p.Y - 4, 8, 8));
                }
            }
            else if (Tool == SpawnMapTool.Polygon && area.Shape == SpawnAreaShape.Polygon)
            {
                foreach (var p in area.Points)
                {
                    context.DrawEllipse(accent, new Pen(handle, 1), WorldToScreen(p.X, p.Y), 4.5, 4.5);
                }
            }
        }

        if (_polygon.Count > 0)
        {
            var points = _polygon.ToList();
            if (_hover is { } hover)
            {
                points.Add(hover);
            }

            context.DrawGeometry(null, new Pen(accent, 2, new DashStyle([4, 3], 0)), Outline(points, close: false));
            foreach (var p in _polygon)
            {
                context.DrawEllipse(accent, null, WorldToScreen(p.X, p.Y), 3.5, 3.5);
            }
        }
    }

    // Cell names are the game's identifiers (A_0 ... Z_4), not translated.
    private FormattedText CellLabel(MapCell cell, IBrush brush)
    {
        if (!ReferenceEquals(brush, _labelBrush))
        {
            _labelBrush = brush;
            _labels.Clear();
        }

        if (!_labels.TryGetValue(cell, out var label))
        {
            _labels[cell] = label = new FormattedText(cell.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 12, brush);
        }

        return label;
    }

    private StreamGeometry Outline(IReadOnlyList<Vector2> points, bool close)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        for (var i = 0; i < points.Count; i++)
        {
            var p = WorldToScreen(points[i].X, points[i].Y);
            if (i == 0)
            {
                g.BeginFigure(p, close);
            }
            else
            {
                g.LineTo(p);
            }
        }

        if (points.Count > 0)
        {
            g.EndFigure(close);
        }

        return geometry;
    }

    private static readonly Typeface LabelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
}
