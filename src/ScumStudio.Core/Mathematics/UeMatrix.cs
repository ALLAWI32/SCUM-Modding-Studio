using System.Numerics;

namespace ScumStudio.Core.Mathematics;

/// <summary>
/// <c>FMatrix</c> operations on <see cref="Matrix4x4"/>. Both use the row-vector convention (<c>v' = v * M</c>):
/// rows 1-3 are the transformed X/Y/Z axes and row 4 is the origin, so UE's <c>M[r][c]</c> is <c>M(r+1)(c+1)</c>.
/// </summary>
public static class UeMatrix
{
    /// <summary>Row <paramref name="axis"/> (0 = X, 1 = Y, 2 = Z) including scale (UE <c>GetScaledAxis</c>).</summary>
    public static FVector GetScaledAxis(in Matrix4x4 m, int axis) => axis switch
    {
        0 => new FVector(m.M11, m.M12, m.M13),
        1 => new FVector(m.M21, m.M22, m.M23),
        2 => new FVector(m.M31, m.M32, m.M33),
        _ => throw new ArgumentOutOfRangeException(nameof(axis)),
    };

    /// <summary>Replaces row <paramref name="axis"/> (0 = X, 1 = Y, 2 = Z), UE <c>SetAxis</c>.</summary>
    public static void SetAxis(ref Matrix4x4 m, int axis, FVector value)
    {
        switch (axis)
        {
            case 0:
                (m.M11, m.M12, m.M13) = (value.X, value.Y, value.Z);
                break;
            case 1:
                (m.M21, m.M22, m.M23) = (value.X, value.Y, value.Z);
                break;
            case 2:
                (m.M31, m.M32, m.M33) = (value.X, value.Y, value.Z);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(axis));
        }
    }

    /// <summary>Translation row (UE <c>GetOrigin</c>).</summary>
    public static FVector GetOrigin(in Matrix4x4 m) => new(m.M41, m.M42, m.M43);

    /// <summary>
    /// Normalises the three axis rows in place, leaving rows shorter than sqrt(<paramref name="tolerance"/>) untouched
    /// (UE <c>FMatrix::RemoveScaling</c>).
    /// </summary>
    public static void RemoveScaling(ref Matrix4x4 m, float tolerance = UeMath.SmallNumber)
    {
        for (var axis = 0; axis < 3; axis++)
        {
            var row = GetScaledAxis(m, axis);
            var squareSum = row.SizeSquared();
            var scale = squareSum - tolerance >= 0f ? 1f / MathF.Sqrt(squareSum) : 1f;
            SetAxis(ref m, axis, row * scale);
        }
    }

    /// <summary>
    /// Returns the per-axis scale and normalises the axis rows in place; zero-length rows give scale 0
    /// (UE <c>FMatrix::ExtractScaling</c>).
    /// </summary>
    public static FVector ExtractScaling(ref Matrix4x4 m, float tolerance = UeMath.SmallNumber)
    {
        Span<float> scale = stackalloc float[3];
        for (var axis = 0; axis < 3; axis++)
        {
            var row = GetScaledAxis(m, axis);
            var squareSum = row.SizeSquared();
            if (squareSum > tolerance)
            {
                var length = MathF.Sqrt(squareSum);
                scale[axis] = length;
                SetAxis(ref m, axis, row * (1f / length));
            }
            else
            {
                scale[axis] = 0f;
            }
        }

        return new FVector(scale[0], scale[1], scale[2]);
    }

    /// <summary>Inverse, or identity when the matrix is singular (UE <c>FMatrix::Inverse</c>).</summary>
    public static Matrix4x4 InverseOrIdentity(in Matrix4x4 m)
    {
        if (GetScaledAxis(m, 0).IsNearlyZero(UeMath.SmallNumber) &&
            GetScaledAxis(m, 1).IsNearlyZero(UeMath.SmallNumber) &&
            GetScaledAxis(m, 2).IsNearlyZero(UeMath.SmallNumber))
        {
            return Matrix4x4.Identity;
        }

        return Matrix4x4.Invert(m, out var inverse) ? inverse : Matrix4x4.Identity;
    }

    /// <summary>
    /// Rotation quaternion of an orthonormal row-vector matrix. Port of UE <c>FQuat::FQuat(const FMatrix&amp;)</c>.
    /// </summary>
    public static FQuat ToQuat(in Matrix4x4 m)
    {
        // Matrix4x4[row, column] is UE's M[r][c], so the UE algorithm is ported literally.
        var trace = m[0, 0] + m[1, 1] + m[2, 2];
        if (trace > 0f)
        {
            var invS = 1f / MathF.Sqrt(trace + 1f);
            var w = 0.5f * (1f / invS);
            var s = 0.5f * invS;
            return new FQuat((m[1, 2] - m[2, 1]) * s, (m[2, 0] - m[0, 2]) * s, (m[0, 1] - m[1, 0]) * s, w);
        }

        var i = 0;
        if (m[1, 1] > m[0, 0])
        {
            i = 1;
        }

        if (m[2, 2] > m[i, i])
        {
            i = 2;
        }

        ReadOnlySpan<int> next = [1, 2, 0];
        var j = next[i];
        var k = next[j];
        var sq = m[i, i] - m[j, j] - m[k, k] + 1f;
        var inv = 1f / MathF.Sqrt(sq);
        Span<float> qt = stackalloc float[4];
        qt[i] = 0.5f * (1f / inv);
        var scale = 0.5f * inv;
        qt[3] = (m[j, k] - m[k, j]) * scale;
        qt[j] = (m[i, j] + m[j, i]) * scale;
        qt[k] = (m[i, k] + m[k, i]) * scale;
        return new FQuat(qt[0], qt[1], qt[2], qt[3]);
    }
}
