using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Model;

/// <summary>
/// The spline a bent actor is drawn with (<see cref="Editing.BendActorOp"/>): one <c>SplineMeshComponent</c> whose cubic
/// Hermite curve follows a circular arc. The mesh's longer horizontal axis runs along the curve, the middle of the mesh
/// stays where it was, the arc keeps the mesh's length, and positive degrees turn right (seen walking from the start to
/// the end), negative left. The actor's scale is baked into the curve and the cross-section scale, so the component
/// itself has scale 1 and the arc stays round whatever the scale.
/// </summary>
/// <remarks>
/// The end tangents are 4·r·tan(θ/4) long (r the arc radius, θ the bend in radians): the cubic that matches a circular
/// arc best, within about 0.03 % of the radius at 90° and 0.3 % at 180°. Straight (θ = 0) reproduces the scaled mesh
/// exactly. The mesh range mapped onto the curve is written explicitly (<c>SplineBoundaryMin/Max</c>) so the game and the
/// viewport map vertices the same way.
/// </remarks>
public static class BendShape
{
    /// <summary>Largest bend, degrees (a half circle).</summary>
    public const float MaxDegrees = 180f;

    /// <summary>
    /// The spline parameters drawing a mesh with bounds <paramref name="meshBounds"/> (UE component space, centimetres)
    /// at <paramref name="scale"/> (the actor's relative scale) bent by <paramref name="degrees"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Empty bounds, or a zero scale.</exception>
    public static SplineMeshParams For(BoundingBox meshBounds, FVector scale, float degrees, float sway1, float sway2) =>
        SplineSway.Apply(For(meshBounds, scale, degrees), sway1, sway2);

    /// <summary>The whole shape: the bend, the ends moved, turned or widened (<see cref="SplineEnds"/>), then the handles pushed.</summary>
    public static SplineMeshParams For(BoundingBox meshBounds, FVector scale, Editing.BendValue shape) =>
        SplineEnds.Shape(For(meshBounds, scale, shape.Degrees), shape.Sway1, shape.Sway2, shape.Start, shape.End);

    /// <summary>
    /// The whole shape as the game draws it: cut into pieces of about the mesh's own length, so a fence made longer
    /// repeats its bars instead of stretching them (<see cref="SplineTiles"/>); one piece while it is about as long as made.
    /// </summary>
    public static IReadOnlyList<SplineMeshParams> Pieces(BoundingBox meshBounds, FVector scale, Editing.BendValue shape) =>
        shape.HasLegs ? [Legs(meshBounds, scale, shape.Legs)] : SplineTiles.Split(For(meshBounds, scale, shape), PieceLength(meshBounds, scale));

    /// <summary>
    /// Longer legs (owner: "lower the tower's foot under the water, the tower stays where it is"): the mesh drawn upright
    /// along a vertical spline from its foot, <paramref name="legs"/> cm lower, to its top, which stays. The stretch is at
    /// the foot and fades upwards (the curve runs H + 3E(1-t)² per unit, H the height, E the legs): the top keeps its size,
    /// a point at height fraction t comes down E(1-t)³, the foot grows long. At most a quarter of the height shorter.
    /// </summary>
    public static SplineMeshParams Legs(BoundingBox meshBounds, FVector scale, float legs)
    {
        var sz = MathF.Abs(scale.Z);
        var bottom = meshBounds.Min.Z * sz;
        var top = meshBounds.Max.Z * sz;
        var height = top - bottom;
        var extra = MathF.Max(legs, -0.25f * height);
        var cross = new Vector2(scale.X, scale.Y);

        // Forward along Z needs an up direction across it; with a quarter roll the mesh's X and Y stay where they were.
        return new SplineMeshParams
        {
            StartPos = new FVector(0f, 0f, bottom - extra),
            EndPos = new FVector(0f, 0f, top),
            StartTangent = FVector.Up * (height + (3f * extra)),
            EndTangent = FVector.Up * height,
            StartScale = cross,
            EndScale = cross,
            StartRoll = -MathF.PI / 2f,
            EndRoll = -MathF.PI / 2f,
            SplineUpDir = FVector.Forward,
            ForwardAxis = SplineMeshAxis.Z,
            SplineBoundaryMin = meshBounds.Min.Z,
            SplineBoundaryMax = meshBounds.Max.Z,
        };
    }

    /// <summary>Length of one straight piece: the mesh along its longer horizontal axis times the actor's scale on it, cm.</summary>
    public static float PieceLength(BoundingBox meshBounds, FVector scale)
    {
        var x = meshBounds.Size.X * MathF.Abs(scale.X);
        var y = meshBounds.Size.Y * MathF.Abs(scale.Y);
        return MathF.Max(x, y);
    }

    /// <summary>The curve of <see cref="For(BoundingBox, FVector, float, float, float)"/> without handle pushes.</summary>
    public static SplineMeshParams For(BoundingBox meshBounds, FVector scale, float degrees)
    {
        if (meshBounds.IsEmpty || scale.X == 0f || scale.Y == 0f || scale.Z == 0f)
        {
            throw new ArgumentException("A bend needs a mesh with bounds and a non-zero scale.", nameof(meshBounds));
        }

        // The longer horizontal axis follows the curve. With Y along it, a quarter roll keeps the mesh upright.
        var alongY = meshBounds.Size.Y * MathF.Abs(scale.Y) > meshBounds.Size.X * MathF.Abs(scale.X);
        var forward = alongY ? FVector.Right : FVector.Forward;
        var right = alongY ? new FVector(-1f, 0f, 0f) : FVector.Right;
        var (min, max, along) = alongY ? (meshBounds.Min.Y, meshBounds.Max.Y, scale.Y) : (meshBounds.Min.X, meshBounds.Max.X, scale.X);
        var length = (max - min) * along;
        var middle = forward * ((min + max) * 0.5f * along);
        var theta = Math.Clamp(degrees, -MaxDegrees, MaxDegrees) * MathF.PI / 180f;

        FVector startPos, endPos, startTangent, endTangent;
        if (MathF.Abs(theta) < 1e-4f)
        {
            startPos = middle - (forward * (length * 0.5f));
            endPos = middle + (forward * (length * 0.5f));
            startTangent = endTangent = forward * length;
        }
        else
        {
            var radius = length / theta; // signed: the centre is on the right for a right turn
            var half = theta * 0.5f;
            var (sin, cos) = MathF.SinCos(half);
            var ahead = forward * (radius * sin);
            var aside = right * (radius * (1f - cos));
            startPos = middle - ahead + aside;
            endPos = middle + ahead + aside;
            var tangentLength = 4f * radius * MathF.Tan(theta * 0.25f);
            startTangent = ((forward * cos) - (right * sin)) * tangentLength;
            endTangent = ((forward * cos) + (right * sin)) * tangentLength;
        }

        return new SplineMeshParams
        {
            StartPos = startPos,
            EndPos = endPos,
            StartTangent = startTangent,
            EndTangent = endTangent,
            StartScale = alongY ? new Vector2(scale.Z, scale.X) : new Vector2(scale.Y, scale.Z),
            EndScale = alongY ? new Vector2(scale.Z, scale.X) : new Vector2(scale.Y, scale.Z),
            StartRoll = alongY ? -MathF.PI / 2f : 0f,
            EndRoll = alongY ? -MathF.PI / 2f : 0f,
            ForwardAxis = alongY ? SplineMeshAxis.Y : SplineMeshAxis.X,
            SplineBoundaryMin = min,
            SplineBoundaryMax = max,
        };
    }
}
