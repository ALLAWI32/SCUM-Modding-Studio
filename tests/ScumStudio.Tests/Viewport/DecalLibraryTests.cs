using ScumStudio.Viewport.Decals;

namespace ScumStudio.Tests.Viewport;

/// <summary>The decal library shipped with the studio (<see cref="DecalLibrary"/>): many designs, every one drawable and stable.</summary>
public sealed class DecalLibraryTests
{
    [Fact]
    public void TheLibraryHasManyDesignsInEveryCategory()
    {
        var all = DecalLibrary.All;
        Assert.True(all.Count >= 90, $"{all.Count} designs");
        Assert.Equal(all.Count, all.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.True(all.Select(d => d.Category).Distinct().Count() >= 11);
        Assert.All(DecalLibrary.Categories, c => Assert.Contains(all, d => d.Category == c));
        Assert.All(all, d => Assert.InRange(d.Regions.Count, 1, 4));
        Assert.Equal(30, all.Count(d => d.Category == "Numbers"));
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
            Assert.True(total.Sum() < raster.Width * raster.Height * 0.999f || design.Category is "Army" or "Stripes" or "Camo", $"{design.Id} fills its whole box");
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

    [Fact]
    public void NumbersAreSmoothCurvesNotClockSegments()
    {
        // Owner (2026-10-17): "digits like a clock". A digit is now a broad-pen stroke along curves: its outline has many
        // points, and the round parts of an 8 have soft (anti-aliased) edges all the way round, not the 7 straight bars.
        foreach (var d in Enumerable.Range(0, 10))
        {
            var bold = DecalLibrary.Find($"number-bold-{d}")!;
            var points = bold.Regions[1].Path.Split('L').Length;
            Assert.True(points > 60, $"{d}: {points} points");
        }

        var eight = DecalLibrary.Find("number-bold-8")!.Rasterize(512);
        var plane = eight.Planes[1];
        var soft = plane.Count(v => v is > 0.05f and < 0.95f);
        var solid = plane.Count(v => v >= 0.95f);
        Assert.True(soft > 200 && solid > 2000, $"{soft} soft and {solid} solid pixels");

        // The 0 is one ring: its middle is empty, its sides are inked.
        var zero = DecalLibrary.Find("number-bold-0")!.Rasterize(256);
        var (w, h) = (zero.Width, zero.Height);
        Assert.Equal(0f, zero.Planes[1][((h / 2) * w) + (w / 2)], 2);
        Assert.Contains(Enumerable.Range(0, w / 2), x => zero.Planes[1][((h / 2) * w) + x] > 0.95f);
    }

    [Fact]
    public void SavedDesignIdsOfTheFirstLibraryStillResolve()
    {
        string[] ids =
        [
            "tribal-1", "tribal-6", "flames-1", "flames-6", "stripes-racing", "stripes-pinstripe", "stripes-hazard", "stripes-checker", "stripes-swoosh",
            "stripes-speed", "number-racing-0", "number-racing-9", "number-stencil-0", "number-stencil-9", "army-star", "army-star-ring", "army-chevrons",
            "army-roundel", "army-camo-woodland", "army-camo-desert", "army-camo-digital", "anime-speed-lines", "anime-sun-rays", "anime-lightning",
            "anime-waves", "anime-sakura", "anime-burst", "skull-plain", "skull-crossbones", "skull-diamond", "skull-horns", "shape-heart", "shape-wings",
            "shape-ring", "shape-star", "shape-diamond", "shape-arrow", "logo-shield", "logo-badge", "logo-banner", "logo-hexagon",
        ];
        Assert.All(ids, id => Assert.NotNull(DecalLibrary.Find(id)));
    }

    [Fact]
    public async Task ContactSheetForALook()
    {
        // With SCUMSTUDIO_SCREENSHOTS set: every design in its own colours on a mid grey, 8 a row, 160 px cells (outside the repository).
        if (Environment.GetEnvironmentVariable("SCUMSTUDIO_SCREENSHOTS") is not { Length: > 0 } folder || folder is "0")
        {
            return;
        }

        const int Cell = 160;
        const int PerRow = 8;
        var all = DecalLibrary.All;
        var rows = (all.Count + PerRow - 1) / PerRow;
        var (w, h) = (Cell * PerRow, Cell * rows);
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = (92, 96, 100, 255);
        }

        static byte Srgb(float linear) => (byte)Math.Clamp(MathF.Round((linear <= 0.0031308f ? linear * 12.92f : (1.055f * MathF.Pow(linear, 1f / 2.4f)) - 0.055f) * 255f), 0, 255);
        for (var k = 0; k < all.Count; k++)
        {
            var design = all[k];
            var size = (int)(design.Aspect >= 1 ? Cell - 12 : (Cell - 12) * design.Aspect);
            var image = new ScumStudio.Viewport.Decals.DecalLayer().Render(design.Rasterize(Math.Max(8, size)), design);
            var (ox, oy) = (((k % PerRow) * Cell) + ((Cell - image.Width) / 2), ((k / PerRow) * Cell) + ((Cell - image.Height) / 2));
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var s = ((y * image.Width) + x) * 4;
                    var a = image.Rgba[s + 3];
                    if (a <= 0f)
                    {
                        continue;
                    }

                    var d = (((oy + y) * w) + ox + x) * 4;
                    for (var c = 0; c < 3; c++)
                    {
                        var under = rgba[d + c] / 255f;
                        var under01 = under <= 0.04045f ? under / 12.92f : MathF.Pow((under + 0.055f) / 1.055f, 2.4f);
                        rgba[d + c] = Srgb(image.Rgba[s + c] + (under01 * (1f - a)));
                    }
                }
            }
        }

        Directory.CreateDirectory(folder);
        await ScumStudio.Rendering.Imaging.ImageExport.SavePngAsync(rgba, w, h, Path.Combine(folder, "decal-library.png"));
    }
}
