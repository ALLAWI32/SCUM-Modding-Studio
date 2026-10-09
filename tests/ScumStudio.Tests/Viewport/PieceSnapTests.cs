using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary><see cref="PieceSnap"/>: a wall piece dropped near another joins it end to end, and a joint made by hand is learned.</summary>
public sealed class PieceSnapTests
{
    // A 6 m wall piece along X from 0, 40 cm thick, 3 m tall.
    private static readonly BoundingBox Wall = new(new Vector3(0, -20, 0), new Vector3(600, 20, 300));

    [Fact]
    public void ADroppedPieceContinuesTheOtherStraight()
    {
        var target = new SnapPiece("SM_Wall", new FTransform(new FRotator(0, 30, 0), new FVector(1000, 500, 50), FVector.One), Wall);
        var targetEnd = target.World.TransformPosition(new FVector(600, 0, 150));
        var targetFoot = target.World.TransformPosition(new FVector(600, 0, 0));

        // Dropped roughly after it, a bit off and turned the wrong way.
        var moving = new SnapPiece("SM_Wall", new FTransform(new FRotator(0, 80, 0), targetFoot + new FVector(90, -60, 20), FVector.One), Wall);
        var snapped = PieceSnap.Best(moving, [target], PieceSnap.Reach(moving));

        Assert.NotNull(snapped);
        var world = snapped!.Value.World;
        Assert.Equal(30f, world.Rotator().Yaw, 0.01f); // turned like the wall it continues
        Assert.True(FVector.Distance(world.TransformPosition(new FVector(0, 0, 150)), targetEnd) < 0.01f, "its start sits on the other's end");

        // Far away: nothing to join.
        Assert.Null(PieceSnap.Best(moving with { World = new FTransform(targetEnd + new FVector(5000, 0, 0)) }, [target], PieceSnap.Reach(moving)));
    }

    [Fact]
    public void AStretchedPieceRunsAlongItsStretchedAxis()
    {
        // Owner: a bridge piece stretched along X did not join the same piece. A 4 x 5 m slab (longer along Y) stretched
        // 1.5x along X runs along X (6 m) and joins end to end along X.
        var slab = new BoundingBox(new Vector3(0, 0, 0), new Vector3(400, 500, 50));
        Assert.True(PieceSnap.Ends(slab).AlongY);
        Assert.False(PieceSnap.Ends(slab, new FVector(1.5f, 1f, 1f)).AlongY);
        var stretched = new FVector(1.5f, 1f, 1f);
        var target = new SnapPiece("SM_Slab", new FTransform(new FRotator(0, 0, 0), FVector.Zero, stretched), slab);
        var moving = target with { World = new FTransform(new FRotator(0, 0, 0), new FVector(650, 30, 0), stretched) };
        var snapped = PieceSnap.Best(moving, [target], PieceSnap.Reach(moving));
        Assert.NotNull(snapped);
        Assert.Equal(600f, snapped!.Value.World.Translation.X, 0.5f); // right after the 6 m piece
        Assert.Equal(0f, snapped.Value.World.Translation.Y, 0.5f);
        Assert.Equal(stretched, snapped.Value.World.Scale3D);
    }

    [Theory]
    [InlineData("/Game/ConZ_Files/Models/Road/KrkBridge/KB_Meshes/SM_KrkBridge_Fill", true)]
    [InlineData("/Game/ConZ_Files/Models/Road/RiverBridge/SM_RiverBridge_01", true)]
    [InlineData("/Game/ConZ_Files/Models/Buildings/Prison/Buildings/SM_Prison_Bridge", true)]
    [InlineData("/Game/ConZ_Files/Landscape/Landscape_WM/SM_A_0_4c_Bridge_01_WM", false)]
    [InlineData("/Game/ConZ_Files/Models/Buildings/Church/SM_Church_01", false)]
    public void RoadAndBridgePiecesBendWhateverTheirShape(string mesh, bool bends) =>
        Assert.Equal(bends, GizmoMath.IsRoadOrBridgePiece(mesh));

    [Fact]
    public void AJointMadeByHandIsOfferedAgain()
    {
        var pillar = new BoundingBox(new Vector3(-100, -100, 0), new Vector3(100, 100, 800)); // square: "along X"
        var bridgeA = new SnapPiece("SM_Bridge", new FTransform(new FRotator(0, 10, 0), new FVector(0, 0, 0), FVector.One), Wall);
        var placed = new SnapPiece("SM_Pillar", new FTransform(new FRotator(0, 55, 0), bridgeA.World.TransformPosition(new FVector(600, 0, 0)) + new FVector(0, 0, -30), FVector.One), pillar);
        var joint = PieceSnap.JointOf(placed, bridgeA);
        Assert.NotNull(joint);

        // A second bridge elsewhere: dropping a pillar near the same spot of it lands exactly like the first time.
        var bridgeB = new SnapPiece("SM_Bridge", new FTransform(new FRotator(0, -40, 0), new FVector(9000, 3000, 200), FVector.One), Wall);
        var expected = (joint!.Value * bridgeB.World);
        var moving = placed with { World = new FTransform(new FRotator(0, 0, 0), expected.Translation + new FVector(40, 40, 0), FVector.One) };
        var snapped = PieceSnap.Best(moving, [bridgeB], PieceSnap.Reach(moving), _ => [joint.Value]);

        Assert.NotNull(snapped);
        Assert.True(FVector.Distance(snapped!.Value.World.Translation, expected.Translation) < 0.01f);
        Assert.Equal(expected.Rotator().Yaw, snapped.Value.World.Rotator().Yaw, 0.01f);
    }
    // A road piece 10 m long on X, 8 m wide, its surface at Z 0.
    private static readonly BoundingBox Road = new(new Vector3(0, -400, -50), new Vector3(1000, 400, 0));

    [Fact]
    public void ARoadDroppedBesideAnotherLiesFlushAgainstIt()
    {
        var target = new SnapPiece("SM_Road", new FTransform(new FRotator(0, 20, 0), new FVector(0, 0, 100), FVector.One), Road);

        // Dropped about a metre off its side, a little high, 3 m along it.
        var moving = new SnapPiece("SM_Road", new FTransform(new FRotator(0, 25, 0), target.World.TransformPosition(new FVector(300, 900, 40)), FVector.One), Road);
        var snapped = PieceSnap.Best(moving, [target], PieceSnap.Reach(moving));

        Assert.NotNull(snapped);
        var local = target.World.InverseTransformPosition(snapped!.Value.World.Translation);
        Assert.Equal(800f, local.Y, 0.5f); // side against side: no gap, no overlap
        Assert.Equal(0f, local.Z, 0.5f); // tops level: no step for a car
        Assert.Equal(300f, local.X, 0.5f); // where it was dropped along the road
        Assert.Equal(20f, snapped.Value.World.Rotator().Yaw, 0.01f);
    }

    [Fact]
    public void AFenceStandsOnTheRoadAlongItsEdge()
    {
        float[] p = [0, -400, -50, 1000, -400, -50, 0, 400, -50, 1000, 400, -50, 0, -400, 0, 1000, -400, 0, 0, 400, 0, 1000, 400, 0];
        var deck = MeshData.Create("deck", p, [0, 1, 3, 0, 3, 2, 4, 7, 5, 4, 6, 7]);
        var target = new SnapPiece("SM_Road", new FTransform(new FRotator(0, -15, 0), new FVector(2000, 300, 500), FVector.One), Road, deck);
        var fence = new BoundingBox(new Vector3(0, -10, 0), new Vector3(300, 10, 120));

        // Brought near the right edge, a bit above the deck, turned the other way: it keeps facing that way.
        var moving = new SnapPiece("SM_Fence", new FTransform(new FRotator(0, 170, 0), target.World.TransformPosition(new FVector(500, 350, 60)), FVector.One), fence);
        var snapped = PieceSnap.Best(moving, [target], PieceSnap.Reach(moving));

        Assert.NotNull(snapped);
        var world = snapped!.Value.World;
        var foot = target.World.InverseTransformPosition(world.TransformPosition(new FVector(150, 0, 0)));
        Assert.Equal(0f, foot.Z, 0.5f); // standing on the deck
        Assert.Equal(390f, foot.Y, 0.5f); // its outer side flush with the road's edge
        Assert.Equal(165f, world.Rotator().Yaw, 0.01f);
    }

    [Fact]
    public void AStraightPiecesEndsAreWeldTargets()
    {
        var piece = new SnapPiece("SM_Road", new FTransform(new FRotator(0, 90, 0), new FVector(100, 200, 300), FVector.One), Road);
        var ends = PieceSnap.EndSections(piece);
        Assert.True(FVector.Distance(ends[1].Middle, new FVector(100, 1200, 300)) < 0.01f, $"end at {ends[1].Middle}");
        Assert.True(FVector.Dot(ends[1].Outward, FVector.Right) > 0.9999f);
        Assert.True(FVector.Dot(ends[0].Outward, FVector.Right) < -0.9999f);
        Assert.Equal(800f, ends[0].Width, 0.01f);
    }
}
