using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Assets.Textures;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Pak;
using ScumStudio.Viewport;
using Xunit.Abstractions;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// The r37 verifier's leftovers on the Map at Game-like, on the real game files (<c>SCUM_PAKS</c>, key from this PC's store;
/// skipped without): the B_4 hangar's white diamond and white engine cubes, the far view's broken roads and the airfield's
/// flat brown smudge.
/// </summary>
public sealed class MapLeftoversRealTests(ITestOutputHelper output)
{
    private const string Maps = "/Game/ConZ_Files/Maps/The_Island/";

    private static AssetCatalog? Open() =>
        Environment.GetEnvironmentVariable("SCUM_PAKS") is { Length: > 0 } paks && Directory.Exists(paks)
            ? AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() })
            : null;

    /// <summary>
    /// The white diamond high on the B_4 hangar's back wall was the Map's pin of <c>VolumeZone13</c> (a "NoSea" zone: a brush,
    /// nothing drawn); the white walls of B_4_Outpost_Exterior are engine cubes whose actor is hidden in the game.
    /// </summary>
    [Fact]
    public void TheB4HangarHasNoWhiteDiamondAndTheHiddenCubesAreNotDrawn()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return; // not asked for
        }

        var reader = new Cue4ParseLevelReader(catalog);
        var airfield = LevelDocument.Load(reader, Maps + "B_4_Outpost_Airfield");
        var exterior = LevelDocument.Load(reader, Maps + "B_4_Outpost_Exterior");
        var zone = airfield.Actors.Single(a => a.Name == "VolumeZone13");
        var cubes = exterior.Actors.Where(a => a.StaticMeshPath?.StartsWith("/Engine/BasicShapes/Cube", StringComparison.OrdinalIgnoreCase) == true).ToList();
        var pins = LevelScenePreparer.CollectPlacements(airfield, 0, new LevelSceneOptions { IncludeInstances = false }).Where(p => SpawnMarkers.IsMarker(p.MeshPath)).ToList();
        var drawn = LevelScenePreparer.CollectPlacements(exterior, 0, new LevelSceneOptions { IncludeInstances = false });
        output.WriteLine($"VolumeZone13: root {zone.Root?.ClassName}; {pins.Count} pins in the airfield ({string.Join(", ", pins.Select(p => p.Actor.Name + " " + p.MeshPath))}); cubes {string.Join(", ", cubes.Select(c => $"{c.Name} hidden {c.HiddenInGame}"))}");

        Assert.Equal("BrushComponent", zone.Root?.ClassName);
        Assert.DoesNotContain(pins, p => p.Actor.Name.StartsWith("VolumeZone", StringComparison.Ordinal));
        Assert.Equal(["Cube4", "Cube5", "Cube6", "Cube7"], cubes.Select(c => c.Name).Order());
        Assert.All(cubes, c => Assert.True(c.HiddenInGame, c.Name));
        Assert.DoesNotContain(drawn, p => p.MeshPath.StartsWith("/Engine/BasicShapes/Cube", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(drawn, p => p.MeshPath.StartsWith("/Game/", StringComparison.Ordinal)); // the level itself is still drawn
    }

    /// <summary>
    /// The far view of B_4's landscape tile: its far model's ground (the island-wide blurred texture) gives way to the
    /// backdrop's terrain of the tile, its distant forest stays.
    /// </summary>
    [Fact]
    public void TheFarModelOfB4sLandscapeTileKeepsItsForestWithoutItsGround()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        const string tile = "Landscape_B_4_2b";
        var models = FarModelsScene.Read(catalog, WorldIndex.FromCatalog(catalog)).Where(m => m.LevelPath == Maps + tile).ToList();
        var far = FarModelsScene.Prepare(catalog, models);
        var terrain = new PreparedLevelScene([], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, TextureImage>(), [],
            [new PreparedTerrain("LandscapeComponent_463", tile, far.Meshes.Values.First().Mesh)], [], TimeSpan.Zero);

        var combined = FarModelsScene.Combine(terrain, far);

        var model = Assert.Single(combined.Placements);
        var before = far.Meshes[models[0].MeshPath].Mesh.Sections.Select(s => s.MaterialName).ToList();
        var after = combined.Meshes[model.MeshPath].Mesh.Sections.Select(s => s.MaterialName).ToList();
        output.WriteLine($"{models[0].MeshPath}: [{string.Join(", ", before)}] -> {model.MeshPath}: [{string.Join(", ", after)}]");
        Assert.Contains(before, m => m.Contains("DistantLandscape", StringComparison.Ordinal));
        Assert.DoesNotContain(after, m => m.Contains("DistantLandscape", StringComparison.Ordinal));
        Assert.Contains(after, m => m.Contains("Foliage_Distant", StringComparison.Ordinal));
    }

    /// <summary>
    /// B_4 airfield's brown smudge in front of the hangar is the dry grass layer (93% at (584376, -222064)) drawn without a
    /// close-up slot: its component has six layers on its dry ground, four slots. It now gives its grain to the rest of the ground\n    /// (the fifth layer), the largest patch the four slots leave.
    /// </summary>
    [Fact]
    public void TheAirfieldsDryGrassPatchTakesACloseUpSlot()
    {
        using var catalog = Open();
        if (catalog is null)
        {
            return;
        }

        var reader = new Cue4ParseLevelReader(catalog);
        var tile = LevelDocument.Load(reader, Maps + "Landscape_B_4_2b");
        var scene = new LevelScenePreparer(catalog).Prepare([tile], new LevelSceneOptions { IncludeInstances = false, TextureSize = 64 });
        var under = scene.HeightField!.SampleLayers(584376f, -222064f);
        output.WriteLine("ground at the smudge: " + string.Join(", ", under.Select(l => $"{l.Name} {l.Weight:0.00}")));
        Assert.Equal("Dry_Grass_01", under[0].Name);

        var checkedAny = false;
        foreach (var terrain in scene.Terrain.Where(t => t.Layers is not null))
        {
            var layers = terrain.Layers!;
            var patch = layers.Layers.FirstOrDefault(l => l.Name == "Dry_Grass_01" && l.Weights.Count(w => w >= 230) >= 100);
            if (patch is null)
            {
                continue;
            }

            bool Drawable(LandscapeLayerWeights l) => scene.LayerCatalog!.Resolve(l.Name) is { DiffuseTexture: { } path, TilingCm: > 0f } && scene.LayerTextures!.Images.ContainsKey(path);
            var slots = LevelSceneUploader.DetailLayers(layers, terrain.Surface, scene.SeaLevelCm ?? 0f, Drawable).Where(Drawable).Take(LevelSceneUploader.DetailSlots + 1).Select(l => l.Name).ToList();
            output.WriteLine($"{terrain.Name}: slots [{string.Join(", ", slots.Take(LevelSceneUploader.DetailSlots))}], the rest of the ground: {slots.ElementAtOrDefault(LevelSceneUploader.DetailSlots)}");
            Assert.Contains("Dry_Grass_01", slots);
            checkedAny = true;
        }

        Assert.True(checkedAny, "no component of the tile has the dry grass patch");
    }
}

/// <summary>
/// The close-up slot pick (<see cref="LevelSceneUploader.DetailLayers"/>) runs on the render thread as the ground streams
/// in: on B_4's airfield tile it adds little to the order by weight alone (what the uploader did before). Real game files
/// only (<c>SCUM_PAKS</c>), timed apart from the parallel tests.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class MapLeftoversTimingRealTests(ITestOutputHelper output)
{
    [Fact]
    public void TheSlotPickAddsLittleOnTheRenderThread()
    {
        if (Environment.GetEnvironmentVariable("SCUM_PAKS") is not { Length: > 0 } paks || !Directory.Exists(paks))
        {
            return;
        }

        using var catalog = AssetCatalog.OpenPaks(paks, new AssetCatalogOptions { AesKey = AesKeyText.FromEnvironmentOrStore() });
        var tile = LevelDocument.Load(new Cue4ParseLevelReader(catalog), "/Game/ConZ_Files/Maps/The_Island/Landscape_B_4_2b");
        var scene = new LevelScenePreparer(catalog).Prepare([tile], new LevelSceneOptions { IncludeInstances = false, TextureSize = 64 });

        double PerComponent(Func<LandscapeLayerWeights, bool>? drawable)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var round = 0; round < 3; round++)
            {
                foreach (var terrain in scene.Terrain.Where(t => t.Layers is not null))
                {
                    LevelSceneUploader.DetailLayers(terrain.Layers!, terrain.Surface, scene.SeaLevelCm ?? 0f, drawable);
                }
            }

            return clock.Elapsed.TotalMilliseconds / (3 * Math.Max(1, scene.Terrain.Count));
        }

        PerComponent(null); // JIT
        var byWeight = PerComponent(_ => false);
        var picked = PerComponent(null);
        output.WriteLine($"slot pick: {picked:0.00} ms a component, by weight alone {byWeight:0.00} ms");
        if (!Ci.On)
        {
            Assert.True(picked - byWeight < 3.0, $"the pick adds {picked - byWeight:0.0} ms a component");
        }
    }
}
