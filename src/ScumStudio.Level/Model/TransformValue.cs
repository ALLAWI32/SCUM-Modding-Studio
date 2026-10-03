using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Model;

/// <summary>
/// A transform the way a scene component stores it and an editor shows it: <c>RelativeLocation</c> (cm),
/// <c>RelativeRotation</c> (degrees) and <c>RelativeScale3D</c>. Also used for ISM instance transforms. Edit operations
/// carry these values verbatim so replaying them writes exactly what the user typed.
/// </summary>
/// <param name="Location">Location in centimetres.</param>
/// <param name="Rotation">Rotation (pitch, yaw, roll) in degrees.</param>
/// <param name="Scale">Per-axis scale.</param>
public readonly record struct TransformValue(FVector Location, FRotator Rotation, FVector Scale)
{
    /// <summary>Zero location and rotation, unit scale.</summary>
    public static TransformValue Identity => new(FVector.Zero, FRotator.Zero, FVector.One);

    /// <summary>Creates a translation-only value.</summary>
    public static TransformValue At(float x, float y, float z) => new(new FVector(x, y, z), FRotator.Zero, FVector.One);

    /// <summary>Converts to an <see cref="FTransform"/> (quaternion rotation).</summary>
    public FTransform ToTransform() => new(Rotation, Location, Scale);

    /// <summary>Converts from an <see cref="FTransform"/>; the rotator is derived from the quaternion (UE <c>FQuat::Rotator</c>).</summary>
    public static TransformValue FromTransform(FTransform transform) =>
        new(transform.Translation, transform.Rotator(), transform.Scale3D);

    /// <summary>Compares the resulting transforms within <paramref name="tolerance"/> (equivalent rotators compare equal).</summary>
    public bool IsNearlyEqual(TransformValue other, float tolerance = UeMath.KindaSmallNumber) =>
        ToTransform().Equals(other.ToTransform(), tolerance);

    /// <inheritdoc />
    public override string ToString() => $"loc ({Location}) rot ({Rotation}) scale ({Scale})";
}
