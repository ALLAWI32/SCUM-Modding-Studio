using System.Globalization;
using System.Runtime.InteropServices;

namespace ScumStudio.Core.Mathematics;

/// <summary>
/// Unreal Engine 4 <c>FRotator</c>: Euler angles in degrees. Serialised in cooked packages as
/// 3 × float32 in the order Pitch, Yaw, Roll (e.g. <c>RelativeRotation</c> on a SceneComponent).
/// </summary>
/// <remarks>
/// UE conventions (UE space: X forward, Y right, Z up, left-handed):
/// <list type="bullet">
/// <item><b>Yaw</b> rotates about +Z; positive yaw turns +X towards +Y (yaw 90 maps X to Y).</item>
/// <item><b>Pitch</b> rotates about +Y; positive pitch lifts +X towards +Z (nose up).</item>
/// <item><b>Roll</b> rotates about +X; positive roll turns +Z towards +Y and +Y towards -Z.</item>
/// </list>
/// The combined rotation applies roll first, then pitch, then yaw (<c>FRotationMatrix</c> order).
/// </remarks>
/// <param name="Pitch">Rotation about the right (Y) axis, degrees.</param>
/// <param name="Yaw">Rotation about the up (Z) axis, degrees.</param>
/// <param name="Roll">Rotation about the forward (X) axis, degrees.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct FRotator(float Pitch, float Yaw, float Roll)
{
    /// <summary>The zero rotation.</summary>
    public static FRotator Zero => default;

    /// <summary>
    /// Converts to a quaternion. Port of UE 4.27 <c>FRotator::Quaternion</c> (non-SIMD path), float precision.
    /// </summary>
    public FQuat Quaternion()
    {
        const float halfDegToRad = UeMath.DegreesToRadians / 2f;
        var (sp, cp) = MathF.SinCos((Pitch % 360f) * halfDegToRad);
        var (sy, cy) = MathF.SinCos((Yaw % 360f) * halfDegToRad);
        var (sr, cr) = MathF.SinCos((Roll % 360f) * halfDegToRad);

        return new FQuat(
            (cr * sp * sy) - (sr * cp * cy),
            (-cr * sp * cy) - (sr * cp * sy),
            (cr * cp * sy) - (sr * sp * cy),
            (cr * cp * cy) + (sr * sp * sy));
    }

    /// <summary>Rotates <paramref name="v"/> by this rotation (UE <c>FRotator::RotateVector</c>).</summary>
    public FVector RotateVector(FVector v) => Quaternion().RotateVector(v);

    /// <summary>Rotates <paramref name="v"/> by the inverse of this rotation (UE <c>FRotator::UnrotateVector</c>).</summary>
    public FVector UnrotateVector(FVector v) => Quaternion().UnrotateVector(v);

    /// <summary>
    /// Unit forward direction of this rotation (UE <c>FRotator::Vector</c>): (cos P cos Y, cos P sin Y, sin P).
    /// </summary>
    public FVector Vector()
    {
        var (sp, cp) = MathF.SinCos((Pitch % 360f) * UeMath.DegreesToRadians);
        var (sy, cy) = MathF.SinCos((Yaw % 360f) * UeMath.DegreesToRadians);
        return new FVector(cp * cy, cp * sy, sp);
    }

    /// <summary>The inverse rotation (UE <c>FRotator::GetInverse</c>).</summary>
    public FRotator GetInverse() => Quaternion().Inverse().Rotator();

    /// <summary>Each axis wrapped to (-180, 180] (UE <c>FRotator::GetNormalized</c>).</summary>
    public FRotator GetNormalized() =>
        new(UeMath.NormalizeAxis(Pitch), UeMath.NormalizeAxis(Yaw), UeMath.NormalizeAxis(Roll));

    /// <summary>Each axis wrapped to [0, 360) (UE <c>FRotator::Clamp</c>).</summary>
    public FRotator Clamp() =>
        new(UeMath.ClampAxis(Pitch), UeMath.ClampAxis(Yaw), UeMath.ClampAxis(Roll));

    /// <summary>
    /// Euler vector as UE's <c>FRotator::Euler</c>: X = Roll, Y = Pitch, Z = Yaw (degrees).
    /// </summary>
    public FVector Euler() => new(Roll, Pitch, Yaw);

    /// <summary>Inverse of <see cref="Euler"/> (UE <c>FRotator::MakeFromEuler</c>): X = Roll, Y = Pitch, Z = Yaw.</summary>
    public static FRotator MakeFromEuler(FVector euler) => new(euler.Y, euler.Z, euler.X);

    /// <summary>
    /// True when every axis differs by at most <paramref name="tolerance"/> degrees after wrapping
    /// (port of <c>FRotator::Equals</c>, so 0 and 360 compare equal).
    /// </summary>
    public bool Equals(FRotator other, float tolerance) =>
        MathF.Abs(UeMath.NormalizeAxis(Pitch - other.Pitch)) <= tolerance &&
        MathF.Abs(UeMath.NormalizeAxis(Yaw - other.Yaw)) <= tolerance &&
        MathF.Abs(UeMath.NormalizeAxis(Roll - other.Roll)) <= tolerance;

    /// <summary>
    /// True when this rotator and <paramref name="other"/> describe the same orientation within
    /// <paramref name="tolerance"/> (compares quaternions, so e.g. (180, 0, 0) equals (0, 180, 180)).
    /// </summary>
    public bool IsSameOrientation(FRotator other, float tolerance = UeMath.KindaSmallNumber) =>
        Quaternion().Equals(other.Quaternion(), tolerance);

    /// <summary>True when every wrapped axis is within <paramref name="tolerance"/> of zero.</summary>
    public bool IsNearlyZero(float tolerance = UeMath.KindaSmallNumber) =>
        MathF.Abs(UeMath.NormalizeAxis(Pitch)) <= tolerance &&
        MathF.Abs(UeMath.NormalizeAxis(Yaw)) <= tolerance &&
        MathF.Abs(UeMath.NormalizeAxis(Roll)) <= tolerance;

    /// <summary>True when any component is NaN or infinite.</summary>
    public bool ContainsNaN() => !float.IsFinite(Pitch) || !float.IsFinite(Yaw) || !float.IsFinite(Roll);

    /// <summary>Component-wise sum (not a rotation composition; use quaternions for that).</summary>
    public static FRotator operator +(FRotator a, FRotator b) => new(a.Pitch + b.Pitch, a.Yaw + b.Yaw, a.Roll + b.Roll);

    /// <summary>Component-wise difference.</summary>
    public static FRotator operator -(FRotator a, FRotator b) => new(a.Pitch - b.Pitch, a.Yaw - b.Yaw, a.Roll - b.Roll);

    /// <summary>Component-wise scale.</summary>
    public static FRotator operator *(FRotator r, float s) => new(r.Pitch * s, r.Yaw * s, r.Roll * s);

    /// <summary>UE-style text, e.g. <c>P=0.000 Y=90.000 R=0.000</c> (invariant culture).</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"P={Pitch:0.000} Y={Yaw:0.000} R={Roll:0.000}");
}
