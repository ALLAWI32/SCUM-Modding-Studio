using System.Numerics;
using ScumStudio.Assets.Landscape;
using ScumStudio.Assets.Textures;
using ScumStudio.Tests.Assets;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>Ground colour baking from paint layers: palette blend, slope rock, sand/underwater rules, debug modes.</summary>
public sealed class TerrainAlbedoBakerTests
{
    private static readonly TerrainLayerCatalog Catalog = TerrainLayerCatalog.Default;

    [Fact]
    public void SingleLayer_OnFlatHighGround_IsTheLayerColour_PaddedToAPowerOfTwo()
    {
        var surface = LandscapeExtractorTests.Ramp(6, 150f, Vector2.Zero, (_, _) => 5000f);
        var layers = Layers(surface, ("Forest_Ground", _ => 255));
        var albedo = TerrainAlbedoBaker.Bake(surface, layers)!;
        Assert.Equal(8, albedo.Size); // 7 samples → 8 texels, the last column/row repeats sample 6
        Assert.Equal(8 * 8 * 4, albedo.Rgba.Length);
        var forest = Catalog.Resolve("Forest_Ground").Color;
        Assert.All(Enumerable.Range(0, 64), i => Assert.Equal(forest, Pixel(albedo, i % 8, i / 8)));
        Assert.Equal(255, albedo.Rgba[3]);
    }

    [Fact]
    public void TwoLayers_BlendByWeight_InLinearSpace()
    {
        var surface = LandscapeExtractorTests.Ramp(3, 150f, Vector2.Zero, (_, _) => 5000f);
        var layers = Layers(surface, ("Gravel", i => 128), ("Grass_Continental", i => 127));
        var albedo = TerrainAlbedoBaker.Bake(surface, layers)!;
        var expected = TerrainColor.FromLinear((Catalog.Resolve("Gravel").Color.ToLinear() * 128 + Catalog.Resolve("Grass_Continental").Color.ToLinear() * 127) / 255f);
        AssertNear(expected, Pixel(albedo, 1, 1));
    }

    [Fact]
    public void HiddenAndNonBlendedLayers_AreIgnored()
    {
        var surface = LandscapeExtractorTests.Ramp(3, 150f, Vector2.Zero, (_, _) => 5000f);
        var layers = Layers(surface, ("Forest_Ground", _ => 255), ("EraseFoliage", _ => 255));
        layers = layers with { Layers = [layers.Layers[0], layers.Layers[1] with { WeightBlended = false }] };
        Assert.Equal(Catalog.Resolve("Forest_Ground").Color, Pixel(TerrainAlbedoBaker.Bake(surface, layers)!, 2, 2));
    }

    [Fact]
    public void SteepSlopes_TurnToRock()
    {
        // 63° slope (300 cm rise per 150 cm quad): normal Z ≈ 0.45, below the 0.70..0.82 rock blend range → fully rock.
        var surface = LandscapeExtractorTests.Ramp(4, 150f, Vector2.Zero, (x, _) => 5000f + 300f * x);
        var albedo = TerrainAlbedoBaker.Bake(surface, Layers(surface, ("Forest_Ground", _ => 255)))!;
        Assert.Equal(Catalog.Rules.Rock.Color, Pixel(albedo, 2, 2));
    }

    [Fact]
    public void AutoLayer_IsSandAtTheBeach_UnderwaterRockDeepDown_AndItsOwnColourInland()
    {
        var auto = Catalog.Resolve("Default_Slope_Height");
        var settings = new TerrainBakeSettings { ShoreTint = false };
        Assert.Equal(Catalog.Rules.Sand.Color, BakeFlat(100f));
        Assert.Equal(Catalog.Rules.Underwater.Color, BakeFlat(-1000f));
        Assert.Equal(auto.Color, BakeFlat(3000f));

        TerrainColor BakeFlat(float z)
        {
            var surface = LandscapeExtractorTests.Ramp(2, 150f, Vector2.Zero, (_, _) => z);
            return Pixel(TerrainAlbedoBaker.Bake(surface, Layers(surface, ("Default_Slope_Height", _ => 255)), settings)!, 1, 1);
        }
    }

    [Fact]
    public void ShoreTint_DarkensWetGround_AndTintsUnderwater()
    {
        var c = new Vector3(0.5f, 0.4f, 0.3f);
        Assert.Equal(c, TerrainAlbedoBaker.ShoreTint(c, 500f));
        var wet = TerrainAlbedoBaker.ShoreTint(c, 0f);
        Assert.True(wet.X < c.X && wet.Y < c.Y);
        var deep = TerrainAlbedoBaker.ShoreTint(c, -5000f);
        Assert.True(deep.Z / deep.X > c.Z / c.X, "underwater is bluer");
    }

    [Fact]
    public void LayersMode_UsesDebugColours_HeightMode_UsesTheRamp_PlainBakesNothing()
    {
        var surface = LandscapeExtractorTests.Ramp(3, 150f, Vector2.Zero, (_, _) => 5000f);
        var layers = Layers(surface, ("Seabed", _ => 255));
        var debug = TerrainAlbedoBaker.Bake(surface, layers, new TerrainBakeSettings { Mode = GroundMode.Layers })!;
        Assert.Equal(Catalog.Resolve("Seabed").DebugColor, Pixel(debug, 0, 0));

        var height = TerrainAlbedoBaker.Bake(surface, layers, new TerrainBakeSettings { Mode = GroundMode.Height, HeightMinCm = -1000f, HeightMaxCm = 10000f })!;
        Assert.Equal(TerrainColor.FromLinear(TerrainAlbedoBaker.HeightRamp(5000f, -1000f, 10000f)), Pixel(height, 1, 1));
        Assert.Null(TerrainAlbedoBaker.Bake(surface, layers, new TerrainBakeSettings { Mode = GroundMode.Plain }));

        var low = TerrainAlbedoBaker.HeightRamp(-500f, -1000f, 10000f);
        var high = TerrainAlbedoBaker.HeightRamp(9900f, -1000f, 10000f);
        Assert.True(low.Z > low.X, "below sea level is blue");
        Assert.True(high.X > 0.8f && high.Y > 0.8f, "peaks are white");
    }

    [Fact]
    public void Holes_AreTransparent_AndMissingLayersFallBackToTheAutoRule()
    {
        var surface = LandscapeExtractorTests.Ramp(2, 150f, Vector2.Zero, (_, _) => 5000f);
        var layers = new LandscapeComponentLayers("C", (0, 0), 2, [], [255, 0, 0, 0, 0, 0, 0, 0, 0], [], 1, []);
        var albedo = TerrainAlbedoBaker.Bake(surface, layers)!;
        Assert.Equal(0, albedo.Rgba[3]);
        Assert.Equal(255, albedo.Rgba[(1 * albedo.Size + 1) * 4 + 3]);
        Assert.Equal(Catalog.Resolve("Default_Slope_Height").Color, Pixel(albedo, 1, 1));
        // No layer data at all bakes the same way.
        Assert.Equal(Pixel(albedo, 1, 1), Pixel(TerrainAlbedoBaker.Bake(surface, null)!, 1, 1));
    }

    [Fact]
    public void BakeAll_IsIndexAligned()
    {
        var a = LandscapeExtractorTests.Ramp(2, 150f, Vector2.Zero, (_, _) => 5000f);
        var b = LandscapeExtractorTests.Ramp(2, 150f, new Vector2(300f, 0f), (_, _) => -5000f);
        var baked = TerrainAlbedoBaker.BakeAll([(a, null), (b, null)], new TerrainBakeSettings(), out var elapsed);
        Assert.Equal(2, baked.Count);
        Assert.NotEqual(Pixel(baked[0]!, 1, 1), Pixel(baked[1]!, 1, 1));
        Assert.True(elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public void Textures_BlendByWeight_AtTheBakedResolution()
    {
        // 10 quads of 100 cm; Forest_Ground on samples x < 5, Gravel on x ≥ 5; solid red / blue stand-ins for the layer textures.
        var surface = LandscapeExtractorTests.Ramp(10, 100f, Vector2.Zero, (_, _) => 5000f);
        var layers = Layers(surface, ("Forest_Ground", i => (byte)(i % 11 < 5 ? 255 : 0)), ("Gravel", i => (byte)(i % 11 < 5 ? 0 : 255)));
        var textures = new TerrainLayerTextures(new Dictionary<string, TextureImage>
        {
            [Catalog.Resolve("Forest_Ground").DiffuseTexture!] = Solid(255, 0, 0),
            [Catalog.Resolve("Gravel").DiffuseTexture!] = Solid(0, 0, 255),
        });
        var albedo = TerrainAlbedoBaker.Bake(surface, layers, new TerrainBakeSettings { Textures = textures, ShoreTint = false })!;
        Assert.Equal(TerrainBakeSettings.DefaultTexturedSize, albedo.Size);

        var small = TerrainAlbedoBaker.Bake(surface, layers, new TerrainBakeSettings { Textures = textures, TextureSize = 64, ShoreTint = false })!;
        Assert.Equal(64, small.Size);
        // Texel t sits at quad (t + 0.5) * 16 / 64 - 0.5: texel 8 → quad 1.6 (forest), texel 48 → quad 11.6 → clamped to 10 (gravel).
        Assert.Equal(new TerrainColor(255, 0, 0), Pixel(small, 8, 8));
        Assert.Equal(new TerrainColor(0, 0, 255), Pixel(small, 48, 8));
        // Between samples 4 and 5 the weights cross: texel 19 (quad 4.375) is 5/8 forest, texel 20 (quad 4.625) 3/8.
        var left = Pixel(small, 19, 30);
        var right = Pixel(small, 20, 30);
        Assert.True(left.R > left.B && left.B > 100, left.ToString());
        Assert.True(right.B > right.R && right.R > 100, right.ToString());
        Assert.Equal(TerrainColor.FromLinear(new Vector3(0.625f, 0f, 0.375f)), left);
    }

    [Fact]
    public void Textures_RepeatAtTheCatalogTiling_InWorldCentimetres()
    {
        // A black|white texture tiled every 400 cm over 100 cm quads (one texel per quad): texel columns 1, 5 are black, 3, 7 white.
        var surface = LandscapeExtractorTests.Ramp(8, 100f, new Vector2(-2000f, 0f), (_, _) => 5000f);
        var catalog = Catalog.WithOverrides("""{ "layers": [ { "name": "Forest_Ground", "tilingCm": 400 } ] }""");
        var layers = Layers(surface, ("Forest_Ground", _ => 255));
        var image = new TextureImage("Stripes", 2, 1, [0, 0, 0, 255, 255, 255, 255, 255], "PF_B8G8R8A8", 0, 2, 1, IsSrgb: true, IsNormalMap: false);
        var textures = new TerrainLayerTextures(new Dictionary<string, TextureImage> { [catalog.Resolve("Forest_Ground").DiffuseTexture!] = image });
        var settings = new TerrainBakeSettings { Catalog = catalog, Textures = textures, TextureSize = 16, MacroVariation = 0f, ShoreTint = false };
        var albedo = TerrainAlbedoBaker.Bake(surface, layers, settings)!;
        Assert.Equal(new TerrainColor(0, 0, 0), Pixel(albedo, 1, 4));
        Assert.Equal(new TerrainColor(255, 255, 255), Pixel(albedo, 3, 4));
        Assert.Equal(new TerrainColor(0, 0, 0), Pixel(albedo, 5, 4));
        Assert.Equal(new TerrainColor(255, 255, 255), Pixel(albedo, 7, 4));
        Assert.Equal(new TerrainColor(188, 188, 188), Pixel(albedo, 2, 4)); // halfway between the texels: linear 0.5
        // Mixing in the macro repeat changes the plain stripes.
        var macro = TerrainAlbedoBaker.Bake(surface, layers, settings with { MacroVariation = 0.5f })!;
        Assert.NotEqual(new TerrainColor(0, 0, 0), Pixel(macro, 1, 4));
    }

    [Fact]
    public void MipChain_BoxFiltersToTheMean_PicksTheLevelForTheTexelDensity_AndWraps()
    {
        // 4×4 sRGB: left half black, right half white.
        var rgba = new byte[4 * 4 * 4];
        for (var i = 0; i < 16; i++)
        {
            var v = i % 4 < 2 ? (byte)0 : (byte)255;
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = v;
            rgba[i * 4 + 3] = 255;
        }

        var chain = new LinearMipChain(new TextureImage("T", 4, 4, rgba, "PF_B8G8R8A8", 0, 4, 4, IsSrgb: true, IsNormalMap: false));
        Assert.Equal(3, chain.LevelCount);
        Assert.True(Vector3.Distance(new Vector3(0.5f), chain.Mean) < 1e-5f);
        Assert.Equal(0, chain.LevelFor(4f));
        Assert.Equal(1, chain.LevelFor(2f));
        Assert.Equal(2, chain.LevelFor(1f));
        Assert.Equal(0, chain.LevelFor(100f));
        Assert.Equal(2, chain.LevelFor(0.1f));
        Assert.Equal(Vector3.Zero, chain.Sample(0.125f, 0.5f, 0)); // texel 0 centre
        Assert.Equal(Vector3.One, chain.Sample(0.625f, 0.5f, 0)); // texel 2 centre
        Assert.Equal(Vector3.Zero, chain.Sample(1.125f, 0.5f, 0)); // wraps
        Assert.Equal(Vector3.Zero, chain.Sample(-0.875f, 0.5f, 0)); // wraps below zero
        Assert.Equal(Vector3.One, chain.Sample(0.75f, 0.25f, 1)); // 2×2 mip keeps the halves
    }

    private static TextureImage Solid(byte r, byte g, byte b)
    {
        var rgba = new byte[2 * 2 * 4];
        for (var i = 0; i < 4; i++)
        {
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = 255;
        }

        return new TextureImage("Solid", 2, 2, rgba, "PF_B8G8R8A8", 0, 2, 2, IsSrgb: true, IsNormalMap: false);
    }

    internal static LandscapeComponentLayers Layers(LandscapeSurface surface, params (string Name, Func<int, byte> Weight)[] layers)
    {
        var n = surface.SampleCount;
        var list = layers.Select(l => new LandscapeLayerWeights(l.Name, null, true, Enumerable.Range(0, n * n).Select(l.Weight).ToArray())).ToList();
        return new LandscapeComponentLayers(surface.Name, surface.SectionBase, surface.ComponentSizeQuads, list, null, [], list.Count, []);
    }

    internal static void AssertNear(TerrainColor expected, TerrainColor actual, int tolerance = 1) =>
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance,
            $"expected {expected}, got {actual}");

    internal static TerrainColor Pixel(TerrainAlbedo albedo, int x, int y)
    {
        var o = (y * albedo.Size + x) * 4;
        return new TerrainColor(albedo.Rgba[o], albedo.Rgba[o + 1], albedo.Rgba[o + 2]);
    }
}
