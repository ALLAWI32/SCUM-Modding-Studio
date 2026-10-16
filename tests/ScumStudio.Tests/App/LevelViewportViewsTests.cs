using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScumStudio.App.Controls;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// seviscache #5: Esc cancels a move, turn or scale like Blender. On the headless platform (no GL; the camera, the projection
/// and the hit test are all there): Esc during a gizmo, free or handle drag puts the object back, raises no
/// <see cref="LevelViewport.TransformDragged"/> (the one journaled op) and no handle release, and the mouse-up after it does nothing.
/// </summary>
public sealed class LevelViewportCancelTests
{
    private static readonly FTransform Start = new(new FRotator(0, 30, 0), new FVector(0, 0, 0), new FVector(1, 1, 1));

    [AvaloniaFact]
    public void EscapeDuringAGizmoDragPutsItBackAndJournalsNothing()
    {
        var (window, viewport, drags) = Show();
        var cancelled = 0;
        viewport.DragCancelled += (_, _) => cancelled++;
        foreach (var handle in new[] { GizmoAxis.X, GizmoAxis.PlaneXY, GizmoAxis.RotateZ, GizmoAxis.ScaleX })
        {
            var at = viewport.HandleScreenPoint(handle)!.Value;
            var to = at + new Avalonia.Vector(40, -30);
            window.MouseMove(at);
            window.MouseDown(at, MouseButton.Left);
            window.MouseMove(to);
            Assert.True(viewport.DragPreviewRoot.HasValue, $"{handle} previews a change");
            Assert.True(viewport.IsDragging);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Null(viewport.DragPreviewRoot); // drawn where it was again
            Assert.False(viewport.IsDragging);
            Assert.False(viewport.IsDragLabelVisible);

            window.MouseMove(to + new Avalonia.Vector(30, 10)); // still held: nothing follows
            Assert.Null(viewport.DragPreviewRoot);
            window.MouseUp(to + new Avalonia.Vector(30, 10), MouseButton.Left);
            window.MouseMove(new Point(5, 5));
        }

        Assert.Empty(drags);
        Assert.Equal(4, cancelled);

        // A drag after a cancelled one works as before: one journaled op.
        var x = viewport.HandleScreenPoint(GizmoAxis.X)!.Value;
        window.MouseMove(x);
        window.MouseDown(x, MouseButton.Left);
        window.MouseMove(x + new Avalonia.Vector(40, 0));
        window.MouseUp(x + new Avalonia.Vector(40, 0), MouseButton.Left);
        Assert.Single(drags);
        window.Close();
    }

    [AvaloniaFact]
    public void EscapeDuringAFreeDragPutsItBackAndJournalsNothing()
    {
        var (window, viewport, drags) = Show();
        var empty = new Point(80, 600); // off the gizmo: a press there grabs what the pick finds (here: the selection)
        window.MouseDown(empty, MouseButton.Left);
        viewport.GrabSelectionForTests();
        window.MouseMove(empty + new Avalonia.Vector(60, -20));
        Assert.True(viewport.DragPreviewRoot.HasValue, "the free drag previews a move");

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Null(viewport.DragPreviewRoot);
        window.MouseMove(empty + new Avalonia.Vector(120, -40)); // the button is still down: no new drag starts
        Assert.Null(viewport.DragPreviewRoot);
        window.MouseWheel(empty + new Avalonia.Vector(120, -40), new Avalonia.Vector(0, 1)); // the wheel no longer turns a held object
        Assert.Null(viewport.DragPreviewRoot);
        window.MouseUp(empty + new Avalonia.Vector(120, -40), MouseButton.Left);
        Assert.Empty(drags);

        // Without a drag, Esc is not taken (the page's own Esc, e.g. clearing "all of this kind", still runs).
        var taken = new List<bool>();
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => taken.Add(e.Handled), RoutingStrategies.Bubble, handledEventsToo: true);
        viewport.Focus();
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Assert.Equal([false], taken);
        window.Close();
    }

    [AvaloniaFact]
    public void EscapeDuringAHandleDragDropsTheShapeAndNeverReleases()
    {
        var (window, viewport, _) = Show();
        var foot = new FVector(0, 0, 0);
        viewport.LegHandle = foot;
        var events = new List<string>();
        viewport.LegDragged += (_, _) => events.Add("drag");
        viewport.ShapeHandleReleased += (_, _) => events.Add("released");
        viewport.ShapeDragCancelled += (_, _) => events.Add("cancelled");
        var at = ToScreen(viewport, UeToGl.Point(foot));

        window.MouseDown(at, MouseButton.Left);
        Assert.True(viewport.IsDragging);
        window.MouseMove(at + new Avalonia.Vector(0, 40));
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseMove(at + new Avalonia.Vector(0, 80));
        window.MouseUp(at + new Avalonia.Vector(0, 80), MouseButton.Left);

        Assert.Equal(["drag", "cancelled"], events);
        Assert.False(viewport.IsDragging);
        window.Close();
    }

    internal static Point ToScreen(LevelViewport viewport, Vector3 gl)
    {
        var (w, h) = (viewport.Bounds.Width, viewport.Bounds.Height);
        var clip = Vector4.Transform(new Vector4(gl, 1f), viewport.Camera.GetViewProjection((float)(w / h)));
        return new Point(((clip.X / clip.W) + 1) * 0.5 * w, (1 - (clip.Y / clip.W)) * 0.5 * h);
    }

    private static (Window Window, LevelViewport Viewport, List<TransformDragEventArgs> Drags) Show()
    {
        var viewport = new LevelViewport { SelectedId = 7, SelectedRootWorld = Start };
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
}

/// <summary>
/// seviscache #6 and #7: the numpad turns the view along an axis (by the physical key, so Num Lock off does not nudge,
/// drop to the ground or delete), and F3 / Shift+Z switch wireframe.
/// </summary>
public sealed class LevelViewportViewKeyTests
{
    private static readonly FTransform Root = new(FRotator.Zero, new FVector(2000, -1500, 300), FVector.One);

    [AvaloniaFact]
    public void NumpadSevenLooksDownOnTheSelectionAndCtrlFromBelow()
    {
        var (window, viewport, _) = Show();
        window.KeyPress(Key.NumPad7, RawInputModifiers.None, PhysicalKey.NumPad7, "7");
        Assert.Equal(-FlyCamera.MaxPitch, viewport.Camera.Pitch);
        AssertLooksAt(viewport, UeToGl.Point(Root.Translation));
        Assert.True(viewport.Camera.Position.Y > UeToGl.Point(Root.Translation).Y + 100f, "above it");

        window.KeyPress(Key.NumPad7, RawInputModifiers.Control, PhysicalKey.NumPad7, null);
        Assert.Equal(FlyCamera.MaxPitch, viewport.Camera.Pitch);
        AssertLooksAt(viewport, UeToGl.Point(Root.Translation));
        Assert.True(viewport.Camera.Position.Y < UeToGl.Point(Root.Translation).Y - 100f, "below it");
        window.Close();
    }

    [AvaloniaFact]
    public void WithNumLockOffTheNumpadChangesTheViewInsteadOfNudgingOrDeleting()
    {
        var (window, viewport, drags) = Show();
        var taken = new List<bool>();
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => taken.Add(e.Handled), RoutingStrategies.Bubble, handledEventsToo: true);

        window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.NumPad3, null); // Numpad 3 with Num Lock off
        Assert.Equal(90f, viewport.Camera.Yaw, 1e-3f);
        Assert.Equal(0f, viewport.Camera.Pitch, 1e-3f);
        AssertLooksAt(viewport, UeToGl.Point(Root.Translation));
        Assert.Empty(drags); // not the 1 cm nudge

        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.NumPad1, null); // Numpad 1: Front, not Fit to ground
        Assert.Equal(0f, viewport.Camera.Yaw, 1e-3f);
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.NumPadDecimal, null); // Numpad .: frame, never Delete
        window.KeyPress(Key.PageUp, RawInputModifiers.None, PhysicalKey.NumPad9, null); // Numpad 9: the other side
        Assert.Equal(180f, MathF.Abs(viewport.Camera.Yaw), 1e-3f);
        Assert.Equal([true, true, true, true], taken); // the page never sees them as End, Delete or PageDown

        // The PageDown key itself still nudges the selection down by 1 cm.
        window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
        var nudge = Assert.Single(drags);
        Assert.Equal(Root.Translation.Z - 1f, nudge.RootWorld.Translation.Z, 1e-3f);
        window.Close();
    }

    [AvaloniaFact]
    public void WithNothingSelectedTheViewTurnsAroundWhatIsInTheMiddle()
    {
        var (window, viewport, _) = Show();
        viewport.SelectedId = 0;
        viewport.SelectedRootWorld = null;
        viewport.Camera.Position = new Vector3(0f, 3000f, 0f);
        viewport.Camera.Yaw = 30f;
        viewport.Camera.Pitch = -45f;
        var (origin, direction) = (viewport.Camera.Position, viewport.Camera.Forward);
        var ground = origin + (direction * (-origin.Y / direction.Y)); // the ground plane under the middle of the screen
        var distance = Vector3.Distance(origin, ground);

        window.KeyPress(Key.NumPad1, RawInputModifiers.None, PhysicalKey.NumPad1, "1");
        Assert.Equal(0f, viewport.Camera.Yaw, 1e-3f);
        Assert.Equal(0f, viewport.Camera.Pitch, 1e-3f);
        AssertLooksAt(viewport, ground);
        Assert.Equal(distance, Vector3.Distance(viewport.Camera.Position, ground), 1f);
        window.Close();
    }

    [AvaloniaFact]
    public void FThreeAndShiftZSwitchWireframe()
    {
        var (window, viewport, _) = Show();
        Assert.False(viewport.Wireframe);
        window.KeyPress(Key.F3, RawInputModifiers.None, PhysicalKey.F3, null);
        Assert.True(viewport.Wireframe);
        window.KeyPress(Key.Z, RawInputModifiers.Shift, PhysicalKey.Z, "Z");
        Assert.False(viewport.Wireframe);
        window.KeyPress(Key.Z, RawInputModifiers.None, PhysicalKey.Z, "z"); // Z alone is no switch
        Assert.False(viewport.Wireframe);
        window.Close();
    }

    private static void AssertLooksAt(LevelViewport viewport, Vector3 target)
    {
        var camera = viewport.Camera;
        var along = Vector3.Dot(target - camera.Position, camera.Forward);
        Assert.True(along > 0f, "in front");
        var off = Vector3.Distance(camera.Position + (camera.Forward * along), target);
        Assert.True(off < 1f, $"{target} is {off} cm off the middle of the view");
    }

    private static (Window Window, LevelViewport Viewport, List<TransformDragEventArgs> Drags) Show()
    {
        var viewport = new LevelViewport { SelectedId = 7, SelectedRootWorld = Root };
        var window = new Window { Width = 900, Height = 700, Content = viewport };
        window.Show();
        HeadlessUi.Pump();
        viewport.Focus();
        viewport.Camera.Position = new Vector3(600f, 500f, 900f);
        viewport.Camera.Yaw = -124f;
        viewport.Camera.Pitch = -25f;
        var drags = new List<TransformDragEventArgs>();
        viewport.TransformDragged += (_, e) => drags.Add(e);
        return (window, viewport, drags);
    }
}
