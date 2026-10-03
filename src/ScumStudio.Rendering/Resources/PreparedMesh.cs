using System.Numerics;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Rendering.Resources;

/// <summary>A material section of one LOD: an index range of <see cref="PreparedMesh.Indices"/> drawn with one texture.</summary>
/// <param name="FirstIndex">First element in <see cref="PreparedMesh.Indices"/>.</param>
/// <param name="IndexCount">Number of indices (a triangle list).</param>
/// <param name="BaseVertex">Added to every index (the LOD's first vertex in <see cref="PreparedMesh.Vertices"/>).</param>
/// <param name="Material">Material object path or name of the section (empty when unknown); textures are looked up by it.</param>
public readonly record struct PreparedSection(int FirstIndex, int IndexCount, int BaseVertex, string Material);

/// <summary>One level of detail of a <see cref="PreparedMesh"/>.</summary>
/// <param name="ScreenSize">
/// Screen size (the mesh's bounding sphere diameter as a fraction of the view height, UE's convention) below which the
/// next coarser LOD takes over; the finest LOD is drawn above its own value.
/// </param>
/// <param name="Sections">Material sections of the LOD.</param>
public sealed record PreparedLod(float ScreenSize, PreparedSection[] Sections)
{
    /// <summary>Number of indices over all sections.</summary>
    public int IndexCount => Sections.Sum(s => s.IndexCount);
}

/// <summary>
/// CPU-side vertex data ready for upload: interleaved position (3), normal (3), uv (2) floats per vertex in the
/// renderer's GL world, plus triangle indices and GL-space bounds. The vertices and indices of every LOD are packed
/// one after another; <see cref="Lods"/> names the ranges (finest LOD first).
/// </summary>
/// <param name="Name">Mesh name.</param>
/// <param name="Vertices">Interleaved vertices, <see cref="FloatsPerVertex"/> floats each.</param>
/// <param name="Indices">Triangle-list indices, relative to each LOD's <see cref="PreparedSection.BaseVertex"/>.</param>
/// <param name="Bounds">Bounds of the converted positions (all LODs).</param>
/// <param name="Lods">Levels of detail, finest first (at least one).</param>
public sealed record PreparedMesh(string Name, float[] Vertices, uint[] Indices, BoundingBox Bounds, PreparedLod[] Lods)
{
    /// <summary>Floats per interleaved vertex (position, normal, uv).</summary>
    public const int FloatsPerVertex = 8;

    /// <summary>Vertex stride in bytes.</summary>
    public const int Stride = FloatsPerVertex * sizeof(float);

    /// <summary>Number of vertices (all LODs).</summary>
    public int VertexCount => Vertices.Length / FloatsPerVertex;

    /// <summary>
    /// Converts <paramref name="mesh"/> (a single LOD) to the renderer layout. <see cref="MeshSpace.Unreal"/> swaps Y and Z
    /// (<c>UeToGl.Point</c>) and scales positions by <paramref name="unitScale"/>; missing normals are computed from the
    /// triangles (area-weighted), missing UVs become zero.
    /// </summary>
    /// <exception cref="ArgumentException">The mesh is structurally invalid (<see cref="MeshData.Validate"/>).</exception>
    public static PreparedMesh From(MeshData mesh, MeshSpace space = MeshSpace.Unreal, float unitScale = 1f) =>
        FromLods([mesh], [1f], space, unitScale);

    /// <summary>
    /// Converts a LOD chain (finest first) the way <see cref="From"/> converts one mesh, packing all LODs into one vertex
    /// and one index array; <paramref name="screenSizes"/> gives each LOD's <see cref="PreparedLod.ScreenSize"/>
    /// (missing entries halve the previous one).
    /// </summary>
    /// <exception cref="ArgumentException">A LOD is structurally invalid, or the list is empty.</exception>
    public static PreparedMesh FromLods(IReadOnlyList<MeshData> lods, ReadOnlySpan<float> screenSizes, MeshSpace space = MeshSpace.Unreal, float unitScale = 1f)
    {
        ArgumentNullException.ThrowIfNull(lods);
        if (lods.Count == 0)
        {
            throw new ArgumentException("At least one LOD is required.", nameof(lods));
        }

        var totalVertices = lods.Sum(l => l.VertexCount);
        var totalIndices = lods.Sum(l => l.Indices.Length);
        var vertices = new float[totalVertices * FloatsPerVertex];
        var indices = new uint[totalIndices];
        var prepared = new PreparedLod[lods.Count];
        var bounds = BoundingBox.Empty;
        var vertexOffset = 0;
        var indexOffset = 0;
        var screenSize = 1f;
        for (var l = 0; l < lods.Count; l++)
        {
            var mesh = lods[l];
            bounds = bounds.Union(Convert(mesh, space, unitScale, vertices.AsSpan(vertexOffset * FloatsPerVertex)));
            mesh.Indices.CopyTo(indices, indexOffset);
            var sections = mesh.Sections.Length > 0
                ? mesh.Sections.Select(s => new PreparedSection(indexOffset + s.FirstIndex, s.IndexCount, vertexOffset, s.MaterialName)).ToArray()
                : [new PreparedSection(indexOffset, mesh.Indices.Length, vertexOffset, string.Empty)];
            screenSize = l < screenSizes.Length ? screenSizes[l] : screenSize * 0.5f;
            prepared[l] = new PreparedLod(screenSize, sections);
            vertexOffset += mesh.VertexCount;
            indexOffset += mesh.Indices.Length;
        }

        return new PreparedMesh(lods[0].Name, vertices, indices, bounds, prepared);
    }

    /// <summary>Writes the interleaved vertices of <paramref name="mesh"/> into <paramref name="target"/> and returns their bounds.</summary>
    private static BoundingBox Convert(MeshData mesh, MeshSpace space, float unitScale, Span<float> target)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var problems = mesh.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException($"Mesh '{mesh.Name}' is invalid: {string.Join(" ", problems)}", nameof(mesh));
        }

        var n = mesh.VertexCount;
        var normals = mesh.Normals.Length == n * 3 ? mesh.Normals : ComputeNormals(mesh.Positions, mesh.Indices);
        var hasUv = mesh.Uv0.Length == n * 2;
        var swap = space == MeshSpace.Unreal;
        var bounds = BoundingBox.Empty;
        for (var i = 0; i < n; i++)
        {
            var px = mesh.Positions[i * 3];
            var py = mesh.Positions[(i * 3) + 1];
            var pz = mesh.Positions[(i * 3) + 2];
            var nx = normals[i * 3];
            var ny = normals[(i * 3) + 1];
            var nz = normals[(i * 3) + 2];
            var position = swap ? new Vector3(px, pz, py) * unitScale : new Vector3(px, py, pz) * unitScale;
            var normal = swap ? new Vector3(nx, nz, ny) : new Vector3(nx, ny, nz);
            var o = i * FloatsPerVertex;
            target[o] = position.X;
            target[o + 1] = position.Y;
            target[o + 2] = position.Z;
            target[o + 3] = normal.X;
            target[o + 4] = normal.Y;
            target[o + 5] = normal.Z;
            target[o + 6] = hasUv ? mesh.Uv0[i * 2] : 0f;
            target[o + 7] = hasUv ? mesh.Uv0[(i * 2) + 1] : 0f;
            bounds = bounds.Include(position);
        }

        return bounds;
    }

    /// <summary>Area-weighted vertex normals of a triangle list (flat xyz arrays); vertices of degenerate triangles only get +Z.</summary>
    public static float[] ComputeNormals(ReadOnlySpan<float> positions, ReadOnlySpan<uint> indices)
    {
        var n = positions.Length / 3;
        var acc = new Vector3[n];
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = (int)indices[t];
            var b = (int)indices[t + 1];
            var c = (int)indices[t + 2];
            var pa = new Vector3(positions[a * 3], positions[(a * 3) + 1], positions[(a * 3) + 2]);
            var pb = new Vector3(positions[b * 3], positions[(b * 3) + 1], positions[(b * 3) + 2]);
            var pc = new Vector3(positions[c * 3], positions[(c * 3) + 1], positions[(c * 3) + 2]);
            var face = Vector3.Cross(pb - pa, pc - pa);
            acc[a] += face;
            acc[b] += face;
            acc[c] += face;
        }

        var result = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            var v = acc[i].LengthSquared() > 1e-20f ? Vector3.Normalize(acc[i]) : Vector3.UnitZ;
            result[i * 3] = v.X;
            result[(i * 3) + 1] = v.Y;
            result[(i * 3) + 2] = v.Z;
        }

        return result;
    }
}
