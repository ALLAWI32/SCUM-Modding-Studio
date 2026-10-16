using System.Reflection;
using ScumStudio.Core.Geometry;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Rendering;
using ScumStudio.Rendering.Cameras;
using ScumStudio.Rendering.SceneGraph;
using ScumStudio.Tests.Rendering;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// Whole-island mode draws the game's own far view (the <c>DistantLevelManager</c>'s merged models) behind the levels
/// loaded in detail: each model where the game puts it, never culled by a draw distance of its own, and hidden in the same
/// frame its level is shown in detail.
/// </summary>
[Collection(GlCollection.Name)]
public sealed class FarModelsSceneTests
{
    private const string Maps = "/Game/ConZ_Files/Maps/The_Island/";

    private static readonly FarModelsScene.FarModel[] Models =
    [
        new("A_0_Outpost", Maps + "A_0_Outpost", "/Game/ConZ_Files/Maps/The_Island/Landscape/Distant_Models/A_0/SM_A_0_Outpost_LOD0.SM_A_0_Outpost_LOD0",
            new FTransform(new FRotator(0f, 37.5f, 0f).Quaternion(), new FVector(-622_000f, -556_000f, 3_000f), FVector.One)),
        new("B_2_Farm", Maps + "B_2_Farm", "/Game/ConZ_Files/Maps/The_Island/Landscape/Distant_Models/B_2/SM_B_2_Farm_LOD0.SM_B_2_Farm_LOD0",
            new FTransform(new FRotator(2f, -90f, 1f).Quaternion(), new FVector(10_000f, 20_000f, 1_500f), new FVector(1.5f, 1.5f, 1f))),
        new("Seabed_04", null, "/Game/ConZ_Files/Maps/The_Island/Landscape/Distant_Models/Seabed/SM_Seabed_04.SM_Seabed_04",
            new FTransform(FQuat.Identity, new FVector(400_000f, 0f, -5_000f), new FVector(4f, 4f, 1f))),
    ];

    [Fact]
    public void DocumentHasOneActorPerFarModelAtItsTransform()
    {
        var document = FarModelsScene.Document(Models);

        Assert.Equal(Models.Length, document.Actors.Count);
        for (var i = 0; i < Models.Length; i++)
        {
            var actor = document.Actors[i];
            Assert.Equal(FarModelsScene.ActorClass, actor.ClassName);
            Assert.Equal(Models[i].LevelPath, FarModelsScene.LevelOf(actor));
            var mesh = Assert.Single(actor.Components, c => c.StaticMeshPath is not null);
            Assert.Equal(Models[i].MeshPath, mesh.StaticMeshPath);
            AssertSamePlace(Models[i].World, mesh.WorldTransform);
        }

        var placements = LevelScenePreparer.CollectPlacements(document, 0, FarModelsScene.Options);
        Assert.Equal(Models.Select(m => m.MeshPath), placements.Select(p => p.MeshPath));
        for (var i = 0; i < Models.Length; i++)
        {
            AssertSamePlace(Models[i].World, placements[i].World);
            Assert.Equal(0f, placements[i].CullDistance);
        }
    }

    [Fact]
    public void CombineKeepsTerrainAndAddsFarPlacementsWithoutDrawDistance()
    {
        var terrain = Terrain("Landscape_A_0_1", "Landscape_B_2_3");
        var far = Far(Models);

        var combined = FarModelsScene.Combine(terrain, far);

        Assert.Equal(terrain.Terrain, combined.Terrain); // the very same components: nothing baked again
        Assert.Equal(terrain.SeaLevelCm, combined.SeaLevelCm);
        Assert.Equal(terrain.Ground, combined.Ground);
        Assert.Equal(Models.Length, combined.Placements.Count);
        Assert.Equal(far.Meshes.Keys.Order(), combined.Meshes.Keys.Order());
        Assert.All(combined.Placements, p =>
        {
            Assert.Equal(float.PositiveInfinity, LevelSceneUploader.DrawDistanceOf(p)); // only the render distance caps them
            Assert.Equal(0u, p.SelectableId); // the backdrop is never picked
        });
        Assert.Empty(terrain.Placements);
    }

    [GlFact]
    public void FarModelsOfDetailedLevelsAreHidden()
    {
        using var gl = GlHarness.Create(32, 32);
        using var backdrop = LevelSceneUploader.Upload(gl.Renderer, FarModelsScene.Combine(Terrain("Landscape_A_0_1"), Far(Models)));
        SceneNode FarNode(string name) => backdrop.Scene.Nodes.Single(n => n.Tag is ScenePlacement p && p.Actor.Name == name);
        var (outpost, farm, seabed) = (FarNode("A_0_Outpost"), FarNode("B_2_Farm"), FarNode("Seabed_04"));
        var detailed = new PreparedLevelScene([Empty(Maps + "A_0_Outpost")], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, TextureImage>(), [], [], [], TimeSpan.Zero);

        backdrop.HideTerrainOf(detailed);

        Assert.False(outpost.Visible, "the outpost is drawn in detail: its far model goes");
        Assert.True(farm.Visible);
        Assert.True(seabed.Visible, "a model of no level stays");
        Assert.All(backdrop.TerrainNodes, n => Assert.True(n.Visible));

        backdrop.HideTerrainOf(null);

        Assert.True(outpost.Visible && farm.Visible && seabed.Visible);
    }

    [GlFact]
    public void FarModelsStopOnlyAtTheRenderDistance()
    {
        using var gl = GlHarness.Create(32, 32);
        using var backdrop = LevelSceneUploader.Upload(gl.Renderer, FarModelsScene.Combine(Terrain("Landscape_A_0_1"), Far(Models)));
        var camera = new FlyCamera();
        camera.SetClipRange(10f, 3_000_000f);
        camera.Orbit(UeToGl.Point(new FVector(-300_000f, -280_000f, 0f)), 0f, -89f, 1_500_000f); // 15 km up, everything in view
        RenderStats Draw(float objectDistance)
        {
            gl.Renderer.Settings = gl.Renderer.Settings with { ObjectDrawDistance = objectDistance, CullPixelSize = 0f, Shadows = false };
            return gl.Renderer.Render(gl.Target, backdrop.Scene, camera);
        }

        var unlimited = Draw(0f);
        var capped = Draw(100_000f); // a render distance of 1 km: every model is farther

        Assert.Equal(unlimited.Instances - Models.Length, capped.Instances);
        Assert.Equal(unlimited.Culled + Models.Length, capped.Culled);
    }

    [Fact]
    public void LevelsAroundMatchesTheApp()
    {
        var world = World(
            Level("A_0_Outpost", WorldPackageKind.Poi, new MapCell('A', 0), -630_000f, -560_000f, 20_000f),
            Level("Landscape_A_0_1", WorldPackageKind.Landscape, new MapCell('A', 0), -700_000f, -600_000f, 100_000f),
            Level("Landscape_A_0_2", WorldPackageKind.Landscape, new MapCell('A', 0), -600_000f, -600_000f, 100_000f),
            Level("B_2_Farm", WorldPackageKind.Poi, new MapCell('B', 2), 10_000f, 20_000f, 5_000f),
            Level("Landscape_B_2_3", WorldPackageKind.Landscape, new MapCell('B', 2), 0f, 0f, 100_000f),
            Level("TV_Base_A_1", WorldPackageKind.TvBase, new MapCell('A', 1), -400_000f, -560_000f, 10_000f),
            Level("A_0_Outpost_BuiltData", WorldPackageKind.BuiltData, new MapCell('A', 0), -630_000f, -560_000f, 20_000f),
            Level("Island_Wide_Data", WorldPackageKind.Misc, null, 0f, 0f, 1_000_000f),
            Level("A_0_Outpost_Interior", WorldPackageKind.Poi, new MapCell('A', 0), -630_000f, -560_000f, 0f) with { Tile = null });

        foreach (var camera in new[] { new FVector(-622_000f, -556_000f, 3_000f), new FVector(5_000f, 5_000f, 2_000f), new FVector(-500_000f, -560_000f, 0f), new FVector(0f, 0f, 2_000_000f) })
        {
            foreach (var radius in new[] { 10_000f, 60_000f, 100_000f, 400_000f })
            {
                Assert.Equal(ScumStudio.App.ViewModels.MapPageViewModel.LevelsAround(world, camera, radius), FarModelsScene.LevelsAround(world, camera, radius));
            }
        }

        Assert.Equal([Maps + "A_0_Outpost", Maps + "Landscape_A_0_1", Maps + "Landscape_A_0_2"], FarModelsScene.LevelsAround(world, new FVector(-622_000f, -556_000f, 3_000f), 10_000f));
    }

    private static void AssertSamePlace(FTransform expected, FTransform actual)
    {
        foreach (var p in new[] { FVector.Zero, new FVector(1_000f, 0f, 0f), new FVector(0f, 1_000f, 500f) })
        {
            Assert.True(FVector.Distance(expected.TransformPosition(p), actual.TransformPosition(p)) < 0.5f, $"{expected} vs {actual}");
        }
    }

    private static LevelDocument Empty(string path) => LevelDocument.FromData(new LevelData { PackagePath = path, Exports = [], ActorIndices = [] });

    private static MeshData Quad(string name) =>
        MeshData.Create(name, [-1000, -1000, 0, 1000, -1000, 0, -1000, 1000, 0, 1000, 1000, 0], [0u, 1u, 2u, 2u, 1u, 3u],
            [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 0, 1, 0, 0, 1, 1, 1]);

    /// <summary>The island backdrop as the preparer makes it: terrain components and a sea, no placements.</summary>
    private static PreparedLevelScene Terrain(params string[] tiles) =>
        new([], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, TextureImage>(), [],
            tiles.Select(t => new PreparedTerrain("C_0", t, Quad(t))).ToList(), [], TimeSpan.Zero)
        {
            SeaLevelCm = 0f,
        };

    /// <summary>The far models prepared as <see cref="FarModelsScene.Prepare"/> does, with a quad for each mesh.</summary>
    private static PreparedLevelScene Far(IReadOnlyList<FarModelsScene.FarModel> models)
    {
        var document = FarModelsScene.Document(models);
        var placements = LevelScenePreparer.CollectPlacements(document, 0, FarModelsScene.Options);
        var meshes = placements.Select(p => p.MeshPath).Distinct().ToDictionary(p => p, p => new PreparedMeshAsset(p, Quad(p), null), StringComparer.OrdinalIgnoreCase);
        return new PreparedLevelScene([document], placements, meshes, new Dictionary<string, TextureImage>(), [], [], [], TimeSpan.Zero) { RequestedPlacements = placements.Count };
    }

    private static WorldPackage Level(string name, WorldPackageKind kind, MapCell? cell, float x, float y, float half) =>
        new($"SCUM/Content/ConZ_Files/Maps/The_Island/{name}.umap", Maps + name, name, string.Empty, kind, cell, true)
        {
            Tile = new WorldTileInfo((0, 0, 0), new FVector(x - half, y - half, -1_000f), new FVector(x + half, y + half, 5_000f), half > 0f,
                new WorldTileLayer("Layer", 0, (0, 0), 20_000, true), false, "None", [], 0),
        };

    /// <summary>A world index of <paramref name="packages"/> with their tiles (the public builders read them from game files).</summary>
    private static WorldIndex World(params WorldPackage[] packages) =>
        (WorldIndex)Activator.CreateInstance(typeof(WorldIndex), BindingFlags.NonPublic | BindingFlags.Instance, null,
            ["SCUM/Content/ConZ_Files/Maps/The_Island", (IReadOnlyList<WorldPackage>)packages, null], null)!;
}
