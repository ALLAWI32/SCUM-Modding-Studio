using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Model;

/// <summary>
/// Bends a mesh along a spline the way <c>USplineMeshComponent</c> does (port of
/// <c>CalcSliceTransform</c> / <c>CalcSliceTransformAtSplineOffset</c>, UE 4.27 <c>SplineMeshComponent.cpp</c>, and of the
/// <c>CalcSliceTransform</c> / <c>CalcSliceRot</c> pair of <c>LocalVertexFactory.ush</c> that the game runs per vertex).
/// Pure CPU code: no GL, no asset access.
/// </summary>
/// <remarks>
/// For each vertex, its coordinate along <see cref="SplineMeshParams.ForwardAxis"/> picks a point of the Hermite curve
/// (the ratio comes from the mesh bounds, or from <c>SplineBoundaryMin/Max</c> when set) and the other two coordinates are
/// laid out in the frame at that point: <c>X = SplineUpDir × direction</c>, <c>Y = direction × X</c>, rolled, scaled and
/// offset with values interpolated between the start and end (through a smooth-step when
/// <see cref="SplineMeshParams.SmoothInterpRollScale"/>). Normals go through the same frame (including its scale, as the
/// shader does) and are renormalised. The result stays in component space.
/// </remarks>
public static class SplineMeshDeformer
{
    /// <summary>
    /// A copy of <paramref name="mesh"/> bent along <paramref name="spline"/>: positions and normals deformed, UVs, indices and
    /// sections shared, bounds recomputed. The mesh's own bounds define the range mapped onto the spline, as the engine uses
    /// the static mesh bounds; pass <paramref name="range"/> (LOD 0's bounds) when bending a coarser LOD so all LODs line up.
    /// </summary>
    public static MeshData Deform(MeshData mesh, SplineMeshParams spline, BoundingBox? range = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(spline);
        var (dirMask, xMask, yMask) = AxisMasks(spline.ForwardAxis);
        var positions = new float[mesh.Positions.Length];
        var hasNormals = mesh.Normals.Length == mesh.Positions.Length;
        var normals = hasNormals ? new float[mesh.Normals.Length] : [];
        for (var i = 0; i < positions.Length; i += 3)
        {
            var p = new FVector(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]);
            var alpha = ComputeRatioAlongSpline(spline, range ?? mesh.Bounds, FVector.Dot(p, dirMask));
            var frame = SliceFrame(spline, alpha);
            var bent = frame.Position + frame.X * FVector.Dot(p, xMask) + frame.Y * FVector.Dot(p, yMask);
            positions[i] = bent.X;
            positions[i + 1] = bent.Y;
            positions[i + 2] = bent.Z;
            if (hasNormals)
            {
                var n = new FVector(mesh.Normals[i], mesh.Normals[i + 1], mesh.Normals[i + 2]);
                var rotated = (frame.Direction * FVector.Dot(n, dirMask) + frame.X * FVector.Dot(n, xMask) + frame.Y * FVector.Dot(n, yMask)).GetSafeNormal();
                normals[i] = rotated.X;
                normals[i + 1] = rotated.Y;
                normals[i + 2] = rotated.Z;
            }
        }

        return mesh with { Positions = positions, Normals = normals, Bounds = BoundingBox.FromPositions(positions) };
    }

    /// <summary>
    /// The mesh drawn once along each of <paramref name="pieces"/> (a longer piece repeated, see
    /// <c>SplineTiles</c>), as one mesh: each material section keeps all its triangles together.
    /// </summary>
    public static MeshData DeformPieces(MeshData mesh, IReadOnlyList<SplineMeshParams> pieces, BoundingBox? range = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(pieces);
        if (pieces.Count == 1)
        {
            return Deform(mesh, pieces[0], range);
        }

        var parts = pieces.Select(p => Deform(mesh, p, range)).ToList();
        var vertices = (uint)mesh.VertexCount;
        var indices = new List<uint>(mesh.Indices.Length * parts.Count);
        var sections = new List<MeshSection>(mesh.Sections.Length);
        foreach (var section in mesh.Sections)
        {
            var first = indices.Count;
            for (var c = 0; c < parts.Count; c++)
            {
                for (var i = section.FirstIndex; i < section.FirstIndex + section.IndexCount; i++)
                {
                    indices.Add(mesh.Indices[i] + ((uint)c * vertices));
                }
            }

            sections.Add(section with { FirstIndex = first, IndexCount = indices.Count - first });
        }

        var positions = parts.SelectMany(p => p.Positions).ToArray();
        return mesh with
        {
            Positions = positions,
            Normals = parts.SelectMany(p => p.Normals).ToArray(),
            Uv0 = Enumerable.Repeat(mesh.Uv0, parts.Count).SelectMany(u => u).ToArray(),
            Indices = [.. indices],
            Sections = [.. sections],
            Bounds = BoundingBox.FromPositions(positions),
        };
    }

    /// <summary>Transform of the mesh slice at <paramref name="distanceAlong"/> (forward-axis coordinate), as <c>CalcSliceTransform</c>.</summary>
    public static FTransform CalcSliceTransform(SplineMeshParams spline, BoundingBox meshBounds, float distanceAlong) =>
        CalcSliceTransformAtSplineOffset(spline, ComputeRatioAlongSpline(spline, meshBounds, distanceAlong));

    /// <summary>
    /// Ratio along the spline of a forward-axis coordinate (<c>ComputeRatioAlongSpline</c>): 0 at
    /// <c>SplineBoundaryMin</c> and 1 at <c>SplineBoundaryMax</c>, or across the mesh bounds when those are equal.
    /// </summary>
    public static float ComputeRatioAlongSpline(SplineMeshParams spline, BoundingBox meshBounds, float distanceAlong)
    {
        ArgumentNullException.ThrowIfNull(spline);
        if (MathF.Abs(spline.SplineBoundaryMax - spline.SplineBoundaryMin) > UeMath.SmallNumber)
        {
            return (distanceAlong - spline.SplineBoundaryMin) / (spline.SplineBoundaryMax - spline.SplineBoundaryMin);
        }

        var axis = (int)spline.ForwardAxis;
        var min = AxisValue(meshBounds.Min, axis);
        var range = AxisValue(meshBounds.Max, axis) - min;
        return range > UeMath.SmallNumber ? (distanceAlong - min) / range : 0f;
    }

    /// <summary>
    /// Transform of the slice at spline ratio <paramref name="alpha"/> (<c>CalcSliceTransformAtSplineOffset</c>): the frame
    /// axes in the order of the forward axis, the spline point as translation, and the cross-section scale on the two
    /// other axes (1 along the spline).
    /// </summary>
    public static FTransform CalcSliceTransformAtSplineOffset(SplineMeshParams spline, float alpha)
    {
        ArgumentNullException.ThrowIfNull(spline);
        var frame = SliceFrame(spline, alpha, scaled: false);
        var scale = Lerp(spline.StartScale, spline.EndScale, HermiteAlpha(spline, alpha));
        var (x, y, z, scale3D) = spline.ForwardAxis switch
        {
            SplineMeshAxis.Y => (frame.Y, frame.Direction, frame.X, new FVector(scale.Y, 1f, scale.X)),
            SplineMeshAxis.Z => (frame.X, frame.Y, frame.Direction, new FVector(scale.X, scale.Y, 1f)),
            _ => (frame.Direction, frame.X, frame.Y, new FVector(1f, scale.X, scale.Y)),
        };
        var matrix = new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            frame.Position.X, frame.Position.Y, frame.Position.Z, 1f);
        return FTransform.FromMatrix(matrix) with { Scale3D = scale3D };
    }

    /// <summary>Cubic Hermite position at <paramref name="a"/> (<c>SplineEvalPos</c>).</summary>
    public static FVector SplineEvalPos(FVector startPos, FVector startTangent, FVector endPos, FVector endTangent, float a)
    {
        var a2 = a * a;
        var a3 = a2 * a;
        return (2 * a3 - 3 * a2 + 1) * startPos + (a3 - 2 * a2 + a) * startTangent + (a3 - a2) * endTangent + (-2 * a3 + 3 * a2) * endPos;
    }

    /// <summary>Unit tangent of the cubic Hermite curve at <paramref name="a"/> (<c>SplineEvalDir</c>).</summary>
    public static FVector SplineEvalDir(FVector startPos, FVector startTangent, FVector endPos, FVector endTangent, float a)
    {
        var c = 6 * startPos + 3 * startTangent + 3 * endTangent - 6 * endPos;
        var d = -6 * startPos - 4 * startTangent - 2 * endTangent + 6 * endPos;
        return (c * (a * a) + d * a + startTangent).GetSafeNormal();
    }

    /// <summary>
    /// The frame at ratio <paramref name="alpha"/>: spline point (offset applied), unit direction, and the rolled cross
    /// axes X (<c>SplineUpDir × direction</c>) and Y (<c>direction × X</c>), multiplied by the cross-section scale when
    /// <paramref name="scaled"/>.
    /// </summary>
    private static (FVector Position, FVector Direction, FVector X, FVector Y) SliceFrame(SplineMeshParams s, float alpha, bool scaled = true)
    {
        var hermiteAlpha = HermiteAlpha(s, alpha);
        var position = SplineEvalPos(s.StartPos, s.StartTangent, s.EndPos, s.EndTangent, alpha);
        var direction = SplineEvalDir(s.StartPos, s.StartTangent, s.EndPos, s.EndTangent, alpha);
        var baseX = FVector.Cross(s.SplineUpDir, direction).GetSafeNormal();
        var baseY = FVector.Cross(direction, baseX).GetSafeNormal();

        var offset = Lerp(s.StartOffset, s.EndOffset, hermiteAlpha);
        position += offset.X * baseX + offset.Y * baseY;

        var roll = s.StartRoll + (s.EndRoll - s.StartRoll) * hermiteAlpha;
        var (sin, cos) = MathF.SinCos(roll);
        var x = cos * baseX - sin * baseY;
        var y = cos * baseY + sin * baseX;
        if (scaled)
        {
            var scale = Lerp(s.StartScale, s.EndScale, hermiteAlpha);
            x *= scale.X;
            y *= scale.Y;
        }

        return (position, direction, x, y);
    }

    /// <summary>The ratio used for roll, scale and offset: a smooth-step of <paramref name="alpha"/> when the component asks for it.</summary>
    private static float HermiteAlpha(SplineMeshParams s, float alpha) => s.SmoothInterpRollScale ? SmoothStep(alpha) : alpha;

    /// <summary><c>FMath::SmoothStep(0, 1, x)</c>.</summary>
    private static float SmoothStep(float x) => x <= 0f ? 0f : x >= 1f ? 1f : x * x * (3f - 2f * x);

    private static Vector2 Lerp(Vector2 a, Vector2 b, float alpha) => a + (b - a) * alpha;

    private static float AxisValue(Vector3 v, int axis) => axis switch { 1 => v.Y, 2 => v.Z, _ => v.X };

    /// <summary>Which mesh coordinate runs along the spline and which two go to the frame's X and Y (the shader's SplineMeshDir/X/Y masks).</summary>
    private static (FVector Dir, FVector X, FVector Y) AxisMasks(SplineMeshAxis axis) => axis switch
    {
        SplineMeshAxis.Y => (FVector.Right, FVector.Up, FVector.Forward),
        SplineMeshAxis.Z => (FVector.Up, FVector.Forward, FVector.Right),
        _ => (FVector.Forward, FVector.Right, FVector.Up),
    };
}
