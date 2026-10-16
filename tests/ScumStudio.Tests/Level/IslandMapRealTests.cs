using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Texture;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.World;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// <see cref="IslandMap"/>'s calibration against the game itself. Real game files only (<c>SCUM_PAKS</c>, key from this
/// PC's store): every landscape tile's World Composition bounds must sit in the cell its name gives, the tiles must span
/// the calibrated square, the outposts must sit in their cells, and the game's map texture must decode as a square and,
/// as the app decodes it, show sea and land where the terrain has them.
/// </summary>
public sealed class IslandMapRealTests
{
    // Open sea and inland ground (world cm) from the game's terrain heights: each about 180 m or more from a coast and clear
    // of the cell lines and names. Each of the seven other ways to lay the picture (mirrored, turned, swapped axes) puts at
    // least six of the twelve on the other side of a coast.
    private static readonly Vector2[] OpenSea =
        [new(460000, -640000), new(-860000, -500000), new(-160000, -520000), new(600000, -320000), new(-640000, -640000), new(40000, -640000)];

    private static readonly Vector2[] Inland =
        [new(340000, -520000), new(-480000, 180000), new(60000, -200000), new(-800000, 480000), new(-880000, -160000), new(-800000, -120000)];

    private static AssetCatalog? Open() =>
        Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;

    [Fact]
    public void EveryLandscapeTileCentreLiesInItsNamedCell()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return; // not asked for
        }

        var tiles = Tiles(catalog, WorldPackageKind.Landscape);
        Assert.Equal(400, tiles.Count);
        var wrong = tiles.Where(t => IslandMap.CellAt(t.Tile!.Center.X, t.Tile.Center.Y) != t.Cell).Select(t => $"{t.Name} at {t.Tile!.Center}").ToList();
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void TileUnionMatchesTheCalibrationWithinOnePercent()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        // Edge tiles reach out to the buoys at the world's edge; the tiles' centres +- half a typical tile do not.
        var tiles = Tiles(catalog, WorldPackageKind.Landscape);
        var half = tiles.Select(t => MathF.Min(t.Tile!.Size.X, t.Tile.Size.Y)).Order().ElementAt(tiles.Count / 2) / 2f;
        var tolerance = IslandMap.SizeCm / 100f;
        Assert.Equal(IslandMap.MinCm, tiles.Min(t => t.Tile!.Center.X) - half, tolerance);
        Assert.Equal(IslandMap.MinCm, tiles.Min(t => t.Tile!.Center.Y) - half, tolerance);
        Assert.Equal(IslandMap.MinCm + IslandMap.SizeCm, tiles.Max(t => t.Tile!.Center.X) + half, tolerance);
        Assert.Equal(IslandMap.MinCm + IslandMap.SizeCm, tiles.Max(t => t.Tile!.Center.Y) + half, tolerance);
    }

    [Fact]
    public void IslandMapTextureDecodesSquare()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        var image = TextureDecoder.Decode(catalog.LoadObject<UTexture2D>(IslandMap.TexturePath), maxSize: 4096);
        Assert.Equal(image.Width, image.Height);
        Assert.True(image.Width >= 2048, $"{image.Width}x{image.Height}");
    }

    [Fact]
    public void TheGamePictureIsTheRightWayUp()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        // As the app decodes it, every open-sea point must be bluer than every inland one. On the reference export of the
        // texture the gap is +31 this way up and -31 to -81 in the seven other orientations.
        var image = IslandMapPicture.FromCatalog(catalog)!.Decode()!;
        var sea = OpenSea.Select(p => Blue(image, p)).ToList();
        var land = Inland.Select(p => Blue(image, p)).ToList();
        Assert.True(sea.Min() > land.Max(), $"blue over green: sea {string.Join(", ", sea.Select(b => b.ToString("F1")))}; land {string.Join(", ", land.Select(b => b.ToString("F1")))}");
    }

    [Fact]
    public void KnownLandmarksFallInTheirCells()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        var index = WorldIndex.FromCatalog(catalog).WithTileInfo(catalog);
        foreach (var name in new[] { "A_0_Outpost", "B_4_Outpost" })
        {
            var outpost = index.Find(name)!;
            Assert.True(outpost.Tile is { BoundsValid: true }, name);
            Assert.Equal(outpost.Cell, IslandMap.CellAt(outpost.Tile!.Center.X, outpost.Tile.Center.Y));
        }

        // Every place sublevel (outposts, airfields, towns: <cell>_<name>) lies in its named cell, give or take the few
        // that reach over a cell border.
        var places = Tiles(catalog, WorldPackageKind.Poi);
        var inside = places.Count(t => IslandMap.CellAt(t.Tile!.Center.X, t.Tile.Center.Y) == t.Cell);
        Assert.True(inside >= places.Count * 0.95, $"{inside} of {places.Count} place sublevels lie in their cell");
        Assert.Contains(places, t => t.Name.Contains("Airfield", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Airport", StringComparison.OrdinalIgnoreCase));
    }

    // Blue minus green (0-255) of the mean of the 9 x 9 pixels at the world point.
    private static double Blue(TextureImage image, Vector2 at)
    {
        var (u, v) = IslandMap.ToMap(at.X, at.Y);
        var (cx, cy) = ((int)(u * image.Width), (int)(v * image.Height));
        var (sum, count) = (0d, 0);
        for (var y = Math.Max(cy - 4, 0); y <= Math.Min(cy + 4, image.Height - 1); y++)
        {
            for (var x = Math.Max(cx - 4, 0); x <= Math.Min(cx + 4, image.Width - 1); x++)
            {
                var o = ((y * image.Width) + x) * 4;
                sum += image.Rgba[o + 2] - image.Rgba[o + 1];
                count++;
            }
        }

        return sum / count;
    }

    private static List<WorldPackage> Tiles(AssetCatalog catalog, WorldPackageKind kind) =>
        WorldIndex.FromCatalog(catalog).WithTileInfo(catalog).Packages
            .Where(p => p.IsMap && p.Kind == kind && p.Cell is not null && p.Tile is { BoundsValid: true } tile && tile.Size.X > 0)
            .ToList();
}
