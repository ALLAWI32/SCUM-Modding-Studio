using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ScumStudio.App.Controls;

/// <summary>
/// Draws one of the line icons of <c>Styles/Icons.axaml</c> (24 x 24 grid) scaled to the control, stroked with
/// <see cref="Foreground"/> using round caps and joins. The icon is chosen by resource key (<see cref="Kind"/>) so
/// view models can expose icons as plain strings.
/// </summary>
public sealed class GlyphIcon : Control
{
    /// <summary>Size of the icon design grid.</summary>
    public const double GridSize = 24;

    /// <summary>Resource key of the geometry, e.g. <c>Icon.Map</c>.</summary>
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<GlyphIcon, string?>(nameof(Kind));

    /// <summary>Explicit geometry; wins over <see cref="Kind"/>.</summary>
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<GlyphIcon, Geometry?>(nameof(Data));

    /// <summary>Stroke brush.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<GlyphIcon, IBrush?>(nameof(Foreground), Brushes.White);

    /// <summary>Stroke thickness in device-independent pixels (not scaled with the icon).</summary>
    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<GlyphIcon, double>(nameof(StrokeThickness), 1.5);

    /// <summary>Default edge length when no Width/Height is set.</summary>
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<GlyphIcon, double>(nameof(IconSize), 18);

    static GlyphIcon()
    {
        AffectsRender<GlyphIcon>(KindProperty, DataProperty, ForegroundProperty, StrokeThicknessProperty);
        AffectsMeasure<GlyphIcon>(IconSizeProperty);
    }

    /// <inheritdoc cref="KindProperty" />
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <inheritdoc cref="DataProperty" />
    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <inheritdoc cref="ForegroundProperty" />
    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <inheritdoc cref="StrokeThicknessProperty" />
    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    /// <inheritdoc cref="IconSizeProperty" />
    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(IconSize, IconSize);

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        var geometry = Data ?? ResolveGeometry();
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (geometry is null || Foreground is null || size <= 0)
        {
            return;
        }

        var scale = size / GridSize;
        var offsetX = (Bounds.Width - size) / 2;
        var offsetY = (Bounds.Height - size) / 2;
        var pen = new Pen(Foreground, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private Geometry? ResolveGeometry()
    {
        var key = Kind;
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is Geometry g)
        {
            return g;
        }

        return Application.Current?.TryGetResource(key, ActualThemeVariant, out var appValue) == true ? appValue as Geometry : null;
    }
}
