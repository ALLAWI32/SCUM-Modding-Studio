using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

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

    /// <summary>Rotation around UE Z (the yaw ring, yellow).</summary>
    Yaw,
}

/// <summary>
/// Geometry of the viewport's translation gizmo, independent of any UI: three axis segments drawn from the selected
/// actor's root in the renderer's GL space, hit-tested against a mouse ray and dragged along one axis. Pure functions,
/// unit-tested.
/// </summary>
public static class GizmoMath
{
    /// <summary>Handle length in GL units (cm) for a camera this far from the gizmo origin: constant on screen, never tiny.</summary>
    public static float HandleLength(float cameraDistance) => MathF.Max(50f, cameraDistance * 0.12f);

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

    /// <summary>Linear RGBA of a handle; brighter when hovered or dragged.</summary>
    public static Vector4 Color(GizmoAxis axis, bool active) => (axis, active) switch
    {
        (GizmoAxis.X, false) => new Vector4(0.90f, 0.22f, 0.18f, 1f),
        (GizmoAxis.Y, false) => new Vector4(0.25f, 0.78f, 0.25f, 1f),
        (GizmoAxis.Z, false) => new Vector4(0.25f, 0.50f, 1.00f, 1f),
        (GizmoAxis.Yaw, false) => new Vector4(0.95f, 0.80f, 0.20f, 0.9f),
        (_, true) => new Vector4(1f, 0.95f, 0.5f, 1f),
        _ => Vector4.One,
    };

    /// <summary>Rounds <paramref name="value"/> to a multiple of <paramref name="step"/> (no snapping when the step is not positive).</summary>
    public static float Snap(float value, float step) => step > 0f ? MathF.Round(value / step) * step : value;

    /// <summary>
    /// Parameter of the point on the line <c>lineOrigin + t · lineDirection</c> that is closest to the ray (the ray is
    /// clamped to start at its origin), and the distance between those two points. False when ray and line are parallel.
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
