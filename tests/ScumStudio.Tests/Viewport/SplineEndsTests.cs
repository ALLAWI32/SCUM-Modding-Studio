using System.Numerics;
using System.Text.Json;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Serialization;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// <see cref="SplineEnds"/> (owner: "grab the road's corners and widen or narrow it, make the bridge longer or shorter,
/// weld it to the rest of the bridge"): through <see cref="SplineMeshDeformer"/>, the mesh the game would draw.
/// </summary>
public sealed class SplineEndsTests
{
    // A road piece 1000 cm long on X (from 0), 800 cm wide, 50 cm thick below its surface at Z 0.
    private static readonly BoundingBox Road = new(new Vector3(0, -400, -50), new Vector3(1000, 400, 0));

    private static MeshData Box(BoundingBox b)
    {
        var p = new List<float>();
        for (var i = 0; i < 8; i++)
        {
            p.AddRange([(i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z]);
        }

        return MeshData.Create("box", [.. p], [0, 1, 3, 0, 3, 2, 4, 7, 5, 4, 6, 7]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEndHandleMakesThePieceLongerAndTheOtherEndStays(bool alongY)
    {
        var bounds = alongY ? Swap(Road) : Road;
        var straight = BendShape.For(bounds, FVector.One, 0f);
        var forward = alongY ? FVector.Right : FVector.Forward;
        var longer = SplineEnds.Apply(straight, default, new SplineEnd(Move: forward * 300f));
        var mesh = SplineMeshDeformer.Deform(Box(bounds), longer);

        var along = alongY ? mesh.Bounds.Size.Y : mesh.Bounds.Size.X;
        Assert.Equal(1300f, along, 0.5f);
        Assert.True(FVector.Distance(SplineEnds.Section(longer, bounds, atEnd: false).Middle, SplineEnds.Section(straight, bounds, atEnd: false).Middle) < 0.01f);
        Assert.Equal(800f, SplineEnds.Section(longer, bounds, atEnd: true).Width, 0.5f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACornerDraggedOutWidensThatSideOnlyAtThatEnd(bool alongY)
    {
        var bounds = alongY ? Swap(Road) : Road;
        var straight = BendShape.For(bounds, FVector.One, 0f);
        var before = SplineEnds.Section(straight, bounds, atEnd: true);
        var outward = (before.Right - before.Middle).GetSafeNormal();

        // The right corner of the end goes 2 m further out.
        var end = SplineEnds.DragCorner(straight, straight, default, bounds, atEnd: true, rightSide: true, before.Right + (outward * 200f));
        var shaped = SplineEnds.Apply(straight, default, end);
        var after = SplineEnds.Section(shaped, bounds, atEnd: true);
        Assert.True(FVector.Distance(after.Right, before.Right + (outward * 200f)) < 0.5f, $"right corner at {after.Right}");
        Assert.True(FVector.Distance(after.Left, before.Left) < 0.5f, $"left corner moved to {after.Left}");
        Assert.Equal(1000f, after.Width, 0.5f);

        // The start is as it was, and the mesh the game draws widens towards the end.
        var start = SplineEnds.Section(shaped, bounds, atEnd: false);
        Assert.Equal(800f, start.Width, 0.5f);
        var mesh = SplineMeshDeformer.Deform(Box(bounds), shaped);
        var across = alongY ? mesh.Bounds.Size.X : mesh.Bounds.Size.Y;
        Assert.Equal(1000f, across, 1f);

        // Dragged across the other corner it stays 10 cm wide, never inside out.
        var squeezed = SplineEnds.DragCorner(straight, straight, default, bounds, atEnd: true, rightSide: true, before.Left - (outward * 300f));
        Assert.Equal(10f, SplineEnds.Section(SplineEnds.Apply(straight, default, squeezed), bounds, atEnd: true).Width, 0.5f);
    }

    [Fact]
    public void AnEndWeldsOntoAnotherPiecesEndWithoutAKink()
    {
        // The bridge that is there: a road piece running north-east, its end in the world.
        var bridgeWorld = new FTransform(new FRotator(0, 35, 0), new FVector(5000, -2000, 1200), FVector.One);
        var bridge = BendShape.For(Road, FVector.One, 20f);
        var there = SplineEnds.Section(bridge, Road, atEnd: true);
        var targetWorld = new EndSection(bridgeWorld.TransformPosition(there.Middle), bridgeWorld.TransformPosition(there.Left),
            bridgeWorld.TransformPosition(there.Right), bridgeWorld.TransformVectorNoScale(there.Outward), there.Width);

        // The new piece, narrower (60 %) and a little off, its start welded onto that end.
        var pieceWorld = new FTransform(new FRotator(0, 60, 0), targetWorld.Middle + new FVector(150, 200, -80), FVector.One);
        var piece = BendShape.For(Road, new FVector(1, 0.6f, 1), 0f);
        var local = new EndSection(pieceWorld.InverseTransformPosition(targetWorld.Middle), pieceWorld.InverseTransformPosition(targetWorld.Left),
            pieceWorld.InverseTransformPosition(targetWorld.Right), pieceWorld.InverseTransformVector(targetWorld.Outward).GetSafeNormal(), targetWorld.Width);
        var start = SplineEnds.Weld(piece, default, default, Road, atEnd: false, local);
        var welded = SplineEnds.Section(SplineEnds.Apply(piece, start, default), Road, atEnd: false);

        Assert.True(FVector.Distance(pieceWorld.TransformPosition(welded.Middle), targetWorld.Middle) < 0.5f, "the middles meet");
        Assert.True(FVector.Dot(pieceWorld.TransformVectorNoScale(welded.Outward), targetWorld.Outward) < -0.9999f, "it leaves the way the bridge comes in");
        Assert.Equal(targetWorld.Width, welded.Width, 0.5f);
    }

    [Fact]
    public void EndsJournalAndUndo()
    {
        var actor = new ActorRef("/Game/Maps/Island", "Bridge_Copy");
        var ends = new SplineEnd(new FVector(300, -20, 15), Yaw: 12f, Pitch: -3f, Grow: 0.25f, Shift: 40f, Lift: 120f);
        var op = new BendActorOp(actor, 0f, 0f, NewEnd: ends);
        var json = JsonSerializer.Serialize<EditOp>(op, LevelJson.Compact);
        var back = Assert.IsType<BendActorOp>(JsonSerializer.Deserialize<EditOp>(json, LevelJson.Compact));
        Assert.True(back.NewValue.IsNearly(op.NewValue));

        var state = new EditState();
        Assert.Null(state.Validate(op));
        state.Apply(op);
        Assert.False(state.GetBendValue(actor).IsStraight); // only the end edited: still drawn as a spline piece
        state.Apply(op.Inverse());
        Assert.True(state.GetBendValue(actor).IsStraight);
        Assert.NotNull(state.Validate(new BendActorOp(actor, 0f, 0f, NewEnd: ends with { Grow = -1f }))); // no width left
    }

    [Fact]
    public void ALongerPieceRepeatsInsteadOfStretching()
    {
        var straight = BendShape.For(Road, FVector.One, 0f);
        Assert.Same(straight, Assert.Single(SplineTiles.Split(straight, 1000f))); // as long as made: one piece

        // Pulled to 2.6 times its length and bent: three pieces of equal length, end to end without a kink.
        var shape = new BendValue(40f, End: new SplineEnd(Move: new FVector(1600, 0, 0)));
        var pieces = BendShape.Pieces(Road, FVector.One, shape);
        Assert.Equal(3, pieces.Count);
        var lengths = pieces.Select(SplineTiles.Length).ToList();
        Assert.All(lengths, l => Assert.Equal(lengths.Average(), l, 1f));
        Assert.Equal(SplineTiles.Length(BendShape.For(Road, FVector.One, shape)), lengths.Sum(), 1f);
        for (var i = 1; i < pieces.Count; i++)
        {
            Assert.True(FVector.Distance(pieces[i - 1].EndPos, pieces[i].StartPos) < 0.01f);
            Assert.True(FVector.Dot(pieces[i - 1].EndTangent.GetSafeNormal(), pieces[i].StartTangent.GetSafeNormal()) > 0.9999f);
        }

        // Drawn: the mesh three times, each about its own length (the bars keep their spacing).
        var mesh = SplineMeshDeformer.DeformPieces(Box(Road), pieces);
        Assert.Equal(Box(Road).VertexCount * 3, mesh.VertexCount);
        Assert.Equal(Box(Road).Indices.Length * 3, mesh.Sections.Sum(s => s.IndexCount));
    }

    [Fact]
    public void ArrowsRaiseTheMiddleOrOneEnd()
    {
        var straight = BendShape.For(Road, FVector.One, 0f);
        float HeightAt(SplineMeshParams s, float t) => SplineMeshDeformer.SplineEvalPos(s.StartPos, s.StartTangent, s.EndPos, s.EndTangent, t).Z;

        // Both push handles up 4 m: a hump of 3 m in the middle, the ends where they were.
        var hump = SplineEnds.Shape(straight, 0f, 0f, new SplineEnd(Lift: 400f), new SplineEnd(Lift: 400f));
        Assert.Equal(300f, HeightAt(hump, 0.5f), 0.5f);
        Assert.Equal(straight.StartPos, hump.StartPos);
        Assert.Equal(straight.EndPos, hump.EndPos);

        // One up, one down: a wave through the middle.
        var wave = SplineEnds.Shape(straight, 0f, 0f, new SplineEnd(Lift: 400f), new SplineEnd(Lift: -400f));
        Assert.True(HeightAt(wave, 0.25f) > 50f && HeightAt(wave, 0.75f) < -50f);
        Assert.Equal(0f, HeightAt(wave, 0.5f), 0.5f);

        // The end raised 5 m: a ramp from the start (still on its level, leaving flat) up to the end (arriving flat).
        var ramp = SplineEnds.Shape(straight, 0f, 0f, default, new SplineEnd(Move: FVector.Up * 500f));
        Assert.Equal(500f, ramp.EndPos.Z, 0.01f);
        Assert.Equal(0f, ramp.StartPos.Z, 0.01f);
        Assert.Equal(0f, ramp.StartTangent.Z, 0.01f);
        Assert.Equal(0f, ramp.EndTangent.Z, 0.01f);
        Assert.Equal(250f, HeightAt(ramp, 0.5f), 0.5f);
    }

    [Fact]
    public void LongerLegsSinkTheFootAndKeepTheTop()
    {
        // A tower 40 m tall, its pivot at the foot, wider on X than on Y (so a turned mesh would show).
        var tower = new BoundingBox(new Vector3(-1200, -400, 0), new Vector3(1200, 400, 4000));
        var mesh = Box(tower);
        var scale = new FVector(1.2f, 0.8f, 1f);

        // No change: the vertical spline draws the scaled tower exactly as it was (no turn, no squash).
        var same = SplineMeshDeformer.Deform(mesh, BendShape.Legs(tower, scale, 0f));
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var expected = new FVector(mesh.Positions[v * 3] * scale.X, mesh.Positions[(v * 3) + 1] * scale.Y, mesh.Positions[(v * 3) + 2] * scale.Z);
            var got = new FVector(same.Positions[v * 3], same.Positions[(v * 3) + 1], same.Positions[(v * 3) + 2]);
            Assert.True(FVector.Distance(expected, got) < 0.05f, $"vertex {v}: {got} instead of {expected}");
        }

        // 20 m longer legs: the foot 20 m down, the top where it was, the stretch at the foot.
        var spline = Assert.Single(BendShape.Pieces(tower, scale, new BendValue(0f, Legs: 2000f)));
        var deep = SplineMeshDeformer.Deform(mesh, spline);
        Assert.Equal(-2000f, deep.Bounds.Min.Z, 0.5f);
        Assert.Equal(4000f, deep.Bounds.Max.Z, 0.5f);
        Assert.Equal(1200f * 1.2f, deep.Bounds.Max.X, 0.5f);
        var at60 = SplineMeshDeformer.SplineEvalPos(spline.StartPos, spline.StartTangent, spline.EndPos, spline.EndTangent, 0.6f).Z;
        Assert.Equal(2400f - (2000f * 0.064f), at60, 1f); // 60 % up comes down only 1.3 m
    }

    private static BoundingBox Swap(BoundingBox b) => new(new Vector3(b.Min.Y, b.Min.X, b.Min.Z), new Vector3(b.Max.Y, b.Max.X, b.Max.Z));
}
