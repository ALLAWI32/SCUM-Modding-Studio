using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.World;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Whole-island mode: every landscape tile as a coarse backdrop, and the cell under the camera loaded in full detail
/// (edits work as in a normal cell load). Flying into another cell loads that one in the background and swaps it in.
/// </summary>
public sealed partial class MapPageViewModel
{
    /// <summary>True while the whole island is shown (the viewport keeps the camera when cells swap).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsLoadChoice))]
    private bool _isWorldMode;

    /// <summary>The island's terrain (all tiles, coarse), drawn behind the detailed cell.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasView), nameof(NeedsLoadChoice))]
    private PreparedLevelScene? _worldBackdrop;

    /// <summary>World mode status for the HUD ("Island · A_0 · loading B_0").</summary>
    [ObservableProperty]
    private string _worldStatus = string.Empty;

    private Task<bool>? _backdropBuild;
    private AssetCatalog? _backdropCatalog;
    private PreparedLevelScene? _islandTerrain; // the backdrop without the far models (they are added to it)
    private FVector? _lastCamera;

    /// <summary>Builds the whole-island backdrop and switches to world mode, streaming the levels around the camera.</summary>
    [RelayCommand]
    public async Task OpenWholeIslandAsync()
    {
        if (_services.Workspace.Catalog is not { } catalog || World is not { } world)
        {
            _services.Notifications.Warning(Localization.Loc.T("Map.NoGameFiles"), Localization.Loc.T("Dump.Status.NoPaks"));
            RefreshSwitch();
            return;
        }

        EndSector(MapLoadMode.Island);
        Isolate = false; // the whole island again: its terrain shows and streams
        IsWorldMode = true;
        _holdStreaming = false; // the island streams around the camera at once
        if (await EnsureBackdropAsync(catalog, world).ConfigureAwait(true) && LoadMode == MapLoadMode.Island)
        {
            WorldStatus = Localization.Loc.T("Map.World.Ready");
            ShowFarModels(catalog, world, _islandTerrain); // the game's far view of every place, added once it is prepared
        }
    }

    /// <summary>
    /// The island's terrain behind whatever is loaded: built once per catalog (a build under way is shared, a failed one
    /// is tried again). False when it failed.
    /// </summary>
    private Task<bool> EnsureBackdropAsync(AssetCatalog catalog, WorldIndex world)
    {
        if (ReferenceEquals(_backdropCatalog, catalog) && _backdropBuild is { } build && (!build.IsCompleted || build.Result))
        {
            return build;
        }

        _backdropCatalog = catalog;
        return _backdropBuild = BuildBackdropAsync(catalog, world);
    }

    private async Task<bool> BuildBackdropAsync(AssetCatalog catalog, WorldIndex world)
    {
        var tiles = world.Packages.Where(p => p.IsMap && p.Kind == WorldPackageKind.Landscape).Select(p => p.PackagePath).ToList();
        WorldStatus = Localization.Loc.F("Map.World.Terrain", 0, tiles.Count);
        try
        {
            // A cancelled sector keeps its line: the terrain goes on building in the background for the next load.
            var progress = new Progress<(int Done, int Total, string Item)>(p =>
            {
                if (LoadMode != MapLoadMode.Sector || IsSectorLoading)
                {
                    WorldStatus = Localization.Loc.F("Map.World.Terrain", p.Done, p.Total);
                }
            });
            var options = new LevelSceneOptions { LandscapeStep = 16, TerrainTextureSize = 128 };
            WorldBackdrop = await Task.Run(() =>
            {
                // Built once per game version and kept: reading 400 landscape tiles took 15-17 s at every start.
                var cache = TerrainCache.PathFor(Path.Combine(_services.DataDirectory, "cache"), catalog, tiles, options);
                if (TerrainCache.TryLoad(cache) is { } kept)
                {
                    _services.Logger.LogInformation("Island terrain read from the cache in {Ms:0} ms.", kept.Elapsed.TotalMilliseconds);
                    return kept;
                }

                var built = new LevelScenePreparer(catalog, _services.Logger).PrepareTerrain(tiles, options, progress);
                try
                {
                    TerrainCache.Save(cache, built);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _services.Logger.LogWarning("Island terrain not cached: {Message}", ex.Message);
                }

                return built;
            }).ConfigureAwait(true);
            _islandTerrain = WorldBackdrop;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Island terrain failed: {Message}", ex.Message);
            WorldStatus = Localization.Loc.F("Map.World.Failed", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Called about twice a second with the camera position: in whole-island mode the levels around the camera stream
    /// in and out as it moves (see <see cref="StreamAround"/>), whatever sector they belong to. With only a sector
    /// loaded (<see cref="MapLoadMode.Sector"/>) nothing else loads until Load more.
    /// </summary>
    public void UpdateWorldCamera(FVector cameraUe)
    {
        if (!IsWorldMode || World is not { } world || Isolate)
        {
            return; // Isolate: only what is loaded shows, nothing streams in
        }

        _lastCamera = cameraUe;
        if (LoadMode == MapLoadMode.Sector)
        {
            return;
        }

        FollowCamera(world, cameraUe);
        if (_holdStreaming)
        {
            _holdCamera ??= cameraUe;
            if (FVector.Distance(_holdCamera.Value, cameraUe) < Quality.StreamRadiusCm * 0.25f)
            {
                return; // the levels shown on purpose stay while the camera is where it was
            }

            _holdStreaming = false;
        }

        StreamAround(world, cameraUe);
    }

    /// <summary>The game's spawn places are shown as coloured pins (kept for the next start).</summary>
    [ObservableProperty]
    private bool _showSpawns;

    partial void OnShowSpawnsChanged(bool value)
    {
        _services.UiState.Update(u => u with { ShowSpawnPoints = value });
        RefreshHiddenIds();
    }

    /// <summary>Where the camera was when the map was last used (owner: reopen there, not far out over the island), or null.</summary>
    public MapView? SavedView => _services.UiState.Current.MapView;

    /// <summary>Keeps the camera for the next start (the view calls this every few seconds while it moves).</summary>
    public void RememberView(FVector at, float yaw, float pitch)
    {
        var view = new MapView(MathF.Round(at.X), MathF.Round(at.Y), MathF.Round(at.Z), MathF.Round(yaw, 1), MathF.Round(pitch, 1));
        if (view != SavedView)
        {
            _services.UiState.Update(u => u with { MapView = view });
        }
    }

    /// <summary>The cell whose landscape tiles contain the point (UE cm), or null over the open sea.</summary>
    public static MapCell? CellAt(WorldIndex world, float x, float y) =>
        world.Packages.FirstOrDefault(p => p.Kind == WorldPackageKind.Landscape && p.Cell is not null && p.Tile is { BoundsValid: true } t
            && x >= t.BoundsMin.X && x <= t.BoundsMax.X && y >= t.BoundsMin.Y && y <= t.BoundsMax.Y)?.Cell;
}
