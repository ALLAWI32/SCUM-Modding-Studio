namespace ScumStudio.Core.Geometry;

/// <summary>
/// A draw range of a <see cref="MeshData"/> that uses one material.
/// </summary>
/// <param name="MaterialName">Material (or material instance) object path or name; empty when unknown.</param>
/// <param name="FirstIndex">First element in <see cref="MeshData.Indices"/>.</param>
/// <param name="IndexCount">Number of indices (a multiple of 3; triangle list).</param>
public sealed record MeshSection(string MaterialName, int FirstIndex, int IndexCount);

/// <summary>
/// Renderer-neutral triangle mesh extracted from a cooked asset (one LOD).
/// </summary>
/// <remarks>
/// Coordinates are Unreal Engine units (centimetres) and axes (X forward, Y right, Z up, left-handed).
/// All vertex arrays are flat and share the same vertex count <c>N</c>:
/// <see cref="Positions"/> and <see cref="Normals"/> hold <c>3N</c> floats (xyz),
/// <see cref="Uv0"/> holds <c>2N</c> floats (uv), or is empty when the mesh has no UVs.
/// <see cref="Indices"/> is a triangle list indexing those vertices.
/// </remarks>
/// <param name="Name">Asset or object name (e.g. <c>SM_Container_01</c>).</param>
/// <param name="Positions">Vertex positions, xyz interleaved.</param>
/// <param name="Normals">Vertex normals, xyz interleaved (unit length); may be empty.</param>
/// <param name="Uv0">First UV channel, uv interleaved; may be empty.</param>
/// <param name="Indices">Triangle-list indices.</param>
/// <param name="Sections">Material sections covering <see cref="Indices"/>.</param>
/// <param name="Bounds">Axis-aligned bounds of <see cref="Positions"/>.</param>
public sealed record MeshData(
    string Name,
    float[] Positions,
    float[] Normals,
    float[] Uv0,
    uint[] Indices,
    MeshSection[] Sections,
    BoundingBox Bounds)
{
    /// <summary>Number of vertices (<c>Positions.Length / 3</c>).</summary>
    public int VertexCount => Positions.Length / 3;

    /// <summary>Number of triangles (<c>Indices.Length / 3</c>).</summary>
    public int TriangleCount => Indices.Length / 3;

    /// <summary>
    /// Creates a mesh and computes <see cref="Bounds"/> from <paramref name="positions"/>.
    /// When <paramref name="sections"/> is null a single unnamed section covering all indices is used.
    /// </summary>
    public static MeshData Create(
        string name,
        float[] positions,
        uint[] indices,
        float[]? normals = null,
        float[]? uv0 = null,
        MeshSection[]? sections = null) =>
        new(
            name,
            positions,
            normals ?? [],
            uv0 ?? [],
            indices,
            sections ?? [new MeshSection(string.Empty, 0, indices.Length)],
            BoundingBox.FromPositions(positions));

    /// <summary>
    /// Checks the structural invariants (array lengths, index range, section ranges).
    /// Returns an empty list when the mesh is valid, otherwise human-readable problems.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Positions.Length % 3 != 0)
        {
            problems.Add($"Positions length {Positions.Length} is not a multiple of 3.");
        }

        var n = VertexCount;
        if (Normals.Length != 0 && Normals.Length != n * 3)
        {
            problems.Add($"Normals length {Normals.Length} does not match {n} vertices.");
        }

        if (Uv0.Length != 0 && Uv0.Length != n * 2)
        {
            problems.Add($"Uv0 length {Uv0.Length} does not match {n} vertices.");
        }

        if (Indices.Length % 3 != 0)
        {
            problems.Add($"Indices length {Indices.Length} is not a multiple of 3.");
        }

        foreach (var index in Indices)
        {
            if (index >= (uint)n)
            {
                problems.Add($"Index {index} is out of range for {n} vertices.");
                break;
            }
        }

        foreach (var s in Sections)
        {
            if (s.FirstIndex < 0 || s.IndexCount < 0 || (long)s.FirstIndex + s.IndexCount > Indices.Length)
            {
                problems.Add($"Section '{s.MaterialName}' [{s.FirstIndex}, +{s.IndexCount}) exceeds {Indices.Length} indices.");
            }
        }

        return problems;
    }
}
