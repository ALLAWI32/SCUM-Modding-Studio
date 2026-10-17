using System.Globalization;
using System.Numerics;
using System.Text;

namespace ScumStudio.Viewport.Decals;

/// <summary>
/// Smooth vector outlines for the decal library (owner, 2026-10-17: "the stickers look primitive, digits like a clock"):
/// closed polygons sampled finely from Bézier curves, ellipse arcs and closed splines, strokes with round caps and joins,
/// all wound the same way so that overlapping pieces join (<see cref="FillRule.NonZero"/>) and a reversed outline cuts a
/// hole. The rasterizer (<see cref="PathRaster"/>, 4 x 4 samples a pixel) anti-aliases every edge.
/// </summary>
internal sealed class Ink
{
    private readonly List<Vector2[]> _polygons = [];

    /// <summary>Applied to every point added from now on (a design drawn in its own box and placed in another).</summary>
    public Matrix3x2 T { get; set; } = Matrix3x2.Identity;

    /// <summary>The outlines so far (after <see cref="T"/>).</summary>
    public IReadOnlyList<Vector2[]> Polygons => _polygons;

    /// <summary>A filled outline (wound so that it joins whatever it overlaps).</summary>
    public Ink Fill(IEnumerable<Vector2> points) => Add(points, hole: false);

    /// <summary>An outline cut out of the filled shape it lies in.</summary>
    public Ink Hole(IEnumerable<Vector2> points) => Add(points, hole: true);

    /// <summary>A disc.</summary>
    public Ink Circle(Vector2 centre, float radius) => Fill(Curve.Ellipse(centre, radius, radius));

    /// <summary>An ellipse.</summary>
    public Ink Ellipse(Vector2 centre, float rx, float ry, float rotationDeg = 0f) => Fill(Curve.Ellipse(centre, rx, ry, rotationDeg));

    /// <summary>A ring between two radii.</summary>
    public Ink Ring(Vector2 centre, float inner, float outer) => Circle(centre, outer).Hole(Curve.Ellipse(centre, inner, inner));

    /// <summary>A rectangle with rounded corners.</summary>
    public Ink RoundRect(float x, float y, float w, float h, float r) => Fill(Curve.RoundRect(x, y, w, h, r));

    /// <summary>A star of <paramref name="points"/> points, its corners rounded by <paramref name="rounding"/> (0 = sharp).</summary>
    public Ink Star(Vector2 centre, float outer, float inner, int points, float rotationDeg = -90f, float rounding = 0f) =>
        Fill(Curve.Star(centre, outer, inner, points, rotationDeg, rounding));

    /// <summary>
    /// A line <paramref name="width"/> wide along <paramref name="line"/> (a polyline sampled from curves): smooth runs are one
    /// outline, corners sharper than 25° and both ends get a round cap (or a square end).
    /// </summary>
    public Ink Stroke(IReadOnlyList<Vector2> line, float width, bool round = true)
    {
        ArgumentNullException.ThrowIfNull(line);
        var hw = width / 2f;
        var points = Curve.Dedupe(line);
        if (points.Count < 2)
        {
            return points.Count == 1 && round ? Circle(points[0], hw) : this;
        }

        var run = new List<Vector2> { points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            run.Add(points[i]);
            var sharp = i < points.Count - 1 && Turn(points[i - 1], points[i], points[i + 1]) > 25f;
            if (sharp)
            {
                Band(run, hw);
                Circle(points[i], hw); // a round join
                run = [points[i]];
            }
        }

        Band(run, hw);
        if (round)
        {
            Circle(points[0], hw);
            Circle(points[^1], hw);
        }

        return this;
    }

    /// <summary>A closed line <paramref name="width"/> wide round <paramref name="loop"/> (a ring of any smooth shape).</summary>
    public Ink StrokeClosed(IReadOnlyList<Vector2> loop, float width)
    {
        var (a, b) = (Curve.Offset(loop, width / 2f, closed: true), Curve.Offset(loop, -width / 2f, closed: true));
        var (outer, inner) = MathF.Abs(Curve.Area(a)) >= MathF.Abs(Curve.Area(b)) ? (a, b) : (b, a);
        return Fill(outer).Hole(inner);
    }

    /// <summary>
    /// A shape that tapers along <paramref name="spine"/>: its half width at fraction t of the way is
    /// <paramref name="halfWidth"/>(t) (a flame tongue, a tribal blade, a horn).
    /// </summary>
    public Ink Taper(IReadOnlyList<Vector2> spine, Func<float, float> halfWidth)
    {
        ArgumentNullException.ThrowIfNull(halfWidth);
        var pts = Curve.Dedupe(spine);
        if (pts.Count < 2)
        {
            return this;
        }

        var left = new List<Vector2>(pts.Count);
        var right = new List<Vector2>(pts.Count);
        var length = Curve.Length(pts);
        var run = 0f;
        for (var i = 0; i < pts.Count; i++)
        {
            run += i == 0 ? 0f : Vector2.Distance(pts[i - 1], pts[i]);
            var dir = Vector2.Normalize(pts[Math.Min(i + 1, pts.Count - 1)] - pts[Math.Max(i - 1, 0)]);
            var n = new Vector2(-dir.Y, dir.X) * MathF.Max(0f, halfWidth(length <= 0f ? 0f : run / length));
            left.Add(pts[i] + n);
            right.Add(pts[i] - n);
        }

        right.Reverse();
        return Fill([.. left, .. right]);
    }

    /// <summary>Every outline of <paramref name="other"/> (already placed) added to this.</summary>
    public Ink Merge(Ink other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _polygons.AddRange(other._polygons);
        return this;
    }

    /// <summary>The outlines as SVG path data (absolute <c>M L Z</c>, two decimals).</summary>
    public override string ToString()
    {
        var s = new StringBuilder();
        foreach (var poly in _polygons)
        {
            for (var i = 0; i < poly.Length; i++)
            {
                s.Append(i == 0 ? 'M' : 'L').Append(poly[i].X.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(poly[i].Y.ToString("0.##", CultureInfo.InvariantCulture)).Append(' ');
            }

            s.Append("Z ");
        }

        return s.ToString().TrimEnd();
    }

    private static float Turn(Vector2 a, Vector2 b, Vector2 c)
    {
        var (u, v) = (b - a, c - b);
        if (u.LengthSquared() < 1e-8f || v.LengthSquared() < 1e-8f)
        {
            return 0f;
        }

        var cos = Math.Clamp(Vector2.Dot(Vector2.Normalize(u), Vector2.Normalize(v)), -1f, 1f);
        return MathF.Acos(cos) * 180f / MathF.PI;
    }

    private void Band(List<Vector2> run, float hw)
    {
        if (run.Count < 2)
        {
            return;
        }

        var left = Curve.Offset(run, hw, closed: false);
        var right = Curve.Offset(run, -hw, closed: false);
        right.Reverse();
        Fill([.. left, .. right]);
    }

    private Ink Add(IEnumerable<Vector2> points, bool hole)
    {
        var poly = points.Select(p => Vector2.Transform(p, T)).Where(p => float.IsFinite(p.X) && float.IsFinite(p.Y)).ToArray(); // a degenerate normal never reaches the path data
        if (poly.Length < 3)
        {
            return this;
        }

        // Positive area (in y-down view-box units) is "filled"; a hole goes the other way round.
        if ((Curve.Area(poly) < 0f) != hole)
        {
            Array.Reverse(poly);
        }

        _polygons.Add(poly);
        return this;
    }
}

/// <summary>Curves sampled into points (view-box units, y down).</summary>
internal static class Curve
{
    /// <summary>A cubic Bézier from <paramref name="p0"/> to <paramref name="p3"/>, <paramref name="n"/> + 1 points.</summary>
    public static Vector2[] Bezier(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, int n = 32)
    {
        var pts = new Vector2[n + 1];
        for (var i = 0; i <= n; i++)
        {
            var t = i / (float)n;
            var u = 1f - t;
            pts[i] = (u * u * u * p0) + (3f * u * u * t * p1) + (3f * u * t * t * p2) + (t * t * t * p3);
        }

        return pts;
    }

    /// <summary>An ellipse arc from <paramref name="fromDeg"/> to <paramref name="toDeg"/> (0° = +x, 90° = +y, i.e. down).</summary>
    public static Vector2[] Arc(Vector2 centre, float rx, float ry, float fromDeg, float toDeg, int n = 48)
    {
        var pts = new Vector2[n + 1];
        for (var i = 0; i <= n; i++)
        {
            var a = (fromDeg + ((toDeg - fromDeg) * i / n)) * MathF.PI / 180f;
            pts[i] = centre + new Vector2(MathF.Cos(a) * rx, MathF.Sin(a) * ry);
        }

        return pts;
    }

    /// <summary>A whole ellipse (closed: the last point is not repeated), turned by <paramref name="rotationDeg"/>.</summary>
    public static Vector2[] Ellipse(Vector2 centre, float rx, float ry, float rotationDeg = 0f, int n = 96)
    {
        var turn = Matrix3x2.CreateRotation(rotationDeg * MathF.PI / 180f);
        return [.. Arc(Vector2.Zero, rx, ry, 0f, 360f, n).Take(n).Select(p => Vector2.Transform(p, turn) + centre)];
    }

    /// <summary>A rectangle with rounded corners (closed).</summary>
    public static Vector2[] RoundRect(float x, float y, float w, float h, float r)
    {
        r = MathF.Min(r, MathF.Min(w, h) / 2f);
        if (r <= 0f)
        {
            return [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];
        }

        return
        [
            .. Arc(new(x + w - r, y + r), r, r, -90f, 0f, 12),
            .. Arc(new(x + w - r, y + h - r), r, r, 0f, 90f, 12),
            .. Arc(new(x + r, y + h - r), r, r, 90f, 180f, 12),
            .. Arc(new(x + r, y + r), r, r, 180f, 270f, 12),
        ];
    }

    /// <summary>A star outline; <paramref name="rounding"/> &gt; 0 rounds its corners (a spline through them).</summary>
    public static Vector2[] Star(Vector2 centre, float outer, float inner, int points, float rotationDeg, float rounding = 0f)
    {
        var corners = new Vector2[points * 2];
        for (var i = 0; i < corners.Length; i++)
        {
            var a = (rotationDeg + (i * 180f / points)) * MathF.PI / 180f;
            var r = i % 2 == 0 ? outer : inner;
            corners[i] = centre + new Vector2(MathF.Cos(a) * r, MathF.Sin(a) * r);
        }

        return rounding <= 0f ? corners : Rounded(corners, rounding);
    }

    /// <summary>A polygon whose corners are cut by quadratic curves <paramref name="radius"/> long on each side.</summary>
    public static Vector2[] Rounded(IReadOnlyList<Vector2> corners, float radius, int steps = 8)
    {
        var pts = new List<Vector2>();
        for (var i = 0; i < corners.Count; i++)
        {
            var (prev, at, next) = (corners[(i + corners.Count - 1) % corners.Count], corners[i], corners[(i + 1) % corners.Count]);
            var r = MathF.Min(radius, MathF.Min(Vector2.Distance(prev, at), Vector2.Distance(at, next)) * 0.45f);
            var (a, b) = (at + (Vector2.Normalize(prev - at) * r), at + (Vector2.Normalize(next - at) * r));
            for (var s = 0; s <= steps; s++)
            {
                var t = s / (float)steps;
                pts.Add(((1 - t) * (1 - t) * a) + (2 * (1 - t) * t * at) + (t * t * b));
            }
        }

        return [.. pts];
    }

    /// <summary>A smooth closed curve through <paramref name="points"/> (Catmull-Rom), <paramref name="perSpan"/> points between two of them.</summary>
    public static Vector2[] Spline(IReadOnlyList<Vector2> points, int perSpan = 12, bool closed = true)
    {
        var pts = new List<Vector2>();
        var n = points.Count;
        var spans = closed ? n : n - 1;
        Vector2 P(int i) => closed ? points[((i % n) + n) % n] : points[Math.Clamp(i, 0, n - 1)];
        for (var i = 0; i < spans; i++)
        {
            var (p0, p1, p2, p3) = (P(i - 1), P(i), P(i + 1), P(i + 2));
            for (var s = 0; s < perSpan; s++)
            {
                var t = s / (float)perSpan;
                var (t2, t3) = (t * t, t * t * t);
                pts.Add(0.5f * ((2f * p1) + ((p2 - p0) * t) + (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t2) + (((3f * p1) - p0 - (3f * p2) + p3) * t3)));
            }
        }

        if (!closed)
        {
            pts.Add(points[^1]);
        }

        return [.. pts];
    }

    /// <summary>Joins pieces of one line (a point repeated where two pieces meet is kept once).</summary>
    public static Vector2[] Join(params Vector2[][] pieces) => [.. Dedupe(pieces.SelectMany(p => p).ToList())];

    /// <summary>The same points without one that repeats the one before it.</summary>
    public static List<Vector2> Dedupe(IReadOnlyList<Vector2> points)
    {
        var list = new List<Vector2>(points.Count);
        foreach (var p in points)
        {
            if (list.Count == 0 || Vector2.DistanceSquared(list[^1], p) > 1e-6f)
            {
                list.Add(p);
            }
        }

        return list;
    }

    /// <summary>Signed area (positive: clockwise on screen, y down).</summary>
    public static float Area(IReadOnlyList<Vector2> poly)
    {
        var a = 0f;
        for (var i = 0; i < poly.Count; i++)
        {
            var (p, q) = (poly[i], poly[(i + 1) % poly.Count]);
            a += (p.X * q.Y) - (q.X * p.Y);
        }

        return a / 2f;
    }

    /// <summary>Length of a polyline.</summary>
    public static float Length(IReadOnlyList<Vector2> line)
    {
        var l = 0f;
        for (var i = 1; i < line.Count; i++)
        {
            l += Vector2.Distance(line[i - 1], line[i]);
        }

        return l;
    }

    /// <summary>
    /// The line moved <paramref name="distance"/> to its left (negative: right), mitred at each point (a miter is at most
    /// twice the distance, so a tight turn does not spike).
    /// </summary>
    public static List<Vector2> Offset(IReadOnlyList<Vector2> line, float distance, bool closed)
    {
        var pts = Dedupe(line);
        if (closed && pts.Count > 2 && Vector2.DistanceSquared(pts[0], pts[^1]) < 1e-6f)
        {
            pts.RemoveAt(pts.Count - 1);
        }

        var n = pts.Count;
        var result = new List<Vector2>(n);
        for (var i = 0; i < n; i++)
        {
            Vector2 Dir(int a, int b) => Vector2.Normalize(pts[b] - pts[a]);
            Vector2 Left(Vector2 d) => new(-d.Y, d.X);
            Vector2 normal;
            if (!closed && i == 0)
            {
                normal = Left(Dir(0, 1));
            }
            else if (!closed && i == n - 1)
            {
                normal = Left(Dir(n - 2, n - 1));
            }
            else
            {
                var (before, after) = (Left(Dir((i + n - 1) % n, i)), Left(Dir(i, (i + 1) % n)));
                var mid = before + after;
                normal = mid.LengthSquared() < 1e-6f ? after : Vector2.Normalize(mid) / MathF.Max(0.5f, Vector2.Dot(Vector2.Normalize(mid), after));
            }

            result.Add(pts[i] + (normal * distance));
        }

        return result;
    }

    /// <summary>Each point mirrored across the vertical line x = <paramref name="axis"/> (order reversed, so the winding stays).</summary>
    public static Vector2[] MirrorX(IEnumerable<Vector2> points, float axis) => [.. points.Select(p => new Vector2((2 * axis) - p.X, p.Y)).Reverse()];

    /// <summary>A convex-window clip (Sutherland-Hodgman) of <paramref name="poly"/> to the box 0..<paramref name="w"/> x 0..<paramref name="h"/>.</summary>
    public static Vector2[] ClipToBox(IReadOnlyList<Vector2> poly, float w, float h)
    {
        IEnumerable<Vector2> Edge(IReadOnlyList<Vector2> input, Func<Vector2, float> side)
        {
            for (var i = 0; i < input.Count; i++)
            {
                var (a, b) = (input[i], input[(i + 1) % input.Count]);
                var (da, db) = (side(a), side(b));
                if (da >= 0)
                {
                    yield return a;
                }

                if ((da >= 0) != (db >= 0))
                {
                    yield return a + ((b - a) * (da / (da - db)));
                }
            }
        }

        IReadOnlyList<Vector2> p = poly;
        foreach (var side in new Func<Vector2, float>[] { v => v.X, v => w - v.X, v => v.Y, v => h - v.Y })
        {
            p = Edge(p, side).ToList();
            if (p.Count == 0)
            {
                break;
            }
        }

        return [.. p];
    }
}
