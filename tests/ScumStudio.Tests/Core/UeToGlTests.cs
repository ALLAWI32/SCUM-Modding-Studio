using System.Numerics;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Tests.Core;

public sealed class UeToGlTests
{
    private static readonly FTransform[] Transforms =
    [
        FTransform.Identity,
        new(new FRotator(0f, 90f, 0f), new FVector(100f, 200f, 300f), FVector.One),
        new(new FRotator(25f, -60f, 110f), new FVector(-1500f, 42f, 7f), new FVector(1f, 2f, 0.5f)),
        new(new FRotator(-10f, 5f, 0f), new FVector(0f, 0f, -80f), new FVector(-1f, 1f, 1f)),
    ];

    [Fact]
    public void AxisMappingSwapsYAndZ()
    {
        Assert.Equal(new Vector3(1f, 3f, 2f), UeToGl.Point(new FVector(1f, 2f, 3f)));
        Assert.Equal(new Vector3(1f, 3f, 2f), UeToGl.Direction(new FVector(1f, 2f, 3f)));
        MathAssert.Near(new Vector3(0.01f, 0.03f, 0.02f), UeToGl.Point(new FVector(1f, 2f, 3f), 0.01f), 1e-7f);
        Assert.Equal(new FVector(100f, 200f, 300f), UeToGl.ToUePoint(new Vector3(1f, 3f, 2f), 0.01f));
        Assert.Equal(new FVector(1f, 2f, 3f), UeToGl.ToUeDirection(new Vector3(1f, 3f, 2f)));

        // UE forward -> GL +X, UE right -> GL +Z, UE up -> GL +Y.
        Assert.Equal(Vector3.UnitX, UeToGl.Direction(FVector.Forward));
        Assert.Equal(Vector3.UnitZ, UeToGl.Direction(FVector.Right));
        Assert.Equal(Vector3.UnitY, UeToGl.Direction(FVector.Up));

        // The mapping is a reflection (left-handed to right-handed) and its own inverse.
        Assert.Equal(-1f, UeToGl.AxisSwap.GetDeterminant());
        Assert.Equal(Matrix4x4.Identity, UeToGl.AxisSwap * UeToGl.AxisSwap);
        Assert.Equal(UeToGl.Point(new FVector(4f, 5f, 6f)), Vector3.Transform(new Vector3(4f, 5f, 6f), UeToGl.AxisSwap));
    }

    [Fact]
    public void RotationConversionMatchesDirectionConversion()
    {
        var vectors = new[] { FVector.Forward, FVector.Right, FVector.Up, new FVector(3f, -4f, 12f) };
        foreach (var rotator in new[] { new FRotator(0f, 90f, 0f), new FRotator(30f, 45f, 60f), new FRotator(-80f, 170f, -10f) })
        {
            var q = rotator.Quaternion();
            foreach (var v in vectors)
            {
                MathAssert.Near(UeToGl.Direction(q.RotateVector(v)), Vector3.Transform(UeToGl.Direction(v), UeToGl.Rotation(q)), 1e-3f);
            }

            MathAssert.SameRotation(q, UeToGl.ToUeRotation(UeToGl.Rotation(q)));
        }

        // UE yaw 90 (X to Y) is a turn from GL +X to GL +Z, i.e. -90 degrees about GL +Y.
        MathAssert.Near(Vector3.UnitZ, Vector3.Transform(Vector3.UnitX, UeToGl.Rotation(new FRotator(0f, 90f, 0f).Quaternion())));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.01f)]
    public void ModelMatricesMatchTransformPosition(float unitScale)
    {
        var points = new[] { FVector.Zero, new FVector(10f, 20f, 30f), new FVector(-250f, 4f, 99f) };
        foreach (var transform in Transforms)
        {
            var glModel = UeToGl.ModelMatrix(transform, unitScale);
            var ueModel = UeToGl.ModelMatrixForUeVertices(transform, unitScale);
            foreach (var p in points)
            {
                var expected = UeToGl.Point(transform.TransformPosition(p), unitScale);
                var tolerance = 1e-2f * unitScale;
                MathAssert.Near(expected, Vector3.Transform(UeToGl.Point(p, unitScale), glModel), tolerance);
                MathAssert.Near(expected, Vector3.Transform(p.ToVector3(), ueModel), tolerance);
            }
        }
    }

    [Fact]
    public void FrontFacesBecomeCounterClockwise()
    {
        // A UE triangle whose front face points up (+Z): stock SCUM meshes have cross(v1-v0, v2-v0) opposite the
        // outward normal (clockwise front faces, FORMAT_NOTES "UE front faces are clockwise").
        var v0 = FVector.Zero;
        var v1 = new FVector(0f, 100f, 0f);
        var v2 = new FVector(100f, 0f, 0f);
        var normal = FVector.Up;
        Assert.True(FVector.Dot(FVector.Cross(v1 - v0, v2 - v0), normal) < 0f);

        // After the axis swap the same index order is counter-clockwise around the mapped normal (GL_CCW front faces).
        var g0 = UeToGl.Point(v0);
        var g1 = UeToGl.Point(v1);
        var g2 = UeToGl.Point(v2);
        Assert.True(Vector3.Dot(Vector3.Cross(g1 - g0, g2 - g0), UeToGl.Direction(normal)) > 0f);

        Assert.True(UeToGl.FlipsWinding(new FTransform(FQuat.Identity, FVector.Zero, new FVector(-1f, 1f, 1f))));
        Assert.False(UeToGl.FlipsWinding(new FTransform(FQuat.Identity, FVector.Zero, new FVector(-1f, -1f, 1f))));
        Assert.False(UeToGl.FlipsWinding(FTransform.Identity));
    }

    [Fact]
    public void ViewMatrixLooksAlongUeForwardWithoutMirroring()
    {
        var view = UeToGl.CreateViewMatrix(FVector.Zero, FRotator.Zero);

        // Right-handed view space: the camera looks down -Z, +X is screen right, +Y is screen up.
        MathAssert.Near(new Vector3(0f, 0f, -1000f), Vector3.Transform(UeToGl.Point(new FVector(1000f, 0f, 0f)), view));
        MathAssert.Near(new Vector3(100f, 0f, -1000f), Vector3.Transform(UeToGl.Point(new FVector(1000f, 100f, 0f)), view));
        MathAssert.Near(new Vector3(0f, 100f, -1000f), Vector3.Transform(UeToGl.Point(new FVector(1000f, 0f, 100f)), view));

        // Yaw 90 looks along UE +Y from a camera at (0, 0, 200).
        var turned = UeToGl.CreateViewMatrix(new FVector(0f, 0f, 200f), new FRotator(0f, 90f, 0f));
        MathAssert.Near(new Vector3(0f, 0f, -500f), Vector3.Transform(UeToGl.Point(new FVector(0f, 500f, 200f)), turned), 1e-2f);

        // Pitch -30 looks down: a point straight ahead and below lies on the view axis.
        var down = UeToGl.CreateViewMatrix(FVector.Zero, new FRotator(-30f, 0f, 0f), 0.01f);
        var target = new FRotator(-30f, 0f, 0f).Vector() * 1000f;
        MathAssert.Near(new Vector3(0f, 0f, -10f), Vector3.Transform(UeToGl.Point(target, 0.01f), down), 1e-3f);
    }

    [Fact]
    public void PerspectiveUsesOpenGlDepthRange()
    {
        const float near = 10f;
        const float far = 100_000f;
        var projection = UeToGl.CreatePerspectiveGl(MathF.PI / 3f, 16f / 9f, near, far);

        static float NdcZ(Matrix4x4 m, float viewZ)
        {
            var clip = Vector4.Transform(new Vector4(0f, 0f, viewZ, 1f), m);
            return clip.Z / clip.W;
        }

        MathAssert.Near(-1f, NdcZ(projection, -near));
        MathAssert.Near(1f, NdcZ(projection, -far));

        // A point on the top edge of the vertical field of view maps to NDC y = 1.
        var edge = Vector4.Transform(new Vector4(0f, MathF.Tan(MathF.PI / 6f) * 50f, -50f, 1f), projection);
        MathAssert.Near(1f, edge.Y / edge.W);

        Assert.Throws<ArgumentOutOfRangeException>(() => UeToGl.CreatePerspectiveGl(1f, 1f, 0f, 10f));
        Assert.Throws<ArgumentOutOfRangeException>(() => UeToGl.CreatePerspectiveGl(1f, 1f, 10f, 5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => UeToGl.CreatePerspectiveGl(0f, 1f, 1f, 5f));
    }
}
