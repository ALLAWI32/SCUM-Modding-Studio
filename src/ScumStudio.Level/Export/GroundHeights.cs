using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.World;

namespace ScumStudio.Level.Export;

/// <summary>
/// The landscape's height at a world X/Y, from the catalog's landscape tiles: each tile is read the first time a place in
/// its bounds is asked for (the export's collision check asks only where rocks were placed). At most
/// <see cref="MaxTiles"/> tiles stay read, the least recently used going first, so a check over the whole island (the
/// spawn points under the ground) does not hold every tile at once; asking tile by tile reads each one once.
/// </summary>
public sealed class GroundHeights
{
    /// <summary>Tiles kept read by default (a landscape tile is about 1 km square).</summary>
    public const int DefaultMaxTiles = 24;

    private readonly Func<List<(string Path, WorldTileInfo Tile)>> _listTiles;
    private readonly Func<string, IReadOnlyList<LandscapeSurface>> _read;
    private readonly Dictionary<string, LinkedListNode<(string Path, IReadOnlyList<LandscapeSurface> Surfaces)>> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Path, IReadOnlyList<LandscapeSurface> Surfaces)> _recent = new();
    private List<(string Path, WorldTileInfo Tile)>? _tiles;

    /// <summary>Heights from the landscape tiles of <paramref name="catalog"/>.</summary>
    public GroundHeights(AssetCatalog catalog, int maxTiles = DefaultMaxTiles)
        : this(() => LandscapeTiles(catalog), path => ReadTile(catalog, path), maxTiles)
    {
        ArgumentNullException.ThrowIfNull(catalog);
    }

    /// <summary>Heights from <paramref name="tiles"/>, each read by <paramref name="read"/> (tests).</summary>
    internal GroundHeights(Func<List<(string Path, WorldTileInfo Tile)>> tiles, Func<string, IReadOnlyList<LandscapeSurface>> read, int maxTiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTiles, 1);
        _listTiles = tiles;
        _read = read;
        MaxTiles = maxTiles;
    }

    /// <summary>How many tiles stay read at most.</summary>
    public int MaxTiles { get; }

    /// <summary>The tiles read now (tests and diagnostics).</summary>
    public int LoadedTiles => _loaded.Count;

    /// <summary>How many times a tile was read (a tile read again after it was dropped counts again).</summary>
    public int TileReads { get; private set; }

    /// <summary>The ground height (cm) at world (<paramref name="x"/>, <paramref name="y"/>), or null where there is no terrain.</summary>
    public float? At(float x, float y)
    {
        _tiles ??= _listTiles();
        foreach (var (path, _) in _tiles.Where(t => t.Tile.BoundsMin.X <= x && x <= t.Tile.BoundsMax.X && t.Tile.BoundsMin.Y <= y && y <= t.Tile.BoundsMax.Y))
        {
            if (Surfaces(path) is { } surfaces && Sample(surfaces, x, y) is { } z)
            {
                return z;
            }
        }

        return null;
    }

    /// <summary>The catalog's landscape tile levels with their world bounds; a tile whose header cannot be read is left out.</summary>
    internal static List<(string Path, WorldTileInfo Tile)> LandscapeTiles(AssetCatalog catalog) => WorldIndex.FromCatalog(catalog).Packages
        .Where(p => p.Kind == WorldPackageKind.Landscape && p.IsMap)
        .Select(p => (p.PackagePath, Tile: TileOf(catalog, p.PackagePath)))
        .Where(t => t.Tile is { BoundsValid: true })
        .Select(t => (t.PackagePath, t.Tile!))
        .ToList();

    /// <summary>
    /// The tile record of <paramref name="path"/>, or null when it is missing or its header is not one the reader takes (a
    /// mod's or a plugin's tile, audit F190): that tile then has no ground, instead of every check failing.
    /// </summary>
    private static WorldTileInfo? TileOf(AssetCatalog catalog, string path)
    {
        try
        {
            return catalog.TryGetPackageFile(path, out var file) ? WorldTileInfo.TryRead(CookedPackage.Parse(file.Read(), [], null, path)) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return null;
        }
    }

    private static float? Sample(IReadOnlyList<LandscapeSurface> surfaces, float x, float y)
    {
        foreach (var surface in surfaces)
        {
            if (surface.TryGetQuadCoordinates(x, y, out var qx, out var qy))
            {
                return surface.SampleHeight(qx, qy);
            }
        }

        return null;
    }

    private static IReadOnlyList<LandscapeSurface> ReadTile(AssetCatalog catalog, string path)
    {
        try
        {
            return LandscapeExtractor.Extract(catalog, path, new LandscapeExtractOptions { ReadGrass = false, ComputeNormals = false, PackedNormals = false })
                .SelectMany(p => p.Components).Select(c => c.Surface).OfType<LandscapeSurface>().ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return []; // an unreadable tile has no ground here; the check then says nothing about it
        }
    }

    /// <summary>The surfaces of tile <paramref name="path"/>, read now when they are not kept, the least recent tile dropped beyond <see cref="MaxTiles"/>.</summary>
    private IReadOnlyList<LandscapeSurface> Surfaces(string path)
    {
        if (_loaded.TryGetValue(path, out var node))
        {
            _recent.Remove(node);
            _recent.AddFirst(node);
            return node.Value.Surfaces;
        }

        var surfaces = _read(path);
        TileReads++;
        _loaded[path] = _recent.AddFirst((path, surfaces));
        while (_loaded.Count > MaxTiles && _recent.Last is { } last)
        {
            _recent.RemoveLast();
            _loaded.Remove(last.Value.Path);
        }

        return surfaces;
    }
}
