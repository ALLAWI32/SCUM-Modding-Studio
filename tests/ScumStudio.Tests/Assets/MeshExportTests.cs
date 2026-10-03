using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ScumStudio.Assets.Export;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Tests.Assets;

public sealed class MeshExportTests
{
    /// <summary>
    /// One UE-style front face: seen in UE's left-handed frame it is clockwise, i.e. dot(cross(v1-v0, v2-v0), n) &lt; 0
    /// (the convention measured on stock SCUM meshes), plus a second section with one more triangle.
    /// </summary>
    private static MeshData Sample() => new(
        "SM_Test",
        [0, 0, 0, 0, 100, 0, 100, 0, 0, 0, 0, 50],
        [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1],
        [0, 0, 0, 1, 1, 0, 0.5f, 0.5f],
        [0, 1, 2, 0, 2, 3],
        [new MeshSection("/Game/M/MI_A.MI_A", 0, 3), new MeshSection("/Game/M/MI_B.MI_B", 3, 3)],
        BoundingBox.FromPositions([0, 0, 0, 0, 100, 0, 100, 0, 0, 0, 0, 50]));

    [Fact]
    public void Gltf_IsStructurallyValid_AndConvertsAxesAndUnits()
    {
        var mesh = Sample();
        var (json, bin) = GltfExporter.Build(mesh, "test.bin");
        var gltf = GltfReader.Parse(json, bin);

        Assert.Equal("2.0", gltf.Version);
        Assert.Equal(bin.Length, gltf.BufferLength);
        Assert.Equal(2, gltf.Primitives.Count);
        Assert.Equal(["MI_A", "MI_B"], gltf.MaterialNames);

        var positions = gltf.ReadVec3(gltf.Primitives[0].Position);
        Assert.Equal(4, positions.Length);
        // UE (0, 100, 0) cm -> glTF (0, 0, 1) m ; UE (0, 0, 50) -> (0, 0.5, 0).
        Assert.Equal(new Vector3(0, 0, 1), positions[1]);
        Assert.Equal(new Vector3(0, 0.5f, 0), positions[3]);
        var (min, max) = gltf.AccessorBounds(gltf.Primitives[0].Position);
        Assert.Equal(new Vector3(0, 0, 0), min);
        Assert.Equal(new Vector3(1, 0.5f, 1), max);

        Assert.Equal([0u, 1u, 2u], gltf.ReadIndices(gltf.Primitives[0].Indices));
        Assert.Equal([0u, 2u, 3u], gltf.ReadIndices(gltf.Primitives[1].Indices));
        Assert.Equal(5123, gltf.IndexComponentType(gltf.Primitives[0].Indices));
    }

    [Fact]
    public void Gltf_FrontFacesAreCounterClockwise()
    {
        var mesh = Sample();
        var (json, bin) = GltfExporter.Build(mesh, "test.bin");
        var gltf = GltfReader.Parse(json, bin);
        var p = gltf.ReadVec3(gltf.Primitives[0].Position);
        var n = gltf.ReadVec3(gltf.Primitives[0].Normal!.Value);
        var idx = gltf.ReadIndices(gltf.Primitives[0].Indices);
        var face = Vector3.Cross(p[idx[1]] - p[idx[0]], p[idx[2]] - p[idx[0]]);
        Assert.True(Vector3.Dot(face, n[idx[0]]) > 0, "glTF front face must be counter-clockwise (right-handed, normal-facing).");

        // Sanity: in UE space the same triangle is clockwise against its normal.
        var ue = Vector3.Cross(new Vector3(0, 100, 0), new Vector3(100, 0, 0));
        Assert.True(Vector3.Dot(ue, Vector3.UnitZ) < 0);
    }

    [Fact]
    public void Gltf_UeAxes_ReversesWinding()
    {
        var (json, bin) = GltfExporter.Build(Sample(), "t.bin", new MeshExportOptions { ConvertToYUp = false, Scale = 1f });
        var gltf = GltfReader.Parse(json, bin);
        Assert.Equal([0u, 2u, 1u], gltf.ReadIndices(gltf.Primitives[0].Indices));
        Assert.Equal(new Vector3(0, 100, 0), gltf.ReadVec3(gltf.Primitives[0].Position)[1]);
    }

    [Fact]
    public void Gltf_LargeMesh_Uses32BitIndices()
    {
        const int n = 70000;
        var positions = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            positions[i * 3] = i;
        }

        var mesh = MeshData.Create("Big", positions, [0, 1, (uint)(n - 1)]);
        var (json, bin) = GltfExporter.Build(mesh, "big.bin");
        var gltf = GltfReader.Parse(json, bin);
        Assert.Equal(5125, gltf.IndexComponentType(gltf.Primitives[0].Indices));
        Assert.Equal([0u, 1u, (uint)(n - 1)], gltf.ReadIndices(gltf.Primitives[0].Indices));
        Assert.Null(gltf.Primitives[0].Normal);
    }

    [Fact]
    public void Obj_WritesVerticesFacesAndGroups()
    {
        var writer = new StringWriter(CultureInfo.InvariantCulture);
        ObjExporter.Write(Sample(), writer, mtlFileName: "t.mtl");
        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Contains("mtllib t.mtl", lines);
        Assert.Equal(4, lines.Count(l => l.StartsWith("v ", StringComparison.Ordinal)));
        Assert.Equal(4, lines.Count(l => l.StartsWith("vt ", StringComparison.Ordinal)));
        Assert.Equal(4, lines.Count(l => l.StartsWith("vn ", StringComparison.Ordinal)));
        Assert.Equal(["usemtl MI_A", "usemtl MI_B"], lines.Where(l => l.StartsWith("usemtl", StringComparison.Ordinal)));
        Assert.Equal(["f 1/1/1 2/2/2 3/3/3", "f 1/1/1 3/3/3 4/4/4"], lines.Where(l => l.StartsWith("f ", StringComparison.Ordinal)));
        Assert.Contains("v 0 0 1", lines);         // UE (0,100,0) cm -> (0,0,1) m, Y up
        Assert.Contains("vt 0 0", lines);          // UE uv (0,1) -> OBJ (0, 1-1)

        var mtl = new StringWriter(CultureInfo.InvariantCulture);
        ObjExporter.WriteMtl(Sample(), mtl);
        Assert.Contains("newmtl MI_A", mtl.ToString(), StringComparison.Ordinal);
        Assert.Contains("newmtl MI_B", mtl.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_WritesCompanionFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "scumstudio-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            var gltfFiles = await GltfExporter.SaveAsync(Sample(), Path.Combine(dir, "m.gltf"));
            var objFiles = await ObjExporter.SaveAsync(Sample(), Path.Combine(dir, "m.obj"));
            Assert.All(gltfFiles.Concat(objFiles), f => Assert.True(File.Exists(f), f));
            Assert.EndsWith("m.bin", gltfFiles[1], StringComparison.Ordinal);
            var gltf = GltfReader.Load(gltfFiles[0]);
            Assert.Equal(2, gltf.Primitives.Count);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
