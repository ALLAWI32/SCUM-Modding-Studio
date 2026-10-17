using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Drives off a bridge sideways, on paper (Discord Blondiiile, 2026-10-16: "I got too close to the edge and fell through,
/// the bridge railing has no collision"): at slices along the mesh, a level line from the middle of the deck out past
/// each side at bumper and hip height. Where the mesh's own triangles stop it, a railing stands there; the collision
/// boxes must stop it there too (and nowhere earlier: no hidden wall on the road). Probes are made in the mesh's own
/// space and carried onto a bent piece by the slice the game draws there.
/// </summary>
internal static class RailingProbe
{
    /// <summary>One sideways line: where along the mesh, which side, how high over the deck, and its ends (mesh space).</summary>
    public sealed record Probe(float Along, int Side, float Height, FVector From, FVector To);

    /// <summary>A probe and where along it (0..1) the mesh and the boxes stop it, or null.</summary>
    public sealed record Result(Probe Probe, float? Mesh, float? Boxes)
    {
        /// <summary>The mesh stops the line here (a railing or wall) and the boxes stop it no more than 30 cm past it.</summary>
        public bool Covered(float length) => Mesh is null || (Boxes is { } b && (b - Mesh.Value) * length <= 30f);
    }

    /// <summary>The mesh's long axis as the bend uses it (its longer horizontal one).</summary>
    public static int LongAxis(BoundingBox b) => b.Size.Y > b.Size.X ? 1 : 0;

    /// <summary>
    /// Probes over the deck: <paramref name="slices"/> along the long axis (5 % in from each end), both sides, at
    /// <paramref name="heights"/> cm over the deck top found there (the highest face up with 2 m free over it).
    /// </summary>
    public static List<Probe> Probes(MeshData mesh, int slices = 40, params float[] heights)
    {
        heights = heights.Length > 0 ? heights : [.. Enumerable.Range(1, 10).Select(i => i * 15f)];
        var b = mesh.Bounds;
        var k = LongAxis(b);
        var j = 1 - k;
        var (min, max) = (Get(b.Min, k), Get(b.Max, k));
        var mid = (Get(b.Min, j) + Get(b.Max, j)) * 0.5f;
        var half = (Get(b.Max, j) - Get(b.Min, j)) * 0.5f;
        var probes = new List<Probe>();
        for (var i = 0; i < slices; i++)
        {
            var a = min + ((max - min) * (0.05f + (0.9f * i / Math.Max(1, slices - 1))));
            if (DeckTop(mesh, k, a, mid, half) is not { } top)
            {
                continue;
            }

            foreach (var side in new[] { -1, 1 })
            {
                foreach (var h in heights)
                {
                    var from = Point(k, a, mid, top + h);
                    var to = Point(k, a, mid + (side * (half + 100f)), top + h);
                    probes.Add(new Probe(a, side, h, from, to));
                }
            }
        }

        return probes;
    }

    /// <summary>Where the deck's driving surface is at <paramref name="a"/> along the mesh, or null where it has none.</summary>
    public static float? DeckTop(MeshData mesh, int k, float a, float mid, float half)
    {
        foreach (var off in new[] { 0f, -0.25f, 0.25f })
        {
            var (x, y) = k == 0 ? (a, mid + (off * half)) : (mid + (off * half), a);
            if (TopAt(mesh, x, y) is { } top)
            {
                return top;
            }
        }

        return null;
    }

    /// <summary>Each probe against the mesh's triangles and against <paramref name="boxes"/> in the same space.</summary>
    public static List<Result> Run(MeshData mesh, IReadOnlyList<Probe> probes, IReadOnlyList<CollisionBox> boxes) =>
        probes.Select(q => new Result(q, FirstTriangle(mesh, q.From, q.To), FirstBox(boxes, q.From, q.To))).ToList();

    /// <summary>
    /// The same for a bent piece: the mesh's triangles stop the probe where they do in mesh space, the boxes (component
    /// space, as written) are met by the probe carried by the slice at its place along the curve.
    /// </summary>
    public static List<Result> RunBent(MeshData mesh, IReadOnlyList<Probe> probes, IReadOnlyList<CollisionBox> boxes, SplineMeshParams spline, BoundingBox bounds)
    {
        var axis = spline.ForwardAxis switch { SplineMeshAxis.Y => FVector.Right, SplineMeshAxis.Z => FVector.Up, _ => FVector.Forward };
        return probes.Select(q =>
        {
            var a = FVector.Dot(q.From, axis);
            var slice = SplineMeshDeformer.CalcSliceTransform(spline, bounds, a);
            var from = slice.TransformPosition(q.From - (axis * a));
            var to = slice.TransformPosition(q.To - (axis * a));
            return new Result(q, FirstTriangle(mesh, q.From, q.To), FirstBox(boxes, from, to));
        }).ToList();
    }

    /// <summary>Where (0..1) the segment first meets a triangle of <paramref name="mesh"/>, or null.</summary>
    public static float? FirstTriangle(MeshData mesh, FVector from, FVector to)
    {
        var p = mesh.Positions;
        var idx = mesh.Indices;
        var d = to - from;
        float? best = null;
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            var a = V(p, idx[t]);
            var e1 = V(p, idx[t + 1]) - a;
            var e2 = V(p, idx[t + 2]) - a;
            var h = FVector.Cross(d, e2);
            var det = FVector.Dot(e1, h);
            if (MathF.Abs(det) < 1e-9f)
            {
                continue;
            }

            var s = from - a;
            var u = FVector.Dot(s, h) / det;
            if (u is < 0f or > 1f)
            {
                continue;
            }

            var q = FVector.Cross(s, e1);
            var v = FVector.Dot(d, q) / det;
            if (v < 0f || u + v > 1f)
            {
                continue;
            }

            var f = FVector.Dot(e2, q) / det;
            if (f is >= 0f and <= 1f && (best is null || f < best))
            {
                best = f;
            }
        }

        return best;
    }

    /// <summary>Where (0..1) the segment first enters one of <paramref name="boxes"/> (0 when it starts inside one), or null.</summary>
    public static float? FirstBox(IReadOnlyList<CollisionBox> boxes, FVector from, FVector to)
    {
        float? best = null;
        foreach (var box in boxes)
        {
            var rotation = box.Rotation.Quaternion();
            var o = rotation.UnrotateVector(from - box.Center);
            var d = rotation.UnrotateVector(to - from);
            var (t0, t1) = (0f, 1f);
            var hit = true;
            for (var k = 0; k < 3 && hit; k++)
            {
                var half = Get(box.Size, k) * 0.5f;
                var (ok, dk) = (Get(o, k), Get(d, k));
                if (MathF.Abs(dk) < 1e-9f)
                {
                    hit = MathF.Abs(ok) <= half;
                    continue;
                }

                var (n, f) = ((-half - ok) / dk, (half - ok) / dk);
                if (n > f)
                {
                    (n, f) = (f, n);
                }

                (t0, t1) = (MathF.Max(t0, n), MathF.Min(t1, f));
                hit = t0 <= t1;
            }

            if (hit && (best is null || t0 < best))
            {
                best = t0;
            }
        }

        return best;
    }

    /// <summary>One line per side: probes the mesh stops, how many of them the boxes cover, hidden walls.</summary>
    public static string Summary(IReadOnlyList<Result> results)
    {
        var parts = new List<string>();
        foreach (var side in new[] { -1, 1 })
        {
            var mine = results.Where(r => r.Probe.Side == side).ToList();
            var railed = mine.Count(r => r.Mesh is not null);
            var (uncovered, walls) = Problems(mine);
            parts.Add($"{(side < 0 ? "left" : "right")}: {railed}/{mine.Count} railed, {railed - uncovered.Count} covered, {walls.Count} hidden wall(s)");
        }

        return string.Join("; ", parts);
    }

    /// <summary>
    /// Probes the mesh stops that the boxes let through (or stop more than 30 cm further out), and probes the boxes stop
    /// more than 60 cm before the railing of their slice (the nearest place any height of that slice and side meets the mesh;
    /// the mesh's edge where it has none): a wall standing on the road.
    /// </summary>
    public static (List<Result> Uncovered, List<Result> HiddenWalls) Problems(IReadOnlyList<Result> results)
    {
        var uncovered = new List<Result>();
        var walls = new List<Result>();
        foreach (var slice in results.GroupBy(r => (r.Probe.Along, r.Probe.Side)))
        {
            var length = FVector.Distance(slice.First().Probe.From, slice.First().Probe.To);
            var rail = slice.Where(r => r.Mesh is not null).Select(r => r.Mesh!.Value * length).DefaultIfEmpty((length - 100f)).Min();
            uncovered.AddRange(slice.Where(r => !r.Covered(length)));
            walls.AddRange(slice.Where(r => r.Boxes is { } b && (rail - (b * length)) > 60f));
        }

        return (uncovered, walls);
    }

    /// <summary>The top of the mesh's driving surface at (<paramref name="x"/>, <paramref name="y"/>): the highest face up with 2 m free over it.</summary>
    public static float? TopAt(MeshData mesh, float x, float y)
    {
        var hits = new List<(float Z, bool Up)>();
        for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            if (Vertical(mesh.Positions, mesh.Indices, t, x, y) is { } hit)
            {
                hits.Add(hit);
            }
        }

        hits.Sort((l, r) => l.Z.CompareTo(r.Z));
        for (var i = hits.Count - 1; i >= 0; i--)
        {
            if (hits[i].Up && (i == hits.Count - 1 || hits[i + 1].Z - hits[i].Z > 200f))
            {
                return hits[i].Z;
            }
        }

        return null;
    }

    /// <summary>
    /// The deck from above: at <paramref name="slices"/> places along and 9 across (the middle 70 % of its width), where the
    /// mesh has a driving surface, how far the top of the boxes (met by a line down carried by the slice) is above it, cm.
    /// </summary>
    public static List<(FVector At, float Rise)> Deck(MeshData mesh, IReadOnlyList<CollisionBox> boxes, SplineMeshParams? spline, BoundingBox bounds, int slices = 30)
    {
        var b = mesh.Bounds;
        var k = LongAxis(b);
        var j = 1 - k;
        var (min, max) = (Get(b.Min, k), Get(b.Max, k));
        var mid = (Get(b.Min, j) + Get(b.Max, j)) * 0.5f;
        var half = (Get(b.Max, j) - Get(b.Min, j)) * 0.5f;
        var axis = k == 0 ? FVector.Forward : FVector.Right;
        var result = new List<(FVector, float)>();
        for (var i = 0; i < slices; i++)
        {
            var a = min + ((max - min) * (0.03f + (0.94f * i / Math.Max(1, slices - 1))));
            for (var c = -4; c <= 4; c++)
            {
                var across = mid + (c / 4f * half * 0.7f);
                var top = k == 0 ? TopAt(mesh, a, across) : TopAt(mesh, across, a);
                if (top is not { } z)
                {
                    continue;
                }

                var from = Point(k, a, across, z + 300f);
                var to = Point(k, a, across, z - 100f);
                if (spline is not null)
                {
                    var slice = SplineMeshDeformer.CalcSliceTransform(spline, bounds, a);
                    (from, to) = (slice.TransformPosition(from - (axis * a)), slice.TransformPosition(to - (axis * a)));
                }

                if (FirstBox(boxes, from, to) is { } t)
                {
                    result.Add((Point(k, a, across, z), 300f - (t * 400f)));
                }
                else
                {
                    result.Add((Point(k, a, across, z), float.NegativeInfinity)); // nothing under the wheel
                }
            }
        }

        return result;
    }

    private static FVector Point(int k, float a, float across, float z) => k == 0 ? new FVector(a, across, z) : new FVector(across, a, z);

    private static (float Z, bool Up)? Vertical(float[] p, uint[] idx, int t, float x, float y)
    {
        var (i0, i1, i2) = ((int)idx[t] * 3, (int)idx[t + 1] * 3, (int)idx[t + 2] * 3);
        var d = ((p[i1 + 1] - p[i2 + 1]) * (p[i0] - p[i2])) + ((p[i2] - p[i1]) * (p[i0 + 1] - p[i2 + 1]));
        if (MathF.Abs(d) < 1e-6f)
        {
            return null;
        }

        var w0 = (((p[i1 + 1] - p[i2 + 1]) * (x - p[i2])) + ((p[i2] - p[i1]) * (y - p[i2 + 1]))) / d;
        var w1 = (((p[i2 + 1] - p[i0 + 1]) * (x - p[i2])) + ((p[i0] - p[i2]) * (y - p[i2 + 1]))) / d;
        var w2 = 1f - w0 - w1;
        return w0 < 0f || w1 < 0f || w2 < 0f ? null : ((w0 * p[i0 + 2]) + (w1 * p[i1 + 2]) + (w2 * p[i2 + 2]), d < 0f);
    }

    private static FVector V(float[] p, uint i) => new(p[i * 3], p[(i * 3) + 1], p[(i * 3) + 2]);

    private static float Get(System.Numerics.Vector3 v, int k) => k switch { 1 => v.Y, 2 => v.Z, _ => v.X };

    private static float Get(FVector v, int k) => k switch { 1 => v.Y, 2 => v.Z, _ => v.X };
}
