using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using ScumStudio.Level.World;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// "Reset a sector to stock" in the History panel (Discord, pheidian: "how do I revert one area back to default (one
/// sector)?"): pick a sector, see what the project changed there by kind, and take it all back as ONE edit that Undo puts
/// back (<see cref="EditState.PlanSectorReset"/>). The map has no area selection, so a whole sector is the smallest area.
/// </summary>
public sealed partial class MapPageViewModel
{
    private SectorResetPlan? _resetPlan;

    /// <summary>The 25 sectors of the island, for the picker.</summary>
    public IReadOnlyList<MapCell> ResetCells { get; } = MapCell.All.ToList();

    /// <summary>The sector to reset.</summary>
    [ObservableProperty]
    private MapCell? _resetCell;

    /// <summary>What the reset takes back, one line per kind ("12 deleted objects come back").</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _resetLines = [];

    /// <summary>What it leaves (copies' sources, island-wide changes with no known place), or that nothing is changed there.</summary>
    [ObservableProperty]
    private string _resetNote = string.Empty;

    /// <summary>True when the sector holds changes to take back.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetSectorCommand))]
    private bool _canResetSector;

    /// <summary>"Reset D_2 to stock".</summary>
    [ObservableProperty]
    private string _resetButtonText = Loc.T("Map.Reset.Pick");

    /// <summary>The panel opened: the sector the map is on (or the one picked last), and what a reset of it takes back.</summary>
    public void PrepareSectorReset()
    {
        CommitShape(); // a waiting scale or bend is a change of the sector too
        ResetCell ??= SectorCell ?? _cameraCell;
        RefreshSectorReset();
    }

    partial void OnResetCellChanged(MapCell? value) => RefreshSectorReset();

    /// <summary>Plans the reset of <see cref="ResetCell"/> again (after a pick, an edit or a language change).</summary>
    public void RefreshSectorReset()
    {
        var project = _services.Projects.Current;
        _resetPlan = null;
        if (project is null || ResetCell is not { } cell)
        {
            ResetLines = [];
            ResetNote = project is null ? Loc.T("Map.Reset.NoProject") : string.Empty;
            ResetButtonText = Loc.T("Map.Reset.Pick");
            CanResetSector = false;
            return;
        }

        var plan = _resetPlan = project.State.PlanSectorReset(cell, LocateForReset, Loc.F("Map.Reset.HistoryTitle", cell));
        ResetLines = plan.Counts.OrderBy(c => c.Key).Select(c => Loc.F(LineKey(c.Key), c.Value)).ToList();
        var notes = new List<string>();
        if (plan.Op is null)
        {
            notes.Add(Loc.F("Map.Reset.Nothing", cell));
        }

        if (plan.KeptCopySources > 0)
        {
            notes.Add(Loc.F("Map.Reset.KeptSources", plan.KeptCopySources));
        }

        if (plan.Unplaced > 0)
        {
            notes.Add(Loc.F("Map.Reset.Unplaced", plan.Unplaced));
        }

        ResetNote = string.Join(" ", notes);
        ResetButtonText = Loc.F("Map.Reset.Run", cell);
        CanResetSector = plan.Op is not null;
    }

    /// <summary>Takes back every change of the sector as one edit (Undo puts it all back).</summary>
    [RelayCommand(CanExecute = nameof(CanResetSector))]
    private void ResetSector()
    {
        CommitShape();
        RefreshSectorReset(); // the plan is made against the state as it is now
        if (_resetPlan is not { Op: { } op } plan)
        {
            return;
        }

        try
        {
            _services.Projects.Apply(op);
            _services.Logger.LogInformation("Sector {Cell} reset to stock: {Count} change(s) taken back in one edit.", plan.Cell, plan.Total);
            _services.Notifications.Success(Loc.F("Map.Reset.Done", plan.Cell), Loc.F("Map.Reset.DoneDetail", plan.Total));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _services.Notifications.Error(Loc.F("Map.Reset.Failed", plan.Cell), ex.Message);
        }

        RefreshSectorReset();
    }

    /// <summary>Where a game object stands, when the map knows it: a loaded object, or one of the island's spawn places.</summary>
    private (float X, float Y)? LocateForReset(ActorRef actor)
    {
        if (PristineOf(actor) is { } item)
        {
            var at = item.Actor.WorldTransform.Translation;
            return (at.X, at.Y);
        }

        if (SpawnPlaces.IsStaticData(actor.Level) && SpawnPlaces.ParseName(actor.Actor) is { } id && SpawnPlacesForReset() is { } places
            && places.FirstOrDefault(p => p.Kind == id.Kind && p.Index == id.Index) is { } place)
        {
            var at = place.Transform.Translation;
            return (at.X, at.Y);
        }

        return null;
    }

    // The island's spawn places, read once per game folder (the map reads them the same way when it shows them).
    private IReadOnlyList<SpawnPlace>? SpawnPlacesForReset()
    {
        if (_services.Workspace.Catalog is not { } catalog)
        {
            return _spawnPlaces;
        }

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

        return _spawnPlaces;
    }

    private static string LineKey(SectorResetKind kind) => kind switch
    {
        SectorResetKind.RestoredObjects => "Map.Reset.Line.RestoredObjects",
        SectorResetKind.RestoredInstances => "Map.Reset.Line.RestoredInstances",
        SectorResetKind.RestoredParts => "Map.Reset.Line.RestoredParts",
        SectorResetKind.MovedBack => "Map.Reset.Line.MovedBack",
        SectorResetKind.RemovedObjects => "Map.Reset.Line.RemovedObjects",
        SectorResetKind.RemovedInstances => "Map.Reset.Line.RemovedInstances",
        SectorResetKind.Shapes => "Map.Reset.Line.Shapes",
        SectorResetKind.Meshes => "Map.Reset.Line.Meshes",
        SectorResetKind.Loot => "Map.Reset.Line.Loot",
        SectorResetKind.Grass => "Map.Reset.Line.Grass",
        SectorResetKind.Spawners => "Map.Reset.Line.Spawners",
        SectorResetKind.Holes => "Map.Reset.Line.Holes",
        _ => "Map.Reset.Line.SubZones",
    };
}
