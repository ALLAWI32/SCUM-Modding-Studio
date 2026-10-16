using System.Numerics;
using ScumStudio.Viewport.Decals;

namespace ScumStudio.Tests.Viewport;

/// <summary>The decals' path filler (<see cref="PathRaster"/>): exact areas, anti-aliased edges, holes and regions.</summary>
public sealed class PathRasterTests
{
    private const string Circle40 = "M 90 50 C 90 72.09 72.09 90 50 90 C 27.91 90 10 72.09 10 50 C 10 27.91 27.91 10 50 10 C 72.09 10 90 27.91 90 50 Z";

    [Fact]
    public void ASquareOnWholePixelsCoversExactlyItsArea()
    {
        var plane = PathRaster.Fill("M 2 2 H 12 V 12 H 2 Z", 16, 16, Matrix3x2.Identity);
        Assert.Equal(100f, plane.Sum(), 3);
        Assert.All(plane, v => Assert.True(v is 0f or 1f));
    }

    [Fact]
    public void ACircleCoversItsAreaWithinOnePercent()
    {
        var area = PathRaster.Fill(Circle40, 100, 100, Matrix3x2.Identity).Sum();
        Assert.InRange(area, MathF.PI * 1600 * 0.99f, MathF.PI * 1600 * 1.01f);
    }

    [Fact]
    public void EvenOddCutsAHoleAndNonZeroDoesNot()
    {
        const string Nested = "M 0 0 H 20 V 20 H 0 Z M 5 5 H 15 V 15 H 5 Z";
        Assert.Equal(300f, PathRaster.Fill(Nested, 20, 20, Matrix3x2.Identity, FillRule.EvenOdd).Sum(), 3);
        Assert.Equal(400f, PathRaster.Fill(Nested, 20, 20, Matrix3x2.Identity, FillRule.NonZero).Sum(), 3);
        // Nonzero: the inner outline wound the other way round is a hole.
        Assert.Equal(300f, PathRaster.Fill("M 0 0 H 20 V 20 H 0 Z M 5 5 V 15 H 15 V 5 Z", 20, 20, Matrix3x2.Identity).Sum(), 3);
    }

    [Fact]
    public void EdgesBetweenPixelsAreAntiAliased()
    {
        var plane = PathRaster.Fill("M 0.5 0.5 H 10.5 V 10.5 H 0.5 Z", 12, 12, Matrix3x2.Identity);
        Assert.Equal(100f, plane.Sum(), 3);
        Assert.Equal(0.25f, plane[0], 3);        // corner: a quarter of the pixel
        Assert.Equal(0.5f, plane[5], 3);         // top edge: half
        Assert.Equal(1f, plane[(5 * 12) + 5], 3); // inside
        Assert.Contains(plane, v => v is > 0f and < 1f);
    }

    [Fact]
    public void RelativeCommandsCurvesAndTransformsWork()
    {
        Assert.InRange(PathRaster.Fill("m 0 0 l 10 0 l 0 10 z", 10, 10, Matrix3x2.Identity).Sum(), 49f, 51.5f); // 4 x 4 samples on a diagonal
        Assert.Equal(200f, PathRaster.Fill("M 0 0 h 10 v 5 h -10 z", 40, 40, Matrix3x2.CreateScale(2f)).Sum(), 3);
        var arch = PathRaster.Fill("M 0 20 Q 10 0 20 20 Z", 20, 20, Matrix3x2.Identity).Sum();
        Assert.InRange(arch, 20 * 20 * 2 / 3f * 0.5f * 0.98f, 20 * 20 * 2 / 3f * 0.5f * 1.02f); // a parabola's segment: 2/3 of its box
        Assert.Throws<FormatException>(() => PathRaster.Fill("M 0 0 A 1 1 0 0 0 5 5 Z", 10, 10, Matrix3x2.Identity));
    }

    [Fact]
    public void RegionsDoNotOverlap()
    {
        var design = new DecalDesign("t", "Shapes", 20, 20,
            [new DecalRegion("M 0 0 H 20 V 20 H 0 Z", 0xFF0000), new DecalRegion("M 5.3 5.3 H 14.7 V 14.7 H 5.3 Z", 0x00FF00)]);
        var raster = design.Rasterize(20);
        for (var i = 0; i < 400; i++)
        {
            Assert.InRange(raster.Planes[0][i] + raster.Planes[1][i], 0f, 1.0001f);
        }

        Assert.Equal(400f - raster.Planes[1].Sum(), raster.Planes[0].Sum(), 2);
        Assert.InRange(raster.Planes[1].Sum(), 9.4f * 9.4f * 0.97f, 9.4f * 9.4f * 1.03f);
    }
}
