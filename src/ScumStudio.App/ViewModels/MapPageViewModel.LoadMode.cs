using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.World;

namespace ScumStudio.App.ViewModels;

/// <summary>How the Map loads the island (owner: "load only this sector").</summary>
public enum MapLoadMode
{
    /// <summary>The levels around the camera stream in and out as it flies.</summary>
    Island,

    /// <summary>One sector (map cell) loads fully in one phase; nothing else loads until Load more.</summary>
    Sector,
}

/// <summary>Steps done of a level load's prepare phase (meshes and landscape tiles), for a determinate bar.</summary>
public readonly record struct LevelLoadProgress(int Done, int Total)
{
    /// <summary>True once the preparer said how many steps there are.</summary>
    public bool HasTotal => Total > 0;
}

/// <summary>A camera placement in UE space (cm): where the eye is and the point it looks at.</summary>
public readonly record struct CameraShot(FVector Eye, FVector LookAt);

/// <summary>
/// Load mode: when the island can be shown the Map asks how to load it (the whole island, streamed around the camera, or
/// one sector), the header switches between the two, and a loaded sector pauses streaming until Load more.
/// </summary>
public sealed partial class MapPageViewModel
{
    private CancellationTokenSource? _sectorCts;
    private MapCell? _sectorTarget;
    private bool _quietSector;
    private bool _switchReset;
    private MapCell? _cameraCell;

    /// <summary>
    /// A sector came up away from the camera (picked in the list or the tree): the view puts the camera on it. World mode
    /// keeps the camera where it is, and with streaming paused it would face terrain where nothing loads.
    /// </summary>
    public event Action<CameraShot>? FrameSectorRequested;

    /// <summary>How the island loads; null until chosen. Not kept: the owner wants the choice each time the Map opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIslandMode), nameof(IsSectorMode), nameof(NeedsLoadChoice), nameof(NextSector))]
    private MapLoadMode? _loadMode;

    /// <summary>The sector is loaded and streaming is paused: flying inside it loads nothing else.</summary>
    [ObservableProperty]
    private bool _isSectorLocked;

    /// <summary>True during a sector's one loading phase.</summary>
    [ObservableProperty]
    private bool _isSectorLoading;

    /// <summary>
    /// The sector for <see cref="MapLoadMode.Sector"/>. Until one is loaded it follows the user: the cell the camera flies
    /// into, or the one picked in the tree, whichever came last.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextSector))]
    private MapCell? _sectorCell;

    /// <summary>The cells that have levels, for the sector picker.</summary>
    [ObservableProperty]
    private IReadOnlyList<MapCell> _cells = [];

    /// <summary>Steps of the current level load, reported on the UI thread.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SectorProgressText))]
    private LevelLoadProgress _loadProgress;

    /// <summary>The whole island streams around the camera.</summary>
    public bool IsIslandMode => !_switchReset && LoadMode == MapLoadMode.Island;

    /// <summary>Only a sector is loaded.</summary>
    public bool IsSectorMode => !_switchReset && LoadMode == MapLoadMode.Sector;

    /// <summary>The sector "Only this sector" would load, shown beside it while the picker is hidden; null in sector mode.</summary>
    public MapCell? NextSector => LoadMode == MapLoadMode.Sector ? null : SectorCell;

    /// <summary>The world has landscape tiles, so the island (and its sectors) can be shown.</summary>
    public bool HasIsland => World?.Packages.Any(p => p.Kind == WorldPackageKind.Landscape) == true;

    /// <summary>Nothing is shown yet and the island can be: the Map asks how to load it instead of loading it by itself.</summary>
    public bool NeedsLoadChoice => LoadMode is null && !IsWorldMode && WorldBackdrop is null && HasIsland;

    /// <summary>"Sector A_2: 37 / 120" while a sector loads.</summary>
    public string SectorProgressText => _sectorTarget is not { } cell
        ? string.Empty
        : LoadProgress.HasTotal
            ? Localization.Loc.F("Map.LoadMode.Progress", cell, LoadProgress.Done, LoadProgress.Total)
            : Localization.Loc.F("Map.World.LoadingCell", cell);

    partial void OnWorldChanged(WorldIndex? value)
    {
        Cells = value?.Packages.Where(p => p.IsContentLevel && p.Cell is not null).Select(p => p.Cell!.Value).Distinct().Order().ToList() ?? [];
        if (value is null)
        {
            EndSector(LoadMode);
        }

        if (SectorCell is not { } picked || !Cells.Contains(picked))
        {
            PickSectorQuietly(DefaultSector());
        }

        OnPropertyChanged(nameof(HasIsland));
        OnPropertyChanged(nameof(NeedsLoadChoice));
    }

    partial void OnIsWorldModeChanged(bool value)
    {
        if (!value)
        {
            // A single level picked from the tree: the sector is gone, the view frames the level (the island's camera is
            // stale), and the switch would load that level's cell.
            StopFarModels();
            IsSectorLocked = false;
            _sectorTarget = null; // picking that sector again loads it again
            _lastCamera = null;
            if (SelectedPackage?.Cell is { } cell && Cells.Contains(cell))
            {
                PickSectorQuietly(cell);
            }
        }
    }

    partial void OnSectorCellChanged(MapCell? value)
    {
        // Picked in the header while only a sector is loaded: that sector loads instead.
        if (!_quietSector && LoadMode == MapLoadMode.Sector && value is { } cell && cell != _sectorTarget)
        {
            LevelLoadCompletion = LoadSectorAsync(cell);
        }
    }

    /// <summary>The game's paks are connected and nothing is shown: the Map asks how to load (it opened the whole island by itself before).</summary>
    private void OfferLoadChoice()
    {
        EndSector(null);
        PickSectorQuietly(DefaultSector());
        _services.Logger.LogInformation("Map: asking how to load the island (whole island or sector {Cell}).", SectorCell);
    }

    /// <summary>Loads the picked sector (<see cref="SectorCell"/>).</summary>
    [RelayCommand]
    private Task ChooseSectorAsync()
    {
        if ((SectorCell ?? DefaultSector()) is not { } cell || (IsSectorLocked && cell == _sectorTarget && IsWorldMode))
        {
            RefreshSwitch();
            return Task.CompletedTask;
        }

        return LevelLoadCompletion = LoadSectorAsync(cell);
    }

    /// <summary>
    /// Loads every level of <paramref name="cell"/> in one phase over the island's terrain, then pauses streaming: flying
    /// inside the sector loads nothing else until Load more.
    /// </summary>
    public async Task LoadSectorAsync(MapCell cell)
    {
        if (_services.Workspace.Catalog is not { } catalog || World is not { } world || CellPackages(world, cell) is not { Count: > 0 } packages)
        {
            RefreshSwitch();
            return;
        }

        EndSector(MapLoadMode.Sector);
        var cts = _sectorCts = new CancellationTokenSource();
        _sectorTarget = cell;
        SectorCell = cell;
        IsSectorLoading = true;
        IsWorldMode = true; // the island's terrain and the camera stay
        _loadedCell = cell;
        LoadProgress = default;
        OnPropertyChanged(nameof(SectorProgressText));
        WorldStatus = Localization.Loc.F("Map.World.LoadingCell", cell);
        try
        {
            var before = _loadCts;
            await EnsureBackdropAsync(catalog, world).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return; // Cancel, another sector or the whole island: each sets its own line
            }

            if (!ReferenceEquals(_loadCts, before))
            {
                WorldStatus = Localization.Loc.F("Map.LoadMode.Cancelled", cell); // a level from the tree or the AI came first
                return;
            }

            WorldStatus = Localization.Loc.F("Map.World.LoadingCell", cell);
            var loading = LoadLevelsAsync(packages, landscapeStep: 4);
            var load = _loadCts;
            await loading.ConfigureAwait(true);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (!ReferenceEquals(_loadCts, load) || load?.IsCancellationRequested != false)
            {
                WorldStatus = Localization.Loc.F("Map.LoadMode.Cancelled", cell);
                return;
            }

            if (PreparedScene is not { } scene || !packages.All(p => scene.Documents.Any(d => string.Equals(d.PackagePath, p, StringComparison.OrdinalIgnoreCase))))
            {
                WorldStatus = LoadStatus; // why it failed
                return;
            }

            IsSectorLocked = true;
            WorldStatus = Localization.Loc.F("Map.LoadMode.Locked", cell);
            _services.Logger.LogInformation("Sector {Cell}: {Count} level(s) loaded; streaming paused.", cell, packages.Count);
            if (!CameraOver(world, cell) && SectorShot(world, cell) is { } shot)
            {
                FrameSectorRequested?.Invoke(shot);
            }
        }
        finally
        {
            if (ReferenceEquals(_sectorCts, cts))
            {
                IsSectorLoading = false;
            }
        }
    }

    /// <summary>Ends the sector: streaming resumes around the camera at once.</summary>
    [RelayCommand]
    private void LoadMore()
    {
        EndSector(MapLoadMode.Island);
        _holdStreaming = false;
        WorldStatus = Localization.Loc.T("Map.World.Ready");
        if (IsWorldMode && World is { } world && _lastCamera is { } camera)
        {
            FollowCamera(world, camera);
            StreamAround(world, camera);
        }
    }

    /// <summary>Stops the load under way; what was shown stays, and a sector is not locked.</summary>
    [RelayCommand]
    private void CancelLoad()
    {
        var sector = IsSectorLoading ? _sectorTarget : null;
        _sectorCts?.Cancel();
        _loadCts?.Cancel();

        // The streamer must neither take the cancelled set as shown nor start it again half a second later.
        _streamed = PreparedScene?.Documents.Select(d => d.PackagePath).ToList() ?? [];
        _holdStreaming = true;
        _holdCamera = null;
        if (sector is { } cell)
        {
            IsSectorLoading = false;
            LoadStatus = WorldStatus = Localization.Loc.F("Map.LoadMode.Cancelled", cell);
        }
    }

    /// <summary>
    /// A level load's progress, on the UI thread. A report of a load that was replaced, cancelled or has finished is
    /// dropped: such reports came from the thread pool and overwrote the final status.
    /// </summary>
    private void OnLoadProgress(CancellationTokenSource load, (int Done, int Total, string Item) p)
    {
        if (!ReferenceEquals(_loadCts, load) || load.IsCancellationRequested || !IsLoadingLevel)
        {
            return;
        }

        LoadProgress = new LevelLoadProgress(Math.Min(p.Total, Math.Max(LoadProgress.Done, p.Done + 1)), p.Total);
        LoadStatus = Localization.Loc.F("Map.Preparing", p.Done + 1, p.Total, p.Item[(p.Item.LastIndexOf('/') + 1)..]);
    }

    private void EndSector(MapLoadMode? mode)
    {
        _sectorCts?.Cancel();
        _sectorCts = null;
        _cameraCell = null; // the next camera tick in island mode picks the sector it is in
        IsSectorLoading = false;
        IsSectorLocked = false;
        LoadMode = mode;
        RefreshSwitch();
    }

    /// <summary>Not in sector mode: the camera flew into another cell, which becomes the sector the switch loads.</summary>
    private void FollowCamera(WorldIndex world, FVector camera)
    {
        if (CellAt(world, camera.X, camera.Y) is { } here && here != _cameraCell)
        {
            _cameraCell = here;
            if (Cells.Contains(here))
            {
                PickSectorQuietly(here);
            }
        }
    }

    /// <summary>Not in sector mode: a cell picked in the tree is the sector the switch loads, until the camera flies into another.</summary>
    private void FollowPick(MapCell cell)
    {
        if (_lastCamera is { } camera && World is { } world)
        {
            _cameraCell = CellAt(world, camera.X, camera.Y);
        }

        PickSectorQuietly(cell);
    }

    /// <summary>The camera (this run's, else the one the Map reopens with) is over <paramref name="cell"/>.</summary>
    private bool CameraOver(WorldIndex world, MapCell cell) =>
        (_lastCamera ?? (SavedView is { } view ? new FVector(view.X, view.Y, view.Z) : (FVector?)null)) is { } at
        && CellAt(world, at.X, at.Y) == cell;

    /// <summary>A view of the whole of <paramref name="cell"/>: from above a corner, looking at its centre. Null without tile bounds.</summary>
    internal static CameraShot? SectorShot(WorldIndex world, MapCell cell)
    {
        var tiles = world.InCell(cell).Where(p => p.Kind == WorldPackageKind.Landscape && p.Tile is { BoundsValid: true }).Select(p => p.Tile!).ToList();
        if (tiles.Count == 0)
        {
            return null;
        }

        var min = tiles.Select(t => t.BoundsMin).Aggregate(FVector.Min);
        var max = tiles.Select(t => t.BoundsMax).Aggregate(FVector.Max);
        var centre = (min + max) * 0.5f;
        var distance = ((max - min) * 0.5f).Size2D() * 1.5f;
        return new CameraShot(centre + new FVector(-0.54f, -0.54f, 0.64f) * distance, centre); // 40 degrees down
    }

    /// <summary>
    /// Puts the header switch back on the mode. A click on its checked half unchecks that half, and a binding does not
    /// give a control again the value it already gave it: both halves are told "off" once, then their state.
    /// </summary>
    private void RefreshSwitch()
    {
        _switchReset = true;
        OnPropertyChanged(nameof(IsIslandMode));
        OnPropertyChanged(nameof(IsSectorMode));
        _switchReset = false;
        OnPropertyChanged(nameof(IsIslandMode));
        OnPropertyChanged(nameof(IsSectorMode));
    }

    private void PickSectorQuietly(MapCell? cell)
    {
        _quietSector = true;
        try
        {
            SectorCell = cell;
        }
        finally
        {
            _quietSector = false;
        }
    }

    /// <summary>The cell under the camera (this run's, else the one kept from the last), else the first.</summary>
    private MapCell? DefaultSector() =>
        World is not { } world ? null
        : _lastCamera is { } camera && CellAt(world, camera.X, camera.Y) is { } here ? here
        : SavedView is { } view && CellAt(world, view.X, view.Y) is { } saved ? saved
        : Cells.Count > 0 ? Cells[0] : null;
}
