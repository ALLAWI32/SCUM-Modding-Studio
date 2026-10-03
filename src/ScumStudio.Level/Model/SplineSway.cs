using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Model;

/// <summary>
/// Pushes a spline mesh sideways without moving its ends (owner: "handles along the bridge I push right or left; right,
/// left, right, left as I like"): the curve's two inner Bézier control points (a third of the way from each end) move
/// <c>sway1</c> / <c>sway2</c> cm to the right of the line from start to end (negative = left), and the tangents follow.
/// Same side bows the piece, opposite sides make an S; the ends stay where they are, so nothing comes apart from its
/// neighbours.
/// </summary>
public static class SplineSway
{
    /// <summary><paramref name="spline"/> with its inner control points pushed sideways (component space, cm).</summary>
    public static SplineMeshParams Apply(SplineMeshParams spline, float sway1, float sway2)
    {
        ArgumentNullException.ThrowIfNull(spline);
        if (sway1 == 0f && sway2 == 0f)
        {
            return spline;
        }

        var (p1, p2) = Controls(spline);
        var right = Right(spline);
        p1 += right * sway1;
        p2 += right * sway2;
        return spline with
        {
            StartTangent = (p1 - spline.StartPos) * 3f,
            EndTangent = (spline.EndPos - p2) * 3f,
        };
    }

    /// <summary>The inner Bézier control points of the curve (a third of each tangent from its end).</summary>
    public static (FVector First, FVector Second) Controls(SplineMeshParams spline)
    {
        ArgumentNullException.ThrowIfNull(spline);
        return (spline.StartPos + (spline.StartTangent / 3f), spline.EndPos - (spline.EndTangent / 3f));
    }

    /// <summary>The horizontal direction to the right of the line from the curve's start to its end (component space).</summary>
    public static FVector Right(SplineMeshParams spline)
    {
        ArgumentNullException.ThrowIfNull(spline);
        var chord = spline.EndPos - spline.StartPos;
        var right = FVector.Cross(FVector.Up, new FVector(chord.X, chord.Y, 0f)).GetSafeNormal();
        return right.IsNearlyZero() ? FVector.Right : right;
    }

    /// <summary>Length of the straight line from the curve's start to its end, cm.</summary>
    public static float Span(SplineMeshParams spline) => FVector.Distance(spline.StartPos, spline.EndPos);
}
