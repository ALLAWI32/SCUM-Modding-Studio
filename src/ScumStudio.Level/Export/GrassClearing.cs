using System.Buffers.Binary;
using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;

namespace ScumStudio.Level.Export;

/// <summary>
/// "Clear grass under it" (owner and a Discord user, 2026-10-09: "I placed a bridge and the meadow grass grows through it").
/// <para>How the game decides where landscape grass grows (UE 4.27 <c>LandscapeGrass.cpp</c>): in a game world the grass
/// builder only reads the component's cooked <c>GrassData</c> (<c>FLandscapeComponentGrassData</c>: <c>HeightData</c>
/// plus one density map per <c>LandscapeGrassType</c>, <c>(ComponentSizeQuads + 1)²</c> bytes each) and skips a
/// component or type that has none; the grass maps are rendered from the weightmaps and the landscape material only
/// <c>WITH_EDITOR</c>. Each grass instance takes the bilinear mix of the four samples around it, so a sample at 0 only
/// keeps grass out of the four quads it touches. The weightmaps (and the <c>EraseFoliage</c> layer the level designers
/// painted under roads) only colour the ground in a cooked build.</para>
/// <para>The edit therefore zeroes, in place (same length, every other byte as cooked), each grass density sample whose
/// quads touch an object's footprint, and the export deletes the bushes and grass instances (SCUM's
/// <c>FoliageInstancedBush</c>/<c>FoliageInstancedGrass</c> components, not trees or rocks) whose base stands inside it.</para>
/// </summary>
public static class GrassClearing
{
    /// <summary>How far (cm) a footprint reaches past the object's mesh bounds: grass blades lean, nothing should poke through an edge.</summary>
    public const float MarginCm = 25f;

    /// <summary>
    /// True for the foliage the clearing removes: SCUM's bush and grass foliage components, or engine foliage drawing a
    /// mesh from a bush, grass, plant or flower folder. Trees, rocks and anything not painted as foliage stay.
    /// </summary>
    public static bool IsClearedFoliage(ComponentRecord component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!component.IsInstanced)
        {
            return false;
        }

        if (component.ClassName.Contains("FoliageInstancedBush", StringComparison.OrdinalIgnoreCase)
            || component.ClassName.Contains("FoliageInstancedGrass", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // ponytail: folder names of the game's small plants; extend when the owner finds one left standing.
        return component.ClassName.Contains("Foliage", StringComparison.OrdinalIgnoreCase)
               && component.StaticMeshPath is { } mesh
               && !mesh.Contains("/Trees/", StringComparison.OrdinalIgnoreCase) && !mesh.Contains("/Rocks/", StringComparison.OrdinalIgnoreCase)
               && (mesh.Contains("/Bush", StringComparison.OrdinalIgnoreCase) || mesh.Contains("/Grass", StringComparison.OrdinalIgnoreCase)
                   || mesh.Contains("/Plants/", StringComparison.OrdinalIgnoreCase) || mesh.Contains("/Flowers/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A footprint and the heights it spans (world cm): what stands in it stands inside the object.</summary>
    /// <param name="Outline">Convex outline in world X/Y.</param>
    /// <param name="Bottom">Lowest point of the piece.</param>
    /// <param name="Top">Highest point of the piece.</param>
    public readonly record struct Volume(Vector2[] Outline, float Bottom, float Top);

    /// <summary>How far (cm) below an object's lowest point a spawn point still counts as inside it (points sit on the ground).</summary>
    public const float GroundToleranceCm = 50f;

    /// <summary>
    /// The ground footprint of <paramref name="actor"/> in world X/Y as convex polygons grown by <paramref name="margin"/>
    /// cm: one per visible mesh component (its bounds' outline), one per instance, and for a bent spline piece one per
    /// sixteenth of its length (the bent outline). Components whose mesh <paramref name="bounds"/> does not know are left out.
    /// </summary>
    public static List<Vector2[]> Footprints(ActorRecord actor, Func<string, BoundingBox?> bounds, float margin = MarginCm) =>
        Volumes(actor, bounds, margin).Select(v => v.Outline).ToList();

    /// <summary>The pieces of <see cref="Footprints"/> with the heights each spans.</summary>
    public static List<Volume> Volumes(ActorRecord actor, Func<string, BoundingBox?> bounds, float margin = MarginCm)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(bounds);
        var result = new List<Volume>();
        foreach (var c in actor.Components.Where(c => c.IsSceneComponent && c.IsVisible && c.StaticMeshPath is not null && !HelperMeshes.IsHelper(actor, c)))
        {
            if (bounds(c.StaticMeshPath!) is not { IsEmpty: false } b)
            {
                continue;
            }

            if (c.IsInstanced)
            {
                result.AddRange(c.Instances.Select(i => Piece(Corners(b).Select(p => c.WorldTransform.TransformPosition(i.TransformPosition(p))), margin)));
            }
            else if (c.SplineMesh is { } spline)
            {
                var k = spline.ForwardAxis switch { SplineMeshAxis.Y => 1, SplineMeshAxis.Z => 2, _ => 0 };
                var (from, to) = (Get(b.Min, k), Get(b.Max, k));
                List<FVector> Slice(int i)
                {
                    var slice = SplineMeshDeformer.CalcSliceTransform(spline, b, from + ((to - from) * i / 16f));
                    return Corners(b).Select(p => c.WorldTransform.TransformPosition(slice.TransformPosition(Set(p, k)))).ToList();
                }

                var previous = Slice(0);
                for (var i = 1; i <= 16; i++)
                {
                    var next = Slice(i);
                    result.Add(Piece(previous.Concat(next), margin));
                    previous = next;
                }
            }
            else
            {
                result.Add(Piece(Corners(b).Select(p => c.WorldTransform.TransformPosition(p)), margin));
            }
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="point"/> stands inside one of <paramref name="volumes"/>: within its outline, between its
    /// top and <see cref="GroundToleranceCm"/> below its bottom.
    /// </summary>
    public static bool Inside(IEnumerable<Volume> volumes, FVector point) =>
        volumes.Any(v => point.Z <= v.Top && point.Z >= v.Bottom - GroundToleranceCm && Contains(v.Outline, Xy(point)));

    private static Volume Piece(IEnumerable<FVector> corners, float margin)
    {
        var points = corners.ToList();
        return new Volume(Grow(Hull(points.Select(Xy)), margin), points.Min(p => p.Z), points.Max(p => p.Z));
    }

    /// <summary>True when <paramref name="point"/> lies inside any of <paramref name="polygons"/>.</summary>
    public static bool Covers(IEnumerable<Vector2[]> polygons, Vector2 point) => polygons.Any(p => Contains(p, point));

    /// <summary>Even-odd point-in-polygon test (any simple polygon; points on an edge may go either way).</summary>
    public static bool Contains(ReadOnlySpan<Vector2> polygon, Vector2 p)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < ((b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>True when <paramref name="polygon"/> overlaps the open axis-aligned square of half size <paramref name="half"/> around <paramref name="center"/>.</summary>
    public static bool TouchesSquare(ReadOnlySpan<Vector2> polygon, Vector2 center, float half)
    {
        if (Contains(polygon, center))
        {
            return true;
        }

        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            if (SegmentTouchesSquare(polygon[j] - center, polygon[i] - center, half))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Which samples of a <paramref name="stride"/>² grass map must be 0 so no grass grows inside
    /// <paramref name="polygons"/> (given in the component's quad coordinates): every sample whose four quads touch one.
    /// </summary>
    public static bool[] ClearedSamples(int stride, IReadOnlyList<Vector2[]> polygons)
    {
        var mask = new bool[stride * stride];
        foreach (var polygon in polygons)
        {
            // Only the samples within one quad of the polygon's box can be touched.
            var (minX, minY, maxX, maxY) = (polygon.Min(p => p.X), polygon.Min(p => p.Y), polygon.Max(p => p.X), polygon.Max(p => p.Y));
            for (var y = Math.Max(0, (int)MathF.Floor(minY)); y <= Math.Min(stride - 1, (int)MathF.Ceiling(maxY)); y++)
            {
                for (var x = Math.Max(0, (int)MathF.Floor(minX)); x <= Math.Min(stride - 1, (int)MathF.Ceiling(maxX)); x++)
                {
                    mask[(y * stride) + x] |= TouchesSquare(polygon, new Vector2(x, y), 1f);
                }
            }
        }

        return mask;
    }

    /// <summary>Where one grass density map of a component's cooked <c>GrassData</c> lies in its export.</summary>
    /// <param name="GrassType">Package index of the <c>LandscapeGrassType</c> (the map's key).</param>
    /// <param name="Offset">Export-relative offset of the first density byte.</param>
    /// <param name="Length">Density bytes.</param>
    public readonly record struct GrassMap(int GrassType, int Offset, int Length);

    /// <summary>
    /// The density maps of a cooked <c>ULandscapeComponent</c> export (<paramref name="payload"/>), read from the end of its
    /// tagged properties (<paramref name="propertiesEnd"/>): the object guid flag (+ guid), <c>HeightData</c>
    /// (<c>BulkSerialize</c>: element size 2, count, heights), then <c>WeightData</c> (count, then per grass type its
    /// package index and a byte array) — UE 4.27 <c>operator&lt;&lt;(FArchive&amp;, FLandscapeComponentGrassData&amp;)</c>
    /// with editor-only data filtered. Null when the bytes do not have that shape (or the component has no grass).
    /// </summary>
    public static IReadOnlyList<GrassMap>? ReadGrassMaps(ReadOnlySpan<byte> payload, int propertiesEnd, int stride)
    {
        var at = propertiesEnd;
        if (!TryInt(payload, ref at, out var hasGuid) || hasGuid is not (0 or 1))
        {
            return null;
        }

        at += hasGuid * 16;
        if (!TryInt(payload, ref at, out var elementSize) || !TryInt(payload, ref at, out var heights) || elementSize != 2
            || (heights != 0 && heights != stride * stride))
        {
            return null;
        }

        at += heights * 2;
        if (!TryInt(payload, ref at, out var types) || types is < 0 or > 256)
        {
            return null;
        }

        var maps = new List<GrassMap>(types);
        for (var i = 0; i < types; i++)
        {
            if (!TryInt(payload, ref at, out var key) || !TryInt(payload, ref at, out var length) || length < 0 || at + length > payload.Length)
            {
                return null;
            }

            maps.Add(new GrassMap(key, at, length));
            at += length;
        }

        // bCooked follows (a 32-bit bool): a last check that the walk ended where the game's reader does.
        return TryInt(payload, ref at, out var cooked) && cooked is 0 or 1 ? maps : null;
    }

    /// <summary>Zeroes the masked samples of every full-size map in <paramref name="payload"/>; returns how many non-zero densities went to 0.</summary>
    public static int Clear(Span<byte> payload, IReadOnlyList<GrassMap> maps, bool[] mask)
    {
        var cleared = 0;
        foreach (var map in maps.Where(m => m.Length == mask.Length))
        {
            var data = payload.Slice(map.Offset, map.Length);
            for (var i = 0; i < data.Length; i++)
            {
                if (mask[i] && data[i] != 0)
                {
                    data[i] = 0;
                    cleared++;
                }
            }
        }

        return cleared;
    }

    /// <summary>Convex hull (Andrew's monotone chain), counter-clockwise.</summary>
    public static Vector2[] Hull(IEnumerable<Vector2> points)
    {
        var p = points.Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
        if (p.Count < 3)
        {
            return [.. p];
        }

        var hull = new Vector2[p.Count * 2];
        var k = 0;
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
        for (var i = 0; i < p.Count; i++)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0)
            {
                k--;
            }

            hull[k++] = p[i];
        }

        for (int i = p.Count - 2, lower = k + 1; i >= 0; i--)
        {
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0)
            {
                k--;
            }

            hull[k++] = p[i];
        }

        return hull[..(k - 1)];
    }

    /// <summary>The hull grown by an axis-aligned square of half size <paramref name="margin"/> (a Minkowski sum: at least the margin every way).</summary>
    public static Vector2[] Grow(Vector2[] hull, float margin) => margin <= 0f
        ? hull
        : Hull(hull.SelectMany(v => new[] { v + new Vector2(-margin, -margin), v + new Vector2(margin, -margin), v + new Vector2(margin, margin), v + new Vector2(-margin, margin) }));

    private static bool SegmentTouchesSquare(Vector2 a, Vector2 b, float half)
    {
        // Liang-Barsky against the open square |x|, |y| < half (a shade inside, so a shared edge does not count).
        var h = half - 1e-4f;
        var (t0, t1) = (0f, 1f);
        var d = b - a;
        foreach (var (p, q) in new[] { (-d.X, a.X + h), (d.X, h - a.X), (-d.Y, a.Y + h), (d.Y, h - a.Y) })
        {
            if (MathF.Abs(p) < 1e-12f)
            {
                if (q <= 0f)
                {
                    return false;
                }

                continue;
            }

            var t = q / p;
            if (p < 0f)
            {
                t0 = MathF.Max(t0, t);
            }
            else
            {
                t1 = MathF.Min(t1, t);
            }

            if (t0 > t1)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryInt(ReadOnlySpan<byte> data, ref int at, out int value)
    {
        if (at < 0 || at + 4 > data.Length)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
        at += 4;
        return true;
    }

    private static Vector2 Xy(FVector v) => new(v.X, v.Y);

    private static IEnumerable<FVector> Corners(BoundingBox b)
    {
        foreach (var x in new[] { b.Min.X, b.Max.X })
        {
            foreach (var y in new[] { b.Min.Y, b.Max.Y })
            {
                foreach (var z in new[] { b.Min.Z, b.Max.Z })
                {
                    yield return new FVector(x, y, z);
                }
            }
        }
    }

    private static float Get(Vector3 v, int axis) => axis switch { 1 => v.Y, 2 => v.Z, _ => v.X };

    private static FVector Set(FVector v, int axis) => axis switch { 1 => new FVector(v.X, 0f, v.Z), 2 => new FVector(v.X, v.Y, 0f), _ => new FVector(0f, v.Y, v.Z) };
}
