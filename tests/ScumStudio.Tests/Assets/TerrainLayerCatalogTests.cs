using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Assets.Textures;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Assets;

/// <summary>The embedded terrain layer table, user overrides, hashed colours for unknown layers and texture mean colours.</summary>
public sealed class TerrainLayerCatalogTests
{
    /// <summary>The 19 paint layers (+ DataLayer) allocated in cell A_0 of the island.</summary>
    public static readonly string[] A0Layers =
    [
        "Forest_Ground", "Seabed", "Default_Slope_Height", "River_Weeds", "Grass_Continental", "Dry_Grass_01", "Gravel", "Forest_Continental_02",
        "Forest_Continental_01", "Beach_Sand", "River_Reeds", "Beach_Gravel", "River_Pebbles", "EraseFoliage", "Rocky_Soil_01", "Asphalt", "Gravel_02",
        "Asphalt_Rubble", "White_Gravel", "DataLayer",
    ];

    [Fact]
    public void Default_ResolvesEveryA0Layer()
    {
        var catalog = TerrainLayerCatalog.Parse(TerrainLayerCatalog.ReadEmbeddedJson());
        foreach (var name in A0Layers)
        {
            var style = catalog.Resolve(name);
            Assert.True(style.IsKnown, name);
            Assert.Equal(TerrainColorSource.Fallback, style.ColorSource);
        }

        Assert.Empty(catalog.UnknownLayers);
        Assert.Equal(TerrainLayerRule.SlopeHeight, catalog.Resolve("Default_Slope_Height").Rule);
        Assert.Equal(TerrainLayerRule.Hidden, catalog.Resolve("EraseFoliage").Rule);
        Assert.Equal(TerrainLayerRule.Hidden, catalog.Resolve("DataLayer").Rule);
        Assert.Equal(new TerrainColor(86, 72, 50), catalog.Resolve("Forest_Ground").Color);
        Assert.Equal("/Game/ConZ_Files/Landscape/LandscapeTextures/T_PineForestGround_D", catalog.Resolve("Forest_Ground").DiffuseTexture);
        Assert.Equal("/Game/ConZ_Files/Landscape/LandscapeTextures/T_PineForestGround_NHR", catalog.Resolve("Forest_Ground").NhrTexture);
        Assert.Equal("/Game/ConZ_Files/Landscape/Textures/T_Asphalt_D", catalog.Resolve("Asphalt").DiffuseTexture);
        Assert.Equal(600f, catalog.Resolve("Gravel").TilingCm);
        Assert.Equal(0.82f, catalog.Rules.RockSlopeStartNz);
        Assert.Equal("/Game/ConZ_Files/Landscape/LandscapeTextures/T_Rocks_D", catalog.Rules.Rock.DiffuseTexture);

        // Layers view colours are distinct for the A_0 layers that are drawn.
        var drawn = A0Layers.Where(n => catalog.Resolve(n).Rule != TerrainLayerRule.Hidden).Select(n => catalog.Resolve(n).DebugColor).ToList();
        Assert.Equal(drawn.Count, drawn.Distinct().Count());
    }

    [Fact]
    public void UnknownLayer_GetsAStableHashedColour_AndIsReported()
    {
        var a = TerrainLayerCatalog.Parse(TerrainLayerCatalog.ReadEmbeddedJson());
        var b = TerrainLayerCatalog.Parse(TerrainLayerCatalog.ReadEmbeddedJson());
        var styleA = a.Resolve("Moon_Dust");
        var styleB = b.Resolve("moon_dust");
        Assert.False(styleA.IsKnown);
        Assert.Equal(TerrainColorSource.Hashed, styleA.ColorSource);
        Assert.Equal(styleA.Color, styleB.Color);
        Assert.Equal(styleA.DebugColor, styleB.DebugColor);
        Assert.NotEqual(a.Resolve("Mars_Dust").Color, styleA.Color);
        Assert.Equal(["Mars_Dust", "Moon_Dust"], a.UnknownLayers);
        Assert.Equal(TerrainLayerCatalog.HashedColor("x", vivid: true), TerrainLayerCatalog.HashedColor("X", vivid: true));
    }

    [Fact]
    public void Overrides_ReplaceOnlyTheGivenFields_AndSupportWildcards()
    {
        var catalog = TerrainLayerCatalog.Default.WithOverrides("""
            {
              // comments are allowed
              "layers": [
                { "name": "Forest_Ground", "color": "#102030" },
                { "name": "Lava", "diffuse": "T_Lava_D", "color": "#FF4000", "tilingCm": 250 },
                { "name": "Snow*", "color": "#FAFAFA", "rule": "none" },
              ],
              "autoRules": { "seaLevelCm": 50, "rock": { "color": "#010203" } }
            }
            """);
        var forest = catalog.Resolve("Forest_Ground");
        Assert.Equal(new TerrainColor(0x10, 0x20, 0x30), forest.Color);
        Assert.Equal("/Game/ConZ_Files/Landscape/LandscapeTextures/T_PineForestGround_D", forest.DiffuseTexture);
        var lava = catalog.Resolve("Lava");
        Assert.True(lava.IsKnown);
        Assert.Equal("/Game/ConZ_Files/Landscape/LandscapeTextures/T_Lava_D", lava.DiffuseTexture);
        Assert.Equal(250f, lava.TilingCm);
        // Exact entries win over wildcards; other Snow* names use the wildcard.
        Assert.Equal(new TerrainColor(0xE4, 0xE8, 0xEC), catalog.Resolve("Snow").Color);
        Assert.Equal(new TerrainColor(0xFA, 0xFA, 0xFA), catalog.Resolve("Snow_Deep").Color);
        Assert.Equal("Snow_Deep", catalog.Resolve("Snow_Deep").Name);
        Assert.Equal(50f, catalog.Rules.SeaLevelCm);
        Assert.Equal(new TerrainColor(1, 2, 3), catalog.Rules.Rock.Color);
        Assert.Equal(TerrainLayerCatalog.Default.Rules.Sand, catalog.Rules.Sand);
        // The default is untouched.
        Assert.Equal(new TerrainColor(86, 72, 50), TerrainLayerCatalog.Default.Resolve("Forest_Ground").Color);

        Assert.Throws<InvalidDataException>(() => TerrainLayerCatalog.Default.WithOverrides("""{ "layers": [ { "name": "X", "rule": "sparkle" } ] }"""));
        Assert.Throws<InvalidDataException>(() => TerrainLayerCatalog.Default.WithOverrides("""{ "layers": [ { "color": "#000000" } ] }"""));
        Assert.Throws<FormatException>(() => TerrainLayerCatalog.Default.WithOverrides("""{ "layers": [ { "name": "X", "color": "green" } ] }"""));
    }

    [Fact]
    public void LoadDefault_AppliesAnOverrideFile_AndIgnoresABrokenOne()
    {
        var dir = Directory.CreateTempSubdirectory("scumstudio-layers-");
        try
        {
            var good = Path.Combine(dir.FullName, "good.json");
            File.WriteAllText(good, """{ "layers": [ { "name": "Seabed", "color": "#000080" } ] }""");
            Assert.Equal(new TerrainColor(0, 0, 0x80), TerrainLayerCatalog.LoadDefault(good).Resolve("Seabed").Color);

            var broken = Path.Combine(dir.FullName, "broken.json");
            File.WriteAllText(broken, "{ not json");
            var fallback = TerrainLayerCatalog.LoadDefault(broken);
            Assert.Equal(TerrainLayerCatalog.Default.Resolve("Seabed").Color, fallback.Resolve("Seabed").Color);
            Assert.Contains(fallback.Warnings, w => w.Contains("broken.json", StringComparison.Ordinal));

            Assert.Same(TerrainLayerCatalog.Default, TerrainLayerCatalog.LoadDefault(Path.Combine(dir.FullName, "missing.json")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void TerrainColor_ParsesFormatsAndConverts()
    {
        Assert.Equal(new TerrainColor(0xAB, 0xCD, 0xEF), TerrainColor.Parse("#abcdef"));
        Assert.Equal(new TerrainColor(1, 2, 3), TerrainColor.Parse("010203"));
        Assert.Equal("#ABCDEF", new TerrainColor(0xAB, 0xCD, 0xEF).ToString());
        var c = new TerrainColor(188, 170, 130);
        Assert.Equal(c, TerrainColor.FromLinear(c.ToLinear()));
    }

    [Fact]
    public void MeanColor_AveragesInLinearSpace()
    {
        // Half black, half white (sRGB) averages to linear 0.5 = sRGB 188.
        var rgba = new byte[] { 0, 0, 0, 255, 255, 255, 255, 255 };
        var image = new TextureImage("T", 2, 1, rgba, "PF_B8G8R8A8", 0, 2, 1, IsSrgb: true, IsNormalMap: false);
        Assert.Equal(new TerrainColor(188, 188, 188), TerrainLayerCatalog.MeanColor(image));
        var linear = image with { IsSrgb = false };
        Assert.Equal(new TerrainColor(128, 128, 128), TerrainLayerCatalog.MeanColor(linear));
    }

    [FixturesFact]
    public void WithTextureColors_UsesTheTextureMean_WhenTheTextureExists()
    {
        // Stand-in for a T_*_D layer texture (the layer textures are not in the fixtures): a stock vehicle texture.
        const string texture = "/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Textures/T_Interior_D";
        using var assets = AssetCatalog.Open(FixturePaths.OrigRoot);
        var catalog = TerrainLayerCatalog.Default.WithOverrides($$"""{ "layers": [ { "name": "Forest_Ground", "diffuse": "{{texture}}" } ] }""");
        var resolved = catalog.WithTextureColors(assets, out var missing);
        var forest = resolved.Resolve("Forest_Ground");
        Assert.Equal(TerrainColorSource.Texture, forest.ColorSource);
        var expected = TerrainLayerCatalog.MeanColor(TextureDecoder.Decode(assets.LoadObject<UTexture2D>(texture + ".T_Interior_D"), 64));
        Assert.Equal(expected, forest.Color);
        Assert.DoesNotContain(texture, missing);
        Assert.Contains("/Game/ConZ_Files/Landscape/LandscapeTextures/T_Beach_Sand_D", missing);
        Assert.Equal(TerrainColorSource.Fallback, resolved.Resolve("Beach_Sand").ColorSource);
    }

    [MapSliceFact]
    public void Slice_HasNoLayerTextures_SoEveryLayerKeepsItsFallback()
    {
        using var assets = MapSlice.Open();
        var resolved = TerrainLayerCatalog.Default.WithTextureColors(assets, out var missing);
        Assert.All(A0Layers, n => Assert.NotEqual(TerrainColorSource.Texture, resolved.Resolve(n).ColorSource));
        Assert.Contains("/Game/ConZ_Files/Landscape/LandscapeTextures/T_PineForestGround_D", missing);
        Assert.True(missing.Count >= 20, $"{missing.Count} missing");
    }
}
