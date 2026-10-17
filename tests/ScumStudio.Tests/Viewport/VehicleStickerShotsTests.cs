using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.App.Controls;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Pak;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Pictures for the Laika/WolfsWagen sticker report (<see cref="VehicleMaskUvRealTests"/>): a sticker on a front door and on
/// the hood of the Laika, the WolfsWagen and the Rager, baked by the studio and drawn the way the game composes the body
/// (colour texture through UV0, paint through the colour mask in its own UV channel, read from the game's files here). Only
/// with <c>SCUMSTUDIO_SCREENSHOTS</c> (the folder), the real game files and OpenGL; <c>R37_LABEL</c> names the run.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class VehicleStickerShotsTests(ITestOutputHelper output)
{
    [GlFact]
    public async Task StickersOnTheDoorAndTheHoodAsTheGameShowsThem()
    {
        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is not { Length: > 0 } folder || folder == "0" || RealPaks.SkipReason is not null)
        {
            return;
        }

        var label = Environment.GetEnvironmentVariable("R37_LABEL") is { Length: > 0 } l ? l : "after";
        Directory.CreateDirectory(folder);
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        foreach (var vehicle in new[] { "/Game/ConZ_Files/Vehicles/Car/Laika/BPC_Laika", "/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen", "/Game/ConZ_Files/Vehicles/Car/Rager/BPC_Rager" })
        {
            var body = Body.Load(catalog, vehicle);
            var stamps = new List<(string Spot, DecalStamp Stamp)>();
            foreach (var spot in new[] { "Door", "Hood" })
            {
                var (k, hit, _) = VehicleMaskUvRealTests.Aim(body.Model, body.Parts, spot, VehicleMaskUvRealTests.Bounds(body.Parts).Centre);
                var part = body.Parts[k];
                var layer = new DecalLayer { Position = hit.Position, Normal = hit.Normal, Up = VehicleMaskUvRealTests.UpOn(part.Transform, hit.Normal), WidthCm = 45, HeightCm = 45 };
                stamps.Add((spot, DecalBaker.Stamps(layer, Sticker, part.Transform)[0]));
            }

            var baked = DecalBaker.Bake(body.Diffuse, body.Mask, body.Parts, [.. stamps.Select(s => s.Stamp)]);
            output.WriteLine($"{body.Leaf} ({label}): shared [{string.Join(", ", baked.SharedUv)}]");
            var game = body.GameViews(baked.Diffuse, baked.Mask);
            foreach (var (spot, stamp) in stamps)
            {
                var file = Path.Combine(folder, $"r37-{body.Leaf}-{spot.ToLowerInvariant()}-{label}.png");
                await Shots.RenderAsync(body.Model, game, stamp.Position, stamp.Normal, file);
            }

            // The whole side the door sticker is on, to see where else it shows.
            var side = stamps[0].Stamp;
            await Shots.RenderAsync(body.Model, game, VehicleMaskUvRealTests.Bounds(body.Parts).Centre, side.Normal with { Z = 0.25f },
                Path.Combine(folder, $"r37-{body.Leaf}-side-{label}.png"), 650f);
            var other = side.Normal with { Y = -side.Normal.Y, Z = 0.25f };
            await Shots.RenderAsync(body.Model, game, VehicleMaskUvRealTests.Bounds(body.Parts).Centre, other,
                Path.Combine(folder, $"r37-{body.Leaf}-otherside-{label}.png"), 650f);
        }
    }

    /// <summary>A 64 px sticker: an "F" (it shows turns and mirrors) in magenta on yellow with a dark frame.</summary>
    internal static readonly DecalImage Sticker = MakeSticker();

    private static DecalImage MakeSticker()
    {
        const int N = 64;
        var rgba = new float[N * N * 4];
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var frame = x < 4 || y < 4 || x >= N - 4 || y >= N - 4;
                var f = (x is >= 16 and < 26 && y is >= 12 and < 52) || (y is >= 12 and < 22 && x is >= 16 and < 48) || (y is >= 28 and < 36 && x is >= 16 and < 40);
                var c = frame ? new Vector3(0.02f, 0.02f, 0.02f) : f ? new Vector3(1f, 0f, 1f) : new Vector3(1f, 0.75f, 0f);
                var i = ((y * N) + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (c.X, c.Y, c.Z, 1f);
            }
        }

        return new DecalImage(N, N, rgba);
    }
}

/// <summary>A vehicle's body paint as the game draws it: the material, its parts, its textures and the mask's UVs read from the game's files.</summary>
internal sealed record Body(string Leaf, PreviewModel Model, string Material, List<PreviewPart> Parts, TextureImage Diffuse, TextureImage Mask, List<float[]> MaskUvs, int Channel)
{
    public static Body Load(AssetCatalog catalog, string vehicle, int textureSize = 2048)
    {
        var model = new MeshPreviewLoader(catalog) { TextureSize = 1024 }.LoadBlueprint(vehicle)!;
        var inspector = new MaterialInspector(catalog);
        var (material, info) = model.Parts.Select(p => p.Material).Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(m => (Material: m, Info: inspector.Inspect(m)))
            .Where(m => VehicleMaskUvRealTests.Own(m.Info, "Color Mask") is not null && VehicleMaskUvRealTests.Own(m.Info, "Diffuse Map") is not null)
            .OrderByDescending(m => model.Parts.Where(p => p.Material == m.Material).Sum(p => p.Mesh.TriangleCount)).First();
        var channel = (int)MathF.Round(info.Scalars.FirstOrDefault(s => s.Name == "Color Mask UV Channel")?.Value ?? 0f);
        var parts = model.Parts.Where(p => p.Material == material).ToList();
        var maskUvs = parts.Select(p => channel == 0 ? p.Mesh.Uv0 : VehicleMaskUvRealTests.GameUv(catalog, p.Mesh.Name, channel, p.Mesh.VertexCount)).ToList();
        return new Body(vehicle[(vehicle.LastIndexOf('/') + 1)..], model, material, parts,
            TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(VehicleMaskUvRealTests.Own(info, "Diffuse Map")!), textureSize),
            TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(VehicleMaskUvRealTests.Own(info, "Color Mask")!), textureSize), maskUvs, channel);
    }

    /// <summary>
    /// What the game shows on each body part (sRGB, UV0 layout): <paramref name="diffuse"/> painted as far as
    /// <paramref name="mask"/> covers each point through its own channel. One texture per part: parts sharing UV0 texels
    /// (the Laika's doors) read their own mask texels in the game.
    /// </summary>
    public Func<PreviewPart, TextureImage?> GameViews(TextureImage diffuse, TextureImage mask, Vector3? paintA = null, Vector3? paintB = null)
    {
        var views = new Dictionary<PreviewPart, TextureImage>(ReferenceEqualityComparer.Instance);
        for (var k = 0; k < Parts.Count; k++)
        {
            var rgba = (byte[])diffuse.Rgba.Clone();
            VehicleMaskUvRealTests.Sample([Parts[k]], [MaskUvs[k]], diffuse.Width, diffuse.Height, (_, _, _, _, uv0, uvm) =>
            {
                var shown = VehicleMaskUvRealTests.Shown(diffuse, mask, uv0, uvm, paintA, paintB);
                var i = VehicleMaskUvRealTests.Texel(diffuse, uv0);
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (Shots.Srgb(shown.X), Shots.Srgb(shown.Y), Shots.Srgb(shown.Z), 255);
            });
            views[Parts[k]] = diffuse with { Name = $"#game/{k}", Rgba = rgba, IsSrgb = true, CompressedMips = null };
        }

        return p => views.GetValueOrDefault(p);
    }

    /// <summary>The material's parts all wearing <paramref name="texture"/> (what the studio's 3D view draws).</summary>
    public Func<PreviewPart, TextureImage?> Wearing(TextureImage texture) => p => p.Material == Material ? texture : null;

    /// <summary>The paint coverage the game reads at each UV0 texel a body triangle covers (-1 elsewhere).</summary>
    public float[] GameCoverage(TextureImage mask, int w, int h)
    {
        var cover = new float[w * h];
        Array.Fill(cover, -1f);
        VehicleMaskUvRealTests.Sample(Parts, MaskUvs, w, h, (_, _, _, _, uv0, uvm) =>
            cover[VehicleMaskUvRealTests.Texel(Diffuse with { Width = w, Height = h }, uv0) / 4] = PaintRule.Coverage(mask.Rgba, VehicleMaskUvRealTests.Texel(mask, uvm)));
        return cover;
    }
}

/// <summary>Offscreen pictures of a vehicle whose body wears one texture.</summary>
internal static class Shots
{
    public static byte Srgb(float linear)
    {
        var v = Math.Clamp(linear, 0f, 1f);
        return (byte)Math.Clamp(MathF.Round((v <= 0.0031308f ? 12.92f * v : (1.055f * MathF.Pow(v, 1 / 2.4f)) - 0.055f) * 255f), 0f, 255f);
    }

    /// <summary>Draws <paramref name="model"/> with each part wearing its <paramref name="texture"/> (when it has one), seen along −<paramref name="normal"/> (UE) at <paramref name="at"/>.</summary>
    public static async Task RenderAsync(PreviewModel model, Func<PreviewPart, TextureImage?> texture, Vector3 at, Vector3 normal, string file, float distance = 260f)
    {
        const int W = 960, H = 640;
        var textures = new Dictionary<string, TextureImage>(model.Textures, StringComparer.OrdinalIgnoreCase);
        var parts = new List<PreviewPart>();
        foreach (var p in model.Parts)
        {
            if (texture(p) is { } t)
            {
                textures[t.Name] = t;
                parts.Add(p with { TexturePath = t.Name, Tint = null, NormalPath = null });
            }
            else
            {
                parts.Add(p);
            }
        }

        var painted = model with { Parts = parts, Textures = textures };
        using var gl = GlHarness.Create(W, H);
        using var scene = PreviewScene.Upload(gl.Renderer, painted);
        var n = Vector3.Normalize(normal);
        var look = -new Vector3(n.X, n.Z, n.Y); // UE (x, y, z) is GL (x, z, y)
        var pitch = Math.Clamp(MathF.Asin(Math.Clamp(look.Y, -1f, 1f)) * 180f / MathF.PI, -65f, 10f);
        var yaw = MathF.Abs(look.X) + MathF.Abs(look.Z) < 0.2f ? 180f : MathF.Atan2(look.Z, look.X) * 180f / MathF.PI;
        var camera = new FlyCamera();
        camera.Orbit(new Vector3(at.X, at.Z, at.Y), yaw + 15f, pitch - 8f, distance);
        camera.FitClipRange(distance, scene.Bounds.Extent.Length());
        gl.Renderer.Settings = MeshPreview.SettingsFor(PreviewLight.Studio, gl.Renderer.Settings, scene.Bounds.Extent.Length(), camera.Forward) with { ShowGrid = false };
        gl.Renderer.Render(gl.Target, scene.Scene, camera);
        await ImageExport.SavePngAsync(gl.Target.ReadColorRgba(), W, H, file);
    }
}
