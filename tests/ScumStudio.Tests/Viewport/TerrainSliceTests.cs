using System.Numerics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.Context;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Tests.Assets;
using ScumStudio.Tests.Level;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>A fact that needs both the map slice (<c>SCUM_MAP_SLICE</c>) and an OpenGL 4.3 context.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GlMapSliceFactAttribute : FactAttribute
{
    public GlMapSliceFactAttribute()
    {
        if ((MapSlice.SkipReason ?? GlTestEnvironment.SkipReason) is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>
/// The ground of cell A_0 (16 landscape tiles of the map slice) prepared the way the app does it: baked Realistic colours,
/// sea plane, ground queries, and a GL render of a beach.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class TerrainSliceTests
{
    private readonly ITestOutputHelper _output;

    public TerrainSliceTests(ITestOutputHelper output) => _output = output;

    private static PreparedLevelScene PrepareA0(AssetCatalog catalog, LevelSceneOptions options)
    {
        var reader = new Cue4ParseLevelReader(catalog);
        var documents = LandscapeLayerReaderTests.A0Tiles().Select(t => LevelDocument.Load(reader, MapSlice.MapsPath + t)).ToList();
        return new LevelScenePreparer(catalog).Prepare(documents, options);
    }

    [MapSliceFact]
    public void WholeIslandBackdrop_BakesEveryTileAndKeepsNoHeightData()
    {
        using var catalog = MapSlice.Open();
        var tiles = LandscapeLayerReaderTests.A0Tiles().Select(t => MapSlice.MapsPath + t).ToList();
        var prepared = new LevelScenePreparer(catalog).PrepareTerrain(tiles, new LevelSceneOptions { LandscapeStep = 16, TerrainTextureSize = 64 });
        _output.WriteLine($"terrain of {tiles.Count} tiles in {prepared.Elapsed.TotalMilliseconds:0} ms");
        Assert.Equal(64, prepared.Terrain.Count);
        Assert.All(prepared.Terrain, t =>
        {
            Assert.Equal(64, t.Albedo!.Size);
            Assert.Null(t.Surface); // dropped after baking: 400 tiles must fit in memory
        });
        Assert.Empty(prepared.Documents);
    }

    [MapSliceFact]
    public void CellA0_Realistic_IsTheDefault_BakesEveryComponent_AndAddsTheSea()
    {
        using var catalog = MapSlice.Open();
        var prepared = PrepareA0(catalog, new LevelSceneOptions { LandscapeStep = 4, IncludeInstances = false, TextureSize = 0 });
        _output.WriteLine($"prepared in {prepared.Elapsed.TotalMilliseconds:0} ms, bake {prepared.TerrainBakeTime.TotalMilliseconds:0} ms");
        Assert.Equal(GroundMode.Realistic, new LevelSceneOptions().Ground);
        Assert.Equal(GroundMode.Realistic, prepared.Ground);
        Assert.Equal(64, prepared.Terrain.Count);
        Assert.All(prepared.Terrain, t =>
        {
            Assert.NotNull(t.Albedo);
            Assert.Equal(256, t.Albedo!.Size);
            Assert.NotNull(t.Layers);
            Assert.NotNull(t.Surface);
            Assert.Equal(65 * 65, t.Mesh.VertexCount);
        });
        Assert.Equal(0f, prepared.SeaLevelCm);
        Assert.Equal(64, prepared.HeightField!.ComponentCount);
        Assert.NotNull(prepared.LayerCatalog);
        Assert.Empty(prepared.LayerCatalog!.UnknownLayers);
        Assert.DoesNotContain(prepared.Warnings, w => w.Contains("not in the layer table", StringComparison.Ordinal));
        Assert.Contains("/Game/ConZ_Files/Landscape/LandscapeTextures/T_Beach_Sand_D", prepared.MissingLayerTextures);

        // Beach samples a little above the waterline bake to sand colours (not the old flat green tint).
        var sand = prepared.LayerCatalog.Rules.Sand.Color;
        var beachPixels = 0;
        foreach (var t in prepared.Terrain)
        {
            var beach = t.Layers!.Find("Beach_Sand");
            if (beach is null)
            {
                continue;
            }

            var n = t.Surface!.SampleCount;
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var z = t.Surface.HeightAt(x, y);
                    if (beach.Weights[y * n + x] >= 250 && z is > 60f and < 150f && t.Surface.NormalAt(x, y).Z > 0.95f)
                    {
                        TerrainAlbedoBakerTests.AssertNear(sand, TerrainAlbedoBakerTests.Pixel(t.Albedo!, x, y), 3);
                        beachPixels++;
                    }
                }
            }
        }

        _output.WriteLine($"beach samples checked: {beachPixels}");
        Assert.True(beachPixels > 50, $"{beachPixels} beach samples");

        var plain = PrepareA0(catalog, new LevelSceneOptions { LandscapeStep = 8, IncludeInstances = false, TextureSize = 0, Ground = GroundMode.Plain, SeaPlane = false });
        Assert.All(plain.Terrain, t => Assert.Null(t.Albedo));
        Assert.Null(plain.SeaLevelCm);
    }

    [MapSliceFact]
    public void CellA0_HeightField_AtTheOutpost_IsInTheTileBounds_OnAsphaltOrGravel()
    {
        using var catalog = MapSlice.Open();
        var prepared = PrepareA0(catalog, new LevelSceneOptions { LandscapeStep = 8, IncludeInstances = false, TextureSize = 0, Ground = GroundMode.Plain });
        var field = prepared.HeightField!;
        var z = field.SampleHeight(-622000f, -556000f);
        Assert.NotNull(z);
        // A_0_Outpost_Exterior FWorldTileInfo Z bounds.
        Assert.InRange(z!.Value, -329f, 3262f);
        var layers = field.SampleLayers(-622000f, -556000f);
        _output.WriteLine(field.Describe(-622000f, -556000f));
        Assert.Contains(layers, l => l.Name is "Asphalt" or "Gravel");

        // A ray straight down from 500 m hits the same height; a slanted one from the sea hits the ground too.
        var down = field.Raycast(new Vector3(-622000f, -556000f, 50000f), -Vector3.UnitZ)!.Value;
        Assert.Equal(z.Value, down.Position.Z, 1);
        var slanted = field.Raycast(new Vector3(-640000f, -575000f, 20000f), Vector3.Normalize(new Vector3(18000f, 19000f, -20000f)))!.Value;
        Assert.Equal(field.SampleHeight(slanted.Position.X, slanted.Position.Y)!.Value, slanted.Position.Z, 1);
    }

    [GlMapSliceFact]
    public void CellA0_Upload_DrawsSandOnTheBeach_NoGrid_OutdoorSky_AndSwitchesModes()
    {
        using var catalog = MapSlice.Open();
        var prepared = PrepareA0(catalog, new LevelSceneOptions { LandscapeStep = 4, IncludeInstances = false, TextureSize = 0 });

        // A flat, fully sandy beach sample above the waterline.
        (PreparedTerrain Terrain, int X, int Y)? beach = null;
        foreach (var t in prepared.Terrain)
        {
            var sandLayer = t.Layers!.Find("Beach_Sand");
            var n = t.Surface!.SampleCount;
            for (var y = 4; y < n - 4 && beach is null && sandLayer is not null; y++)
            {
                for (var x = 4; x < n - 4; x++)
                {
                    var flat = Enumerable.Range(-3, 7).All(d => sandLayer.Weights[(y + d) * n + x] >= 250 && sandLayer.Weights[y * n + x + d] >= 250);
                    if (flat && t.Surface.HeightAt(x, y) is > 60f and < 200f && t.Surface.NormalAt(x, y).Z > 0.97f)
                    {
                        beach = (t, x, y);
                        break;
                    }
                }
            }
        }

        Assert.NotNull(beach);
        var (terrain, bx, by) = beach!.Value;
        var target = terrain.Surface!.WorldPosition(bx, by);

        using var harness = GlHarness.Create(96, 96);
        using var level = LevelSceneUploader.Upload(harness.Renderer, prepared);
        Assert.True(level.HasSea);
        Assert.NotNull(level.Scene.Environment);
        Assert.False(harness.Renderer.EffectiveSettings(level.Scene).ShowGrid);

        // Unlit straight-down view of the beach: the centre pixel shows the baked sand colour.
        harness.Renderer.Settings = new RenderSettings
        {
            SkyColor = Vector3.One,
            GroundColor = Vector3.One,
            LightColor = Vector3.Zero,
            ShowGrid = true,
            UseSceneEnvironment = false,
        };
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 100_000f);
        camera.Orbit(UeToGl.Point(new FVector(target.X, target.Y, target.Z)), 0f, -89f, 1500f);
        harness.Renderer.Render(harness.Target, level.Scene, camera);
        var centre = harness.Pixel(harness.Target.ReadColorRgba(), 48, 48);
        var sand = prepared.LayerCatalog!.Rules.Sand.Color;
        _output.WriteLine($"beach pixel {centre[0]},{centre[1]},{centre[2]} vs sand {sand}");
        TerrainAlbedoBakerTests.AssertNear(sand, new TerrainColor(centre[0], centre[1], centre[2]), 6);

        // Plain mode through SetTerrainAlbedo: the same pixel becomes the old flat tint.
        level.SetTerrainAlbedo(new TerrainAlbedo?[prepared.Terrain.Count], GroundMode.Plain);
        Assert.Equal(GroundMode.Plain, level.Ground);
        harness.Renderer.Render(harness.Target, level.Scene, camera);
        var plain = harness.Pixel(harness.Target.ReadColorRgba(), 48, 48);
        var tint = TerrainColor.FromLinear(new Vector3(LevelSceneUploader.TerrainTint.X, LevelSceneUploader.TerrainTint.Y, LevelSceneUploader.TerrainTint.Z));
        TerrainAlbedoBakerTests.AssertNear(tint, new TerrainColor(plain[0], plain[1], plain[2]), 4);

        // And back to Layers colours, re-baked from the prepared data.
        var debug = LevelScenePreparer.BakeTerrain(prepared.Terrain, new TerrainBakeSettings { Mode = GroundMode.Layers }, out _);
        level.SetTerrainAlbedo(debug, GroundMode.Layers);
        harness.Renderer.Render(harness.Target, level.Scene, camera);
        var layers = harness.Pixel(harness.Target.ReadColorRgba(), 48, 48);
        TerrainAlbedoBakerTests.AssertNear(TerrainLayerCatalog.Default.Resolve("Beach_Sand").DebugColor, new TerrainColor(layers[0], layers[1], layers[2]), 6);
    }
}
