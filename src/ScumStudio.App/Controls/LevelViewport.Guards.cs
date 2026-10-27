using System.Numerics;
using Avalonia;
using Avalonia.Threading;
using ScumStudio.Core.Mathematics;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Viewport;

namespace ScumStudio.App.Controls;

/// <summary>How a guard post is drawn on the 3D map.</summary>
public enum GuardMarkerKind
{
    /// <summary>A post of the set being edited.</summary>
    Normal,

    /// <summary>The picked post (drawn white).</summary>
    Selected,

    /// <summary>Where a click would put the next post (half transparent).</summary>
    Ghost,

    /// <summary>A post of another set (half transparent).</summary>
    Other,
}

/// <summary>One guard post to draw: a pole with a diamond on top, coloured by the guard's level.</summary>
/// <param name="At">The floor under the guard (UE cm).</param>
/// <param name="Level">The guard's level (3 weakest, 5 strongest).</param>
/// <param name="Kind">How it is drawn.</param>
/// <param name="Set">The set the post belongs to (empty for the ghost under the cursor).</param>
/// <param name="Index">Its place in the set (-1 for the ghost).</param>
public readonly record struct GuardMarker(FVector At, int Level, GuardMarkerKind Kind, string Set = "", int Index = -1);

/// <summary>The guard tool's drawing: the posts and the circle where a player wakes the set.</summary>
/// <param name="Markers">The posts.</param>
/// <param name="WakeCenter">The middle of the edited set's area (UE cm), or null for no circle.</param>
/// <param name="WakeRadius">How close to <paramref name="WakeCenter"/> a player wakes the guards (cm).</param>
public sealed record GuardOverlay(IReadOnlyList<GuardMarker> Markers, FVector? WakeCenter = null, float WakeRadius = 0f);

/// <summary>
/// The guard tool (Map page, "Guard posts"): posts drawn where they stand in the 3D map, so the owner sees where each guard
/// will be (the flat Spawns map hid the height, and a dungeon's floor lies under its roof).
/// </summary>
public sealed partial class LevelViewport
{
    /// <summary>Guard tool: the cursor places posts instead of selecting, and no brush circle is drawn.</summary>
    public static readonly StyledProperty<bool> GuardModeProperty =
        AvaloniaProperty.Register<LevelViewport, bool>(nameof(GuardMode));

    /// <summary>The guard posts and the wake-up circle to draw.</summary>
    public static readonly StyledProperty<GuardOverlay?> GuardOverlayProperty =
        AvaloniaProperty.Register<LevelViewport, GuardOverlay?>(nameof(GuardOverlay));

    private const float PostHeightCm = 180f;
    private const float PostDiamondCm = 40f;
    private GuardMarker? _guardGrab;
    private bool _guardGrabTried;

    /// <summary>A press in <see cref="GuardMode"/> landed on a post's pole (picked, and dragged while the button is held).</summary>
    public event EventHandler<GuardMarker>? GuardMarkerGrabbed;

    /// <summary>The grabbed post is dragged: where the cursor meets the horizontal plane at the post's floor (UE cm).</summary>
    public event EventHandler<FVector>? GuardMarkerDragged;

    /// <summary>
    /// The guard tool's part of a brush press: the first press of a stroke picks the post whose pole the cursor's ray passes
    /// closest to (the posts are drawn through roofs, so the cursor's ground point is not where the post is); while the button
    /// is held the post follows the cursor along its own floor plane. True while a post is grabbed (the stroke puts no new post).
    /// </summary>
    private bool HandleGuardStroke(Vector3 origin, Vector3 direction, bool pressed)
    {
        if (!GuardMode || !pressed || GuardOverlay is not { } overlay)
        {
            return _guardGrab is not null && pressed;
        }

        if (!_guardGrabTried)
        {
            _guardGrabTried = true;
            if (PickGuardMarker(origin, direction, overlay) is { } marker)
            {
                _guardGrab = marker;
                Dispatcher.UIThread.Post(() => GuardMarkerGrabbed?.Invoke(this, marker));
            }
        }

        if (_guardGrab is { } grabbed && direction.Y < -1e-4f)
        {
            var foot = UeToGl.Point(grabbed.At);
            var t = (foot.Y - origin.Y) / direction.Y; // GL's Y is up: the plane of the post's floor
            if (t > 0f)
            {
                var at = UeToGl.ToUePoint(origin + (direction * t));
                Dispatcher.UIThread.Post(() => GuardMarkerDragged?.Invoke(this, at with { Z = grabbed.At.Z }));
            }
        }

        return _guardGrab is not null;
    }

    /// <summary>The post (not the ghost) whose pole passes nearest the ray, within a few percent of its distance (at least 40 cm).</summary>
    private static GuardMarker? PickGuardMarker(Vector3 origin, Vector3 direction, GuardOverlay overlay)
    {
        GuardMarker? best = null;
        var bestDistance = float.MaxValue;
        foreach (var marker in overlay.Markers.Where(m => m.Kind != GuardMarkerKind.Ghost && m.Index >= 0))
        {
            var foot = UeToGl.Point(marker.At);
            var top = UeToGl.Point(new FVector(marker.At.X, marker.At.Y, marker.At.Z + PostHeightCm));
            var (distance, along) = RayToSegment(origin, direction, foot, top);
            var reach = MathF.Max(40f, 0.03f * Vector3.Distance(origin, (foot + top) / 2f));
            if (along > 0f && distance <= reach && distance < bestDistance)
            {
                best = marker;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>The closest approach of the ray (unit <paramref name="d"/>) to the segment a-b, and how far along the ray it is.</summary>
    internal static (float Distance, float Along) RayToSegment(Vector3 o, Vector3 d, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var r = o - a;
        var abab = Vector3.Dot(ab, ab);
        var dab = Vector3.Dot(d, ab);
        var denominator = abab - (dab * dab);
        // s: the segment parameter of the closest point (least squares of |r + t d - s ab|); the ray's t follows from projecting that point on the ray.
        var s = denominator < 1e-6f ? 0f : Math.Clamp((Vector3.Dot(ab, r) - (Vector3.Dot(d, r) * dab)) / denominator, 0f, 1f);
        var p = a + (ab * s);
        var t = Vector3.Dot(p - o, d);
        var onRay = o + (d * MathF.Max(t, 0f));
        return (Vector3.Distance(onRay, p), t);
    }

    /// <inheritdoc cref="GuardModeProperty" />
    public bool GuardMode
    {
        get => GetValue(GuardModeProperty);
        set => SetValue(GuardModeProperty, value);
    }

    /// <inheritdoc cref="GuardOverlayProperty" />
    public GuardOverlay? GuardOverlay
    {
        get => GetValue(GuardOverlayProperty);
        set => SetValue(GuardOverlayProperty, value);
    }

    /// <summary>The colour of a post of a guard level: green (3), amber (4), red (5).</summary>
    internal static Vector4 GuardColor(int level, GuardMarkerKind kind)
    {
        var color = level switch
        {
            <= 3 => new Vector4(0.3f, 1f, 0.45f, 1f),
            4 => new Vector4(1f, 0.8f, 0.2f, 1f),
            _ => new Vector4(1f, 0.35f, 0.3f, 1f),
        };
        return kind switch
        {
            GuardMarkerKind.Selected => new Vector4(1f, 1f, 1f, 1f),
            GuardMarkerKind.Ghost or GuardMarkerKind.Other => color with { W = 0.5f },
            _ => color,
        };
    }

    /// <summary>Overlay lines of the guard posts (a pole, a diamond on top, a ring at the foot) and the wake-up circle.</summary>
    internal static IEnumerable<OverlayLine> GuardLines(GuardOverlay overlay)
    {
        foreach (var (at, level, kind, _, _) in overlay.Markers)
        {
            var color = GuardColor(level, kind);
            var width = kind == GuardMarkerKind.Selected ? 3f : 2f;
            var foot = UeToGl.Point(at);
            var top = UeToGl.Point(new FVector(at.X, at.Y, at.Z + PostHeightCm));
            yield return new OverlayLine(foot, top, color, width);

            var ring = GizmoMath.RingPoints(foot + new Vector3(0f, SpotMarkLift, 0f), PostDiamondCm, 12);
            for (var i = 1; i < ring.Length; i++)
            {
                yield return new OverlayLine(ring[i - 1], ring[i], color, width);
            }

            // A diamond (a plus and its ends) on the pole's top, so the post reads from far away.
            var side = new Vector3(PostDiamondCm, 0f, 0f);
            var depth = new Vector3(0f, 0f, PostDiamondCm);
            var up = new Vector3(0f, PostDiamondCm, 0f);
            yield return new OverlayLine(top - up, top + side, color, width);
            yield return new OverlayLine(top + side, top + up, color, width);
            yield return new OverlayLine(top + up, top - side, color, width);
            yield return new OverlayLine(top - side, top - up, color, width);
            yield return new OverlayLine(top - depth, top + depth, color, width);
        }

        if (overlay.WakeCenter is { } centre && overlay.WakeRadius > 1f)
        {
            var wake = new Vector4(0.4f, 0.8f, 1f, 0.55f);
            var ring = GizmoMath.RingPoints(UeToGl.Point(new FVector(centre.X, centre.Y, centre.Z + SpotMarkLift)), overlay.WakeRadius, 96);
            for (var i = 1; i < ring.Length; i++)
            {
                yield return new OverlayLine(ring[i - 1], ring[i], wake);
            }
        }
    }
}
