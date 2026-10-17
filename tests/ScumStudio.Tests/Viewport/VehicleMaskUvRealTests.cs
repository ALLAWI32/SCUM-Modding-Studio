using System.Numerics;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Mathematics;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Bug report on 3.4.0: "The Laika and WolfsWagen textures aren't syncing to the body correctly, and if you place a sticker
/// on them anywhere it gets distorted and moved around the cars." SCUM's car master <c>M_Car_01</c> reads the
/// <c>Diffuse Map</c> through UV0 but the <c>Color Mask</c> through the UV channel its <c>Color Mask UV Channel</c> scalar
/// names: 0 on the Rager's body (<c>MI_Rager_Outer</c>), 1 on the Laika's (<c>MI_Laika_Outside_A</c>) and the WolfsWagen's
/// (<c>MI_WW_BodyOutside_A</c>, whose mask is even named <c>T_WWBody_2_Color_UV2</c>). On the real game files (nothing of the
/// game is written into the repository): a sticker baked onto a front door and onto the hood changes what the game shows
/// (<c>lerp(diffuse at UV0, paint, mask at its own channel)</c>, <see cref="PaintRule"/>) only inside its footprint, apart
/// from places the bake names as sharing its texels, and shows the sticker on the clicked panel's UV island. The UVs and
/// the channel are read here straight from the game's mesh and material, not through the studio's own code. How the game
/// lights it is not checked.
/// </summary>
public sealed class VehicleMaskUvRealTests(ITestOutputHelper output)
{
    private const float StickerCm = 40f;
    private static readonly DecalImage Magenta = new(1, 1, [1f, 0f, 1f, 1f]);
    private static readonly Vector3 PaintA = new(0f, 0.6f, 0f);
    private static readonly Vector3 PaintB = new(0f, 0f, 0.6f);

    [PaksTheory]
    [InlineData("/Game/ConZ_Files/Vehicles/Car/Laika/BPC_Laika", 1)]
    [InlineData("/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen", 1)]
    [InlineData("/Game/ConZ_Files/Vehicles/Car/Rager/BPC_Rager", 0)]
    public void AStickerOnTheDoorOrTheHoodShowsInGameOnlyWhereItWasPlaced(string vehicle, int maskChannel)
    {
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var model = new MeshPreviewLoader(catalog) { TextureSize = 0 }.LoadBlueprint(vehicle)!;
        var inspector = new MaterialInspector(catalog);

        // The body paint: the material with a colour texture and a colour mask of its own drawing the most triangles.
        var (material, info) = model.Parts.Select(p => p.Material).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => (Material: m, Info: inspector.Inspect(m)))
            .Where(m => Own(m.Info, "Color Mask") is not null && Own(m.Info, "Diffuse Map") is not null)
            .OrderByDescending(m => model.Parts.Where(p => p.Material == m.Material).Sum(p => p.Mesh.TriangleCount)).First();
        var channel = (int)MathF.Round(info.Scalars.FirstOrDefault(s => s.Name == "Color Mask UV Channel")?.Value ?? 0f);
        output.WriteLine($"{vehicle}: body {material}, Color Mask UV Channel {channel}, diffuse {Own(info, "Diffuse Map")}, mask {Own(info, "Color Mask")}");
        Assert.Equal(maskChannel, channel);

        var parts = model.Parts.Where(p => p.Material == material).ToList();
        var diffuse = TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(Own(info, "Diffuse Map")!), 2048);
        var mask = TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(Own(info, "Color Mask")!), 2048);
        var maskUvs = parts.Select(p => channel == 0 ? p.Mesh.Uv0 : GameUv(catalog, p.Mesh.Name, channel, p.Mesh.VertexCount)).ToList();
        var centre = Bounds(parts).Centre;

        foreach (var spot in new[] { "Door", "Hood" })
        {
            var (k, hit, normal) = Aim(model, parts, spot, centre);
            var part = parts[k];
            var layer = new DecalLayer { Position = hit.Position, Normal = hit.Normal, Up = UpOn(part.Transform, hit.Normal), WidthCm = StickerCm, HeightCm = StickerCm };
            var stamp = Assert.Single(DecalBaker.Stamps(layer, Magenta, part.Transform));
            var baked = DecalBaker.Bake(diffuse, mask, parts, [stamp]);
            var island = Islands(parts, maskUvs, k)[hit.Triangle];

            var (s, t, n) = DecalBaker.Axes(stamp.Normal, stamp.Up, stamp.RotationDeg);
            bool Within(Vector3 p, float slack) =>
                MathF.Abs(Vector3.Dot(p - stamp.Position, s)) <= (StickerCm / 2) + slack && MathF.Abs(Vector3.Dot(p - stamp.Position, t)) <= (StickerCm / 2) + slack
                && MathF.Abs(Vector3.Dot(p - stamp.Position, n)) <= (StickerCm / 2) + Math.Max(slack, 0f);

            // A place whose texels the sticker's texels are (a mirrored door sharing the other door's) shows it too: the bake
            // must name it (the studio then says so); anywhere else it must not show.
            int changed = 0, spill = 0, core = 0, shows = 0, told = 0;
            var spillParts = new SortedSet<string>(StringComparer.Ordinal);
            Sample(parts, maskUvs, diffuse.Width, diffuse.Height, (j, tri, p, facing, uv0, uvm) =>
            {
                var before = Shown(diffuse, mask, uv0, uvm);
                var after = Shown(baked.Diffuse, baked.Mask, uv0, uvm);
                var onIsland = j == k && island.Contains(tri);
                if (Vector3.Distance(before, after) > 0.1f)
                {
                    changed++;
                    if (!Within(p, 1.5f))
                    {
                        if (baked.SharedUv.Contains(NameOf(parts[j])))
                        {
                            told++;
                        }
                        else
                        {
                            spill++;
                            spillParts.Add(NameOf(parts[j]));
                        }
                    }
                }

                if (onIsland && Within(p, -2f) && Vector3.Dot(facing, n) > 0.5f)
                {
                    core++;
                    shows += Vector3.Distance(after, new Vector3(1f, 0f, 1f)) < 0.15f ? 1 : 0;
                }
            });

            output.WriteLine($"  {spot} on {part.Name} ({part.Attachment}) at {hit.Position}, n {normal}: {changed} samples changed, {spill} outside its footprint"
                             + $" [{string.Join(", ", spillParts)}], {told} on places named as sharing [{string.Join(", ", baked.SharedUv)}]; {shows} of {core} under it show the sticker");
            Assert.True(core > 200, $"{spot}: only {core} samples under the sticker");
            Assert.True(shows >= core * 0.9f, $"{spot}: the sticker shows on {shows} of the {core} samples under it");
            Assert.True(spill <= Math.Max(25, changed / 100), $"{spot}: {spill} of {changed} changed samples lie outside the sticker's footprint");
        }
    }

    /// <summary>A part as the bake names it: its attachment (<c>BPC_Laika_Door_FrontRight</c>), or its mesh for the vehicle's own.</summary>
    internal static string NameOf(PreviewPart part) => part.Attachment.Length > 0 ? part.Attachment[(part.Attachment.LastIndexOf('/') + 1)..] : part.Name;

    /// <summary>The material's own texture in <paramref name="parameter"/> (not the master's flat dummy), or null.</summary>
    internal static string? Own(MaterialInfo info, string parameter) =>
        info.Textures.FirstOrDefault(t => t.Name == parameter && !t.TexturePath.Contains("Dummy", StringComparison.OrdinalIgnoreCase))?.TexturePath;

    /// <summary>
    /// A click from outside onto the middle of the biggest panel of a front door (seen from its side) or the hood (from
    /// above) that the click meets first: the part, the hit and the panel's outward direction.
    /// </summary>
    internal static (int Part, SurfaceHit Hit, Vector3 Normal) Aim(PreviewModel model, List<PreviewPart> parts, string spot, Vector3 carCentre)
    {
        var candidates = new List<(int Part, Vector3 Centre, Vector3 Out, float Area)>();
        for (var k = 0; k < parts.Count; k++)
        {
            var part = parts[k];
            var door = part.Attachment.Contains("Door_Front", StringComparison.OrdinalIgnoreCase);
            var hood = part.Attachment.Contains("Hood", StringComparison.OrdinalIgnoreCase);
            if ((spot == "Door" && !door) || (spot == "Hood" && !hood))
            {
                continue;
            }

            var bounds = Bounds([part]);
            var (pos, idx) = (part.Mesh.Positions, part.Mesh.Indices);
            Vector3 V(uint i) => part.Transform.TransformPosition(new FVector(pos[i * 3], pos[(i * 3) + 1], pos[(i * 3) + 2])).ToVector3();
            for (var i = 0; i + 2 < idx.Length; i += 3)
            {
                var (a, b, c) = (V(idx[i]), V(idx[i + 1]), V(idx[i + 2]));
                var cross = Vector3.Cross(b - a, c - a);
                var middle = (a + b + c) / 3f;
                var outward = spot == "Door" ? new Vector3(0, MathF.Sign(middle.Y - carCentre.Y), 0) : Vector3.UnitZ;
                if (cross.Length() < 1f || MathF.Abs(Vector3.Dot(Vector3.Normalize(cross), outward)) < 0.85f
                    || Vector3.Distance(middle, bounds.Centre) > bounds.Size.Length() * 0.25f)
                {
                    continue;
                }

                candidates.Add((k, middle, outward, cross.Length()));
            }
        }

        foreach (var c in candidates.OrderByDescending(c => c.Area).Take(60))
        {
            var ray = new SurfaceRay(-1, c.Centre + (c.Out * 400f), -c.Out);
            if (MeshRay.Hit(model, ray) is { } found && ReferenceEquals(model.Parts[found.PartIndex], parts[c.Part]))
            {
                return (c.Part, found.Hit, c.Out);
            }
        }

        throw new Xunit.Sdk.XunitException($"no {spot} panel of the body paint is the first thing a click meets");
    }

    /// <summary>Up on a decal at <paramref name="normal"/> (part space), as the studio drops it: the world's up, or its forward on a hood.</summary>
    internal static Vector3 UpOn(FTransform transform, Vector3 normal)
    {
        var up = transform.InverseTransformVector(FVector.Up).ToVector3();
        return MathF.Abs(Vector3.Dot(Vector3.Normalize(up), normal)) > 0.8f ? transform.InverseTransformVector(FVector.Forward).ToVector3() : up;
    }

    /// <summary>What the game shows at a point (linear): its diffuse texel through UV0, painted as far as its mask texel through the mask's channel covers it.</summary>
    internal static Vector3 Shown(TextureImage diffuse, TextureImage mask, Vector2 uv0, Vector2 uvm, Vector3? paintA = null, Vector3? paintB = null)
    {
        var d = Texel(diffuse, uv0);
        var m = Texel(mask, uvm);
        return PaintRule.Shown(new Vector3(Linear(diffuse.Rgba[d]), Linear(diffuse.Rgba[d + 1]), Linear(diffuse.Rgba[d + 2])), mask.Rgba, m, paintA ?? PaintA, paintB ?? PaintB);
    }

    internal static int Texel(TextureImage image, Vector2 uv)
    {
        var x = (((int)MathF.Floor(uv.X * image.Width) % image.Width) + image.Width) % image.Width;
        var y = (((int)MathF.Floor(uv.Y * image.Height) % image.Height) + image.Height) % image.Height;
        return ((y * image.Width) + x) * 4;
    }

    internal static float Linear(byte v) => v / 255f <= 0.04045f ? v / 255f / 12.92f : MathF.Pow(((v / 255f) + 0.055f) / 1.055f, 2.4f);

    /// <summary>
    /// Calls <paramref name="visit"/> at every texel centre of a <paramref name="w"/> x <paramref name="h"/> texture that a
    /// triangle of the parts covers in UV0: the part, the triangle, the point (vehicle space), the triangle's normal, its UV0
    /// and its UV in the mask's channel there.
    /// </summary>
    internal static void Sample(List<PreviewPart> parts, List<float[]> maskUvs, int w, int h, Action<int, int, Vector3, Vector3, Vector2, Vector2> visit)
    {
        for (var k = 0; k < parts.Count; k++)
        {
            var (mesh, transform, uvm) = (parts[k].Mesh, parts[k].Transform, maskUvs[k]);
            var (pos, uv, idx) = (mesh.Positions, mesh.Uv0, mesh.Indices);
            Vector3 P(uint i) => transform.TransformPosition(new FVector(pos[i * 3], pos[(i * 3) + 1], pos[(i * 3) + 2])).ToVector3();
            for (var tri = 0; tri < idx.Length / 3; tri++)
            {
                var (i0, i1, i2) = (idx[tri * 3], idx[(tri * 3) + 1], idx[(tri * 3) + 2]);
                var (a, b, c) = (new Vector2(uv[i0 * 2] * w, uv[(i0 * 2) + 1] * h), new Vector2(uv[i1 * 2] * w, uv[(i1 * 2) + 1] * h), new Vector2(uv[i2 * 2] * w, uv[(i2 * 2) + 1] * h));
                var area = ((b.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (b.Y - a.Y));
                if (MathF.Abs(area) < 1e-6f)
                {
                    continue;
                }

                var (p0, p1, p2) = (P(i0), P(i1), P(i2));
                var facing = Vector3.Cross(p1 - p0, p2 - p0);
                facing = facing.LengthSquared() > 0 ? Vector3.Normalize(facing) : Vector3.Zero;
                var meshNormal = new Vector3(mesh.Normals[i0 * 3], mesh.Normals[(i0 * 3) + 1], mesh.Normals[(i0 * 3) + 2]);
                var worldNormal = transform.TransformVectorNoScale(FVector.FromVector3(meshNormal)).ToVector3();
                facing = Vector3.Dot(facing, worldNormal) < 0 ? -facing : facing; // towards the outside the mesh's normals give
                var (m0, m1, m2) = (new Vector2(uvm[i0 * 2], uvm[(i0 * 2) + 1]), new Vector2(uvm[i1 * 2], uvm[(i1 * 2) + 1]), new Vector2(uvm[i2 * 2], uvm[(i2 * 2) + 1]));
                var (x0, x1) = ((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                var (y0, y1) = ((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > (long)w * h)
                {
                    continue;
                }

                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        var (px, py) = (x + 0.5f, y + 0.5f);
                        var wb = (((px - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (py - a.Y))) / area;
                        var wc = (((b.X - a.X) * (py - a.Y)) - ((px - a.X) * (b.Y - a.Y))) / area;
                        var wa = 1f - wb - wc;
                        if (wa < 0f || wb < 0f || wc < 0f)
                        {
                            continue;
                        }

                        visit(k, tri, (wa * p0) + (wb * p1) + (wc * p2), facing, new Vector2(px / w, py / h), (wa * m0) + (wb * m1) + (wc * m2));
                    }
                }
            }
        }
    }

    /// <summary>
    /// The UV islands of part <paramref name="k"/>: for each triangle, the set of triangles connected to it through edges
    /// whose corners share the position, the UV0 and the mask's UV (the game draws both textures continuously across them).
    /// </summary>
    private static Dictionary<int, HashSet<int>> Islands(List<PreviewPart> parts, List<float[]> maskUvs, int k)
    {
        var (mesh, uvm) = (parts[k].Mesh, maskUvs[k]);
        var (pos, uv, idx) = (mesh.Positions, mesh.Uv0, mesh.Indices);
        var weld = new Dictionary<(int, int, int, int, int, int, int), int>();
        int Key(uint v)
        {
            var key = ((int)MathF.Round(pos[v * 3] * 100), (int)MathF.Round(pos[(v * 3) + 1] * 100), (int)MathF.Round(pos[(v * 3) + 2] * 100),
                (int)MathF.Round(uv[v * 2] * 8192), (int)MathF.Round(uv[(v * 2) + 1] * 8192), (int)MathF.Round(uvm[v * 2] * 8192), (int)MathF.Round(uvm[(v * 2) + 1] * 8192));
            if (!weld.TryGetValue(key, out var id))
            {
                weld[key] = id = weld.Count;
            }

            return id;
        }

        var tris = idx.Length / 3;
        var parent = Enumerable.Range(0, tris).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                x = parent[x] = parent[parent[x]];
            }

            return x;
        }

        var edges = new Dictionary<(int, int), int>();
        for (var t = 0; t < tris; t++)
        {
            var c = new[] { Key(idx[t * 3]), Key(idx[(t * 3) + 1]), Key(idx[(t * 3) + 2]) };
            for (var e = 0; e < 3; e++)
            {
                var edge = (Math.Min(c[e], c[(e + 1) % 3]), Math.Max(c[e], c[(e + 1) % 3]));
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

        var groups = Enumerable.Range(0, tris).GroupBy(Find).ToDictionary(g => g.Key, g => g.ToHashSet());
        return Enumerable.Range(0, tris).ToDictionary(t => t, t => groups[Find(t)]);
    }

    /// <summary>
    /// UV channel <paramref name="channel"/> of the game's mesh named <paramref name="meshName"/> (finest LOD with render
    /// data, the vertex order the studio's meshes use), read here with CUE4Parse alone.
    /// </summary>
    internal static float[] GameUv(AssetCatalog catalog, string meshName, int channel, int vertexCount)
    {
        var file = catalog.Provider.Files.Keys.Where(f => f.EndsWith("/" + meshName + ".uasset", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Contains("/Vehicles2/", StringComparison.OrdinalIgnoreCase)).First();
        var package = file[..^".uasset".Length];
        UObject? mesh = catalog.LoadFirstExport<USkeletalMesh>(package);
        mesh ??= catalog.LoadFirstExport<UStaticMesh>(package);
        CBaseMeshLod lod = mesh switch
        {
            USkeletalMesh sk when sk.TryConvert(out CSkeletalMesh converted) => converted.LODs[0],
            UStaticMesh st when st.TryConvert(out CStaticMesh converted) => converted.LODs[0],
            _ => throw new InvalidDataException($"{file} is no mesh"),
        };
        Assert.True(lod.NumTexCoords > channel, $"{meshName} has {lod.NumTexCoords} UV channel(s)");
        var extra = lod.ExtraUV.Value[channel - 1];
        Assert.Equal(vertexCount, extra.Length);
        return extra.SelectMany(v => new[] { v.U, v.V }).ToArray();
    }

    internal static (Vector3 Centre, Vector3 Size) Bounds(IEnumerable<PreviewPart> parts)
    {
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var part in parts)
        {
            var p = part.Mesh.Positions;
            foreach (var i in part.Mesh.Indices)
            {
                var v = part.Transform.TransformPosition(new FVector(p[i * 3], p[(i * 3) + 1], p[(i * 3) + 2])).ToVector3();
                (min, max) = (Vector3.Min(min, v), Vector3.Max(max, v));
            }
        }

        return ((min + max) / 2f, max - min);
    }
}
