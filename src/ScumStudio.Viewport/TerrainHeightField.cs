using System.Globalization;
using System.Numerics;
using ScumStudio.Assets.Landscape;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Viewport;

/// <summary>A ray hit on the terrain (UE world space, centimetres).</summary>
/// <param name="Position">Hit point.</param>
/// <param name="Normal">Ground normal at the hit (unit, Z up).</param>
/// <param name="Distance">Distance from the ray origin (in units of the direction's length).</param>
/// <param name="Component">Name of the landscape component that was hit.</param>
public readonly record struct TerrainHit(Vector3 Position, Vector3 Normal, float Distance, string Component);

/// <summary>Weight of one paint layer at a ground position.</summary>
/// <param name="Name">Layer name.</param>
/// <param name="Weight">Weight 0..1.</param>
/// <param name="WeightBlended">False for layers painted on top (e.g. <c>EraseFoliage</c>).</param>
public readonly record struct TerrainLayerSample(string Name, float Weight, bool WeightBlended);

/// <summary>
/// CPU ground queries over the loaded terrain components: height, normal and paint layers at a world X/Y, and ray casts.
/// Heights follow the full-detail landscape triangles exactly (whatever vertex step the mesh was drawn with). All
/// positions are UE world centimetres (X/Y horizontal, Z up); <see cref="RaycastGl"/> takes renderer (GL) space.
/// Immutable and thread-safe.
/// </summary>
public sealed class TerrainHeightField
{
    private readonly Entry[] _entries;
    private readonly Dictionary<(int, int), List<int>> _grid = [];
    private readonly float _cellSize;

    /// <summary>Builds the field from component surfaces (and optionally their paint layers).</summary>
    public TerrainHeightField(IEnumerable<(LandscapeSurface Surface, LandscapeComponentLayers? Layers)> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        _entries = components.Select(c => new Entry(c.Surface, c.Layers is { } l && l.SampleCount == c.Surface.SampleCount ? l : null)).ToArray();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var cell = 0f;
        var quad = float.MaxValue;
        foreach (var e in _entries)
        {
            var s = e.Surface;
            min = Vector3.Min(min, new Vector3(s.WorldMinXY, s.MinHeightCm));
            max = Vector3.Max(max, new Vector3(s.WorldMaxXY, s.MaxHeightCm));
            cell = MathF.Max(cell, MathF.Max(s.WorldMaxXY.X - s.WorldMinXY.X, s.WorldMaxXY.Y - s.WorldMinXY.Y));
            quad = MathF.Min(quad, s.QuadSizeCm);
        }

        Min = _entries.Length == 0 ? Vector3.Zero : min;
        Max = _entries.Length == 0 ? Vector3.Zero : max;
        QuadSizeCm = _entries.Length == 0 ? 100f : quad;
        _cellSize = MathF.Max(cell, 1f);
        for (var i = 0; i < _entries.Length; i++)
        {
            var s = _entries[i].Surface;
            var (x0, y0) = CellOf(s.WorldMinXY.X, s.WorldMinXY.Y);
            var (x1, y1) = CellOf(s.WorldMaxXY.X, s.WorldMaxXY.Y);
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    if (!_grid.TryGetValue((x, y), out var list))
                    {
                        _grid[(x, y)] = list = [];
                    }

                    list.Add(i);
                }
            }
        }
    }

    /// <summary>Number of components.</summary>
    public int ComponentCount => _entries.Length;

    /// <summary>World minimum (X/Y of the footprint, lowest height).</summary>
    public Vector3 Min { get; }

    /// <summary>World maximum (X/Y of the footprint, highest height).</summary>
    public Vector3 Max { get; }

    /// <summary>Smallest quad edge length of the components (cm).</summary>
    public float QuadSizeCm { get; }

    /// <summary>The ground height at world (<paramref name="x"/>, <paramref name="y"/>), or null outside the terrain.</summary>
    public float? SampleHeight(float x, float y) => TryLocate(x, y, out var e, out var qx, out var qy) ? e.Surface.SampleHeight(qx, qy) : null;

    /// <summary>The ground normal (unit, UE axes) at world (<paramref name="x"/>, <paramref name="y"/>), or null outside the terrain.</summary>
    public Vector3? Normal(float x, float y) => TryLocate(x, y, out var e, out var qx, out var qy) ? e.Surface.SampleNormal(qx, qy) : null;

    /// <summary>
    /// The paint layers at world (<paramref name="x"/>, <paramref name="y"/>) with their weights (bilinear between
    /// samples), strongest first; empty outside the terrain or without layer data.
    /// </summary>
    public IReadOnlyList<TerrainLayerSample> SampleLayers(float x, float y)
    {
        if (!TryLocate(x, y, out var e, out var qx, out var qy) || e.Layers is not { } layers)
        {
            return [];
        }

        var n = layers.SampleCount;
        var x0 = Math.Min((int)MathF.Floor(qx), n - 2);
        var y0 = Math.Min((int)MathF.Floor(qy), n - 2);
        x0 = Math.Max(0, x0);
        y0 = Math.Max(0, y0);
        var fx = Math.Clamp(qx - x0, 0f, 1f);
        var fy = Math.Clamp(qy - y0, 0f, 1f);
        var result = new List<TerrainLayerSample>();
        foreach (var layer in layers.Layers)
        {
            var w = layer.Weights;
            var a = w[y0 * n + x0];
            var b = w[y0 * n + x0 + 1];
            var c = w[(y0 + 1) * n + x0];
            var d = w[(y0 + 1) * n + x0 + 1];
            var value = ((a * (1f - fx) + b * fx) * (1f - fy) + (c * (1f - fx) + d * fx) * fy) / 255f;
            if (value > 0.001f)
            {
                result.Add(new TerrainLayerSample(layer.Name, value, layer.WeightBlended));
            }
        }

        result.Sort((p, q) => q.Weight.CompareTo(p.Weight));
        return result;
    }

    /// <summary>
    /// A one-line ground read-out for a hover status bar, e.g.
    /// <c>Ground: Forest_Ground 64%, Default_Slope_Height 26% · Z 123.4 m</c>; null outside the terrain.
    /// </summary>
    public string? Describe(float x, float y, int maxLayers = 3)
    {
        if (SampleHeight(x, y) is not { } z)
        {
            return null;
        }

        var layers = SampleLayers(x, y).Where(l => l.WeightBlended).Take(Math.Max(0, maxLayers))
            .Select(l => string.Create(CultureInfo.InvariantCulture, $"{l.Name} {l.Weight * 100f:0}%"));
        var text = string.Join(", ", layers);
        return string.Create(CultureInfo.InvariantCulture, $"Ground: {(text.Length > 0 ? text : "(no layer data)")} · Z {z / 100f:0.0} m");
    }

    /// <summary>
    /// Casts a ray (UE world centimetres) against the terrain and returns the first hit from above, or null. The ray is
    /// marched in steps of half a quad and refined by bisection (sub-centimetre accuracy).
    /// </summary>
    /// <param name="origin">Ray origin.</param>
    /// <param name="direction">Ray direction (any non-zero length; <see cref="TerrainHit.Distance"/> is in its units).</param>
    /// <param name="maxDistance">Largest distance to search, in units of <paramref name="direction"/>.</param>
    public TerrainHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance = float.MaxValue)
    {
        if (_entries.Length == 0 || direction.LengthSquared() < 1e-20f)
        {
            return null;
        }

        // Clip the ray to the terrain's bounding box (slightly padded).
        var boxMin = Min - new Vector3(1f, 1f, 10f);
        var boxMax = Max + new Vector3(1f, 1f, 10f);
        var tEnter = 0f;
        var tExit = maxDistance;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = origin[axis];
            var d = direction[axis];
            if (MathF.Abs(d) < 1e-12f)
            {
                if (o < boxMin[axis] || o > boxMax[axis])
                {
                    return null;
                }

                continue;
            }

            var t0 = (boxMin[axis] - o) / d;
            var t1 = (boxMax[axis] - o) / d;
            if (t0 > t1)
            {
                (t0, t1) = (t1, t0);
            }

            tEnter = MathF.Max(tEnter, t0);
            tExit = MathF.Min(tExit, t1);
            if (tEnter > tExit)
            {
                return null;
            }
        }

        var horizontal = new Vector2(direction.X, direction.Y).Length();
        if (horizontal < 1e-9f)
        {
            // Vertical ray: one column.
            if (direction.Z >= 0f || SampleHeight(origin.X, origin.Y) is not { } h || origin.Z < h)
            {
                return null;
            }

            var t = (h - origin.Z) / direction.Z;
            return t <= maxDistance ? MakeHit(origin + direction * t, t) : null;
        }

        var dt = QuadSizeCm * 0.5f / horizontal;
        var steps = (int)Math.Min(4_000_000d, Math.Ceiling((tExit - tEnter) / dt) + 1);
        float? previousT = null;
        for (var s = 0; s <= steps; s++)
        {
            var t = MathF.Min(tEnter + s * dt, tExit);
            var p = origin + direction * t;
            if (SampleHeight(p.X, p.Y) is not { } ground)
            {
                previousT = null;
                continue;
            }

            if (p.Z <= ground)
            {
                if (previousT is { } lo)
                {
                    var hi = t;
                    for (var k = 0; k < 40; k++)
                    {
                        var mid = 0.5f * (lo + hi);
                        var m = origin + direction * mid;
                        if (SampleHeight(m.X, m.Y) is { } g && m.Z <= g)
                        {
                            hi = mid;
                        }
                        else
                        {
                            lo = mid;
                        }
                    }

                    return MakeHit(origin + direction * hi, hi);
                }

                if (s == 0 && t <= 0f)
                {
                    // The ray starts below the ground: no hit from above.
                    return null;
                }

                if (s == 0)
                {
                    return MakeHit(p, t);
                }
            }
            else
            {
                previousT = t;
            }

            if (t >= tExit)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// <see cref="Raycast"/> for a ray in the renderer's GL space (Y up, as produced by camera unprojection); the hit is
    /// returned in UE space.
    /// </summary>
    public TerrainHit? RaycastGl(Vector3 originGl, Vector3 directionGl, float maxDistance = float.MaxValue)
    {
        var o = UeToGl.ToUePoint(originGl);
        var d = UeToGl.ToUeDirection(directionGl);
        return Raycast(new Vector3(o.X, o.Y, o.Z), new Vector3(d.X, d.Y, d.Z), maxDistance);
    }

    private TerrainHit MakeHit(Vector3 position, float t)
    {
        if (!TryLocate(position.X, position.Y, out var e, out var qx, out var qy))
        {
            return new TerrainHit(position, Vector3.UnitZ, t, string.Empty);
        }

        var z = e.Surface.SampleHeight(qx, qy);
        return new TerrainHit(position with { Z = z }, e.Surface.SampleNormal(qx, qy), t, e.Surface.Name);
    }

    private bool TryLocate(float x, float y, out Entry entry, out float qx, out float qy)
    {
        entry = default!;
        qx = qy = 0f;
        if (!_grid.TryGetValue(CellOf(x, y), out var list))
        {
            return false;
        }

        foreach (var i in list)
        {
            if (_entries[i].Surface.TryGetQuadCoordinates(x, y, out qx, out qy))
            {
                entry = _entries[i];
                return true;
            }
        }

        return false;
    }

    private (int X, int Y) CellOf(float x, float y) => ((int)MathF.Floor(x / _cellSize), (int)MathF.Floor(y / _cellSize));

    private sealed record Entry(LandscapeSurface Surface, LandscapeComponentLayers? Layers);
}
