using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Model;

/// <summary>
/// One end of a spline piece moved, turned and made wider or narrower (owner: "grab the road's right and left corners and
/// widen or narrow it as I like; make the bridge longer or shorter; join it to the rest of the bridge without corner
/// problems"). Component space of the piece.
/// </summary>
/// <param name="Move">How far the end moved, cm.</param>
/// <param name="Yaw">Turn of the end's direction, degrees (positive right).</param>
/// <param name="Pitch">Tilt of the end's direction, degrees (positive up).</param>
/// <param name="Grow">Width change: 0 as made, 0.5 half again as wide, -0.5 half as wide.</param>
/// <param name="Shift">Sideways shift of the cross-section, cm (positive right): with <paramref name="Grow"/> one corner moves, the other stays.</param>
/// <param name="Lift">How far the push handle next to this end is raised, cm (negative lowers): both up make a hump in the middle, one up one down a wave.</param>
public readonly record struct SplineEnd(FVector Move = default, float Yaw = 0f, float Pitch = 0f, float Grow = 0f, float Shift = 0f, float Lift = 0f)
{
    /// <summary>True when the end is as the piece was made.</summary>
    public bool IsNone => IsNearly(default);

    /// <summary>True when both are the same within rounding.</summary>
    public bool IsNearly(SplineEnd other) =>
        FVector.Distance(Move, other.Move) < 0.05f && MathF.Abs(Yaw - other.Yaw) < 0.01f && MathF.Abs(Pitch - other.Pitch) < 0.01f
        && MathF.Abs(Grow - other.Grow) < 0.0005f && MathF.Abs(Shift - other.Shift) < 0.05f && MathF.Abs(Lift - other.Lift) < 0.05f;

    /// <summary>True when every value is a usable number (an end may not shrink to nothing).</summary>
    public bool IsValid =>
        !Move.ContainsNaN() && Move.Size() < 1_000_000f && float.IsFinite(Yaw) && float.IsFinite(Pitch) && MathF.Abs(Pitch) <= 89f
        && float.IsFinite(Grow) && Grow > -0.99f && Grow < 100f && float.IsFinite(Shift) && MathF.Abs(Shift) < 100_000f
        && float.IsFinite(Lift) && MathF.Abs(Lift) < 1_000_000f;
}

/// <summary>The cross-section at one end of a spline piece (component space): where it leaves, how wide it is, its edges.</summary>
/// <param name="Middle">The middle of the end at the mesh's own height (where a road's surface usually is): what welds.</param>
/// <param name="Left">The left edge at that height.</param>
/// <param name="Right">The right edge at that height.</param>
/// <param name="Outward">Unit direction out of the piece at this end.</param>
/// <param name="Width">Distance between the edges, cm.</param>
public readonly record struct EndSection(FVector Middle, FVector Left, FVector Right, FVector Outward, float Width);

/// <summary>
/// End edits of spline pieces (<see cref="SplineEnd"/>): applying them, the end cross-sections handles are drawn at, a
/// corner dragged sideways, and an end welded onto another piece's end. Follows the slice frame of UE 4.27
/// <c>SplineMeshComponent</c>: cross axes X = SplineUpDir × direction (right), Y = direction × X (up), rolled, with the
/// offset along the unrolled axes.
/// </summary>
public static class SplineEnds
{
    /// <summary>
    /// The full shape of a piece: its curve with the ends edited, then the two handles pushed sideways
    /// (<see cref="SplineSway"/>) and raised (<see cref="SplineEnd.Lift"/>).
    /// </summary>
    public static SplineMeshParams Shape(SplineMeshParams spline, float sway1, float sway2, SplineEnd start, SplineEnd end) =>
        Raise(SplineSway.Apply(Apply(spline, start, end), sway1, sway2), start.Lift, end.Lift);

    /// <summary>The curve's two inner control points raised (owner: "a rise in the middle"), the ends where they are.</summary>
    private static SplineMeshParams Raise(SplineMeshParams spline, float lift1, float lift2)
    {
        if (lift1 == 0f && lift2 == 0f)
        {
            return spline;
        }

        var (p1, p2) = SplineSway.Controls(spline);
        p1 += FVector.Up * lift1;
        p2 += FVector.Up * lift2;
        return spline with { StartTangent = (p1 - spline.StartPos) * 3f, EndTangent = (spline.EndPos - p2) * 3f };
    }

    /// <summary>
    /// <paramref name="spline"/> with its ends moved, turned and widened. The tangents keep their length in proportion to
    /// the distance between the ends, so a piece made longer stays as round as it was.
    /// </summary>
    public static SplineMeshParams Apply(SplineMeshParams spline, SplineEnd start, SplineEnd end)
    {
        ArgumentNullException.ThrowIfNull(spline);
        if (start.IsNone && end.IsNone)
        {
            return spline;
        }

        var startPos = spline.StartPos + start.Move;
        var endPos = spline.EndPos + end.Move;
        var before = FVector.Distance(spline.StartPos, spline.EndPos);
        var stretch = before > 1f ? FVector.Distance(startPos, endPos) / before : 1f;
        return spline with
        {
            StartPos = startPos,
            EndPos = endPos,
            StartTangent = Turn(spline.StartTangent, start.Yaw, start.Pitch) * stretch,
            EndTangent = Turn(spline.EndTangent, end.Yaw, end.Pitch) * stretch,
            StartScale = Widen(spline.StartScale, spline.StartRoll, start.Grow),
            EndScale = Widen(spline.EndScale, spline.EndRoll, end.Grow),
            StartOffset = spline.StartOffset + new Vector2(start.Shift, 0f),
            EndOffset = spline.EndOffset + new Vector2(end.Shift, 0f),
        };
    }

    /// <summary>The cross-section at the start (or end) of <paramref name="spline"/> drawing a mesh with <paramref name="meshBounds"/>.</summary>
    public static EndSection Section(SplineMeshParams spline, BoundingBox meshBounds, bool atEnd)
    {
        var f = Frame(spline, meshBounds, atEnd);
        var middle = f.Point + (f.Scale * f.CentreCoordinate * f.Slope) * f.Right;
        var half = MathF.Abs(f.Scale * f.Slope) * (f.MaxCoordinate - f.MinCoordinate) * 0.5f;
        return new EndSection(middle, middle - (f.Right * half), middle + (f.Right * half), atEnd ? f.Direction : -f.Direction, half * 2f);
    }

    /// <summary>
    /// The end edit after the right (or left) corner of one end is dragged sideways to <paramref name="point"/>: that edge
    /// goes there, the other edge of the end stays. At least 10 cm wide, never turned inside out.
    /// </summary>
    /// <param name="shaped">The piece as drawn now (its frame at the end).</param>
    /// <param name="raw">The piece before any end edit (its own width and offset).</param>
    /// <param name="current">The end's edit now.</param>
    /// <param name="meshBounds">The mesh's bounds.</param>
    /// <param name="atEnd">The end (true) or the start.</param>
    /// <param name="rightSide">The right corner (seen walking from the start to the end) or the left one.</param>
    /// <param name="point">Where the corner was dragged (component space; only its sideways distance counts).</param>
    public static SplineEnd DragCorner(SplineMeshParams shaped, SplineMeshParams raw, SplineEnd current, BoundingBox meshBounds, bool atEnd, bool rightSide, FVector point)
    {
        var now = Frame(shaped, meshBounds, atEnd);
        var made = Frame(raw, meshBounds, atEnd);
        var span = now.MaxCoordinate - now.MinCoordinate;
        if (span < 1e-3f || MathF.Abs(made.Scale * made.Slope) < 1e-6f)
        {
            return current;
        }

        // Sideways positions are linear in the mesh's cross coordinate c: offset + c·m, m = width scale × roll slope.
        var maxSide = rightSide == (now.Scale * now.Slope > 0f);
        var dragged = maxSide ? now.MaxCoordinate : now.MinCoordinate;
        var kept = maxSide ? now.MinCoordinate : now.MaxCoordinate;
        var keptAt = now.Offset + (kept * now.Scale * now.Slope);
        var target = FVector.Dot(point - now.Point, now.Right) + now.Offset;
        var madeSlope = made.Scale * made.Slope;
        var slope = (target - keptAt) / (dragged - kept);
        if (MathF.Sign(slope) != MathF.Sign(madeSlope) || MathF.Abs(slope) * span < 10f)
        {
            slope = MathF.Sign(madeSlope) * 10f / span;
        }

        var offset = keptAt - (kept * slope);
        return current with { Grow = (slope / madeSlope) - 1f, Shift = offset - made.Offset };
    }

    /// <summary>
    /// The end edit that welds one end onto another piece's end: the middles meet, the piece leaves the weld the way the
    /// other comes in (no kink), and the end is as wide as the other. The handle push next to this end should be zero
    /// (it would turn the end again).
    /// </summary>
    /// <param name="raw">The piece before any end edit.</param>
    /// <param name="start">The start's edit now.</param>
    /// <param name="end">The end's edit now.</param>
    /// <param name="meshBounds">The mesh's bounds.</param>
    /// <param name="atEnd">Which end welds.</param>
    /// <param name="target">The other piece's end, in this piece's component space.</param>
    public static SplineEnd Weld(SplineMeshParams raw, SplineEnd start, SplineEnd end, BoundingBox meshBounds, bool atEnd, EndSection target)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var wanted = atEnd ? -target.Outward : target.Outward; // the tangent's way at this end
        var (heading, elevation) = Angles(atEnd ? raw.EndTangent : raw.StartTangent);
        var (wantedHeading, wantedElevation) = Angles(wanted);
        var made = Frame(raw, meshBounds, atEnd);
        var madeWidth = MathF.Abs(made.Scale * made.Slope) * (made.MaxCoordinate - made.MinCoordinate);
        var current = atEnd ? end : start;
        var trial = current with
        {
            Yaw = Wrap(wantedHeading - heading),
            Pitch = Math.Clamp(wantedElevation - elevation, -89f, 89f),
            Grow = madeWidth > 1f && target.Width > 1f ? (target.Width / madeWidth) - 1f : 0f,
            Shift = 0f,
            Lift = 0f,
        };
        var shaped = atEnd ? Apply(raw, start, trial) : Apply(raw, trial, end);
        var section = Section(shaped, meshBounds, atEnd);
        return trial with { Move = trial.Move + (target.Middle - section.Middle) };
    }

    /// <summary>The cross-section frame at one end and the mesh's extent across the piece (on whichever cross axis lies flat).</summary>
    private static (FVector Point, FVector Direction, FVector Right, float Offset, float Scale, float Slope, float MinCoordinate, float MaxCoordinate, float CentreCoordinate)
        Frame(SplineMeshParams spline, BoundingBox meshBounds, bool atEnd)
    {
        var position = atEnd ? spline.EndPos : spline.StartPos;
        var tangent = atEnd ? spline.EndTangent : spline.StartTangent;
        var direction = tangent.IsNearlyZero() ? (spline.EndPos - spline.StartPos).GetSafeNormal() : tangent.GetSafeNormal();
        var right = FVector.Cross(spline.SplineUpDir, direction).GetSafeNormal();
        if (right.IsNearlyZero())
        {
            right = FVector.Right;
        }

        var up = FVector.Cross(direction, right).GetSafeNormal();
        var offset = atEnd ? spline.EndOffset : spline.StartOffset;
        var roll = atEnd ? spline.EndRoll : spline.StartRoll;
        var scale = atEnd ? spline.EndScale : spline.StartScale;
        var axis = WidthAxis(roll);
        var (sin, cos) = MathF.SinCos(roll);
        var (crossX, crossY) = CrossAxes(spline.ForwardAxis);
        var meshAxis = axis == 0 ? crossX : crossY;
        var min = FVector.Dot(new FVector(meshBounds.Min.X, meshBounds.Min.Y, meshBounds.Min.Z), meshAxis);
        var max = FVector.Dot(new FVector(meshBounds.Max.X, meshBounds.Max.Y, meshBounds.Max.Z), meshAxis);
        return (position + (offset.X * right) + (offset.Y * up), direction, right, offset.X, axis == 0 ? scale.X : scale.Y,
            axis == 0 ? cos : sin, min, max, (min + max) * 0.5f);
    }

    /// <summary>Which cross-section axis lies across the piece (0 = the frame's X, the right, unless rolled about a quarter turn).</summary>
    private static int WidthAxis(float roll)
    {
        var (sin, cos) = MathF.SinCos(roll);
        return MathF.Abs(cos) >= MathF.Abs(sin) ? 0 : 1;
    }

    /// <summary>The mesh axes laid on the frame's X and Y for each forward axis (the shader's SplineMeshX/Y masks).</summary>
    private static (FVector X, FVector Y) CrossAxes(SplineMeshAxis forward) => forward switch
    {
        SplineMeshAxis.Y => (FVector.Up, FVector.Forward),
        SplineMeshAxis.Z => (FVector.Forward, FVector.Right),
        _ => (FVector.Right, FVector.Up),
    };

    private static Vector2 Widen(Vector2 scale, float roll, float grow) =>
        grow == 0f ? scale : WidthAxis(roll) == 0 ? scale with { X = scale.X * (1f + grow) } : scale with { Y = scale.Y * (1f + grow) };

    /// <summary><paramref name="v"/> turned right by <paramref name="yaw"/> and up by <paramref name="pitch"/> degrees, same length.</summary>
    private static FVector Turn(FVector v, float yaw, float pitch)
    {
        if (yaw == 0f && pitch == 0f)
        {
            return v;
        }

        var (heading, elevation) = Angles(v);
        var (sh, ch) = MathF.SinCos((heading + yaw) * MathF.PI / 180f);
        var (se, ce) = MathF.SinCos(Math.Clamp(elevation + pitch, -89.9f, 89.9f) * MathF.PI / 180f);
        return new FVector(ce * ch, ce * sh, se) * v.Size();
    }

    /// <summary>Heading (degrees right of +X) and elevation (degrees up) of a direction.</summary>
    private static (float Heading, float Elevation) Angles(FVector v) =>
        (MathF.Atan2(v.Y, v.X) * 180f / MathF.PI, MathF.Atan2(v.Z, v.Size2D()) * 180f / MathF.PI);

    private static float Wrap(float degrees)
    {
        var d = degrees % 360f;
        return d > 180f ? d - 360f : d <= -180f ? d + 360f : d;
    }
}
