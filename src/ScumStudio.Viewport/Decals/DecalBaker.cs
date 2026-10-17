using System.Diagnostics;
using System.Numerics;
using ScumStudio.Assets.Textures;
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
public sealed record DecalStamp(DecalImage Image, Vector3 Position, Vector3 Normal, Vector3 Up, float WidthCm, float HeightCm, float RotationDeg, bool FlipX, float Opacity);

/// <summary>A bake's textures and the parts that show the decals somewhere else too (they share its texels).</summary>
/// <param name="Diffuse">The diffuse with the decals (sRGB).</param>
/// <param name="Mask">The colour mask with the paint taken off under them, at the diffuse's size.</param>
/// <param name="SharedUv">Parts whose surface elsewhere uses the same texels (mirrored or shared UVs), by name.</param>
public sealed record DecalBakeResult(TextureImage Diffuse, TextureImage Mask, IReadOnlyList<string> SharedUv);

/// <summary>
/// Bakes decals into a material's textures the way the game will show them: each decal is projected along its normal onto
/// the parts' triangles (a box as deep as half its smaller side, only faces turned towards it), rasterised in UV0 at the
/// texture's size, so it wraps round curved panels and never floats. Over the paint it follows <see cref="PaintRule"/>:
/// the mask loses the paint under the decal and the diffuse makes up for it, whatever colour the player paints.
/// </summary>
public static class DecalBaker
{
    private const float MinFacing = 0.2f;
    private const int Dilation = 4;
    private const int GridCells = 64;

    private static readonly float[] ToLinearTable = Enumerable.Range(0, 256).Select(i => SrgbToLinear(i / 255f)).ToArray();

    /// <summary>The stamps of <paramref name="layer"/> (on a part standing at <paramref name="partTransform"/>): itself and, when mirrored, its copy across Y = 0.</summary>
    public static IReadOnlyList<DecalStamp> Stamps(DecalLayer layer, DecalImage image, FTransform partTransform)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var position = partTransform.TransformPosition(FVector.FromVector3(layer.Position)).ToVector3();
        var normal = Vector3.Normalize(partTransform.TransformVectorNoScale(FVector.FromVector3(layer.Normal)).ToVector3());
        var up = partTransform.TransformVectorNoScale(FVector.FromVector3(layer.Up)).ToVector3();
        var stamp = new DecalStamp(image, position, normal, up, layer.WidthCm, layer.HeightCm, layer.RotationDeg, layer.FlipX, Math.Clamp(layer.Opacity, 0f, 1f));
        if (!layer.Mirror)
        {
            return [stamp];
        }

        static Vector3 Reflect(Vector3 v) => new(v.X, -v.Y, v.Z);
        return [stamp, stamp with { Position = Reflect(position), Normal = Reflect(normal), Up = Reflect(up), FlipX = !layer.FlipX, RotationDeg = -layer.RotationDeg }];
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
        var island = new bool[w * h];
        foreach (var t in tris)
        {
            Raster(t, w, h, (i, _) => island[i] = true);
        }

        var shared = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var layer = new float[w * h * 4]; // one stamp: straight linear colour + alpha
        var touched = new List<int>();
        foreach (var stamp in stamps.Where(s => s.Opacity > 0f && s.WidthCm > 0f && s.HeightCm > 0f))
        {
            var frame = Frame.Of(stamp);
            foreach (var t in tris)
            {
                if (!frame.MayTouch(t))
                {
                    continue;
                }

                Raster(t, w, h, (i, b) =>
                {
                    var p = (b.X * t.P0) + (b.Y * t.P1) + (b.Z * t.P2);
                    if (frame.Sample(p) is not { W: > 0f } d)
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
                    }
                });
            }

            if (touched.Count == 0)
            {
                continue;
            }

            Shared(tris, frame, layer, touched, w, h, parts, shared);
            Dilate(layer, touched, island, w, h);
            foreach (var i in touched)
            {
                var a = layer[(i * 4) + 3];
                var px = i * 4;
                var d = new Vector3(ToLinearTable[colour[px]], ToLinearTable[colour[px + 1]], ToLinearTable[colour[px + 2]]);
                var under = PaintRule.DiffuseUnder(d, PaintRule.Coverage(paint, px), new Vector3(layer[px], layer[px + 1], layer[px + 2]), a);
                (colour[px], colour[px + 1], colour[px + 2]) = (ToSrgb(under.X), ToSrgb(under.Y), ToSrgb(under.Z));
                PaintRule.Uncover(paint, px, a);
                (layer[px], layer[px + 1], layer[px + 2], layer[px + 3]) = (0f, 0f, 0f, 0f);
            }

            touched.Clear();
        }

        Debug.WriteLine($"DecalBaker: {stamps.Count} stamp(s), {tris.Count} triangles at {w}x{h} in {clock.ElapsedMilliseconds} ms");
        return new DecalBakeResult(
            diffuse with { Rgba = colour, Width = w, Height = h, IsSrgb = true, CompressedMips = null },
            mask with { Rgba = paint, Width = w, Height = h, IsSrgb = false, CompressedMips = null },
            [.. shared]);
    }

    /// <summary>
    /// Other triangles whose UVs cover texels the stamp painted, at a place the stamp does not reach (the mirrored half of a
    /// body sharing one UV island, a door sharing the body's): the decal shows there too.
    /// </summary>
    private static void Shared(List<Tri> tris, Frame frame, float[] layer, List<int> touched, int w, int h, IReadOnlyList<PreviewPart> parts, SortedSet<string> shared)
    {
        var grid = new bool[GridCells * GridCells];
        foreach (var i in touched)
        {
            grid[((i / w) * GridCells / h * GridCells) + ((i % w) * GridCells / w)] = true;
        }

        foreach (var t in tris)
        {
            var name = Name(parts[t.Part]);
            if (shared.Contains(name) || !Overlaps(t, grid, w, h))
            {
                continue;
            }

            var found = false;
            Raster(t, w, h, (i, b) =>
            {
                if (!found && layer[(i * 4) + 3] > 0.05f && !frame.Inside((b.X * t.P0) + (b.Y * t.P1) + (b.Z * t.P2), 0.75f))
                {
                    found = true;
                }
            });
            if (found)
            {
                shared.Add(name);
            }
        }
    }

    private static string Name(PreviewPart part) => part.Attachment.Length > 0 ? part.Attachment[(part.Attachment.LastIndexOf('/') + 1)..] : part.Name;

    private static bool Overlaps(Tri t, bool[] grid, int w, int h)
    {
        var (x0, x1) = (Math.Clamp((int)(t.MinX * GridCells / w), 0, GridCells - 1), Math.Clamp((int)(t.MaxX * GridCells / w), 0, GridCells - 1));
        var (y0, y1) = (Math.Clamp((int)(t.MinY * GridCells / h), 0, GridCells - 1), Math.Clamp((int)(t.MaxY * GridCells / h), 0, GridCells - 1));
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                if (grid[(y * GridCells) + x])
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Spreads the stamp <see cref="Dilation"/> texels into the gutters round UV islands, so smaller mips do not show a seam.</summary>
    private static void Dilate(float[] layer, List<int> touched, bool[] island, int w, int h)
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
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || island[n] || layer[(n * 4) + 3] > 0f)
                    {
                        continue;
                    }

                    Array.Copy(layer, i * 4, layer, n * 4, 4);
                    grown.Add(n);
                }
            }

            touched.AddRange(grown);
            edge = grown;
        }
    }

    /// <summary>A triangle of the material: corners in vehicle space, in texel space, and its normal.</summary>
    private sealed record Tri(int Part, Vector3 P0, Vector3 P1, Vector3 P2, Vector2 T0, Vector2 T1, Vector2 T2, Vector3 Normal)
    {
        public float MinX => MathF.Min(T0.X, MathF.Min(T1.X, T2.X));

        public float MaxX => MathF.Max(T0.X, MathF.Max(T1.X, T2.X));

        public float MinY => MathF.Min(T0.Y, MathF.Min(T1.Y, T2.Y));

        public float MaxY => MathF.Max(T0.Y, MathF.Max(T1.Y, T2.Y));
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

            Vector3 P(uint i) => transform.TransformPosition(new FVector(mesh.Positions[i * 3], mesh.Positions[(i * 3) + 1], mesh.Positions[(i * 3) + 2])).ToVector3();
            Vector3 N(uint i) => transform.TransformVectorNoScale(new FVector(mesh.Normals[i * 3], mesh.Normals[(i * 3) + 1], mesh.Normals[(i * 3) + 2])).ToVector3();
            Vector2 T(uint i) => new(mesh.Uv0[i * 2] * w, mesh.Uv0[(i * 2) + 1] * h);
            var idx = mesh.Indices;
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                var (a, b, c) = (P(idx[i]), P(idx[i + 1]), P(idx[i + 2]));
                var n = mesh.Normals.Length > 0 ? N(idx[i]) + N(idx[i + 1]) + N(idx[i + 2]) : Vector3.Cross(b - a, c - a);
                list.Add(new Tri(k, a, b, c, T(idx[i]), T(idx[i + 1]), T(idx[i + 2]), n.LengthSquared() > 0f ? Vector3.Normalize(n) : Vector3.Zero));
            }
        }

        return list;
    }

    /// <summary>Calls <paramref name="visit"/> with the texel index and the barycentric weights of every texel centre inside the UV triangle (tiling UVs wrap).</summary>
    private static void Raster(Tri t, int w, int h, Action<int, Vector3> visit)
    {
        var (a, b, c) = (t.T0, t.T1, t.T2);
        var area = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
        if (MathF.Abs(area) < 1e-9f)
        {
            return;
        }

        const float Eps = -1e-4f;
        var (x0, x1) = ((int)MathF.Floor(t.MinX - 0.5f), (int)MathF.Ceiling(t.MaxX - 0.5f));
        var (y0, y1) = ((int)MathF.Floor(t.MinY - 0.5f), (int)MathF.Ceiling(t.MaxY - 0.5f));
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
            return new Frame(stamp.Position, s, t, n, stamp.WidthCm / 2, stamp.HeightCm / 2, MathF.Min(stamp.WidthCm, stamp.HeightCm) / 2, stamp);
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
