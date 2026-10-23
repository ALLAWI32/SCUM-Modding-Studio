using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Pak;
using ScumStudio.Rendering.Imaging;
using ScumStudio.Tests.Rendering;
using Xunit.Abstractions;

namespace ScumStudio.Tests.App;

/// <summary>
/// r39 (owner; Discord igor8802, 19.png): every Assets tile of the ZIL trucks was pure white while their 3D view was
/// textured. Their colour maps are streaming virtual textures: 3.4.0 could not decode them and cached white tiles, 3.5.0
/// could, but its cache key was the same and the white tiles were read back. Made now, the tiles are painted. Real game
/// files and OpenGL.
/// </summary>
[Collection(GlCollection.Name)] // the tiles render with OpenGL: never beside another GL test
public sealed class ThumbnailVirtualTextureRealTests(ITestOutputHelper output)
{
    private const string Cabin = "/Game/ConZ_Files/Models/Vehicles/Zil_Truck/130_Emergency/MainMesh/SM_130E_Cabin_01";
    private const string Truck = "/Game/ConZ_Files/Models/Vehicles/Zil_Truck/ZIL130/BP_Zil_130_Truck_01";

    [PaksFact]
    public async Task AVirtualTexturedVehicleTileIsNotWhite()
    {
        if (GlTestEnvironment.SkipReason is not null)
        {
            return; // no OpenGL here
        }

        using var catalog = AssetCatalog.OpenPaks(RealPaks.Folder!, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var directory = Path.Combine(Path.GetTempPath(), "scumstudio-thumbs", Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new ThumbnailService(directory, NullLogger.Instance); // a fresh cache: the tiles are made now
            var cabin = await service.GetAssetAsync(catalog, new PackageEntry("SCUM/Content" + Cabin["/Game".Length..] + ".uasset", Cabin, "StaticMesh"));
            var truck = await service.GetBlueprintAsync(catalog, Truck);
            // The share of saturated pixels the diagnosis measured on these tiles (r39 stats.tsv: the cabin 0.146, the whole
            // truck 0.044, it shows more grey chassis), halved. A truck drawn in its white surface colour has none at all
            // (0.000, like the railway drawn with the grey ice layer): under the tile's warm grey light white turns grey, so
            // the share of near-white pixels stays 0 either way and is only a bound.
            foreach (var (name, png, minimum) in new[] { ("SM_130E_Cabin_01 (StaticMesh tile)", cabin, 0.07), ("BP_Zil_130_Truck_01 (Blueprint tile)", truck, 0.02) })
            {
                Assert.True(png is not null, $"no tile for {name}");
                var (rgba, width, height) = await ImageExport.LoadRgbaAsync(png!);
                var (white, saturated) = Shares(rgba, width * height);
                output.WriteLine($"{name}: {width}x{height}, white {white:0.000}, saturated {saturated:0.000}");
                Assert.True(saturated > minimum, $"{name}: saturated {saturated:0.000} (at least {minimum}), the tile is not painted");
                Assert.True(white < 0.20, $"{name}: white {white:0.000}");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>The share of near-white pixels (every channel at least 235) and of saturated ones (max - min at least 30: paint, rust, wood).</summary>
    private static (double White, double Saturated) Shares(byte[] rgba, int pixels)
    {
        var white = 0;
        var saturated = 0;
        for (var i = 0; i < pixels; i++)
        {
            int r = rgba[i * 4], g = rgba[(i * 4) + 1], b = rgba[(i * 4) + 2];
            if (r >= 235 && g >= 235 && b >= 235)
            {
                white++;
            }

            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) >= 30)
            {
                saturated++;
            }
        }

        return (white / (double)pixels, saturated / (double)pixels);
    }
}
