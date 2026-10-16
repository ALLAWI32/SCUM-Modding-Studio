using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Materials;
using ScumStudio.Assets.Textures;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Viewport.Decals;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// The stock colour masks <see cref="PaintRule"/> rests on (SCUM_FIXTURES: the loose stock Kinglet and WolfsWagen materials,
/// M_Car_01 and the dummy masks): paint is in green and blue, never in red or alpha.
/// </summary>
public sealed class PaintRuleFixtureTests(ITestOutputHelper output)
{
    private const string PlaneMask = "/Game/ConZ_Files/Models/Vehicles2/Plane_01/Textures/T_Plane_01_Body_Mask_A.T_Plane_01_Body_Mask_A";
    private const string WolfsWagenMask = "/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Textures/T_WWBody_2_Color_UV2.T_WWBody_2_Color_UV2";
    private const string Basic = "/Game/ConZ_Files/Textures/Basic/";

    [FixturesFact]
    public void TheStockMasksPaintThroughGreenAndBlueOnly()
    {
        using var catalog = AssetCatalog.OpenLoose(FixturePaths.OrigRoot, new AssetCatalogOptions { LooseOverlays = [FixturePaths.OrigExtraRoot] });
        var inspector = new MaterialInspector(catalog);
        var plane = inspector.Inspect("/Game/ConZ_Files/Models/Vehicles2/Plane_01/Materials/MI_Plane_01_Body_A.MI_Plane_01_Body_A");
        Assert.Contains(plane.ParentChain, p => p.Contains("/M_Car_01.", StringComparison.Ordinal));
        Assert.Equal(PlaneMask, plane.Textures.Single(t => t.Name == "Color Mask").TexturePath);
        Assert.NotEqual(plane.Vectors.Single(v => v.Name == "Base Color A").Value, plane.Vectors.Single(v => v.Name == "Base Color B").Value); // navy and tan

        // The WolfsWagen's body mask is laid out in UV1 ("Color Mask UV Channel" 1): a bake in UV0 cannot write it.
        var body = inspector.Inspect("/Game/ConZ_Files/Models/Vehicles2/WolfsWagen/Materials/MI_WW_BodyOutside_A.MI_WW_BodyOutside_A");
        Assert.Equal(WolfsWagenMask, body.Textures.Single(t => t.Name == "Color Mask").TexturePath);
        Assert.Equal(1f, body.Scalars.Single(s => s.Name == "Color Mask UV Channel").Value);
        Assert.Equal(0f, inspector.Inspect("/Game/ConZ_Files/Materials/Car/M_Car_01.M_Car_01").Scalars.Single(s => s.Name == "Color Mask UV Channel").Value);

        var planeStats = Stats(catalog, PlaneMask);
        var wwStats = Stats(catalog, WolfsWagenMask);
        foreach (var s in new[] { planeStats, wwStats })
        {
            Assert.Equal(0, s.Red);       // red is never used ...
            Assert.Equal(0, s.AlphaLow);  // ... nor alpha (DXT1: 255 everywhere)
            Assert.InRange(s.Painted, 0.4, 0.8);
        }

        Assert.InRange(planeStats.Blue, 0.3, 0.5);      // the Kinglet's tan (colour B) ...
        Assert.InRange(planeStats.BlueOutside, 0.01, 0.1); // ... partly outside the green: coverage is max(green, blue)
        Assert.Equal(0, wwStats.Blue);                   // the WolfsWagen: one colour

        Assert.Equal(1, Stats(catalog, Basic + "T_FlatWhiteMask_Dummy_01.T_FlatWhiteMask_Dummy_01").Painted);
        Assert.Equal(0, Stats(catalog, Basic + "T_FlatBlackMask_Dummy_01.T_FlatBlackMask_Dummy_01").Painted);
        Assert.Equal(0, Stats(catalog, Basic + "T_FlatBlackColor_Dummy_01.T_FlatBlackColor_Dummy_01").Painted);
    }

    /// <summary>Fractions of texels: red set, alpha below half, painted (coverage over half), blue over half, blue over green.</summary>
    private (double Red, double AlphaLow, double Painted, double Blue, double BlueOutside) Stats(AssetCatalog catalog, string path)
    {
        Assert.True(catalog.TryLoadObject<UTexture2D>(path, out var texture), path);
        var image = TextureDecoder.Decode(texture, 1024);
        var px = image.Rgba;
        var n = (double)(px.Length / 4);
        var (red, alpha, painted, blue, outside) = (0, 0, 0, 0, 0);
        for (var i = 0; i < px.Length; i += 4)
        {
            red += px[i] > 8 ? 1 : 0;
            alpha += px[i + 3] < 128 ? 1 : 0;
            painted += PaintRule.Coverage(px, i) > 0.5f ? 1 : 0;
            blue += PaintRule.SelectB(px, i) > 0.5f ? 1 : 0;
            outside += px[i + 2] > px[i + 1] + 16 ? 1 : 0;
        }

        var stats = (red / n, alpha / n, painted / n, blue / n, outside / n);
        output.WriteLine($"{path[(path.LastIndexOf('.') + 1)..]} {image.Width}x{image.Height} {image.PixelFormat}: {stats}");
        return stats;
    }
}
