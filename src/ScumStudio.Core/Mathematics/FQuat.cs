using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ScumStudio.Core.Mathematics;

/// <summary>
/// Unreal Engine 4 <c>FQuat</c>: a rotation quaternion in UE space (X forward, Y right, Z up, left-handed).
/// Serialised in cooked packages as 4 × float32 (X, Y, Z, W), e.g. the rotation part of an <c>FTransform</c>.
/// </summary>
/// <remarks>
/// Multiplication follows UE: <c>A * B</c> applies <c>B</c> first, then <c>A</c> (Hamilton product, identical to
/// <see cref="System.Numerics.Quaternion"/> component-wise). <see cref="RotateVector"/> computes <c>q v q*</c>.
/// Note that <c>default(FQuat)</c> is all zeros, not <see cref="Identity"/>.
/// </remarks>
/// <param name="X">Vector part X.</param>
/// <param name="Y">Vector part Y.</param>
/// <param name="Z">Vector part Z.</param>
/// <param name="W">Scalar part.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct FQuat(float X, float Y, float Z, float W)
{
    /// <summary>The identity rotation (0, 0, 0, 1).</summary>
    public static FQuat Identity => new(0f, 0f, 0f, 1f);

    /// <summary>
    /// Rotation of <paramref name="angleRadians"/> about the unit axis <paramref name="axis"/>
    /// (UE <c>FQuat(FVector Axis, float AngleRad)</c>).
    /// </summary>
    public static FQuat FromAxisAngle(FVector axis, float angleRadians)
    {
        var (s, c) = MathF.SinCos(0.5f * angleRadians);
        return new FQuat(s * axis.X, s * axis.Y, s * axis.Z, c);
    }

    /// <summary>Squared norm.</summary>
    public float SizeSquared() => X * X + Y * Y + Z * Z + W * W;

    /// <summary>Norm.</summary>
    public float Size() => MathF.Sqrt(SizeSquared());

    /// <summary>True when |1 - |q|²| is below <see cref="UeMath.QuatNormalizedThreshold"/> (UE <c>IsNormalized</c>).</summary>
    public bool IsNormalized() => MathF.Abs(1f - SizeSquared()) < UeMath.QuatNormalizedThreshold;

    /// <summary>True when any component is NaN or infinite.</summary>
    public bool ContainsNaN() => !float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Z) || !float.IsFinite(W);

    /// <summary>
    /// Unit-length copy; returns <see cref="Identity"/> when the squared norm is below <paramref name="tolerance"/>
    /// (UE <c>FQuat::GetNormalized</c>).
    /// </summary>
    public FQuat GetNormalized(float tolerance = UeMath.SmallNumber)
    {
        var squareSum = SizeSquared();
        if (squareSum >= tolerance)
        {
            var scale = 1f / MathF.Sqrt(squareSum);
            return new FQuat(X * scale, Y * scale, Z * scale, W * scale);
        }

        return Identity;
    }

    /// <summary>Inverse of a unit quaternion (the conjugate), UE <c>FQuat::Inverse</c>.</summary>
    public FQuat Inverse() => new(-X, -Y, -Z, W);

    /// <summary>Rotates <paramref name="v"/>: <c>v' = v + w·T + (q × T)</c> with <c>T = 2 (q × v)</c> (UE <c>RotateVector</c>).</summary>
    public FVector RotateVector(FVector v)
    {
        var q = new FVector(X, Y, Z);
        var t = 2f * FVector.Cross(q, v);
        return v + (W * t) + FVector.Cross(q, t);
    }

    /// <summary>Rotates <paramref name="v"/> by the inverse rotation (UE <c>UnrotateVector</c>).</summary>
    public FVector UnrotateVector(FVector v)
    {
        var q = new FVector(-X, -Y, -Z);
        var t = 2f * FVector.Cross(q, v);
        return v + (W * t) + FVector.Cross(q, t);
    }

    /// <summary>The rotated +X axis.</summary>
    public FVector GetForwardVector() => RotateVector(FVector.Forward);

    /// <summary>The rotated +Y axis.</summary>
    public FVector GetRightVector() => RotateVector(FVector.Right);

    /// <summary>The rotated +Z axis.</summary>
    public FVector GetUpVector() => RotateVector(FVector.Up);

    /// <summary>Rotation angle in radians, in [0, 2π] (UE <c>GetAngle</c>).</summary>
    public float GetAngle() => 2f * MathF.Acos(Math.Clamp(W, -1f, 1f));

    /// <summary>Unit rotation axis; +X for (near) identity (UE <c>GetRotationAxis</c>).</summary>
    public FVector GetRotationAxis()
    {
        var s = MathF.Sqrt(MathF.Max(1f - (W * W), 0f));
        return s >= 0.0001f ? new FVector(X / s, Y / s, Z / s) : FVector.Forward;
    }

    /// <summary>Angle in radians between two unit quaternions (UE <c>AngularDistance</c>).</summary>
    public float AngularDistance(FQuat other)
    {
        var inner = (X * other.X) + (Y * other.Y) + (Z * other.Z) + (W * other.W);
        return MathF.Acos(Math.Clamp((2f * inner * inner) - 1f, -1f, 1f));
    }

    /// <summary>
    /// Converts to Euler angles. Port of UE 4.27 <c>FQuat::Rotator</c>, including the gimbal-lock branches at
    /// pitch ±90° (threshold 0.4999995). <c>FMath::FastAsin</c> is replaced by the exact <see cref="MathF.Asin"/>.
    /// </summary>
    public FRotator Rotator()
    {
        const float singularityThreshold = 0.4999995f;
        var singularityTest = (Z * X) - (W * Y);
        var yawY = 2f * ((W * Z) + (X * Y));
        var yawX = 1f - (2f * ((Y * Y) + (Z * Z)));
        var yaw = MathF.Atan2(yawY, yawX) * UeMath.RadiansToDegrees;

        if (singularityTest < -singularityThreshold)
        {
            var roll = UeMath.NormalizeAxis(-yaw - (2f * MathF.Atan2(X, W) * UeMath.RadiansToDegrees));
            return new FRotator(-90f, yaw, roll);
        }

        if (singularityTest > singularityThreshold)
        {
            var roll = UeMath.NormalizeAxis(yaw - (2f * MathF.Atan2(X, W) * UeMath.RadiansToDegrees));
            return new FRotator(90f, yaw, roll);
        }

        var pitch = MathF.Asin(Math.Clamp(2f * singularityTest, -1f, 1f)) * UeMath.RadiansToDegrees;
        var rollAngle = MathF.Atan2(-2f * ((W * X) + (Y * Z)), 1f - (2f * ((X * X) + (Y * Y)))) * UeMath.RadiansToDegrees;
        return new FRotator(pitch, yaw, rollAngle);
    }

    /// <summary>Euler vector (X = Roll, Y = Pitch, Z = Yaw, degrees), UE <c>FQuat::Euler</c>.</summary>
    public FVector Euler() => Rotator().Euler();

    /// <summary>Quaternion from an Euler vector (X = Roll, Y = Pitch, Z = Yaw, degrees), UE <c>FQuat::MakeFromEuler</c>.</summary>
    public static FQuat MakeFromEuler(FVector euler) => FRotator.MakeFromEuler(euler).Quaternion();

    /// <summary>
    /// True when both quaternions represent the same rotation within <paramref name="tolerance"/> per component,
    /// treating q and -q as equal (UE <c>FQuat::Equals</c>).
    /// </summary>
    public bool Equals(FQuat other, float tolerance) =>
        (MathF.Abs(X - other.X) <= tolerance && MathF.Abs(Y - other.Y) <= tolerance &&
         MathF.Abs(Z - other.Z) <= tolerance && MathF.Abs(W - other.W) <= tolerance) ||
        (MathF.Abs(X + other.X) <= tolerance && MathF.Abs(Y + other.Y) <= tolerance &&
         MathF.Abs(Z + other.Z) <= tolerance && MathF.Abs(W + other.W) <= tolerance);

    /// <summary>
    /// Spherical interpolation along the shorter arc, normalised (UE <c>FQuat::Slerp</c>).
    /// </summary>
    public static FQuat Slerp(FQuat a, FQuat b, float alpha)
    {
        var rawCosom = (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W);
        var cosom = rawCosom >= 0f ? rawCosom : -rawCosom;
        float scale0;
        float scale1;
        if (cosom < 0.9999f)
        {
            var omega = MathF.Acos(cosom);
            var invSin = 1f / MathF.Sin(omega);
            scale0 = MathF.Sin((1f - alpha) * omega) * invSin;
            scale1 = MathF.Sin(alpha * omega) * invSin;
        }
        else
        {
            scale0 = 1f - alpha;
            scale1 = alpha;
        }

        scale1 = rawCosom >= 0f ? scale1 : -scale1;
        return new FQuat(
            (scale0 * a.X) + (scale1 * b.X),
            (scale0 * a.Y) + (scale1 * b.Y),
            (scale0 * a.Z) + (scale1 * b.Z),
            (scale0 * a.W) + (scale1 * b.W)).GetNormalized();
    }

    /// <summary>
    /// Shortest rotation taking unit vector <paramref name="from"/> to unit vector <paramref name="to"/>
    /// (UE <c>FQuat::FindBetweenNormals</c>).
    /// </summary>
    public static FQuat FindBetweenNormals(FVector from, FVector to) => FindBetweenHelper(from, to, 1f);

    /// <summary>
    /// Shortest rotation taking the direction of <paramref name="from"/> to that of <paramref name="to"/>
    /// (UE <c>FQuat::FindBetweenVectors</c>; inputs need not be normalised).
    /// </summary>
    public static FQuat FindBetweenVectors(FVector from, FVector to) =>
        FindBetweenHelper(from, to, MathF.Sqrt(from.SizeSquared() * to.SizeSquared()));

    private static FQuat FindBetweenHelper(FVector a, FVector b, float normAb)
    {
        var w = normAb + FVector.Dot(a, b);
        FQuat result;
        if (w >= 1e-6f * normAb)
        {
            result = new FQuat((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X), w);
        }
        else
        {
            // Opposite directions: rotate 180 degrees about any axis perpendicular to a.
            result = MathF.Abs(a.X) > MathF.Abs(a.Y)
                ? new FQuat(-a.Z, 0f, a.X, 0f)
                : new FQuat(0f, -a.Z, a.Y, 0f);
        }

        return result.GetNormalized();
    }

    /// <summary>Returns the same components as a <see cref="System.Numerics.Quaternion"/> (no axis conversion; UE space).</summary>
    public Quaternion ToQuaternion() => new(X, Y, Z, W);

    /// <summary>Creates a UE quaternion from a <see cref="System.Numerics.Quaternion"/> holding UE-space components.</summary>
    public static FQuat FromQuaternion(Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    /// <summary>
    /// Rotation matrix in UE space, row-vector convention (<c>v' = v * M</c>), equal to UE's
    /// <c>FQuatRotationMatrix</c> and to <see cref="Matrix4x4.CreateFromQuaternion"/>.
    /// </summary>
    public Matrix4x4 ToMatrix() => Matrix4x4.CreateFromQuaternion(ToQuaternion());

    /// <summary>
    /// Rotation part of a UE-space row-vector matrix (rows must be orthonormal). Port of UE
    /// <c>FQuat::FQuat(const FMatrix&amp;)</c>.
    /// </summary>
    public static FQuat FromMatrix(Matrix4x4 m) => UeMatrix.ToQuat(m);

    /// <summary>Composition: applies <paramref name="b"/> first, then <paramref name="a"/> (UE <c>operator*</c>).</summary>
    public static FQuat operator *(FQuat a, FQuat b) =>
        new(
            (a.W * b.X) + (a.X * b.W) + (a.Y * b.Z) - (a.Z * b.Y),
            (a.W * b.Y) - (a.X * b.Z) + (a.Y * b.W) + (a.Z * b.X),
            (a.W * b.Z) + (a.X * b.Y) - (a.Y * b.X) + (a.Z * b.W),
            (a.W * b.W) - (a.X * b.X) - (a.Y * b.Y) - (a.Z * b.Z));

    /// <summary>Rotates a vector (UE <c>FQuat::operator*(const FVector&amp;)</c>).</summary>
    public static FVector operator *(FQuat q, FVector v) => q.RotateVector(v);

    /// <summary>UE-style text, e.g. <c>X=0.000000 Y=0.000000 Z=0.707107 W=0.707107</c>.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"X={X:0.000000} Y={Y:0.000000} Z={Z:0.000000} W={W:0.000000}");
}
