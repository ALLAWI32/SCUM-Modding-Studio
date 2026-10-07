using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Landscape;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.World;

namespace ScumStudio.Level.Export;

/// <summary>
/// The landscape's height at a world X/Y, from the catalog's landscape tiles: each tile is read the first time a place in
/// its bounds is asked for (the export's collision check asks only where rocks were placed).
/// </summary>
public sealed class GroundHeights(AssetCatalog catalog)
{
    private readonly List<LandscapeSurface> _surfaces = [];
    private List<(string Path, WorldTileInfo Tile)>? _tiles;

    /// <summary>The ground height (cm) at world (<paramref name="x"/>, <paramref name="y"/>), or null where there is no terrain.</summary>
    public float? At(float x, float y)
    {
        foreach (var (path, _) in Tiles().Where(t => t.Tile.BoundsMin.X <= x && x <= t.Tile.BoundsMax.X && t.Tile.BoundsMin.Y <= y && y <= t.Tile.BoundsMax.Y).ToList())
        {
            _tiles!.RemoveAll(t => t.Path == path); // read once
            try
            {
                _surfaces.AddRange(LandscapeExtractor.Extract(catalog, path, new LandscapeExtractOptions { ReadGrass = false, ComputeNormals = false, PackedNormals = false })
                    .SelectMany(p => p.Components).Select(c => c.Surface).OfType<LandscapeSurface>());
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // ponytail: an unreadable tile has no ground here; the check then says nothing about it
            }
        }

        foreach (var surface in _surfaces)
        {
            if (surface.TryGetQuadCoordinates(x, y, out var qx, out var qy))
            {
                return surface.SampleHeight(qx, qy);
            }
        }

        return null;
    }

    private List<(string Path, WorldTileInfo Tile)> Tiles() => _tiles ??= WorldIndex.FromCatalog(catalog).Packages
        .Where(p => p.Kind == WorldPackageKind.Landscape && p.IsMap && catalog.TryGetPackageFile(p.PackagePath, out _))
        .Select(p => (p.PackagePath, Tile: catalog.TryGetPackageFile(p.PackagePath, out var file) ? WorldTileInfo.TryRead(CookedPackage.Parse(file.Read(), [], null, p.PackagePath)) : null))
        .Where(t => t.Tile is { BoundsValid: true })
        .Select(t => (t.PackagePath, t.Tile!))
        .ToList();
}
