using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Tests.Rendering;

namespace ScumStudio.Tests.App;

/// <summary>
/// <see cref="ThumbnailService"/> over the loose fixture packages: textures and materials decode to small PNGs that are
/// cached on disk and reused, other classes give nothing, and a mesh is drawn through the shared off-screen context.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class ThumbnailServiceTests
{
    [FixturesFact]
    public async Task TexturesAndMaterialsAreCachedAsSmallPngs()
    {
        using var catalog = AssetCatalog.OpenLoose(FixturePaths.OrigRoot);
        var directory = TempDirectory();
        try
        {
            using var service = new ThumbnailService(directory, NullLogger.Instance);
            var index = catalog.BuildIndex();
            var texture = index.Entries.First(e => e.ClassName == "Texture2D");
            var png = await service.GetAssetAsync(catalog, texture);
            Assert.NotNull(png);
            Assert.StartsWith(directory, png, StringComparison.Ordinal);
            var (_, width, height) = await ImageExport.LoadRgbaAsync(png!);
            Assert.True(width <= ThumbnailService.Size && height <= ThumbnailService.Size, $"{width}x{height}");

            // Second call: the cached file, not a new decode.
            var stamp = File.GetLastWriteTimeUtc(png!);
            Assert.Equal(png, await service.GetAssetAsync(catalog, texture));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(png!));

            // A material shows its base-colour texture (the WolfsWagen instances have theirs in the fixtures).
            var found = false;
            foreach (var material in index.Entries.Where(e => e.ClassName == "MaterialInstanceConstant" && e.PackagePath.Contains("WolfsWagen", StringComparison.Ordinal)))
            {
                if (await service.GetAssetAsync(catalog, material) is not null)
                {
                    found = true;
                    break;
                }
            }

            Assert.True(found, "no WolfsWagen material gave a thumbnail");

            // Other classes and unresolved classes have no thumbnail.
            var other = index.Entries.First(e => e.ClassName is not null && !ThumbnailService.Supports(e.ClassName));
            Assert.Null(await service.GetAssetAsync(catalog, other));
            Assert.Null(await service.GetAssetAsync(catalog, texture with { ClassName = null }));
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [GlFact]
    public async Task MeshesAreDrawnThroughTheOffscreenContext()
    {
        if (FixturePaths.SkipReason is not null)
        {
            return;
        }

        using var catalog = AssetCatalog.OpenLoose(FixturePaths.OrigRoot);
        var directory = TempDirectory();
        try
        {
            using var service = new ThumbnailService(directory, NullLogger.Instance);
            var mesh = catalog.BuildIndex().Entries.First(e => e.Name == "SK_WolfsWagen");
            var png = await service.GetAssetAsync(catalog, mesh);
            Assert.NotNull(png);
            var (rgba, width, height) = await ImageExport.LoadRgbaAsync(png!);
            Assert.Equal((ThumbnailService.Size, ThumbnailService.Size), (width, height));
            // Something was drawn: not every pixel is the clear colour.
            var first = (rgba[0], rgba[1], rgba[2]);
            Assert.Contains(Enumerable.Range(0, width * height), i => (rgba[i * 4], rgba[(i * 4) + 1], rgba[(i * 4) + 2]) != first);
        }
        finally
        {
            Cleanup(directory);
        }
    }

    [Fact]
    public void ShrinkHalvesUntilTheLargestEdgeFits()
    {
        var rgba = new byte[8 * 4 * 4];
        Array.Fill(rgba, (byte)200);
        var (small, width, height) = ThumbnailService.Shrink(rgba, 8, 4, 3);
        Assert.Equal((2, 1), (width, height));
        Assert.Equal(8, small.Length);
        Assert.All(small, b => Assert.Equal(200, b));
    }

    private static string TempDirectory() => Path.Combine(Path.GetTempPath(), "scumstudio-thumbs", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
