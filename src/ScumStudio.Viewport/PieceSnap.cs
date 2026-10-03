using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;

namespace ScumStudio.Viewport;

/// <summary>A piece the snap can join: its mesh, where it stands (UE world) and the mesh's own bounds (UE, component space).</summary>
/// <param name="Mesh">Mesh object path (joints are learned per pair of meshes).</param>
/// <param name="World">World transform of the component drawing the mesh.</param>
/// <param name="Bounds">The mesh's bounds in its own space.</param>
/// <param name="Geometry">The mesh's triangles (own space), to stand things on its real surface; null = on its box.</param>
public readonly record struct SnapPiece(string Mesh, FTransform World, BoundingBox Bounds, MeshData? Geometry = null);

/// <summary>
/// Piece-to-piece snapping (owner: "a wall or tunnel piece brought next to another should know its place and stick"):
/// a dragged piece joins a nearby one end to end, turned the same way so the line continues straight, at the end it is
/// closest to; or lies alongside it, side touching side with the tops flush (roads next to each other, no step for a
/// car); or, when it is narrow (a fence, a kerb, a walkway), stands on its surface along either edge. A way two meshes
/// were joined by hand before (a pillar under a bridge, a tunnel mouth at an angle) is learned and offered for the same
/// pair again. A piece facing the other way along the line keeps facing that way. Pure math, UE space.
/// </summary>
public static class PieceSnap
{
    /// <summary>
    /// The centres of a piece's two end faces along its length (its longer horizontal axis), in its own space, and that axis
    /// (true = Y).
    /// </summary>
    public static (FVector Start, FVector End, bool AlongY) Ends(BoundingBox bounds)
    {
        var c = bounds.Center;
        return bounds.Size.Y > bounds.Size.X
            ? (new FVector(c.X, bounds.Min.Y, c.Z), new FVector(c.X, bounds.Max.Y, c.Z), true)
            : (new FVector(bounds.Min.X, c.Y, c.Z), new FVector(bounds.Max.X, c.Y, c.Z), false);
    }

    /// <summary>
    /// Where <paramref name="moving"/> would go next to <paramref name="target"/>: its start at the target's end and its end at
    /// the target's start (each turned like the target, a quarter turn more when their lengths run along different axes),
    /// then every learned joint (moving relative to target) applied to the target.
    /// </summary>
    public static IEnumerable<FTransform> Candidates(SnapPiece moving, SnapPiece target, IEnumerable<FTransform>? learned = null)
    {
        var (movingStart, movingEnd, movingY) = Ends(moving.Bounds);
        var (targetStart, targetEnd, targetY) = Ends(target.Bounds);
        var turn = movingY == targetY ? FQuat.Identity : FQuat.FromAxisAngle(FVector.Up, (targetY ? 1f : -1f) * MathF.PI / 2f);
        var rotation = target.World.Rotation * turn;
        var along = movingY ? FVector.Right : FVector.Forward;
        var flip = FVector.Dot(moving.World.Rotation.RotateVector(along), rotation.RotateVector(along)) < 0f;
        if (flip)
        {
            rotation *= FQuat.FromAxisAngle(FVector.Up, MathF.PI);
        }

        var scale = moving.World.Scale3D;

        FTransform Joined(FVector movingAnchor, FVector targetAnchor) =>
            new(rotation, target.World.TransformPosition(targetAnchor) - rotation.RotateVector(scale * movingAnchor), scale);

        yield return Joined(flip ? movingEnd : movingStart, targetEnd);
        yield return Joined(flip ? movingStart : movingEnd, targetStart);
        foreach (var side in Alongside(moving, target, rotation, targetY))
        {
            yield return side;
        }

        foreach (var joint in learned ?? [])
        {
            yield return (joint with { Scale3D = FVector.One }) * (target.World with { Scale3D = FVector.One }) with { Scale3D = scale };
        }
    }

    /// <summary>
    /// Places beside <paramref name="target"/> (turned <paramref name="rotation"/>): side against side with the tops flush,
    /// and for a narrow piece on the target's surface along each edge. Only where the two overlap along the target's
    /// length (otherwise the end joints apply); the place along it is where the piece is now, or lined up with the
    /// target's start or end when within 1.5 m of it.
    /// </summary>
    private static IEnumerable<FTransform> Alongside(SnapPiece moving, SnapPiece target, FQuat rotation, bool targetY)
    {
        // Target space: the target's own axes in cm (its scale applied), origin at its root.
        var relative = target.World.Rotation.Inverse() * rotation;
        var (mMin, mMax) = Box(moving.Bounds, p => relative.RotateVector(moving.World.Scale3D * p));
        var (tMin, tMax) = Box(target.Bounds, p => target.World.Scale3D * p);
        var o = target.World.Rotation.UnrotateVector(moving.World.Translation - target.World.Translation);
        int a = targetY ? 1 : 0, l = targetY ? 0 : 1;
        var overlap = MathF.Min(Get(o, a) + Get(mMax, a), Get(tMax, a)) - MathF.Max(Get(o, a) + Get(mMin, a), Get(tMin, a));
        if (overlap < 0.25f * MathF.Min(Get(mMax, a) - Get(mMin, a), Get(tMax, a) - Get(tMin, a)))
        {
            yield break;
        }

        var at = Get(o, a);
        if (MathF.Abs(at + Get(mMin, a) - Get(tMin, a)) < 150f)
        {
            at = Get(tMin, a) - Get(mMin, a);
        }
        else if (MathF.Abs(at + Get(mMax, a) - Get(tMax, a)) < 150f)
        {
            at = Get(tMax, a) - Get(mMax, a);
        }

        FTransform Place(float lateral, float up)
        {
            var local = Set(Set(new FVector(0f, 0f, up), a, at), l, lateral);
            return new FTransform(rotation, target.World.Translation + target.World.Rotation.RotateVector(local), moving.World.Scale3D);
        }

        // Side against side, tops flush.
        yield return Place(Get(tMax, l) - Get(mMin, l), tMax.Z - mMax.Z);
        yield return Place(Get(tMin, l) - Get(mMax, l), tMax.Z - mMax.Z);

        // Narrow: standing on the surface at each edge, its outer side flush with the target's.
        if (Get(mMax, l) - Get(mMin, l) < 0.5f * (Get(tMax, l) - Get(tMin, l)))
        {
            foreach (var lateral in new[] { Get(tMax, l) - Get(mMax, l), Get(tMin, l) - Get(mMin, l) })
            {
                var centre = Set(Set(FVector.Zero, a, at + ((Get(mMin, a) + Get(mMax, a)) * 0.5f)), l, lateral + ((Get(mMin, l) + Get(mMax, l)) * 0.5f));
                var surface = SurfaceAt(target, centre) ?? tMax.Z;
                yield return Place(lateral, surface - mMin.Z);
            }
        }
    }

    /// <summary>
    /// The height (target space, cm) of the target's top surface over a point, from its triangles (the highest one
    /// covering it, so a deck rather than the water under a bridge); null without geometry or when nothing covers it.
    /// </summary>
    private static float? SurfaceAt(SnapPiece target, FVector point)
    {
        if (target.Geometry is not { } mesh)
        {
            return null;
        }

        // ponytail: every triangle each frame of a drag; a grid over the mesh if big meshes make drags stutter.
        var s = target.World.Scale3D;
        var (x, y) = (point.X / s.X, point.Y / s.Y);
        var p = mesh.Positions;
        float? best = null;
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            int i0 = (int)mesh.Indices[i] * 3, i1 = (int)mesh.Indices[i + 1] * 3, i2 = (int)mesh.Indices[i + 2] * 3;
            var d = ((p[i1 + 1] - p[i2 + 1]) * (p[i0] - p[i2])) + ((p[i2] - p[i1]) * (p[i0 + 1] - p[i2 + 1]));
            if (MathF.Abs(d) < 1e-6f)
            {
                continue;
            }

            var w0 = (((p[i1 + 1] - p[i2 + 1]) * (x - p[i2])) + ((p[i2] - p[i1]) * (y - p[i2 + 1]))) / d;
            var w1 = (((p[i2 + 1] - p[i0 + 1]) * (x - p[i2])) + ((p[i0] - p[i2]) * (y - p[i2 + 1]))) / d;
            var w2 = 1f - w0 - w1;
            if (w0 < 0f || w1 < 0f || w2 < 0f)
            {
                continue;
            }

            var z = ((w0 * p[i0 + 2]) + (w1 * p[i1 + 2]) + (w2 * p[i2 + 2])) * s.Z;
            best = best is { } b ? MathF.Max(b, z) : z;
        }

        return best;
    }

    private static (FVector Min, FVector Max) Box(BoundingBox bounds, Func<FVector, FVector> map)
    {
        var (min, max) = (new FVector(float.MaxValue), new FVector(float.MinValue));
        for (var i = 0; i < 8; i++)
        {
            var c = map(new FVector((i & 1) == 0 ? bounds.Min.X : bounds.Max.X, (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z));
            (min, max) = (FVector.Min(min, c), FVector.Max(max, c));
        }

        return (min, max);
    }

    private static float Get(FVector v, int axis) => axis == 0 ? v.X : v.Y;

    private static FVector Set(FVector v, int axis, float value) => axis == 0 ? v with { X = value } : v with { Y = value };

    /// <summary>
    /// The two ends of a straight piece as weld targets (world): the middle of each end face at the mesh's own height,
    /// the way out of the piece there, and its width.
    /// </summary>
    public static EndSection[] EndSections(SnapPiece piece)
    {
        var (start, end, alongY) = Ends(piece.Bounds);
        var c = piece.Bounds.Center;
        var width = alongY ? piece.Bounds.Size.X * MathF.Abs(piece.World.Scale3D.X) : piece.Bounds.Size.Y * MathF.Abs(piece.World.Scale3D.Y);
        var middle = piece.World.TransformPosition(new FVector(c.X, c.Y, 0f));

        EndSection At(FVector face)
        {
            var point = piece.World.TransformPosition(face with { Z = 0f });
            var outward = (point - middle).GetSafeNormal();
            var side = FVector.Cross(FVector.Up, outward).GetSafeNormal();
            return new EndSection(point, point - (side * (width * 0.5f)), point + (side * (width * 0.5f)), outward, width);
        }

        return [At(start), At(end)];
    }

    /// <summary>
    /// The candidate closest to where <paramref name="moving"/> is now, among all <paramref name="targets"/>, when it lies
    /// within <paramref name="reach"/> (cm); otherwise null. Returns the target joined too.
    /// </summary>
    public static (FTransform World, SnapPiece Target)? Best(SnapPiece moving, IEnumerable<SnapPiece> targets, float reach, Func<SnapPiece, IEnumerable<FTransform>>? learned = null)
    {
        (FTransform, SnapPiece)? best = null;
        var bestDistance = reach;
        foreach (var target in targets)
        {
            foreach (var candidate in Candidates(moving, target, learned?.Invoke(target)))
            {
                var distance = FVector.Distance(candidate.Translation, moving.World.Translation);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (candidate, target);
                }
            }
        }

        return best;
    }

    /// <summary>How far a piece may be from a joint and still snap: a quarter of its length, at least 1.5 m.</summary>
    public static float Reach(SnapPiece piece)
    {
        var size = piece.Bounds.Size;
        return MathF.Max(150f, 0.25f * MathF.Max(size.X * MathF.Abs(piece.World.Scale3D.X), size.Y * MathF.Abs(piece.World.Scale3D.Y)));
    }

    /// <summary>
    /// When <paramref name="moving"/> was dropped touching <paramref name="target"/> (their world boxes within
    /// <paramref name="touch"/> cm), the joint to remember (moving relative to target, scale left out); else null.
    /// </summary>
    public static FTransform? JointOf(SnapPiece moving, SnapPiece target, float touch = 50f)
    {
        var a = WorldBox(moving);
        var b = WorldBox(target);
        var gap = new FVector(
            MathF.Max(0f, MathF.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X)),
            MathF.Max(0f, MathF.Max(a.Min.Y - b.Max.Y, b.Min.Y - a.Max.Y)),
            MathF.Max(0f, MathF.Max(a.Min.Z - b.Max.Z, b.Min.Z - a.Max.Z)));
        return gap.Size() <= touch ? (moving.World with { Scale3D = FVector.One }) * (target.World with { Scale3D = FVector.One }).Inverse() : null;
    }

    /// <summary>The axis-aligned world box around a piece (its eight corners transformed).</summary>
    public static BoundingBox WorldBox(SnapPiece piece)
    {
        var box = BoundingBox.Empty;
        var (min, max) = (piece.Bounds.Min, piece.Bounds.Max);
        for (var i = 0; i < 8; i++)
        {
            var corner = piece.World.TransformPosition(new FVector((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
            box = box.Union(new BoundingBox(new System.Numerics.Vector3(corner.X, corner.Y, corner.Z), new System.Numerics.Vector3(corner.X, corner.Y, corner.Z)));
        }

        return box;
    }
}
