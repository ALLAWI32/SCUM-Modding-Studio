using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.SceneGraph;
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
        Assert.Equal(1500f, GizmoMath.HandleLength(10_000f));
        Assert.True(GizmoMath.HitTolerance(1500f) > 50f);
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

    [Fact]
    public void RotateAboutTurnsAboutAnyAxisAndKeepsThePlace()
    {
        var start = new FTransform(new FRotator(0, 30, 0), new FVector(1, 2, 3), new FVector(2, 2, 2));

        // About world Z: plain yaw, like RotateYaw.
        var yawed = GizmoMath.RotateAbout(start, new FVector(0, 0, 1), 47f);
        Assert.Equal(77f, yawed.Rotator().Yaw, 0.01f);
        Assert.Equal(0f, yawed.Rotator().Pitch, 0.01f);
        Assert.Equal(start.Translation, yawed.Translation);
        Assert.Equal(start.Scale3D, yawed.Scale3D);

        // About world X by 90 (UE, left-handed): +Y goes to +Z, so +Z goes to -Y.
        var rolled = GizmoMath.RotateAbout(FTransform.Identity, new FVector(1, 0, 0), 90f);
        var up = rolled.Rotation.RotateVector(new FVector(0, 0, 1));
        Assert.Equal(0f, up.X, 1e-4f);
        Assert.Equal(-1f, up.Y, 1e-4f);
        Assert.Equal(0f, up.Z, 1e-4f);

        // About the object's own forward axis (a world-space direction): its forward stays, its right turns.
        var forward = start.Rotation.RotateVector(new FVector(1, 0, 0));
        var spun = GizmoMath.RotateAbout(start, forward, 90f);
        var stillForward = spun.Rotation.RotateVector(new FVector(1, 0, 0));
        Assert.Equal(forward.X, stillForward.X, 1e-4f);
        Assert.Equal(forward.Y, stillForward.Y, 1e-4f);
        Assert.Equal(forward.Z, stillForward.Z, 1e-4f);
        Assert.True(spun.Rotation.IsNormalized());

        Assert.Equal(start, GizmoMath.RotateAbout(start, FVector.Zero, 45f));
        Assert.Equal(start, GizmoMath.RotateAbout(start, new FVector(0, 0, 1), 0f));
    }

    [Fact]
    public void TranslateInPlaneMovesAlongTwoAxesWithSnapPerAxis()
    {
        var start = new FTransform(FQuat.Identity, new FVector(100, 200, 300), FVector.One);
        var moved = GizmoMath.TranslateInPlane(start, new FVector(1, 0, 0), new FVector(0, 1, 0), 123f, -47f, 0f);
        Assert.Equal(new FVector(223, 153, 300), moved.Translation);

        var snapped = GizmoMath.TranslateInPlane(start, new FVector(1, 0, 0), new FVector(0, 1, 0), 123f, -47f, 50f);
        Assert.Equal(new FVector(200, 150, 300), snapped.Translation);
        Assert.Equal(start.Rotation, snapped.Rotation);
        Assert.Equal(start.Scale3D, snapped.Scale3D);
    }

    [Fact]
    public void ScaleChangesOneAxisOrAllWithSnapAndAFloor()
    {
        var start = new FTransform(FQuat.Identity, new FVector(1, 2, 3), new FVector(2, 1, 0.5f));
        Assert.Equal(new FVector(3, 1, 0.5f), GizmoMath.Scale(start, GizmoAxis.ScaleX, 1.5f, 0f).Scale3D);
        Assert.Equal(new FVector(2, 1.5f, 0.5f), GizmoMath.Scale(start, GizmoAxis.ScaleY, 1.5f, 0f).Scale3D);
        Assert.Equal(new FVector(2, 1, 0.75f), GizmoMath.Scale(start, GizmoAxis.ScaleZ, 1.5f, 0f).Scale3D);
        Assert.Equal(new FVector(4, 2, 1), GizmoMath.Scale(start, GizmoAxis.ScaleUniform, 2f, 0f).Scale3D);
        Assert.Equal(start.Translation, GizmoMath.Scale(start, GizmoAxis.ScaleUniform, 2f, 0f).Translation);

        // Snap to tenths; never to nothing.
        Assert.Equal(2.4f, GizmoMath.Scale(start, GizmoAxis.ScaleX, 1.23f, 0.1f).Scale3D.X, 1e-4f);
        Assert.Equal(0.02f, GizmoMath.Scale(start, GizmoAxis.ScaleX, -3f, 0f).Scale3D.X, 1e-4f);
        Assert.Equal(0.02f, GizmoMath.Scale(start, GizmoAxis.ScaleX, 0.01f, 0.1f).Scale3D.X, 1e-4f);
    }

    [Fact]
    public void PlaneHitAndAngleFollowTheRingBasis()
    {
        // Looking straight down at the GL XZ plane (UE XY): the hit lands under the ray.
        Assert.True(GizmoMath.TryHitPlane(new Vector3(30, 100, 40), new Vector3(0, -1, 0), Vector3.Zero, Vector3.UnitY, out var hit));
        Assert.Equal(new Vector3(30, 0, 40), hit);
        Assert.False(GizmoMath.TryHitPlane(new Vector3(0, 100, 0), Vector3.UnitX, Vector3.Zero, Vector3.UnitY, out _)); // parallel
        Assert.False(GizmoMath.TryHitPlane(new Vector3(0, 100, 0), Vector3.UnitY, Vector3.Zero, Vector3.UnitY, out _)); // away from it

        // The angle runs from u (0) towards v (90), the same in GL and UE as both are dot products.
        var u = GizmoMath.GlDirection(GizmoAxis.X);
        var v = GizmoMath.GlDirection(GizmoAxis.Y);
        Assert.Equal(0f, GizmoMath.PlaneAngle(u * 5f, Vector3.Zero, u, v), 1e-3f);
        Assert.Equal(90f, GizmoMath.PlaneAngle(v * 5f, Vector3.Zero, u, v), 1e-3f);
        Assert.Equal(-135f, GizmoMath.PlaneAngle((-u - v) * 5f, Vector3.Zero, u, v), 1e-3f);

        // A quarter turn measured on the Z ring is a quarter yaw: X ends up where Y was.
        var turned = GizmoMath.RotateAbout(FTransform.Identity, GizmoMath.UeDirection(GizmoAxis.Z), GizmoMath.AngleDelta(0f, 90f));
        var x = turned.Rotation.RotateVector(new FVector(1, 0, 0));
        Assert.Equal(0f, x.X, 1e-4f);
        Assert.Equal(1f, x.Y, 1e-4f);
    }

    [Fact]
    public void HandleKindsAndAxesAreKnown()
    {
        Assert.Equal(GizmoKind.Move, GizmoMath.KindOf(GizmoAxis.X));
        Assert.Equal(GizmoKind.Plane, GizmoMath.KindOf(GizmoAxis.PlaneZX));
        Assert.Equal(GizmoKind.Rotate, GizmoMath.KindOf(GizmoAxis.RotateView));
        Assert.Equal(GizmoKind.Scale, GizmoMath.KindOf(GizmoAxis.ScaleUniform));
        Assert.Equal(GizmoKind.None, GizmoMath.KindOf(GizmoAxis.None));
        Assert.Equal(GizmoAxis.Z, GizmoMath.AxisOf(GizmoAxis.PlaneXY)); // the square's normal
        Assert.Equal(GizmoAxis.X, GizmoMath.AxisOf(GizmoAxis.PlaneYZ));
        Assert.Equal(GizmoAxis.Y, GizmoMath.AxisOf(GizmoAxis.ScaleY));
        Assert.Equal(GizmoAxis.None, GizmoMath.AxisOf(GizmoAxis.ScaleUniform));
        Assert.Equal((GizmoAxis.Z, GizmoAxis.X), GizmoMath.PlaneAxes(GizmoAxis.PlaneZX));
    }

    [Fact]
    public void OverlayDrawsEveryHandleWithDarkEdgesAndAGlobe()
    {
        var frame = new GizmoFrame(new Vector3(100, 50, -20), Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY, new Vector3(100, 600, 800), Vector3.Normalize(new Vector3(0, -550, -820)), 200f);
        var lines = new List<OverlayLine>();
        var triangles = new List<OverlayTriangle>();
        GizmoOverlay.Draw(frame, GizmoAxis.None, GizmoAxis.None, dragging: false, scale: true, lines, triangles);
        Assert.True(triangles.Count > 0, "cones, squares and cubes are filled");
        Assert.Equal(0, lines.Count % 2);
        var edges = lines.Take(lines.Count / 2).ToList();
        Assert.All(edges, e => Assert.True(e.Color.X < 0.05f && e.Width > GizmoOverlay.LineWidth, "every line gets a wider dark edge under it"));

        // The globe: the back half of the X and Y rings is dim and thin; the spin ring (Z) is purple, thicker and whole,
        // the Z arrow stays blue.
        var zRing = GizmoOverlay.RingPoints(frame, GizmoAxis.RotateZ);
        Assert.True(GizmoOverlay.FacesCamera(frame, frame.Origin + (Vector3.UnitY * 10f)));
        Assert.False(GizmoOverlay.FacesCamera(frame, frame.Origin - (Vector3.UnitY * 10f)));
        Assert.Contains(lines, l => l.Color == GizmoOverlay.Red with { W = 0.3f } && l.Width < GizmoOverlay.LineWidth);
        Assert.Contains(lines, l => l.Color == GizmoOverlay.Blue && l.Width == GizmoOverlay.LineWidth);
        Assert.DoesNotContain(lines, l => l.Color == GizmoOverlay.Blue with { W = 0.3f });
        var purple = lines.Where(l => l.Color == GizmoOverlay.Purple).ToList();
        Assert.Equal(zRing.Length - 1, purple.Count);
        Assert.All(purple, l => Assert.True(l.Width > GizmoOverlay.LineWidth, "the spin ring is thicker than the others"));
        Assert.DoesNotContain(lines, l => l.Color == GizmoOverlay.Purple with { W = 0.3f });
        Assert.Equal(GizmoOverlay.Purple, GizmoOverlay.ColorOf(GizmoAxis.RotateZ));
        Assert.Equal(GizmoOverlay.Blue, GizmoOverlay.ColorOf(GizmoAxis.Z));
        Assert.True(GizmoOverlay.Purple.Z > GizmoOverlay.Purple.X && GizmoOverlay.Purple.X > GizmoOverlay.Purple.Y, "purple: blue over red over green");
        Assert.Equal(zRing[0], zRing[^1]);

        // Pick shapes: cubes first, rings last, the view ring whole, the axis rings only on their front half.
        var shapes = GizmoOverlay.PickShapes(frame, scale: true);
        Assert.Equal(GizmoAxis.ScaleX, shapes[0].Handle);
        Assert.Equal(0, shapes[0].Priority);
        Assert.Equal(GizmoAxis.RotateView, shapes[^1].Handle);
        Assert.Equal(65, shapes[^1].Points.Length);
        Assert.All(shapes.Where(s => s.Handle == GizmoAxis.RotateX).SelectMany(s => s.Points), p => Assert.True(Vector3.Dot(p - frame.Origin, frame.View) > -1f));
        Assert.Equal(65, Assert.Single(shapes, s => s.Handle == GizmoAxis.RotateZ).Points.Length); // the spin ring grabs anywhere
        Assert.DoesNotContain(GizmoOverlay.PickShapes(frame, scale: false), s => GizmoMath.KindOf(s.Handle) == GizmoKind.Scale);

        // Hover and drag: the lit handle is yellow, the others fade while dragging.
        lines.Clear();
        triangles.Clear();
        GizmoOverlay.Draw(frame, GizmoAxis.None, GizmoAxis.X, dragging: true, scale: true, lines, triangles);
        Assert.Contains(lines, l => l.Color == GizmoOverlay.Yellow);
        Assert.Contains(lines, l => l.Color == GizmoOverlay.Green with { W = 0.4f });
        Assert.DoesNotContain(lines, l => l.Color == GizmoOverlay.Green);
    }

    [Fact]
    public void ThePivotIsTheMiddleOfWhatIsDrawnNotTheRoot()
    {
        // Owner: the globe stood at the root, 10 m away from the object. A building whose root is a corner: its mesh
        // spans 12 x 8 x 6 m, the middle 10 m in front of the root; the root turned and scaled.
        var root = new FTransform(new FRotator(0, 30, 0), new FVector(5000, -2000, 300), new FVector(2, 2, 2));
        var mesh = new BoundingBox(new Vector3(400, -400, -300), new Vector3(1600, 400, 300)); // UE, root space (before the scale)
        var world = UeToGl.ModelMatrixForUeVertices(root);
        var box = GizmoMath.LocalBox([(mesh, world, "/Game/ConZ_Files/Models/Buildings/House/SM_House")], root);
        Assert.Equal(1000f, box.Center.X, 0.01f);
        Assert.Equal(0f, box.Center.Y, 0.01f);

        var (local, slides) = GizmoMath.PivotLocal(box, root, "/Game/ConZ_Files/Models/Buildings/House/SM_House", new Vector3(0, 5000, 0), -Vector3.UnitY);
        Assert.False(slides);
        Assert.Equal(new FVector(1000, 0, 0), local);
        Assert.Equal(2000f, FVector.Distance(root.TransformPosition(local), root.Translation), 0.1f); // 10 m, scaled twice

        // Nothing drawn: the root.
        Assert.Equal((FVector.Zero, false), GizmoMath.PivotLocal(BoundingBox.Empty, root, null, Vector3.Zero, -Vector3.UnitY));
    }

    [Fact]
    public void ALongPiecesPivotIsWhereTheCameraLooksAlongIt()
    {
        // A 200 m road along its root's X (turned to run along world Y), 8 m wide.
        var root = new FTransform(new FRotator(0, 90, 0), new FVector(0, 0, 0), FVector.One);
        var road = new BoundingBox(new Vector3(0, -400, 0), new Vector3(20000, 400, 50));
        const string mesh = "/Game/ConZ_Files/Landscape/Roads/SM_Road_01#spline3";
        Assert.True(GizmoMath.IsLong(20000, 800, mesh));
        Assert.False(GizmoMath.IsLong(1200, 800, mesh));
        Assert.False(GizmoMath.IsLong(4000, 1000, "/Game/ConZ_Files/Models/Buildings/Wall/SM_Wall")); // 6 times in the building folder
        Assert.True(GizmoMath.IsLong(7000, 1000, "/Game/ConZ_Files/Models/Buildings/Wall/SM_Wall"));

        // The camera looks at the road 60 m along it, from the side: the pivot is there, on the road's middle line.
        Vector3 Look(FVector from, FVector at) => Vector3.Normalize(UeToGl.Point(at) - UeToGl.Point(from));
        var eye = new FVector(-3000, 6000, 3000);
        var (local, slides) = GizmoMath.PivotLocal(road, root, mesh, UeToGl.Point(eye), Look(eye, new FVector(0, 6000, 0)));
        Assert.True(slides);
        Assert.Equal(6000f, local.X, 1f);
        Assert.Equal(0f, local.Y, 0.01f);
        Assert.Equal(25f, local.Z, 0.01f);

        // Looking past its end: the end; looking along it from beyond the start: the start, nearest the eye.
        Assert.Equal(20000f, GizmoMath.PivotLocal(road, root, mesh, UeToGl.Point(eye), Look(eye, new FVector(0, 30000, 0))).Local.X, 1f);
        var behind = new FVector(0, -5000, 25);
        Assert.Equal(0f, GizmoMath.PivotLocal(road, root, mesh, UeToGl.Point(behind), Look(behind, new FVector(0, 0, 25))).Local.X, 1f);

        // Without a mesh path (a multi-selection) it is never long: the middle. Nor is a spawn stand-in (a trader with his
        // arms out is 1.4 m wide and 0.4 m deep).
        Assert.Equal(10000f, GizmoMath.PivotLocal(road, root, null, UeToGl.Point(eye), Look(eye, new FVector(0, 6000, 0))).Local.X, 0.01f);
        var trader = new BoundingBox(new Vector3(-70, -19, 0), new Vector3(70, 19, 180));
        Assert.Equal((new FVector(0, 0, 90), false), GizmoMath.PivotLocal(trader, root, SpawnMarkers.MeshKey(SpawnKind.Trader, "/Game/NPC/SK_Mechanic.SK_Mechanic"), UeToGl.Point(eye), Look(eye, FVector.Zero)));
    }
}
