using System.Numerics;
using ScumStudio.Assets.Materials;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// The 3D previews draw a material with the Map's rules (<c>LevelScenePreparer</c>): glass and water see-through, the
/// masters' colour scale on a texture, leaves cut out without a roughness highlight.
/// </summary>
public sealed class MaterialLooksTests
{
    private static MaterialInfo Material(string name, Vector4? tint = null) =>
        new(name, "/Game/M/" + name + "." + name, [], [], [], [], null, tint, []);

    [Fact]
    public void TexturedGlassIsSeeThrough()
    {
        var (tint, _, _) = MaterialLooks.Of(Material("MI_Glass") with { IsTranslucent = true }, "/Game/M/MI_Glass.MI_Glass", textured: true);
        Assert.NotNull(tint); // before: null, the texture drawn opaque
        Assert.True(tint!.Value.W < 1f, $"alpha {tint.Value.W}");
    }

    [Fact]
    public void UntexturedTranslucentIsDrawnAtFortyPercent()
    {
        var glass = Material("MI_Glass", new Vector4(0.2f, 0.3f, 0.4f, 1f)) with { IsTranslucent = true };
        Assert.Equal(new Vector4(0.2f, 0.3f, 0.4f, 0.4f), MaterialLooks.Of(glass, "/Game/M/MI_Glass.MI_Glass", textured: false).Tint);
    }

    [Fact]
    public void TexturedOpaqueTakesTheMastersColourScale()
    {
        var (tint, clip, _) = MaterialLooks.Of(Material("MI_Wall") with { BaseColorScale = new Vector3(0.5f) }, "/Game/M/MI_Wall.MI_Wall", textured: true);
        Assert.Equal(new Vector4(0.5f, 0.5f, 0.5f, 1f), tint);
        Assert.Equal(0f, clip);
    }

    [Fact]
    public void MaskedLeavesAreCutOutWithoutRoughness()
    {
        var leaves = Material("MI_Leaves") with { OpacityMaskClip = 0.33f, RoughnessRange = new Vector2(0.2f, 0.8f) };
        var (_, clip, roughness) = MaterialLooks.Of(leaves, "/Game/M/MI_Leaves.MI_Leaves", textured: true);
        Assert.True(clip > 0f);
        Assert.Equal(Vector2.Zero, roughness);
    }

    [Fact]
    public void RiversAreTheMapsWaterColour()
    {
        var river = Material("MI_River_01") with { IsTranslucent = true };
        Assert.Equal(new Vector4(0.035f, 0.12f, 0.17f, 0.75f), MaterialLooks.Of(river, "/Game/M/MI_River_01.MI_River_01", textured: false).Tint);
    }
}
