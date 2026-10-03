using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ScumStudio.Core.Mathematics;

/// <summary>
/// Unreal Engine 4 <c>FVector</c>: three single-precision components in UE space
/// (centimetres; X forward, Y right, Z up; left-handed). Serialised in cooked packages as 3 × float32 (X, Y, Z).
/// </summary>
/// <param name="X">Forward component.</param>
/// <param name="Y">Right component.</param>
/// <param name="Z">Up component.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct FVector(float X, float Y, float Z)
{
    /// <summary>(0, 0, 0).</summary>
    public static FVector Zero => default;

    /// <summary>(1, 1, 1), the identity scale.</summary>
    public static FVector One => new(1f, 1f, 1f);

    /// <summary>(1, 0, 0): UE forward.</summary>
    public static FVector Forward => new(1f, 0f, 0f);

    /// <summary>(0, 1, 0): UE right.</summary>
    public static FVector Right => new(0f, 1f, 0f);

    /// <summary>(0, 0, 1): UE up.</summary>
    public static FVector Up => new(0f, 0f, 1f);

    /// <summary>Creates a vector with all three components set to <paramref name="value"/>.</summary>
    public FVector(float value)
        : this(value, value, value)
    {
    }

    /// <summary>Euclidean length.</summary>
    public float Size() => MathF.Sqrt(SizeSquared());

    /// <summary>Squared length.</summary>
    public float SizeSquared() => X * X + Y * Y + Z * Z;

    /// <summary>Length of the XY projection (horizontal distance in UE).</summary>
    public float Size2D() => MathF.Sqrt(X * X + Y * Y);

    /// <summary>True when every component's magnitude is at most <paramref name="tolerance"/>.</summary>
    public bool IsNearlyZero(float tolerance = UeMath.KindaSmallNumber) =>
        MathF.Abs(X) <= tolerance && MathF.Abs(Y) <= tolerance && MathF.Abs(Z) <= tolerance;

    /// <summary>True when any component is NaN or infinite.</summary>
    public bool ContainsNaN() => !float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Z);

    /// <summary>
    /// Unit-length copy, or <see cref="Zero"/> when the squared length is below <paramref name="tolerance"/>
    /// (port of <c>FVector::GetSafeNormal</c>).
    /// </summary>
    public FVector GetSafeNormal(float tolerance = UeMath.SmallNumber)
    {
        var squareSum = SizeSquared();
        if (squareSum == 1f)
        {
            return this;
        }

        if (squareSum < tolerance)
        {
            return Zero;
        }

        var scale = 1f / MathF.Sqrt(squareSum);
        return new FVector(X * scale, Y * scale, Z * scale);
    }

    /// <summary>
    /// Per-component comparison within <paramref name="tolerance"/> (port of <c>FVector::Equals</c>).
    /// </summary>
    public bool Equals(FVector other, float tolerance) =>
        MathF.Abs(X - other.X) <= tolerance && MathF.Abs(Y - other.Y) <= tolerance && MathF.Abs(Z - other.Z) <= tolerance;

    /// <summary>Dot product (UE <c>operator|</c>).</summary>
    public static float Dot(FVector a, FVector b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>Cross product (UE <c>operator^</c>; same formula as in right-handed maths).</summary>
    public static FVector Cross(FVector a, FVector b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    /// <summary>Distance between two points.</summary>
    public static float Distance(FVector a, FVector b) => (a - b).Size();

    /// <summary>Component-wise minimum.</summary>
    public static FVector Min(FVector a, FVector b) => new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));

    /// <summary>Component-wise maximum.</summary>
    public static FVector Max(FVector a, FVector b) => new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));

    /// <summary>Linear interpolation.</summary>
    public static FVector Lerp(FVector a, FVector b, float alpha) => a + (b - a) * alpha;

    /// <summary>Returns the same components as a <see cref="Vector3"/> (no axis conversion; still UE space).</summary>
    public Vector3 ToVector3() => new(X, Y, Z);

    /// <summary>Creates a UE vector from a <see cref="Vector3"/> holding UE-space components (no axis conversion).</summary>
    public static FVector FromVector3(Vector3 value) => new(value.X, value.Y, value.Z);

    /// <summary>Sum.</summary>
    public static FVector operator +(FVector a, FVector b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    /// <summary>Difference.</summary>
    public static FVector operator -(FVector a, FVector b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>Negation.</summary>
    public static FVector operator -(FVector v) => new(-v.X, -v.Y, -v.Z);

    /// <summary>Component-wise product (used for scaling by a Scale3D).</summary>
    public static FVector operator *(FVector a, FVector b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    /// <summary>Scalar product.</summary>
    public static FVector operator *(FVector v, float s) => new(v.X * s, v.Y * s, v.Z * s);

    /// <summary>Scalar product.</summary>
    public static FVector operator *(float s, FVector v) => new(v.X * s, v.Y * s, v.Z * s);

    /// <summary>Scalar division.</summary>
    public static FVector operator /(FVector v, float s) => new(v.X / s, v.Y / s, v.Z / s);

    /// <summary>UE-style text, e.g. <c>X=100.000 Y=-50.000 Z=20.000</c> (invariant culture).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"X={X:0.000} Y={Y:0.000} Z={Z:0.000}");
}
