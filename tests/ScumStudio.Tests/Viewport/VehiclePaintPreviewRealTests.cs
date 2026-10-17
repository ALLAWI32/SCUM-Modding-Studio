using System.Numerics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Pak;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Report on 3.4.0: "the Laika and WolfsWagen textures aren't syncing to the body correctly". The studio's 3D view paints
/// a body through its colour mask (<see cref="PaintBaker"/>); the Laika's and the WolfsWagen's masks lie in UV1. On the real
/// game files, with the colour texture replaced by a picture: the paint the 3D view shows on each texel is the paint the
/// game reads there through the mask's own UV channel (taken here from the game's meshes), on the Rager as before. With
/// <c>SCUMSTUDIO_SCREENSHOTS</c> it also draws the 3.4.0 view, the fixed view and the game's look side by side.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class VehiclePaintPreviewRealTests(ITestOutputHelper output)
{
    [PaksTheory]
    [InlineData("/Game/ConZ_Files/Vehicles/Car/Laika/BPC_Laika")]
    [InlineData("/Game/ConZ_Files/Vehicles/Car/WolfsWagen/BPC_WolfsWagen")]
    [InlineData("/Game/ConZ_Files/Vehicles/Car/Rager/BPC_Rager")]
    public async Task TheStudioPaintsAReplacedTextureWhereTheGameDoes(string vehicle)
    {
        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var body = Body.Load(catalog, vehicle, 1024);
        Assert.All(body.Parts, p => Assert.Equal(body.Channel, p.MaskUv)); // the loader read the material's channel
        Assert.All(body.Parts, p => Assert.NotNull(p.Mesh.Uv(p.MaskUv)));   // and brought it along
        Assert.All(body.Parts.Zip(body.MaskUvs), x => Assert.Equal(x.Second, x.First.Mesh.Uv(x.First.MaskUv)));

        // The colour texture replaced by a checker picture (as the Textures tab does), painted blue.
        var replaced = Checker(body.Diffuse.Width, body.Diffuse.Height);
        var (a, b) = (new Vector4(0.05f, 0.12f, 0.6f, 1f), new Vector4(0.6f, 0.4f, 0.05f, 1f));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var fixedView = PaintBaker.Bake(replaced, body.Mask, a, b, body.Parts);
        var first = clock.ElapsedMilliseconds;
        clock.Restart();
        _ = PaintBaker.Bake(replaced, body.Mask, b, a, body.Parts); // another colour: the mask's map is kept
        output.WriteLine($"paint bake at {replaced.Width} px with the mask's own UVs: first {first} ms, again {clock.ElapsedMilliseconds} ms");
        var oldView = PaintBaker.Bake(replaced, body.Mask, a, b); // 3.4.0: the mask read through UV0
        var game = body.GameCoverage(body.Mask, replaced.Width, replaced.Height);

        int Wrong(TextureImage view) => Enumerable.Range(0, game.Length).Count(i => game[i] >= 0f && MathF.Abs((view.Rgba[(i * 4) + 3] / 255f) - game[i]) > 0.25f);
        var covered = game.Count(c => c >= 0f);
        var (wrongNow, wrongBefore) = (Wrong(fixedView), Wrong(oldView));
        output.WriteLine($"{body.Leaf}: mask channel {body.Channel}; paint differs from the game's on {wrongNow} of {covered} texels (3.4.0: {wrongBefore})");
        Assert.True(wrongNow <= covered / 50, $"{wrongNow} of {covered} texels painted unlike the game");
        if (body.Channel != 0)
        {
            Assert.True(wrongBefore > covered / 20, "the 3.4.0 view was right here already?"); // Laika 9.6 %, WolfsWagen 41 %
        }

        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is { Length: > 0 } folder && folder != "0" && GlTestEnvironment.SkipReason is null)
        {
            Directory.CreateDirectory(folder);
            var centre = VehicleMaskUvRealTests.Bounds(body.Parts).Centre;
            var view = new Vector3(0.55f, -1f, 0.35f);
            await Shots.RenderAsync(body.Model, body.Wearing(oldView with { Name = "#old" }), centre, view, Path.Combine(folder, $"r37-{body.Leaf}-replaced-studio-3.4.0.png"), 700f);
            await Shots.RenderAsync(body.Model, body.Wearing(fixedView with { Name = "#fixed" }), centre, view, Path.Combine(folder, $"r37-{body.Leaf}-replaced-studio-fixed.png"), 700f);
            await Shots.RenderAsync(body.Model, body.GameViews(replaced, body.Mask, new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z)), centre, view, Path.Combine(folder, $"r37-{body.Leaf}-replaced-game.png"), 700f);

            // The studio's own 3D view of a sticker on the door (the paint baked through the mask's channel).
            var (k, hit, _) = VehicleMaskUvRealTests.Aim(body.Model, body.Parts, "Door", centre);
            var layer = new DecalLayer { Position = hit.Position, Normal = hit.Normal, Up = VehicleMaskUvRealTests.UpOn(body.Parts[k].Transform, hit.Normal), WidthCm = 45, HeightCm = 45 };
            var stamp = DecalBaker.Stamps(layer, VehicleStickerShotsTests.Sticker, body.Parts[k].Transform)[0];
            var baked = DecalBaker.Bake(body.Diffuse, body.Mask, body.Parts, [stamp]);
            var studio = PaintBaker.Bake(baked.Diffuse, baked.Mask, a, b, body.Parts);
            await Shots.RenderAsync(body.Model, body.Wearing(studio with { Name = "#studio" }), stamp.Position, stamp.Normal, Path.Combine(folder, $"r37-{body.Leaf}-door-studio-fixed.png"));
        }
    }

    /// <summary>A picture a user might put on the body: 16 x 16 squares of white and red (sRGB).</summary>
    private static TextureImage Checker(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var red = ((x * 16 / w) + (y * 16 / h)) % 2 == 0;
                var i = ((y * w) + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = red ? ((byte)220, (byte)30, (byte)30, (byte)255) : ((byte)235, (byte)235, (byte)235, (byte)255);
            }
        }

        return new TextureImage("Checker", w, h, rgba, "PF_B8G8R8A8", 0, w, h, true, false);
    }
}
