using System.Numerics;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Model;

/// <summary>
/// A piece made longer repeats instead of stretching (owner: "a longer fence should not get bigger gaps; put copy after
/// copy"): the curve is cut into as many pieces of about the mesh's own length as fit (equal lengths along the curve),
/// each drawn with the whole mesh. One piece becomes one <c>SplineMeshComponent</c> in the game.
/// </summary>
public static class SplineTiles
{
    /// <summary>Most pieces one curve is cut into.</summary>
    public const int MaxPieces = 64;

    /// <summary>
    /// <paramref name="spline"/> cut into round(length / <paramref name="pieceLength"/>) pieces of equal length along it
    /// (at least one; just <paramref name="spline"/> when one fits).
    /// </summary>
    public static IReadOnlyList<SplineMeshParams> Split(SplineMeshParams spline, float pieceLength)
    {
        ArgumentNullException.ThrowIfNull(spline);
        var table = LengthTable(spline, 256);
        var total = table[^1];
        var count = pieceLength > 1f ? Math.Clamp((int)MathF.Round(total / pieceLength), 1, MaxPieces) : 1;
        if (count == 1)
        {
            return [spline];
        }

        var cuts = new float[count + 1];
        for (var i = 0; i <= count; i++)
        {
            cuts[i] = ParameterAt(table, total * i / count);
        }

        var pieces = new SplineMeshParams[count];
        for (var i = 0; i < count; i++)
        {
            pieces[i] = Piece(spline, cuts[i], cuts[i + 1]);
        }

        return pieces;
    }

    /// <summary>Length of the curve, cm.</summary>
    public static float Length(SplineMeshParams spline) => LengthTable(spline, 256)[^1];

    /// <summary>The part of the curve between parameters <paramref name="t0"/> and <paramref name="t1"/>, as a curve of its own.</summary>
    private static SplineMeshParams Piece(SplineMeshParams s, float t0, float t1)
    {
        var span = t1 - t0;
        float Mix(float a, float b, float t) => a + ((b - a) * Alpha(s, t));
        Vector2 Mix2(Vector2 a, Vector2 b, float t) => a + ((b - a) * Alpha(s, t));
        return s with
        {
            StartPos = Position(s, t0),
            EndPos = Position(s, t1),
            StartTangent = Derivative(s, t0) * span,
            EndTangent = Derivative(s, t1) * span,
            StartScale = Mix2(s.StartScale, s.EndScale, t0),
            EndScale = Mix2(s.StartScale, s.EndScale, t1),
            StartOffset = Mix2(s.StartOffset, s.EndOffset, t0),
            EndOffset = Mix2(s.StartOffset, s.EndOffset, t1),
            StartRoll = Mix(s.StartRoll, s.EndRoll, t0),
            EndRoll = Mix(s.StartRoll, s.EndRoll, t1),
            SmoothInterpRollScale = false,
        };
    }

    // Roll, scale and offset follow a smooth-step of the parameter when the component asks for it (as the slice frame does).
    private static float Alpha(SplineMeshParams s, float t) => s.SmoothInterpRollScale ? t * t * (3f - (2f * t)) : t;

    private static FVector Position(SplineMeshParams s, float t)
    {
        var t2 = t * t;
        var t3 = t2 * t;
        return ((2 * t3) - (3 * t2) + 1) * s.StartPos + (t3 - (2 * t2) + t) * s.StartTangent + (t3 - t2) * s.EndTangent + ((-2 * t3) + (3 * t2)) * s.EndPos;
    }

    private static FVector Derivative(SplineMeshParams s, float t)
    {
        var t2 = t * t;
        return ((6 * t2) - (6 * t)) * s.StartPos + ((3 * t2) - (4 * t) + 1) * s.StartTangent + ((3 * t2) - (2 * t)) * s.EndTangent + ((-6 * t2) + (6 * t)) * s.EndPos;
    }

    /// <summary>Length along the curve at <paramref name="steps"/> + 1 even parameters (first 0, last the whole length).</summary>
    private static float[] LengthTable(SplineMeshParams s, int steps)
    {
        var table = new float[steps + 1];
        var previous = s.StartPos;
        for (var i = 1; i <= steps; i++)
        {
            var point = Position(s, (float)i / steps);
            table[i] = table[i - 1] + FVector.Distance(previous, point);
            previous = point;
        }

        return table;
    }

    /// <summary>The parameter where the curve has run <paramref name="length"/> cm (linear between table steps).</summary>
    private static float ParameterAt(float[] table, float length)
    {
        var steps = table.Length - 1;
        var i = Array.BinarySearch(table, length);
        if (i >= 0)
        {
            return (float)i / steps;
        }

        i = Math.Clamp(~i, 1, steps);
        var (a, b) = (table[i - 1], table[i]);
        var f = b > a ? (length - a) / (b - a) : 0f;
        return (i - 1 + f) / steps;
    }
}
