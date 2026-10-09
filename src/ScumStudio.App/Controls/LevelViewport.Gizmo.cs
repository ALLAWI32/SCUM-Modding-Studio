using System.Globalization;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.App.Controls;

/// <summary>
/// The transform gizmo (owner: "like the FiveM map editors, Unity and Unreal"): arrows with cone heads move along an
/// axis, the squares between them move in a plane, the three rings of the globe turn about an axis, the yellow ring
/// turns about the view, the cubes scale (one axis each, the centre one all three). Geometry in
/// <see cref="GizmoOverlay"/>; here: hit testing on screen, the drags, the live value label and the cursor.
/// </summary>
public sealed partial class LevelViewport
{
    /// <summary>False hides the scale cubes: the selection cannot be scaled (a spawn part, a road piece, a bent object).</summary>
    public static readonly StyledProperty<bool> CanScaleProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(CanScale), defaultValue: true);

    /// <summary>The live value of the drag ("+2.50 m", "35°", "x1.20"), empty when nothing is dragged (read-only).</summary>
    public static readonly StyledProperty<string> DragLabelProperty =
        AvaloniaProperty.Register<LevelViewport, string>(nameof(DragLabel), string.Empty);

    /// <summary>Where the drag label sits: a margin from the viewport's top-left corner, next to the cursor (read-only).</summary>
    public static readonly StyledProperty<Thickness> DragLabelMarginProperty =
        AvaloniaProperty.Register<LevelViewport, Thickness>(nameof(DragLabelMargin));

    /// <summary>True while a gizmo drag shows its label (read-only).</summary>
    public static readonly StyledProperty<bool> IsDragLabelVisibleProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(IsDragLabelVisible));

    private const double PickRadius = 10; // pixels around a handle that still grab it
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private GizmoFrame _dragFrame;
    private Vector3 _dragStartHit;
    private float _dragLastAngle;
    private float _dragTurn; // degrees turned so far, unwrapped: a whole spin reads 360°
    private Point _dragStartScreen;
    private float _ringScreenSense; // 0: the turn is measured in the ring's plane; +1/-1: by the cursor's angle around the pivot on screen

    /// <inheritdoc cref="CanScaleProperty" />
    public bool CanScale
    {
        get => GetValue(CanScaleProperty);
        set => SetValue(CanScaleProperty, value);
    }

    /// <summary>The scale cubes show: the selection can be scaled and is not one of a multi-selection.</summary>
    private bool ShowsScale => CanScale && !InGroup();

    /// <inheritdoc cref="DragLabelProperty" />
    public string DragLabel
    {
        get => GetValue(DragLabelProperty);
        private set => SetValue(DragLabelProperty, value);
    }

    /// <inheritdoc cref="DragLabelMarginProperty" />
    public Thickness DragLabelMargin
    {
        get => GetValue(DragLabelMarginProperty);
        private set => SetValue(DragLabelMarginProperty, value);
    }

    /// <inheritdoc cref="IsDragLabelVisibleProperty" />
    public bool IsDragLabelVisible
    {
        get => GetValue(IsDragLabelVisibleProperty);
        private set => SetValue(IsDragLabelVisibleProperty, value);
    }

    /// <summary>The transform the current gizmo drag previews, for tests.</summary>
    internal FTransform? DragPreviewRoot => _dragPreview?.Root;

    /// <summary>The gizmo where the selection's root is at <paramref name="root"/>.</summary>
    private GizmoFrame Frame(FTransform root)
    {
        var origin = UeToGl.Point(PivotOf(root));
        return new GizmoFrame(origin, AxisGl(GizmoAxis.X, root), AxisGl(GizmoAxis.Y, root), AxisGl(GizmoAxis.Z, root),
            _camera.Position, _camera.Forward, GizmoMath.HandleLength(Vector3.Distance(_camera.Position, origin)));
    }

    /// <summary>
    /// A point on screen that picks <paramref name="handle"/> (its centre, the middle of its arrow, a point on its ring),
    /// or null when it is not shown or no such point exists; for tests.
    /// </summary>
    internal Point? HandleScreenPoint(GizmoAxis handle)
    {
        if (SelectedRootWorld is not { } root)
        {
            return null;
        }

        var frame = Frame(root);
        foreach (var shape in GizmoOverlay.PickShapes(frame, ShowsScale).Where(s => s.Handle == handle))
        {
            var candidates = new List<Vector3>();
            if (shape.Closed)
            {
                candidates.Add(shape.Points.Aggregate(Vector3.Zero, (a, p) => a + p) / shape.Points.Length);
            }

            for (var i = 1; i < shape.Points.Length; i++)
            {
                candidates.Add((shape.Points[i - 1] + shape.Points[i]) * 0.5f);
            }

            candidates.AddRange(shape.Points);
            foreach (var candidate in candidates)
            {
                if (ToScreen(candidate) is { } p && TryHitGizmo(p, out var hit) && hit == handle)
                {
                    return p;
                }
            }
        }

        return null;
    }

    /// <summary>The handle under <paramref name="position"/>: cubes before squares before arrows before rings, nearest first.</summary>
    private bool TryHitGizmo(Point position, out GizmoAxis handle)
    {
        handle = GizmoAxis.None;
        if (SelectedId == 0 || SelectedRootWorld is not { } root)
        {
            return false;
        }

        var frame = Frame(root);
        var bestPriority = int.MaxValue;
        var bestDistance = PickRadius;
        foreach (var shape in GizmoOverlay.PickShapes(frame, ShowsScale))
        {
            if (shape.Priority > bestPriority)
            {
                continue;
            }

            var screen = new Point[shape.Points.Length];
            var visible = true;
            for (var i = 0; i < shape.Points.Length && visible; i++)
            {
                if (ToScreen(shape.Points[i]) is { } p)
                {
                    screen[i] = p;
                }
                else
                {
                    visible = false; // a point behind the camera: this shape cannot be picked
                }
            }

            if (!visible)
            {
                continue;
            }

            var distance = shape.Points.Length == 1 ? Distance(position, screen[0]) : DistanceToPolyline(position, screen, shape.Closed);
            if (distance < bestDistance)
            {
                bestPriority = shape.Priority;
                bestDistance = distance;
                handle = shape.Handle;
            }
        }

        return handle != GizmoAxis.None;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>Distance from <paramref name="p"/> to a polyline on screen; zero inside a closed one (a convex square).</summary>
    private static double DistanceToPolyline(Point p, Point[] points, bool closed)
    {
        var best = double.PositiveInfinity;
        var n = closed ? points.Length : points.Length - 1;
        var inside = closed;
        var sign = 0;
        for (var i = 0; i < n; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Length];
            best = Math.Min(best, DistanceToSegment(p, a, b));
            if (closed)
            {
                var cross = ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));
                var s = double.IsNaN(cross) ? 0 : Math.Sign(cross); // Math.Sign throws on NaN
                if (s != 0 && sign != 0 && s != sign)
                {
                    inside = false;
                }

                sign = s != 0 ? s : sign;
            }
        }

        return inside && n > 2 ? 0 : best;
    }

    /// <summary>Starts dragging <paramref name="handle"/> from <paramref name="position"/>: remembers where the drag began on its axis, plane or ring.</summary>
    private void BeginGizmoDrag(GizmoAxis handle, Point position, FTransform root)
    {
        _dragFrame = Frame(root); // before the drag freezes the pivot
        _dragging = true;
        _dragAxis = handle;
        _dragStartRoot = root;
        _dragStartScreen = position;
        _dragStartParameter = 0f;
        _dragStartHit = _dragFrame.Origin;
        _dragLastAngle = _dragTurn = 0f;
        var (origin, direction) = ScreenRay(position);
        var f = _dragFrame;
        switch (GizmoMath.KindOf(handle))
        {
            case GizmoKind.Move:
            case GizmoKind.Scale when handle != GizmoAxis.ScaleUniform:
                GizmoMath.TryClosestParameter(origin, direction, f.Origin, f.Axis(GizmoMath.AxisOf(handle)), out _dragStartParameter, out _);
                break;
            case GizmoKind.Plane:
                GizmoMath.TryHitPlane(origin, direction, f.Origin, f.Axis(GizmoMath.AxisOf(handle)), out _dragStartHit);
                break;
            case GizmoKind.Rotate:
                // The view ring, and a ring seen nearly edge-on (its plane cannot be hit cleanly: a building's yaw ring from
                // a camera near the horizon), turn by the cursor's angle around the pivot on screen, like the old yaw ring;
                // clockwise is a positive turn about an axis that points at the camera. Decided once, at the press.
                var normal = handle == GizmoAxis.RotateView ? -f.CameraForward : f.Axis(GizmoMath.AxisOf(handle));
                _ringScreenSense = handle == GizmoAxis.RotateView || MathF.Abs(Vector3.Dot(direction, normal)) < 0.2f
                    ? (Vector3.Dot(normal, f.View) < 0f ? -1f : 1f)
                    : 0f;
                _dragLastAngle = RingAngle(handle, position) ?? 0f;
                break;
        }

        BeginAutoSnap(SelectedId, SelectedInstance);
        UpdateCursor();
    }

    /// <summary>The angle of the cursor around the ring of <paramref name="handle"/>, degrees; null when its plane is not under the cursor.</summary>
    private float? RingAngle(GizmoAxis handle, Point position)
    {
        var f = _dragFrame;
        if (_ringScreenSense != 0f)
        {
            return ToScreen(f.Origin) is { } centre ? ScreenAngle(position, centre) * _ringScreenSense : null;
        }

        var axis = GizmoMath.AxisOf(handle);
        var (origin, direction) = ScreenRay(position);
        if (!GizmoMath.TryHitPlane(origin, direction, f.Origin, f.Axis(axis), out var hit))
        {
            return null;
        }

        var (u, v) = f.RingBasis(axis);
        return GizmoMath.PlaneAngle(hit, f.Origin, u, v);
    }

    /// <summary>Follows the cursor with the handle being dragged: the preview transform and the label.</summary>
    private void UpdateGizmoDrag(Point position)
    {
        var (origin, direction) = ScreenRay(position);
        var f = _dragFrame;
        var start = _dragStartRoot;
        var translationSnap = _snapHeld ? TranslationSnap : 0f;
        FTransform? result = null;
        var label = string.Empty;
        switch (GizmoMath.KindOf(_dragAxis))
        {
            case GizmoKind.Move:
                // Along the arrow, as far as the mouse goes: no jump onto other objects' boxes here (Discord salvador: "the
                // movement happens in steps"); Ctrl snaps to the grid, a free drag still joins pieces.
                if (GizmoMath.TryClosestParameter(origin, direction, f.Origin, f.Axis(_dragAxis), out var t, out _))
                {
                    var delta = t - _dragStartParameter;
                    var axisUe = AxisUe(_dragAxis, start);
                    result = GizmoMath.Translate(start, axisUe, delta, translationSnap);
                    label = Metres(GizmoMath.Snap(delta, translationSnap));
                    if (PieceSnapAlong(result.Value, axisUe.GetSafeNormal(), null) is { } joined)
                    {
                        result = joined; // a piece reaching the same piece's end joins it (green box)
                    }
                }

                break;

            case GizmoKind.Plane:
                if (GizmoMath.TryHitPlane(origin, direction, f.Origin, f.Axis(GizmoMath.AxisOf(_dragAxis)), out var hit))
                {
                    var (a, b) = GizmoMath.PlaneAxes(_dragAxis);
                    var moved = hit - _dragStartHit;
                    var (da, db) = (Vector3.Dot(moved, f.Axis(a)), Vector3.Dot(moved, f.Axis(b)));
                    result = GizmoMath.TranslateInPlane(start, AxisUe(a, start), AxisUe(b, start), da, db, translationSnap);
                    label = Metres(GizmoMath.Snap(da, translationSnap)) + "  " + Metres(GizmoMath.Snap(db, translationSnap));
                    var normalUe = FVector.Cross(AxisUe(a, start), AxisUe(b, start)).GetSafeNormal();
                    if (PieceSnapAlong(result.Value, null, normalUe) is { } joined)
                    {
                        result = joined;
                    }
                }

                break;

            case GizmoKind.Rotate:
                if (RingAngle(_dragAxis, position) is { } angle)
                {
                    // The view ring turns about the axis towards the camera (a clockwise mouse turns the object clockwise);
                    // the others about their arrow's axis (the object's own with LocalAxes, else the world's), all about the pivot.
                    _dragTurn += GizmoMath.AngleDelta(_dragLastAngle, angle);
                    _dragLastAngle = angle;
                    var turn = GizmoMath.Snap(_dragTurn, _snapHeld ? RotationSnap : 0f);
                    var axisUe = _dragAxis == GizmoAxis.RotateView ? UeToGl.ToUeDirection(-f.CameraForward) : AxisUe(GizmoMath.AxisOf(_dragAxis), start);
                    result = AboutPivot(start, GizmoMath.RotateAbout(start, axisUe, turn));
                    label = turn.ToString("0.#", CultureInfo.InvariantCulture) + "°";
                }

                break;

            case GizmoKind.Scale:
                float ratio;
                if (_dragAxis == GizmoAxis.ScaleUniform)
                {
                    // The centre cube: right or up grows, left or down shrinks (150 px doubles).
                    ratio = 1f + (float)((position.X - _dragStartScreen.X) - (position.Y - _dragStartScreen.Y)) / 150f;
                }
                else if (GizmoMath.TryClosestParameter(origin, direction, f.Origin, f.Axis(GizmoMath.AxisOf(_dragAxis)), out var now, out _) && _dragStartParameter > 1e-3f)
                {
                    ratio = now / _dragStartParameter;
                }
                else
                {
                    break;
                }

                // Scale3D is the object's own: with world axes, the cube on a world axis stretches the object's axis closest to it.
                var cube = LocalAxes || _dragAxis == GizmoAxis.ScaleUniform ? _dragAxis : ClosestLocalCube(AxisUe(GizmoMath.AxisOf(_dragAxis), start), start);
                var scaled = GizmoMath.Scale(start, cube, ratio, _snapHeld ? 0.1f : 0f);
                result = AboutPivot(start, scaled);
                var shown = cube switch
                {
                    GizmoAxis.ScaleX => scaled.Scale3D.X / Safe(start.Scale3D.X),
                    GizmoAxis.ScaleY => scaled.Scale3D.Y / Safe(start.Scale3D.Y),
                    _ => scaled.Scale3D.Z / Safe(start.Scale3D.Z),
                };
                label = "x" + shown.ToString("0.00", CultureInfo.InvariantCulture);
                break;
        }

        if (result is { } preview)
        {
            _dragPreview = new DragPreview(SelectedId, preview, SelectedInstance);
            DragLabel = label;
            DragLabelMargin = new Thickness(position.X + 18, position.Y + 18, 0, 0);
            IsDragLabelVisible = label.Length > 0;
            RequestNextFrameRendering();
        }
    }

    private static float Safe(float scale) => MathF.Abs(scale) < 1e-6f ? 1e-6f : scale;

    /// <summary>The scale cube of the object's own axis that lies closest to <paramref name="world"/> (the dragged cube's arrow).</summary>
    private static GizmoAxis ClosestLocalCube(FVector world, FTransform root)
    {
        return new (GizmoAxis Cube, GizmoAxis Axis)[] { (GizmoAxis.ScaleX, GizmoAxis.X), (GizmoAxis.ScaleY, GizmoAxis.Y), (GizmoAxis.ScaleZ, GizmoAxis.Z) }
            .MaxBy(p => MathF.Abs(FVector.Dot(root.Rotation.RotateVector(GizmoMath.UeDirection(p.Axis)), world)))
            .Cube;
    }

    private static string Metres(float cm) => (cm / 100f).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " m";

    /// <summary>Ends the gizmo drag: the preview goes to the journal (with its scale for a cube drag), the label hides.</summary>
    private void EndGizmoDrag()
    {
        var scaled = GizmoMath.KindOf(_dragAxis) == GizmoKind.Scale;
        _dragging = false;
        _dragAxis = GizmoAxis.None;
        IsDragLabelVisible = false;
        DragLabel = string.Empty;
        CommitDragPreview(scaled);
        UpdateCursor();
    }

    /// <summary>Tracks the handle under the cursor (drawn yellow) and shows a hand over it.</summary>
    private void UpdateHover(Point position)
    {
        var hover = TryHitGizmo(position, out var handle) ? handle : GizmoAxis.None;
        if (hover != _hoverAxis)
        {
            _hoverAxis = hover;
            RequestNextFrameRendering();
        }

        UpdateCursor();
    }

    private void UpdateCursor()
    {
        if (!_cursorLocked)
        {
            Cursor = _dragging || _hoverAxis != GizmoAxis.None ? HandCursor : null;
        }
    }

    /// <summary>Draws the gizmo for the selection whose root is at <paramref name="root"/> (the drag preview's root while dragging).</summary>
    private void BuildGizmoOverlay(SceneRenderer renderer, FTransform? root)
    {
        renderer.Overlay.Clear();
        renderer.OverlayTriangles.Clear();
        if (root is not { } r || SelectedId == 0)
        {
            return;
        }

        GizmoOverlay.Draw(Frame(r), _hoverAxis, _dragAxis, _dragging, ShowsScale, renderer.Overlay, renderer.OverlayTriangles);
    }
}
