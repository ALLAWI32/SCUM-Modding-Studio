using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ScumStudio.Core.Mathematics;

/// <summary>
/// Unreal Engine 4 <c>FTransform</c> (rotation, translation, non-uniform scale) in UE space
/// (centimetres; X forward, Y right, Z up; left-handed).
/// </summary>
/// <remarks>
/// <para>
/// A point is transformed as <c>Rotation.RotateVector(Scale3D * p) + Translation</c>: scale, then rotate, then translate.
/// Composition follows UE: <c>A * B</c> applies <c>A</c> first, then <c>B</c>. For a component attached to a parent,
/// <c>World = Relative * ParentWorld</c>.
/// </para>
/// <para>
/// Native serialisation (e.g. <c>FInstancedStaticMeshInstanceData</c>, bone poses) is 10 × float32:
/// Rotation X, Y, Z, W, Translation X, Y, Z, Scale3D X, Y, Z (see <see cref="FromRawFloats"/>).
/// Note that <c>default(FTransform)</c> is all zeros; use <see cref="Identity"/>.
/// </para>
/// </remarks>
/// <param name="Rotation">Rotation (should be normalised).</param>
/// <param name="Translation">Translation in centimetres.</param>
/// <param name="Scale3D">Per-axis scale.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct FTransform(FQuat Rotation, FVector Translation, FVector Scale3D)
{
    /// <summary>Number of floats in the native serialised form.</summary>
    public const int RawFloatCount = 10;

    /// <summary>The identity transform.</summary>
    public static FTransform Identity => new(FQuat.Identity, FVector.Zero, FVector.One);

    /// <summary>Creates a transform from a rotator, as a SceneComponent's RelativeRotation/Location/Scale3D.</summary>
    public FTransform(FRotator rotation, FVector translation, FVector scale3D)
        : this(rotation.Quaternion(), translation, scale3D)
    {
    }

    /// <summary>Creates a translation-only transform.</summary>
    public FTransform(FVector translation)
        : this(FQuat.Identity, translation, FVector.One)
    {
    }

    /// <summary>Rotation as Euler angles.</summary>
    public FRotator Rotator() => Rotation.Rotator();

    /// <summary>Transforms a point: scale, rotate, translate (UE <c>TransformPosition</c>).</summary>
    public FVector TransformPosition(FVector point) => Rotation.RotateVector(Scale3D * point) + Translation;

    /// <summary>Transforms a point ignoring scale (UE <c>TransformPositionNoScale</c>).</summary>
    public FVector TransformPositionNoScale(FVector point) => Rotation.RotateVector(point) + Translation;

    /// <summary>Transforms a direction: scale and rotate, no translation (UE <c>TransformVector</c>).</summary>
    public FVector TransformVector(FVector vector) => Rotation.RotateVector(Scale3D * vector);

    /// <summary>Rotates a direction, ignoring scale and translation (UE <c>TransformVectorNoScale</c>).</summary>
    public FVector TransformVectorNoScale(FVector vector) => Rotation.RotateVector(vector);

    /// <summary>Inverse of <see cref="TransformPosition"/> (UE <c>InverseTransformPosition</c>).</summary>
    public FVector InverseTransformPosition(FVector point) =>
        Rotation.UnrotateVector(point - Translation) * UeMath.SafeScaleReciprocal(Scale3D);

    /// <summary>Inverse of <see cref="TransformVector"/> (UE <c>InverseTransformVector</c>).</summary>
    public FVector InverseTransformVector(FVector vector) =>
        Rotation.UnrotateVector(vector) * UeMath.SafeScaleReciprocal(Scale3D);

    /// <summary>Applies this transform's rotation to <paramref name="rotation"/> (UE <c>TransformRotation</c>).</summary>
    public FQuat TransformRotation(FQuat rotation) => Rotation * rotation;

    /// <summary>Removes this transform's rotation from <paramref name="rotation"/> (UE <c>InverseTransformRotation</c>).</summary>
    public FQuat InverseTransformRotation(FQuat rotation) => Rotation.Inverse() * rotation;

    /// <summary>
    /// The inverse transform (UE <c>FTransform::Inverse</c>). Exact for uniform scale; with non-uniform scale and
    /// rotation an FTransform cannot represent the true inverse (shear), exactly as in UE.
    /// </summary>
    public FTransform Inverse()
    {
        var invRotation = Rotation.Inverse();
        var invScale = UeMath.SafeScaleReciprocal(Scale3D);
        var invTranslation = invRotation.RotateVector(invScale * -Translation);
        return new FTransform(invRotation, invTranslation, invScale);
    }

    /// <summary>
    /// Returns the transform <c>R</c> such that <c>R * other == this</c>, i.e. this transform expressed relative to
    /// <paramref name="other"/> (UE <c>GetRelativeTransform</c>; e.g. a world transform to a RelativeLocation/Rotation
    /// under a parent). Negative scales use UE's matrix path.
    /// </summary>
    public FTransform GetRelativeTransform(FTransform other)
    {
        if (AnyHasNegativeScale(Scale3D, other.Scale3D))
        {
            var safeRecip = UeMath.SafeScaleReciprocal(other.Scale3D);
            var desiredScale = Scale3D * safeRecip;
            return ConstructFromMatrixWithDesiredScale(
                ToMatrixWithScale(),
                UeMatrix.InverseOrIdentity(other.ToMatrixWithScale()),
                desiredScale);
        }

        var recipScale = UeMath.SafeScaleReciprocal(other.Scale3D);
        if (!other.Rotation.IsNormalized())
        {
            return Identity;
        }

        var inverse = other.Rotation.Inverse();
        return new FTransform(
            inverse * Rotation,
            inverse.RotateVector(Translation - other.Translation) * recipScale,
            Scale3D * recipScale);
    }

    /// <summary>
    /// Row-vector matrix in UE space including scale (UE <c>ToMatrixWithScale</c>):
    /// <c>Scale * Rotation * Translation</c>, so <c>Vector3.Transform(p, M) == TransformPosition(p)</c>.
    /// </summary>
    public Matrix4x4 ToMatrixWithScale()
    {
        var m = Matrix4x4.CreateFromQuaternion(Rotation.ToQuaternion());
        UeMatrix.SetAxis(ref m, 0, UeMatrix.GetScaledAxis(m, 0) * Scale3D.X);
        UeMatrix.SetAxis(ref m, 1, UeMatrix.GetScaledAxis(m, 1) * Scale3D.Y);
        UeMatrix.SetAxis(ref m, 2, UeMatrix.GetScaledAxis(m, 2) * Scale3D.Z);
        m.M41 = Translation.X;
        m.M42 = Translation.Y;
        m.M43 = Translation.Z;
        return m;
    }

    /// <summary>Row-vector matrix in UE space without scale (UE <c>ToMatrixNoScale</c>).</summary>
    public Matrix4x4 ToMatrixNoScale()
    {
        var m = Matrix4x4.CreateFromQuaternion(Rotation.ToQuaternion());
        m.M41 = Translation.X;
        m.M42 = Translation.Y;
        m.M43 = Translation.Z;
        return m;
    }

    /// <summary>
    /// Decomposes an affine row-vector UE-space matrix (UE <c>FTransform::SetFromMatrix</c>). A negative determinant is
    /// represented as a negative X scale.
    /// </summary>
    public static FTransform FromMatrix(Matrix4x4 matrix)
    {
        var m = matrix;
        var scale = UeMatrix.ExtractScaling(ref m);
        if (matrix.GetDeterminant() < 0f)
        {
            scale = new FVector(-scale.X, scale.Y, scale.Z);
            UeMatrix.SetAxis(ref m, 0, -UeMatrix.GetScaledAxis(m, 0));
        }

        var rotation = UeMatrix.ToQuat(m).GetNormalized();
        return new FTransform(rotation, UeMatrix.GetOrigin(matrix), scale);
    }

    /// <summary>
    /// Reads the native 10-float layout: rotation XYZW, translation XYZ, scale XYZ.
    /// </summary>
    public static FTransform FromRawFloats(ReadOnlySpan<float> values)
    {
        if (values.Length < RawFloatCount)
        {
            throw new ArgumentException($"An FTransform needs {RawFloatCount} floats.", nameof(values));
        }

        return new FTransform(
            new FQuat(values[0], values[1], values[2], values[3]),
            new FVector(values[4], values[5], values[6]),
            new FVector(values[7], values[8], values[9]));
    }

    /// <summary>Writes the native 10-float layout (see <see cref="FromRawFloats"/>).</summary>
    public void WriteRawFloats(Span<float> destination)
    {
        if (destination.Length < RawFloatCount)
        {
            throw new ArgumentException($"An FTransform needs {RawFloatCount} floats.", nameof(destination));
        }

        destination[0] = Rotation.X;
        destination[1] = Rotation.Y;
        destination[2] = Rotation.Z;
        destination[3] = Rotation.W;
        destination[4] = Translation.X;
        destination[5] = Translation.Y;
        destination[6] = Translation.Z;
        destination[7] = Scale3D.X;
        destination[8] = Scale3D.Y;
        destination[9] = Scale3D.Z;
    }

    /// <summary>Component-wise comparison within <paramref name="tolerance"/> (q and -q are equal), UE <c>Equals</c>.</summary>
    public bool Equals(FTransform other, float tolerance) =>
        Rotation.Equals(other.Rotation, tolerance) &&
        Translation.Equals(other.Translation, tolerance) &&
        Scale3D.Equals(other.Scale3D, tolerance);

    /// <summary>True when any component is NaN or infinite.</summary>
    public bool ContainsNaN() => Rotation.ContainsNaN() || Translation.ContainsNaN() || Scale3D.ContainsNaN();

    /// <summary>
    /// Composition: applies <paramref name="a"/> first, then <paramref name="b"/> (UE <c>FTransform::Multiply</c>);
    /// e.g. <c>childWorld = childRelative * parentWorld</c>. Negative scales use UE's matrix path.
    /// </summary>
    public static FTransform operator *(FTransform a, FTransform b)
    {
        if (AnyHasNegativeScale(a.Scale3D, b.Scale3D))
        {
            return ConstructFromMatrixWithDesiredScale(a.ToMatrixWithScale(), b.ToMatrixWithScale(), a.Scale3D * b.Scale3D);
        }

        return new FTransform(
            b.Rotation * a.Rotation,
            b.Rotation.RotateVector(b.Scale3D * a.Translation) + b.Translation,
            a.Scale3D * b.Scale3D);
    }

    /// <summary>UE-style text: translation, rotator and scale.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"T=({Translation}) R=({Rotator()}) S=({Scale3D})");

    private static bool AnyHasNegativeScale(FVector a, FVector b) =>
        a.X < 0f || a.Y < 0f || a.Z < 0f || b.X < 0f || b.Y < 0f || b.Z < 0f;

    /// <summary>Port of UE <c>FTransform::ConstructTransformFromMatrixWithDesiredScale</c>.</summary>
    private static FTransform ConstructFromMatrixWithDesiredScale(Matrix4x4 aMatrix, Matrix4x4 bMatrix, FVector desiredScale)
    {
        var m = aMatrix * bMatrix;
        UeMatrix.RemoveScaling(ref m);

        // Re-apply the sign of the desired scale to the axes.
        UeMatrix.SetAxis(ref m, 0, UeMatrix.GetScaledAxis(m, 0) * Sign(desiredScale.X));
        UeMatrix.SetAxis(ref m, 1, UeMatrix.GetScaledAxis(m, 1) * Sign(desiredScale.Y));
        UeMatrix.SetAxis(ref m, 2, UeMatrix.GetScaledAxis(m, 2) * Sign(desiredScale.Z));

        var rotation = UeMatrix.ToQuat(m).GetNormalized();
        return new FTransform(rotation, UeMatrix.GetOrigin(m), desiredScale);
    }

    // UE FVector::GetSignVector: FloatSelect(v, 1, -1), so 0 counts as positive.
    private static float Sign(float value) => value >= 0f ? 1f : -1f;
}
