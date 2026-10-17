using System.Numerics;
using Avalonia;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Viewport;

namespace ScumStudio.App.Controls;

/// <summary>A line laid over the ground (UE world X/Y), drawn just above the terrain under it.</summary>
/// <param name="Points">The points in order.</param>
/// <param name="Color">Line colour (RGBA, 0..1).</param>
public sealed record GroundOutline(IReadOnlyList<Vector2> Points, Vector4 Color);

/// <summary>The hole tool (see <see cref="TerrainHole"/>): its outline on the ground, the wheel that sizes it, and the holes picks fall through.</summary>
public sealed partial class LevelViewport
{
    /// <summary>Hole mode: the mouse wheel raises <see cref="HoleWheel"/> (the hole's size) instead of flying, and the brush circle is not drawn.</summary>
    public static readonly StyledProperty<bool> HoleModeProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(HoleMode));

    /// <summary>Lines drawn over the ground (the hole tool's outline, the holes cut so far).</summary>
    public static readonly StyledProperty<IReadOnlyList<GroundOutline>?> GroundOutlinesProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyList<GroundOutline>?>(nameof(GroundOutlines));

    /// <summary>The project's terrain holes: the ground under the cursor, the brush and drops fall through them.</summary>
    public static readonly StyledProperty<IReadOnlyList<TerrainHole>?> TerrainHolesProperty =
        AvaloniaProperty.Register<LevelViewport, IReadOnlyList<TerrainHole>?>(nameof(TerrainHoles));

    private const float OutlineStepCm = 50f;
    private Func<float, float, bool>? _holeTest;
    private IReadOnlyList<TerrainHole>? _holeTestFor;

    /// <summary>Raised in <see cref="HoleMode"/> for each wheel notch (positive away from the user).</summary>
    public event EventHandler<float>? HoleWheel;

    /// <inheritdoc cref="HoleModeProperty" />
    public bool HoleMode
    {
        get => GetValue(HoleModeProperty);
        set => SetValue(HoleModeProperty, value);
    }

    /// <inheritdoc cref="GroundOutlinesProperty" />
    public IReadOnlyList<GroundOutline>? GroundOutlines
    {
        get => GetValue(GroundOutlinesProperty);
        set => SetValue(GroundOutlinesProperty, value);
    }

    /// <inheritdoc cref="TerrainHolesProperty" />
    public IReadOnlyList<TerrainHole>? TerrainHoles
    {
        get => GetValue(TerrainHolesProperty);
        set => SetValue(TerrainHolesProperty, value);
    }

    /// <summary>Gives the terrain the project's holes (the scene or backdrop may be new since the last frame).</summary>
    private void SyncHoles()
    {
        var holes = TerrainHoles;
        if (!ReferenceEquals(holes, _holeTestFor))
        {
            _holeTestFor = holes;
            _holeTest = holes is { Count: > 0 } list ? (x, y) => TerrainHole.CutsQuadAt(list, x, y) : null;
        }

        foreach (var field in new[] { Scene?.HeightField, Backdrop?.HeightField })
        {
            if (field is not null && !ReferenceEquals(field.Holes, _holeTest))
            {
                field.Holes = _holeTest;
            }
        }
    }

    /// <summary>Raises <see cref="HoleWheel"/>; false when the wheel is not the hole's.</summary>
    private bool TryHoleWheel(float notches)
    {
        if (!HoleMode || _drone || _looking)
        {
            return false;
        }

        HoleWheel?.Invoke(this, notches);
        return true;
    }

    /// <summary>The <see cref="GroundOutlines"/> as overlay lines, each point 30 cm above the ground under it.</summary>
    private void DrawGroundOutlines(List<OverlayLine> lines)
    {
        if (GroundOutlines is not { Count: > 0 } outlines)
        {
            return;
        }

        // A hole's outline lies on its rim: the terrain's own height there, the hole itself ignored.
        var (detail, island) = (Scene?.HeightField, Backdrop?.HeightField);
        float? Ground(float x, float y) => detail?.SurfaceHeight(x, y) ?? island?.SurfaceHeight(x, y);
        foreach (var outline in outlines)
        {
            var draped = DrapedLine(outline.Points, Ground);
            for (var i = 1; i < draped.Count; i++)
            {
                lines.Add(new OverlayLine(draped[i - 1], draped[i], outline.Color));
            }
        }
    }

    /// <summary>
    /// <paramref name="points"/> (UE X/Y) laid over the ground in GL space: every segment split about every 50 cm, each
    /// point 30 cm above the ground (<paramref name="ground"/>: null off the terrain, where the last known height is kept).
    /// </summary>
    internal static List<Vector3> DrapedLine(IReadOnlyList<Vector2> points, Func<float, float, float?> ground)
    {
        var result = new List<Vector3>();
        var last = 0f;
        for (var i = 0; i < points.Count; i++)
        {
            var steps = i == 0 ? 1 : Math.Clamp((int)MathF.Ceiling(Vector2.Distance(points[i - 1], points[i]) / OutlineStepCm), 1, 400);
            for (var s = i == 0 ? 0 : 1; s <= (i == 0 ? 0 : steps); s++)
            {
                var p = i == 0 ? points[0] : Vector2.Lerp(points[i - 1], points[i], s / (float)steps);
                last = ground(p.X, p.Y) ?? last;
                result.Add(UeToGl.Point(new FVector(p.X, p.Y, last + RingLift)));
            }
        }

        return result;
    }
}
