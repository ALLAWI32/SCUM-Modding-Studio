using ScumStudio.App.ViewModels;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// Paint on the Vehicles/Weapons 3D tab, which now loads full-size textures kept compressed for the GPU: the colour
/// textures a paint bakes or tints are decoded on their own (<see cref="ModulePageViewModel.RgbaOf"/>).
/// </summary>
public sealed class ModulePaintTextureTests(ITestOutputHelper output)
{
    private const string Texture = "/Game/Weapons/T_Gun_D.T_Gun_D";
    private const string Material = "/Game/Weapons/MI_Gun.MI_Gun";
    private const int MaxSize = ModulePageViewModel.PaintTextureSize;

    [Fact]
    public void TintGainOfACompressedOnlyTextureIsOne()
    {
        var gain = ModulePageViewModel.TintGain(Gun(Compressed()), Material, new Dictionary<string, TextureImage>());
        Assert.False(float.IsNaN(gain));
        Assert.Equal(1f, gain);
    }

    [Fact]
    public void TintGainReadsTheDecodedPixels()
    {
        // A dark gun texture (sRGB 40) is lifted to the 6× cap, also when the 3D view holds it compressed.
        var model = Gun(Compressed());
        var pixels = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(ModulePageViewModel.RgbaOf(model, Texture, pixels, _ => Solid(40), MaxSize));
        Assert.Equal(6f, ModulePageViewModel.TintGain(model, Material, pixels));
    }

    [Fact]
    public void RgbaOfDecodesACompressedTextureOnce()
    {
        var model = Gun(Compressed());
        var cache = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        var calls = 0;
        TextureImage? Decode(string path)
        {
            Assert.Equal(Texture, path);
            calls++;
            return Solid(128);
        }

        var first = ModulePageViewModel.RgbaOf(model, Texture, cache, Decode, MaxSize);
        var second = ModulePageViewModel.RgbaOf(model, Texture, cache, Decode, MaxSize);
        Assert.Same(first, second);
        Assert.Equal(1, calls);
        Assert.Equal(4 * 4 * 4, first!.Rgba.Length);
        Assert.Null(first.CompressedMips); // what PaintBaker paints over: a bake keeps the decoded pixels, not the GPU blocks
    }

    [Fact]
    public void RgbaOfKeepsPixelsItHasAndSkipsWhatItCannotRead()
    {
        var solid = Solid(128);
        var cache = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        Assert.Same(solid, ModulePageViewModel.RgbaOf(Gun(solid), Texture, cache, _ => throw new InvalidOperationException("no decode"), MaxSize));

        cache.Clear();
        Assert.Null(ModulePageViewModel.RgbaOf(Gun(Compressed()), Texture, cache, _ => null, MaxSize));
        Assert.Empty(cache);
        Assert.Null(ModulePageViewModel.RgbaOf(Gun(Compressed()), "/Game/Other.Other", cache, _ => Solid(128), MaxSize));
    }

    [Fact]
    public void RgbaOfShrinksATextureLargerThanThePaintSize()
    {
        // The 3D view decodes a non-BC colour texture whole (up to 8192 px): a bake gets it at the paint size.
        var cache = new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase);
        var big = Solid(128, 16);
        var pixels = ModulePageViewModel.RgbaOf(Gun(big), Texture, cache, _ => throw new InvalidOperationException("no decode"), 4);
        Assert.Equal((4, 4, 4 * 4 * 4), (pixels!.Width, pixels.Height, pixels.Rgba.Length));
        Assert.All(pixels.Rgba.Where((_, i) => i % 4 == 0), v => Assert.Equal(128, v));
        Assert.Same(pixels, cache[Texture]);

        cache.Clear();
        var decoded = ModulePageViewModel.RgbaOf(Gun(Compressed()), Texture, cache, _ => Solid(40, 8), 4);
        Assert.Equal((4, 4), (decoded!.Width, decoded.Height));
    }

    [Fact]
    public void APaintBakeWhileTheColourPickerIsDraggedStaysUnderTwoFrames()
    {
        // Repaint bakes on the UI thread at every colour change. At the paint size that is 35-100 ms on the owner's PC, so a
        // drag bakes half the size (ModulePageViewModel.DragPixels) and the whole bake follows when the picker rests.
        var mask = Solid(0, 1024);
        for (var i = 1; i < mask.Rgba.Length; i += 8)
        {
            mask.Rgba[i] = 255; // green on every other texel: paint over half the body
        }

        var whole = BestBake(Solid(90, MaxSize), mask);
        var dragged = BestBake(Solid(90, MaxSize / 2), mask);
        output.WriteLine(FormattableString.Invariant($"PaintBaker.Bake best of 5: {MaxSize}² {whole:F1} ms, {MaxSize / 2}² (dragging) {dragged:F1} ms"));
        if (!Ci.On) Assert.True(dragged < 30, FormattableString.Invariant($"a bake while dragging takes {dragged:F1} ms"));
    }

    private static double BestBake(TextureImage diffuse, TextureImage mask)
    {
        var best = double.MaxValue;
        for (var run = 0; run < 5; run++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var baked = PaintBaker.Bake(diffuse, mask, new System.Numerics.Vector4(0.5f, 0.3f, 0.05f, 1f));
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
            Assert.Equal(diffuse.Rgba.Length, baked.Rgba.Length);
        }

        return best;
    }

    private static PreviewModel Gun(TextureImage texture)
    {
        var mesh = MeshData.Create("SK_Gun", [0, 0, 0, 10, 0, 0, 0, 10, 0], [0u, 1u, 2u]);
        return new PreviewModel("Gun", [new PreviewPart("SK_Gun", mesh, Texture, FTransform.Identity) { Material = Material }],
            new Dictionary<string, TextureImage>(StringComparer.OrdinalIgnoreCase) { [Texture] = texture });
    }

    /// <summary>A BC1 texture as <see cref="TextureDecoder.DecodeForGpu"/> leaves it: blocks, no pixels.</summary>
    private static TextureImage Compressed() =>
        new("T_Gun_D", 4, 4, [], "PF_DXT1", 0, 4, 4, true, false) { CompressedMips = [new byte[8]] };

    private static TextureImage Solid(byte value, int size = 4)
    {
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = rgba[i + 1] = rgba[i + 2] = value;
            rgba[i + 3] = 255;
        }

        return new TextureImage("T_Gun_D", size, size, rgba, "PF_B8G8R8A8", 0, size, size, true, false);
    }
}
