using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;
using ScumStudio.Level.World;
using ScumStudio.Modding.Catalog;

namespace ScumStudio.Viewport;

/// <summary>
/// The game's own far view for whole-island mode. The persistent level's <c>DistantLevelManager</c> lists 1,523 merged
/// models (one per place: a building, an outpost, a quarter of a town, see <see cref="FarModels"/>) that the game draws
/// while their level is not loaded. The Map draws them with the island backdrop, each hidden in the frame its level is
/// shown in detail (<see cref="LevelScene.HideTerrainOf"/>), so towns and outposts stand on the horizon instead of bare
/// ground (owner: "render far, smooth, like the game").
/// </summary>
public static class FarModelsScene
{
    /// <summary>Class name of the actors <see cref="Document"/> makes (what <see cref="LevelOf"/> recognises).</summary>
    public const string ActorClass = "DistantLevelModel";

    /// <summary>One far model as the Map draws it.</summary>
    /// <param name="Name">Level name the model stands for (the manager's <c>Name</c>).</param>
    /// <param name="LevelPath">Package path of that level, or null when the island has no such level (sea and seabed proxies): always shown.</param>
    /// <param name="MeshPath">The mesh drawn.</param>
    /// <param name="World">Where it is drawn.</param>
    public sealed record FarModel(string Name, string? LevelPath, string MeshPath, FTransform World);

    /// <summary>
    /// How the far meshes are prepared: 256 px textures (each model has one blurred texture, seen from hundreds of metres),
    /// no terrain, no sea, no spawn models.
    /// </summary>
    public static LevelSceneOptions Options { get; } = new() { TextureSize = 256, IncludeLandscape = false, SpawnModels = false, SeaPlane = false };

    /// <summary>The far models of the game files in <paramref name="catalog"/>, with their levels in <paramref name="world"/>.</summary>
    public static IReadOnlyList<FarModel> Read(AssetCatalog catalog, WorldIndex world)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return From(FarModels.ReadDescriptions(ModdableAssets.ReadPackage(catalog, FarModels.IslandLevel), out _), world);
    }

    /// <summary>
    /// The models of <paramref name="descriptions"/>. Each draws its <c>MeshLODs[0]</c>: that mesh's own cooked LODs take
    /// over with distance; the coarser entries are the game's own distance bands and are not needed. Models the export
    /// collapsed (scale 0.0001: they showed only what was deleted) and models without a mesh are left out.
    /// </summary>
    public static IReadOnlyList<FarModel> From(IEnumerable<FarModels.Description> descriptions, WorldIndex world)
    {
        ArgumentNullException.ThrowIfNull(descriptions);
        ArgumentNullException.ThrowIfNull(world);
        var models = new List<FarModel>();
        foreach (var d in descriptions)
        {
            var s = d.World.Scale3D;
            if (d.Meshes.Count == 0 || MathF.Max(MathF.Abs(s.X), MathF.Max(MathF.Abs(s.Y), MathF.Abs(s.Z))) < 0.001f)
            {
                continue;
            }

            var level = !string.IsNullOrWhiteSpace(d.Name) && world.Find(d.Name) is { IsSublevel: true } p ? p.PackagePath : null;
            models.Add(new FarModel(d.Name, level, d.Meshes[0], d.World));
        }

        return models;
    }

    /// <summary>
    /// The models as a level document (as <c>SpawnPlaces.Over</c> does for spawn places): one actor of class
    /// <see cref="ActorClass"/> per model, named after its level, with the level's package path as its class path and a
    /// static mesh root component at the model's transform.
    /// </summary>
    public static LevelDocument Document(IReadOnlyList<FarModel> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var exports = new List<LevelExportData>(models.Count * 2);
        var actors = new List<int>(models.Count);
        foreach (var model in models)
        {
            var actor = exports.Count;
            exports.Add(new LevelExportData
            {
                Index = actor,
                Name = model.Name.Length > 0 ? model.Name : $"FarModel_{actors.Count}",
                ClassName = ActorClass,
                ClassPath = model.LevelPath ?? string.Empty,
                IsLoaded = true,
                RootComponent = actor + 1,
            });
            exports.Add(new LevelExportData
            {
                Index = actor + 1,
                Name = "StaticMeshComponent0",
                ClassName = "StaticMeshComponent",
                ClassPath = "/Script/Engine.StaticMeshComponent",
                OuterIndex = actor,
                IsLoaded = true,
                IsComponent = true,
                IsSceneComponent = true,
                IsStaticMeshComponent = true,
                StaticMesh = model.MeshPath,
                RelativeLocation = model.World.Translation,
                RelativeRotation = model.World.Rotation.Rotator(),
                RelativeScale3D = model.World.Scale3D,
            });
            actors.Add(actor);
        }

        return LevelDocument.FromData(new LevelData { PackagePath = FarModels.IslandLevel, Exports = exports, ActorIndices = actors });
    }

    /// <summary>The package path of the level a far model actor (<see cref="Document"/>) stands for; null for any other actor or a model of no level.</summary>
    public static string? LevelOf(ActorRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.ClassName == ActorClass && actor.ClassPath.Length > 0 ? actor.ClassPath : null;
    }

    /// <summary>
    /// Loads the meshes and textures of <paramref name="models"/> (any thread). No <see cref="LevelPrepareCache"/>: a cache
    /// empties itself when the options change (the far models' 256 px against the quality's size), so sharing the Map's
    /// would throw away what the detailed levels loaded.
    /// </summary>
    public static PreparedLevelScene Prepare(AssetCatalog catalog, IReadOnlyList<FarModel> models, ILogger? logger = null,
        IProgress<(int Done, int Total, string Item)>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        logger ??= NullLogger.Instance;
        var clock = Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes();
        var far = new LevelScenePreparer(catalog, logger).Prepare([Document(models)], Options, progress, cancellationToken);
        logger.LogInformation(
            "Far models: {Models} models ({Leveled} of a level), {Meshes} meshes ({Triangles:N0} triangles at LOD 0), {Textures} textures, {Missing} missing; prepared in {Ms:0} ms, {Mb:0} MB allocated (process).",
            models.Count, models.Count(m => m.LevelPath is not null), far.Meshes.Count, far.MeshTriangles, far.Textures.Count, far.MissingMeshes.Count,
            clock.Elapsed.TotalMilliseconds, (GC.GetTotalAllocatedBytes() - allocated) / (1024.0 * 1024.0));
        return far;
    }

    /// <summary>
    /// The island backdrop <paramref name="terrain"/> (terrain only, <see cref="LevelScenePreparer.PrepareTerrain"/>) with the
    /// far models of <paramref name="far"/> (<see cref="Prepare"/>). The models are never picked and keep no cull distance
    /// of their own (<see cref="LevelSceneUploader.DrawDistanceOf"/>: unlimited), so only the render distance the backdrop
    /// is drawn with (<c>RenderSettings.ObjectDrawDistance</c>) and the pixel cull stop them.
    /// </summary>
    public static PreparedLevelScene Combine(PreparedLevelScene terrain, PreparedLevelScene far)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(far);
        if (terrain.Documents.Count > 0 || terrain.Placements.Count > 0)
        {
            throw new ArgumentException("A terrain-only scene is expected.", nameof(terrain));
        }

        // A landscape tile's far model is that tile's ground (opaque, one blurred island-wide texture) with its distant
        // forest on it (masked cards). The backdrop draws the tile's ground already: only the forest is kept. Both grounds
        // drawn, each poked through the other, and the terrain's roads showed as pale broken dashes from high up.
        var tiles = terrain.Terrain.Select(t => t.LevelName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var meshes = far.Meshes;
        var placements = new List<ScenePlacement>(far.Placements.Count);
        foreach (var model in far.Placements)
        {
            var p = model;
            if (LevelOf(p.Actor) is { } level && tiles.Contains(level[(level.LastIndexOf('/') + 1)..]) && far.Meshes.TryGetValue(p.MeshPath, out var asset))
            {
                var key = p.MeshPath + GroundlessSuffix;
                if (!meshes.ContainsKey(key))
                {
                    var forest = WithoutGround(asset, key);
                    if (forest is null)
                    {
                        continue; // only ground: the backdrop's terrain is all of it
                    }

                    if (ReferenceEquals(forest, asset))
                    {
                        placements.Add(p with { SelectableId = 0, CullDistance = 0f }); // no ground of its own
                        continue;
                    }

                    var copy = ReferenceEquals(meshes, far.Meshes) ? new Dictionary<string, PreparedMeshAsset>(far.Meshes, StringComparer.OrdinalIgnoreCase) : (Dictionary<string, PreparedMeshAsset>)meshes;
                    copy[key] = forest;
                    meshes = copy;
                }

                p = p with { MeshPath = key };
            }

            placements.Add(p.SelectableId == 0 && p.CullDistance == 0f ? p : p with { SelectableId = 0, CullDistance = 0f });
        }

        return new PreparedLevelScene(far.Documents, placements, meshes, far.Textures, far.MissingMeshes, terrain.Terrain,
            [.. terrain.Warnings, .. far.Warnings], terrain.Elapsed + far.Elapsed)
        {
            Slots = far.Slots,
            IdShift = far.IdShift,
            RequestedPlacements = far.RequestedPlacements,
            Ground = terrain.Ground,
            SeaLevelCm = terrain.SeaLevelCm,
            HeightField = terrain.HeightField,
            LayerCatalog = terrain.LayerCatalog,
            LayerTextures = terrain.LayerTextures,
            MissingLayerTextures = terrain.MissingLayerTextures,
            TerrainBakeTime = terrain.TerrainBakeTime,
        };
    }

    /// <summary>Suffix of the mesh key of a landscape tile's far model without its ground (<see cref="WithoutGround"/>).</summary>
    public const string GroundlessSuffix = "#forest";

    /// <summary>
    /// <paramref name="asset"/> (a landscape tile's far model) without its opaque sections, its ground, under
    /// <paramref name="key"/>: the masked and see-through ones (the distant forest's cards) stay. Null when it is all ground,
    /// <paramref name="asset"/> itself when it has no opaque section.
    /// </summary>
    internal static PreparedMeshAsset? WithoutGround(PreparedMeshAsset asset, string key)
    {
        ArgumentNullException.ThrowIfNull(asset);
        bool Ground(string material) => !asset.MaterialAlphaCutoffs.ContainsKey(material) && !(asset.MaterialTints.TryGetValue(material, out var tint) && tint.W < 1f);
        if (!asset.Lods.Any(l => l.Sections.Any(s => Ground(s.MaterialName))))
        {
            return asset;
        }

        var lods = new List<ScumStudio.Core.Geometry.MeshData>();
        foreach (var lod in asset.Lods)
        {
            var indices = new List<uint>(lod.Indices.Length);
            var sections = new List<ScumStudio.Core.Geometry.MeshSection>();
            foreach (var section in lod.Sections.Where(s => !Ground(s.MaterialName)))
            {
                sections.Add(section with { FirstIndex = indices.Count });
                indices.AddRange(new ArraySegment<uint>(lod.Indices, section.FirstIndex, section.IndexCount));
            }

            if (sections.Count == 0)
            {
                break; // a coarser LOD with only ground: the forest ends with the LOD before it
            }

            lods.Add(lod with { Indices = [.. indices], Sections = [.. sections] });
        }

        return lods.Count == 0 ? null : asset with
        {
            MeshPath = key,
            Mesh = lods[0],
            Lods = lods,
            LodScreenSizes = [.. asset.LodScreenSizes.Take(lods.Count)],
        };
    }

    /// <summary>
    /// The levels whole-island mode loads in detail for a camera at <paramref name="camera"/> (the same set and order as the
    /// Map's <c>MapPageViewModel.LevelsAround</c>, for the command line, which has no app): content sublevels of a cell whose
    /// tile bounds come within <paramref name="radius"/> (cm, 3D), non-landscape first.
    /// </summary>
    public static List<string> LevelsAround(WorldIndex world, FVector camera, float radius)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Packages
            .Where(p => p.IsContentLevel && p.Cell is not null && p.Tile is { BoundsValid: true } t && DistanceSquared(t.BoundsMin, t.BoundsMax, camera) <= radius * radius)
            .OrderBy(p => p.Kind == WorldPackageKind.Landscape)
            .ThenBy(p => p.PackagePath, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.PackagePath)
            .ToList();
    }

    private static float DistanceSquared(FVector min, FVector max, FVector p)
    {
        var dx = MathF.Max(0f, MathF.Max(min.X - p.X, p.X - max.X));
        var dy = MathF.Max(0f, MathF.Max(min.Y - p.Y, p.Y - max.Y));
        var dz = MathF.Max(0f, MathF.Max(min.Z - p.Z, p.Z - max.Z));
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}
