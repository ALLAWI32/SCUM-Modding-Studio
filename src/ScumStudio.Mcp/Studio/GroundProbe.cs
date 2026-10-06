using System.Numerics;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Assets.Meshes;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.World;
using ScumStudio.Viewport;

namespace ScumStudio.Mcp.Studio;

/// <summary>
/// Where things land on the island, for tools that build: the terrain height (landscape tiles read on demand), the tops
/// of the objects already standing there (so a crate lands on a table, a wall piece on the one below, a building on a
/// building), the bounds of a mesh, and how deep the game itself plants a mesh into the ground (learned from the
/// game's own foliage instances of that mesh nearby, so a planted forest looks like the game's).
/// </summary>
internal sealed class GroundProbe
{
    private readonly AssetCatalog _catalog;
    private readonly WorldIndex _world;
    private readonly Func<string, LevelDocument> _level;
    private readonly Dictionary<string, TerrainHeightField?> _tiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<BoundingBox>> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BoundingBox?> _meshBounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, float?> _plantOffsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BoundingBox> _placed = [];

    /// <summary>Creates a probe over <paramref name="world"/> (with tile bounds) reading levels through <paramref name="level"/>.</summary>
    public GroundProbe(AssetCatalog catalog, WorldIndex world, Func<string, LevelDocument> level)
    {
        _catalog = catalog;
        _world = world;
        _level = level;
    }

    /// <summary>Map sublevels (POI, TV base, abandoned city, misc, landscape tile) whose tile bounds contain the point, smallest first.</summary>
    public IReadOnlyList<WorldPackage> LevelsAt(float x, float y) =>
        _world.Packages
            .Where(p => p.IsContentLevel && p.Cell is not null
                        && p.Tile is { BoundsValid: true } t && x >= t.BoundsMin.X && x <= t.BoundsMax.X && y >= t.BoundsMin.Y && y <= t.BoundsMax.Y)
            .OrderBy(p => p.Kind == WorldPackageKind.Landscape)
            .ThenBy(p => (p.Tile!.BoundsMax.X - p.Tile.BoundsMin.X) * (p.Tile.BoundsMax.Y - p.Tile.BoundsMin.Y))
            .ToList();

    /// <summary>Terrain height at the point (UE cm), or null over the sea / outside the island.</summary>
    public float? TerrainHeight(float x, float y)
    {
        foreach (var tile in LevelsAt(x, y).Where(p => p.Kind == WorldPackageKind.Landscape))
        {
            if (Tile(tile.PackagePath)?.SampleHeight(x, y) is { } z)
            {
                return z;
            }
        }

        return null;
    }

    /// <summary>
    /// The highest surface under the point no higher than <paramref name="fromZ"/>: the terrain or the top of an object
    /// standing there (level actors, project additions passed in <paramref name="added"/>, objects placed earlier in this
    /// batch). Null where there is neither.
    /// </summary>
    public (float Z, string What)? SurfaceBelow(float x, float y, float fromZ, IEnumerable<BoundingBox> added)
    {
        (float Z, string What)? best = TerrainHeight(x, y) is { } ground && ground <= fromZ ? (ground, "terrain") : null;
        foreach (var box in LevelsAt(x, y).Where(p => p.Kind != WorldPackageKind.Landscape).SelectMany(p => Boxes(p.PackagePath)).Concat(added).Concat(_placed))
        {
            if (x >= box.Min.X && x <= box.Max.X && y >= box.Min.Y && y <= box.Max.Y && box.Max.Z <= fromZ + 1f && (best is null || box.Max.Z > best.Value.Z))
            {
                best = (box.Max.Z, "object");
            }
        }

        return best;
    }

    /// <summary>Remembers an object placed by the current tool call (later ones can stand on it).</summary>
    public void AddPlaced(BoundingBox box) => _placed.Add(box);

    /// <summary>Forgets the objects placed by the previous tool call.</summary>
    public void ClearPlaced() => _placed.Clear();

    /// <summary>Local bounds (UE cm) of a static or skeletal mesh, or null when it cannot be read.</summary>
    public BoundingBox? MeshBounds(string mesh)
    {
        if (!_meshBounds.TryGetValue(mesh, out var bounds))
        {
            try
            {
                bounds = MeshExtractor.Describe(_catalog.LoadObject(mesh)).Bounds;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                bounds = null;
            }

            _meshBounds[mesh] = bounds;
        }

        return bounds;
    }

    /// <summary>World bounds of <paramref name="mesh"/> placed at <paramref name="world"/>.</summary>
    public BoundingBox? WorldBounds(string mesh, FTransform world)
    {
        if (MeshBounds(mesh) is not { IsEmpty: false } local)
        {
            return null;
        }

        var box = BoundingBox.Empty;
        for (var i = 0; i < 8; i++)
        {
            var corner = new FVector((i & 1) == 0 ? local.Min.X : local.Max.X, (i & 2) == 0 ? local.Min.Y : local.Max.Y, (i & 4) == 0 ? local.Min.Z : local.Max.Z);
            var p = world.TransformPosition(corner);
            box = box.Include(new Vector3(p.X, p.Y, p.Z));
        }

        return box;
    }

    /// <summary>
    /// How high above the terrain the game puts this mesh's pivot per unit of scale (negative = planted into the ground),
    /// measured on its own instances in the landscape tiles around the point; null when the game has none nearby.
    /// </summary>
    public float? PlantOffset(string mesh, float x, float y)
    {
        if (_plantOffsets.TryGetValue(mesh, out var known))
        {
            return known;
        }

        // ponytail: samples the tiles within ~1.5 km; a mesh the game only uses elsewhere gets no learned offset.
        var samples = new List<float>();
        var tiles = _world.Packages.Where(p => p.Kind == WorldPackageKind.Landscape && p.Tile is { BoundsValid: true } t
                                               && t.BoundsMin.X - 150_000f <= x && t.BoundsMax.X + 150_000f >= x && t.BoundsMin.Y - 150_000f <= y && t.BoundsMax.Y + 150_000f >= y);
        foreach (var tile in tiles)
        {
            foreach (var instance in _level(tile.PackagePath).Actors.SelectMany(a => a.InstanceTransforms))
            {
                if (instance.StaticMeshPath is { } path && Level.Editing.EditOpFactory.SameObject(path, mesh) && samples.Count < 400)
                {
                    var at = instance.WorldTransform.Translation;
                    if (TerrainHeight(at.X, at.Y) is { } ground)
                    {
                        samples.Add((at.Z - ground) / MathF.Max(0.05f, instance.WorldTransform.Scale3D.Z));
                    }
                }
            }

            if (samples.Count >= 40)
            {
                break;
            }
        }

        float? offset = samples.Count >= 3 ? samples.Order().ElementAt(samples.Count / 2) : null;
        _plantOffsets[mesh] = offset;
        return offset;
    }

    private TerrainHeightField? Tile(string packagePath)
    {
        if (!_tiles.TryGetValue(packagePath, out var field))
        {
            try
            {
                var surfaces = LandscapeExtractor.Extract(_catalog, packagePath, new LandscapeExtractOptions { Step = 1, ReadLayers = false, ReadGrass = false })
                    .SelectMany(p => p.Components)
                    .Where(c => c.Surface is not null)
                    .Select(c => (c.Surface!, (LandscapeComponentLayers?)null))
                    .ToList();
                field = surfaces.Count > 0 ? new TerrainHeightField(surfaces) : null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                field = null;
            }

            _tiles[packagePath] = field;
        }

        return field;
    }

    private List<BoundingBox> Boxes(string packagePath)
    {
        if (!_boxes.TryGetValue(packagePath, out var boxes))
        {
            boxes = [];
            foreach (var component in _level(packagePath).Actors.SelectMany(a => a.Components))
            {
                if (component.StaticMeshPath is { } mesh && !component.IsInstanced && component.IsVisible
                    && WorldBounds(mesh, component.WorldTransform) is { } box && box.Size.Z > 1f)
                {
                    boxes.Add(box);
                }
            }

            _boxes[packagePath] = boxes;
        }

        return boxes;
    }
}
