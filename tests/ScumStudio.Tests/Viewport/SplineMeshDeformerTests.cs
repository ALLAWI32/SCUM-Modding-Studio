using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// <see cref="SplineMeshDeformer"/> against hand-computed points of <c>USplineMeshComponent::CalcSliceTransform</c>: a
/// straight spline only translates, quarter-circle tangents turn the end slice, roll/scale/offset and the other forward
/// axes lay the cross-section out in the rolled frame.
/// </summary>
public sealed class SplineMeshDeformerTests
{
    private const float Tolerance = 1e-3f;

    /// <summary>A 100 cm long strip along +X: corners at x 0/100, y ±10, z 0/5; normals up.</summary>
    private static MeshData Strip() => MeshData.Create(
        "strip",
        [0, -10, 0, 100, -10, 0, 100, 10, 0, 0, 10, 0, 0, -10, 5, 100, -10, 5, 100, 10, 5, 0, 10, 5],
        [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6],
        normals: [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1]);

    private static FVector PositionOf(MeshData mesh, int vertex) =>
        new(mesh.Positions[vertex * 3], mesh.Positions[vertex * 3 + 1], mesh.Positions[vertex * 3 + 2]);

    private static FVector NormalOf(MeshData mesh, int vertex) =>
        new(mesh.Normals[vertex * 3], mesh.Normals[vertex * 3 + 1], mesh.Normals[vertex * 3 + 2]);

    [Fact]
    public void StraightSplineOnlyTranslates()
    {
        var strip = Strip();
        var spline = new SplineMeshParams
        {
            StartPos = new FVector(200, 50, -3),
            StartTangent = new FVector(100, 0, 0),
            EndPos = new FVector(300, 50, -3),
            EndTangent = new FVector(100, 0, 0),
        };

        var bent = SplineMeshDeformer.Deform(strip, spline);

        Assert.Equal(strip.VertexCount, bent.VertexCount);
        Assert.Same(strip.Indices, bent.Indices);
        for (var v = 0; v < strip.VertexCount; v++)
        {
            AssertNear(PositionOf(strip, v) + new FVector(200, 50, -3), PositionOf(bent, v));
            AssertNear(FVector.Up, NormalOf(bent, v));
        }

        AssertNear(new FVector(200, 40, -3), FVector.FromVector3(bent.Bounds.Min));
        AssertNear(new FVector(300, 60, 2), FVector.FromVector3(bent.Bounds.Max));
        var slice = SplineMeshDeformer.CalcSliceTransform(spline, strip.Bounds, 25f);
        AssertNear(FVector.Right, slice.TransformVectorNoScale(FVector.Right)); // no rotation
        AssertNear(new FVector(225, 50, -3), slice.Translation);
        AssertNear(FVector.One, slice.Scale3D);
    }

    [Fact]
    public void QuarterCircleTangentsTurnTheEndSlice()
    {
        const float r = 100f;
        var k = r * MathF.PI / 2f; // tangent length of a quarter circle of radius r
        var strip = Strip();
        var spline = new SplineMeshParams
        {
            StartPos = FVector.Zero,
            StartTangent = new FVector(k, 0, 0),
            EndPos = new FVector(r, r, 0),
            EndTangent = new FVector(0, k, 0),
        };

        var bent = SplineMeshDeformer.Deform(strip, spline);

        // Start (x = 0): frame = identity, so y goes to +Y.
        AssertNear(new FVector(0, -10, 0), PositionOf(bent, 0));
        AssertNear(new FVector(0, 10, 5), PositionOf(bent, 7));
        // End (x = 100): direction +Y, frame X = Up x Dir = -X, so y goes to -X; the normal stays up.
        AssertNear(new FVector(r + 10, r, 0), PositionOf(bent, 1));
        AssertNear(new FVector(r - 10, r, 5), PositionOf(bent, 6));
        AssertNear(FVector.Up, NormalOf(bent, 6));

        // Midpoint of the Hermite curve: (End + (StartTangent - EndTangent) / 4) / 2, direction (1, 1, 0) / sqrt 2.
        var mid = SplineMeshDeformer.CalcSliceTransform(spline, strip.Bounds, 50f);
        AssertNear(new FVector(r / 2 + k / 8, r / 2 - k / 8, 0), mid.Translation);
        AssertNear(new FVector(1, 1, 0) / MathF.Sqrt(2f), mid.TransformVectorNoScale(FVector.Forward));
        AssertNear(new FVector(-1, 1, 0) / MathF.Sqrt(2f), mid.TransformVectorNoScale(FVector.Right));
        AssertNear(FVector.Up, mid.TransformVectorNoScale(FVector.Up));
    }

    [Fact]
    public void RollScaleAndOffsetLayOutTheCrossSection()
    {
        var strip = Strip();
        var spline = new SplineMeshParams
        {
            StartTangent = new FVector(100, 0, 0),
            EndPos = new FVector(100, 0, 0),
            EndTangent = new FVector(100, 0, 0),
            StartRoll = MathF.PI / 2,
            EndRoll = MathF.PI / 2,
            StartScale = new Vector2(2, 3),
            EndScale = new Vector2(2, 3),
            StartOffset = new Vector2(5, 7),
            EndOffset = new Vector2(5, 7),
        };

        var bent = SplineMeshDeformer.Deform(strip, spline);

        // Frame X = +Y, Y = +Z; a quarter roll turns X into -Z and Y into +X of the frame: (x, y, z) -> (x, 5 + 3z, 7 - 2y).
        AssertNear(new FVector(0, 5, 27), PositionOf(bent, 0));
        AssertNear(new FVector(100, 20, -13), PositionOf(bent, 6));
        AssertNear(FVector.Right, NormalOf(bent, 6)); // up normals now point along +Y
        var slice = SplineMeshDeformer.CalcSliceTransformAtSplineOffset(spline, 0.5f);
        AssertNear(new FVector(1, 2, 3), slice.Scale3D);
        AssertNear(new FVector(50, 5, 7), slice.Translation);
    }

    [Fact]
    public void SmoothInterpolationUsesASmoothStepForScaleRollAndOffsetOnly()
    {
        var spline = new SplineMeshParams
        {
            StartTangent = new FVector(100, 0, 0),
            EndPos = new FVector(100, 0, 0),
            EndTangent = new FVector(100, 0, 0),
            StartScale = Vector2.One,
            EndScale = new Vector2(3, 5),
            SmoothInterpRollScale = true,
        };

        var slice = SplineMeshDeformer.CalcSliceTransformAtSplineOffset(spline, 0.25f);

        const float smooth = 0.25f * 0.25f * (3f - 2f * 0.25f); // 0.15625
        AssertNear(new FVector(1, 1 + 2 * smooth, 1 + 4 * smooth), slice.Scale3D);
        AssertNear(new FVector(25, 0, 0), slice.Translation); // the position itself is not smoothed
        var linear = SplineMeshDeformer.CalcSliceTransformAtSplineOffset(spline with { SmoothInterpRollScale = false }, 0.25f);
        AssertNear(new FVector(1, 1.5f, 2), linear.Scale3D);
    }

    [Fact]
    public void ForwardAxisZAndBoundaryPickTheCoordinateAlongTheSpline()
    {
        // A column along +Z (z 0..100) bent onto a spline along +X: (a, b, c) -> (c, a, b).
        var column = MeshData.Create("column", [0, 0, 0, 10, -4, 100, 3, 2, 50], [0, 1, 2]);
        var spline = new SplineMeshParams
        {
            StartTangent = new FVector(100, 0, 0),
            EndPos = new FVector(100, 0, 0),
            EndTangent = new FVector(100, 0, 0),
            ForwardAxis = SplineMeshAxis.Z,
        };

        var bent = SplineMeshDeformer.Deform(column, spline);
        AssertNear(new FVector(0, 0, 0), PositionOf(bent, 0));
        AssertNear(new FVector(100, 10, -4), PositionOf(bent, 1));
        AssertNear(new FVector(50, 3, 2), PositionOf(bent, 2));
        Assert.Equal(0.5f, SplineMeshDeformer.ComputeRatioAlongSpline(spline, column.Bounds, 50f), Tolerance);

        // A custom boundary maps z 0..50 onto the whole spline, so z = 50 lands on the end.
        var bounded = SplineMeshDeformer.Deform(column, spline with { SplineBoundaryMin = 0, SplineBoundaryMax = 50 });
        AssertNear(new FVector(100, 3, 2), PositionOf(bounded, 2));
        AssertNear(new FVector(200, 10, -4), PositionOf(bounded, 1));
    }

    [Fact]
    public void EveryLodOfASplinePlacementIsBentOverTheFinestLodsLength()
    {
        // A coarse LOD that only reaches x = 50: bent over LOD 0's 100 cm it ends half way, like the engine draws it.
        var fine = Strip();
        var coarse = MeshData.Create("coarse", [0, -10, 0, 50, -10, 0, 50, 10, 0], [0, 1, 2]);
        var spline = new SplineMeshParams
        {
            StartPos = new FVector(200, 0, 0),
            StartTangent = new FVector(100, 0, 0),
            EndPos = new FVector(300, 0, 0),
            EndTangent = new FVector(100, 0, 0),
        };
        var component = new ComponentRecord(1, "Road", "SplineMeshComponent", true, null, FTransform.Identity, FTransform.Identity, "/Game/Road.Road", [])
        {
            SplineMesh = spline,
        };
        var actor = new ActorRecord(0, "LandscapeStreamingProxy_0", "/Script/Landscape.LandscapeStreamingProxy", null, [component], FTransform.Identity,
            ActorKind.Other, null, []);
        var placements = new List<ScenePlacement> { new("/Game/Road.Road", Matrix4x4.Identity, FTransform.Identity, 1, "Road", 0, actor, component, null) };
        var meshes = new Dictionary<string, PreparedMeshAsset>(StringComparer.OrdinalIgnoreCase)
        {
            ["/Game/Road.Road"] = new("/Game/Road.Road", fine, null) { Lods = [fine, coarse], LodScreenSizes = [1f, 0.3f] },
        };

        Assert.Equal(1, SplineMeshPlacements.Apply(placements, meshes));
        var bent = meshes[placements[0].MeshPath];
        Assert.Equal(2, bent.Lods.Count);
        Assert.Same(bent.Mesh, bent.Lods[0]);
        AssertNear(new FVector(300, -10, 0), PositionOf(bent.Lods[0], 1));
        AssertNear(new FVector(250, -10, 0), PositionOf(bent.Lods[1], 1)); // not 300: the coarse LOD is not stretched
    }

    private static void AssertNear(FVector expected, FVector actual) =>
        Assert.True(expected.Equals(actual, Tolerance), $"expected {expected}, got {actual}");
}
