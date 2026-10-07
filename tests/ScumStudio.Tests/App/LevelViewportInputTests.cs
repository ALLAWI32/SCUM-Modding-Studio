using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using System.Globalization;
using System.Numerics;
using Avalonia.Media;
using ScumStudio.App.Controls;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// The mouse must reach the 3D viewport. OpenGlControlBase draws through a child surface visual that Avalonia's hit
/// test ignores, so without its own hit area every click, drag and wheel went to the panel behind it (owner report:
/// "no control at all" on the Map page).
/// </summary>
public sealed class LevelViewportInputTests
{
    [AvaloniaFact]
    public void PointerHitTestFindsTheViewport()
    {
        var viewport = new LevelViewport();
        var window = new Window { Width = 400, Height = 300, Content = new Border { Background = Brushes.Black, Padding = new Thickness(20), Child = viewport } };
        window.Show();
        HeadlessUi.Pump();

        Assert.Same(viewport, window.InputHitTest(new Point(200, 150)));
        Assert.NotSame(viewport, window.InputHitTest(new Point(5, 5)));
        window.Close();
    }

    [AvaloniaFact]
    public void PointerHitTestFindsTheMeshPreview()
    {
        var preview = new MeshPreview();
        var window = new Window { Width = 400, Height = 300, Content = new Border { Background = Brushes.Black, Padding = new Thickness(20), Child = preview } };
        window.Show();
        HeadlessUi.Pump();

        Assert.Same(preview, window.InputHitTest(new Point(200, 150)));
        Assert.NotSame(preview, window.InputHitTest(new Point(5, 5)));
        window.Close();
    }
}

/// <summary>Drone mode: Tab toggles it, Escape always leaves it, and losing focus leaves it.</summary>
public sealed class LevelViewportDroneTests
{
    [AvaloniaFact]
    public void EscapeAndTabLeaveDroneMode()
    {
        var viewport = new LevelViewport();
        var window = new Window { Width = 400, Height = 300, Content = viewport };
        window.Show();
        HeadlessUi.Pump();
        viewport.Focus();

        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.True(viewport.IsDroneMode);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.False(viewport.IsDroneMode);

        viewport.ToggleDrone();
        Assert.True(viewport.IsDroneMode);
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.False(viewport.IsDroneMode);
        window.Close();
    }
}

/// <summary>
/// The transform gizmo on the headless platform (no GL, but the camera, the projection and the hit test are all there):
/// a press on each kind of handle starts the right drag, a move previews the expected transform, a release raises one
/// <see cref="LevelViewport.TransformDragged"/> (the one journaled op), scale drags say so.
/// </summary>
public sealed class LevelViewportGizmoTests
{
    private static readonly FTransform Start = new(new FRotator(0, 30, 0), new FVector(0, 0, 0), new FVector(1, 1, 1));

    [AvaloniaFact]
    public void ArrowsMoveAlongTheirAxis()
    {
        var (window, viewport, drags) = Show();
        foreach (var (handle, axis) in new[] { (GizmoAxis.X, 0), (GizmoAxis.Y, 1), (GizmoAxis.Z, 2) })
        {
            var preview = Drag(window, viewport, handle, towards: handle);
            var moved = preview.Translation - Start.Translation;
            var along = axis switch { 0 => moved.X, 1 => moved.Y, _ => moved.Z };
            Assert.True(MathF.Abs(along) > 1f, $"{handle}: moved {moved}");
            Assert.True(moved.Size() - MathF.Abs(along) < 0.01f * moved.Size() + 0.01f, $"{handle} moves along its world axis only: {moved}");
            Assert.Equal(Start.Rotation, preview.Rotation);
            Assert.Equal(Start.Scale3D, preview.Scale3D);
        }

        Assert.Equal(3, drags.Count);
        Assert.All(drags, d => Assert.False(d.Scaled));
        window.Close();
    }

    [AvaloniaFact]
    public void LocalAxesFollowTheObject()
    {
        var (window, viewport, drags) = Show(localAxes: true);
        var preview = Drag(window, viewport, GizmoAxis.X, towards: GizmoAxis.X);
        var moved = preview.Translation - Start.Translation;
        var forward = Start.Rotation.RotateVector(new FVector(1, 0, 0));
        Assert.True(moved.Size() > 1f);
        Assert.Equal(1f, MathF.Abs(FVector.Dot(moved.GetSafeNormal(), forward)), 1e-3f);
        Assert.Single(drags);
        window.Close();
    }

    [AvaloniaFact]
    public void PlaneSquaresMoveInTheirPlane()
    {
        var (window, viewport, drags) = Show();
        var xy = Drag(window, viewport, GizmoAxis.PlaneXY, towards: GizmoAxis.X);
        Assert.Equal(0f, xy.Translation.Z, 0.01f);
        Assert.True(MathF.Abs(xy.Translation.X) + MathF.Abs(xy.Translation.Y) > 1f);

        var yz = Drag(window, viewport, GizmoAxis.PlaneYZ, towards: GizmoAxis.Z);
        Assert.Equal(0f, yz.Translation.X, 0.01f);
        Assert.True(MathF.Abs(yz.Translation.Z) > 1f);

        var zx = Drag(window, viewport, GizmoAxis.PlaneZX, towards: GizmoAxis.X);
        Assert.Equal(0f, zx.Translation.Y, 0.01f);
        Assert.Equal(3, drags.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void RingsTurnAboutTheirAxisAndTheViewRingAboutTheView()
    {
        var (window, viewport, drags) = Show();
        var yawed = Drag(window, viewport, GizmoAxis.RotateZ, around: true);
        Assert.Equal(Start.Translation, yawed.Translation);
        Assert.Equal(Start.Scale3D, yawed.Scale3D);
        Assert.True(MathF.Abs(GizmoMath.AngleDelta(30f, yawed.Rotator().Yaw)) > 5f, "turned about Z");
        Assert.Equal(0f, yawed.Rotator().Pitch, 0.1f);
        Assert.Equal(0f, yawed.Rotator().Roll, 0.1f);

        var pitched = Drag(window, viewport, GizmoAxis.RotateY, around: true);
        var y = Start.Rotation.RotateVector(new FVector(0, 1, 0));
        var stillY = pitched.Rotation.RotateVector(new FVector(0, 1, 0));
        Assert.True(pitched.Rotation.AngularDistance(Start.Rotation) > 0.05f, "turned");
        Assert.True(FVector.Distance(y, stillY) > 0.05f, "about the world Y axis, not the object's own");

        var view = Drag(window, viewport, GizmoAxis.RotateView, around: true);
        Assert.True(view.Rotation.AngularDistance(Start.Rotation) > 0.05f, "turned about the view");
        Assert.Equal(Start.Translation, view.Translation);
        Assert.Equal(3, drags.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void CubesScaleAndSayScaled()
    {
        var (window, viewport, drags) = Show();
        var x = Drag(window, viewport, GizmoAxis.ScaleX, towards: GizmoAxis.X);
        Assert.True(x.Scale3D.X > 1.05f, $"grew along X: {x.Scale3D}");
        Assert.Equal(1f, x.Scale3D.Y, 1e-4f);
        Assert.Equal(1f, x.Scale3D.Z, 1e-4f);

        var all = Drag(window, viewport, GizmoAxis.ScaleUniform, towards: GizmoAxis.None);
        Assert.True(all.Scale3D.X > 1.05f, $"grew: {all.Scale3D}");
        Assert.Equal(all.Scale3D.X, all.Scale3D.Y, 1e-4f);
        Assert.Equal(all.Scale3D.X, all.Scale3D.Z, 1e-4f);
        Assert.Equal(2, drags.Count);
        Assert.All(drags, d => Assert.True(d.Scaled));

        // Hidden when the selection cannot be scaled.
        viewport.CanScale = false;
        Assert.Null(viewport.HandleScreenPoint(GizmoAxis.ScaleX));
        Assert.NotNull(viewport.HandleScreenPoint(GizmoAxis.X));
        window.Close();
    }

    [AvaloniaFact]
    public void AnEdgeOnRingStillTurnsAndAnEdgeOnSquareDoesNotFlyAway()
    {
        var (window, viewport, drags) = Show();
        viewport.Camera.Position = new Vector3(600f, 150f, 900f);
        viewport.Camera.Pitch = -8f; // near the horizon: the Z ring and the XY square are seen almost edge-on
        var yawed = Drag(window, viewport, GizmoAxis.RotateZ, around: true);
        Assert.True(MathF.Abs(GizmoMath.AngleDelta(30f, yawed.Rotator().Yaw)) > 5f, "turned about Z by the screen angle");
        Assert.Equal(0f, yawed.Rotator().Pitch, 0.1f);
        Assert.Equal(Start.Translation, yawed.Translation);

        // Closer to the horizon the square's plane cannot be hit cleanly: a press on it previews nothing, nothing is journaled.
        viewport.Camera.Position = new Vector3(600f, 50f, 900f);
        viewport.Camera.Pitch = -3f;
        if (viewport.HandleScreenPoint(GizmoAxis.PlaneXY) is { } at)
        {
            window.MouseDown(at, MouseButton.Left);
            window.MouseMove(at + new Avalonia.Vector(40, 0));
            Assert.Null(viewport.DragPreviewRoot);
            window.MouseUp(at + new Avalonia.Vector(40, 0), MouseButton.Left);
        }

        // With world axes on a turned object, the cube on world X stretches the object's own axis nearest to it (Y at yaw 30 is not it).
        viewport.LocalAxes = false;
        viewport.SelectedRootWorld = Start with { Rotation = new FRotator(0, 80, 0).Quaternion() };
        var x = Drag(window, viewport, GizmoAxis.ScaleX, towards: GizmoAxis.X);
        Assert.Equal(1f, x.Scale3D.X, 1e-4f);
        Assert.NotEqual(1f, x.Scale3D.Y, 1e-4f);
        Assert.Equal(2, drags.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void HoverLightsTheHandleAndShowsAHand()
    {
        var (window, viewport, _) = Show();
        var at = viewport.HandleScreenPoint(GizmoAxis.RotateX)!.Value;
        window.MouseMove(at);
        Assert.Equal(GizmoAxis.RotateX, viewport.ActiveAxis);
        Assert.NotNull(viewport.Cursor);
        window.MouseMove(new Point(5, 5));
        Assert.Equal(GizmoAxis.None, viewport.ActiveAxis);
        Assert.Null(viewport.Cursor);
        Assert.False(viewport.IsDragLabelVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void TheGizmoStandsOnTheMiddleOfWhatIsDrawnAndAMoveIsExactlyTheDrag()
    {
        // Owner: the globe stood at the root, 10-15 m from the selected thing. What is drawn has its middle 10 m from the root.
        var (window, viewport, drags) = Show();
        var centre = new FVector(700f, 700f, 200f);
        viewport.PivotPartsForTests = [Block(centre)];
        viewport.Camera.Orbit(UeToGl.Point(centre), 35f, -30f, 1500f);
        var origin = viewport.GizmoOrigin!.Value;
        Assert.True(Vector3.Distance(UeToGl.Point(centre), origin) < 0.5f, $"the gizmo stands on the middle, not at the root: {origin}");

        // An arrow drag: the object moves along the arrow exactly as far as the grabbed point of the arrow (through the
        // middle) followed the cursor, and the label says so.
        var at = viewport.HandleScreenPoint(GizmoAxis.X)!.Value;
        var to = at + new Avalonia.Vector(45, 12);
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(to);
        var label = viewport.DragLabel;
        var preview = viewport.DragPreviewRoot!.Value;
        window.MouseUp(to, MouseButton.Left);
        var moved = preview.Translation - Start.Translation;
        var delta = Along(viewport, to, origin) - Along(viewport, at, origin);
        Assert.True(MathF.Abs(delta) > 1f, $"dragged {delta}");
        Assert.Equal(delta, moved.X, 0.05f);
        Assert.Equal(0f, moved.Y, 1e-3f);
        Assert.Equal(0f, moved.Z, 1e-3f);
        Assert.Equal(delta / 100f, float.Parse(label[..^2], CultureInfo.InvariantCulture), 0.006f);
        Assert.Equal(Start.Rotation, preview.Rotation);
        Assert.Single(drags);

        // Dropped (the view model takes the new root, the scene draws the object there): the gizmo moved with it, no jump.
        viewport.SelectedRootWorld = preview;
        viewport.PivotPartsForTests = [Block(centre + moved)];
        Assert.True(Vector3.Distance(origin + UeToGl.Direction(moved), viewport.GizmoOrigin!.Value) < 0.5f);

        // One of a multi-selection: no scale cubes (one would grow, the others not).
        viewport.GroupWorlds = [new GroupWorld(7, null, preview), new GroupWorld(8, null, Start)];
        Assert.Null(viewport.HandleScreenPoint(GizmoAxis.ScaleX));
        Assert.NotNull(viewport.HandleScreenPoint(GizmoAxis.RotateZ));
        window.Close();
    }

    [AvaloniaFact]
    public void TheSpinRingTurnsAboutTheMiddleAllTheWayRound()
    {
        var (window, viewport, _) = Show();
        var centre = new FVector(700f, 700f, 200f);
        viewport.PivotPartsForTests = [Block(centre)];
        viewport.Camera.Orbit(UeToGl.Point(centre), 35f, -30f, 1500f);

        // A quarter turn on the purple ring: the middle stays where it is, the root swings round it.
        var yawed = Drag(window, viewport, GizmoAxis.RotateZ, around: true);
        Assert.True(MathF.Abs(GizmoMath.AngleDelta(30f, yawed.Rotator().Yaw)) > 5f, "turned about Z");
        Assert.Equal(0f, yawed.Rotator().Pitch, 0.1f);
        var middle = Start.InverseTransformPosition(centre);
        Assert.True(FVector.Distance(centre, yawed.TransformPosition(middle)) < 0.5f, $"the middle stays: {yawed.TransformPosition(middle)}");
        Assert.True(FVector.Distance(Start.Translation, yawed.Translation) > 50f, "the root swings round the middle");

        // Round and round: the label keeps counting past 180° (a whole spin reads 360°).
        var at = viewport.HandleScreenPoint(GizmoAxis.RotateZ)!.Value;
        var pivot = viewport.HandleScreenPoint(GizmoAxis.ScaleUniform)!.Value;
        var (radius, start) = (Math.Sqrt(((at.X - pivot.X) * (at.X - pivot.X)) + ((at.Y - pivot.Y) * (at.Y - pivot.Y))), Math.Atan2(at.Y - pivot.Y, at.X - pivot.X));
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        var point = at;
        for (var step = 1; step <= 7; step++)
        {
            var angle = start + (step * Math.PI / 4);
            point = new Point(pivot.X + (Math.Cos(angle) * radius), pivot.Y + (Math.Sin(angle) * radius));
            window.MouseMove(point);
        }

        var degrees = float.Parse(viewport.DragLabel.TrimEnd('°'), CultureInfo.InvariantCulture);
        window.MouseUp(point, MouseButton.Left);
        Assert.True(MathF.Abs(degrees) > 200f, $"turned {degrees}°");
        window.Close();
    }

    [AvaloniaFact]
    public void ALongPieceHasItsGizmoWhereTheCameraLooks()
    {
        // A 200 m road piece along its root's X: the gizmo stands on its middle line where the camera looks, and follows
        // the camera when it flies far, not when it nudges.
        var (window, viewport, _) = Show();
        viewport.SelectedRootWorld = FTransform.Identity;
        viewport.PivotPartsForTests = [(new BoundingBox(new Vector3(0, 0, -400), new Vector3(20000, 50, 400)), Matrix4x4.Identity, "/Game/ConZ_Files/Landscape/Roads/SM_Road_01#spline1")];
        viewport.Camera.Orbit(UeToGl.Point(new FVector(12000, 0, 0)), -60f, -30f, 3000f);
        Assert.Equal(12000f, viewport.GizmoOrigin!.Value.X, 100f);
        var there = viewport.GizmoOrigin!.Value;
        viewport.Camera.Position += new Vector3(100f, 0f, 0f);
        Assert.Equal(there, viewport.GizmoOrigin!.Value);
        viewport.Camera.Orbit(UeToGl.Point(new FVector(4000, 0, 0)), -60f, -30f, 3000f);
        Assert.Equal(4000f, viewport.GizmoOrigin!.Value.X, 100f);
        window.Close();
    }

    /// <summary>A drawn 3 m block whose middle is at <paramref name="centre"/> (UE).</summary>
    private static (BoundingBox Box, Matrix4x4 World, string? Mesh) Block(FVector centre)
    {
        var c = UeToGl.Point(centre);
        return (new BoundingBox(c - new Vector3(150f), c + new Vector3(150f)), Matrix4x4.Identity, "/Game/ConZ_Files/Props/SM_Block");
    }

    /// <summary>Where the cursor at <paramref name="p"/> grabs the X arrow line through <paramref name="origin"/> (cm along it).</summary>
    private static float Along(LevelViewport viewport, Point p, Vector3 origin)
    {
        var (o, d) = viewport.Camera.ScreenRay((float)p.X, (float)p.Y, (int)Math.Round(viewport.Bounds.Width), (int)Math.Round(viewport.Bounds.Height));
        Assert.True(GizmoMath.TryClosestParameter(o, d, origin, Vector3.UnitX, out var t, out _));
        return t;
    }

    private static (Window Window, LevelViewport Viewport, List<TransformDragEventArgs> Drags) Show(bool localAxes = false)
    {
        var viewport = new LevelViewport { LocalAxes = localAxes, SelectedId = 7, SelectedRootWorld = Start };
        var window = new Window { Width = 900, Height = 700, Content = viewport };
        window.Show();
        HeadlessUi.Pump();
        viewport.Camera.Position = new Vector3(600f, 500f, 900f);
        viewport.Camera.Yaw = -124f;
        viewport.Camera.Pitch = -25f;
        var drags = new List<TransformDragEventArgs>();
        viewport.TransformDragged += (_, e) => drags.Add(e);
        return (window, viewport, drags);
    }

    /// <summary>Presses on <paramref name="handle"/>, drags 40 px outward along <paramref name="towards"/> (or a quarter turn around the pivot), releases; the preview before the release.</summary>
    private static FTransform Drag(Window window, LevelViewport viewport, GizmoAxis handle, GizmoAxis towards = GizmoAxis.None, bool around = false)
    {
        var at = viewport.HandleScreenPoint(handle);
        Assert.True(at.HasValue, $"{handle} is on screen");
        var centre = viewport.HandleScreenPoint(GizmoAxis.ScaleUniform)!.Value;
        Point to;
        if (around)
        {
            var angle = Math.Atan2(at!.Value.Y - centre.Y, at.Value.X - centre.X) + (Math.PI / 4);
            var radius = Math.Sqrt(((at.Value.X - centre.X) * (at.Value.X - centre.X)) + ((at.Value.Y - centre.Y) * (at.Value.Y - centre.Y)));
            to = new Point(centre.X + (Math.Cos(angle) * radius), centre.Y + (Math.Sin(angle) * radius));
        }
        else if (towards == GizmoAxis.None)
        {
            to = at!.Value + new Avalonia.Vector(40, -40);
        }
        else
        {
            var tip = viewport.HandleScreenPoint(towards)!.Value;
            var d = tip - centre;
            var length = Math.Sqrt((d.X * d.X) + (d.Y * d.Y));
            to = at!.Value + new Avalonia.Vector(d.X / length * 40, d.Y / length * 40);
        }

        window.MouseMove(at!.Value);
        Assert.Equal(handle, viewport.ActiveAxis);
        window.MouseDown(at.Value, MouseButton.Left);
        Assert.Equal(handle, viewport.ActiveAxis);
        window.MouseMove(to);
        Assert.True(viewport.IsDragLabelVisible, "the live value shows while dragging");
        Assert.NotEqual(string.Empty, viewport.DragLabel);
        var preview = viewport.DragPreviewRoot;
        Assert.True(preview.HasValue, $"{handle} previews a transform");
        window.MouseUp(to, MouseButton.Left);
        Assert.Null(viewport.DragPreviewRoot);
        Assert.False(viewport.IsDragLabelVisible);
        window.MouseMove(new Point(5, 5)); // off the gizmo: nothing hovered, nothing dragged
        Assert.Equal(GizmoAxis.None, viewport.ActiveAxis);
        return preview!.Value;
    }
}

/// <summary>
/// Owner: the brush did nothing while the cursor was over empty ground. Its circle now always has a place (the ground
/// plane when nothing is hit, a point far ahead on it for the sky) and each dab of a stroke says where the last one was.
/// </summary>
public sealed class LevelViewportBrushTests
{
    [AvaloniaFact]
    public void TheBrushPaintsOnEmptyGroundAndTowardsTheSkyAndReportsTheWayItCame()
    {
        var viewport = new LevelViewport { BrushMode = true, BrushRadius = 5 };
        var window = new Window { Width = 800, Height = 600, Content = viewport };
        window.Show();
        HeadlessUi.Pump();
        viewport.Camera.Position = new Vector3(0f, 3000f, 0f);
        viewport.Camera.Pitch = -60f;
        var strokes = new List<(FVector From, FVector To)>();
        viewport.BrushPainted += (_, s) => strokes.Add(s);

        window.MouseDown(new Point(400, 300), MouseButton.Left);
        window.MouseMove(new Point(700, 320)); // a fast sweep
        window.MouseUp(new Point(700, 320), MouseButton.Left);
        window.MouseMove(new Point(100, 100)); // hovering: the circle follows, nothing is painted
        HeadlessUi.Pump();

        Assert.Equal(2, strokes.Count);
        Assert.Equal(strokes[0].To, strokes[0].From); // a stroke starts where it is
        Assert.Equal(strokes[0].To, strokes[1].From); // and sweeps on from its last dab
        Assert.True(FVector.Distance(OnGround(viewport, 400, 300), strokes[0].To) < 1f, strokes[0].To.ToString());
        Assert.True(FVector.Distance(OnGround(viewport, 700, 320), strokes[1].To) < 1f, strokes[1].To.ToString());

        // Towards the sky: on the ground plane, as far ahead as a big thing can be picked.
        viewport.Camera.Pitch = 30f;
        window.MouseDown(new Point(400, 300), MouseButton.Left);
        window.MouseUp(new Point(400, 300), MouseButton.Left);
        HeadlessUi.Pump();
        var sky = strokes[^1].To;
        var from = UeToGl.ToUePoint(viewport.Camera.ScreenRay(400, 300, 800, 600).Origin);
        Assert.Equal(3, strokes.Count);
        Assert.Equal(0f, sky.Z, 0.01f);
        Assert.Equal(viewport.MaxPickDistanceLarge, MathF.Sqrt(((sky.X - from.X) * (sky.X - from.X)) + ((sky.Y - from.Y) * (sky.Y - from.Y))), 1f);
        window.Close();
    }

    /// <summary>Paint mode journals a stroke when the button is let go: the end comes once, after the stroke's last dab.</summary>
    [AvaloniaFact]
    public void LettingGoEndsTheStrokeAfterItsLastDab()
    {
        var viewport = new LevelViewport { BrushMode = true, BrushRadius = 5 };
        var window = new Window { Width = 800, Height = 600, Content = viewport };
        window.Show();
        HeadlessUi.Pump();
        viewport.Camera.Position = new Vector3(0f, 3000f, 0f);
        viewport.Camera.Pitch = -60f;
        var events = new List<string>();
        viewport.BrushPainted += (_, _) => events.Add("dab");
        viewport.BrushStrokeEnded += (_, _) => events.Add("end");

        window.MouseDown(new Point(400, 300), MouseButton.Left);
        window.MouseMove(new Point(450, 300));
        window.MouseMove(new Point(500, 310));
        window.MouseUp(new Point(500, 310), MouseButton.Left);
        window.MouseMove(new Point(100, 100)); // hovering afterwards neither paints nor ends anything
        HeadlessUi.Pump();

        Assert.Equal(["dab", "dab", "dab", "end"], events);
        window.Close();
    }

    /// <summary>Where the mouse ray at pixel (<paramref name="x"/>, <paramref name="y"/>) meets the ground plane (UE height 0).</summary>
    private static FVector OnGround(LevelViewport viewport, int x, int y)
    {
        var (o, d) = viewport.Camera.ScreenRay(x, y, (int)Math.Round(viewport.Bounds.Width), (int)Math.Round(viewport.Bounds.Height));
        return UeToGl.ToUePoint(o + (d * (-o.Y / d.Y)));
    }
}
