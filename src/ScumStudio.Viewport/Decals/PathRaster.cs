using System.Globalization;
using System.Numerics;

namespace ScumStudio.Viewport.Decals;

/// <summary>Which parts of a path are inside: SVG's <c>fill-rule</c>.</summary>
public enum FillRule
{
    /// <summary>Inside where the outlines wind round the point (a hole needs an outline the other way round).</summary>
    NonZero,

    /// <summary>Inside where a line out of the point crosses an odd number of outlines (any inner outline is a hole).</summary>
    EvenOdd,
}

/// <summary>
/// Fills SVG path data (<c>M L H V C Q Z</c>, upper case absolute, lower case relative) into a coverage plane: curves
/// are flattened in pixel space and every pixel is sampled 4 x 4 times, so edges are anti-aliased and a square on whole
/// pixels covers exactly its area.
/// </summary>
public static class PathRaster
{
    private const int Sub = 4;

    /// <summary>Coverage 0..1 per pixel (row-major) of <paramref name="data"/> drawn through <paramref name="transform"/> into a <paramref name="width"/> x <paramref name="height"/> plane.</summary>
    public static float[] Fill(string data, int width, int height, Matrix3x2 transform, FillRule rule = FillRule.NonZero) =>
        Fill(Flatten(data, transform), width, height, rule);

    /// <summary>Coverage 0..1 per pixel of closed polygons (pixel coordinates).</summary>
    public static float[] Fill(IReadOnlyList<Vector2[]> polygons, int width, int height, FillRule rule = FillRule.NonZero)
    {
        ArgumentNullException.ThrowIfNull(polygons);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        var edges = new List<(float X0, float Y0, float X1, float Y1, int Dir)>();
        foreach (var poly in polygons)
        {
            for (var i = 0; i < poly.Length; i++)
            {
                var (a, b) = (poly[i], poly[(i + 1) % poly.Length]);
                if (a.Y != b.Y)
                {
                    edges.Add(a.Y < b.Y ? (a.X, a.Y, b.X, b.Y, 1) : (b.X, b.Y, a.X, a.Y, -1));
                }
            }
        }

        edges.Sort((p, q) => p.Y0.CompareTo(q.Y0));
        var counts = new int[width * height];
        var active = new List<int>();
        var crossings = new List<(float X, int Dir)>();
        var next = 0;
        for (var sy = 0; sy < height * Sub; sy++)
        {
            var y = (sy + 0.5f) / Sub;
            while (next < edges.Count && edges[next].Y0 <= y)
            {
                active.Add(next++);
            }

            active.RemoveAll(e => edges[e].Y1 <= y);
            crossings.Clear();
            foreach (var e in active)
            {
                var (x0, y0, x1, y1, dir) = edges[e];
                crossings.Add((x0 + ((y - y0) * (x1 - x0) / (y1 - y0)), dir));
            }

            crossings.Sort((p, q) => p.X.CompareTo(q.X));
            var row = (sy / Sub) * width;
            var winding = 0;
            for (var k = 0; k + 1 < crossings.Count; k++)
            {
                winding += crossings[k].Dir;
                if (rule == FillRule.NonZero ? winding != 0 : (k & 1) == 0)
                {
                    Span(counts, row, width, crossings[k].X, crossings[k + 1].X);
                }
            }
        }

        var coverage = new float[counts.Length];
        for (var i = 0; i < counts.Length; i++)
        {
            coverage[i] = Math.Min(counts[i], Sub * Sub) / (float)(Sub * Sub);
        }

        return coverage;
    }

    /// <summary>Counts the sub-samples (centres at (s + 0.5) / 4) of one sub-scanline that lie in [x0, x1).</summary>
    private static void Span(int[] counts, int row, int width, float x0, float x1)
    {
        var s0 = Math.Max(0, (int)MathF.Ceiling((x0 * Sub) - 0.5f));
        var s1 = Math.Min(width * Sub, (int)MathF.Ceiling((x1 * Sub) - 0.5f));
        var s = s0;
        while (s < s1 && s % Sub != 0)
        {
            counts[row + (s++ / Sub)]++;
        }

        while (s + Sub <= s1)
        {
            counts[row + (s / Sub)] += Sub;
            s += Sub;
        }

        while (s < s1)
        {
            counts[row + (s++ / Sub)]++;
        }
    }

    /// <summary>The closed outlines of <paramref name="data"/> through <paramref name="transform"/>, curves cut into segments of about 2 px.</summary>
    public static List<Vector2[]> Flatten(string data, Matrix3x2 transform)
    {
        ArgumentNullException.ThrowIfNull(data);
        var polygons = new List<Vector2[]>();
        var current = new List<Vector2>();
        var tokens = Tokens(data);
        var (pos, start) = (Vector2.Zero, Vector2.Zero);
        var k = 0;
        var command = 'M';
        void Close()
        {
            if (current.Count >= 3)
            {
                polygons.Add([.. current]);
            }

            current = [];
        }

        Vector2 P(Vector2 p) => Vector2.Transform(p, transform);
        float Number() => k < tokens.Count && tokens[k] is { } t && !char.IsLetter(t[0]) ? float.Parse(tokens[k++], CultureInfo.InvariantCulture)
            : throw new FormatException($"Path data: a number expected at token {k}.");
        Vector2 Point(bool relative) => new Vector2(Number(), Number()) + (relative ? pos : Vector2.Zero);

        while (k < tokens.Count)
        {
            if (char.IsLetter(tokens[k][0]))
            {
                command = tokens[k++][0];
            }

            var relative = char.IsLower(command);
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    Close();
                    pos = start = Point(relative);
                    current.Add(P(pos));
                    command = relative ? 'l' : 'L'; // more pairs after a move are lines
                    break;
                case 'L':
                    pos = Point(relative);
                    current.Add(P(pos));
                    break;
                case 'H':
                    pos = new Vector2(Number() + (relative ? pos.X : 0), pos.Y);
                    current.Add(P(pos));
                    break;
                case 'V':
                    pos = new Vector2(pos.X, Number() + (relative ? pos.Y : 0));
                    current.Add(P(pos));
                    break;
                case 'C':
                    {
                        var (c1, c2, end) = (Point(relative), Point(relative), Point(relative));
                        Curve(current, P(pos), P(c1), P(c2), P(end));
                        pos = end;
                        break;
                    }

                case 'Q':
                    {
                        var (c, end) = (Point(relative), Point(relative));
                        Curve(current, P(pos), P(pos + ((c - pos) * 2f / 3f)), P(end + ((c - end) * 2f / 3f)), P(end));
                        pos = end;
                        break;
                    }

                case 'Z':
                    Close();
                    pos = start;
                    if (k < tokens.Count && !char.IsLetter(tokens[k][0]))
                    {
                        throw new FormatException("Path data: numbers after Z.");
                    }

                    break;
                default:
                    throw new FormatException($"Path data: '{command}' is not supported (M L H V C Q Z).");
            }
        }

        Close();
        return polygons;
    }

    private static void Curve(List<Vector2> into, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        var length = Vector2.Distance(p0, p1) + Vector2.Distance(p1, p2) + Vector2.Distance(p2, p3);
        var n = Math.Clamp((int)MathF.Ceiling(length / 2f), 2, 256);
        for (var i = 1; i <= n; i++)
        {
            var t = i / (float)n;
            var u = 1f - t;
            into.Add((u * u * u * p0) + (3f * u * u * t * p1) + (3f * u * t * t * p2) + (t * t * t * p3));
        }
    }

    private static List<string> Tokens(string data)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < data.Length)
        {
            var c = data[i];
            if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
            }
            else if (char.IsLetter(c) && c is not ('e' or 'E'))
            {
                tokens.Add(c.ToString());
                i++;
            }
            else
            {
                var s = i;
                var dot = false;
                i += c is '-' or '+' ? 1 : 0;
                while (i < data.Length && (char.IsDigit(data[i]) || (data[i] == '.' && !dot)
                                            || (data[i] is 'e' or 'E' && i + 1 < data.Length && (char.IsDigit(data[i + 1]) || data[i + 1] == '-'))))
                {
                    if (data[i] is 'e' or 'E')
                    {
                        i += 2;
                        continue;
                    }

                    dot |= data[i] == '.';
                    i++;
                }

                if (i == s)
                {
                    throw new FormatException($"Path data: unexpected '{c}' at {i}.");
                }

                tokens.Add(data[s..i]);
            }
        }

        return tokens;
    }
}
