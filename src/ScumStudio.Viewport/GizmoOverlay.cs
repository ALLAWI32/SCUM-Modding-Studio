using System.Numerics;
using ScumStudio.Rendering.SceneGraph;

namespace ScumStudio.Viewport;

/// <summary>
/// Where the transform gizmo stands, in the renderer's GL space: its origin (the selection's middle), its three arrow
/// directions (the object's own axes or the world's), the camera, and the arrow length (constant on screen).
/// </summary>
/// <param name="Origin">The pivot.</param>
/// <param name="X">Unit GL direction of the X arrow (red).</param>
/// <param name="Y">Unit GL direction of the Y arrow (green).</param>
/// <param name="Z">Unit GL direction of the Z arrow (blue).</param>
/// <param name="CameraPosition">The camera's place.</param>
/// <param name="CameraForward">The camera's unit view direction.</param>
/// <param name="Length">Arrow length in GL units (cm), see <see cref="GizmoMath.HandleLength"/>.</param>
public readonly record struct GizmoFrame(Vector3 Origin, Vector3 X, Vector3 Y, Vector3 Z, Vector3 CameraPosition, Vector3 CameraForward, float Length)
{
    /// <summary>Unit direction from the pivot towards the camera.</summary>
    public Vector3 View => Vector3.Normalize(CameraPosition - Origin);

    /// <summary>The arrow direction of <paramref name="axis"/> (<see cref="Vector3.Zero"/> for anything else).</summary>
    public Vector3 Axis(GizmoAxis axis) => axis switch
    {
        GizmoAxis.X => X,
        GizmoAxis.Y => Y,
        GizmoAxis.Z => Z,
        _ => Vector3.Zero,
    };

    /// <summary>The two unit directions (0 and 90 degrees) of the ring about <paramref name="axis"/>: Y-Z for X, Z-X for Y, X-Y for Z.</summary>
    public (Vector3 U, Vector3 V) RingBasis(GizmoAxis axis) => axis switch
    {
        GizmoAxis.X => (Y, Z),
        GizmoAxis.Y => (Z, X),
        _ => (X, Y),
    };
}

/// <summary>One pickable part of the gizmo: a point (one entry), a polyline, or a closed outline.</summary>
/// <param name="Handle">The handle it belongs to.</param>
/// <param name="Points">GL points.</param>
/// <param name="Closed">True for a filled square (the inside counts as hit).</param>
/// <param name="Priority">Lower wins when several shapes are under the cursor: cubes 0, squares 1, arrows 2, rings 3.</param>
public readonly record struct GizmoPickShape(GizmoAxis Handle, Vector3[] Points, bool Closed, int Priority);

/// <summary>
/// The look of the viewport's transform gizmo (the owner wants it like the FiveM map editors, Unity and Unreal): three
/// arrows with cone heads, a square between each pair of arrows for a move in that plane, three rings drawn as a globe
/// (the X and Y rings dim and thin on the half away from the camera, the purple spin ring whole) plus a yellow ring facing
/// the camera, and small cubes for scale. Pure geometry in GL space: the viewport and the offscreen tests draw it through
/// the renderer's overlay lists.
/// </summary>
public static class GizmoOverlay
{
    /// <summary>Line thickness in pixels.</summary>
    public const float LineWidth = 2.5f;

    /// <summary>Radius of the three axis rings as a fraction of the arrow length.</summary>
    public const float RingRadius = 0.74f;

    /// <summary>Radius of the view ring as a fraction of the arrow length: outside everything, so no scale cube ever lands on it.</summary>
    public const float ViewRingRadius = 1.3f;

    /// <summary>Where the scale cubes sit along their arrows, as a fraction of the arrow length.</summary>
    public const float CubeAt = 1.16f;

    private const float ArrowStart = 0.22f;
    private const float ConeStart = 0.8f;
    private const float ConeRadius = 0.075f;
    private const float PlaneNear = 0.28f;
    private const float PlaneFar = 0.46f;
    private const float CubeHalf = 0.06f;
    private const float CentreCubeHalf = 0.06f;
    private const int RingSegments = 64;
    private const int ConeSegments = 12;

    /// <summary>Axis red #E5484D (linear).</summary>
    public static readonly Vector4 Red = Srgb(0xE5, 0x48, 0x4D);

    /// <summary>Axis green #46A758 (linear).</summary>
    public static readonly Vector4 Green = Srgb(0x46, 0xA7, 0x58);

    /// <summary>Axis blue #3E8EF7 (linear).</summary>
    public static readonly Vector4 Blue = Srgb(0x3E, 0x8E, 0xF7);

    /// <summary>
    /// The spin ring purple #A855F7 (linear): the ring about the Z arrow turns the object about its up axis (owner: "an
    /// obvious ring to spin it 360°, like the purple ring in other editors"); the Z arrow stays blue.
    /// </summary>
    public static readonly Vector4 Purple = Srgb(0xA8, 0x55, 0xF7);

    /// <summary>Hover and drag yellow #F5D90A (linear).</summary>
    public static readonly Vector4 Yellow = Srgb(0xF5, 0xD9, 0x0A);

    /// <summary>The dark edge drawn under every line so it reads on sky, concrete and grass alike.</summary>
    public static readonly Vector4 Outline = new(0.01f, 0.01f, 0.01f, 0.75f);

    private static readonly GizmoAxis[] Arrows = [GizmoAxis.X, GizmoAxis.Y, GizmoAxis.Z];
    private static readonly GizmoAxis[] Planes = [GizmoAxis.PlaneXY, GizmoAxis.PlaneYZ, GizmoAxis.PlaneZX];
    private static readonly GizmoAxis[] Rings = [GizmoAxis.RotateX, GizmoAxis.RotateY, GizmoAxis.RotateZ];
    private static readonly GizmoAxis[] Cubes = [GizmoAxis.ScaleX, GizmoAxis.ScaleY, GizmoAxis.ScaleZ];

    /// <summary>The colour of a handle at rest: its axis colour, purple for the spin ring, yellow for the view ring, white-grey for the centre cube.</summary>
    public static Vector4 ColorOf(GizmoAxis handle) => handle == GizmoAxis.RotateZ ? Purple : GizmoMath.AxisOf(handle) switch
    {
        GizmoAxis.X => Red,
        GizmoAxis.Y => Green,
        GizmoAxis.Z => Blue,
        _ => handle == GizmoAxis.RotateView ? Yellow : new Vector4(0.8f, 0.8f, 0.8f, 1f),
    };

    /// <summary>The centre of a scale cube (<see cref="GizmoAxis.ScaleUniform"/>: the origin).</summary>
    public static Vector3 CubeCentre(in GizmoFrame f, GizmoAxis cube) =>
        f.Origin + (f.Axis(GizmoMath.AxisOf(cube)) * (CubeAt * f.Length));

    /// <summary>The four corners of a plane square (the two axes of <see cref="GizmoMath.PlaneAxes"/>).</summary>
    public static Vector3[] PlaneCorners(in GizmoFrame f, GizmoAxis plane)
    {
        var (a, b) = GizmoMath.PlaneAxes(plane);
        var (da, db) = (f.Axis(a), f.Axis(b));
        var (near, far) = (PlaneNear * f.Length, PlaneFar * f.Length);
        return
        [
            f.Origin + (da * near) + (db * near),
            f.Origin + (da * far) + (db * near),
            f.Origin + (da * far) + (db * far),
            f.Origin + (da * near) + (db * far),
        ];
    }

    /// <summary>The points of a ring (closed): the globe ring of <paramref name="ring"/>, or the view ring for <see cref="GizmoAxis.RotateView"/>.</summary>
    public static Vector3[] RingPoints(in GizmoFrame f, GizmoAxis ring)
    {
        var (u, v, radius) = ring == GizmoAxis.RotateView ? ViewBasis(f) : (f.RingBasis(GizmoMath.AxisOf(ring)).U, f.RingBasis(GizmoMath.AxisOf(ring)).V, RingRadius * f.Length);
        var points = new Vector3[RingSegments + 1];
        for (var i = 0; i < RingSegments; i++)
        {
            var angle = MathF.Tau * i / RingSegments;
            points[i] = f.Origin + (u * (MathF.Cos(angle) * radius)) + (v * (MathF.Sin(angle) * radius));
        }

        points[RingSegments] = points[0];
        return points;
    }

    /// <summary>The 0-degree and 90-degree directions of the view ring (the camera's right and up) and its radius.</summary>
    public static (Vector3 U, Vector3 V, float Radius) ViewBasis(in GizmoFrame f)
    {
        var forward = f.CameraForward;
        var right = Vector3.Cross(forward, Vector3.UnitY);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        return (right, up, ViewRingRadius * f.Length);
    }

    /// <summary>True when a point of a ring lies on the half of the globe that faces the camera.</summary>
    public static bool FacesCamera(in GizmoFrame f, Vector3 point) => Vector3.Dot(point - f.Origin, f.View) >= 0f;

    /// <summary>
    /// Everything the cursor can pick, nearest-priority first: cubes, plane squares, arrows, rings (the X and Y rings only on
    /// their front half, the spin ring and the view ring whole).
    /// </summary>
    public static List<GizmoPickShape> PickShapes(in GizmoFrame f, bool scale)
    {
        var shapes = new List<GizmoPickShape>();
        if (scale)
        {
            foreach (var cube in Cubes)
            {
                shapes.Add(new GizmoPickShape(cube, [CubeCentre(f, cube)], false, 0));
            }

            shapes.Add(new GizmoPickShape(GizmoAxis.ScaleUniform, [f.Origin], false, 0));
        }

        foreach (var plane in Planes)
        {
            shapes.Add(new GizmoPickShape(plane, PlaneCorners(f, plane), true, 1));
        }

        foreach (var arrow in Arrows)
        {
            var d = f.Axis(arrow);
            shapes.Add(new GizmoPickShape(arrow, [f.Origin + (d * (ArrowStart * f.Length)), f.Origin + (d * f.Length)], false, 2));
        }

        foreach (var ring in Rings)
        {
            var points = RingPoints(f, ring);
            if (ring == GizmoAxis.RotateZ)
            {
                shapes.Add(new GizmoPickShape(ring, points, false, 3)); // the spin ring is drawn whole: grabbed anywhere
                continue;
            }

            // The front half only, as runs of consecutive segments: the dim back half is not grabbed through the object.
            var run = new List<Vector3>();
            for (var i = 1; i < points.Length; i++)
            {
                if (FacesCamera(f, (points[i - 1] + points[i]) * 0.5f))
                {
                    if (run.Count == 0)
                    {
                        run.Add(points[i - 1]);
                    }

                    run.Add(points[i]);
                }
                else if (run.Count > 0)
                {
                    shapes.Add(new GizmoPickShape(ring, [.. run], false, 3));
                    run.Clear();
                }
            }

            if (run.Count > 1)
            {
                shapes.Add(new GizmoPickShape(ring, [.. run], false, 3));
            }
        }

        shapes.Add(new GizmoPickShape(GizmoAxis.RotateView, RingPoints(f, GizmoAxis.RotateView), false, 3));
        return shapes;
    }

    /// <summary>
    /// Draws the gizmo into <paramref name="lines"/> and <paramref name="triangles"/>: <paramref name="hover"/> is drawn
    /// yellow and thicker; while <paramref name="dragging"/>, <paramref name="active"/> stays yellow and every other handle
    /// fades to 40 %. The scale cubes are left out when <paramref name="scale"/> is false.
    /// </summary>
    public static void Draw(in GizmoFrame f, GizmoAxis hover, GizmoAxis active, bool dragging, bool scale, List<OverlayLine> lines, List<OverlayTriangle> triangles)
    {
        var strokes = new List<OverlayLine>();
        var fills = new List<OverlayTriangle>();
        var lit = dragging ? active : hover;

        // Rings first (under the arrows): the three of the globe, then the view ring. The spin ring (Z, last so it lies on
        // the others) is purple, a pixel thicker and whole; the X and Y rings are dim and thin on their back half.
        foreach (var ring in Rings)
        {
            var (color, width) = Style(ring, lit, dragging);
            var spin = ring == GizmoAxis.RotateZ;
            var points = RingPoints(f, ring)[..^1];
            for (var i = 0; i < points.Length; i++)
            {
                var front = spin || FacesCamera(f, (points[i] + points[(i + 1) % points.Length]) * 0.5f);
                strokes.Add(Segment(points, i, front ? color : color with { W = color.W * 0.3f }, front ? width + (spin ? 1f : 0f) : width * 0.55f));
            }
        }

        {
            var (color, width) = Style(GizmoAxis.RotateView, lit, dragging);
            var points = RingPoints(f, GizmoAxis.RotateView)[..^1];
            for (var i = 0; i < points.Length; i++)
            {
                strokes.Add(Segment(points, i, color, width));
            }
        }

        foreach (var plane in Planes)
        {
            var (color, width) = Style(plane, lit, dragging);
            var c = PlaneCorners(f, plane);
            var fill = color with { W = color.W * (lit == plane ? 0.6f : 0.35f) };
            fills.Add(new OverlayTriangle(c[0], c[1], c[2], fill));
            fills.Add(new OverlayTriangle(c[0], c[2], c[3], fill));
            for (var i = 0; i < 4; i++)
            {
                strokes.Add(Segment(c, i, color, width * 0.7f));
            }
        }

        foreach (var arrow in Arrows)
        {
            var (color, width) = Style(arrow, lit, dragging);
            var d = f.Axis(arrow);
            var tip = f.Origin + (d * f.Length);
            var coneBase = f.Origin + (d * (ConeStart * f.Length));
            strokes.Add(new OverlayLine(f.Origin + (d * (ArrowStart * f.Length)), coneBase, color, width));
            Cone(f, coneBase, tip, d, ConeRadius * f.Length, color, fills, strokes);
        }

        if (scale)
        {
            foreach (var cube in Cubes)
            {
                var (color, _) = Style(cube, lit, dragging);
                Cube(f, CubeCentre(f, cube), CubeHalf * f.Length, color, fills, strokes);
            }

            var (centre, _) = Style(GizmoAxis.ScaleUniform, lit, dragging);
            Cube(f, f.Origin, CentreCubeHalf * f.Length, centre, fills, strokes);
        }

        // Fills, then every line's dark edge, then the lines: the edges never cut through a neighbouring line.
        triangles.AddRange(fills);
        foreach (var line in strokes)
        {
            lines.Add(line with { Color = Outline with { W = Outline.W * line.Color.W }, Width = line.Width + 2f });
        }

        lines.AddRange(strokes);
    }

    /// <summary>Linear colour of an sRGB byte triple.</summary>
    public static Vector4 Srgb(byte r, byte g, byte b) => new(Linear(r), Linear(g), Linear(b), 1f);

    private static float Linear(byte c)
    {
        var s = c / 255f;
        return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
    }

    private static (Vector4 Color, float Width) Style(GizmoAxis handle, GizmoAxis lit, bool dragging)
    {
        if (handle == lit)
        {
            return (Yellow, LineWidth + 1f);
        }

        var color = ColorOf(handle);
        return (dragging ? color with { W = color.W * 0.4f } : color, LineWidth);
    }

    /// <summary>
    /// Segment <paramref name="i"/> of the closed polyline <paramref name="loop"/> (the last point joins the first), mitered
    /// to its neighbours: the segments tile without overlap, so a translucent loop stays even instead of beaded.
    /// </summary>
    private static OverlayLine Segment(Vector3[] loop, int i, Vector4 color, float width)
    {
        var n = loop.Length;
        return new OverlayLine(loop[i], loop[(i + 1) % n], color, width, loop[(i + n - 1) % n], loop[(i + 2) % n]);
    }

    /// <summary>A shaded cone from <paramref name="baseCentre"/> to <paramref name="tip"/> with a closed base.</summary>
    private static void Cone(in GizmoFrame f, Vector3 baseCentre, Vector3 tip, Vector3 axis, float radius, Vector4 color, List<OverlayTriangle> fills, List<OverlayLine> strokes)
    {
        var (u, v) = Perpendicular(axis);
        var rim = new Vector3[ConeSegments];
        for (var i = 0; i < ConeSegments; i++)
        {
            var angle = MathF.Tau * i / ConeSegments;
            rim[i] = baseCentre + (u * (MathF.Cos(angle) * radius)) + (v * (MathF.Sin(angle) * radius));
        }

        for (var i = 0; i < ConeSegments; i++)
        {
            var next = rim[(i + 1) % ConeSegments];
            fills.Add(Shaded(f, tip, rim[i], next, color));
            fills.Add(Shaded(f, baseCentre, next, rim[i], color));
        }

        // A thin dark rim where the cone meets the shaft, so the head keeps its shape on a bright wall.
        for (var i = 0; i < ConeSegments; i++)
        {
            strokes.Add(Segment(rim, i, color with { X = color.X * 0.5f, Y = color.Y * 0.5f, Z = color.Z * 0.5f }, 1f));
        }
    }

    /// <summary>A shaded cube of half size <paramref name="half"/> aligned with the frame's axes, with thin dark edges.</summary>
    private static void Cube(in GizmoFrame f, Vector3 centre, float half, Vector4 color, List<OverlayTriangle> fills, List<OverlayLine> strokes)
    {
        var (x, y, z) = (f.X * half, f.Y * half, f.Z * half);
        Vector3 Corner(int i) => centre + ((i & 1) == 0 ? -x : x) + ((i & 2) == 0 ? -y : y) + ((i & 4) == 0 ? -z : z);
        ReadOnlySpan<(int A, int B, int C, int D)> faces = [(0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5)];
        foreach (var (a, b, c, d) in faces)
        {
            fills.Add(Shaded(f, Corner(a), Corner(b), Corner(c), color));
            fills.Add(Shaded(f, Corner(a), Corner(c), Corner(d), color));
        }

        var edge = color with { X = color.X * 0.35f, Y = color.Y * 0.35f, Z = color.Z * 0.35f };
        foreach (var (a, b) in new[] { (0, 1), (2, 3), (4, 5), (6, 7), (0, 2), (1, 3), (4, 6), (5, 7), (0, 4), (1, 5), (2, 6), (3, 7) })
        {
            strokes.Add(new OverlayLine(Corner(a), Corner(b), edge, 1f));
        }
    }

    /// <summary>A triangle lit by how much it faces the camera (0.55 away to 1.0 facing), so cones and cubes read as solids.</summary>
    private static OverlayTriangle Shaded(in GizmoFrame f, Vector3 a, Vector3 b, Vector3 c, Vector4 color)
    {
        var normal = Vector3.Cross(b - a, c - a);
        var toCamera = f.CameraPosition - a;
        var facing = normal.LengthSquared() > 1e-12f && toCamera.LengthSquared() > 1e-12f
            ? MathF.Abs(Vector3.Dot(Vector3.Normalize(normal), Vector3.Normalize(toCamera)))
            : 1f;
        var light = 0.55f + (0.45f * facing);
        return new OverlayTriangle(a, b, c, new Vector4(color.X * light, color.Y * light, color.Z * light, color.W));
    }

    private static (Vector3 U, Vector3 V) Perpendicular(Vector3 axis)
    {
        var helper = MathF.Abs(axis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, helper));
        return (u, Vector3.Normalize(Vector3.Cross(axis, u)));
    }
}
