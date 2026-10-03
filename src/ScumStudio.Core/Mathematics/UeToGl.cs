using System.Numerics;

namespace ScumStudio.Core.Mathematics;

/// <summary>
/// Converts Unreal Engine space to the right-handed, Y-up world used by the OpenGL renderer.
/// </summary>
/// <remarks>
/// <para><b>Axis mapping</b> (a reflection that swaps Y and Z, so it is its own inverse):</para>
/// <code>
///   UE (left-handed, Z-up, cm)          GL world (right-handed, Y-up)
///   +X  forward                  --&gt;    +X
///   +Y  right                    --&gt;    +Z
///   +Z  up                       --&gt;    +Y
///   gl = (ue.X, ue.Z, ue.Y) * unitScale        ue = (gl.X, gl.Z, gl.Y) / unitScale
/// </code>
/// <para>
/// This is the same (x, y, z) to (x, z, y) swap the glTF/OBJ exporters use (MeshExportOptions.ConvertToYUp), so meshes
/// look identical in the viewport and in exported files. <c>unitScale</c> converts centimetres to render units
/// (1 = keep centimetres, 0.01 = metres) and applies to positions and translations only, never to directions.
/// </para>
/// <para>
/// A camera in the GL world looking along +X with +Y up has +Z on its right, which is exactly where UE's +Y (right)
/// lands, so the image is not mirrored. Because the mapping is a reflection, UE's clockwise front faces become
/// counter-clockwise: keep triangle index order and use GL's default <c>glFrontFace(GL_CCW)</c>. A transform whose
/// scale has an odd number of negative components flips winding again (<see cref="FlipsWinding"/>).
/// </para>
/// <para>
/// <b>Matrices</b> are <see cref="Matrix4x4"/> in the row-vector convention (<c>v' = v * M</c>; translation in
/// M41..M43). Their memory layout is exactly what <c>glUniformMatrix4fv(location, 1, transpose: false, ...)</c> expects
/// for a column-vector shader (<c>gl_Position = uProj * uView * uModel * vec4(pos, 1)</c>); on the CPU compose them as
/// <c>model * view * projection</c>.
/// </para>
/// <para>
/// Rotations: conjugating by the reflection maps a UE quaternion (x, y, z, w) to (-x, -z, -y, w) in GL
/// (<see cref="Rotation"/>).
/// </para>
/// </remarks>
public static class UeToGl
{
    /// <summary>
    /// The axis-swap matrix P (<c>gl = ue * P</c>, and <c>ue = gl * P</c>; P is its own inverse; determinant -1).
    /// </summary>
    public static Matrix4x4 AxisSwap { get; } = new(
        1f, 0f, 0f, 0f,
        0f, 0f, 1f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 0f, 1f);

    /// <summary>Converts a UE position (cm) to GL world units.</summary>
    public static Vector3 Point(FVector ue, float unitScale = 1f) => new(ue.X * unitScale, ue.Z * unitScale, ue.Y * unitScale);

    /// <summary>Converts a UE direction or normal to GL axes (no scaling).</summary>
    public static Vector3 Direction(FVector ue) => new(ue.X, ue.Z, ue.Y);

    /// <summary>Converts a GL world position back to UE centimetres.</summary>
    public static FVector ToUePoint(Vector3 gl, float unitScale = 1f) => new(gl.X / unitScale, gl.Z / unitScale, gl.Y / unitScale);

    /// <summary>Converts a GL direction back to UE axes.</summary>
    public static FVector ToUeDirection(Vector3 gl) => new(gl.X, gl.Z, gl.Y);

    /// <summary>
    /// Converts a UE rotation to the equivalent GL-world rotation, so that
    /// <c>Vector3.Transform(Direction(v), Rotation(q)) == Direction(q.RotateVector(v))</c>.
    /// </summary>
    public static Quaternion Rotation(FQuat ue) => new(-ue.X, -ue.Z, -ue.Y, ue.W);

    /// <summary>Inverse of <see cref="Rotation"/>.</summary>
    public static FQuat ToUeRotation(Quaternion gl) => new(-gl.X, -gl.Z, -gl.Y, gl.W);

    /// <summary>
    /// Converts a UE-space affine row-vector matrix to a GL-world matrix acting on GL-space vertices
    /// (<c>P * M * P</c>, translation multiplied by <paramref name="unitScale"/>).
    /// </summary>
    public static Matrix4x4 Matrix(in Matrix4x4 ueMatrix, float unitScale = 1f)
    {
        var gl = AxisSwap * ueMatrix * AxisSwap;
        gl.M41 *= unitScale;
        gl.M42 *= unitScale;
        gl.M43 *= unitScale;
        return gl;
    }

    /// <summary>
    /// Model matrix for vertices that were already converted with <see cref="Point"/> (same <paramref name="unitScale"/>),
    /// e.g. meshes uploaded in GL axes.
    /// </summary>
    public static Matrix4x4 ModelMatrix(in FTransform transform, float unitScale = 1f) =>
        Matrix(transform.ToMatrixWithScale(), unitScale);

    /// <summary>
    /// Model matrix for vertices still in UE space (raw <c>MeshData.Positions</c> in cm): applies the UE transform,
    /// then the axis swap and <paramref name="unitScale"/> (<c>M_ue * P * s</c>).
    /// </summary>
    public static Matrix4x4 ModelMatrixForUeVertices(in FTransform transform, float unitScale = 1f) =>
        transform.ToMatrixWithScale() * AxisSwap * Matrix4x4.CreateScale(unitScale);

    /// <summary>
    /// True when <paramref name="transform"/> mirrors geometry (odd number of negative scale components), so the
    /// renderer must flip the front-face winding for that draw relative to the default.
    /// </summary>
    public static bool FlipsWinding(in FTransform transform) =>
        transform.Scale3D.X * transform.Scale3D.Y * transform.Scale3D.Z < 0f;

    /// <summary>
    /// Right-handed view matrix for a camera placed and oriented in UE terms (e.g. a pawn's location and control
    /// rotation): looks along the rotator's forward vector with its up vector up.
    /// </summary>
    public static Matrix4x4 CreateViewMatrix(FVector cameraLocation, FRotator cameraRotation, float unitScale = 1f)
    {
        var q = cameraRotation.Quaternion();
        var eye = Point(cameraLocation, unitScale);
        var forward = Direction(q.GetForwardVector());
        var up = Direction(q.GetUpVector());
        return Matrix4x4.CreateLookAt(eye, eye + forward, up);
    }

    /// <summary>
    /// Right-handed OpenGL perspective projection with clip-space depth in [-1, 1] (the classic
    /// <c>gluPerspective</c>). <see cref="Matrix4x4.CreatePerspectiveFieldOfView"/> targets Direct3D's [0, 1] depth and
    /// is only correct in GL together with <c>glClipControl(GL_LOWER_LEFT, GL_ZERO_TO_ONE)</c>.
    /// </summary>
    /// <param name="fieldOfViewYRadians">Vertical field of view in radians (0, π).</param>
    /// <param name="aspectRatio">Width / height.</param>
    /// <param name="nearPlane">Distance to the near plane (&gt; 0).</param>
    /// <param name="farPlane">Distance to the far plane (&gt; near).</param>
    public static Matrix4x4 CreatePerspectiveGl(float fieldOfViewYRadians, float aspectRatio, float nearPlane, float farPlane)
    {
        if (fieldOfViewYRadians <= 0f || fieldOfViewYRadians >= MathF.PI)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldOfViewYRadians));
        }

        if (aspectRatio <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(aspectRatio));
        }

        if (nearPlane <= 0f || farPlane <= nearPlane)
        {
            throw new ArgumentOutOfRangeException(nameof(nearPlane), "Require 0 < near < far.");
        }

        var f = 1f / MathF.Tan(fieldOfViewYRadians * 0.5f);
        var range = nearPlane - farPlane;
        return new Matrix4x4(
            f / aspectRatio, 0f, 0f, 0f,
            0f, f, 0f, 0f,
            0f, 0f, (farPlane + nearPlane) / range, -1f,
            0f, 0f, 2f * farPlane * nearPlane / range, 0f);
    }
}
