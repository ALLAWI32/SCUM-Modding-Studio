using System.Numerics;
using ScumStudio.Assets.Textures;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Viewport;

namespace ScumStudio.Tests.Viewport;

/// <summary>
/// r37 verifier's leftovers on the Map at Game-like: a white diamond high inside the B_4 hangar (the Map's pin for an
/// invisible zone volume), white walls of engine cubes the game hides, and the far view's roads as pale broken dashes (two
/// grounds drawn over each other).
/// </summary>
public sealed class MapLeftoversTests
{
    private const string Maps = "/Game/ConZ_Files/Maps/The_Island/";

    [Fact]
    public void AZoneVolumeIsNoPin()
    {
        // SCUM's VolumeZone (B_4 hangar, "NoSea" zone): a brush and nothing else; its class name does not end in "Volume".
        var brush = new ComponentRecord(6, "BrushComponent0", "BrushComponent", true, null, FTransform.Identity, new FTransform(new FVector(586461f, -223808f, 847f)), null, []);
        var zone = new ActorRecord(5, "VolumeZone13", "/Script/ConZ.VolumeZone", brush.ExportIndex, [brush], brush.WorldTransform, ActorKind.Other, null, []) { ClassName = "VolumeZone" };

        Assert.False(SpawnMarkers.IsPinOnly(zone, out var effect));
        Assert.False(effect);
        Assert.DoesNotContain(LevelScenePreparer.PinPlacements(zone, 1u, 0), p => SpawnMarkers.IsMarker(p.MeshPath));
    }

    [Fact]
    public void AnActorHiddenInGameDrawsNothing()
    {
        // B_4_Outpost_Exterior's Cube4: an engine cube 27 m long, bHidden on the actor (not on its mesh component).
        var document = Document(("Cube4", true), ("SM_Barrel", false));

        var placements = LevelScenePreparer.CollectPlacements(document, 0);

        Assert.True(document.Actors.Single(a => a.Name == "Cube4").HiddenInGame);
        Assert.Equal(["SM_Barrel/StaticMeshComponent0"], placements.Select(p => p.Name));
        Assert.False(SpawnMarkers.IsPinOnly(document.Actors.Single(a => a.Name == "Cube4"), out _)); // no white diamond in its place either
    }

    [Fact]
    public void ALandscapeTilesFarModelKeepsOnlyItsForest()
    {
        // The game's far model of a landscape tile: its ground (opaque) and its distant forest (masked cards). The backdrop draws
        // the tile's ground itself; both drawn, the terrain's roads poked through the far ground as pale dashes.
        const string tile = "Landscape_B_4_2b", ground = "/Game/M/MI_DistantLandscape.MI_DistantLandscape", forest = "/Game/M/MI_Foliage_Distant.MI_Foliage_Distant";
        var models = new[]
        {
            new FarModelsScene.FarModel(tile, Maps + tile, "/Game/D/SM_Distant_B_4_2b_LOD0.SM_Distant_B_4_2b_LOD0", FTransform.Identity),
            new FarModelsScene.FarModel("B_4_Outpost", Maps + "B_4_Outpost", "/Game/D/SM_B_4_Outpost_LOD0.SM_B_4_Outpost_LOD0", FTransform.Identity),
        };
        var document = FarModelsScene.Document(models);
        var placements = LevelScenePreparer.CollectPlacements(document, 0, FarModelsScene.Options);
        var meshes = models.ToDictionary(m => m.MeshPath, m => new PreparedMeshAsset(m.MeshPath, TwoSections(m.MeshPath, ground, forest), null)
        {
            MaterialAlphaCutoffs = new Dictionary<string, float> { [forest] = 0.33f },
        }, StringComparer.OrdinalIgnoreCase);
        var far = new PreparedLevelScene([document], placements, meshes, new Dictionary<string, TextureImage>(), [], [], [], TimeSpan.Zero);
        var terrain = new PreparedLevelScene([], [], new Dictionary<string, PreparedMeshAsset>(), new Dictionary<string, TextureImage>(), [],
            [new PreparedTerrain("LandscapeComponent_463", tile, TwoSections("t", ground, forest))], [], TimeSpan.Zero);

        var combined = FarModelsScene.Combine(terrain, far);

        var tileModel = combined.Placements.Single(p => p.Actor.Name == tile);
        Assert.EndsWith(FarModelsScene.GroundlessSuffix, tileModel.MeshPath);
        var kept = combined.Meshes[tileModel.MeshPath].Mesh;
        var section = Assert.Single(kept.Sections);
        Assert.Equal((forest, 0, 3), (section.MaterialName, section.FirstIndex, section.IndexCount));
        Assert.Equal([4u, 5u, 6u], kept.Indices); // the forest's triangle, not the ground's
        Assert.Equal(2, combined.Meshes[models[1].MeshPath].Mesh.Sections.Length); // an outpost's far model keeps everything
        Assert.Equal(models[1].MeshPath, combined.Placements.Single(p => p.Actor.Name == "B_4_Outpost").MeshPath);
    }

    [Fact]
    public void AFarModelThatIsAllGroundIsLeftToTheTerrain()
    {
        var asset = new PreparedMeshAsset("m", TwoSections("m", "/Game/M/A.A", "/Game/M/B.B"), null);

        Assert.Null(FarModelsScene.WithoutGround(asset, "m#forest"));
        var masked = asset with { MaterialAlphaCutoffs = new Dictionary<string, float> { ["/Game/M/A.A"] = 0.3f, ["/Game/M/B.B"] = 0.3f } };
        Assert.Same(masked, FarModelsScene.WithoutGround(masked, "m#forest"));
    }

    // Two triangles, one per material: indices 0-2 the first, 3-5 the second.
    private static MeshData TwoSections(string name, string first, string second) =>
        MeshData.Create(name, [0, 0, 0, 100, 0, 0, 0, 100, 0, 1000, 0, 0, 1100, 0, 0, 1000, 100, 0, 1100, 100, 0], [0u, 1u, 2u, 4u, 5u, 6u],
            sections: [new MeshSection(first, 0, 3), new MeshSection(second, 3, 3)]);

    private static LevelDocument Document(params (string Name, bool Hidden)[] actors)
    {
        var exports = new List<LevelExportData>();
        var roots = new List<int>();
        foreach (var (name, hidden) in actors)
        {
            var actor = exports.Count;
            exports.Add(new LevelExportData
            {
                Index = actor, Name = name, ClassName = "StaticMeshActor", ClassPath = "/Script/Engine.StaticMeshActor", IsLoaded = true,
                RootComponent = actor + 1, HiddenInGame = hidden,
            });
            exports.Add(new LevelExportData
            {
                Index = actor + 1, Name = "StaticMeshComponent0", ClassName = "StaticMeshComponent", ClassPath = "/Script/Engine.StaticMeshComponent",
                OuterIndex = actor, IsLoaded = true, IsComponent = true, IsSceneComponent = true, IsStaticMeshComponent = true,
                StaticMesh = "/Engine/BasicShapes/Cube.Cube", RelativeLocation = new FVector(569425.9f, -227527.6f, 421.2f), RelativeScale3D = new FVector(0.462f, 27.236f, 5.243f),
            });
            roots.Add(actor);
        }

        return LevelDocument.FromData(new LevelData { PackagePath = Maps + "B_4_Outpost_Exterior", Exports = exports, ActorIndices = roots });
    }
}
