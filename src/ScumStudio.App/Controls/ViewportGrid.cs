using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace ScumStudio.App.Controls;

/// <summary>
/// The designed empty state of a viewport or preview: a warm engineering grid (minor/major lines, the two axes through
/// the centre in the axis colours) that fades into the background towards the edges.
/// </summary>
public sealed class ViewportGrid : Control
{
    /// <summary>Background fill.</summary>
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<ViewportGrid, IBrush?>(nameof(Background), new SolidColorBrush(Color.Parse("#0B0C0A")));

    /// <summary>Distance between minor lines in device-independent pixels.</summary>
    public static readonly StyledProperty<double> CellSizeProperty =
        AvaloniaProperty.Register<ViewportGrid, double>(nameof(CellSize), 24);

    /// <summary>Every how many minor cells a major line is drawn.</summary>
    public static readonly StyledProperty<int> MajorEveryProperty =
        AvaloniaProperty.Register<ViewportGrid, int>(nameof(MajorEvery), 5);

    /// <summary>Colour of both axis lines when <see cref="AxisXBrush"/>/<see cref="AxisYBrush"/> are not set.</summary>
    public static readonly StyledProperty<IBrush?> AxisBrushProperty =
        AvaloniaProperty.Register<ViewportGrid, IBrush?>(nameof(AxisBrush));

    /// <summary>Colour of the horizontal axis line (X).</summary>
    public static readonly StyledProperty<IBrush?> AxisXBrushProperty =
        AvaloniaProperty.Register<ViewportGrid, IBrush?>(nameof(AxisXBrush));

    /// <summary>Colour of the vertical axis line (Y).</summary>
    public static readonly StyledProperty<IBrush?> AxisYBrushProperty =
        AvaloniaProperty.Register<ViewportGrid, IBrush?>(nameof(AxisYBrush));

    // Warm hairlines one and two steps above the viewport colour (Stroke family).
    private static readonly IPen MinorPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#171813")), 1);
    private static readonly IPen MajorPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#24261E")), 1);

    static ViewportGrid()
    {
        AffectsRender<ViewportGrid>(BackgroundProperty, CellSizeProperty, MajorEveryProperty, AxisBrushProperty, AxisXBrushProperty, AxisYBrushProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ViewportGrid>(true);
    }

    /// <inheritdoc cref="BackgroundProperty" />
    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <inheritdoc cref="CellSizeProperty" />
    public double CellSize
    {
        get => GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }

    /// <inheritdoc cref="MajorEveryProperty" />
    public int MajorEvery
    {
        get => GetValue(MajorEveryProperty);
        set => SetValue(MajorEveryProperty, value);
    }

    /// <inheritdoc cref="AxisBrushProperty" />
    public IBrush? AxisBrush
    {
        get => GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    /// <inheritdoc cref="AxisXBrushProperty" />
    public IBrush? AxisXBrush
    {
        get => GetValue(AxisXBrushProperty);
        set => SetValue(AxisXBrushProperty, value);
    }

    /// <inheritdoc cref="AxisYBrushProperty" />
    public IBrush? AxisYBrush
    {
        get => GetValue(AxisYBrushProperty);
        set => SetValue(AxisYBrushProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
        {
            context.FillRectangle(background, bounds);
        }

        var cell = Math.Max(4, CellSize);
        var major = Math.Max(1, MajorEvery);
        var cx = Math.Round(bounds.Width / 2) + 0.5;
        var cy = Math.Round(bounds.Height / 2) + 0.5;

        // Lines are laid out from the centre so the axes sit on a major line.
        var firstX = cx - Math.Ceiling(cx / cell) * cell;
        for (var x = firstX; x <= bounds.Width; x += cell)
        {
            var index = (int)Math.Round((x - cx) / cell);
            context.DrawLine(index % major == 0 ? MajorPen : MinorPen, new Point(x, 0), new Point(x, bounds.Height));
        }

        var firstY = cy - Math.Ceiling(cy / cell) * cell;
        for (var y = firstY; y <= bounds.Height; y += cell)
        {
            var index = (int)Math.Round((y - cy) / cell);
            context.DrawLine(index % major == 0 ? MajorPen : MinorPen, new Point(0, y), new Point(bounds.Width, y));
        }

        var axisX = AxisXBrush ?? AxisBrush;
        var axisY = AxisYBrush ?? AxisBrush;
        using (context.PushOpacity(0.45))
        {
            if (axisY is not null)
            {
                context.DrawLine(new Pen(axisY, 1), new Point(cx, 0), new Point(cx, bounds.Height));
            }

            if (axisX is not null)
            {
                context.DrawLine(new Pen(axisX, 1), new Point(0, cy), new Point(bounds.Width, cy));
            }
        }

        // Fade the grid into the background towards the edges so the centre (where the hint sits) reads first.
        if (Background is ISolidColorBrush solid && bounds.Width > 0 && bounds.Height > 0)
        {
            var edge = solid.Color;
            var vignette = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.75, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, edge.R, edge.G, edge.B), 0.25),
                    new GradientStop(Color.FromArgb(235, edge.R, edge.G, edge.B), 1),
                },
            };
            context.FillRectangle(vignette, bounds);
        }
    }
}
