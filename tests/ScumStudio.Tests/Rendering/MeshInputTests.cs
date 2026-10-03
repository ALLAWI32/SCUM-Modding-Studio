using System.Numerics;
using ScumStudio.Assets.Export;
using ScumStudio.Core.Geometry;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Import;
using ScumStudio.Rendering.Procedural;
using ScumStudio.Rendering.Resources;

namespace ScumStudio.Tests.Rendering;

public sealed class MeshInputTests
{
    private static MeshData UeTriangle() => MeshData.Create(
        "Tri",
        [0f, 0f, 0f, 100f, 0f, 0f, 0f, 200f, 300f],
        [0u, 1u, 2u],
        normals: [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
        uv0: [0f, 0f, 1f, 0f, 0f, 1f]);

    [Fact]
    public void PreparedMeshConvertsUnrealAxesAndScale()
    {
        var prepared = PreparedMesh.From(UeTriangle(), MeshSpace.Unreal, 0.01f);
        Assert.Equal(3, prepared.VertexCount);
        // Vertex 2: UE (0, 200, 300) cm -> GL (0, 3, 2) m; normal UE +Z (up) -> GL +Y.
        var v = prepared.Vertices.AsSpan(2 * PreparedMesh.FloatsPerVertex, PreparedMesh.FloatsPerVertex).ToArray();
        Assert.Equal([0f, 3f, 2f, 0f, 1f, 0f, 0f, 1f], v);
        Assert.Equal(new Vector3(0f, 0f, 0f), prepared.Bounds.Min);
        Assert.Equal(new Vector3(1f, 3f, 2f), prepared.Bounds.Max);
        Assert.Equal([0u, 1u, 2u], prepared.Indices);

        var gl = PreparedMesh.From(UeTriangle(), MeshSpace.Gl);
        Assert.Equal(new Vector3(100f, 200f, 300f), gl.Bounds.Max);
    }

    [Fact]
    public void MissingNormalsAreComputedAndInvalidMeshesRejected()
    {
        var mesh = MeshData.Create("NoNormals", [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f], [0u, 1u, 2u]);
        var prepared = PreparedMesh.From(mesh, MeshSpace.Gl);
        Assert.Equal([0f, 0f, 1f], prepared.Vertices.AsSpan(3, 3).ToArray());
        Assert.Equal([0f, 0f], prepared.Vertices.AsSpan(6, 2).ToArray());

        var broken = MeshData.Create("Broken", [0f, 0f, 0f], [0u, 1u, 2u]);
        Assert.Throws<ArgumentException>(() => PreparedMesh.From(broken));
    }

    [Fact]
    public void CubeHasOutwardCounterClockwiseFaces()
    {
        var cube = PrimitiveMeshes.Cube(2f);
        Assert.Empty(cube.Validate());
        Assert.Equal(24, cube.VertexCount);
        Assert.Equal(12, cube.TriangleCount);
        for (var t = 0; t < cube.Indices.Length; t += 3)
        {
            Vector3 P(uint i) => new(cube.Positions[i * 3], cube.Positions[(i * 3) + 1], cube.Positions[(i * 3) + 2]);
            var a = cube.Indices[t];
            var face = Vector3.Cross(P(cube.Indices[t + 1]) - P(a), P(cube.Indices[t + 2]) - P(a));
            var normal = new Vector3(cube.Normals[a * 3], cube.Normals[(a * 3) + 1], cube.Normals[(a * 3) + 2]);
            Assert.True(Vector3.Dot(face, normal) > 0f, $"triangle {t / 3} winds clockwise");
            Assert.True(Vector3.Dot(P(a), normal) > 0f, $"triangle {t / 3} faces inwards");
        }
    }

    [Fact]
    public void ObjReaderParsesPolygonsNegativeIndicesAndMaterials()
    {
        const string obj = """
            # quad + triangle
            o Thing
            v 0 0 0
            v 1 0 0
            v 1 1 0
            v 0 1 0
            vt 0 0
            vt 1 0
            vt 1 1
            vn 0 0 1
            usemtl A
            f 1/1/1 2/2/1 3/3/1 4/1/1
            usemtl B
            f -4//-1 -2//-1 -1//-1
            """;
        var mesh = ObjReader.Parse(obj);
        Assert.Equal("Thing", mesh.Name);
        Assert.Empty(mesh.Validate());
        Assert.Equal(3, mesh.TriangleCount);
        Assert.Equal(["A", "B"], mesh.Sections.Select(s => s.MaterialName));
        Assert.Equal(6, mesh.Sections[0].IndexCount);
        Assert.Equal(3, mesh.Sections[1].IndexCount);
        // Corner "3/3/1": V flipped to the top-left origin.
        Assert.Equal(new Vector3(1f, 1f, 0f), mesh.Bounds.Max);
        Assert.Contains(mesh.Uv0.Chunk(2), uv => uv[0] == 1f && uv[1] == 0f);
        Assert.Throws<InvalidDataException>(() => ObjReader.Parse("v 0 0 0\nf 1 2 3\n"));
        Assert.Throws<InvalidDataException>(() => ObjReader.Parse("v 0 zero 0\n"));
    }

    [Fact]
    public void ObjRoundTripsThroughTheAssetsExporter()
    {
        var source = PrimitiveMeshes.Cube(100f);
        using var writer = new StringWriter();
        ObjExporter.Write(source, writer, new MeshExportOptions { Scale = 1f, ConvertToYUp = false });
        var mesh = ObjReader.Parse(writer.ToString());
        Assert.Equal(source.TriangleCount, mesh.TriangleCount);
        Assert.Equal(source.VertexCount, mesh.VertexCount);
        Assert.Equal(source.Bounds, mesh.Bounds);
        // The exporter may emit corners in a different order; compare the (position, uv) vertex sets.
        Assert.Equal(VertexSet(source), VertexSet(mesh));
    }

    private static HashSet<(Vector3, Vector2)> VertexSet(MeshData m) =>
        Enumerable.Range(0, m.VertexCount)
            .Select(i => (new Vector3(m.Positions[i * 3], m.Positions[(i * 3) + 1], m.Positions[(i * 3) + 2]), new Vector2(m.Uv0[i * 2], m.Uv0[(i * 2) + 1])))
            .ToHashSet();

    [Fact]
    public void SrgbTransferFunctionsRoundTrip()
    {
        Assert.Equal(0f, ColorSpace.LinearToSrgb(0f));
        Assert.Equal(1f, ColorSpace.LinearToSrgb(1f), 5);
        Assert.Equal(188, ColorSpace.LinearToSrgbByte(0.5f));
        for (var v = 0f; v <= 1f; v += 0.05f)
        {
            Assert.Equal(v, ColorSpace.SrgbToLinear(ColorSpace.LinearToSrgb(v)), 4);
        }
    }
}
