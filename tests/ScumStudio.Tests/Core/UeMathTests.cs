using System.Numerics;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Tests.Core;

public sealed class UeMathTests
{
    private static readonly float Half = MathF.Sqrt(0.5f);

    public static TheoryData<float, float, float> Rotators => new()
    {
        { 0f, 0f, 0f },
        { 30f, 45f, 60f },
        { -45f, 170f, -120f },
        { 10f, -90f, 179f },
        { 89f, 12f, -33f },
        { -60f, -135f, 45f },
    };

    [Fact]
    public void Yaw90RotatesForwardToRight()
    {
        var yaw = new FRotator(0f, 90f, 0f);
        MathAssert.Near(FVector.Right, yaw.RotateVector(FVector.Forward));
        MathAssert.Near(-FVector.Forward, yaw.RotateVector(FVector.Right));
        MathAssert.Near(FVector.Up, yaw.RotateVector(FVector.Up));
    }

    [Fact]
    public void Pitch90LiftsForwardToUp()
    {
        var pitch = new FRotator(90f, 0f, 0f);
        MathAssert.Near(FVector.Up, pitch.RotateVector(FVector.Forward));
        MathAssert.Near(-FVector.Forward, pitch.RotateVector(FVector.Up));
    }

    [Fact]
    public void Roll90TurnsUpToRightAndRightToDown()
    {
        var roll = new FRotator(0f, 0f, 90f);
        MathAssert.Near(FVector.Right, roll.RotateVector(FVector.Up));
        MathAssert.Near(-FVector.Up, roll.RotateVector(FVector.Right));
        MathAssert.Near(FVector.Forward, roll.RotateVector(FVector.Forward));
    }

    [Fact]
    public void RotatorQuaternionKnownValues()
    {
        MathAssert.SameRotation(new FQuat(0f, 0f, Half, Half), new FRotator(0f, 90f, 0f).Quaternion());
        MathAssert.SameRotation(new FQuat(0f, -Half, 0f, Half), new FRotator(90f, 0f, 0f).Quaternion());
        MathAssert.SameRotation(new FQuat(-Half, 0f, 0f, Half), new FRotator(0f, 0f, 90f).Quaternion());
        MathAssert.SameRotation(FQuat.Identity, FRotator.Zero.Quaternion());
        MathAssert.SameRotation(FQuat.Identity, new FRotator(0f, 360f, 0f).Quaternion());
    }

    [Theory]
    [MemberData(nameof(Rotators))]
    public void RotatorMatchesUeRotationMatrix(float pitch, float yaw, float roll)
    {
        // Independent reference: the rows of UE's FRotationTranslationMatrix are the rotated X, Y and Z axes.
        var (sp, cp) = MathF.SinCos(pitch * UeMath.DegreesToRadians);
        var (sy, cy) = MathF.SinCos(yaw * UeMath.DegreesToRadians);
        var (sr, cr) = MathF.SinCos(roll * UeMath.DegreesToRadians);
        var xAxis = new FVector(cp * cy, cp * sy, sp);
        var yAxis = new FVector((sr * sp * cy) - (cr * sy), (sr * sp * sy) + (cr * cy), -sr * cp);
        var zAxis = new FVector(-((cr * sp * cy) + (sr * sy)), (cy * sr) - (cr * sp * sy), cr * cp);

        var rotator = new FRotator(pitch, yaw, roll);
        MathAssert.Near(xAxis, rotator.RotateVector(FVector.Forward));
        MathAssert.Near(yAxis, rotator.RotateVector(FVector.Right));
        MathAssert.Near(zAxis, rotator.RotateVector(FVector.Up));
        MathAssert.Near(xAxis, rotator.Vector());
    }

    [Theory]
    [MemberData(nameof(Rotators))]
    public void RotatorQuaternionRoundTrip(float pitch, float yaw, float roll)
    {
        var rotator = new FRotator(pitch, yaw, roll);
        var back = rotator.Quaternion().Rotator();
        MathAssert.Near(rotator, back);
        Assert.True(rotator.Quaternion().IsNormalized());
    }

    [Fact]
    public void GimbalLockKeepsOrientation()
    {
        foreach (var rotator in new[] { new FRotator(90f, 30f, 0f), new FRotator(-90f, -45f, 10f), new FRotator(90f, 0f, 90f) })
        {
            var back = rotator.Quaternion().Rotator();
            Assert.True(MathF.Abs(MathF.Abs(back.Pitch) - 90f) < 1e-3f, back.ToString());
            Assert.True(rotator.IsSameOrientation(back, 1e-3f), $"{rotator} vs {back}");
        }
    }

    [Fact]
    public void RotatorBeyondRangeKeepsOrientation()
    {
        var rotator = new FRotator(120f, 400f, -200f);
        var back = rotator.Quaternion().Rotator();
        Assert.True(rotator.IsSameOrientation(back, 1e-4f));
        MathAssert.Near(rotator.RotateVector(new FVector(3f, -4f, 5f)), back.RotateVector(new FVector(3f, -4f, 5f)), 1e-3f);
    }

    [Theory]
    [InlineData(270f, -90f)]
    [InlineData(-270f, 90f)]
    [InlineData(180f, 180f)]
    [InlineData(-180f, 180f)]
    [InlineData(540f, 180f)]
    [InlineData(-45f, -45f)]
    [InlineData(0f, 0f)]
    public void NormalizeAxis(float input, float expected) => Assert.Equal(expected, UeMath.NormalizeAxis(input));

    [Theory]
    [InlineData(-90f, 270f)]
    [InlineData(720f, 0f)]
    [InlineData(370f, 10f)]
    public void ClampAxis(float input, float expected) => Assert.Equal(expected, UeMath.ClampAxis(input), 4);

    [Fact]
    public void RotatorHelpers()
    {
        var rotator = new FRotator(10f, 20f, 30f);
        Assert.Equal(new FVector(30f, 10f, 20f), rotator.Euler());
        Assert.Equal(rotator, FRotator.MakeFromEuler(rotator.Euler()));
        Assert.True(new FRotator(0f, 360f, -360f).IsNearlyZero());
        Assert.True(new FRotator(0f, 359.99999f, 0f).Equals(FRotator.Zero, 1e-3f));
        Assert.Equal(new FRotator(-90f, 180f, 90f), new FRotator(270f, 540f, -270f).GetNormalized());
        MathAssert.Near(new FVector(1f, 2f, 3f), rotator.UnrotateVector(rotator.RotateVector(new FVector(1f, 2f, 3f))));
        MathAssert.Near(new FVector(1f, 2f, 3f), rotator.GetInverse().RotateVector(rotator.RotateVector(new FVector(1f, 2f, 3f))));
        Assert.Equal("P=10.000 Y=20.000 R=30.000", rotator.ToString());
    }

    [Theory]
    [MemberData(nameof(Rotators))]
    public void QuaternionIdentities(float pitch, float yaw, float roll)
    {
        var q = new FRotator(pitch, yaw, roll).Quaternion();
        var r = new FRotator(yaw * 0.5f, roll, pitch).Quaternion();
        var v = new FVector(120f, -35f, 64f);

        MathAssert.SameRotation(FQuat.Identity, q * q.Inverse());
        MathAssert.Near(v.Size(), q.RotateVector(v).Size(), 1e-2f);
        MathAssert.Near(v, q.UnrotateVector(q.RotateVector(v)), 1e-3f);

        // UE composition: (q * r) applies r first, then q.
        MathAssert.Near(q.RotateVector(r.RotateVector(v)), (q * r).RotateVector(v), 1e-3f);

        // Same element convention as System.Numerics.
        MathAssert.Near(q.RotateVector(v).ToVector3(), Vector3.Transform(v.ToVector3(), q.ToQuaternion()), 1e-3f);
        MathAssert.SameRotation(q, FQuat.FromMatrix(q.ToMatrix()));
        var matrix = q.ToMatrix();
        MathAssert.Near(q.GetForwardVector(), UeMatrix.GetScaledAxis(matrix, 0));
        MathAssert.Near(q.GetRightVector(), UeMatrix.GetScaledAxis(matrix, 1));
        MathAssert.Near(q.GetUpVector(), UeMatrix.GetScaledAxis(matrix, 2));
    }

    [Fact]
    public void QuaternionFromMatrixHandlesHalfTurns()
    {
        // Trace <= 0 branches of the UE algorithm.
        foreach (var axis in new[] { FVector.Forward, FVector.Right, FVector.Up, new FVector(1f, 1f, 0f).GetSafeNormal() })
        {
            var q = FQuat.FromAxisAngle(axis, MathF.PI);
            MathAssert.SameRotation(q, FQuat.FromMatrix(q.ToMatrix()));
        }
    }

    [Fact]
    public void AxisAngleSlerpAndFindBetween()
    {
        var yaw90 = FQuat.FromAxisAngle(FVector.Up, MathF.PI / 2f);
        MathAssert.SameRotation(new FRotator(0f, 90f, 0f).Quaternion(), yaw90);
        MathAssert.Near(MathF.PI / 2f, yaw90.GetAngle());
        MathAssert.Near(FVector.Up, yaw90.GetRotationAxis());
        MathAssert.Near(MathF.PI / 2f, FQuat.Identity.AngularDistance(yaw90), 1e-3f);

        MathAssert.SameRotation(new FRotator(0f, 45f, 0f).Quaternion(), FQuat.Slerp(FQuat.Identity, yaw90, 0.5f));
        MathAssert.SameRotation(yaw90, FQuat.Slerp(FQuat.Identity, yaw90, 1f));

        var between = FQuat.FindBetweenNormals(FVector.Forward, FVector.Right);
        MathAssert.Near(FVector.Right, between.RotateVector(FVector.Forward));
        var opposite = FQuat.FindBetweenVectors(new FVector(5f, 0f, 0f), new FVector(-2f, 0f, 0f));
        MathAssert.Near(-FVector.Forward, opposite.RotateVector(FVector.Forward));
        var up = FQuat.FindBetweenVectors(FVector.Up, new FVector(0f, 0.5f, 0.5f));
        MathAssert.Near(new FVector(0f, 1f, 1f).GetSafeNormal(), up.RotateVector(FVector.Up));
    }

    [Fact]
    public void QuaternionEulerMatchesRotator()
    {
        var rotator = new FRotator(15f, -30f, 45f);
        MathAssert.Near(rotator.Euler(), rotator.Quaternion().Euler(), 1e-2f);
        MathAssert.SameRotation(rotator.Quaternion(), FQuat.MakeFromEuler(rotator.Euler()));
    }

    [Fact]
    public void VectorBasics()
    {
        var a = new FVector(1f, 2f, 3f);
        var b = new FVector(-4f, 5f, 0.5f);
        Assert.Equal(1f * -4f + 2f * 5f + 3f * 0.5f, FVector.Dot(a, b));
        Assert.Equal(FVector.Up, FVector.Cross(FVector.Forward, FVector.Right));
        Assert.Equal(new FVector(2f, 4f, 6f), a * 2f);
        Assert.Equal(new FVector(-4f, 10f, 1.5f), a * b);
        Assert.Equal(5f, new FVector(3f, 4f, 0f).Size());
        Assert.Equal(FVector.Zero, FVector.Zero.GetSafeNormal());
        MathAssert.Near(1f, b.GetSafeNormal().Size());
        Assert.Equal("X=1.000 Y=2.000 Z=3.000", a.ToString());
        Assert.True(new FVector(float.NaN, 0f, 0f).ContainsNaN());
    }

    [Fact]
    public void TransformPositionKnownValue()
    {
        var transform = new FTransform(new FRotator(0f, 90f, 0f), new FVector(100f, 0f, 0f), new FVector(2f));
        MathAssert.Near(new FVector(100f, 20f, 0f), transform.TransformPosition(new FVector(10f, 0f, 0f)));
        MathAssert.Near(new FVector(0f, 20f, 0f), transform.TransformVector(new FVector(10f, 0f, 0f)));
        MathAssert.Near(new FVector(10f, 0f, 0f), transform.InverseTransformPosition(new FVector(100f, 20f, 0f)));
    }

    public static TheoryData<FTransform, FTransform> TransformPairs => new()
    {
        {
            new FTransform(new FRotator(10f, 20f, 30f), new FVector(100f, -50f, 25f), new FVector(1f, 2f, 3f)),
            new FTransform(new FRotator(-40f, 75f, 5f), new FVector(-300f, 20f, 1000f), new FVector(1.5f)) },
        {
            FTransform.Identity,
            new FTransform(new FRotator(0f, 90f, 0f), new FVector(0f, 0f, 50f), FVector.One) },
        {
            new FTransform(new FRotator(0f, -30f, 0f), new FVector(5f, 6f, 7f), new FVector(-1f, 1f, 1f)),
            new FTransform(new FRotator(20f, 45f, -10f), new FVector(1f, 2f, 3f), new FVector(2f)) },
        {
            new FTransform(new FRotator(33f, 0f, 90f), new FVector(-10f, 0f, 0f), new FVector(0.5f, 0.5f, 4f)),
            new FTransform(new FRotator(0f, 180f, 0f), new FVector(12345f, -6789f, 42f), new FVector(-2f, 2f, 2f)) },
    };

    [Theory]
    [MemberData(nameof(TransformPairs))]
    public void TransformCompositionAppliesLeftFirst(FTransform a, FTransform b)
    {
        // Exact whenever b has uniform scale (FTransform cannot represent shear), incl. UE's negative-scale matrix path.
        var composed = a * b;
        foreach (var p in new[] { FVector.Zero, new FVector(10f, -20f, 30f), new FVector(-7f, 3f, 100f) })
        {
            MathAssert.Near(b.TransformPosition(a.TransformPosition(p)), composed.TransformPosition(p), 5e-2f);
        }
    }

    [Theory]
    [MemberData(nameof(TransformPairs))]
    public void RelativeTransformInvertsComposition(FTransform relative, FTransform parent)
    {
        var world = relative * parent;
        var back = world.GetRelativeTransform(parent);
        foreach (var p in new[] { new FVector(1f, 2f, 3f), new FVector(-50f, 0f, 20f) })
        {
            MathAssert.Near(relative.TransformPosition(p), back.TransformPosition(p), 5e-2f);
        }

        MathAssert.Near(relative.Scale3D, back.Scale3D, 1e-3f);
    }

    [Theory]
    [MemberData(nameof(TransformPairs))]
    public void InverseAndMatrixConversions(FTransform transform, FTransform uniform)
    {
        var p = new FVector(12f, -34f, 56f);

        // InverseTransformPosition is exact for any scale; Inverse() for uniform scale.
        MathAssert.Near(p, transform.InverseTransformPosition(transform.TransformPosition(p)), 1e-2f);
        MathAssert.Near(p, uniform.Inverse().TransformPosition(uniform.TransformPosition(p)), 5e-2f);
        MathAssert.Near(FTransform.Identity, uniform * uniform.Inverse(), 2e-2f);

        // Matrix form uses the row-vector convention like UE's FMatrix.
        var matrix = transform.ToMatrixWithScale();
        MathAssert.Near(transform.TransformPosition(p).ToVector3(), Vector3.Transform(p.ToVector3(), matrix), 1e-2f);
        var decomposed = FTransform.FromMatrix(matrix);
        MathAssert.Near(transform.TransformPosition(p), decomposed.TransformPosition(p), 1e-2f);
        if (transform.Scale3D.X > 0f && transform.Scale3D.Y > 0f && transform.Scale3D.Z > 0f)
        {
            MathAssert.Near(transform, decomposed);
        }

        var noScale = transform.ToMatrixNoScale();
        MathAssert.Near(transform.TransformPositionNoScale(p).ToVector3(), Vector3.Transform(p.ToVector3(), noScale), 1e-2f);
    }

    [Fact]
    public void NegativeDeterminantBecomesNegativeXScale()
    {
        var mirrored = new FTransform(new FRotator(0f, 30f, 0f), new FVector(1f, 2f, 3f), new FVector(1f, -1f, 1f));
        var decomposed = FTransform.FromMatrix(mirrored.ToMatrixWithScale());
        Assert.True(decomposed.Scale3D.X < 0f);
        var p = new FVector(3f, 4f, 5f);
        MathAssert.Near(mirrored.TransformPosition(p), decomposed.TransformPosition(p), 1e-3f);
    }

    [Fact]
    public void IdentityAndRawFloats()
    {
        var transform = new FTransform(new FRotator(1f, 2f, 3f), new FVector(4f, 5f, 6f), new FVector(7f, 8f, 9f));
        MathAssert.Near(transform, FTransform.Identity * transform);
        MathAssert.Near(transform, transform * FTransform.Identity);

        Span<float> raw = stackalloc float[FTransform.RawFloatCount];
        transform.WriteRawFloats(raw);
        Assert.Equal(transform.Rotation.W, raw[3]);
        Assert.Equal(4f, raw[4]);
        Assert.Equal(9f, raw[9]);
        Assert.Equal(transform, FTransform.FromRawFloats(raw));
        Assert.Throws<ArgumentException>(() => FTransform.FromRawFloats(new float[9]));

        Assert.Equal(new FVector(1f, 2f, 3f), new FTransform(new FVector(1f, 2f, 3f)).TransformPosition(FVector.Zero));
        Assert.False(transform.ContainsNaN());
    }

    [Fact]
    public void ZeroScaleIsSafe()
    {
        var flat = new FTransform(FQuat.Identity, FVector.Zero, new FVector(1f, 1f, 0f));
        var inverse = flat.Inverse();
        Assert.False(inverse.ContainsNaN());
        Assert.Equal(0f, inverse.Scale3D.Z);
        Assert.False(flat.InverseTransformPosition(new FVector(1f, 1f, 1f)).ContainsNaN());
    }
}
