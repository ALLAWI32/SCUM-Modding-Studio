using ScumStudio.Assets.Landscape;
using ScumStudio.Tests.Level;

namespace ScumStudio.Tests.Assets;

/// <summary>Paint layer decoding: pure texel mapping tests and the facts measured on cell A_0 of the map slice.</summary>
public sealed class LandscapeLayerReaderTests
{
    [Theory]
    [InlineData("Forest_Ground_LayerInfo", "Forest_Ground")]
    [InlineData("Seabed_LayerInfo", "Seabed")]
    [InlineData("DataLayer", "DataLayer")]
    public void LayerNameOf_StripsTheLayerInfoSuffix(string info, string name) => Assert.Equal(name, LandscapeLayerReader.LayerNameOf(info));

    [Theory]
    [InlineData(0.5f / 256f, 256, 0)]
    [InlineData(128.5f / 256f, 256, 128)]
    [InlineData(64.5f / 128f, 128, 64)]
    public void WeightmapOrigin_PointsAtTheTexelCentre(float offset, int size, int origin) =>
        Assert.Equal(origin, LandscapeLayerReader.WeightmapOrigin(offset, size));

    [Fact]
    public void ExtractChannel_RoundTripsASyntheticWeightmap()
    {
        // 256² RGBA; channel 2 (B) encodes the texel column, channel 3 (A) the row; 2 subsections of 127 quads.
        const int size = 256;
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                rgba[(y * size + x) * 4 + 2] = (byte)x;
                rgba[(y * size + x) * 4 + 3] = (byte)y;
            }
        }

        var columns = new byte[255 * 255];
        var rows = new byte[255 * 255];
        LandscapeLayerReader.ExtractChannel(rgba, size, size, 0, 0, 2, 254, 127, 2, columns);
        LandscapeLayerReader.ExtractChannel(rgba, size, size, 0, 0, 3, 254, 127, 2, rows);
        foreach (var q in new[] { 0, 1, 126, 127, 128, 200, 254 })
        {
            var texel = LandscapeExtractor.TexelIndex(q, 127, 2);
            Assert.Equal(texel, columns[(5 * 255) + q]);
            Assert.Equal(texel, rows[(q * 255) + 5]);
        }

        Assert.Equal(127, columns[127]);
        Assert.Equal(129, columns[128]);
        Assert.Equal(255, columns[254]);

        // A window offset into a shared texture, and reads past the edge return 0.
        var shifted = new byte[8 * 8];
        LandscapeLayerReader.ExtractChannel(rgba, size, size, 250, 3, 2, 7, 7, 1, shifted);
        Assert.Equal(250, shifted[0]);
        Assert.Equal(255, shifted[5]);
        Assert.Equal(0, shifted[6]);
        Assert.Throws<ArgumentOutOfRangeException>(() => LandscapeLayerReader.ExtractChannel(rgba, size, size, 0, 0, 4, 7, 7, 1, shifted));
    }

    [Fact]
    public void Summarize_WeightsComponentsBySampleCount()
    {
        var full = Enumerable.Repeat((byte)255, 4).ToArray();
        var half = new byte[] { 255, 255, 0, 0 };
        var empty = new byte[4];
        var a = new LandscapeComponentLayers("A", (0, 0), 1, [new LandscapeLayerWeights("Grass", null, true, full)], null, [], 1, []);
        var b = new LandscapeComponentLayers("B", (1, 0), 1,
            [new LandscapeLayerWeights("Grass", null, true, half), new LandscapeLayerWeights("Sand", null, true, half.Reverse().ToArray()),
             new LandscapeLayerWeights("EraseFoliage", null, false, empty)], null, [], 3, []);
        var areas = LandscapeLayerReader.Summarize([a, b]);
        Assert.Equal(["Grass", "Sand", "EraseFoliage"], areas.Select(x => x.Name));
        Assert.Equal(0.75, areas[0].AreaShare, 6);
        Assert.Equal(2, areas[0].Components);
        Assert.Equal(0.25, areas[1].AreaShare, 6);
        Assert.False(areas[2].WeightBlended);

        Assert.Equal([("Grass", 1f, true)], b.WeightsAt(0, 0).Select(w => (w.Name, w.Weight, w.WeightBlended)));
        Assert.Equal("Sand", b.WeightsAt(0, 1)[0].Name);
        Assert.False(b.IsHole(0, 0));
    }

    [MapSliceFact]
    public void Slice_A01_Component20_IsAllSeabed()
    {
        using var catalog = MapSlice.Open();
        var components = LandscapeLayerReader.ReadPackage(catalog, MapSlice.MapsPath + "Landscape_A_0_1");
        Assert.Equal(4, components.Count);
        var c20 = Assert.Single(components, c => c.Name == "LandscapeComponent_20");
        Assert.Equal((-2032, 0), c20.SectionBase);
        Assert.Equal(255, c20.SampleCount);
        var layer = Assert.Single(c20.Layers);
        Assert.Equal("Seabed", layer.Name);
        Assert.True(layer.WeightBlended);
        Assert.EndsWith("Seabed_LayerInfo", layer.LayerInfoPath, StringComparison.Ordinal);
        Assert.All(layer.Weights, w => Assert.Equal(255, w));
        Assert.Null(c20.Holes);
        Assert.Empty(c20.Warnings);
    }

    [MapSliceFact]
    public void Slice_A04d_Component44_HasTenAllocations_WithEraseFoliageNotBlended()
    {
        using var catalog = MapSlice.Open();
        var components = LandscapeLayerReader.ReadPackage(catalog, MapSlice.MapsPath + "Landscape_A_0_4d");
        var c44 = Assert.Single(components, c => c.Name == "LandscapeComponent_44");
        Assert.Equal(10, c44.AllocationCount);
        Assert.Equal(10, c44.Layers.Count);
        Assert.Equal(
            ["Forest_Ground", "Rocky_Soil_01", "Dry_Grass_01", "Forest_Continental_01", "Default_Slope_Height", "EraseFoliage", "Gravel", "River_Weeds", "River_Pebbles", "River_Reeds"],
            c44.Layers.Select(l => l.Name));
        Assert.False(c44.Find("EraseFoliage")!.WeightBlended);
        Assert.All(c44.Layers.Where(l => l.Name != "EraseFoliage"), l => Assert.True(l.WeightBlended, l.Name));
        Assert.InRange(c44.Find("Forest_Ground")!.Coverage, 0.5, 0.6);

        var c37 = Assert.Single(components, c => c.Name == "LandscapeComponent_37");
        Assert.True(c37.Grass.Count >= 10, $"{c37.Grass.Count} grass types");
        Assert.All(c37.Grass, g => Assert.Equal(65_025, g.Density.Length));
        Assert.All(c37.Grass, g => Assert.False(string.IsNullOrEmpty(g.GrassTypeName)));
    }

    [MapSliceFact]
    public void Slice_CellA0_BlendedWeightsSumTo255_AndAreasMatchTheResearch()
    {
        using var catalog = MapSlice.Open();
        var all = new List<LandscapeComponentLayers>();
        foreach (var tile in A0Tiles())
        {
            all.AddRange(LandscapeLayerReader.ReadPackage(catalog, MapSlice.MapsPath + tile, includeGrass: false));
        }

        Assert.Equal(64, all.Count);
        var maxDeviation = 0;
        foreach (var c in all)
        {
            var blended = c.Layers.Where(l => l.WeightBlended).ToList();
            for (var i = 0; i < c.SampleCount * c.SampleCount; i++)
            {
                var sum = 0;
                foreach (var layer in blended)
                {
                    sum += layer.Weights[i];
                }

                maxDeviation = Math.Max(maxDeviation, Math.Abs(sum - 255));
            }
        }

        Assert.True(maxDeviation <= 1, $"max |sum - 255| = {maxDeviation}");
        var areas = LandscapeLayerReader.Summarize(all).ToDictionary(a => a.Name);
        Assert.InRange(areas["Forest_Ground"].AreaShare, 0.345, 0.36);
        Assert.InRange(areas["Seabed"].AreaShare, 0.24, 0.255);
        Assert.InRange(areas["Default_Slope_Height"].AreaShare, 0.19, 0.205);
        // 20 distinct allocations in A_0: 19 paint layers plus the DataLayer visibility (hole) layer of 4 components.
        Assert.Equal(19, areas.Count);
        Assert.Equal(4, all.Count(c => c.Holes is not null));
    }

    internal static IEnumerable<string> A0Tiles()
    {
        foreach (var a in new[] { "1", "2", "3", "4" })
        {
            foreach (var b in new[] { string.Empty, "b", "c", "d" })
            {
                yield return $"Landscape_A_0_{a}{b}";
            }
        }
    }
}
