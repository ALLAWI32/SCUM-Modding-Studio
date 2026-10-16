using System.Numerics;
using ScumStudio.Assets.Textures;
using ScumStudio.Viewport;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.Tests.Viewport;

/// <summary>The car paint rule (<see cref="PaintRule"/>): green paints colour A, blue colour B, red and alpha nothing.</summary>
public sealed class PaintRuleTests
{
    [Theory]
    [InlineData(0, 255, 0, 255, 1f, 0f)]     // green: colour A
    [InlineData(0, 255, 255, 255, 1f, 1f)]   // green and blue: colour B
    [InlineData(0, 0, 255, 255, 1f, 1f)]     // blue outside the green (3 % of the Kinglet's mask): still painted B
    [InlineData(255, 0, 0, 0, 0f, 0f)]       // red (no stock mask uses it) and alpha paint nothing
    [InlineData(0, 0, 0, 255, 0f, 0f)]       // flat black: the diffuse as authored
    [InlineData(255, 255, 255, 255, 1f, 1f)] // flat white: paint everywhere
    [InlineData(0, 128, 0, 255, 0.502f, 0f)]
    public void ATexelGivesItsCoverageAndColour(byte r, byte g, byte b, byte a, float coverage, float selectB)
    {
        byte[] texel = [r, g, b, a];
        Assert.Equal(coverage, PaintRule.Coverage(texel, 0), 3);
        Assert.Equal(selectB, PaintRule.SelectB(texel, 0), 3);
    }

    [Fact]
    public void TheBakerPaintsByTheRule()
    {
        // 2x2: green, green+blue, red, black over a mid-grey diffuse; A red, B blue.
        var diffuse = new TextureImage("D", 2, 2, [.. Enumerable.Repeat((byte)128, 16)], "PF_B8G8R8A8", 0, 2, 2, true, false);
        var mask = new TextureImage("M", 2, 2, [0, 255, 0, 255, 0, 255, 255, 255, 255, 0, 0, 255, 0, 0, 0, 255], "PF_DXT1", 0, 2, 2, false, false);
        var baked = PaintBaker.Bake(diffuse, mask, new Vector4(1, 0, 0, 1), new Vector4(0, 0, 1, 1)).Rgba;

        Near([255, 0, 0, 255], baked[0..4]);     // colour A
        Near([0, 0, 255, 255], baked[4..8]);     // colour B
        Near([128, 128, 128, 0], baked[8..12]);  // red is no paint: the diffuse
        Near([128, 128, 128, 0], baked[12..16]); // black: the diffuse
    }

    private static void Near(byte[] expected, byte[] actual) =>
        Assert.True(expected.Zip(actual).All(p => Math.Abs(p.First - p.Second) <= 1), $"{string.Join(',', actual)} is not {string.Join(',', expected)}");

    [Fact]
    public void ADecalUncoversThePaintAndTheDiffuseMakesUpForIt()
    {
        byte[] mask = [0, 255, 0, 255];
        var (diffuse, paint, decal) = (new Vector3(0.2f), new Vector3(0.9f, 0.7f, 0.1f), new Vector3(0.05f, 0.4f, 0.8f));
        foreach (var alpha in new[] { 0f, 0.25f, 0.5f, 1f })
        {
            var under = PaintRule.DiffuseUnder(diffuse, 1f, decal, alpha);
            var m = (byte[])mask.Clone();
            PaintRule.Uncover(m, 0, alpha);
            var shown = PaintRule.Shown(under, m, 0, paint, paint);
            var expected = Vector3.Lerp(paint, decal, alpha);
            Assert.True(Vector3.Distance(shown, expected) < 2.5f / 255f, $"alpha {alpha}: {shown} vs {expected}");
        }

        // Half-painted texel, half-opaque decal: the same, whatever the paint colour.
        foreach (var p in new[] { Vector3.Zero, Vector3.One, paint })
        {
            byte[] half = [0, 128, 0, 255];
            var c = PaintRule.Coverage(half, 0);
            var under = PaintRule.DiffuseUnder(diffuse, c, decal, 0.5f);
            PaintRule.Uncover(half, 0, 0.5f);
            var expected = Vector3.Lerp(Vector3.Lerp(diffuse, p, c), decal, 0.5f);
            Assert.True(Vector3.Distance(PaintRule.Shown(under, half, 0, p, p), expected) < 2.5f / 255f);
        }
    }
}
