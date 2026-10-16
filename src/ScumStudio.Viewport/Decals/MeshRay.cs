using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Viewport.Decals;

/// <summary>A ray from the 3D view (UE space, centimetres) at a preview part: the cursor's line into the scene.</summary>
/// <param name="PartIndex">Index of the part the picker found under the cursor in <see cref="PreviewModel.Parts"/>, or -1 to test every part.</param>
/// <param name="Origin">Ray origin (the camera).</param>
/// <param name="Direction">Ray direction (unit length).</param>
public sealed record SurfaceRay(int PartIndex, Vector3 Origin, Vector3 Direction);

/// <summary>Where a ray meets a mesh, in the part's own space.</summary>
/// <param name="Triangle">Triangle index (first index / 3).</param>
/// <param name="Barycentric">Weights of the triangle's three corners.</param>
/// <param name="Uv0">Texture coordinate (UV0) at the hit; zero when the mesh has none.</param>
/// <param name="Position">Hit point (part space, cm).</param>
/// <param name="Normal">The triangle's normal (part space, unit), turned towards the ray's origin.</param>
/// <param name="Distance">Distance from the ray's origin (cm, world).</param>
public sealed record SurfaceHit(int Triangle, Vector3 Barycentric, Vector2 Uv0, Vector3 Position, Vector3 Normal, float Distance);

/// <summary>
/// The surface point under the cursor: the nearest triangle a ray meets (Möller–Trumbore, both faces), the UV0 and the
/// normal there, so a decal can be dropped where the user clicks.
/// </summary>
public static class MeshRay
{
    /// <summary>
    /// The nearest hit of the ray (<paramref name="originUe"/>, <paramref name="dirUe"/>; UE space) on <paramref name="mesh"/>
    /// standing at <paramref name="partTransform"/>, only on the sections drawn with <paramref name="material"/> when it is given.
    /// </summary>
    public static SurfaceHit? Hit(MeshData mesh, FTransform partTransform, Vector3 originUe, Vector3 dirUe, string? material = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var o = partTransform.InverseTransformPosition(FVector.FromVector3(originUe)).ToVector3();
        var d = partTransform.InverseTransformVector(FVector.FromVector3(dirUe)).ToVector3();
        if (mesh.Bounds.IsEmpty || !Crosses(mesh.Bounds, o, d))
        {
            return null;
        }

        var (p, idx) = (mesh.Positions, mesh.Indices);
        var (best, bestTri, bestU, bestV) = (float.MaxValue, -1, 0f, 0f);
        foreach (var s in mesh.Sections)
        {
            if (material is not null && s.MaterialName.Length > 0 && !string.Equals(s.MaterialName, material, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            for (var i = s.FirstIndex; i + 2 < s.FirstIndex + s.IndexCount; i += 3)
            {
                var (v0, v1, v2) = (Vertex(p, idx[i]), Vertex(p, idx[i + 1]), Vertex(p, idx[i + 2]));
                var (e1, e2) = (v1 - v0, v2 - v0);
                var h = Vector3.Cross(d, e2);
                var det = Vector3.Dot(e1, h);
                if (MathF.Abs(det) < 1e-12f)
                {
                    continue;
                }

                var so = o - v0;
                var u = Vector3.Dot(so, h) / det;
                if (u is < 0f or > 1f)
                {
                    continue;
                }

                var q = Vector3.Cross(so, e1);
                var v = Vector3.Dot(d, q) / det;
                if (v < 0f || u + v > 1f)
                {
                    continue;
                }

                var t = Vector3.Dot(e2, q) / det;
                if (t >= 0f && t < best)
                {
                    (best, bestTri, bestU, bestV) = (t, i / 3, u, v);
                }
            }
        }

        if (bestTri < 0)
        {
            return null;
        }

        var (i0, i1, i2) = (idx[bestTri * 3], idx[(bestTri * 3) + 1], idx[(bestTri * 3) + 2]);
        var bary = new Vector3(1f - bestU - bestV, bestU, bestV);
        var (a, b, c) = (Vertex(p, i0), Vertex(p, i1), Vertex(p, i2));
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        normal = Vector3.Dot(normal, d) > 0f ? -normal : normal;
        var uv = mesh.Uv0.Length == 0 ? Vector2.Zero
            : (bary.X * Uv(mesh.Uv0, i0)) + (bary.Y * Uv(mesh.Uv0, i1)) + (bary.Z * Uv(mesh.Uv0, i2));
        return new SurfaceHit(bestTri, bary, uv, (bary.X * a) + (bary.Y * b) + (bary.Z * c), normal, best * dirUe.Length());
    }

    /// <summary>
    /// The nearest hit over the parts of <paramref name="model"/> (only <see cref="SurfaceRay.PartIndex"/> when it is set),
    /// see-through and cut-out parts (glass, grilles) skipped: a decal goes on the solid surface behind them.
    /// </summary>
    public static (int PartIndex, SurfaceHit Hit)? Hit(PreviewModel model, SurfaceRay ray)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(ray);
        (int, SurfaceHit)? best = null;
        for (var i = 0; i < model.Parts.Count; i++)
        {
            var part = model.Parts[i];
            if ((ray.PartIndex >= 0 && i != ray.PartIndex) || part.AlphaCutoff > 0f || part.Tint is { W: < 1f })
            {
                continue;
            }

            if (Hit(part.Mesh, part.Transform, ray.Origin, ray.Direction) is { } hit && (best is null || hit.Distance < best.Value.Item2.Distance))
            {
                best = (i, hit);
            }
        }

        return best;
    }

    private static Vector3 Vertex(float[] p, uint i) => new(p[i * 3], p[(i * 3) + 1], p[(i * 3) + 2]);

    private static Vector2 Uv(float[] uv, uint i) => new(uv[i * 2], uv[(i * 2) + 1]);

    /// <summary>Slab test: whether the ray o + t·d (t ≥ 0) passes through the box (1 cm of slack for flat panels).</summary>
    private static bool Crosses(BoundingBox box, Vector3 o, Vector3 d)
    {
        float t0 = 0f, t1 = float.MaxValue;
        for (var axis = 0; axis < 3; axis++)
        {
            var (min, max) = (box.Min[axis] - 1f, box.Max[axis] + 1f);
            if (MathF.Abs(d[axis]) < 1e-12f)
            {
                if (o[axis] < min || o[axis] > max)
                {
                    return false;
                }

                continue;
            }

            var (a, b) = ((min - o[axis]) / d[axis], (max - o[axis]) / d[axis]);
            t0 = MathF.Max(t0, MathF.Min(a, b));
            t1 = MathF.Min(t1, MathF.Max(a, b));
            if (t0 > t1)
            {
                return false;
            }
        }

        return true;
    }
}
