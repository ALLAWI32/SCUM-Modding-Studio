using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.Fixtures;
using ScumStudio.Tests.Level;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.App;

/// <summary>
/// <see cref="ThumbnailService"/> over the loose fixture packages: textures and materials decode to small PNGs that are
/// cached on disk and reused, other classes give nothing, and a mesh is drawn through the shared off-screen context.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class ThumbnailServiceTests
{
    [Fact]
    public void FoliageTypesGetThumbnails()
    {
        // Most of the Assets page's trees are foliage types (ScotsPine_FT): their tiles stayed empty.
        Assert.True(ThumbnailService.Supports("FoliageType_InstancedStaticMesh"));
        Assert.False(ThumbnailService.Supports("SoundWave"));
    }

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
    public void CacheKeyFollowsTheDecoderAndLookVersions()
    {
        // r39 (owner, igor8802): 3.5.0 decoded the ZIL trucks' streaming virtual textures, but the cache key stayed the same,
        // so the white tiles 3.4.0 had written were read back for good. The key and the folder now carry the versions of the
        // loader and of the decoder: bumping either one makes every thumbnail again, without touching the service.
        var directory = TempDirectory();
        using var now = new ThumbnailService(directory, NullLogger.Instance);
        using var newLook = new ThumbnailService(directory, NullLogger.Instance, ThumbnailService.VersionOf(MeshPreviewLoader.LookVersion + 1, TextureDecoder.Version));
        using var newDecoder = new ThumbnailService(directory, NullLogger.Instance, ThumbnailService.VersionOf(MeshPreviewLoader.LookVersion, TextureDecoder.Version + 1));
        Assert.Equal(ThumbnailService.VersionOf(MeshPreviewLoader.LookVersion, TextureDecoder.Version), now.Version);

        const string mesh = "/Game/ConZ_Files/Models/Vehicles/Zil_Truck/130_Emergency/MainMesh/SM_130E_Cabin_01";
        ThumbnailService[] services = [now, newLook, newDecoder];
        var files = services.Select(s => s.CacheFile(mesh, 1234)).ToList();
        Assert.Equal(3, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(services, s => Assert.Equal(Path.Combine(directory, "v" + s.Version), Path.GetDirectoryName(s.CacheFile(mesh, 1234))));

        // The same package gives the same file; a game update (another package size) another one.
        Assert.Equal(files[0], now.CacheFile(mesh, 1234));
        Assert.NotEqual(files[0], now.CacheFile(mesh, 1235));
    }

    [Fact]
    public async Task OldVersionFoldersAreSwept()
    {
        // 3.5.1 and earlier wrote every version's tiles side by side into the cache folder and never removed one (68 MB of
        // dead tiles on the owner's PC). The first thumbnail asked for removes the other versions' folders and the loose
        // PNGs of that flat layout; what is no thumbnail cache stays.
        using var temp = new LevelTempDirectory();
        Directory.CreateDirectory(temp.Combine("SCUM", "Content", "T"));
        File.WriteAllBytes(temp.Combine("SCUM", "Content", "T", "T_Sweep.uasset"), [0]); // only its size is read
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        var directory = TempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "v0.0.0"));
            File.WriteAllBytes(Path.Combine(directory, "v0.0.0", "0123456789ABCDEF01234567.png"), [1]);
            File.WriteAllBytes(Path.Combine(directory, "abc.png"), [1]);
            File.WriteAllText(Path.Combine(directory, "notes.txt"), "kept");
            Directory.CreateDirectory(Path.Combine(directory, "various"));

            using var service = new ThumbnailService(directory, NullLogger.Instance);
            var png = await service.GetDerivedAsync(catalog, "/Game/T/T_Sweep", "sweep-test", () => (new byte[2 * 2 * 4], 2, 2));
            Assert.NotNull(service.Sweeping);
            await service.Sweeping!;

            Assert.NotNull(png);
            Assert.True(File.Exists(png), png);
            Assert.Equal(service.VersionDirectory, Path.GetDirectoryName(png));
            Assert.False(Directory.Exists(Path.Combine(directory, "v0.0.0")));
            Assert.False(File.Exists(Path.Combine(directory, "abc.png")));
            Assert.True(File.Exists(Path.Combine(directory, "notes.txt")));
            Assert.True(Directory.Exists(Path.Combine(directory, "various")));

            // A second service of the same version keeps that version's tiles.
            using var again = new ThumbnailService(directory, NullLogger.Instance);
            Assert.Equal(png, await again.GetDerivedAsync(catalog, "/Game/T/T_Sweep", "sweep-test", () => null));
            await again.Sweeping!;
            Assert.True(File.Exists(png), png);
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
