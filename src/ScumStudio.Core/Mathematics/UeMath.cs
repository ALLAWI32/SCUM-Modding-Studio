namespace ScumStudio.Core.Mathematics;

/// <summary>
/// Scalar constants and helpers that mirror Unreal Engine 4.27's <c>FMath</c> / <c>FRotator</c> statics.
/// </summary>
/// <remarks>
/// Unreal Engine space, used by every type in this namespace: units are centimetres, +X is forward, +Y is right,
/// +Z is up, and the frame is left-handed. Rotations are in degrees on <see cref="FRotator"/> and radians elsewhere.
/// </remarks>
public static class UeMath
{
    /// <summary><c>SMALL_NUMBER</c> (1e-8).</summary>
    public const float SmallNumber = 1e-8f;

    /// <summary><c>KINDA_SMALL_NUMBER</c> (1e-4), UE's default comparison tolerance.</summary>
    public const float KindaSmallNumber = 1e-4f;

    /// <summary><c>THRESH_QUAT_NORMALIZED</c>: a quaternion counts as normalised when |1 - |q|²| is below this.</summary>
    public const float QuatNormalizedThreshold = 0.01f;

    /// <summary>Degrees to radians factor.</summary>
    public const float DegreesToRadians = MathF.PI / 180f;

    /// <summary>Radians to degrees factor.</summary>
    public const float RadiansToDegrees = 180f / MathF.PI;

    /// <summary>
    /// Wraps an angle in degrees to [0, 360). Port of <c>FRotator::ClampAxis</c>.
    /// </summary>
    public static float ClampAxis(float angle)
    {
        // C#'s float % has C fmod semantics (result takes the sign of the dividend), like FMath::Fmod.
        angle %= 360f;
        if (angle < 0f)
        {
            angle += 360f;
        }

        return angle;
    }

    /// <summary>
    /// Wraps an angle in degrees to (-180, 180]. Port of <c>FRotator::NormalizeAxis</c>.
    /// </summary>
    public static float NormalizeAxis(float angle)
    {
        angle = ClampAxis(angle);
        if (angle > 180f)
        {
            angle -= 360f;
        }

        return angle;
    }

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> differ by at most <paramref name="tolerance"/>.</summary>
    public static bool IsNearlyEqual(float a, float b, float tolerance = SmallNumber) => MathF.Abs(a - b) <= tolerance;

    /// <summary>Component-wise reciprocal that yields 0 for components whose magnitude is at most <paramref name="tolerance"/>
    /// (port of <c>FTransform::GetSafeScaleReciprocal</c>).</summary>
    public static FVector SafeScaleReciprocal(FVector scale, float tolerance = SmallNumber) =>
        new(
            MathF.Abs(scale.X) <= tolerance ? 0f : 1f / scale.X,
            MathF.Abs(scale.Y) <= tolerance ? 0f : 1f / scale.Y,
            MathF.Abs(scale.Z) <= tolerance ? 0f : 1f / scale.Z);
}
