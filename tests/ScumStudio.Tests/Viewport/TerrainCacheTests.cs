using System.Diagnostics;
using System.Numerics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

public sealed class TerrainCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scumstudio-terrain-cache", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void TheIslandTerrainComesBackAsItWasKept()
    {
        var mesh = new MeshData("C_0", [0, 0, 0, 100, 0, 0, 0, 100, 5], [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 0, 1, 0, 0, 1], [0, 1, 2],
            [new MeshSection("Ground", 0, 3)], new BoundingBox(Vector3.Zero, new Vector3(100, 100, 5)));
        var scene = new PreparedLevelScene([], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, ScumStudio.Assets.Textures.TextureImage>(), [],
            [new PreparedTerrain("LandscapeComponent_0", "Landscape_A_0_1", mesh) { Albedo = new TerrainAlbedo(2, [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255]) }],
            [], TimeSpan.Zero) { Ground = GroundMode.Realistic, SeaLevelCm = null };
        var path = Path.Combine(_dir, "island-terrain-test.bin");

        TerrainCache.Save(path, scene);
        var back = TerrainCache.TryLoad(path)!;

        var t = Assert.Single(back.Terrain);
        Assert.Equal("Landscape_A_0_1", t.LevelName);
        Assert.Equal(mesh.Positions, t.Mesh.Positions);
        Assert.Equal(mesh.Indices, t.Mesh.Indices);
        Assert.Equal(mesh.Bounds, t.Mesh.Bounds);
        Assert.Equal("Ground", t.Mesh.Sections[0].MaterialName);
        Assert.Equal(scene.Terrain[0].Albedo!.Rgba, t.Albedo!.Rgba);
        Assert.Null(back.SeaLevelCm);
        Assert.Null(TerrainCache.TryLoad(Path.Combine(_dir, "missing.bin")));
    }

    /// <summary>Real game files only (<c>SCUM_PAKS</c>): building the island once, then reading it back, much faster.</summary>
    [Fact]
    public void TheRealIslandReadsBackFasterThanItBuilds()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return; // not asked for
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var tiles = WorldIndex.FromCatalog(catalog).Packages.Where(p => p.IsMap && p.Kind == WorldPackageKind.Landscape).Select(p => p.PackagePath).ToList();
        var options = new LevelSceneOptions { LandscapeStep = 16, TerrainTextureSize = 128 };
        var path = TerrainCache.PathFor(_dir, catalog, tiles, options);

        var build = Stopwatch.StartNew();
        var built = new LevelScenePreparer(catalog).PrepareTerrain(tiles, options);
        build.Stop();
        TerrainCache.Save(path, built);
        var read = Stopwatch.StartNew();
        var back = TerrainCache.TryLoad(path)!;
        read.Stop();

        Assert.Equal(built.Terrain.Count, back.Terrain.Count);
        Assert.True(read.Elapsed < build.Elapsed / 4, $"read {read.ElapsedMilliseconds} ms, built {build.ElapsedMilliseconds} ms, {new FileInfo(path).Length / 1048576} MB");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "scumstudio-terrain-timing.txt"),
            $"built {build.ElapsedMilliseconds} ms, read {read.ElapsedMilliseconds} ms, file {new FileInfo(path).Length / 1048576} MB, {back.Terrain.Count} components");
    }
}
