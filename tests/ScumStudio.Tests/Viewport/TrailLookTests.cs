using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// r37 (owner, 3.4.0: flat brown sheets with dark jagged rims near the coast): gravel road and dirt runway pieces, which the
/// game draws only into the landscape texture, are drawn see-through by their own coverage instead of as an opaque sheet.
/// </summary>
public sealed class TrailLookTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1f, 0.8f, 3f, 0.8f)]
    [InlineData(0f, 0.8f, 3f, 0f)]
    [InlineData(0.5f, 0.8f, 3f, 0.4f)]
    [InlineData(0.6f, 0.6f, 15f, 0.6f)]
    [InlineData(0.45f, 0.6f, 15f, 0f)]
    public void CoverageFollowsTheMaskThroughAmountAndContrast(float mask, float amount, float contrast, float expected) =>
        Assert.Equal(expected, TrailLook.Coverage(mask, amount, contrast), 3);

    [Fact]
    public void TheKeyEndsInTheColourMapsObjectPath()
    {
        var material = new MaterialInfo("MI", "/Game/M/MI.MI", [], [], [], [], "/Game/T/T_Trail_D.T_Trail_D", null, [])
        {
            OnlyIntoLandscape = true,
            CoverageTexture = "/Game/T/T_Mask.T_Mask",
            CoverageAmount = 0.6f,
            CoverageContrast = 15f,
        };

        var key = TrailLook.Key("/Game/T/T_Trail_D.T_Trail_D", material);

        Assert.True(TrailLook.IsKey(key));
        Assert.False(TrailLook.IsKey("/Game/T/T_Trail_D.T_Trail_D"));
        Assert.EndsWith(".T_Trail_D", key, StringComparison.Ordinal); // tools that name a texture by its key's last part see the colour map
        Assert.True(TrailLook.Tint(material).W < 0.999f); // the see-through pass
    }

    [Fact]
    public void TheGravelRoadIsDrawnByItsMaskNotAsASheet()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var road = new MaterialInspector(catalog).Inspect("/Game/ConZ_Files/Materials/Road/Gravel_Road/MI_Gravel_Road_01_New");
        var wall = new MaterialInspector(catalog).Inspect("/Game/ConZ_Files/Models/Objects/Outdoor/MotocrossTrack/Fence_01/MI_Wood_Old_02");

        Assert.True(road.OnlyIntoLandscape);
        Assert.False(wall.OnlyIntoLandscape);
        Assert.EndsWith("/T_DirtRoad_Mask.T_DirtRoad_Mask", road.CoverageTexture, StringComparison.Ordinal);

        var image = TrailLook.Compose(catalog, TrailLook.Key(road.BaseColorTexture!, road), 256);
        var alphas = Enumerable.Range(0, image.Width * image.Height).Select(i => image.Rgba[(i * 4) + 3]).ToList();
        var clear = alphas.Count(a => a == 0) / (double)alphas.Count;
        output.WriteLine($"amount {road.CoverageAmount}, contrast {road.CoverageContrast}: alpha {alphas.Min()}..{alphas.Max()}, {clear:P0} of the piece shows the ground only");
        Assert.True(clear > 0.2, "the sides of the piece must let the ground show");
        Assert.True(alphas.Max() > 100, "the wheel tracks must show");
    }
}
