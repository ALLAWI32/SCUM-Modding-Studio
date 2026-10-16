using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.World;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// Whole-island mode's far view: the game's own far models of every place (<see cref="FarModelsScene"/>) drawn with the
/// island terrain, each giving way to its level when that level streams in.
/// </summary>
public sealed partial class MapPageViewModel
{
    // Prepared once per session and game files (some seconds of mesh and texture loading), then added to every island terrain.
    private (AssetCatalog Catalog, PreparedLevelScene Scene)? _farModels;
    private CancellationTokenSource? _farCancel;

    /// <summary>
    /// Adds the far models to <paramref name="terrain"/>, the island backdrop just shown: at once when they are prepared,
    /// else once a worker has prepared them (the terrain stays on screen meanwhile; a failure is logged, the map goes on).
    /// </summary>
    private void ShowFarModels(AssetCatalog catalog, WorldIndex world, PreparedLevelScene? terrain)
    {
        _farCancel?.Cancel();
        _farCancel = null;
        if (terrain is null)
        {
            return;
        }

        if (_farModels is { } kept && ReferenceEquals(kept.Catalog, catalog))
        {
            if (ReferenceEquals(WorldBackdrop, terrain))
            {
                WorldBackdrop = FarModelsScene.Combine(terrain, kept.Scene); // else they are drawn already
            }

            return;
        }

        _farModels = null; // other game files: the old models go before the new ones are loaded
        var cancel = _farCancel = new CancellationTokenSource();
        FarModelsLoading = LoadFarModelsAsync(catalog, world, terrain, cancel.Token);
    }

    /// <summary>The far-model load under way or last finished, or null (tests and benchmarks wait for it).</summary>
    internal Task? FarModelsLoading { get; private set; }

    private async Task LoadFarModelsAsync(AssetCatalog catalog, WorldIndex world, PreparedLevelScene terrain, CancellationToken ct)
    {
        try
        {
            var far = await Task.Run(() => FarModelsScene.Prepare(catalog, FarModelsScene.Read(catalog, world), _services.Logger, null, ct), ct).ConfigureAwait(true);
            _farModels = (catalog, far);
            if (!ct.IsCancellationRequested && IsWorldMode && ReferenceEquals(WorldBackdrop, terrain))
            {
                WorldBackdrop = FarModelsScene.Combine(terrain, far);
            }
        }
        catch (OperationCanceledException)
        {
            // World mode closed or the island was opened again.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _services.Logger.LogWarning("Far models not shown: {Message}", ex.Message);
        }
    }

    /// <summary>World mode closed: a far-model load under way stops (the models already prepared are kept).</summary>
    private void StopFarModels()
    {
        _farCancel?.Cancel();
        _farCancel = null;
    }
}
