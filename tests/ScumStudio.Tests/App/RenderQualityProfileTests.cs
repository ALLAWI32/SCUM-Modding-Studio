using System.Text.Json;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core.Mathematics;
using ScumStudio.Core.Settings;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.World;
using ScumStudio.Tests.Core;

namespace ScumStudio.Tests.App;

/// <summary>
/// The 3D quality presets (the Game-like one, the ground texture each bakes: audit F460) and the far view and Smooth
/// settings (owner: "high FPS AND far render distance at the same time").
/// </summary>
public sealed class RenderQualityProfileTests
{
    [Fact]
    public void GameLikeDrawsAsTheGameDecides()
    {
        var game = RenderQualityProfile.For(RenderQuality.GameLike);
        Assert.Equal(1f, game.LodBias); // the cooked LOD screen sizes as UE's ComputeStaticMeshLOD uses them
        Assert.Equal(0f, game.ObjectDistanceCm); // the game's own cull distances
        Assert.Equal(1f, game.CullPixels);
        Assert.Equal(RenderQualityProfile.For(RenderQuality.Ultra).StreamRadiusCm, game.StreamRadiusCm);
        Assert.Equal(1024, game.TextureSize);
        Assert.Equal(15_000f, game.ShadowDistanceCm);
    }

    [Fact]
    public void GroundTextureFollowsTheQuality()
    {
        // F460: every preset baked 1024² per terrain component (4 MiB of RAM, 5.6 MB of video memory each).
        var size = (RenderQuality q) => RenderQualityProfile.For(q).TerrainTextureSize;
        Assert.Equal(256, size(RenderQuality.Performance));
        Assert.Equal(512, size(RenderQuality.Balanced));
        Assert.Equal([1024, 1024, 1024], new[] { size(RenderQuality.High), size(RenderQuality.Ultra), size(RenderQuality.GameLike) });
    }

    [Fact]
    public void SavedQualitiesKeepTheirMeaning()
    {
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<RenderQuality>().Select(q => (int)q));
        Assert.Equal(RenderQuality.GameLike, Enum.GetValues<RenderQuality>()[^1]);
        var json = JsonSerializer.Serialize(new UiPreferences { RenderQuality = RenderQuality.GameLike }, SettingsStore.JsonOptions);
        Assert.Contains("\"gameLike\"", json);
        Assert.Equal(RenderQuality.GameLike, JsonSerializer.Deserialize<UiPreferences>(json, SettingsStore.JsonOptions)!.RenderQuality);
    }

    [Fact]
    public void RenderDistanceIsOneToFourSectorsAndSmoothIsOn()
    {
        var defaults = new UiPreferences();
        Assert.Equal(2, defaults.RenderDistanceSectors);
        Assert.True(defaults.SmoothFrames);
        Assert.Equal(1, (defaults with { RenderDistanceSectors = 0 }).RenderDistanceSectors);
        Assert.Equal(4, (defaults with { RenderDistanceSectors = 9 }).RenderDistanceSectors);

        using var temp = new TempFolder();
        var store = new SettingsStore(temp.Path);
        var settings = new AppSettings { Ui = new UiPreferences { RenderDistanceSectors = 3, SmoothFrames = false, RenderQuality = RenderQuality.GameLike } };
        store.Save(settings);
        Assert.Equal(settings, new SettingsStore(temp.Path).Load());

        // A hand-edited file asking for more than the four sectors the far view is made for.
        File.WriteAllText(store.FilePath, File.ReadAllText(store.FilePath).Replace("\"renderDistanceSectors\": 3", "\"renderDistanceSectors\": 12", StringComparison.Ordinal));
        Assert.Equal(4, new SettingsStore(temp.Path).Load().Ui.RenderDistanceSectors);
    }

    [Fact]
    public void ASectorIsTheMedianCellOfTheLandscapeTiles()
    {
        // Cells of 2.8 km, 2.8 km and 2.4 km (each four quadrant tiles), a POI level and a tile without bounds that must not count.
        var packages = new List<WorldPackage>();
        foreach (var (cell, width) in new[] { (new MapCell('A', 0), 280_000f), (new MapCell('A', 1), 280_000f), (new MapCell('B', 0), 240_000f) })
        {
            for (var q = 0; q < 4; q++)
            {
                var x0 = (cell.Row * 300_000f) + (q % 2 * width / 2);
                var y0 = ((cell.Column - 'A') * 300_000f) + (q / 2 * width / 2);
                packages.Add(Package($"Landscape_{cell.Column}_{cell.Row}_{q + 1}", WorldPackageKind.Landscape, cell, new FVector(x0, y0, -5000), new FVector(x0 + (width / 2), y0 + (width / 2), 20_000)));
            }
        }

        packages.Add(Package("A_0_Outpost", WorldPackageKind.Poi, new MapCell('A', 0), new FVector(0, 0, 0), new FVector(900_000, 900_000, 0)));
        packages.Add(Package("Landscape_C_0_1", WorldPackageKind.Landscape, new MapCell('C', 0), default, default) with { Tile = null });

        Assert.Equal(280_000f, MapPageViewModel.SectorSizeOf(packages));
        Assert.Equal(MapPageViewModel.DefaultSectorSizeCm, MapPageViewModel.SectorSizeOf([]));
    }

    private static WorldPackage Package(string name, WorldPackageKind kind, MapCell cell, FVector min, FVector max) =>
        new($"SCUM/Content/ConZ_Files/Maps/The_Island/{name}.umap", $"/Game/ConZ_Files/Maps/The_Island/{name}", name, string.Empty, kind, cell, true)
        {
            Tile = new WorldTileInfo((0, 0, 0), min, max, true, new WorldTileLayer("Layer", 0, (0, 0), 0, false), false, string.Empty, [], 0),
        };
}
