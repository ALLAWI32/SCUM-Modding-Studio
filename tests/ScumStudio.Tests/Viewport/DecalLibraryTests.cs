using ScumStudio.Viewport.Decals;

namespace ScumStudio.Tests.Viewport;

/// <summary>The decal library shipped with the studio (<see cref="DecalLibrary"/>): many designs, every one drawable and stable.</summary>
public sealed class DecalLibraryTests
{
    [Fact]
    public void TheLibraryHasManyDesignsInEveryCategory()
    {
        var all = DecalLibrary.All;
        Assert.True(all.Count >= 48, $"{all.Count} designs");
        Assert.Equal(all.Count, all.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.True(all.Select(d => d.Category).Distinct().Count() >= 9);
        Assert.All(DecalLibrary.Categories, c => Assert.Contains(all, d => d.Category == c));
        Assert.All(all, d => Assert.InRange(d.Regions.Count, 1, 4));
        Assert.Equal(20, all.Count(d => d.Category == "Numbers"));
        Assert.Same(all[0], DecalLibrary.Find(all[0].Id));
    }

    [Fact]
    public void EveryDesignAndEveryRegionDrawsAt256Pixels()
    {
        foreach (var design in DecalLibrary.All)
        {
            var raster = design.Rasterize(256);
            Assert.Equal(256, raster.Width);
            for (var k = 0; k < raster.Planes.Count; k++)
            {
                Assert.True(raster.Planes[k].Sum() > 20, $"{design.Id} region {k} draws nothing");
            }

            var total = raster.Planes[0].Select((_, i) => raster.Planes.Sum(p => p[i])).ToArray();
            Assert.True(total.Max() <= 1.0001f, $"{design.Id}: regions overlap");
            Assert.True(total.Sum() < raster.Width * raster.Height * 0.999f || design.Category is "Army" or "Stripes", $"{design.Id} fills its whole box");
        }
    }

    [Fact]
    public void TheDesignsAreTheSameEveryTime()
    {
        var (a, b) = (DecalLibrary.Build(), DecalLibrary.Build());
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Regions.Select(r => r.Path), b[i].Regions.Select(r => r.Path));
            Assert.Equal(a[i].Rasterize(64).Planes.SelectMany(p => p), b[i].Rasterize(64).Planes.SelectMany(p => p));
        }
    }
}
