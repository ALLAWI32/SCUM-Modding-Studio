using System.Numerics;
using Avalonia;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Rendering;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Viewport;

namespace ScumStudio.App.Controls;

/// <summary>Arguments of <see cref="LevelViewport.ShapeEndDragged"/>.</summary>
/// <param name="AtEnd">The end (true) or the start of the piece.</param>
/// <param name="Value">The end's new edit.</param>
/// <param name="Welded">True when it was welded onto another piece's end (the push next to that end is then zero).</param>
public sealed record ShapeEndDragEventArgs(bool AtEnd, SplineEnd Value, bool Welded);

/// <summary>
/// The Shape handles drawn on the selection (owner: "grab the road's corners and widen or narrow it, make the bridge
/// longer or shorter, weld it to the rest of the bridge; make any object bigger or longer by grabbing it"). Spline
/// pieces (bendable objects, road and bridge pieces) get two push handles, an end handle on each end (drag: longer,
/// shorter, anywhere; Shift: up and down; near another piece's end it welds, Alt lets go) and a handle on each corner of
/// each end (drag sideways: that side wider or narrower); an up-down arrow on each push handle raises or lowers the middle
/// (a hump, a dip, a wave) and one on each end handle raises or lowers that end (a ramp). Other single objects get an end handle on each end of their
/// length (drag: longer or shorter, the other end stays) and one on a top corner (drag: bigger or smaller, standing where
/// it stands).
/// </summary>
public sealed partial class LevelViewport
{
    /// <summary>The foot of the selected object, where the legs arrow hangs (null: none).</summary>
    public static readonly StyledProperty<FVector?> LegHandleProperty =
        AvaloniaProperty.Register<LevelViewport, FVector?>(nameof(LegHandle));

    /// <summary>Selected object's scale handles (null: none).</summary>
    public static readonly StyledProperty<ScaleHandleInfo?> ScaleHandlesProperty =
        AvaloniaProperty.Register<LevelViewport, ScaleHandleInfo?>(nameof(ScaleHandles));

    // Spline handles: 0-1 push the curve sideways, 2-3 move the start / end, 4-7 the corners (start left, start right, end left,
    // end right), 8-11 the up-down arrows over handles 0-3.
    private const int EndHandle = 2;
    private const int CornerHandle = 4;
    private const int ArrowHandle = 8;
    private const float WeldReach = 300f; // cm: an end dragged this close to another piece's end welds onto it
    private const float WeldKeep = 50f; // cm: the end it already touched lets go sooner, so it can be made a little longer or shorter

    private static readonly Vector4 CurveColor = new(1f, 0.62f, 0.2f, 1f);
    private static readonly Vector4 PushColor = new(0.35f, 0.9f, 1f, 1f);
    private static readonly Vector4 EndColor = new(0.4f, 1f, 0.5f, 1f);
    private static readonly Vector4 CornerColor = new(1f, 0.25f, 0.3f, 1f); // bright colours only: white got lost on concrete and sky
    private static readonly Vector4 ActiveColor = new(1f, 1f, 0.3f, 1f);

    private int _shapeDragIndex = -1;
    private SplineEnd _endDragStart;
    private FVector _endDragAnchor;
    private Vector3 _endDragHit;
    private float _endDragLift;
    private bool _endDragVertical;
    private List<(EndSection End, bool Touching)> _weldTargets = [];
    private Vector3 _arrowBase;
    private float _arrowFrom;
    private EndSection? _weldedTo;

    private int _scaleDragIndex = -1;
    private FTransform _scaleStart;
    private FVector _scaleFixed;
    private FVector _scaleFixedLocal;
    private float _scaleFrom;

    /// <summary>Raised while an end or corner handle is dragged: the end's new edit.</summary>
    public event EventHandler<ShapeEndDragEventArgs>? ShapeEndDragged;

    /// <summary>Raised while the legs arrow is dragged: how far below where the drag began the cursor is, cm.</summary>
    public event EventHandler<float>? LegDragged;

    /// <inheritdoc cref="LegHandleProperty" />
    public FVector? LegHandle
    {
        get => GetValue(LegHandleProperty);
        set => SetValue(LegHandleProperty, value);
    }

    private bool _legDragging;

    /// <summary>The tip of the legs arrow under the foot (a steady size on screen).</summary>
    private FVector LegTip(FVector foot) =>
        foot - (FVector.Up * (GizmoMath.HandleLength(Vector3.Distance(_camera.Position, UeToGl.Point(foot))) * 0.5f));

    /// <summary>Raised while a scale handle is dragged: the object's new world transform (scale and place).</summary>
    public event EventHandler<FTransform>? ScaleHandleDragged;

    /// <inheritdoc cref="ScaleHandlesProperty" />
    public ScaleHandleInfo? ScaleHandles
    {
        get => GetValue(ScaleHandlesProperty);
        set => SetValue(ScaleHandlesProperty, value);
    }

    private bool IsHandleDragging => _shapeDragIndex >= 0 || _scaleDragIndex >= 0 || _legDragging;

    /// <summary>World (UE) places of the spline handles: the two pushes, then (with mesh bounds) the two ends and four corners.</summary>
    private static FVector[] HandleWorld(ShapeHandleInfo info)
    {
        var shaped = info.Shaped;
        var (a, b) = SplineSway.Controls(shaped);
        FVector W(FVector p) => info.ComponentWorld.TransformPosition(p);
        if (info.MeshBounds.IsEmpty)
        {
            return [W(a), W(b)];
        }

        var start = SplineEnds.Section(shaped, info.MeshBounds, atEnd: false);
        var end = SplineEnds.Section(shaped, info.MeshBounds, atEnd: true);
        return [W(a), W(b), W(start.Middle), W(end.Middle), W(start.Left), W(start.Right), W(end.Left), W(end.Right)];
    }

    /// <summary>Every spline handle with its index: <see cref="HandleWorld"/> and the up-down arrows' tips over the push and end handles.</summary>
    private List<(int Index, FVector At)> AllHandles(ShapeHandleInfo info)
    {
        var handles = HandleWorld(info).Select((p, i) => (i, p)).ToList();
        foreach (var (i, p) in handles.Where(h => h.i < CornerHandle).ToList())
        {
            handles.Add((ArrowHandle + i, ArrowTip(p)));
        }

        return handles;
    }

    /// <summary>The tip of the up-down arrow over a handle (a steady size on screen).</summary>
    private FVector ArrowTip(FVector handle) =>
        handle + (FVector.Up * (GizmoMath.HandleLength(Vector3.Distance(_camera.Position, UeToGl.Point(handle))) * 0.45f));

    /// <summary>World (UE) places of the scale handles: the start and end of the object's length, then a top corner.</summary>
    private static FVector[] ScaleHandleWorld(ScaleHandleInfo info)
    {
        var (start, end, alongY) = PieceSnap.Ends(info.Bounds);
        var b = info.Bounds;
        var corner = alongY ? new FVector(b.Max.X, b.Max.Y, b.Max.Z) : new FVector(b.Max.X, b.Min.Y, b.Max.Z);
        return [info.World.TransformPosition(start), info.World.TransformPosition(end), info.World.TransformPosition(corner)];
    }

    /// <summary>The curve (orange, end to end), the end edges, and the handles (the dragged one bright).</summary>
    private void DrawShapeHandles(SceneRenderer renderer)
    {
        if (LegHandle is { } foot)
        {
            // The legs arrow points down from the foot: drag it down and the foot goes deeper, the top stays.
            var top = UeToGl.Point(foot);
            var tip = UeToGl.Point(LegTip(foot));
            var color = _legDragging ? ActiveColor : CurveColor;
            var head = (top.Y - tip.Y) * 0.25f;
            var side = Vector3.Cross(Vector3.UnitY, Vector3.Normalize(_camera.Position - tip));
            side = side.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(side);
            renderer.Overlay.Add(new OverlayLine(top, tip, color));
            renderer.Overlay.Add(new OverlayLine(tip, tip + (Vector3.UnitY * head) + (side * head * 0.7f), color));
            renderer.Overlay.Add(new OverlayLine(tip, tip + (Vector3.UnitY * head) - (side * head * 0.7f), color));
            DrawSquare(renderer, top, color, 0.05f);
        }

        if (ScaleHandles is { } scale && ShapeHandles is null)
        {
            var points = ScaleHandleWorld(scale);
            renderer.Overlay.Add(new OverlayLine(UeToGl.Point(points[0]), UeToGl.Point(points[1]), EndColor));
            for (var h = 0; h < points.Length; h++)
            {
                DrawSquare(renderer, UeToGl.Point(points[h]), _scaleDragIndex == h ? ActiveColor : h < 2 ? EndColor : CornerColor, 0.07f);
            }
        }

        if (ShapeHandles is not { } info)
        {
            return;
        }

        var shaped = info.Shaped;
        var world = HandleWorld(info);
        var gl = world.Select(p => UeToGl.Point(p)).ToArray();
        var startGl = UeToGl.Point(info.ComponentWorld.TransformPosition(shaped.StartPos));
        var endGl = UeToGl.Point(info.ComponentWorld.TransformPosition(shaped.EndPos));

        // A stem only to a pushed handle: an unpushed one lies on the curve, which stays orange end to end.
        var stem = new Vector4(0.55f, 0.85f, 1f, 0.8f);
        if (MathF.Abs(info.Sway1) >= 1f)
        {
            renderer.Overlay.Add(new OverlayLine(startGl, gl[0], stem));
        }

        if (MathF.Abs(info.Sway2) >= 1f)
        {
            renderer.Overlay.Add(new OverlayLine(endGl, gl[1], stem));
        }

        if (gl.Length > EndHandle)
        {
            renderer.Overlay.Add(new OverlayLine(gl[CornerHandle], gl[CornerHandle + 1], CornerColor));
            renderer.Overlay.Add(new OverlayLine(gl[CornerHandle + 2], gl[CornerHandle + 3], CornerColor));
        }

        var previous = startGl;
        for (var i = 1; i <= 40; i++)
        {
            var at = SplineMeshDeformer.SplineEvalPos(shaped.StartPos, shaped.StartTangent, shaped.EndPos, shaped.EndTangent, i / 40f);
            var point = UeToGl.Point(info.ComponentWorld.TransformPosition(at));
            renderer.Overlay.Add(new OverlayLine(previous, point, CurveColor));
            previous = point;
        }

        for (var h = 0; h < gl.Length; h++)
        {
            var active = _shapeDragIndex == h;
            if (h < EndHandle)
            {
                DrawDiamond(renderer, gl[h], active ? ActiveColor : PushColor);
            }
            else
            {
                DrawSquare(renderer, gl[h], active ? ActiveColor : h < CornerHandle ? EndColor : CornerColor, h < CornerHandle ? 0.07f : 0.05f);
            }

            if (h < CornerHandle)
            {
                DrawArrow(renderer, gl[h], UeToGl.Point(ArrowTip(world[h])), _shapeDragIndex == ArrowHandle + h ? ActiveColor : h < EndHandle ? PushColor : EndColor);
            }
        }

        if (_weldedTo is { } weld)
        {
            // Welded: the other piece's end glows green.
            var a = UeToGl.Point(weld.Left);
            var b = UeToGl.Point(weld.Right);
            var lift = new Vector3(0f, 15f, 0f);
            renderer.Overlay.Add(new OverlayLine(a, b, EndColor));
            renderer.Overlay.Add(new OverlayLine(a + lift, b + lift, EndColor));
        }
    }

    private void DrawDiamond(SceneRenderer renderer, Vector3 centre, Vector4 color)
    {
        var size = GizmoMath.HandleLength(Vector3.Distance(_camera.Position, centre)) * 0.08f;
        Vector3[] diamond = [centre + new Vector3(size, 0, 0), centre + new Vector3(0, 0, size), centre - new Vector3(size, 0, 0), centre - new Vector3(0, 0, size)];
        for (var i = 0; i < 4; i++)
        {
            renderer.Overlay.Add(new OverlayLine(diamond[i], diamond[(i + 1) % 4], color));
        }

        renderer.Overlay.Add(new OverlayLine(centre - new Vector3(0, size, 0), centre + new Vector3(0, size, 0), color));
    }

    /// <summary>An up-down arrow from a handle to <paramref name="tip"/>: heads at both ends of its upper part.</summary>
    private void DrawArrow(SceneRenderer renderer, Vector3 handle, Vector3 tip, Vector4 color)
    {
        var length = tip.Y - handle.Y;
        var head = length * 0.18f;
        var side = Vector3.Cross(Vector3.UnitY, Vector3.Normalize(_camera.Position - tip));
        side = side.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(side);
        var low = handle + new Vector3(0f, length * 0.35f, 0f);
        renderer.Overlay.Add(new OverlayLine(low, tip, color));
        renderer.Overlay.Add(new OverlayLine(tip, tip - (Vector3.UnitY * head) + (side * head * 0.7f), color));
        renderer.Overlay.Add(new OverlayLine(tip, tip - (Vector3.UnitY * head) - (side * head * 0.7f), color));
        renderer.Overlay.Add(new OverlayLine(low, low + (Vector3.UnitY * head) + (side * head * 0.7f), color));
        renderer.Overlay.Add(new OverlayLine(low, low + (Vector3.UnitY * head) - (side * head * 0.7f), color));
    }

    private void DrawSquare(SceneRenderer renderer, Vector3 centre, Vector4 color, float relativeSize)
    {
        var size = GizmoMath.HandleLength(Vector3.Distance(_camera.Position, centre)) * relativeSize;
        Vector3[] square = [centre + new Vector3(size, 0, size), centre + new Vector3(-size, 0, size), centre + new Vector3(-size, 0, -size), centre + new Vector3(size, 0, -size)];
        for (var i = 0; i < 4; i++)
        {
            renderer.Overlay.Add(new OverlayLine(square[i], square[(i + 1) % 4], color));
            renderer.Overlay.Add(new OverlayLine(square[i], square[i] + new Vector3(0, size * 0.6f, 0), color));
        }
    }

    /// <summary>The handle within 12 px of the cursor, or null.</summary>
    private static int? NearestHandle(IEnumerable<(int Index, FVector At)> handles, Func<Vector3, Point?> toScreen, Point position)
    {
        int? best = null;
        var bestDistance = 12.0;
        foreach (var (index, handle) in handles)
        {
            if (toScreen(UeToGl.Point(handle)) is { } p)
            {
                var d = Math.Sqrt(((p.X - position.X) * (p.X - position.X)) + ((p.Y - position.Y) * (p.Y - position.Y)));
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = index;
                }
            }
        }

        return best;
    }

    /// <summary>Starts dragging a Shape or scale handle under the cursor; false when there is none.</summary>
    private bool TryBeginHandleDrag(Point position, bool shift)
    {
        if (LegHandle is { } foot && NearestHandle([(0, LegTip(foot)), (1, foot)], ToScreen, position) is not null)
        {
            _legDragging = true;
            _arrowBase = UeToGl.Point(foot);
            var (rayOrigin, rayDirection) = ScreenRay(position);
            _arrowFrom = GizmoMath.TryClosestParameter(rayOrigin, rayDirection, _arrowBase, Vector3.UnitY, out var down, out _) ? down : 0f;
            return true;
        }

        if (ShapeHandles is { } info && NearestHandle(AllHandles(info), ToScreen, position) is { } handle)
        {
            _shapeDragIndex = handle;
            _weldedTo = null;
            if (handle >= ArrowHandle)
            {
                // Up and down along the vertical through the handle under the arrow.
                _arrowBase = UeToGl.Point(HandleWorld(info)[handle - ArrowHandle]);
                var (rayOrigin, rayDirection) = ScreenRay(position);
                _arrowFrom = GizmoMath.TryClosestParameter(rayOrigin, rayDirection, _arrowBase, Vector3.UnitY, out var up, out _) ? up : 0f;
                _endDragStart = handle is ArrowHandle or ArrowHandle + EndHandle ? info.Start : info.End;
            }
            else if (handle is EndHandle or EndHandle + 1)
            {
                var atEnd = handle == EndHandle + 1;
                _endDragStart = atEnd ? info.End : info.Start;
                _endDragAnchor = HandleWorld(info)[handle];
                var anchorGl = UeToGl.Point(_endDragAnchor);
                var (origin, direction) = ScreenRay(position);
                _endDragHit = GizmoMath.HitHorizontalPlane(origin, direction, anchorGl.Y, _camera.FarPlane) ?? anchorGl;
                _endDragVertical = shift;
                _endDragLift = GizmoMath.TryClosestParameter(origin, direction, anchorGl, Vector3.UnitY, out var t, out _) ? t : 0f;
                _weldTargets = WeldTargets(_endDragAnchor);
            }

            return true;
        }

        if (ShapeHandles is null && ScaleHandles is { } scale && NearestHandle(ScaleHandleWorld(scale).Select((p, i) => (i, p)), ToScreen, position) is { } s)
        {
            _scaleDragIndex = s;
            _scaleStart = scale.World;
            var (start, end, _) = PieceSnap.Ends(scale.Bounds);
            var c = scale.Bounds.Center;
            // The other end stays (longer / shorter), or the bottom middle (bigger / smaller).
            _scaleFixedLocal = s switch { 0 => end, 1 => start, _ => new FVector(c.X, c.Y, scale.Bounds.Min.Z) };
            _scaleFixed = scale.World.TransformPosition(_scaleFixedLocal);
            var handlePoint = ScaleHandleWorld(scale)[s];
            _scaleFrom = s < 2 ? FVector.Distance(handlePoint, _scaleFixed) : (handlePoint - _scaleFixed).Size2D();
            return true;
        }

        return false;
    }

    /// <summary>Follows the cursor with the handle being dragged.</summary>
    private void DragHandle(Point position)
    {
        var (origin, direction) = ScreenRay(position);
        if (_legDragging)
        {
            if (GizmoMath.TryClosestParameter(origin, direction, _arrowBase, Vector3.UnitY, out var along, out _))
            {
                LegDragged?.Invoke(this, _arrowFrom - along);
            }

            return;
        }

        if (_scaleDragIndex >= 0)
        {
            DragScaleHandle(origin, direction);
            return;
        }

        if (ShapeHandles is not { } info)
        {
            return;
        }

        var index = _shapeDragIndex;
        if (index < EndHandle)
        {
            if (ShapeSwayAt(position, index) is { } sway)
            {
                ShapeHandleDragged?.Invoke(this, (index, sway));
            }

            return;
        }

        if (index >= ArrowHandle)
        {
            if (!GizmoMath.TryClosestParameter(origin, direction, _arrowBase, Vector3.UnitY, out var up, out _))
            {
                return;
            }

            var rise = up - _arrowFrom; // GL units are cm, up is UE Z
            var lastEnd = index is ArrowHandle + 1 or ArrowHandle + EndHandle + 1;
            var raised = index < ArrowHandle + EndHandle
                ? _endDragStart with { Lift = _endDragStart.Lift + rise }
                : _endDragStart with { Move = _endDragStart.Move + info.ComponentWorld.InverseTransformVector(FVector.Up * rise) };
            ShapeEndDragged?.Invoke(this, new ShapeEndDragEventArgs(lastEnd, raised, false));
            return;
        }

        var atEnd = index == EndHandle + 1 || index >= CornerHandle + 2;
        if (index >= CornerHandle)
        {
            var corner = UeToGl.Point(HandleWorld(info)[index]);
            if (GizmoMath.HitHorizontalPlane(origin, direction, corner.Y, _camera.FarPlane) is { } hit)
            {
                var local = info.ComponentWorld.InverseTransformPosition(UeToGl.ToUePoint(hit));
                var value = SplineEnds.DragCorner(info.Shaped, info.Base, atEnd ? info.End : info.Start, info.MeshBounds, atEnd, rightSide: (index - CornerHandle) % 2 == 1, local);
                ShapeEndDragged?.Invoke(this, new ShapeEndDragEventArgs(atEnd, value, false));
            }

            return;
        }

        var anchorGl = UeToGl.Point(_endDragAnchor);
        Vector3 moveGl;
        if (_endDragVertical)
        {
            if (!GizmoMath.TryClosestParameter(origin, direction, anchorGl, Vector3.UnitY, out var t, out _))
            {
                return;
            }

            moveGl = Vector3.UnitY * (t - _endDragLift);
        }
        else if (GizmoMath.HitHorizontalPlane(origin, direction, anchorGl.Y, _camera.FarPlane) is { } hit)
        {
            moveGl = hit - _endDragHit;
        }
        else
        {
            return;
        }

        var moved = _endDragAnchor + UeToGl.ToUeDirection(moveGl);
        var shift = info.ComponentWorld.InverseTransformPosition(moved) - info.ComponentWorld.InverseTransformPosition(_endDragAnchor);
        var edit = _endDragStart with { Move = _endDragStart.Move + shift };
        _weldedTo = null;
        if (!_altHeld && NearestWeld(moved) is { } target)
        {
            var c = info.ComponentWorld;
            var local = new EndSection(c.InverseTransformPosition(target.Middle), c.InverseTransformPosition(target.Left), c.InverseTransformPosition(target.Right),
                c.InverseTransformVector(target.Outward).GetSafeNormal(), target.Width);
            edit = SplineEnds.Weld(info.Base, atEnd ? info.Start : edit, atEnd ? edit : info.End, info.MeshBounds, atEnd, local);
            _weldedTo = target;
        }

        ShapeEndDragged?.Invoke(this, new ShapeEndDragEventArgs(atEnd, edit, _weldedTo is not null));
    }

    /// <summary>Longer or shorter along the object's length (its other end stays), or bigger or smaller (its foot stays).</summary>
    private void DragScaleHandle(Vector3 origin, Vector3 direction)
    {
        if (ScaleHandles is not { } info)
        {
            return;
        }

        var handle = ScaleHandleWorld(info with { World = _scaleStart })[_scaleDragIndex];
        if (GizmoMath.HitHorizontalPlane(origin, direction, UeToGl.Point(handle).Y, _camera.FarPlane) is not { } hit || _scaleFrom < 1f)
        {
            return;
        }

        var point = UeToGl.ToUePoint(hit);
        var scale = _scaleStart.Scale3D;
        if (_scaleDragIndex < 2)
        {
            var axis = (handle - _scaleFixed).GetSafeNormal();
            var factor = Math.Clamp(FVector.Dot(point - _scaleFixed, axis) / _scaleFrom, 0.05f, 50f);
            var alongY = info.Bounds.Size.Y * MathF.Abs(scale.Y) > info.Bounds.Size.X * MathF.Abs(scale.X);
            scale = alongY ? scale with { Y = scale.Y * factor } : scale with { X = scale.X * factor };
        }
        else
        {
            scale *= Math.Clamp((point - _scaleFixed).Size2D() / _scaleFrom, 0.05f, 50f);
        }

        var world = _scaleStart with { Scale3D = scale };
        world = world with { Translation = _scaleFixed - world.Rotation.RotateVector(scale * _scaleFixedLocal) };
        ScaleHandleDragged?.Invoke(this, world);
    }

    /// <summary>Ends a handle drag (the edit is journaled through <see cref="ShapeHandleReleased"/>).</summary>
    private void EndHandleDrag()
    {
        _shapeDragIndex = _scaleDragIndex = -1;
        _legDragging = false;
        _weldedTo = null;
        _weldTargets = [];
        ShapeHandleReleased?.Invoke(this, EventArgs.Empty);
        RequestNextFrameRendering();
    }

    /// <summary>
    /// The push (cm, positive right) that puts handle <paramref name="index"/> under the cursor: the cursor's ray meets the
    /// horizontal plane through the handle, and that point's sideways distance from the handle's unpushed place counts.
    /// </summary>
    private float? ShapeSwayAt(Point position, int index)
    {
        if (ShapeHandles is not { } info)
        {
            return null;
        }

        var handle = HandleWorld(info)[index];
        var (origin, direction) = ScreenRay(position);
        if (GizmoMath.HitHorizontalPlane(origin, direction, UeToGl.Point(handle).Y, 1_000_000f) is not { } hit)
        {
            return null;
        }

        var unpushed = info.Unpushed;
        var local = info.ComponentWorld.InverseTransformPosition(UeToGl.ToUePoint(hit));
        var (baseFirst, baseSecond) = SplineSway.Controls(unpushed);
        return FVector.Dot(local - (index == 0 ? baseFirst : baseSecond), SplineSway.Right(unpushed));
    }

    /// <summary>The weld target nearest to <paramref name="point"/> within reach, or null.</summary>
    private EndSection? NearestWeld(FVector point)
    {
        EndSection? best = null;
        var bestDistance = float.MaxValue;
        foreach (var (end, touching) in _weldTargets)
        {
            var d = FVector.Distance(end.Middle, point);
            if (d < (touching ? WeldKeep : WeldReach) && d < bestDistance)
            {
                bestDistance = d;
                best = end;
            }
        }

        return best;
    }

    /// <summary>
    /// The ends within 100 m that a dragged end can weld onto: road and bridge pieces, bent objects and straight pieces
    /// (world), each marked when it touches the dragged end already.
    /// </summary>
    private List<(EndSection End, bool Touching)> WeldTargets(FVector near)
    {
        var targets = new List<(EndSection, bool)>();
        if (_level is null)
        {
            return targets;
        }

        foreach (var node in _level.Scene.Nodes)
        {
            if (node.Mesh is null || !node.IsEffectivelyVisible || node.SelectableId == 0)
            {
                continue;
            }

            var placement = node.Tag as ScenePlacement;
            if (node.SelectableId == SelectedId && (SelectedInstance is not { } selected || placement?.InstanceKey == selected))
            {
                continue; // the piece being shaped
            }

            var ends = SplineEndsOf(node, placement) ?? (PieceOf(node) is { } piece ? PieceSnap.EndSections(piece) : []);
            foreach (var end in ends)
            {
                var distance = FVector.Distance(end.Middle, near);
                if (distance < 10_000f)
                {
                    targets.Add((end, distance < 10f));
                }
            }
        }

        return targets;
    }

    /// <summary>The two ends (world) of a road, rail or bridge piece or a bent object; null for anything else.</summary>
    private EndSection[]? SplineEndsOf(SceneNode node, ScenePlacement? placement)
    {
        SplineMeshParams spline;
        FTransform world;
        BoundingBox bounds;
        if (placement is { InstanceKey: { InstanceIndex: InstanceKey.Segment } key, Component.SplineMesh: { } original })
        {
            var marker = placement.MeshPath.IndexOf(SplineMeshPlacements.KeyMarker, StringComparison.Ordinal);
            if (marker <= 0 || _level?.Prepared.SplineSources.TryGetValue(placement.MeshPath[..marker], out var straight) != true)
            {
                return null;
            }

            spline = SegmentBends?.GetValueOrDefault(key) ?? original;
            world = placement.World;
            bounds = straight!.Mesh.Bounds;
        }
        else if (Bends?.GetValueOrDefault(node.SelectableId) is { } bent)
        {
            var clone = Clones?.FirstOrDefault(c => c.Id == node.SelectableId && c.MeshPath is not null);
            var mesh = placement?.MeshPath ?? clone?.MeshPath;
            if (mesh is null || MeshBoundsOf(mesh) is not { } b)
            {
                return null;
            }

            if (bent.Count == 0)
            {
                return null;
            }

            world = (placement is not null ? NodeWorld(placement) : clone!.RootWorld) with { Scale3D = FVector.One };
            EndSection Moved(EndSection s) =>
                new(world.TransformPosition(s.Middle), world.TransformPosition(s.Left), world.TransformPosition(s.Right), world.TransformVectorNoScale(s.Outward).GetSafeNormal(), s.Width);
            return [Moved(SplineEnds.Section(bent[0], b, atEnd: false)), Moved(SplineEnds.Section(bent[^1], b, atEnd: true))];
        }
        else
        {
            return null;
        }

        EndSection ToWorld(EndSection s) =>
            new(world.TransformPosition(s.Middle), world.TransformPosition(s.Left), world.TransformPosition(s.Right), world.TransformVectorNoScale(s.Outward).GetSafeNormal(), s.Width);
        return [ToWorld(SplineEnds.Section(spline, bounds, atEnd: false)), ToWorld(SplineEnds.Section(spline, bounds, atEnd: true))];
    }

    /// <summary>Where a placement is drawn now (moved instance, moved actor, or as the level has it).</summary>
    private FTransform NodeWorld(ScenePlacement placement) =>
        placement.InstanceKey is { } key && InstanceTransforms?.TryGetValue(key, out var movedInstance) == true ? movedInstance
        : ActorTransforms?.TryGetValue(placement.SelectableId, out var root) == true ? placement.World.GetRelativeTransform(placement.Actor.WorldTransform) * root
        : placement.World;

    /// <summary>Bounds of a mesh the scene has (prepared with it or loaded later).</summary>
    private BoundingBox? MeshBoundsOf(string mesh) =>
        _level?.Prepared.Meshes.TryGetValue(mesh, out var asset) == true ? asset.Mesh.Bounds
        : ExtraMeshes?.FirstOrDefault(m => string.Equals(m.Asset.MeshPath, mesh, StringComparison.OrdinalIgnoreCase))?.Asset.Mesh.Bounds;
}
