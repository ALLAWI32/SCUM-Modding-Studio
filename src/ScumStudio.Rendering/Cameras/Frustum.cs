using System.Numerics;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Rendering.Cameras;

/// <summary>
/// Six view-frustum planes (normals pointing inwards) extracted from a view-projection matrix with OpenGL clip
/// depth [-1, 1] (Gribb/Hartmann), used for per-instance culling of world-space bounding boxes.
/// </summary>
public readonly struct Frustum
{
    private readonly Plane[] _planes;

    private Frustum(Plane[] planes) => _planes = planes;

    /// <summary>Planes in the order left, right, bottom, top, near, far.</summary>
    public ReadOnlySpan<Plane> Planes => _planes;

    /// <summary>Extracts the planes of a row-vector view-projection matrix (<c>clip = world * viewProj</c>).</summary>
    public static Frustum FromViewProjection(in Matrix4x4 m)
    {
        // With row vectors, clip.x = dot(world, column 1), etc.
        var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);
        var planes = new[]
        {
            Make(c4 + c1), // left:   -w <= x
            Make(c4 - c1), // right:   x <= w
            Make(c4 + c2), // bottom: -w <= y
            Make(c4 - c2), // top:     y <= w
            Make(c4 + c3), // near:   -w <= z
            Make(c4 - c3), // far:     z <= w
        };
        return new Frustum(planes);
    }

    /// <summary>True when the world-space box is at least partly inside (conservative: may accept boxes near corners).</summary>
    public bool Intersects(in BoundingBox box)
    {
        if (box.IsEmpty)
        {
            return false;
        }

        foreach (var p in _planes)
        {
            // "Positive vertex": the box corner furthest along the plane normal.
            var v = new Vector3(
                p.Normal.X >= 0f ? box.Max.X : box.Min.X,
                p.Normal.Y >= 0f ? box.Max.Y : box.Min.Y,
                p.Normal.Z >= 0f ? box.Max.Z : box.Min.Z);
            if (Vector3.Dot(p.Normal, v) + p.D < 0f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the point is inside all six planes.</summary>
    public bool Contains(Vector3 point)
    {
        foreach (var p in _planes)
        {
            if (Vector3.Dot(p.Normal, point) + p.D < 0f)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Transforms a local box by an affine row-vector matrix and returns the enclosing world box (Arvo's method).
    /// </summary>
    public static BoundingBox TransformBounds(in BoundingBox local, in Matrix4x4 m)
    {
        if (local.IsEmpty)
        {
            return local;
        }

        var center = Vector3.Transform(local.Center, m);
        var e = local.Extent;
        var extent = new Vector3(
            (MathF.Abs(m.M11) * e.X) + (MathF.Abs(m.M21) * e.Y) + (MathF.Abs(m.M31) * e.Z),
            (MathF.Abs(m.M12) * e.X) + (MathF.Abs(m.M22) * e.Y) + (MathF.Abs(m.M32) * e.Z),
            (MathF.Abs(m.M13) * e.X) + (MathF.Abs(m.M23) * e.Y) + (MathF.Abs(m.M33) * e.Z));
        return new BoundingBox(center - extent, center + extent);
    }

    private static Plane Make(Vector4 v)
    {
        var normal = new Vector3(v.X, v.Y, v.Z);
        var length = normal.Length();
        return length > 0f ? new Plane(normal / length, v.W / length) : new Plane(Vector3.Zero, v.W);
    }
}
