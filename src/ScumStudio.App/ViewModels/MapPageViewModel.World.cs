using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
    private bool _isWorldMode;

    /// <summary>The island's terrain (all tiles, coarse), drawn behind the detailed cell.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasView))]
    private PreparedLevelScene? _worldBackdrop;

    /// <summary>World mode status for the HUD ("Island · A_0 · loading B_0").</summary>
    [ObservableProperty]
    private string _worldStatus = string.Empty;

    /// <summary>Builds the whole-island backdrop and switches to world mode.</summary>
    [RelayCommand]
    public async Task OpenWholeIslandAsync()
    {
        if (_services.Workspace.Catalog is not { } catalog || World is not { } world)
        {
            _services.Notifications.Warning(Localization.Loc.T("Map.NoGameFiles"), Localization.Loc.T("Dump.Status.NoPaks"));
            return;
        }

        IsWorldMode = true;
        var tiles = world.Packages.Where(p => p.IsMap && p.Kind == WorldPackageKind.Landscape).Select(p => p.PackagePath).ToList();
        WorldStatus = Localization.Loc.F("Map.World.Terrain", 0, tiles.Count);
        try
        {
            var progress = new Progress<(int Done, int Total, string Item)>(p => WorldStatus = Localization.Loc.F("Map.World.Terrain", p.Done, p.Total));
            WorldBackdrop = await Task.Run(() => new LevelScenePreparer(catalog, _services.Logger).PrepareTerrain(
                tiles, new LevelSceneOptions { LandscapeStep = 16, TerrainTextureSize = 128 }, progress)).ConfigureAwait(true);
            WorldStatus = Localization.Loc.T("Map.World.Ready");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Island terrain failed: {Message}", ex.Message);
            WorldStatus = Localization.Loc.F("Map.World.Failed", ex.Message);
        }
    }

    /// <summary>
    /// Called about twice a second with the camera position: in whole-island mode the levels around the camera stream
    /// in and out as it moves (see <see cref="StreamAround"/>), whatever sector they belong to.
    /// </summary>
    public void UpdateWorldCamera(FVector cameraUe)
    {
        if (IsWorldMode && World is { } world)
        {
            StreamAround(world, cameraUe);
        }
    }

    /// <summary>The cell whose landscape tiles contain the point (UE cm), or null over the open sea.</summary>
    public static MapCell? CellAt(WorldIndex world, float x, float y) =>
        world.Packages.FirstOrDefault(p => p.Kind == WorldPackageKind.Landscape && p.Cell is not null && p.Tile is { BoundsValid: true } t
            && x >= t.BoundsMin.X && x <= t.BoundsMax.X && y >= t.BoundsMin.Y && y <= t.BoundsMax.Y)?.Cell;
}
