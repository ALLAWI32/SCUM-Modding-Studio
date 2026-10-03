using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// <see cref="BendShape"/> through <see cref="SplineMeshDeformer"/> (the game's bend): straight reproduces the scaled mesh
/// whichever axis is longer, a bend keeps the middle and the length and turns the ends, and the edit journals cleanly.
/// </summary>
public sealed class BendShapeTests
{
    // A wall 600 cm long on X (from -200), 40 cm thick, 300 cm tall; the same wall turned to run along Y.
    private static MeshData Wall(bool alongY)
    {
        float[] corners = [-200, -20, 0, 400, -20, 0, 400, 20, 0, -200, 20, 0, -200, -20, 300, 400, -20, 300, 400, 20, 300, -200, 20, 300];
        if (alongY)
        {
            for (var i = 0; i < corners.Length; i += 3)
            {
                (corners[i], corners[i + 1]) = (corners[i + 1], corners[i]);
            }
        }

        return MeshData.Create("wall", corners, [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6]);
    }

    private static FVector At(MeshData mesh, int vertex) => new(mesh.Positions[vertex * 3], mesh.Positions[vertex * 3 + 1], mesh.Positions[vertex * 3 + 2]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StraightReproducesTheScaledMesh(bool alongY)
    {
        var wall = Wall(alongY);
        var scale = new FVector(1.5f, 2f, 0.5f);
        var bent = SplineMeshDeformer.Deform(wall, BendShape.For(wall.Bounds, scale, 0f));
        for (var v = 0; v < wall.VertexCount; v++)
        {
            var expected = At(wall, v) * scale;
            Assert.True(FVector.Distance(expected, At(bent, v)) < 0.01f, $"vertex {v}: {At(bent, v)} instead of {expected}");
        }
    }

    [Theory]
    [InlineData(90f, false)]
    [InlineData(-90f, false)]
    [InlineData(150f, true)]
    public void ABendKeepsTheMiddleAndTheLengthAndTurnsTheEnds(float degrees, bool alongY)
    {
        var wall = Wall(alongY);
        var spline = BendShape.For(wall.Bounds, FVector.One, degrees);
        var forward = alongY ? FVector.Right : FVector.Forward;
        var right = alongY ? new FVector(-1, 0, 0) : FVector.Right;

        // Middle of the wall (100 cm along) stays put; the ends lie on the side the bend turns to, mirrored.
        var middle = SplineMeshDeformer.SplineEvalPos(spline.StartPos, spline.StartTangent, spline.EndPos, spline.EndTangent, 0.5f);
        Assert.True(FVector.Distance(middle, forward * 100f) < 0.5f, $"middle at {middle}");
        var side = MathF.Sign(degrees);
        Assert.True(FVector.Dot(spline.StartPos - (forward * 100f), right) * side > 0f && FVector.Dot(spline.EndPos - (forward * 100f), right) * side > 0f);
        Assert.Equal(FVector.Dot(spline.StartPos, right), FVector.Dot(spline.EndPos, right), 0.01f);

        // The curve is as long as the wall (within 0.5 %) and leaves its end turned by half the bend.
        var length = 0f;
        var previous = spline.StartPos;
        for (var i = 1; i <= 400; i++)
        {
            var point = SplineMeshDeformer.SplineEvalPos(spline.StartPos, spline.StartTangent, spline.EndPos, spline.EndTangent, i / 400f);
            length += FVector.Distance(previous, point);
            previous = point;
        }

        Assert.Equal(600f, length, 3f);
        var endDirection = SplineMeshDeformer.SplineEvalDir(spline.StartPos, spline.StartTangent, spline.EndPos, spline.EndTangent, 1f);
        var turned = MathF.Atan2(FVector.Dot(endDirection, right), FVector.Dot(endDirection, forward)) * 180f / MathF.PI;
        Assert.Equal(degrees / 2f, turned, 0.5f);

        // The deformed wall still stands upright: its top stays 300 cm above its bottom everywhere.
        var bent = SplineMeshDeformer.Deform(wall, spline);
        for (var v = 0; v < 4; v++)
        {
            Assert.Equal(300f, At(bent, v + 4).Z - At(bent, v).Z, 0.01f);
        }
    }

    [Fact]
    public void HandlesPushThePieceSidewaysWithItsEndsFixed()
    {
        var straight = new SplineMeshParams { StartPos = FVector.Zero, EndPos = new FVector(1200, 0, 0), StartTangent = new FVector(1200, 0, 0), EndTangent = new FVector(1200, 0, 0) };
        float Side(SplineMeshParams s, float t) => SplineMeshDeformer.SplineEvalPos(s.StartPos, s.StartTangent, s.EndPos, s.EndTangent, t).Y;

        var bow = SplineSway.Apply(straight, 300f, 300f);
        Assert.Equal(straight.StartPos, bow.StartPos);
        Assert.Equal(straight.EndPos, bow.EndPos);
        Assert.True(Side(bow, 0.25f) > 0f && Side(bow, 0.5f) > 0f && Side(bow, 0.75f) > 0f); // all of it to the right (+Y)

        var s = SplineSway.Apply(straight, 300f, -300f);
        Assert.True(Side(s, 0.25f) > 0f && Side(s, 0.75f) < 0f); // right then left: an S
        Assert.Equal(0f, Side(s, 0.5f), 0.01f);

        Assert.Same(straight, SplineSway.Apply(straight, 0f, 0f));
    }

    [Fact]
    public void BendsAreJournaledAndUndoneExactly()
    {
        var actor = new ActorRef("/Game/Maps/A_0_Test", "Wall_1");
        var state = new EditState();
        var op = new BendActorOp(actor, 0f, 45f);
        state.Apply(op);
        Assert.Equal(45f, state.GetBend(actor));
        Assert.Contains("/Game/Maps/A_0_Test", state.ChangedLevels);
        Assert.Equal(op, EditOp.FromJson(op.ToJson()));
        Assert.NotNull(state.Validate(new BendActorOp(actor, 0f, 90f))); // out of date: it is at 45
        Assert.NotNull(state.Validate(new BendActorOp(actor, 45f, 200f))); // beyond a half circle
        state.Apply(op.Inverse());
        Assert.True(state.IsEmpty);
    }
}
