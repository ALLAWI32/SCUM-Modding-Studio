using System.Numerics;

namespace ScumStudio.Assets.Landscape;

/// <summary>
/// The full-resolution height grid of one <c>ULandscapeComponent</c>: one sample per quad corner
/// (<see cref="SampleCount"/> = <c>ComponentSizeQuads + 1</c> per side, row-major <c>y * SampleCount + x</c>) with the
/// world height and the packed heightmap normal, plus the component's quad → world transform.
/// <para>Used to bake terrain textures and to answer ground queries (height, normal, raycast) independently of the mesh
/// vertex step. The quad → world mapping assumes the landscape has no pitch/roll (always the case for UE landscapes in
/// practice): world X/Y depend only on the quad coordinates.</para>
/// </summary>
public sealed class LandscapeSurface
{
    private readonly float[] _heights;
    private readonly byte[]? _packedNormals;

    /// <summary>Creates a surface.</summary>
    /// <param name="name">Component export name.</param>
    /// <param name="sectionBase">Section base (landscape quad coordinates of the component's origin).</param>
    /// <param name="componentSizeQuads">Quads per side.</param>
    /// <param name="toWorld">Row-vector transform of (quad x, quad y, local height in landscape units) to UE world centimetres.</param>
    /// <param name="heightsCm">World Z per sample, <c>(componentSizeQuads + 1)²</c> values.</param>
    /// <param name="packedNormals">Heightmap B/A bytes per sample (<c>2 * (componentSizeQuads + 1)²</c>), or null to derive normals from the heights.</param>
    public LandscapeSurface(string name, (int X, int Y) sectionBase, int componentSizeQuads, Matrix4x4 toWorld, float[] heightsCm, byte[]? packedNormals = null)
    {
        ArgumentNullException.ThrowIfNull(heightsCm);
        if (componentSizeQuads < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(componentSizeQuads), "A component has at least one quad.");
        }

        var n = componentSizeQuads + 1;
        if (heightsCm.Length != n * n)
        {
            throw new ArgumentException($"Expected {n * n} heights, got {heightsCm.Length}.", nameof(heightsCm));
        }

        if (packedNormals is not null && packedNormals.Length != n * n * 2)
        {
            throw new ArgumentException($"Expected {n * n * 2} packed normal bytes, got {packedNormals.Length}.", nameof(packedNormals));
        }

        Name = name;
        SectionBase = sectionBase;
        ComponentSizeQuads = componentSizeQuads;
        ToWorld = toWorld;
        _heights = heightsCm;
        _packedNormals = packedNormals;
        NormalRotation = Matrix4x4.Decompose(toWorld, out _, out var rotation, out _) ? rotation : Quaternion.Identity;
        var min = float.MaxValue;
        var max = float.MinValue;
        foreach (var h in heightsCm)
        {
            min = MathF.Min(min, h);
            max = MathF.Max(max, h);
        }

        MinHeightCm = min;
        MaxHeightCm = max;
        var o = Vector3.Transform(Vector3.Zero, toWorld);
        var e = Vector3.Transform(new Vector3(componentSizeQuads, componentSizeQuads, 0f), toWorld);
        var a = Vector3.Transform(new Vector3(componentSizeQuads, 0f, 0f), toWorld);
        var b = Vector3.Transform(new Vector3(0f, componentSizeQuads, 0f), toWorld);
        WorldMinXY = Vector2.Min(Vector2.Min(new Vector2(o.X, o.Y), new Vector2(e.X, e.Y)), Vector2.Min(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)));
        WorldMaxXY = Vector2.Max(Vector2.Max(new Vector2(o.X, o.Y), new Vector2(e.X, e.Y)), Vector2.Max(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y)));
        QuadSizeCm = new Vector2(a.X - o.X, a.Y - o.Y).Length() / componentSizeQuads;
    }

    /// <summary>Component export name.</summary>
    public string Name { get; }

    /// <summary>Section base (landscape quad coordinates of the component's origin).</summary>
    public (int X, int Y) SectionBase { get; }

    /// <summary>Quads per side.</summary>
    public int ComponentSizeQuads { get; }

    /// <summary>Samples per side (<see cref="ComponentSizeQuads"/> + 1).</summary>
    public int SampleCount => ComponentSizeQuads + 1;

    /// <summary>Row-vector transform of (quad x, quad y, local height) to UE world centimetres.</summary>
    public Matrix4x4 ToWorld { get; }

    /// <summary>Rotation of the component (applied to the packed heightmap normals, which are stored unrotated).</summary>
    public Quaternion NormalRotation { get; }

    /// <summary>World heights (UE centimetres), row-major.</summary>
    public ReadOnlySpan<float> HeightsCm => _heights;

    /// <summary>True when the heightmap's packed B/A normals are available.</summary>
    public bool HasPackedNormals => _packedNormals is not null;

    /// <summary>Lowest sample (world centimetres).</summary>
    public float MinHeightCm { get; }

    /// <summary>Highest sample (world centimetres).</summary>
    public float MaxHeightCm { get; }

    /// <summary>World X/Y minimum of the component's footprint.</summary>
    public Vector2 WorldMinXY { get; }

    /// <summary>World X/Y maximum of the component's footprint.</summary>
    public Vector2 WorldMaxXY { get; }

    /// <summary>Edge length of one quad in world centimetres (e.g. 150 on SCUM's island).</summary>
    public float QuadSizeCm { get; }

    /// <summary>World height of sample (<paramref name="x"/>, <paramref name="y"/>), clamped to the grid.</summary>
    public float HeightAt(int x, int y)
    {
        var n = SampleCount;
        return _heights[Math.Clamp(y, 0, n - 1) * n + Math.Clamp(x, 0, n - 1)];
    }

    /// <summary>World position (UE centimetres) of sample (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public Vector3 WorldPosition(int x, int y)
    {
        var p = Vector3.Transform(new Vector3(x, y, 0f), ToWorld);
        return new Vector3(p.X, p.Y, HeightAt(x, y));
    }

    /// <summary>
    /// World-space unit normal (UE axes, Z up) at a sample: the heightmap's packed normal when present and valid,
    /// otherwise central differences of the heights.
    /// </summary>
    public Vector3 NormalAt(int x, int y)
    {
        var n = SampleCount;
        x = Math.Clamp(x, 0, n - 1);
        y = Math.Clamp(y, 0, n - 1);
        if (_packedNormals is { } packed)
        {
            var i = (y * n + x) * 2;
            if (LandscapeExtractor.TryDecodePackedNormal(packed[i], packed[i + 1], out var local))
            {
                return Vector3.Normalize(Vector3.Transform(local, NormalRotation));
            }
        }

        return FiniteDifferenceNormal(x, y);
    }

    /// <summary>Normal from central differences of the world heights at a sample.</summary>
    public Vector3 FiniteDifferenceNormal(int x, int y)
    {
        var n = SampleCount;
        var x0 = Math.Max(0, x - 1);
        var x1 = Math.Min(n - 1, x + 1);
        var y0 = Math.Max(0, y - 1);
        var y1 = Math.Min(n - 1, y + 1);
        var dx = WorldPosition(x1, y) - WorldPosition(x0, y);
        var dy = WorldPosition(x, y1) - WorldPosition(x, y0);
        var normal = Vector3.Cross(dx, dy);
        if (normal.Z < 0f)
        {
            normal = -normal;
        }

        return normal.LengthSquared() > 0f ? Vector3.Normalize(normal) : Vector3.UnitZ;
    }

    /// <summary>
    /// Maps a world X/Y position to fractional quad coordinates of this component. Returns false when the point is
    /// outside the component (a small tolerance keeps shared edges inside both neighbours).
    /// </summary>
    public bool TryGetQuadCoordinates(float worldX, float worldY, out float qx, out float qy)
    {
        var m = ToWorld;
        var det = m.M11 * m.M22 - m.M21 * m.M12;
        if (MathF.Abs(det) < 1e-12f)
        {
            qx = qy = 0f;
            return false;
        }

        var rx = worldX - m.M41;
        var ry = worldY - m.M42;
        qx = (rx * m.M22 - ry * m.M21) / det;
        qy = (ry * m.M11 - rx * m.M12) / det;
        const float tolerance = 1e-3f;
        return qx >= -tolerance && qy >= -tolerance && qx <= ComponentSizeQuads + tolerance && qy <= ComponentSizeQuads + tolerance;
    }

    /// <summary>
    /// Height at fractional quad coordinates, interpolated on the same triangles the landscape mesh uses
    /// (each quad split along the diagonal from (x+1, y) to (x, y+1)), so it matches the full-detail mesh exactly.
    /// </summary>
    public float SampleHeight(float qx, float qy)
    {
        var size = ComponentSizeQuads;
        qx = Math.Clamp(qx, 0f, size);
        qy = Math.Clamp(qy, 0f, size);
        var x = Math.Min((int)MathF.Floor(qx), size - 1);
        var y = Math.Min((int)MathF.Floor(qy), size - 1);
        var fx = qx - x;
        var fy = qy - y;
        var a = HeightAt(x, y);
        var b = HeightAt(x + 1, y);
        var c = HeightAt(x, y + 1);
        var d = HeightAt(x + 1, y + 1);
        return fx + fy <= 1f
            ? a + fx * (b - a) + fy * (c - a)
            : d + (1f - fx) * (c - d) + (1f - fy) * (b - d);
    }

    /// <summary>Normal at fractional quad coordinates: bilinear blend of the four surrounding sample normals.</summary>
    public Vector3 SampleNormal(float qx, float qy)
    {
        var size = ComponentSizeQuads;
        qx = Math.Clamp(qx, 0f, size);
        qy = Math.Clamp(qy, 0f, size);
        var x = Math.Min((int)MathF.Floor(qx), size - 1);
        var y = Math.Min((int)MathF.Floor(qy), size - 1);
        var fx = qx - x;
        var fy = qy - y;
        var n = Vector3.Lerp(Vector3.Lerp(NormalAt(x, y), NormalAt(x + 1, y), fx), Vector3.Lerp(NormalAt(x, y + 1), NormalAt(x + 1, y + 1), fx), fy);
        return n.LengthSquared() > 0f ? Vector3.Normalize(n) : Vector3.UnitZ;
    }
}
