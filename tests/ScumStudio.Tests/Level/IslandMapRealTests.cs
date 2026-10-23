using System.Numerics;
using CUE4Parse.UE4.Assets.Exports.Texture;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.World;
using ScumStudio.Pak;

namespace ScumStudio.Tests.Level;

/// <summary>
/// <see cref="IslandMap"/>'s calibration against the game itself. Real game files only (<c>SCUM_PAKS</c>, key from this
/// PC's store): every landscape tile's World Composition bounds must sit in the cell its name gives, the tiles must span
/// the calibrated square, the outposts must sit in their cells, the game's map texture must decode as a square and, as the
/// app shows it, show sea and land where the terrain has them, and the studio's map must be turned as the game's spawn
/// screen turns it, its cells under the game's own sector labels.
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

        // As the Spawns map shows it (the PNG the app makes, laid out by IslandMap), every open-sea point must be bluer than
        // every inland one. On the reference export of the texture the gap is +31 this way up and -31 to -81 in the seven
        // other orientations.
        var image = AsTheAppShowsIt(catalog);
        var sea = OpenSea.Select(p => Blue(image, p)).ToList();
        var land = Inland.Select(p => Blue(image, p)).ToList();
        Assert.True(sea.Min() > land.Max(), $"blue over green: sea {string.Join(", ", sea.Select(b => b.ToString("F1")))}; land {string.Join(", ", land.Select(b => b.ToString("F1")))}");
    }

    [Fact]
    public void TheStudioMapIsTurnedLikeTheGamesSpawnScreen()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        // Discord 2026-10-17, "the map is inverted": the game's spawn screen (the sector picker every player sees) draws the
        // island map texture turned 180 degrees under its sector buttons, D4 at the top left down to Z0 at the bottom right.
        var screen = GameMapScreen.Read(catalog);
        Assert.Equal("IslandMap", screen.MapTexture);
        Assert.Equal(180f, screen.MapAngle);
        Assert.Equal(25, screen.Sectors.Count);
        Assert.Equal("D4", screen.Sectors[(0, 0)]);
        Assert.Equal("D0", screen.Sectors[(0, 4)]);
        Assert.Equal("Z4", screen.Sectors[(4, 0)]);
        Assert.Equal("Z0", screen.Sectors[(4, 4)]);

        // The studio's picture is that texture turned the same way: its pixel (x, y) is the stored texture's (w-1-x, h-1-y).
        var stored = IslandMapPicture.FromCatalog(catalog)!.Decode()!;
        var shown = AsTheAppShowsIt(catalog);
        var (w, h) = (stored.Width, stored.Height);
        Assert.Equal((w, h), (shown.Width, shown.Height));
        for (var y = 0; y < h; y += 61)
        {
            for (var x = 0; x < w; x += 59)
            {
                var (at, from) = (((y * w) + x) * 4, ((((h - 1 - y) * w) + (w - 1 - x)) * 4));
                Assert.True(shown.Rgba.AsSpan(at, 4).SequenceEqual(stored.Rgba.AsSpan(from, 4)), $"pixel ({x}, {y})");
            }
        }

        // Every cell's centre, and every place sublevel lying in its named cell (outposts, airfields, towns), is drawn
        // under the game's button of that name.
        foreach (var cell in MapCell.All)
        {
            var (minX, minY, maxX, maxY) = IslandMap.CellRect(cell);
            Assert.Equal(screen.ButtonOf(cell), ButtonAt(IslandMap.ToMap((minX + maxX) / 2f, (minY + maxY) / 2f)));
        }

        var places = Tiles(catalog, WorldPackageKind.Poi).Where(t => !IsEmptyBox(t.Tile!) && IslandMap.CellAt(t.Tile!.Center.X, t.Tile.Center.Y) == t.Cell).ToList();
        Assert.True(places.Count > 1000, $"{places.Count} place sublevels");
        var wrong = places.Where(t => screen.ButtonOf(t.Cell!.Value) != ButtonAt(IslandMap.ToMap(t.Tile!.Center.X, t.Tile.Center.Y)))
            .Select(t => $"{t.Name} at {t.Tile!.Center}: drawn under {ButtonAt(IslandMap.ToMap(t.Tile.Center.X, t.Tile.Center.Y))}, the game labels {t.Cell} at {screen.ButtonOf(t.Cell!.Value)}").ToList();
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong.Take(20)));
    }

    // The spawn screen's button (row, column) over the picture position.
    private static (int Row, int Column)? ButtonAt((double U, double V) at) =>
        (Math.Clamp((int)(at.V * 5), 0, 4), Math.Clamp((int)(at.U * 5), 0, 4));

    // The game's map picture exactly as the Spawns map shows it: the PNG IslandMapPicture makes in its cache.
    private static TextureImage AsTheAppShowsIt(AssetCatalog catalog)
    {
        var folder = Directory.CreateTempSubdirectory("scumstudio-islandmap-real-").FullName;
        try
        {
            var result = new IslandMapPicture(folder, NullLogger.Instance, terrainDirectory: null).GetAsync(catalog).GetAwaiter().GetResult();
            Assert.Equal(IslandMapSource.Game, result.Source);
            return ImageFiles.Load(result.PngPath!);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
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

        // Every place sublevel (outposts, airfields, towns: <cell>_<name>) lies in its named cell or less than 1 km past its
        // border; a calibration off by a cell, mirrored or turned puts them 3 km and more away. Read on the game files of
        // 2026-10-17: 1,336 of 1,457 centres lie inside. The town of Samobor (D_4) reaches up to 560 m into D_3, the Z_1
        // trainyard 301 m into Z_2, C_1_Veliki_Tabor_03 is a 4 x 3 km box centred 866 m out, and the four
        // *_Outpost_BossHouse_02 sublevels hold nothing: their bounds are the engine's empty 10 m box at the world origin.
        var places = Tiles(catalog, WorldPackageKind.Poi).Where(t => !IsEmptyBox(t.Tile!)).ToList();
        Assert.True(places.Count > 1000, $"{places.Count} place sublevels");
        var far = places.Where(t => OutsideCm(t.Cell!.Value, t.Tile!.Center.X, t.Tile.Center.Y) >= 100_000f)
            .Select(t => $"{t.Name} at {t.Tile!.Center} ({OutsideCm(t.Cell!.Value, t.Tile.Center.X, t.Tile.Center.Y) / 100f:0} m outside {t.Cell})").ToList();
        Assert.True(far.Count == 0, string.Join(Environment.NewLine, far));
        var inside = places.Count(t => IslandMap.CellAt(t.Tile!.Center.X, t.Tile.Center.Y) == t.Cell);
        Assert.True(inside >= places.Count * 0.85, $"{inside} of {places.Count} place sublevels lie in their cell");
        Assert.Contains(places, t => t.Name.Contains("Airfield", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Airport", StringComparison.OrdinalIgnoreCase));
    }

    // The bounds a sublevel with nothing in it is saved with: a 10 m box around the world origin.
    private static bool IsEmptyBox(ScumStudio.Formats.Packages.WorldTileInfo tile) =>
        tile.Center.X == 0f && tile.Center.Y == 0f && tile.Size.X <= 1000f && tile.Size.Y <= 1000f;

    // How far (cm) the world point lies outside the cell's rectangle; 0 inside.
    private static float OutsideCm(MapCell cell, float x, float y)
    {
        var (minX, minY, maxX, maxY) = IslandMap.CellRect(cell);
        var dx = MathF.Max(0f, MathF.Max(minX - x, x - maxX));
        var dy = MathF.Max(0f, MathF.Max(minY - y, y - maxY));
        return MathF.Sqrt((dx * dx) + (dy * dy));
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
