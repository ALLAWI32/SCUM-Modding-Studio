using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Viewport;

/// <summary>Axis handle of the translation gizmo (UE axes: X forward, Y right, Z up).</summary>
public enum GizmoAxis
{
    /// <summary>No handle.</summary>
    None,

    /// <summary>UE X (red).</summary>
    X,

    /// <summary>UE Y (green).</summary>
    Y,

    /// <summary>UE Z (blue).</summary>
    Z,

    /// <summary>Rotation around UE Z (the flat yaw ring of <see cref="GizmoMath.HitAxis"/>; the viewport draws <see cref="RotateZ"/> instead).</summary>
    Yaw,

    /// <summary>The red ring: rotation about the X arrow's axis.</summary>
    RotateX,

    /// <summary>The green ring: rotation about the Y arrow's axis.</summary>
    RotateY,

    /// <summary>The blue ring: rotation about the Z arrow's axis.</summary>
    RotateZ,

    /// <summary>The yellow outer ring facing the camera: rotation about the view axis.</summary>
    RotateView,

    /// <summary>The square between the X and Y arrows: a move in that plane.</summary>
    PlaneXY,

    /// <summary>The square between the Y and Z arrows.</summary>
    PlaneYZ,

    /// <summary>The square between the Z and X arrows.</summary>
    PlaneZX,

    /// <summary>The cube past the X arrow's tip: scale along X.</summary>
    ScaleX,

    /// <summary>The cube past the Y arrow's tip.</summary>
    ScaleY,

    /// <summary>The cube past the Z arrow's tip.</summary>
    ScaleZ,

    /// <summary>The cube at the centre: uniform scale.</summary>
    ScaleUniform,
}

/// <summary>What a gizmo handle does.</summary>
public enum GizmoKind
{
    /// <summary>Not a handle.</summary>
    None,

    /// <summary>An arrow: a move along one axis.</summary>
    Move,

    /// <summary>A square: a move in a plane.</summary>
    Plane,

    /// <summary>A ring: a turn about an axis.</summary>
    Rotate,

    /// <summary>A cube: a change of size.</summary>
    Scale,
}

/// <summary>
/// Geometry of the viewport's translation gizmo, independent of any UI: three axis segments drawn from the selected
/// actor's root in the renderer's GL space, hit-tested against a mouse ray and dragged along one axis. Pure functions,
/// unit-tested.
/// </summary>
public static class GizmoMath
{
    /// <summary>The farthest a drag follows the mouse (cm along the view ray or the arrow): beyond the view distance.</summary>
    public const float MaxReach = 4_000_000f;

    /// <summary>Handle length in GL units (cm) for a camera this far from the gizmo origin: constant on screen, never tiny.</summary>
    public static float HandleLength(float cameraDistance) => MathF.Max(50f, cameraDistance * 0.15f);

    /// <summary>Pick tolerance around a handle: a fraction of its length.</summary>
    public static float HitTolerance(float handleLength) => handleLength * 0.09f;

    /// <summary>Radius of the yaw ring for a handle length.</summary>
    public static float RingRadius(float handleLength) => handleLength * 0.75f;

    /// <summary>Points of the yaw ring (UE XY plane through <paramref name="origin"/>, GL space), closed polyline.</summary>
    public static Vector3[] RingPoints(Vector3 origin, float radius, int segments = 48)
    {
        var points = new Vector3[segments + 1];
        for (var i = 0; i < segments; i++)
        {
            var angle = MathF.Tau * i / segments;
            points[i] = origin + GlDirection(GizmoAxis.X) * (MathF.Cos(angle) * radius) + GlDirection(GizmoAxis.Y) * (MathF.Sin(angle) * radius);
        }

        points[segments] = points[0]; // closed exactly (no sin(2π) noise)
        return points;
    }

    /// <summary>
    /// Where the ray meets the yaw plane (UE XY through <paramref name="origin"/>): the angle in degrees of that point
    /// around the origin (UE yaw convention, from +X towards +Y) and its distance from the ring of <paramref name="radius"/>.
    /// False when the ray is parallel to the plane or hits it behind the camera.
    /// </summary>
    public static bool TryRingAngle(Vector3 rayOrigin, Vector3 rayDirection, Vector3 origin, float radius, out float angleDegrees, out float distanceToRing)
    {
        angleDegrees = 0f;
        distanceToRing = float.PositiveInfinity;
        var up = GlDirection(GizmoAxis.Z);
        var denominator = Vector3.Dot(rayDirection, up);
        if (MathF.Abs(denominator) < 1e-6f)
        {
            return false;
        }

        var t = Vector3.Dot(origin - rayOrigin, up) / denominator;
        if (t < 0f)
        {
            return false;
        }

        var hit = rayOrigin + rayDirection * t - origin;
        var x = Vector3.Dot(hit, GlDirection(GizmoAxis.X));
        var y = Vector3.Dot(hit, GlDirection(GizmoAxis.Y));
        var radial = MathF.Sqrt(x * x + y * y);
        angleDegrees = MathF.Atan2(y, x) * (180f / MathF.PI);
        distanceToRing = MathF.Abs(radial - radius);
        return true;
    }

    /// <summary>The root transform after turning <paramref name="deltaDegrees"/> around world Z (snapped when <paramref name="snapDegrees"/> is positive).</summary>
    public static FTransform RotateYaw(FTransform start, float deltaDegrees, float snapDegrees)
    {
        var step = Snap(deltaDegrees, snapDegrees);
        var rotator = start.Rotator();
        var turned = new FTransform(new FRotator(rotator.Pitch, rotator.Yaw + step, rotator.Roll), start.Translation, start.Scale3D);
        return turned;
    }

    /// <summary>Shortest signed difference between two angles in degrees, in (-180, 180].</summary>
    public static float AngleDelta(float fromDegrees, float toDegrees)
    {
        var delta = (toDegrees - fromDegrees) % 360f;
        if (delta > 180f)
        {
            delta -= 360f;
        }
        else if (delta <= -180f)
        {
            delta += 360f;
        }

        return delta;
    }

    /// <summary>Unit direction of <paramref name="axis"/> in UE space.</summary>
    public static FVector UeDirection(GizmoAxis axis) => axis switch
    {
        GizmoAxis.X => new FVector(1, 0, 0),
        GizmoAxis.Y => new FVector(0, 1, 0),
        GizmoAxis.Z => new FVector(0, 0, 1),
        _ => FVector.Zero,
    };

    /// <summary>Unit direction of <paramref name="axis"/> in the renderer's GL space.</summary>
    public static Vector3 GlDirection(GizmoAxis axis) => UeToGl.Direction(UeDirection(axis));

    /// <summary>Rounds <paramref name="value"/> to a multiple of <paramref name="step"/> (no snapping when the step is not positive).</summary>
    public static float Snap(float value, float step) => step > 0f ? MathF.Round(value / step) * step : value;

    /// <summary>
    /// Parameter of the point on the line <c>lineOrigin + t · lineDirection</c> that is closest to the ray (the ray is
    /// clamped to start at its origin), and the distance between those two points. False when ray and line are (nearly)
    /// parallel, so that they meet farther than <see cref="MaxReach"/>.
    /// </summary>
    public static bool TryClosestParameter(Vector3 rayOrigin, Vector3 rayDirection, Vector3 lineOrigin, Vector3 lineDirection, out float t, out float distance)
    {
        var d = Vector3.Normalize(rayDirection);
        var u = Vector3.Normalize(lineDirection);
        var w0 = lineOrigin - rayOrigin;
        var b = Vector3.Dot(d, u);
        var denominator = 1f - b * b; // |d|=|u|=1
        t = 0f;
        distance = float.PositiveInfinity;
        if (denominator < 1e-6f)
        {
            return false;
        }

        var d0 = Vector3.Dot(d, w0);
        var e = Vector3.Dot(u, w0);
        var rayParameter = (d0 - b * e) / denominator;
        if (rayParameter < 0f)
        {
            // The closest approach lies behind the camera: measure from the ray origin instead.
            rayParameter = 0f;
            t = -e;
        }
        else
        {
            t = (b * d0 - e) / denominator;
        }

        if (rayParameter > MaxReach || MathF.Abs(t) > MaxReach || !float.IsFinite(t))
        {
            // A ray nearly along the line meets it kilometres away: following it would fling the object off the map
            // (Discord: errors after objects ended up on the far side of the map). Hold still until the mouse turns back.
            t = 0f;
            return false;
        }

        var onRay = rayOrigin + d * rayParameter;
        var onLine = lineOrigin + u * t;
        distance = Vector3.Distance(onRay, onLine);
        return true;
    }

    /// <summary>
    /// The handle the ray hits (closest within <paramref name="tolerance"/> of a segment from <paramref name="origin"/> of
    /// <paramref name="length"/>), with the parameter along it; <see cref="GizmoAxis.None"/> when nothing is hit.
    /// </summary>
    public static GizmoAxis HitAxis(Vector3 rayOrigin, Vector3 rayDirection, Vector3 origin, float length, float tolerance, out float parameter)
    {
        var best = GizmoAxis.None;
        var bestDistance = tolerance;
        parameter = 0f;
        if (TryRingAngle(rayOrigin, rayDirection, origin, RingRadius(length), out var angle, out var ringDistance) && ringDistance <= tolerance)
        {
            // The ring loses to an axis handle that is also under the cursor (checked below).
            best = GizmoAxis.Yaw;
            bestDistance = ringDistance;
            parameter = angle;
        }

        foreach (var axis in new[] { GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z })
        {
            if (!TryClosestParameter(rayOrigin, rayDirection, origin, GlDirection(axis), out var t, out var distance))
            {
                continue;
            }

            if (t >= -tolerance && t <= length + tolerance && distance <= bestDistance)
            {
                best = axis;
                bestDistance = distance;
                parameter = t;
            }
        }

        return best;
    }

    /// <summary>
    /// Where a GL-space ray meets the horizontal plane at GL height <paramref name="planeY"/> (UE Z), or null when the ray
    /// runs (nearly) parallel to it, points away from it or meets it beyond <paramref name="maxDistance"/>.
    /// </summary>
    public static Vector3? HitHorizontalPlane(Vector3 rayOrigin, Vector3 rayDirection, float planeY, float maxDistance)
    {
        if (MathF.Abs(rayDirection.Y) < 1e-4f)
        {
            return null;
        }

        var t = (planeY - rayOrigin.Y) / rayDirection.Y;
        return t > 0f && t <= maxDistance ? rayOrigin + (rayDirection * t) : null;
    }

    /// <summary>
    /// The root transform after a free left-drag: moved by the horizontal part of <paramref name="glMove"/> (snapped to
    /// <paramref name="snap"/> cm), raised by <paramref name="lift"/> cm and turned by <paramref name="yawDegrees"/> around world Z.
    /// </summary>
    public static FTransform FreeMove(FTransform start, Vector3 glMove, float lift, float yawDegrees, float snap)
    {
        var move = UeToGl.ToUeDirection(glMove);
        var moved = start with
        {
            Translation = new FVector(
                start.Translation.X + Snap(move.X, snap),
                start.Translation.Y + Snap(move.Y, snap),
                start.Translation.Z + lift),
        };
        return yawDegrees == 0f ? moved : RotateYaw(moved, yawDegrees, 0f);
    }

    /// <summary>
    /// Auto-snap: the shift (per axis, 0 when nothing is in reach) that makes <paramref name="moving"/> touch or line up
    /// with the nearest face of a box in <paramref name="others"/> no more than <paramref name="reach"/> away — a tower
    /// end meets the next tower, a wall sits flush on the floor. Only boxes beside the moving one count, so a far object
    /// that merely shares a coordinate never pulls it.
    /// </summary>
    public static Vector3 SnapOffset(BoundingBox moving, IEnumerable<BoundingBox> others, float reach)
    {
        // ponytail: linear scan over every placement box (~1 ms per 100k); a grid if huge scenes make drags stutter.
        var best = new Vector3(float.MaxValue);
        foreach (var other in others)
        {
            if (other.Min.X > moving.Max.X + reach || other.Max.X < moving.Min.X - reach
                || other.Min.Y > moving.Max.Y + reach || other.Max.Y < moving.Min.Y - reach
                || other.Min.Z > moving.Max.Z + reach || other.Max.Z < moving.Min.Z - reach)
            {
                continue;
            }

            for (var axis = 0; axis < 3; axis++)
            {
                foreach (var d in (ReadOnlySpan<float>)[other.Max[axis] - moving.Min[axis], other.Min[axis] - moving.Max[axis],
                             other.Min[axis] - moving.Min[axis], other.Max[axis] - moving.Max[axis]])
                {
                    if (MathF.Abs(d) <= reach && MathF.Abs(d) < MathF.Abs(best[axis]))
                    {
                        best[axis] = d;
                    }
                }
            }
        }

        return new Vector3(best.X == float.MaxValue ? 0f : best.X, best.Y == float.MaxValue ? 0f : best.Y, best.Z == float.MaxValue ? 0f : best.Z);
    }

    /// <summary>What <paramref name="handle"/> does.</summary>
    public static GizmoKind KindOf(GizmoAxis handle) => handle switch
    {
        GizmoAxis.X or GizmoAxis.Y or GizmoAxis.Z => GizmoKind.Move,
        GizmoAxis.PlaneXY or GizmoAxis.PlaneYZ or GizmoAxis.PlaneZX => GizmoKind.Plane,
        GizmoAxis.Yaw or GizmoAxis.RotateX or GizmoAxis.RotateY or GizmoAxis.RotateZ or GizmoAxis.RotateView => GizmoKind.Rotate,
        GizmoAxis.ScaleX or GizmoAxis.ScaleY or GizmoAxis.ScaleZ or GizmoAxis.ScaleUniform => GizmoKind.Scale,
        _ => GizmoKind.None,
    };

    /// <summary>The arrow axis a ring, cube or plane square belongs to (<see cref="GizmoAxis.None"/> for the view ring and the centre cube).</summary>
    public static GizmoAxis AxisOf(GizmoAxis handle) => handle switch
    {
        GizmoAxis.X or GizmoAxis.RotateX or GizmoAxis.ScaleX or GizmoAxis.PlaneYZ => GizmoAxis.X,
        GizmoAxis.Y or GizmoAxis.RotateY or GizmoAxis.ScaleY or GizmoAxis.PlaneZX => GizmoAxis.Y,
        GizmoAxis.Z or GizmoAxis.RotateZ or GizmoAxis.ScaleZ or GizmoAxis.PlaneXY or GizmoAxis.Yaw => GizmoAxis.Z,
        _ => GizmoAxis.None,
    };

    /// <summary>The two arrow axes a plane square spans, in order (XY, YZ or ZX).</summary>
    public static (GizmoAxis A, GizmoAxis B) PlaneAxes(GizmoAxis plane) => plane switch
    {
        GizmoAxis.PlaneXY => (GizmoAxis.X, GizmoAxis.Y),
        GizmoAxis.PlaneYZ => (GizmoAxis.Y, GizmoAxis.Z),
        GizmoAxis.PlaneZX => (GizmoAxis.Z, GizmoAxis.X),
        _ => (GizmoAxis.None, GizmoAxis.None),
    };

    /// <summary>
    /// The root transform turned <paramref name="degrees"/> about the UE-space unit <paramref name="axisUe"/> through its
    /// own origin (a world axis, or the object's own axis): the rotation is applied after the one it had, so its place stays.
    /// </summary>
    public static FTransform RotateAbout(FTransform start, FVector axisUe, float degrees)
    {
        var axis = axisUe.GetSafeNormal();
        if (axis.IsNearlyZero() || degrees == 0f)
        {
            return start;
        }

        var turn = FQuat.FromAxisAngle(axis, degrees * UeMath.DegreesToRadians);
        return start with { Rotation = (turn * start.Rotation).GetNormalized() };
    }

    /// <summary>
    /// The root transform moved <paramref name="deltaA"/> cm along <paramref name="a"/> and <paramref name="deltaB"/> cm
    /// along <paramref name="b"/> (UE unit directions), each snapped to <paramref name="snap"/> when positive.
    /// </summary>
    public static FTransform TranslateInPlane(FTransform start, FVector a, FVector b, float deltaA, float deltaB, float snap) =>
        Translate(Translate(start, a, deltaA, snap), b, deltaB, snap);

    /// <summary>
    /// The root transform scaled by <paramref name="ratio"/> along one of its own axes (<see cref="GizmoAxis.ScaleX"/>,
    /// <see cref="GizmoAxis.ScaleY"/>, <see cref="GizmoAxis.ScaleZ"/>) or all three (<see cref="GizmoAxis.ScaleUniform"/>);
    /// the ratio is snapped to <paramref name="snap"/> when positive and never drops below 0.01.
    /// </summary>
    public static FTransform Scale(FTransform start, GizmoAxis handle, float ratio, float snap)
    {
        var r = MathF.Max(Snap(ratio, snap), 0.01f);
        var s = start.Scale3D;
        return start with
        {
            Scale3D = handle switch
            {
                GizmoAxis.ScaleX => new FVector(s.X * r, s.Y, s.Z),
                GizmoAxis.ScaleY => new FVector(s.X, s.Y * r, s.Z),
                GizmoAxis.ScaleZ => new FVector(s.X, s.Y, s.Z * r),
                GizmoAxis.ScaleUniform => s * r,
                _ => s,
            },
        };
    }

    /// <summary>
    /// Where the ray meets the plane through <paramref name="planeOrigin"/> with unit <paramref name="normal"/>; false when
    /// the ray meets the plane at a grazing angle (under about 6 degrees: a pixel of mouse travel would then move the hit
    /// by many pixels' worth along the view, and a dragged object would fly off) or the plane lies behind the camera.
    /// </summary>
    public static bool TryHitPlane(Vector3 rayOrigin, Vector3 rayDirection, Vector3 planeOrigin, Vector3 normal, out Vector3 hit)
    {
        hit = default;
        var denominator = Vector3.Dot(rayDirection, normal);
        if (MathF.Abs(denominator) < 0.1f)
        {
            return false;
        }

        var t = Vector3.Dot(planeOrigin - rayOrigin, normal) / denominator;
        if (t < 0f || t > MaxReach)
        {
            return false;
        }

        hit = rayOrigin + (rayDirection * t);
        return true;
    }

    /// <summary>
    /// The angle in degrees of <paramref name="point"/> about <paramref name="origin"/> in the plane spanned by the unit
    /// directions <paramref name="u"/> (0 degrees) and <paramref name="v"/> (90 degrees); the same space for all (UE or GL).
    /// </summary>
    public static float PlaneAngle(Vector3 point, Vector3 origin, Vector3 u, Vector3 v)
    {
        var p = point - origin;
        return MathF.Atan2(Vector3.Dot(p, v), Vector3.Dot(p, u)) * (180f / MathF.PI);
    }

    // ponytail: proportions only. Owner: a house outside the game's building folder bent like a bridge; what bends is long
    // and narrow (a wall, fence, pipe or bridge), and in the building folder only a long wall piece.
    /// <summary>
    /// True for a piece at least 2.5 times as long as it is wide (6 times in the game's building folder): a road, bridge,
    /// fence, pipe or wall. What can bend, and what gets its gizmo where the camera looks (<see cref="PivotLocal"/>).
    /// </summary>
    public static bool IsLong(float length, float width, string meshPath) =>
        MathF.Max(length, width) >= (meshPath.Contains("/Models/Buildings/", StringComparison.OrdinalIgnoreCase) ? 6f : 2.5f) * MathF.Min(length, width);

    /// <summary>
    /// True for a road or bridge piece of any proportions (the game's road folder, or "Bridge" in its name; not far-view
    /// models): it bends, rises and curves in an S like a road (owner: "I am building a bridge and want to curve it; only
    /// the roads bend"). Its collision is built from its deck when bent.
    /// </summary>
    public static bool IsRoadOrBridgePiece(string meshPath)
    {
        var name = meshPath[(meshPath.LastIndexOf('/') + 1)..];
        return !name.Contains("_WM", StringComparison.OrdinalIgnoreCase) && !name.Contains("Distant", StringComparison.OrdinalIgnoreCase)
            && (meshPath.Contains("/Models/Road/", StringComparison.OrdinalIgnoreCase) || name.Contains("Bridge", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Where the gizmo stands in the space of the selection's root (UE cm, before its scale): the middle of
    /// <paramref name="box"/> (what is drawn of the selection, in that space), or for a long piece (<see cref="IsLong"/> by
    /// <paramref name="meshPath"/>, which null and a spawn stand-in never are) the point of its long middle line nearest the view ray from
    /// <paramref name="eye"/> along <paramref name="look"/> (GL), so a 200 m road has its gizmo where the camera looks.
    /// <c>Slides</c> is true for a long piece: its pivot follows the camera. The root itself when the box is empty.
    /// </summary>
    public static (FVector Local, bool Slides) PivotLocal(BoundingBox box, FTransform root, string? meshPath, Vector3 eye, Vector3 look)
    {
        if (box.IsEmpty)
        {
            return (FVector.Zero, false);
        }

        var middle = new FVector(box.Center.X, box.Center.Y, box.Center.Z);
        var (length, width) = (box.Size.X * MathF.Abs(root.Scale3D.X), box.Size.Y * MathF.Abs(root.Scale3D.Y));
        if (meshPath is null || SpawnMarkers.IsMarker(meshPath) || !IsLong(length, width, meshPath))
        {
            return (middle, false);
        }

        var a = length >= width ? middle with { X = box.Min.X } : middle with { Y = box.Min.Y };
        var b = length >= width ? middle with { X = box.Max.X } : middle with { Y = box.Max.Y };
        var (start, end) = (UeToGl.Point(root.TransformPosition(a)), UeToGl.Point(root.TransformPosition(b)));
        var span = Vector3.Distance(start, end);
        var t = span < 1e-3f ? 0.5f
            : TryClosestParameter(eye, look, start, end - start, out var along, out _) ? along / span
            : Vector3.Dot(eye - start, end - start) / (span * span); // looking along the piece: the point nearest the eye
        return (a + ((b - a) * Math.Clamp(t, 0f, 1f)), true);
    }

    /// <summary>
    /// What the user sees as the selection <paramref name="members"/> (actors by id, or one instance, part or point by its
    /// key) among the drawn <paramref name="nodes"/>: an instance, part or point is its key's meshes; an actor is its meshes
    /// and the pins that pick as it, without its loot points, spawn parts and points (they pick on their own: a car shop's
    /// vehicles are not its trader) unless it has nothing else. Each part: its mesh box, model matrix (GL) and mesh path.
    /// </summary>
    public static List<(BoundingBox Box, Matrix4x4 World, string? Mesh)> PivotParts(IEnumerable<SceneNode> nodes, IReadOnlyCollection<(uint Id, InstanceKey? Instance)> members)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(members);
        var actors = members.Where(m => m.Instance is null).Select(m => m.Id).ToHashSet();
        var keys = members.Where(m => m.Instance is not null).Select(m => m.Instance!.Value).ToHashSet();
        var own = new List<SceneNode>();
        var spawns = new List<SceneNode>();
        foreach (var node in nodes)
        {
            if (node.Mesh is null || !node.IsEffectivelyVisible)
            {
                continue;
            }

            var placement = node.Tag as ScenePlacement;
            if (placement?.InstanceKey is { } key && keys.Contains(key))
            {
                own.Add(node);
            }
            else if (actors.Contains(node.SelectableId))
            {
                (placement is null or { LootMarker: null, Spawner: null, SpawnPoint: null } ? own : spawns).Add(node);
            }
        }

        var withOwn = own.Select(n => n.SelectableId).ToHashSet();
        own.AddRange(spawns.Where(n => !withOwn.Contains(n.SelectableId))); // a loot spawner is its points
        return own.Select(n => (n.Mesh!.Bounds, n.WorldTransform, (n.Tag as ScenePlacement)?.MeshPath)).ToList();
    }

    /// <summary>The box around <paramref name="parts"/> (GL mesh boxes and model matrices) in the space of <paramref name="root"/> (UE cm, before its scale).</summary>
    public static BoundingBox LocalBox(IEnumerable<(BoundingBox Box, Matrix4x4 World, string? Mesh)> parts, FTransform root)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var box = BoundingBox.Empty;
        foreach (var (bounds, world, _) in parts)
        {
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? bounds.Min.X : bounds.Max.X, (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z);
                var local = root.InverseTransformPosition(UeToGl.ToUePoint(Vector3.Transform(corner, world)));
                box = box.Include(new Vector3(local.X, local.Y, local.Z));
            }
        }

        return box;
    }

    /// <summary>The root transform after dragging <paramref name="axis"/> by <paramref name="delta"/> cm (snapped to <paramref name="snap"/> when positive).</summary>
    public static FTransform Translate(FTransform start, GizmoAxis axis, float delta, float snap) => Translate(start, UeDirection(axis), delta, snap);

    /// <summary>
    /// The root transform moved <paramref name="delta"/> cm (snapped to <paramref name="snap"/> when positive) along the
    /// UE-space unit <paramref name="direction"/>: a world axis, or the object's own axis (Discord salvador: "the gizmo
    /// doesn't align with the object's orientation").
    /// </summary>
    public static FTransform Translate(FTransform start, FVector direction, float delta, float snap)
    {
        var step = Snap(delta, snap);
        return start with
        {
            Translation = new FVector(
                start.Translation.X + direction.X * step,
                start.Translation.Y + direction.Y * step,
                start.Translation.Z + direction.Z * step),
        };
    }
}
