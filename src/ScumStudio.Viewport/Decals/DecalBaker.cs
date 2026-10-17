using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;

namespace ScumStudio.Viewport.Decals;

/// <summary>A decal placed in the vehicle's space, ready to bake (one per layer, two when it is mirrored).</summary>
/// <param name="Image">The decal in colour.</param>
/// <param name="Position">Centre (vehicle space, cm).</param>
/// <param name="Normal">Projection direction: the decal is pressed onto the surface along −normal (unit).</param>
/// <param name="Up">Up on the decal before <paramref name="RotationDeg"/>.</param>
/// <param name="WidthCm">Width, cm.</param>
/// <param name="HeightCm">Height, cm.</param>
/// <param name="RotationDeg">Turn about the normal, degrees.</param>
/// <param name="FlipX">Mirrored left to right.</param>
/// <param name="Opacity">0..1.</param>
/// <param name="Depth">How far along the normal it reaches each way (cm); 0: half its smaller side (a sticker on one panel).</param>
public sealed record DecalStamp(DecalImage Image, Vector3 Position, Vector3 Normal, Vector3 Up, float WidthCm, float HeightCm, float RotationDeg, bool FlipX, float Opacity, float Depth = 0f);

/// <summary>A bake's textures and the parts that show the decals somewhere else too (they share its texels).</summary>
/// <param name="Diffuse">The diffuse with the decals (sRGB), in its UV0 layout.</param>
/// <param name="Mask">
/// The colour mask with the paint taken off under them, at the diffuse's size, in the layout of the UV channel the material
/// reads it through (<see cref="PreviewPart.MaskUv"/>: UV1 on the Laika's and the WolfsWagen's bodies).
/// </param>
/// <param name="SharedUv">Parts whose surface elsewhere uses the same texels of the diffuse or the mask (mirrored or shared UVs), by name.</param>
public sealed record DecalBakeResult(TextureImage Diffuse, TextureImage Mask, IReadOnlyList<string> SharedUv);

/// <summary>
/// Bakes decals into a material's textures the way the game will show them: each decal is projected along its normal onto
/// the triangles in its box (as deep as half its smaller side, only faces turned towards it) of the UV island it was put on
/// and of the islands beside it whose texels are their own (<see cref="PressedOn"/>), rasterised at the texture's size, so it
/// wraps round curved panels and never floats. The colour texture is
/// drawn through UV0 and the colour mask through the channel the material reads it through (<see cref="PreviewPart.MaskUv"/>;
/// report on 3.4.0: "the Laika and WolfsWagen textures aren't syncing to the body, a sticker gets distorted and moved around
/// the car", their masks lie in UV1). Over the paint it follows <see cref="PaintRule"/>: the mask loses the paint under the
/// decal and the diffuse makes up for it, whatever colour the player paints. Places elsewhere on the car that use the same
/// texels (a door mirrored onto the other one) cannot be told apart in the textures: they are named in
/// <see cref="DecalBakeResult.SharedUv"/>.
/// </summary>
public static class DecalBaker
{
    private const float MinFacing = 0.2f;
    private const int Dilation = 4;
    private const int GridCells = 64;

    private static readonly float[] ToLinearTable = Enumerable.Range(0, 256).Select(i => SrgbToLinear(i / 255f)).ToArray();

    /// <summary>The UV islands of a mesh per mask channel (triangle → island), kept while the mesh lives.</summary>
    private static readonly ConditionalWeakTable<MeshData, Dictionary<int, int[]>> IslandCache = new();

    /// <summary>The last colour-texel → mask-texel maps of <see cref="MaskInColourUv"/>.</summary>
    private static readonly List<(MeshData[] Meshes, int[] Channels, int W, int H, int Mw, int Mh, int[] Map, int[] Second)> MaskMaps = [];

    /// <summary>The stamps of <paramref name="layer"/> (on a part standing at <paramref name="partTransform"/>): itself and, when mirrored, its copy across Y = 0.</summary>
    public static IReadOnlyList<DecalStamp> Stamps(DecalLayer layer, DecalImage image, FTransform partTransform)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var position = partTransform.TransformPosition(FVector.FromVector3(layer.Position)).ToVector3();
        var normal = Vector3.Normalize(partTransform.TransformVectorNoScale(FVector.FromVector3(layer.Normal)).ToVector3());
        var up = partTransform.TransformVectorNoScale(FVector.FromVector3(layer.Up)).ToVector3();
        // Stuck to the surface: deep enough to reach every panel under it (doors, fenders, the hood), each taking its piece.
        var depth = layer.Wrap ? MathF.Max(layer.WidthCm, layer.HeightCm) : 0f;
        var stamp = new DecalStamp(image, position, normal, up, layer.WidthCm, layer.HeightCm, layer.RotationDeg, layer.FlipX, Math.Clamp(layer.Opacity, 0f, 1f), depth);
        if (!layer.Mirror)
        {
            return [stamp];
        }

        static Vector3 Reflect(Vector3 v) => new(v.X, -v.Y, v.Z);
        return [stamp, stamp with { Position = Reflect(position), Normal = Reflect(normal), Up = Reflect(up), FlipX = !layer.FlipX, RotationDeg = -layer.RotationDeg }];
    }

    /// <summary>True when one of <paramref name="stamps"/> reaches a triangle of <paramref name="parts"/> that faces it (it would show on them).</summary>
    public static bool Touches(IReadOnlyList<PreviewPart> parts, IReadOnlyList<DecalStamp> stamps)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(stamps);
        var frames = stamps.Where(s => s.Opacity > 0f && s.WidthCm > 0f && s.HeightCm > 0f).Select(Frame.Of).ToList();
        return frames.Count > 0 && Triangles(parts, 1, 1).Any(t => frames.Any(f => f.MayTouch(t)));
    }

    /// <summary>
    /// The decal's axes on the surface: across (<c>S</c>, the picture's left to right), up (<c>T</c>, its bottom to top) and
    /// the normal, from the surface <paramref name="normal"/>, the decal's <paramref name="up"/> and its turn.
    /// </summary>
    public static (Vector3 S, Vector3 T, Vector3 N) Axes(Vector3 normal, Vector3 up, float rotationDeg)
    {
        var n = Vector3.Normalize(normal);
        var u = up - (Vector3.Dot(up, n) * n);
        if (u.LengthSquared() < 1e-6f)
        {
            u = MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ - (n.Z * n) : Vector3.UnitX - (n.X * n);
        }

        var t0 = Vector3.Normalize(u);
        var s0 = Vector3.Cross(n, t0);
        var (sin, cos) = MathF.SinCos(rotationDeg * MathF.PI / 180f);
        return ((s0 * cos) + (t0 * sin), (t0 * cos) - (s0 * sin), n);
    }

    /// <summary>
    /// Bakes <paramref name="stamps"/> (bottom first) into <paramref name="diffuse"/> and <paramref name="mask"/> through the
    /// triangles of <paramref name="parts"/> (all drawn with that material), at <paramref name="size"/> pixels on the long side
    /// (0: the diffuse's own size).
    /// </summary>
    public static DecalBakeResult Bake(TextureImage diffuse, TextureImage mask, IReadOnlyList<PreviewPart> parts, IReadOnlyList<DecalStamp> stamps, int size = 0)
    {
        ArgumentNullException.ThrowIfNull(diffuse);
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(stamps);
        var clock = Stopwatch.StartNew();
        var (w, h) = size > 0 && size != Math.Max(diffuse.Width, diffuse.Height)
            ? (Math.Max(1, diffuse.Width * size / Math.Max(diffuse.Width, diffuse.Height)), Math.Max(1, diffuse.Height * size / Math.Max(diffuse.Width, diffuse.Height)))
            : (diffuse.Width, diffuse.Height);
        var colour = Resample(diffuse.Rgba, diffuse.Width, diffuse.Height, w, h);
        var paint = Resample(mask.Rgba, mask.Width, mask.Height, w, h);
        var tris = Triangles(parts, w, h);
        var separate = tris.Any(t => t.MaskApart);
        var island = Covered(tris, w, h, maskUv: false);
        var maskIsland = separate ? Covered(tris, w, h, maskUv: true) : island;
        var islands = new int[parts.Count][];

        var shared = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var layer = new float[w * h * 4]; // one stamp on the colour texture: straight linear colour + alpha
        var maskAt = new int[w * h];      // the mask texel at the same point of the car as a colour texel
        var cut = separate ? new float[w * h] : layer; // one stamp on the mask: alpha (the colour layer's when they share UV0)
        var touched = new List<int>();
        var touchedMask = separate ? new List<int>() : touched;
        foreach (var stamp in stamps.Where(s => s.Opacity > 0f && s.WidthCm > 0f && s.HeightCm > 0f))
        {
            var frame = Frame.Of(stamp);
            var on = PressedOn(tris, frame, parts, islands, w, h);
            foreach (var t in on)
            {
                Raster(t.T0, t.T1, t.T2, w, h, (i, b) =>
                {
                    if (frame.Sample((b.X * t.P0) + (b.Y * t.P1) + (b.Z * t.P2)) is not { W: > 0f } d)
                    {
                        return;
                    }

                    var a = d.W * stamp.Opacity;
                    if (layer[(i * 4) + 3] == 0f)
                    {
                        touched.Add(i);
                    }

                    if (a > layer[(i * 4) + 3])
                    {
                        (layer[i * 4], layer[(i * 4) + 1], layer[(i * 4) + 2], layer[(i * 4) + 3]) = (d.X / d.W, d.Y / d.W, d.Z / d.W, a);
                        maskAt[i] = separate ? TexelOf((b.X * t.M0) + (b.Y * t.M1) + (b.Z * t.M2), w, h) : i;
                    }
                });

                if (separate)
                {
                    Raster(t.M0, t.M1, t.M2, w, h, (j, b) =>
                    {
                        if (frame.Sample((b.X * t.P0) + (b.Y * t.P1) + (b.Z * t.P2)) is not { W: > 0f } d)
                        {
                            return;
                        }

                        var a = d.W * stamp.Opacity;
                        if (cut[j] == 0f)
                        {
                            touchedMask.Add(j);
                        }

                        cut[j] = MathF.Max(cut[j], a);
                    });
                }
            }

            if (touched.Count == 0 && touchedMask.Count == 0)
            {
                continue;
            }

            var onSet = new HashSet<Tri>(on, ReferenceEqualityComparer.Instance);
            Shared(tris, onSet, frame, i => layer[(i * 4) + 3], touched, maskUv: false, w, h, parts, shared);
            if (separate)
            {
                Shared(tris, onSet, frame, j => cut[j], touchedMask, maskUv: true, w, h, parts, shared);
            }

            Dilate(touched, island, w, h, i => layer[(i * 4) + 3] > 0f, (from, to) =>
            {
                Array.Copy(layer, from * 4, layer, to * 4, 4);
                maskAt[to] = separate ? maskAt[from] : to;
            });
            if (separate)
            {
                Dilate(touchedMask, maskIsland, w, h, j => cut[j] > 0f, (from, to) => cut[to] = cut[from]);
            }

            // The colour first, against the paint as it was (a texel's paint goes in the mask's own texel), then the mask.
            foreach (var i in touched)
            {
                var a = layer[(i * 4) + 3];
                var px = i * 4;
                var d = new Vector3(ToLinearTable[colour[px]], ToLinearTable[colour[px + 1]], ToLinearTable[colour[px + 2]]);
                var under = PaintRule.DiffuseUnder(d, PaintRule.Coverage(paint, maskAt[i] * 4), new Vector3(layer[px], layer[px + 1], layer[px + 2]), a);
                (colour[px], colour[px + 1], colour[px + 2]) = (ToSrgb(under.X), ToSrgb(under.Y), ToSrgb(under.Z));
                if (!separate)
                {
                    PaintRule.Uncover(paint, px, a);
                }

                (layer[px], layer[px + 1], layer[px + 2], layer[px + 3]) = (0f, 0f, 0f, 0f);
            }

            if (separate)
            {
                foreach (var j in touchedMask)
                {
                    PaintRule.Uncover(paint, j * 4, cut[j]);
                    cut[j] = 0f;
                }

                touchedMask.Clear();
            }

            touched.Clear();
        }

        Debug.WriteLine($"DecalBaker: {stamps.Count} stamp(s), {tris.Count} triangles at {w}x{h}{(separate ? ", mask in its own UV channel" : string.Empty)} in {clock.ElapsedMilliseconds} ms");
        return new DecalBakeResult(
            diffuse with { Rgba = colour, Width = w, Height = h, IsSrgb = true, CompressedMips = null },
            mask with { Rgba = paint, Width = w, Height = h, IsSrgb = false, CompressedMips = null },
            [.. shared]);
    }

    /// <summary>
    /// <paramref name="mask"/> redrawn in the colour texture's UV0 layout at <paramref name="w"/> x <paramref name="h"/>, for the
    /// 3D view (it draws one painted texture through UV0): each colour texel takes the mask texel at the same point of the car,
    /// through the channel <paramref name="parts"/> read the mask with; the mask itself when that is UV0. Texels no triangle
    /// covers stay unpainted (the few round each island take their neighbour's, for the smaller mips). Where two places share
    /// a colour texel but not a mask texel (the Laika's doors: one UV0 island, each its own in UV1) the one with less paint is
    /// shown, so a decal shows where it was put (the bake names the other place, <see cref="DecalBakeResult.SharedUv"/>).
    /// </summary>
    public static TextureImage MaskInColourUv(TextureImage mask, IReadOnlyList<PreviewPart> parts, int w, int h)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(parts);
        if (!parts.Any(p => p.MaskUv != 0 && p.Mesh.Uv0.Length > 0 && p.Mesh.Uv(p.MaskUv) is not null) || w <= 0 || h <= 0)
        {
            return mask;
        }

        var (first, second) = MaskMap(parts, w, h, mask.Width, mask.Height);
        var (src, rgba) = (mask.Rgba, new byte[w * h * 4]);
        var bare = BitConverter.IsLittleEndian ? unchecked((int)0xFF000000) : 0xFF; // RGBA 0, 0, 0, 255
        Parallel.For(0, h, y =>
        {
            var from = MemoryMarshal.Cast<byte, int>(src.AsSpan());
            var to = MemoryMarshal.Cast<byte, int>(rgba.AsSpan());
            for (var i = y * w; i < (y + 1) * w; i++)
            {
                var j = second[i] >= 0 && PaintRule.Coverage(src, second[i] * 4) < PaintRule.Coverage(src, first[i] * 4) ? second[i] : first[i];
                to[i] = j >= 0 ? from[j] : bare;
            }
        });

        return mask with { Rgba = rgba, Width = w, Height = h, CompressedMips = null };
    }

    /// <summary>
    /// The mask texel (of an <paramref name="mw"/> x <paramref name="mh"/> mask) at each colour texel and another one where a
    /// second place shares the colour texel (-1 where none); the last few maps are kept.
    /// </summary>
    private static (int[] First, int[] Second) MaskMap(IReadOnlyList<PreviewPart> parts, int w, int h, int mw, int mh)
    {
        var meshes = parts.Select(p => p.Mesh).ToArray();
        var channels = parts.Select(p => p.MaskUv).ToArray();
        lock (MaskMaps)
        {
            foreach (var m in MaskMaps)
            {
                if (m.W == w && m.H == h && m.Mw == mw && m.Mh == mh && m.Meshes.Length == meshes.Length
                    && m.Meshes.Zip(meshes).All(x => ReferenceEquals(x.First, x.Second)) && m.Channels.SequenceEqual(channels))
                {
                    return (m.Map, m.Second);
                }
            }
        }

        var (map, second) = (new int[w * h], new int[w * h]);
        Array.Fill(map, -1);
        Array.Fill(second, -1);
        var covered = new List<int>();
        var owner = new int[w * h]; // the part that wrote map[i]
        foreach (var t in Triangles(parts, w, h))
        {
            var (m0, m1, m2) = (t.M0 / new Vector2(w, h), t.M1 / new Vector2(w, h), t.M2 / new Vector2(w, h));
            Raster(t.T0, t.T1, t.T2, w, h, (i, b) =>
            {
                var j = TexelOf(((b.X * m0) + (b.Y * m1) + (b.Z * m2)) * new Vector2(mw, mh), mw, mh);
                if (map[i] < 0)
                {
                    (map[i], owner[i]) = (j, t.Part);
                    covered.Add(i);
                }
                else if (owner[i] != t.Part && second[i] < 0)
                {
                    second[i] = j; // another part on the same colour texel
                }
                else if (owner[i] == t.Part)
                {
                    map[i] = j;
                }
            });
        }

        Dilate(covered, new bool[w * h], w, h, i => map[i] >= 0, (from, to) => (map[to], second[to]) = (map[from], second[from]));
        lock (MaskMaps)
        {
            MaskMaps.Insert(0, (meshes, channels, w, h, mw, mh, map, second));
            if (MaskMaps.Count > 4)
            {
                MaskMaps.RemoveAt(MaskMaps.Count - 1);
            }
        }

        return (map, second);
    }

    /// <summary>
    /// The triangles <paramref name="frame"/> is pressed onto (they face it and reach into its box): the UV island under its
    /// centre (the panel it was put on: the nearest face turned towards it along its normal there) always, and each other
    /// island in its box only when the texels it would paint there are its own: an island whose texels another place of the
    /// car uses too (a mirrored body side, stacked trims) is left out, so the decal never spills there.
    /// </summary>
    private static List<Tri> PressedOn(List<Tri> tris, Frame frame, IReadOnlyList<PreviewPart> parts, int[][] islands, int w, int h)
    {
        var touching = tris.Where(frame.MayTouch).ToList();
        Tri? anchor = null;
        var best = float.MaxValue;
        foreach (var t in touching)
        {
            // Along the normal through the centre, from the outer face of the box inwards: the surface the centre lies on.
            if (Along(t, frame.Centre + (frame.N * frame.Depth), -frame.N) is { } at && at <= 2 * frame.Depth && MathF.Abs(at - frame.Depth) < best)
            {
                (anchor, best) = (t, MathF.Abs(at - frame.Depth));
            }
        }

        int IslandOf(Tri t) => (islands[t.Part] ??= Islands(parts[t.Part]))[t.Index];
        var home = anchor is null ? (-1, -1) : (anchor.Part, IslandOf(anchor));
        var on = new List<Tri>();
        foreach (var island in touching.GroupBy(t => (t.Part, IslandOf(t))))
        {
            var group = island.ToList();
            if (island.Key == home || !Reused(group, tris, frame, w, h))
            {
                on.AddRange(group);
            }
        }

        return on;
    }

    /// <summary>
    /// Whether texels <paramref name="frame"/> would paint through <paramref name="group"/> (one island; in UV0, and in the
    /// mask's UV when it has its own) are used by other triangles of the material too (a few on an island's edge do not count).
    /// </summary>
    private static bool Reused(List<Tri> group, List<Tri> tris, Frame frame, int w, int h)
    {
        var member = new HashSet<Tri>(group, ReferenceEqualityComparer.Instance);
        foreach (var maskUv in group.Any(t => t.MaskApart) ? new[] { false, true } : [false])
        {
            var mine = new HashSet<int>();
            foreach (var t in group)
            {
                var (a, b, c) = maskUv ? (t.M0, t.M1, t.M2) : (t.T0, t.T1, t.T2);
                Raster(a, b, c, w, h, (i, bary) =>
                {
                    if (frame.Sample((bary.X * t.P0) + (bary.Y * t.P1) + (bary.Z * t.P2)) is { W: > 0f })
                    {
                        mine.Add(i);
                    }
                });
            }

            if (mine.Count == 0)
            {
                continue;
            }

            var grid = new bool[GridCells * GridCells];
            foreach (var i in mine)
            {
                grid[((i / w) * GridCells / h * GridCells) + ((i % w) * GridCells / w)] = true;
            }

            var (least, hits) = (Math.Max(4, mine.Count / 200), 0);
            foreach (var t in tris)
            {
                var (a, b, c) = maskUv ? (t.M0, t.M1, t.M2) : (t.T0, t.T1, t.T2);
                if (member.Contains(t) || !Overlaps(a, b, c, grid, w, h))
                {
                    continue;
                }

                Raster(a, b, c, w, h, (i, _) => hits += mine.Contains(i) ? 1 : 0);
                if (hits >= least)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Möller–Trumbore, both faces: how far along <paramref name="d"/> the line from <paramref name="o"/> meets the triangle (t ≥ 0), or null.</summary>
    private static float? Along(Tri t, Vector3 o, Vector3 d)
    {
        var (e1, e2) = (t.P1 - t.P0, t.P2 - t.P0);
        var p = Vector3.Cross(d, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f)
        {
            return null;
        }

        var s = o - t.P0;
        var u = Vector3.Dot(s, p) / det;
        if (u is < -1e-4f or > 1.0001f)
        {
            return null;
        }

        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(d, q) / det;
        if (v < -1e-4f || u + v > 1.0001f)
        {
            return null;
        }

        var at = Vector3.Dot(e2, q) / det;
        return at >= 0f ? at : null;
    }

    /// <summary>
    /// The UV islands of a part's mesh (triangle → island): triangles joined through edges whose corners share the position,
    /// the UV0 and the mask's UV, so both textures run on across the edge. Kept per mesh and mask channel.
    /// </summary>
    private static int[] Islands(PreviewPart part)
    {
        var mesh = part.Mesh;
        var channel = mesh.Uv(part.MaskUv) is null ? 0 : part.MaskUv;
        var byChannel = IslandCache.GetOrCreateValue(mesh);
        lock (byChannel)
        {
            if (byChannel.TryGetValue(channel, out var known))
            {
                return known;
            }
        }

        var (pos, uv, uvm, idx) = (mesh.Positions, mesh.Uv0, mesh.Uv(channel)!, mesh.Indices);
        var weld = new Dictionary<(int, int, int, int, int, int, int), int>();
        var corner = new int[idx.Length];
        for (var c = 0; c < idx.Length; c++)
        {
            var v = idx[c];
            var key = (Q(pos[v * 3], 100), Q(pos[(v * 3) + 1], 100), Q(pos[(v * 3) + 2], 100), Q(uv[v * 2], 8192), Q(uv[(v * 2) + 1], 8192), Q(uvm[v * 2], 8192), Q(uvm[(v * 2) + 1], 8192));
            if (!weld.TryGetValue(key, out var id))
            {
                weld[key] = id = weld.Count;
            }

            corner[c] = id;
        }

        var count = idx.Length / 3;
        var parent = Enumerable.Range(0, count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                x = parent[x] = parent[parent[x]];
            }

            return x;
        }

        var edges = new Dictionary<(int, int), int>();
        for (var t = 0; t < count; t++)
        {
            for (var e = 0; e < 3; e++)
            {
                var (a, b) = (corner[(t * 3) + e], corner[(t * 3) + ((e + 1) % 3)]);
                var edge = (Math.Min(a, b), Math.Max(a, b));
                if (edges.TryGetValue(edge, out var other))
                {
                    parent[Find(t)] = Find(other);
                }
                else
                {
                    edges[edge] = t;
                }
            }
        }

        var islands = new int[count];
        for (var t = 0; t < count; t++)
        {
            islands[t] = Find(t);
        }

        lock (byChannel)
        {
            byChannel[channel] = islands;
        }

        return islands;

        static int Q(float v, float scale) => (int)MathF.Round(v * scale);
    }

    /// <summary>
    /// Triangles that are not where the stamp was pressed (another island, or outside its box) but whose UVs in one texture
    /// cover texels it painted (the mirrored half of a body sharing one UV island, a door sharing the other door's): the decal
    /// shows there too. A part counts once a few texels are shared (an island's edge texel touching its neighbour's does not).
    /// </summary>
    private static void Shared(List<Tri> tris, HashSet<Tri> on, Frame frame, Func<int, float> painted, List<int> touched, bool maskUv, int w, int h,
        IReadOnlyList<PreviewPart> parts, SortedSet<string> shared)
    {
        var grid = new bool[GridCells * GridCells];
        var any = false;
        foreach (var i in touched)
        {
            if (painted(i) > 0.05f)
            {
                grid[((i / w) * GridCells / h * GridCells) + ((i % w) * GridCells / w)] = true;
                any = true;
            }
        }

        if (!any)
        {
            return;
        }

        var least = Math.Max(4, touched.Count / 200);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tris)
        {
            var name = Name(parts[t.Part]);
            var (a, b, c) = maskUv ? (t.M0, t.M1, t.M2) : (t.T0, t.T1, t.T2);
            if (shared.Contains(name) || !Overlaps(a, b, c, grid, w, h))
            {
                continue;
            }

            var mine = on.Contains(t);
            Raster(a, b, c, w, h, (i, bary) =>
            {
                if (painted(i) > 0.05f && (!mine || !frame.Inside((bary.X * t.P0) + (bary.Y * t.P1) + (bary.Z * t.P2), 0.75f)))
                {
                    counts[name] = counts.GetValueOrDefault(name) + 1;
                }
            });
            if (counts.GetValueOrDefault(name) >= least)
            {
                shared.Add(name);
            }
        }
    }

    private static string Name(PreviewPart part) => part.Attachment.Length > 0 ? part.Attachment[(part.Attachment.LastIndexOf('/') + 1)..] : part.Name;

    private static bool Overlaps(Vector2 a, Vector2 b, Vector2 c, bool[] grid, int w, int h)
    {
        var (minX, maxX) = (MathF.Min(a.X, MathF.Min(b.X, c.X)), MathF.Max(a.X, MathF.Max(b.X, c.X)));
        var (minY, maxY) = (MathF.Min(a.Y, MathF.Min(b.Y, c.Y)), MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
        if (maxX - minX >= w || maxY - minY >= h)
        {
            return true; // spread over the whole texture (tiling): let the raster decide
        }

        var (x0, x1) = (Wrap((int)MathF.Floor(minX * GridCells / w)), Wrap((int)MathF.Floor(maxX * GridCells / w)));
        var (y0, y1) = (Wrap((int)MathF.Floor(minY * GridCells / h)), Wrap((int)MathF.Floor(maxY * GridCells / h)));
        for (var y = y0; ; y = (y + 1) % GridCells)
        {
            for (var x = x0; ; x = (x + 1) % GridCells)
            {
                if (grid[(y * GridCells) + x])
                {
                    return true;
                }

                if (x == x1)
                {
                    break;
                }
            }

            if (y == y1)
            {
                return false;
            }
        }

        static int Wrap(int cell) => ((cell % GridCells) + GridCells) % GridCells;
    }

    /// <summary>Which texels of a <paramref name="w"/> x <paramref name="h"/> texture a triangle covers, in UV0 or in the mask's UV.</summary>
    private static bool[] Covered(List<Tri> tris, int w, int h, bool maskUv)
    {
        var covered = new bool[w * h];
        foreach (var t in tris)
        {
            if (maskUv)
            {
                Raster(t.M0, t.M1, t.M2, w, h, (i, _) => covered[i] = true);
            }
            else
            {
                Raster(t.T0, t.T1, t.T2, w, h, (i, _) => covered[i] = true);
            }
        }

        return covered;
    }

    /// <summary>
    /// Spreads what <paramref name="touched"/> holds <see cref="Dilation"/> texels into the gutters round UV islands (texels
    /// <paramref name="island"/> leaves out), so smaller mips do not show a seam; <paramref name="copy"/> copies a texel's
    /// value to an empty neighbour (<paramref name="has"/>), which joins <paramref name="touched"/>.
    /// </summary>
    private static void Dilate(List<int> touched, bool[] island, int w, int h, Func<int, bool> has, Action<int, int> copy)
    {
        var edge = touched;
        for (var step = 0; step < Dilation; step++)
        {
            var grown = new List<int>();
            foreach (var i in edge)
            {
                var (x, y) = (i % w, i / w);
                foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                {
                    var n = (ny * w) + nx;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || island[n] || has(n))
                    {
                        continue;
                    }

                    copy(i, n);
                    grown.Add(n);
                }
            }

            touched.AddRange(grown);
            edge = grown;
        }
    }

    /// <summary>
    /// A triangle of the material: corners in vehicle space, in texel space through UV0 (<c>T</c>) and through the mask's UV
    /// channel (<c>M</c>, the same as <c>T</c> when the mask lies in UV0), its normal, and where it comes from.
    /// </summary>
    private sealed record Tri(int Part, int Index, Vector3 P0, Vector3 P1, Vector3 P2, Vector2 T0, Vector2 T1, Vector2 T2, Vector2 M0, Vector2 M1, Vector2 M2, Vector3 Normal)
    {
        /// <summary>The mask is read through another UV channel than the colour texture here.</summary>
        public bool MaskApart => M0 != T0 || M1 != T1 || M2 != T2;
    }

    private static List<Tri> Triangles(IReadOnlyList<PreviewPart> parts, int w, int h)
    {
        var list = new List<Tri>();
        for (var k = 0; k < parts.Count; k++)
        {
            var (mesh, transform) = (parts[k].Mesh, parts[k].Transform);
            if (mesh.Uv0.Length == 0)
            {
                continue;
            }

            var maskUv = mesh.Uv(parts[k].MaskUv);
            if (maskUv is null)
            {
                Debug.WriteLine($"DecalBaker: {parts[k].Name} has no UV channel {parts[k].MaskUv} for its colour mask; UV0 is used");
                maskUv = mesh.Uv0;
            }

            Vector3 P(uint i) => transform.TransformPosition(new FVector(mesh.Positions[i * 3], mesh.Positions[(i * 3) + 1], mesh.Positions[(i * 3) + 2])).ToVector3();
            Vector3 N(uint i) => transform.TransformVectorNoScale(new FVector(mesh.Normals[i * 3], mesh.Normals[(i * 3) + 1], mesh.Normals[(i * 3) + 2])).ToVector3();
            Vector2 T(uint i) => new(mesh.Uv0[i * 2] * w, mesh.Uv0[(i * 2) + 1] * h);
            Vector2 M(uint i) => new(maskUv[i * 2] * w, maskUv[(i * 2) + 1] * h);
            var idx = mesh.Indices;
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                var (a, b, c) = (P(idx[i]), P(idx[i + 1]), P(idx[i + 2]));
                var n = mesh.Normals.Length > 0 ? N(idx[i]) + N(idx[i + 1]) + N(idx[i + 2]) : Vector3.Cross(b - a, c - a);
                list.Add(new Tri(k, i / 3, a, b, c, T(idx[i]), T(idx[i + 1]), T(idx[i + 2]), M(idx[i]), M(idx[i + 1]), M(idx[i + 2]),
                    n.LengthSquared() > 0f ? Vector3.Normalize(n) : Vector3.Zero));
            }
        }

        return list;
    }

    /// <summary>The texel of a <paramref name="w"/> x <paramref name="h"/> texture under a point in texel space (tiling UVs wrap).</summary>
    private static int TexelOf(Vector2 p, int w, int h)
    {
        var (x, y) = ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));
        return ((((y % h) + h) % h) * w) + (((x % w) + w) % w);
    }

    /// <summary>Calls <paramref name="visit"/> with the texel index and the barycentric weights of every texel centre inside the triangle (texel space; tiling UVs wrap).</summary>
    private static void Raster(Vector2 a, Vector2 b, Vector2 c, int w, int h, Action<int, Vector3> visit)
    {
        var area = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
        if (MathF.Abs(area) < 1e-9f)
        {
            return;
        }

        const float Eps = -1e-4f;
        var (x0, x1) = ((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) - 0.5f), (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)) - 0.5f));
        var (y0, y1) = ((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - 0.5f), (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - 0.5f));
        if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > (long)w * h * 4)
        {
            return; // a degenerate UV spread over the texture many times
        }

        for (var y = y0; y <= y1; y++)
        {
            var py = y + 0.5f;
            for (var x = x0; x <= x1; x++)
            {
                var px = x + 0.5f;
                var w1 = (((px - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (py - a.Y))) / area;
                var w2 = (((b.X - a.X) * (py - a.Y)) - ((px - a.X) * (b.Y - a.Y))) / area;
                var w0 = 1f - w1 - w2;
                if (w0 < Eps || w1 < Eps || w2 < Eps)
                {
                    continue;
                }

                var (tx, ty) = (((x % w) + w) % w, ((y % h) + h) % h);
                visit((ty * w) + tx, new Vector3(w0, w1, w2));
            }
        }
    }

    /// <summary>The decal's box in the vehicle's space: across (s), down (t) and along the normal.</summary>
    private readonly record struct Frame(Vector3 Centre, Vector3 S, Vector3 T, Vector3 N, float HalfW, float HalfH, float Depth, DecalStamp Stamp)
    {
        public static Frame Of(DecalStamp stamp)
        {
            var (s, t, n) = Axes(stamp.Normal, stamp.Up, stamp.RotationDeg);
            return new Frame(stamp.Position, s, t, n, stamp.WidthCm / 2, stamp.HeightCm / 2, stamp.Depth > 0f ? stamp.Depth : MathF.Min(stamp.WidthCm, stamp.HeightCm) / 2, stamp);
        }

        /// <summary>Whether the triangle faces the decal and its corners are not all on one outer side of the box.</summary>
        public bool MayTouch(Tri t)
        {
            if (Vector3.Dot(t.Normal, N) <= MinFacing)
            {
                return false;
            }

            var (a, b, c) = (t.P0 - Centre, t.P1 - Centre, t.P2 - Centre);
            bool Out(Vector3 axis, float half) =>
                (Vector3.Dot(a, axis) > half && Vector3.Dot(b, axis) > half && Vector3.Dot(c, axis) > half)
                || (Vector3.Dot(a, axis) < -half && Vector3.Dot(b, axis) < -half && Vector3.Dot(c, axis) < -half);
            return !Out(S, HalfW) && !Out(T, HalfH) && !Out(N, Depth);
        }

        public bool Inside(Vector3 p, float slack)
        {
            var d = p - Centre;
            return MathF.Abs(Vector3.Dot(d, S)) <= HalfW + slack && MathF.Abs(Vector3.Dot(d, T)) <= HalfH + slack && MathF.Abs(Vector3.Dot(d, N)) <= Depth + slack;
        }

        /// <summary>The decal (premultiplied linear colour, alpha) at vehicle-space point <paramref name="p"/>, or null outside its box.</summary>
        public Vector4? Sample(Vector3 p)
        {
            var d = p - Centre;
            if (MathF.Abs(Vector3.Dot(d, N)) > Depth)
            {
                return null;
            }

            var s = 0.5f + (Vector3.Dot(d, S) / (2 * HalfW));
            var t = 0.5f - (Vector3.Dot(d, T) / (2 * HalfH));
            return s is < 0f or > 1f || t is < 0f or > 1f ? null : Stamp.Image.Sample(Stamp.FlipX ? 1f - s : s, t);
        }
    }

    /// <summary>Nearest-texel copy of an RGBA image at another size (a copy when the size is unchanged).</summary>
    private static byte[] Resample(byte[] rgba, int sw, int sh, int w, int h)
    {
        if (sw == w && sh == h)
        {
            return (byte[])rgba.Clone();
        }

        var dst = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var sy = Math.Min(sh - 1, y * sh / h);
            for (var x = 0; x < w; x++)
            {
                Array.Copy(rgba, ((sy * sw) + Math.Min(sw - 1, x * sw / w)) * 4, dst, ((y * w) + x) * 4, 4);
            }
        }

        return dst;
    }

    private static byte ToSrgb(float linear)
    {
        var v = Math.Clamp(linear, 0f, 1f);
        var s = v <= 0.0031308f ? 12.92f * v : (1.055f * MathF.Pow(v, 1 / 2.4f)) - 0.055f;
        return (byte)Math.Clamp(MathF.Round(s * 255f), 0f, 255f);
    }

    private static float SrgbToLinear(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
}
