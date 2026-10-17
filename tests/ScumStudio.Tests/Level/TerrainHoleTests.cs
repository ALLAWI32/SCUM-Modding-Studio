using System.Buffers.Binary;
using System.Numerics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;

namespace ScumStudio.Tests.Level;

/// <summary>The terrain hole tool without game files: the edit, its undo, the shapes and the collision patch.</summary>
public sealed class TerrainHoleTests
{
    private const string Tile = "/Game/ConZ_Files/Maps/The_Island/Landscape_A_0_1";

    [Fact]
    public void TheEditRoundTripsAndUndoes()
    {
        var hole = new TerrainHole(Tile, TerrainHoleShape.Circle, -890025f, -458775f, 450f);
        var op = new TerrainHoleOp(hole);
        Assert.Equal(op, EditOp.FromJson(op.ToJson()));
        Assert.Contains("\"op\":\"terrainHole\"", op.ToJson(), StringComparison.Ordinal);
        Assert.Equal(op, op.Inverse().Inverse());
        Assert.Contains(typeof(TerrainHoleOp), EditOpRemap.KnownTypes);

        var state = new EditState();
        Assert.NotNull(state.Validate(op.Inverse())); // nothing to fill yet
        state.Apply(op);
        Assert.False(state.IsEmpty);
        Assert.Equal([hole], state.TerrainHoles);
        Assert.Empty(state.ChangedLevels); // the export cuts it after the levels
        Assert.NotNull(state.Validate(op)); // already cut
        Assert.Single(state.Clone().TerrainHoles);
        state.Apply(op.Inverse());
        Assert.True(state.IsEmpty);

        Assert.NotNull(state.Validate(new TerrainHoleOp(hole with { Size = 50f })));
        Assert.NotNull(state.Validate(new TerrainHoleOp(hole with { X = float.NaN })));
    }

    [Fact]
    public void ShapesSnapToTheQuadGrid()
    {
        // 3 quads across: the centre on a quad centre; 4 quads: on a quad corner.
        var three = new TerrainHole(Tile, TerrainHoleShape.Square, 1010f, -40f, 420f, 30f).Snapped();
        Assert.Equal((975f, -75f, 450f, 30f), (three.X, three.Y, three.Size, three.Yaw));
        var four = new TerrainHole(Tile, TerrainHoleShape.Circle, 1010f, -40f, 640f, 30f).Snapped();
        Assert.Equal((1050f, 0f, 600f, 0f), (four.X, four.Y, four.Size, four.Yaw));
        Assert.Equal(TerrainHole.MinSize, (four with { Size = 1f }).Snapped().Size);

        var square = new TerrainHole(Tile, TerrainHoleShape.Square, 0f, 0f, 400f, 45f);
        Assert.True(square.Contains(0f, 270f)); // a corner turned onto the Y axis
        Assert.False(square.Contains(190f, 190f));
        var circle = square with { Shape = TerrainHoleShape.Circle };
        Assert.True(circle.Contains(141f, 141f));
        Assert.False(circle.Contains(150f, 150f));
        Assert.Equal(5, square.Outline().Length);
        Assert.Equal(square.Outline()[0], square.Outline()[^1]);
    }

    [Fact]
    public void QuadsUnderAHoleAreTheOnesWhoseCentreIsInside()
    {
        var grid = new TerrainHoles.Grid(new Vector2(-1500f, -1500f), new Vector2(150f, 0f), new Vector2(0f, 150f), 254);
        var three = new TerrainHole(Tile, TerrainHoleShape.Square, -75f, -75f, 450f);
        Assert.Equal([(8, 8), (9, 8), (10, 8), (8, 9), (9, 9), (10, 9), (8, 10), (9, 10), (10, 10)], TerrainHoles.Quads(grid, [three]));
        // 4 m around a quad corner: the two quad centres each way (75 cm and 225 cm out: only 75 is inside 200).
        Assert.Equal(4, TerrainHoles.Quads(grid, [three with { X = 0f, Y = 0f, Size = 400f }]).Count);
        Assert.Empty(TerrainHoles.Quads(grid, [three with { X = 1e6f }]));
    }

    [Fact]
    public void CuttingMarksTheCellsAndTheirVerticesAsPhysXDoes()
    {
        const int n = 8;
        var payload = new byte[n * n * 4];
        for (var i = 0; i < n * n; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(i * 4), (short)(i * 7));
            payload[(i * 4) + 2] = 0x02;
            payload[(i * 4) + 3] = (byte)(i % 3 == 0 ? 0x82 : 0x02);
        }

        var before = payload.ToArray();
        var field = new LandscapeCollision.Heightfield(0, payload.Length, 1, n, n, 1, 1, 4, 0, 0f, 0f);

        // Render quads x 2..4, y 3..4 = PhysX rows 2..4 (n - 2 - x), columns 3..4.
        var quads = Enumerable.Range(2, 3).SelectMany(x => new[] { (x, 3), (x, 4) }).ToList();
        Assert.Equal(6, TerrainHoles.Cut(payload, field, quads));
        Assert.Equal(0, TerrainHoles.Cut(payload, field, quads)); // already open

        for (var r = 0; r < n - 1; r++)
        {
            for (var c = 0; c < n - 1; c++)
            {
                var inside = r is >= 2 and <= 4 && c is >= 3 and <= 4;
                Assert.Equal(inside, TerrainHoles.IsHole(payload, field, r, c));
            }
        }

        for (var i = 0; i < n * n; i++)
        {
            Assert.Equal(before[i * 4], payload[i * 4]); // heights untouched
            Assert.Equal(before[(i * 4) + 1], payload[(i * 4) + 1]);
            var (r, c) = (i / n, i % n);
            var corner = r is >= 2 and <= 5 && c is >= 3 and <= 5;
            if (!corner)
            {
                Assert.Equal(before[(i * 4) + 2], payload[(i * 4) + 2]);
                Assert.Equal(before[(i * 4) + 3], payload[(i * 4) + 3]);
            }
        }

        // Inside the hole (all four cells open): no collision vertex; on its rim: a collision vertex.
        Assert.Equal(0, payload[field.SampleAt(3, 4) + 3] & 0x80);
        Assert.Equal(0, payload[field.SampleAt(4, 4) + 3] & 0x80);
        Assert.Equal(0x80, payload[field.SampleAt(2, 3) + 3] & 0x80);
        Assert.Equal(0x80, payload[field.SampleAt(5, 5) + 3] & 0x80);
        Assert.Equal(0x7F, payload[field.SampleAt(4, 4) + 3]);
    }

    [Fact]
    public void AHeightfieldNotCookedLikeTheIslandsIsRefused()
    {
        var field = new LandscapeCollision.Heightfield(0, 64, 1, 4, 4, 0, 1, 4, 0, 0f, 0f);
        Assert.Throws<InvalidDataException>(() => TerrainHoles.Cut(new byte[64], field, [(1, 1)]));
        Assert.Null(LandscapeCollision.Read(new byte[200], 0));
    }
}
