using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

public sealed class GizmoMathTests
{
    [Fact]
    public void AutoSnapPullsFlushAgainstANeighbourInReachOnly()
    {
        // A 100 cm tower piece dragged to 20 cm short of the next one, 5 cm higher: it closes the gap and lines up.
        var moving = new BoundingBox(new Vector3(0, 0, 5), new Vector3(100, 100, 105));
        var neighbour = new BoundingBox(new Vector3(120, 0, 0), new Vector3(220, 100, 100));
        var far = new BoundingBox(new Vector3(5000, 0, 0), new Vector3(5100, 100, 100)); // shares Y/Z but is out of reach
        Assert.Equal(new Vector3(20, 0, -5), GizmoMath.SnapOffset(moving, [neighbour, far], 30f));
        Assert.Equal(Vector3.Zero, GizmoMath.SnapOffset(moving, [far], 30f));
    }

    [Fact]
    public void AxisDirectionsMapUeToGl()
    {
        Assert.Equal(new Vector3(1, 0, 0), GizmoMath.GlDirection(GizmoAxis.X));
        Assert.Equal(new Vector3(0, 0, 1), GizmoMath.GlDirection(GizmoAxis.Y)); // UE Y is GL Z
        Assert.Equal(new Vector3(0, 1, 0), GizmoMath.GlDirection(GizmoAxis.Z)); // UE Z (up) is GL Y
        Assert.Equal(FVector.Zero, GizmoMath.UeDirection(GizmoAxis.None));
    }

    [Fact]
    public void ClosestParameterFindsThePointUnderTheRay()
    {
        // Camera above, looking straight down at the XZ plane; the line is the GL X axis through the origin.
        var ok = GizmoMath.TryClosestParameter(new Vector3(30, 100, 0), new Vector3(0, -1, 0), Vector3.Zero, Vector3.UnitX, out var t, out var distance);
        Assert.True(ok);
        Assert.Equal(30f, t, 1e-3f);
        Assert.Equal(0f, distance, 1e-3f);

        // Ray parallel to the line: no unique answer.
        Assert.False(GizmoMath.TryClosestParameter(Vector3.Zero, Vector3.UnitX, new Vector3(0, 5, 0), Vector3.UnitX, out _, out _));

        // A ray passing 7 units beside the line reports that distance.
        Assert.True(GizmoMath.TryClosestParameter(new Vector3(10, 50, 7), new Vector3(0, -1, 0), Vector3.Zero, Vector3.UnitX, out t, out distance));
        Assert.Equal(10f, t, 1e-3f);
        Assert.Equal(7f, distance, 1e-3f);
    }

    [Fact]
    public void HitAxisPicksTheNearestHandleWithinTolerance()
    {
        var origin = new Vector3(100, 20, -40);
        const float length = 200f;
        var tolerance = GizmoMath.HitTolerance(length);

        // Straight down onto the middle of the X handle.
        var axis = GizmoMath.HitAxis(origin + new Vector3(120, 500, 0), new Vector3(0, -1, 0), origin, length, tolerance, out var parameter);
        Assert.Equal(GizmoAxis.X, axis);
        Assert.Equal(120f, parameter, 1e-2f);

        // Down onto the GL Z handle (UE Y).
        axis = GizmoMath.HitAxis(origin + new Vector3(0, 500, 90), new Vector3(0, -1, 0), origin, length, tolerance, out parameter);
        Assert.Equal(GizmoAxis.Y, axis);
        Assert.Equal(90f, parameter, 1e-2f);

        // Beyond the handle end, or far beside it: nothing.
        Assert.Equal(GizmoAxis.None, GizmoMath.HitAxis(origin + new Vector3(length + 100, 500, 0), new Vector3(0, -1, 0), origin, length, tolerance, out _));
        Assert.Equal(GizmoAxis.None, GizmoMath.HitAxis(origin + new Vector3(120, 500, tolerance * 3), new Vector3(0, -1, 0), origin, length, tolerance, out _));
    }

    [Fact]
    public void TranslateMovesAlongTheUeAxisWithSnapping()
    {
        var start = new FTransform(new FRotator(0, 45, 0), new FVector(100, 200, 300), new FVector(2, 2, 2));
        var moved = GizmoMath.Translate(start, GizmoAxis.Z, 123f, 0f);
        Assert.Equal(new FVector(100, 200, 423), moved.Translation);
        Assert.Equal(start.Rotation, moved.Rotation);
        Assert.Equal(start.Scale3D, moved.Scale3D);

        var snapped = GizmoMath.Translate(start, GizmoAxis.Y, 123f, 50f);
        Assert.Equal(new FVector(100, 300, 300), snapped.Translation);
        Assert.Equal(0f, GizmoMath.Snap(4.9f, 10f));
        Assert.Equal(10f, GizmoMath.Snap(5.1f, 10f));
        Assert.Equal(7.5f, GizmoMath.Snap(7.5f, 0f));
    }

    [Fact]
    public void FreeDragFollowsTheGroundPlaneTurnsAndLifts()
    {
        // Looking straight down from 10 m above the plane at GL height 2 m (UE Z 200).
        var hit = GizmoMath.HitHorizontalPlane(new Vector3(500, 1000, 300), -Vector3.UnitY, 200f, 10_000f);
        Assert.Equal(new Vector3(500, 200, 300), hit);
        Assert.Null(GizmoMath.HitHorizontalPlane(new Vector3(0, 1000, 0), Vector3.UnitY, 200f, 10_000f)); // points away
        Assert.Null(GizmoMath.HitHorizontalPlane(new Vector3(0, 1000, 0), Vector3.UnitX, 200f, 10_000f)); // parallel
        Assert.Null(GizmoMath.HitHorizontalPlane(new Vector3(0, 1000, 0), -Vector3.UnitY, 200f, 100f)); // too far

        var start = new FTransform(new FRotator(0, 30, 0), new FVector(100, 200, 300), new FVector(1, 1, 1));
        // GL move (+X 123, up 999 ignored, GL +Z = UE +Y 77) snapped to 10 cm, lifted 50 cm, turned 15 degrees.
        var moved = GizmoMath.FreeMove(start, new Vector3(123, 999, 77), 50f, 15f, 10f);
        Assert.Equal(new FVector(220, 280, 350), moved.Translation);
        Assert.Equal(45f, moved.Rotator().Yaw, 0.01f);
        Assert.Equal(start.Scale3D, moved.Scale3D);
        Assert.Equal(start, GizmoMath.FreeMove(start, Vector3.Zero, 0f, 0f, 10f));
    }

    [Fact]
    public void HandleLengthStaysReadable()
    {
        Assert.Equal(50f, GizmoMath.HandleLength(10f));
        Assert.Equal(1200f, GizmoMath.HandleLength(10_000f));
        Assert.True(GizmoMath.HitTolerance(1200f) > 50f);
    }

    [Fact]
    public void YawRingIsHitAndMeasuredInUeDegrees()
    {
        var origin = new Vector3(100, 20, -40);
        const float length = 200f;
        var radius = GizmoMath.RingRadius(length);
        var tolerance = GizmoMath.HitTolerance(length);

        // Straight down onto the ring at 45 degrees between UE +X and +Y (GL +X / +Z), away from the axis handles.
        var diagonal = radius * MathF.Sqrt(0.5f);
        var axis = GizmoMath.HitAxis(origin + new Vector3(diagonal, 500, diagonal), new Vector3(0, -1, 0), origin, length, tolerance, out var angle);
        Assert.Equal(GizmoAxis.Yaw, axis);
        Assert.Equal(45f, angle, 0.01f);

        // Where the ring crosses the Y handle the handle wins.
        Assert.Equal(GizmoAxis.Y, GizmoMath.HitAxis(origin + new Vector3(0, 500, radius), new Vector3(0, -1, 0), origin, length, tolerance, out _));

        // On the ring at UE -X: 180 degrees; an axis handle under the cursor wins over the ring.
        Assert.True(GizmoMath.TryRingAngle(origin + new Vector3(-radius, 500, 0), new Vector3(0, -1, 0), origin, radius, out angle, out var distance));
        Assert.Equal(180f, MathF.Abs(angle), 0.01f);
        Assert.Equal(0f, distance, 0.01f);
        Assert.Equal(GizmoAxis.X, GizmoMath.HitAxis(origin + new Vector3(radius, 500, 0), new Vector3(0, -1, 0), origin, length, tolerance, out _));

        // Parallel to the plane: no hit.
        Assert.False(GizmoMath.TryRingAngle(origin + new Vector3(0, 0, 500), new Vector3(1, 0, 0), origin, radius, out _, out _));

        Assert.Equal(48 + 1, GizmoMath.RingPoints(origin, radius).Length);
        Assert.Equal(GizmoMath.RingPoints(origin, radius)[0], GizmoMath.RingPoints(origin, radius)[^1]);
    }

    [Fact]
    public void RotateYawTurnsAroundWorldZWithSnapping()
    {
        var start = new FTransform(new FRotator(10, 30, 5), new FVector(1, 2, 3), new FVector(2, 2, 2));
        var turned = GizmoMath.RotateYaw(start, 47f, 0f);
        Assert.Equal(77f, turned.Rotator().Yaw, 0.01f);
        Assert.Equal(10f, turned.Rotator().Pitch, 0.01f);
        Assert.Equal(5f, turned.Rotator().Roll, 0.01f);
        Assert.Equal(start.Translation, turned.Translation);
        Assert.Equal(start.Scale3D, turned.Scale3D);
        Assert.Equal(75f, GizmoMath.RotateYaw(start, 47f, 15f).Rotator().Yaw, 0.01f);

        Assert.Equal(20f, GizmoMath.AngleDelta(170f, -170f), 0.01f); // wraps through 180
        Assert.Equal(-90f, GizmoMath.AngleDelta(45f, -45f), 0.01f);
    }
}
