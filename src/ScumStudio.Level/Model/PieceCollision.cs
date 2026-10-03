using System.Numerics;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Level.Model;

/// <summary>
/// Collision for pieces drawn along a spline (bent, longer, repeated, longer legs). The game builds none for a
/// <c>SplineMeshActor</c> placed by a mod (the editor cooks it; a cooked game only reads it), so the exporter writes a
/// <c>BodySetup</c> of boxes, which need no cooked data (owner: "the new bridge has no collision, the car falls through").
/// The boxes follow the mesh's own collision: its simple shapes, or, for meshes that collide with their triangles (bridge
/// decks, roads), solid columns of the triangles seen from above; then they are cut along the curve and bent with it.
/// </summary>
public static class PieceCollision
{
    /// <summary>Most boxes one piece gets (coarser columns past it).</summary>
    public const int MaxBoxes = 1024;

    /// <summary>
    /// Boxes in the mesh's own space following its collision: <paramref name="simple"/>'s shapes when the mesh has any and
    /// does not collide with its triangles, otherwise <see cref="Columns"/> of <paramref name="mesh"/>.
    /// </summary>
    public static IReadOnlyList<CollisionBox> MeshBoxes(MeshData mesh, MeshCollisionInfo? simple)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return simple is { Boxes.Count: > 0 } && simple.TraceFlag != "CTF_UseComplexAsSimple" ? simple.Boxes : Columns(mesh);
    }

    /// <summary>
    /// The triangles as boxes: a grid over the mesh seen from above, a vertical line through each cell, each solid stretch
    /// along it (between a way in and a way out; a lone surface counts 15 cm thick) a box; side by side cells with the same
    /// stretches merge. Railings stand up from the deck, cables hang thin above it: nothing blocks where the mesh is open.
    /// </summary>
    /// <remarks>ponytail: columns are vertical; an overhang that is open underneath reads right, a slanted wall as steps of its cell size.</remarks>
    public static List<CollisionBox> Columns(MeshData mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var b = mesh.Bounds;
        if (b.IsEmpty || mesh.TriangleCount == 0)
        {
            return [];
        }

        // Cells of at most 1.5 m (a box reaches past an open edge by half a cell at most), coarser only past MaxBoxes.
        var cell = Math.Clamp(MathF.Max(b.Size.X, b.Size.Y) / 48f, 25f, 150f);
        for (var tries = 0; ; tries++, cell *= 2f)
        {
            var boxes = ColumnsAt(mesh, cell);
            if (boxes.Count <= MaxBoxes || tries >= 6)
            {
                return boxes;
            }
        }
    }

    private static List<CollisionBox> ColumnsAt(MeshData mesh, float cell)
    {
        var b = mesh.Bounds;
        var nx = Math.Max(1, (int)MathF.Ceiling(b.Size.X / cell));
        var ny = Math.Max(1, (int)MathF.Ceiling(b.Size.Y / cell));

        // Triangles binned by the cells their footprint covers.
        var bins = new List<int>?[nx * ny];
        var p = mesh.Positions;
        var idx = mesh.Indices;
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            var (i0, i1, i2) = ((int)idx[t] * 3, (int)idx[t + 1] * 3, (int)idx[t + 2] * 3);
            var minX = MathF.Min(p[i0], MathF.Min(p[i1], p[i2]));
            var maxX = MathF.Max(p[i0], MathF.Max(p[i1], p[i2]));
            var minY = MathF.Min(p[i0 + 1], MathF.Min(p[i1 + 1], p[i2 + 1]));
            var maxY = MathF.Max(p[i0 + 1], MathF.Max(p[i1 + 1], p[i2 + 1]));
            var (cx0, cx1) = (Math.Clamp((int)((minX - b.Min.X) / cell), 0, nx - 1), Math.Clamp((int)((maxX - b.Min.X) / cell), 0, nx - 1));
            var (cy0, cy1) = (Math.Clamp((int)((minY - b.Min.Y) / cell), 0, ny - 1), Math.Clamp((int)((maxY - b.Min.Y) / cell), 0, ny - 1));
            for (var y = cy0; y <= cy1; y++)
            {
                for (var x = cx0; x <= cx1; x++)
                {
                    (bins[(y * nx) + x] ??= []).Add(t);
                }
            }
        }

        // Solid stretches up each cell's middle (nudged off the grid so a line never runs along an edge).
        var stretches = new List<(float Low, float High)>[nx * ny];
        var hits = new List<(float Z, bool In)>();
        for (var y = 0; y < ny; y++)
        {
            for (var x = 0; x < nx; x++)
            {
                hits.Clear();
                var px = b.Min.X + ((x + 0.5f) * cell) + 0.0137f;
                var py = b.Min.Y + ((y + 0.5f) * cell) + 0.0291f;
                foreach (var t in bins[(y * nx) + x] ?? [])
                {
                    if (Hit(p, idx, t, px, py) is { } hit)
                    {
                        hits.Add(hit);
                    }
                }

                stretches[(y * nx) + x] = Solid(hits);
            }
        }

        // Merge along X with the same stretches, then rows along Y with the same run.
        var runs = new List<(int X0, int X1, int Y0, int Y1, float Low, float High)>();
        var open = new Dictionary<(int X0, int X1, float Low, float High), int>();
        for (var y = 0; y < ny; y++)
        {
            var current = new Dictionary<(int X0, int X1, float Low, float High), int>();
            for (var x = 0; x < nx;)
            {
                var here = stretches[(y * nx) + x];
                var end = x + 1;
                while (end < nx && Same(stretches[(y * nx) + end], here))
                {
                    end++;
                }

                foreach (var (low, high) in here)
                {
                    var key = (x, end - 1, Round(low), Round(high));
                    if (open.TryGetValue(key, out var run))
                    {
                        runs[run] = runs[run] with { Y1 = y };
                        current[key] = run;
                    }
                    else
                    {
                        runs.Add((x, end - 1, y, y, low, high));
                        current[key] = runs.Count - 1;
                    }
                }

                x = end;
            }

            open = current;
        }

        // Each box trimmed to the mesh: nothing solid past its outer edges.
        return runs.Select(r =>
        {
            var x0 = MathF.Max(b.Min.X + (r.X0 * cell), b.Min.X);
            var x1 = MathF.Min(b.Min.X + ((r.X1 + 1) * cell), b.Max.X);
            var y0 = MathF.Max(b.Min.Y + (r.Y0 * cell), b.Min.Y);
            var y1 = MathF.Min(b.Min.Y + ((r.Y1 + 1) * cell), b.Max.Y);
            return new CollisionBox(new FVector((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (r.Low + r.High) * 0.5f), FRotator.Zero,
                new FVector(x1 - x0, y1 - y0, MathF.Max(r.High - r.Low, 5f)));
        }).ToList();

        static float Round(float v) => MathF.Round(v / 5f) * 5f;

        static bool Same(List<(float Low, float High)> a, List<(float Low, float High)> c) =>
            a.Count == c.Count && a.Zip(c).All(z => MathF.Abs(z.First.Low - z.Second.Low) < 5f && MathF.Abs(z.First.High - z.Second.High) < 5f);
    }

    /// <summary>
    /// Where a vertical line at (x, y) crosses triangle <paramref name="t"/>, and whether going up it goes in there (the
    /// triangle looks down; the mesh's triangles are clockwise seen from the front, <see cref="MeshExtractor"/>), or null.
    /// </summary>
    private static (float Z, bool In)? Hit(float[] p, uint[] idx, int t, float x, float y)
    {
        var (i0, i1, i2) = ((int)idx[t] * 3, (int)idx[t + 1] * 3, (int)idx[t + 2] * 3);
        var d = ((p[i1 + 1] - p[i2 + 1]) * (p[i0] - p[i2])) + ((p[i2] - p[i1]) * (p[i0 + 1] - p[i2 + 1]));
        if (MathF.Abs(d) < 1e-6f)
        {
            return null; // seen edge-on from above (a wall): the stretches around it come from its other faces
        }

        var w0 = (((p[i1 + 1] - p[i2 + 1]) * (x - p[i2])) + ((p[i2] - p[i1]) * (y - p[i2 + 1]))) / d;
        var w1 = (((p[i2 + 1] - p[i0 + 1]) * (x - p[i2])) + ((p[i0] - p[i2]) * (y - p[i2 + 1]))) / d;
        var w2 = 1f - w0 - w1;
        return w0 < 0f || w1 < 0f || w2 < 0f ? null : ((w0 * p[i0 + 2]) + (w1 * p[i1 + 2]) + (w2 * p[i2 + 2]), d > 0f);
    }

    /// <summary>
    /// Solid stretches from the crossings of a vertical line, bottom up: each way out (a face looking up) closes the last
    /// open way in (a face looking down); a way out with none open is a 15 cm slab under it, a way in never closed one over
    /// it; overlapping stretches join. Counting crossings in pairs instead filled a bridge from its road up to its pylon
    /// where the road surface is a sheet of its own (owner: "the pylon is like a hidden wall, the car can't pass").
    /// </summary>
    private static List<(float Low, float High)> Solid(List<(float Z, bool In)> hits)
    {
        // At one height a way in first: a post on a deck stays solid down into it, a two-sided sheet is a slab.
        hits.Sort((a, c) => a.Z != c.Z ? a.Z.CompareTo(c.Z) : c.In.CompareTo(a.In));
        var stretches = new List<(float Low, float High)>();
        var open = new Stack<float>();
        foreach (var (z, @in) in hits)
        {
            if (@in)
            {
                open.Push(z);
            }
            else
            {
                var from = open.Count > 0 ? open.Pop() : z;
                stretches.Add(z - from < 1f ? (z - Slab, z) : (from, z));
            }
        }

        stretches.AddRange(open.Select(z => (z, z + Slab)));
        stretches.Sort();
        var solid = new List<(float Low, float High)>();
        foreach (var (low, high) in stretches)
        {
            if (solid.Count > 0 && low <= solid[^1].High)
            {
                solid[^1] = (solid[^1].Low, MathF.Max(solid[^1].High, high));
            }
            else
            {
                solid.Add((low, high));
            }
        }

        return solid;
    }

    private const float Slab = 15f; // cm: a lone surface's thickness

    /// <summary>
    /// <paramref name="boxes"/> (mesh space) bent along <paramref name="spline"/> as the game bends the mesh: every box cut
    /// at the same places along the curve (<see cref="Cuts"/>), each piece spanning the bent places of its two ends, turned
    /// and scaled like the slice at its middle, and reaching past its cut ends far enough that the next piece's corner
    /// never leaves a gap (component space). Owner: "the car's wheel drops then comes back, the road should be level".
    /// </summary>
    public static List<CollisionBox> Bend(IReadOnlyList<CollisionBox> boxes, SplineMeshParams spline, BoundingBox meshBounds, float degreesPerPiece = 4f)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(spline);
        var axis = spline.ForwardAxis switch { SplineMeshAxis.Y => FVector.Right, SplineMeshAxis.Z => FVector.Up, _ => FVector.Forward };
        var k = Dominant(axis);
        var (start, end) = (Get(Vec(meshBounds.Min), k), Get(Vec(meshBounds.Max), k));
        var cuts = Cuts(spline, meshBounds, start, end, degreesPerPiece);
        var bent = new List<CollisionBox>();
        foreach (var box in boxes)
        {
            var rotation = box.Rotation.Quaternion();
            var along = LongAxis(rotation, axis);
            var length = Get(box.Size, along);
            var axisInMesh = rotation.RotateVector(Unit(along));
            var a0 = FVector.Dot(box.Center - (axisInMesh * (length * 0.5f)), axis);
            var a1 = FVector.Dot(box.Center + (axisInMesh * (length * 0.5f)), axis);
            var parts = new List<float> { 0f };
            if (MathF.Abs(a1 - a0) > 1f)
            {
                parts.AddRange(cuts.Select(c => (c - a0) / (a1 - a0)).Where(f => f is > 0.001f and < 0.999f).Order());
            }

            parts.Add(1f);
            for (var i = 0; i + 1 < parts.Count; i++)
            {
                var c0 = box.Center + (axisInMesh * ((parts[i] - 0.5f) * length));
                var c1 = box.Center + (axisInMesh * ((parts[i + 1] - 0.5f) * length));
                var mid = (c0 + c1) * 0.5f;

                // Where the game puts the two ends of this part of the mesh; the box spans them, turned like the slice between.
                var p0 = Place(spline, meshBounds, axis, c0);
                var p1 = Place(spline, meshBounds, axis, c1);
                var slice = SplineMeshDeformer.CalcSliceTransform(spline, meshBounds, FVector.Dot(mid, axis));
                var scale = slice.Scale3D;
                var stretched = new FVector(MathF.Abs(scale.X), MathF.Abs(scale.Y), MathF.Abs(scale.Z));
                var chord = FVector.Distance(p0, p1);
                var size = Stretch(Set(box.Size, along, length * (parts[i + 1] - parts[i])), rotation, stretched, axis, chord);

                // The box's ends stand square to its middle, the mesh's turned by half the part's turn: each end reaches as
                // far as its corners swing (inside the mesh twice that and 2 cm, so the parts overlap where the curve turns).
                // A turn sideways swings the corners by the box's width, a turn up or down by its thickness only (no more,
                // or the reach stands proud of the road over a hump).
                var relative = SplineMeshDeformer.CalcSliceTransform(spline, meshBounds, FVector.Dot(c0, axis)).Rotation.Inverse()
                    * SplineMeshDeformer.CalcSliceTransform(spline, meshBounds, FVector.Dot(c1, axis)).Rotation;
                var lever = FVector.Cross(axis, relative.GetRotationAxis());
                var tilt = ((Cross(size, along, 0) * MathF.Abs(FVector.Dot(rotation.RotateVector(Unit((along + 1) % 3)), lever)))
                    + (Cross(size, along, 1) * MathF.Abs(FVector.Dot(rotation.RotateVector(Unit((along + 2) % 3)), lever))))
                    * 0.5f * MathF.Sin(MathF.Acos(Math.Clamp(MathF.Abs(relative.W), 0f, 1f)));
                var reach0 = Inside(FVector.Dot(c0, axis)) ? (2f * tilt) + 2f : tilt;
                var reach1 = Inside(FVector.Dot(c1, axis)) ? (2f * tilt) + 2f : tilt;
                var direction = chord > 0.01f ? (p1 - p0) * (1f / chord) : FVector.Zero;
                var center = ((p0 + p1) * 0.5f) + (direction * ((reach1 - reach0) * 0.5f));
                bent.Add(new CollisionBox(center, (slice.Rotation * rotation).Rotator(), Set(size, along, Get(size, along) + reach0 + reach1)));
            }
        }

        return bent;

        bool Inside(float a) => a > MathF.Min(start, end) + 1f && a < MathF.Max(start, end) - 1f;
    }

    /// <summary>
    /// Where along the mesh (its forward axis, from <paramref name="start"/> to <paramref name="end"/>) the boxes are cut:
    /// a part may turn at most <paramref name="degreesPerPiece"/> degrees, its straight chord may stray at most 1 cm from
    /// the curve (a box's flat top sagged under a hump: the wheel dropped), and the road's edges at most 0.5 cm from its
    /// flat top (a curve that climbs twists across). None where the piece runs straight.
    /// </summary>
    /// <remarks>ponytail: 128 samples along the mesh; a very long, wide piece that climbs round a tight curve keeps up to a centimetre or two at its edges.</remarks>
    private static List<float> Cuts(SplineMeshParams spline, BoundingBox meshBounds, float start, float end, float degreesPerPiece)
    {
        const int Samples = 128;
        var at = new float[Samples + 1];
        var place = new FVector[Samples + 1];
        var turn = new FQuat[Samples + 1];
        var edge = new float[Samples + 1];

        // Across and up of the mesh: a road lies along X or Y with Z up (legs, along Z, are not driven on).
        var (across, up) = spline.ForwardAxis switch { SplineMeshAxis.X => (1, 2), SplineMeshAxis.Y => (0, 2), _ => (-1, 2) };
        var half = across < 0 ? 0f : MathF.Max(MathF.Abs(Get(Vec(meshBounds.Min), across)), MathF.Abs(Get(Vec(meshBounds.Max), across)));
        for (var i = 0; i <= Samples; i++)
        {
            at[i] = start + ((end - start) * i / Samples);
            var slice = SplineMeshDeformer.CalcSliceTransform(spline, meshBounds, at[i]);
            (place[i], turn[i]) = (slice.Translation, slice.Rotation);
            edge[i] = across < 0 ? 0f : half * MathF.Abs(Get(slice.Scale3D, across));
        }

        var cuts = new List<float>();
        for (var from = 0; from < Samples;)
        {
            var to = from + 1;
            while (to < Samples && Fits(from, to + 1))
            {
                to++;
            }

            if (to < Samples)
            {
                cuts.Add(at[to]);
            }

            from = to;
        }

        return cuts;

        bool Fits(int from, int to)
        {
            if (Angle(turn[from], turn[to]) > degreesPerPiece)
            {
                return false;
            }

            var chord = place[to] - place[from];
            var length = chord.Size();
            var top = SplineMeshDeformer.CalcSliceTransform(spline, meshBounds, (at[from] + at[to]) * 0.5f).Rotation.RotateVector(Unit(up));
            for (var i = from; i <= to; i++)
            {
                if (edge[i] * MathF.Abs(FVector.Dot(turn[i].RotateVector(Unit(Math.Max(across, 0))), top)) > 0.5f)
                {
                    return false;
                }

                var off = place[i] - place[from];
                if (length > 0.01f && FVector.Distance(off, chord * (FVector.Dot(off, chord) / (length * length))) > 1f)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private static FVector Vec(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>The box's size across the curve: its <paramref name="n"/>-th axis that is not <paramref name="along"/>.</summary>
    private static float Cross(FVector size, int along, int n) => Get(size, (along + 1 + n) % 3);

    /// <summary>Where the game draws the mesh point <paramref name="point"/> (mesh space) on the curve.</summary>
    private static FVector Place(SplineMeshParams spline, BoundingBox meshBounds, FVector axis, FVector point)
    {
        var a = FVector.Dot(point, axis);
        return SplineMeshDeformer.CalcSliceTransform(spline, meshBounds, a).TransformPosition(point - (axis * a));
    }

    /// <summary>Angle between two turns, degrees.</summary>
    private static float Angle(FQuat a, FQuat b) => a.AngularDistance(b) * 180f / MathF.PI;

    /// <summary>The box's size with the cross-section scale of the slice and the bent length along the curve.</summary>
    private static FVector Stretch(FVector size, FQuat rotation, FVector sliceScale, FVector axis, float bentLength)
    {
        // Each box axis takes the scale of the mesh axis it mostly lies along; along the curve it takes the bent length.
        var result = size;
        for (var k = 0; k < 3; k++)
        {
            var boxAxis = rotation.RotateVector(Unit(k));
            var meshAxis = Dominant(boxAxis);
            result = Set(result, k, Get(size, k) * Get(sliceScale, meshAxis));
            if (MathF.Abs(FVector.Dot(boxAxis, axis)) > 0.7f)
            {
                result = Set(result, k, MathF.Max(bentLength, 1f));
            }
        }

        return result;
    }

    /// <summary>The box's own axis (0-2) that lies most along <paramref name="forward"/>.</summary>
    private static int LongAxis(FQuat rotation, FVector forward)
    {
        var best = 0;
        var bestDot = -1f;
        for (var k = 0; k < 3; k++)
        {
            var d = MathF.Abs(FVector.Dot(rotation.RotateVector(Unit(k)), forward));
            if (d > bestDot)
            {
                (best, bestDot) = (k, d);
            }
        }

        return best;
    }

    private static int Dominant(FVector v) =>
        MathF.Abs(v.X) >= MathF.Abs(v.Y) && MathF.Abs(v.X) >= MathF.Abs(v.Z) ? 0 : MathF.Abs(v.Y) >= MathF.Abs(v.Z) ? 1 : 2;

    private static FVector Unit(int k) => k switch { 1 => FVector.Right, 2 => FVector.Up, _ => FVector.Forward };

    private static float Get(FVector v, int k) => k switch { 1 => v.Y, 2 => v.Z, _ => v.X };

    private static FVector Set(FVector v, int k, float value) => k switch { 1 => v with { Y = value }, 2 => v with { Z = value }, _ => v with { X = value } };
}
