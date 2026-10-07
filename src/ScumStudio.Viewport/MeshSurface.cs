using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Viewport;

/// <summary>
/// What a mesh holds up (owner: "Fit to ground puts a thing on whatever is right under it: a roof, a floor, a road, a
/// rock"): its triangles binned over its own X/Y, so a vertical line through the world tests only the few triangles near
/// it. Masked sections (leaves, grass, chain-link) and see-through ones (water, glass) are left out: nothing stands on them.
/// </summary>
public sealed class MeshSurface
{
    private readonly float[] _p;
    private readonly uint[] _idx;
    private readonly BoundingBox _bounds;
    private readonly int _n;
    private readonly float _cellX;
    private readonly float _cellY;
    private readonly int[] _start; // cell c's triangles are _tris[_start[c].._start[c + 1]]
    private readonly int[] _tris;  // first index of each triangle in _idx

    private MeshSurface(MeshData mesh, List<int> tris)
    {
        _p = mesh.Positions;
        _idx = mesh.Indices;
        _bounds = mesh.Bounds;
        _n = Math.Clamp((int)MathF.Sqrt(tris.Count / 8f), 1, 64);
        _cellX = MathF.Max(_bounds.Size.X, 1e-3f) / _n;
        _cellY = MathF.Max(_bounds.Size.Y, 1e-3f) / _n;
        _start = new int[(_n * _n) + 1];
        foreach (var t in tris)
        {
            Cells(t, c => _start[c + 1]++);
        }

        for (var c = 0; c < _n * _n; c++)
        {
            _start[c + 1] += _start[c];
        }

        _tris = new int[_start[^1]];
        var fill = (int[])_start.Clone();
        foreach (var t in tris)
        {
            Cells(t, c => _tris[fill[c]++] = t);
        }
    }

    /// <summary>The surface of <paramref name="asset"/>'s finest LOD, or null when it has nothing solid to stand on.</summary>
    public static MeshSurface? Of(PreparedMeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var mesh = asset.Mesh;
        var tris = new List<int>();
        foreach (var s in mesh.Sections)
        {
            if (asset.MaterialAlphaCutoffs.ContainsKey(s.MaterialName) || asset.MaterialTints.TryGetValue(s.MaterialName, out var tint) && tint.W < 1f)
            {
                continue;
            }

            for (var i = s.FirstIndex; i + 2 < s.FirstIndex + s.IndexCount; i += 3)
            {
                tris.Add(i);
            }
        }

        return tris.Count == 0 || mesh.Bounds.IsEmpty ? null : new MeshSurface(mesh, tris);
    }

    /// <summary>
    /// The highest point (world Z, cm) where the vertical line through (<paramref name="x"/>, <paramref name="y"/>) meets
    /// the mesh standing at <paramref name="world"/>, no higher than <paramref name="fromZ"/>; null when it misses.
    /// </summary>
    public float? HighestBelow(FTransform world, float x, float y, float fromZ)
    {
        // The line in the mesh's own space, o + t·d with t = cm down from fromZ (any turn, tilt or scale of the mesh).
        var o = world.InverseTransformPosition(new FVector(x, y, fromZ));
        var d = world.InverseTransformVector(new FVector(0f, 0f, -1f));
        float t0 = 0f, t1 = float.MaxValue;
        if (!Clip(o.X, d.X, _bounds.Min.X, _bounds.Max.X, ref t0, ref t1) || !Clip(o.Y, d.Y, _bounds.Min.Y, _bounds.Max.Y, ref t0, ref t1)
            || !Clip(o.Z, d.Z, _bounds.Min.Z, _bounds.Max.Z, ref t0, ref t1))
        {
            return null;
        }

        var (ax, bx) = (o.X + (t0 * d.X), o.X + (t1 * d.X));
        var (ay, by) = (o.Y + (t0 * d.Y), o.Y + (t1 * d.Y));
        var (cx0, cx1) = (Cell(MathF.Min(ax, bx), _bounds.Min.X, _cellX), Cell(MathF.Max(ax, bx), _bounds.Min.X, _cellX));
        var (cy0, cy1) = (Cell(MathF.Min(ay, by), _bounds.Min.Y, _cellY), Cell(MathF.Max(ay, by), _bounds.Min.Y, _cellY));
        var best = float.MaxValue;
        for (var cy = cy0; cy <= cy1; cy++)
        {
            for (var cx = cx0; cx <= cx1; cx++)
            {
                var c = (cy * _n) + cx;
                for (var k = _start[c]; k < _start[c + 1]; k++)
                {
                    if (Hit(_tris[k], o, d) is { } t && t < best)
                    {
                        best = t;
                    }
                }
            }
        }

        return best == float.MaxValue ? null : fromZ - best;
    }

    /// <summary>Möller–Trumbore, both faces: how far along d (t ≥ 0) the line meets triangle <paramref name="i"/>, or null.</summary>
    private float? Hit(int i, FVector o, FVector d)
    {
        var (i0, i1, i2) = ((int)_idx[i] * 3, (int)_idx[i + 1] * 3, (int)_idx[i + 2] * 3);
        var v0 = new FVector(_p[i0], _p[i0 + 1], _p[i0 + 2]);
        var e1 = new FVector(_p[i1], _p[i1 + 1], _p[i1 + 2]) - v0;
        var e2 = new FVector(_p[i2], _p[i2 + 1], _p[i2 + 2]) - v0;
        var h = FVector.Cross(d, e2);
        var det = FVector.Dot(e1, h);
        if (MathF.Abs(det) < 1e-9f)
        {
            return null; // seen edge-on (a wall)
        }

        var s = o - v0;
        var u = FVector.Dot(s, h) / det;
        if (u < 0f || u > 1f)
        {
            return null;
        }

        var q = FVector.Cross(s, e1);
        var v = FVector.Dot(d, q) / det;
        if (v < 0f || u + v > 1f)
        {
            return null;
        }

        var t = FVector.Dot(e2, q) / det;
        return t >= 0f ? t : null;
    }

    /// <summary>Narrows [t0, t1] to where o + t·d lies within [min, max] on one axis; false when it never does.</summary>
    private static bool Clip(float o, float d, float min, float max, ref float t0, ref float t1)
    {
        const float Slack = 1f; // cm: a flat floor's bounds have no thickness
        if (MathF.Abs(d) < 1e-9f)
        {
            return o >= min - Slack && o <= max + Slack;
        }

        var (a, b) = ((min - Slack - o) / d, (max + Slack - o) / d);
        t0 = MathF.Max(t0, MathF.Min(a, b));
        t1 = MathF.Min(t1, MathF.Max(a, b));
        return t0 <= t1;
    }

    private int Cell(float v, float min, float size) => Math.Clamp((int)((v - min) / size), 0, _n - 1);

    /// <summary>Calls <paramref name="visit"/> for every cell the triangle starting at index <paramref name="t"/> covers seen from above.</summary>
    private void Cells(int t, Action<int> visit)
    {
        var (i0, i1, i2) = ((int)_idx[t] * 3, (int)_idx[t + 1] * 3, (int)_idx[t + 2] * 3);
        var (x0, x1) = (MathF.Min(_p[i0], MathF.Min(_p[i1], _p[i2])), MathF.Max(_p[i0], MathF.Max(_p[i1], _p[i2])));
        var (y0, y1) = (MathF.Min(_p[i0 + 1], MathF.Min(_p[i1 + 1], _p[i2 + 1])), MathF.Max(_p[i0 + 1], MathF.Max(_p[i1 + 1], _p[i2 + 1])));
        for (var cy = Cell(y0, _bounds.Min.Y, _cellY); cy <= Cell(y1, _bounds.Min.Y, _cellY); cy++)
        {
            for (var cx = Cell(x0, _bounds.Min.X, _cellX); cx <= Cell(x1, _bounds.Min.X, _cellX); cx++)
            {
                visit((cy * _n) + cx);
            }
        }
    }
}
