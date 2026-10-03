using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using ScumStudio.Level.World;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The island's spawn places on the map (owner: "show me the real places where cars, planes and zombies spawn, let me
/// move, delete and copy them"): SCUM keeps them in <c>The_Island_LevelStaticData</c>, read once per game folder. The
/// places over the loaded area come in as one more level (<see cref="SpawnPlaces.StaticDataPath"/>), so they pick, move,
/// copy, delete and undo like any object; Export mod writes them back (<see cref="SpawnPlacesEditor"/>).
/// </summary>
public sealed partial class MapPageViewModel
{
    private IReadOnlyList<SpawnPlace>? _spawnPlaces;
    private AssetCatalog? _spawnPlacesCatalog;

    /// <summary>The spawn places over the area of <paramref name="packagePaths"/>, as a level; null when there are none.</summary>
    private LevelDocument? SpawnPlacesOver(AssetCatalog catalog, WorldIndex? world, IReadOnlyList<string> packagePaths, IReadOnlyList<LevelDocument> documents)
    {
        if (!ReferenceEquals(_spawnPlacesCatalog, catalog))
        {
            _spawnPlacesCatalog = catalog;
            try
            {
                _spawnPlaces = SpawnPlaces.ReadFrom(catalog);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or InvalidOperationException)
            {
                _services.Logger.LogWarning("Spawn places could not be read: {Message}", ex.Message);
                _spawnPlaces = [];
            }
        }

        if (_spawnPlaces is not { Count: > 0 } places)
        {
            return null;
        }

        // The area: the loaded landscape tiles (whole island mode), else around the loaded levels' actors (one level open).
        var areas = world?.Packages
            .Where(p => p.Kind == WorldPackageKind.Landscape && p.Tile is { BoundsValid: true } && packagePaths.Contains(p.PackagePath, StringComparer.OrdinalIgnoreCase))
            .Select(p => (p.Tile!.BoundsMin.X, p.Tile.BoundsMin.Y, p.Tile.BoundsMax.X, p.Tile.BoundsMax.Y))
            .ToList() ?? [];
        if (areas.Count == 0 && SpawnPlaces.AreaAround(documents, 5_000f) is { } around)
        {
            areas.Add(around);
        }

        return areas.Count == 0 ? null : SpawnPlaces.Over(places, areas);
    }
}
