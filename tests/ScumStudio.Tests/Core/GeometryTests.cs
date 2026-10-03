using System.Numerics;
using ScumStudio.Core.Geometry;

namespace ScumStudio.Tests.Core;

public sealed class GeometryTests
{
    [Fact]
    public void BoundsFromPositions()
    {
        float[] positions = [0, 0, 0, 100, -50, 20, -10, 30, 5];
        var box = BoundingBox.FromPositions(positions);

        Assert.Equal(new Vector3(-10, -50, 0), box.Min);
        Assert.Equal(new Vector3(100, 30, 20), box.Max);
        Assert.Equal(new Vector3(110, 80, 20), box.Size);
        Assert.True(box.Contains(new Vector3(0, 0, 10)));
        Assert.False(box.Contains(new Vector3(0, 0, 21)));
    }

    [Fact]
    public void EmptyBoundsBehave()
    {
        var empty = BoundingBox.Empty;
        Assert.True(empty.IsEmpty);
        Assert.Equal(Vector3.Zero, empty.Size);

        var box = new BoundingBox(Vector3.Zero, Vector3.One);
        Assert.Equal(box, empty.Union(box));
        Assert.Equal(box, box.Union(empty));
        Assert.True(BoundingBox.FromPositions([]).IsEmpty);
    }

    [Fact]
    public void MeshCreateComputesBoundsAndDefaultSection()
    {
        var mesh = MeshData.Create(
            "SM_Triangle",
            positions: [0, 0, 0, 100, 0, 0, 0, 100, 0],
            indices: [0, 1, 2]);

        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(1, mesh.TriangleCount);
        Assert.Single(mesh.Sections);
        Assert.Equal(3, mesh.Sections[0].IndexCount);
        Assert.Equal(new Vector3(100, 100, 0), mesh.Bounds.Max);
        Assert.Empty(mesh.Validate());
    }

    [Fact]
    public void MeshValidateReportsProblems()
    {
        var mesh = MeshData.Create(
            "Broken",
            positions: [0, 0, 0, 1, 1, 1],
            indices: [0, 1, 5],
            normals: [0, 0, 1],
            sections: [new MeshSection("M", 0, 6)]);

        var problems = mesh.Validate();
        Assert.Contains(problems, p => p.Contains("Normals"));
        Assert.Contains(problems, p => p.Contains("out of range"));
        Assert.Contains(problems, p => p.Contains("Section"));
    }
}
