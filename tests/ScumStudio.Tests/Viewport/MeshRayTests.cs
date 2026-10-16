using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.Tests.Viewport;

/// <summary>The surface point under the cursor (<see cref="MeshRay"/>): triangle, barycentrics, UV0, normal, nearest hit.</summary>
public sealed class MeshRayTests
{
    [Fact]
    public void ARayHitsATransformedQuadWhereExpected()
    {
        var quad = DecalMeshes.Quad();
        var transform = new FTransform(FQuat.Identity, new FVector(100, 0, 0), new FVector(2, 2, 2));
        var hit = MeshRay.Hit(quad, transform, new Vector3(500, 50, 20), -Vector3.UnitX);

        Assert.NotNull(hit);
        Assert.Equal(0, hit.Triangle);
        AssertNear(new Vector3(0.25f, 0.15f, 0.6f), hit.Barycentric);
        Assert.Equal(0.75f, hit.Uv0.X, 4);
        Assert.Equal(0.4f, hit.Uv0.Y, 4);
        AssertNear(new Vector3(0, 25, 10), hit.Position);
        AssertNear(Vector3.UnitX, hit.Normal);
        Assert.Equal(400f, hit.Distance, 2);
        Assert.Null(MeshRay.Hit(quad, transform, new Vector3(500, 150, 20), -Vector3.UnitX)); // beside it
        Assert.Null(MeshRay.Hit(quad, transform, new Vector3(500, 50, 20), Vector3.UnitX));   // looking away
    }

    [Fact]
    public void ARayHitsTheNearWallOfAnOpenCylinder()
    {
        var tube = DecalMeshes.Cylinder(50, 100, 64, 8);
        var hit = MeshRay.Hit(tube, FTransform.Identity, new Vector3(10, 200, 3), -Vector3.UnitY);

        Assert.NotNull(hit);
        Assert.Equal(150f, hit.Distance, 0); // the near wall (y = +50), not the far one at -50
        Assert.Equal((4 * 64) + 0, hit.Triangle / 2); // ring 4 (x 0..12.5), segment 0 (0..5.6 degrees)
        Assert.Equal(0.6f, hit.Uv0.Y, 4);
        Assert.InRange(hit.Uv0.X, (3.44f / 360f) - 0.002f, (3.44f / 360f) + 0.002f);
        Assert.True(Vector3.Dot(hit.Normal, Vector3.UnitY) > 0.99f);
        Assert.InRange(hit.Position.Y, 49.7f, 50f);
        Assert.Null(MeshRay.Hit(tube, FTransform.Identity, new Vector3(10, 200, 80), -Vector3.UnitY)); // above it
    }

    [Fact]
    public void CutOutAndSeeThroughPartsAreSkipped()
    {
        var quad = DecalMeshes.Quad();
        var grille = new PreviewPart("Grille", quad, null, new FTransform(new FVector(100, 0, 0)), AlphaCutoff: 0.5f);
        var glass = new PreviewPart("Glass", quad, null, new FTransform(new FVector(80, 0, 0)), Tint: new Vector4(1, 1, 1, 0.3f));
        var body = new PreviewPart("Body", quad, null, new FTransform(new FVector(50, 0, 0)));
        var model = new PreviewModel("Car", [grille, glass, body], new Dictionary<string, ScumStudio.Assets.Textures.TextureImage>());

        var hit = MeshRay.Hit(model, new SurfaceRay(-1, new Vector3(500, 10, 10), -Vector3.UnitX));
        Assert.NotNull(hit);
        Assert.Equal(2, hit.Value.PartIndex);
        Assert.Equal(450f, hit.Value.Hit.Distance, 2);
        Assert.Null(MeshRay.Hit(model, new SurfaceRay(0, new Vector3(500, 10, 10), -Vector3.UnitX))); // only the grille asked: none
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"{actual} is not {expected}");
}

/// <summary>Synthetic meshes for the decal tests.</summary>
internal static class DecalMeshes
{
    /// <summary>A 100 x 100 cm quad in the YZ plane facing +X: u = (y + 50) / 100, v = (50 − z) / 100.</summary>
    public static MeshData Quad() => MeshData.Create("Quad",
        [0, -50, -50, 0, 50, -50, 0, 50, 50, 0, -50, 50],
        [0, 1, 2, 0, 2, 3],
        [1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0],
        [0, 1, 1, 1, 1, 0, 0, 0]);

    /// <summary>
    /// An open tube round the X axis (x from −length/2), radius <paramref name="radius"/>, unwrapped: u = angle / 360°
    /// (0 at +Y, towards +Z), v = (x + length / 2) / length; outward normals.
    /// </summary>
    public static MeshData Cylinder(float radius, float length, int segments, int rings)
    {
        var (positions, normals, uv, indices) = (new List<float>(), new List<float>(), new List<float>(), new List<uint>());
        for (var j = 0; j <= rings; j++)
        {
            for (var i = 0; i <= segments; i++)
            {
                var a = i * MathF.Tau / segments;
                var (c, s) = (MathF.Cos(a), MathF.Sin(a));
                positions.AddRange([(-length / 2) + (length * j / rings), radius * c, radius * s]);
                normals.AddRange([0, c, s]);
                uv.AddRange([i / (float)segments, j / (float)rings]);
            }
        }

        var row = (uint)segments + 1;
        for (var j = 0u; j < rings; j++)
        {
            for (var i = 0u; i < segments; i++)
            {
                var (a, b, c, d) = ((j * row) + i, (j * row) + i + 1, ((j + 1) * row) + i + 1, ((j + 1) * row) + i);
                indices.AddRange([a, b, c, a, c, d]);
            }
        }

        return MeshData.Create("Tube", [.. positions], [.. indices], [.. normals], [.. uv]);
    }
}
