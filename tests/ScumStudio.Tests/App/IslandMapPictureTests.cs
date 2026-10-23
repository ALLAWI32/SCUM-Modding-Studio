using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Services;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.World;

namespace ScumStudio.Tests.App;

/// <summary>
/// The picture under the Spawns map: the user's imported picture first, then the game's map texture (decoded once from
/// the paks, then read from the cache), then the terrain drawn from above, else none. Nothing is shipped or downloaded.
/// </summary>
public sealed class IslandMapPictureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("scumstudio-islandmap-").FullName;
    private int _decodes;

    [Fact]
    public async Task ImportedWinsOverGame()
    {
        var source = Path.Combine(_root, "my map.png");
        await PngWriter.SaveAsync(Image(32), source);
        var pictures = Make();

        Assert.True(await pictures.ImportAsync(source));
        var result = await pictures.GetAsync(Game(), CancellationToken.None);

        Assert.Equal(IslandMapSource.Imported, result.Source);
        Assert.Equal(pictures.ImportedPath, result.PngPath);
        Assert.Equal(0, _decodes);
        pictures.ClearImported();
        Assert.Equal(IslandMapSource.Game, (await pictures.GetAsync(Game(), CancellationToken.None)).Source);
    }

    [Fact]
    public async Task GameIsDecodedOnceThenCached()
    {
        var first = await Make().GetAsync(Game(), CancellationToken.None);
        var second = await Make().GetAsync(Game(), CancellationToken.None);

        Assert.Equal(IslandMapSource.Game, first.Source);
        Assert.Equal(first, second);
        Assert.Equal(1, _decodes);
        Assert.True(IslandMapPicture.IsCompletePng(first.PngPath!));
        Assert.StartsWith("islandmap-turned-", Path.GetFileName(first.PngPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGamePictureIsTurnedAsTheGamesSpawnScreenShowsIt()
    {
        // The game's spawn screen draws the map texture turned 180 degrees: the cached picture's pixel (x, y) is the
        // texture's (w-1-x, h-1-y). A texture 48 x 32 with red growing to the right and green down.
        var (w, h) = (48, 32);
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            (rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2], rgba[(i * 4) + 3]) = ((byte)(i % w * 5), (byte)(i / w * 7), 160, 255);
        }

        var expected = (byte[])rgba.Clone();
        var texture = new TextureImage("IslandMap", w, h, rgba, "PF_B8G8R8A8", 0, w, h, true, false);
        var result = await Make().GetAsync(new IslandMapPicture.GamePicture("pak|IslandMap|1", () => texture), CancellationToken.None);

        Assert.Equal(IslandMapSource.Game, result.Source);
        var shown = ImageFiles.Load(result.PngPath!);
        Assert.Equal((w, h), (shown.Width, shown.Height));
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (at, from) = (((y * w) + x) * 4, ((((h - 1 - y) * w) + (w - 1 - x)) * 4));
                Assert.True(shown.Rgba.AsSpan(at, 4).SequenceEqual(expected.AsSpan(from, 4)), $"pixel ({x}, {y})");
            }
        }
    }

    [Fact]
    public void TurnHalfwayKeepsEachPixelWhole()
    {
        byte[] rgba = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        byte[] turned = [9, 10, 11, 12, 5, 6, 7, 8, 1, 2, 3, 4];
        Assert.Equal(turned, IslandMapPicture.TurnHalfway(rgba));
    }

    [Fact]
    public async Task CorruptCachedPngIsRebuilt()
    {
        var first = await Make().GetAsync(Game(), CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(first.PngPath!);
        await File.WriteAllBytesAsync(first.PngPath!, bytes[..(bytes.Length / 2)]); // a write cut short

        var again = await Make().GetAsync(Game(), CancellationToken.None);

        Assert.Equal(IslandMapSource.Game, again.Source);
        Assert.Equal(2, _decodes);
        Assert.True(IslandMapPicture.IsCompletePng(again.PngPath!));
    }

    [Fact]
    public async Task ImportRejectsANonImage()
    {
        var text = Path.Combine(_root, "notes.png");
        await File.WriteAllTextAsync(text, "not a picture");
        var pictures = Make();

        Assert.False(await pictures.ImportAsync(text));
        Assert.False(File.Exists(pictures.ImportedPath));
        Assert.False(await pictures.ImportAsync(Path.Combine(_root, "missing.png")));
    }

    [Fact]
    public async Task NoneWhenNothingAvailable()
    {
        var result = await Make().GetAsync(game: null, CancellationToken.None);
        Assert.Equal(new IslandMapPictureResult(null, IslandMapSource.None), result);

        // A game texture that cannot be read (no key) falls through as well.
        var broken = new IslandMapPicture.GamePicture("pak|1", () => throw new InvalidDataException("no key"));
        Assert.Equal(IslandMapSource.None, (await Make().GetAsync(broken, CancellationToken.None)).Source);
    }

    [Fact]
    public void TerrainPictureDrawsLandWhereTheMeshIs()
    {
        // A flat square of land 50 m above the sea around the centre of A_0 (two triangles, far coarser than the
        // picture's pixels, as the terrain's distant parts are), everything else sea.
        var (minX, minY, maxX, maxY) = IslandMap.CellRect(MapCell.Parse("A_0"));
        var (cx, cy) = ((minX + maxX) / 2f, (minY + maxY) / 2f);
        float[] land = [cx - 60000, cy - 60000, 5000, cx + 60000, cy - 60000, 5000, cx + 60000, cy + 60000, 5000, cx - 60000, cy + 60000, 5000];

        const int size = 256;
        var rgba = IslandMapPicture.DrawTerrain([(land, [0, 1, 2, 0, 2, 3])], seaLevelCm: 0f, size);
        var (u, v) = IslandMap.ToMap(cx, cy);
        var (px, py) = ((int)(u * size), (int)(v * size));
        var landPixels = 0;
        for (var y = py - 4; y <= py + 4; y++)
        {
            for (var x = px - 4; x <= px + 4; x++)
            {
                var p = Pixel(rgba, size, x, y);
                landPixels += p.G > p.B ? 1 : 0;
            }
        }

        var corner = Pixel(rgba, size, 2, 2);
        Assert.Equal(81, landPixels); // every pixel of the square is land, no holes between the corners
        Assert.True(corner.B > corner.R, $"sea is blue: {corner}");
        Assert.InRange(u, 0.8, 1.0); // A_0 is at the picture's right edge (the game's A0 button is the right one of its row)
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private IslandMapPicture Make() => new(Path.Combine(_root, "cache", "islandmap"), NullLogger.Instance, terrainDirectory: null);

    private IslandMapPicture.GamePicture Game() => new("pakchunk14-WindowsNoEditor.pak|IslandMap|1234", () =>
    {
        Interlocked.Increment(ref _decodes);
        return Image(64);
    });

    private static TextureImage Image(int size)
    {
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)i, 90, 160, 255);
        }

        return new TextureImage("IslandMap", size, size, rgba, "PF_B8G8R8A8", 0, size, size, true, false);
    }

    private static (byte R, byte G, byte B) Pixel(byte[] rgba, int size, int x, int y) =>
        (rgba[((y * size) + x) * 4], rgba[(((y * size) + x) * 4) + 1], rgba[(((y * size) + x) * 4) + 2]);
}
