using System.Diagnostics;
using System.Numerics;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Mathematics;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Decals baked onto a UV-unwrapped tube (<see cref="DecalBaker"/>): they wrap round the curve, never reach the far side,
/// blend by their opacity over the paint as the game shows it, mirror, keep their order and report shared UVs.
/// </summary>
public sealed class DecalBakerTests(ITestOutputHelper output)
{
    private const int Size = 512;
    private static readonly TextureImage NoPaint = Image("Mask", 1, 0, 0, 0);

    [Fact]
    public void ADecalWrapsRoundTheCurveAndStaysOffTheFarSide()
    {
        var tube = Part("Body", FTransform.Identity);
        var hit = MeshRay.Hit(tube.Mesh, tube.Transform, new Vector3(0, 200, 0), -Vector3.UnitY)!;
        var layer = new DecalLayer { Position = hit.Position, Normal = hit.Normal, WidthCm = 20, HeightCm = 20 };
        var baked = DecalBaker.Bake(Grey(), NoPaint, [tube], DecalBaker.Stamps(layer, Disc(Vector3.One), tube.Transform));

        var (painted, farthest) = (0, 0f);
        for (var i = 0; i < Size * Size; i++)
        {
            if (baked.Diffuse.Rgba[i * 4] == 128)
            {
                continue;
            }

            painted++;
            var (u, v) = (((i % Size) + 0.5f) / Size, ((i / Size) + 0.5f) / Size);
            var angle = u * MathF.Tau;
            var arc = MathF.Sqrt(MathF.Pow(50 * MathF.Min(angle, MathF.Tau - angle), 2) + MathF.Pow((v * 100) - 50, 2)); // from (0, 50, 0) along the surface
            farthest = MathF.Max(farthest, arc);
            Assert.True(MathF.Cos(angle) > 0.9f, $"texel {i} at {angle} rad: the decal reached the far side");
        }

        var texel = 50 * MathF.Tau / Size * (100f / Size); // cm² of one texel on the tube
        output.WriteLine($"painted {painted} texels, farthest {farthest:F2} cm, expected area {MathF.PI * 100 / texel:F0}");
        Assert.InRange(farthest, 9.3f, 10.07f + 0.7f);   // wraps the full 10 cm round the curve, no further than its arc
        Assert.InRange(painted, MathF.PI * 100 / texel * 0.9f, MathF.PI * 100 / texel * 1.1f);
        Assert.Empty(baked.SharedUv);
    }

    [Fact]
    public void OpacityBlendsAndThePaintRuleShowsTheDecalOverAnyPaint()
    {
        var tube = Part("Body", FTransform.Identity);
        var layer = new DecalLayer { Position = new Vector3(0, 50, 0), Normal = Vector3.UnitY, WidthCm = 20, HeightCm = 20, Opacity = 0.5f };
        var white = DecalBaker.Bake(Grey(), NoPaint, [tube], DecalBaker.Stamps(layer, Disc(Vector3.One), tube.Transform));
        var centre = Centre();
        var half = Linear(128) + ((1f - Linear(128)) * 0.5f);
        Assert.InRange(Linear(white.Diffuse.Rgba[centre]), half - 0.01f, half + 0.01f); // half way to white

        // Painted everywhere (flat white mask): what the game shows is lerp(paint, decal, opacity) for any paint colour.
        var decal = new Vector3(0.05f, 0.3f, 0.8f);
        var painted = DecalBaker.Bake(Grey(), Image("Mask", 1, 255, 255, 255), [tube], DecalBaker.Stamps(layer with { Opacity = 0.6f }, Disc(decal), tube.Transform));
        foreach (var paint in new[] { new Vector3(0.9f, 0.6f, 0.05f), Vector3.Zero, Vector3.One })
        {
            var d = new Vector3(Linear(painted.Diffuse.Rgba[centre]), Linear(painted.Diffuse.Rgba[centre + 1]), Linear(painted.Diffuse.Rgba[centre + 2]));
            var shown = PaintRule.Shown(d, painted.Mask.Rgba, centre, paint, paint);
            var expected = Vector3.Lerp(paint, decal, 0.6f);
            Assert.True(Vector3.Distance(Srgb(shown), Srgb(expected)) <= 2f / 255f * MathF.Sqrt(3), $"{shown} vs {expected}");
        }

        Assert.Equal(Size, painted.Mask.Width); // the mask comes back at the diffuse's size
        Assert.Equal(255, painted.Mask.Rgba[5]); // and keeps the paint away from the decal
    }

    [Fact]
    public void MirrorPutsACopyOnTheOtherSide()
    {
        var tube = Part("Body", FTransform.Identity);
        var layer = new DecalLayer { Position = new Vector3(0, 50, 0), Normal = Vector3.UnitY, WidthCm = 20, HeightCm = 20, Mirror = true };
        var baked = DecalBaker.Bake(Grey(), NoPaint, [tube], DecalBaker.Stamps(layer, Disc(Vector3.One), tube.Transform));
        var (plus, minus) = (0, 0);
        for (var i = 0; i < Size * Size; i++)
        {
            if (baked.Diffuse.Rgba[i * 4] != 128)
            {
                var y = MathF.Cos(((i % Size) + 0.5f) / Size * MathF.Tau);
                (plus, minus) = y > 0.9f ? (plus + 1, minus) : y < -0.9f ? (plus, minus + 1) : throw new Xunit.Sdk.XunitException("painted beside both copies");
            }
        }

        Assert.True(plus > 1000 && Math.Abs(plus - minus) < plus / 20, $"+Y {plus}, -Y {minus}");
    }

    [Fact]
    public void APaletteSeedGivesTheSameColoursEveryTime()
    {
        var layer = new DecalLayer { ColourMode = DecalColourMode.Palette, Palette = ["#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#00FFFF", "#FF00FF"], Seed = 7 };
        uint[] defaults = [0x111111, 0x222222, 0x333333];
        Assert.Equal(layer.RegionColours(3, defaults), layer.RegionColours(3, defaults));
        Assert.NotEqual(layer.RegionColours(3, defaults), (layer with { Seed = 8 }).RegionColours(3, defaults));
        Assert.All(layer.RegionColours(3, defaults), c => Assert.Contains(DecalLayer.Hex(c), layer.Palette));
        Assert.Equal([new Vector3(1, 0, 0), new Vector3(1, 0, 0)], (layer with { ColourMode = DecalColourMode.Single, Colours = ["#FF0000"] }).RegionColours(2, defaults));
    }

    [Fact]
    public void TheLaterLayerIsOnTop()
    {
        var tube = Part("Body", FTransform.Identity);
        var layer = new DecalLayer { Position = new Vector3(0, 50, 0), Normal = Vector3.UnitY, WidthCm = 20, HeightCm = 20 };
        var red = DecalBaker.Stamps(layer, Disc(Vector3.UnitX), tube.Transform)[0];
        var blue = DecalBaker.Stamps(layer, Disc(Vector3.UnitZ), tube.Transform)[0];
        var centre = Centre();
        Assert.Equal(255, DecalBaker.Bake(Grey(), NoPaint, [tube], [red, blue]).Diffuse.Rgba[centre + 2]);
        Assert.Equal(255, DecalBaker.Bake(Grey(), NoPaint, [tube], [blue, red]).Diffuse.Rgba[centre]);
    }

    [Fact]
    public void PartsSharingTheTexelsAreReported()
    {
        var left = Part("BPC_Car_Door_Left", FTransform.Identity);
        var right = Part("BPC_Car_Door_Right", new FTransform(new FVector(300, 0, 0)));
        var layer = new DecalLayer { Position = new Vector3(0, 50, 0), Normal = Vector3.UnitY, WidthCm = 20, HeightCm = 20 };
        var baked = DecalBaker.Bake(Grey(), NoPaint, [left, right], DecalBaker.Stamps(layer, Disc(Vector3.One), left.Transform));
        Assert.Equal(["BPC_Car_Door_Right"], baked.SharedUv);
    }

    [Fact]
    public async Task AFullSizeBakeTakesUnderTwoSecondsOnAWorker()
    {
        var tube = new PreviewPart("Body", DecalMeshes.Cylinder(50, 100, 256, 64), null, FTransform.Identity);
        var layer = new DecalLayer { Position = new Vector3(0, 50, 0), Normal = Vector3.UnitY, WidthCm = 60, HeightCm = 30, Mirror = true };
        var design = DecalLibrary.Find("flames-3")!;
        var image = layer.Render(design.Rasterize(512), design);
        var diffuse = Image("D", 2048, 128, 128, 128);
        var clock = Stopwatch.StartNew();
        var baked = await Task.Run(() => DecalBaker.Bake(diffuse, Image("Mask", 1, 0, 255, 0), [tube], DecalBaker.Stamps(layer, image, tube.Transform)));
        output.WriteLine($"2048 bake: {clock.ElapsedMilliseconds} ms");
        Assert.Equal(2048, baked.Diffuse.Width);
        if (!Ci.On) Assert.True(clock.ElapsedMilliseconds < 2000, $"{clock.ElapsedMilliseconds} ms");
    }

    private static PreviewPart Part(string name, FTransform transform) =>
        new(name, DecalMeshes.Cylinder(50, 100, 128, 32), null, transform) { Attachment = "/Game/Cars/" + name };

    /// <summary>The texel (byte offset) under (0, 50, 0): u = 0, v = 0.5.</summary>
    private static int Centre() => (((Size / 2) * Size) + 1) * 4;

    private static TextureImage Grey() => Image("D", Size, 128, 128, 128);

    private static TextureImage Image(string name, int size, byte r, byte g, byte b)
    {
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (r, g, b, 255);
        }

        return new TextureImage(name, size, size, rgba, "PF_B8G8R8A8", 0, size, size, name == "D", false);
    }

    /// <summary>A solid disc filling its square, linear colour <paramref name="colour"/>.</summary>
    private static DecalImage Disc(Vector3 colour)
    {
        const int N = 64;
        var rgba = new float[N * N * 4];
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var a = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(N / 2f)) <= N / 2f ? 1f : 0f;
                var i = ((y * N) + x) * 4;
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (colour.X * a, colour.Y * a, colour.Z * a, a);
            }
        }

        return new DecalImage(N, N, rgba);
    }

    private static float Linear(byte v) => v / 255f <= 0.04045f ? v / 255f / 12.92f : MathF.Pow(((v / 255f) + 0.055f) / 1.055f, 2.4f);

    private static Vector3 Srgb(Vector3 linear)
    {
        static float S(float v) => v <= 0.0031308f ? 12.92f * v : (1.055f * MathF.Pow(v, 1 / 2.4f)) - 0.055f;
        return new Vector3(S(linear.X), S(linear.Y), S(linear.Z));
    }
}
