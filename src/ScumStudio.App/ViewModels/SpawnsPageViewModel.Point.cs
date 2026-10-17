using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// One spawn place by hand (owner: "FULL control of each spawn place, not only picking a sector"): the place picked on the
/// map or in the list shows its details; it can be dragged, nudged and turned on the map, moved to typed coordinates, put
/// on the ground, made to spawn something else, removed, or shown on the Map page. A drag or a typed move goes onto the
/// terrain when it is known (<see cref="SnapToGround"/>). Every change is one journal entry of the project (undo), written
/// by Export mod like the area's edits.
/// </summary>
public sealed partial class SpawnsPageViewModel
{
    private string? _pointActor;
    private bool _resolvingPoint;
    private Action<SpawnPlace, FVector>? _openOnMap;

    /// <summary>The picked place (its marker id), two-way with the map; null when none is.</summary>
    [ObservableProperty]
    private int? _selectedMarkerId;

    /// <summary>The picked place's details, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPoint))]
    [NotifyCanExecuteChangedFor(nameof(ShowOnMapCommand))]
    private SpawnPointViewModel? _point;

    /// <summary>A moved place goes onto the terrain where it is known (indoor points keep their height).</summary>
    [ObservableProperty]
    private bool _snapToGround = true;

    /// <summary>A place is picked.</summary>
    public bool HasPoint => Point is not null;

    /// <summary>The last move, turn or setting change of the picked place (tests wait for it).</summary>
    internal Task PointEdit { get; private set; } = Task.CompletedTask;

    /// <summary>Wires Show on the Map (the main window switches pages).</summary>
    internal void SetMapOpener(Action<SpawnPlace, FVector> open) => _openOnMap = open;

    partial void OnSelectedMarkerIdChanged(int? value)
    {
        var place = value is { } id && id >= 0 && id < _places.Count ? _places[id] : null;
        _pointActor = place?.ActorName;
        ShowPoint(value, place);
        if (_resolvingPoint)
        {
            return; // the area's rows still carry the old ids: its query picks the row again by name
        }

        // The list follows: the place's row when it is in the area, else none (Remove acts on what the card shows).
        var row = value is { } picked ? AreaPlaces.FirstOrDefault(h => h.Id == picked) : null;
        if (!ReferenceEquals(SelectedPlace, row))
        {
            SelectedPlace = row;
        }
    }

    private void ShowPoint(int? id, SpawnPlace? place)
    {
        var state = _services.Projects.Current?.State;
        Point = id is { } at && place is not null
            ? new SpawnPointViewModel(at, place, state is null ? SpawnPlaceMoves.MapValue(place) : SpawnPlaceMoves.Current(place, state), PointChoices(place), OnPointSettingChanged)
            : null;
    }

    /// <summary>After new markers: the picked place again by its name (its id moves when places are added or removed), with its new values.</summary>
    private void ResolvePoint()
    {
        if (_pointActor is not { } name)
        {
            return;
        }

        var id = MarkerOf(name);
        if (id == SelectedMarkerId)
        {
            ShowPoint(id, id is { } same ? _places[same] : null);
            return;
        }

        _resolvingPoint = true;
        try
        {
            SelectedMarkerId = id;
        }
        finally
        {
            _resolvingPoint = false;
        }
    }

    /// <summary>The marker id of the place named <paramref name="actorName"/> (<c>VehicleSpawn_00100</c>), or null.</summary>
    internal int? MarkerOf(string actorName)
    {
        for (var i = 0; i < _places.Count; i++)
        {
            if (string.Equals(_places[i].ActorName, actorName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Another category: a picked place it does not show is let go.</summary>
    private void DropHiddenPoint()
    {
        var shown = Point?.Place.Kind switch
        {
            null => true,
            SpawnPlaceKind.Vehicle => Tab == SpawnTab.Vehicles,
            SpawnPlaceKind.Animal => Tab == SpawnTab.Animals,
            _ => Tab is SpawnTab.Zombies or SpawnTab.Npcs,
        };
        if (!shown)
        {
            SelectedMarkerId = null;
        }
    }

    /// <summary>What a place of its kind can spawn instead: its vehicle groups, zone settings or biomes.</summary>
    private List<string> PointChoices(SpawnPlace place)
    {
        IEnumerable<string> choices = place.Kind switch
        {
            SpawnPlaceKind.Zone => _zoneEntries.Select(e => Leaf(e.PackagePath)),
            SpawnPlaceKind.Animal => _biomeEntries.Select(e => Leaf(e.PackagePath)),
            SpawnPlaceKind.Vehicle => _stock.Where(p => p.Kind == SpawnPlaceKind.Vehicle).Select(p => p.Group),
            _ => [],
        };
        return choices.Append(place.Group).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The place <paramref name="id"/> as the page has it, or null.</summary>
    private SpawnPlace? PlaceOf(int id) => id >= 0 && id < _places.Count ? _places[id] : null;

    /// <summary>A drag or a nudge on the map ended: the place moves there (onto the ground when it is known), one undo step.</summary>
    [RelayCommand]
    private void MovePlace(SpawnMapMove move) => PointEdit = MovePlaceAsync(move.Id, move.To.X, move.To.Y, null, null);

    /// <summary>Q / E on the map: the place turns, one undo step.</summary>
    [RelayCommand]
    private void TurnPlace(SpawnMapTurn turn)
    {
        if (PlaceOf(turn.Id) is { } place && Project() is { } state && SpawnPlaceMoves.Turn(place, state, turn.Degrees) is { } op)
        {
            Record(op);
        }
    }

    /// <summary>The Delete key on the map: the place is removed from the island's data, one undo step.</summary>
    [RelayCommand]
    private void DeletePlace(int id)
    {
        if (PlaceOf(id) is { } place && Project() is not null
            && Record(new DeleteActorOp(new ActorRef(SpawnPlaces.StaticDataPath, place.ActorName))))
        {
            _services.Notifications.Success(Loc.T("Spawns.Place.Remove"), Loc.F("Spawns.Place.Removed", 1));
        }
    }

    /// <summary>The picked place is removed (the card's Delete).</summary>
    [RelayCommand]
    private void DeletePoint()
    {
        if (Point is { } point)
        {
            DeletePlace(point.Id);
        }
    }

    /// <summary>The picked place goes to the typed coordinates and turn (Enter or Move here), one undo step.</summary>
    [RelayCommand]
    private void ApplyPoint()
    {
        if (Point is not { } point)
        {
            return;
        }

        if (!point.TryRead(out var x, out var y, out var z, out var yaw))
        {
            point.Error = Loc.T("Spawns.Point.Bad");
            return;
        }

        point.Error = string.Empty;
        var was = point.Value;

        // A typed height is used as it is; with the height left alone a place moved elsewhere goes onto the ground.
        float? height = MathF.Abs(z - was.Location.Z) >= 0.5f ? z : null;
        var moved = MathF.Abs(x - was.Location.X) >= 0.5f || MathF.Abs(y - was.Location.Y) >= 0.5f;
        float? turn = MathF.Abs(UeMath.NormalizeAxis(yaw - was.Rotation.Yaw)) >= 0.05f ? yaw : null;
        if (height is null && !moved && turn is null)
        {
            return;
        }

        PointEdit = MovePlaceAsync(point.Id, moved ? x : was.Location.X, moved ? y : was.Location.Y, height, turn, ground: moved);
    }

    /// <summary>The picked place goes onto the terrain under it, one undo step.</summary>
    [RelayCommand]
    private void GroundPoint()
    {
        if (Point is { } point)
        {
            PointEdit = MovePlaceAsync(point.Id, point.Value.Location.X, point.Value.Location.Y, null, null, ground: true, always: true);
        }
    }

    /// <summary>The map zooms in on the picked place (20 m around a point; a zone or hunting area whole).</summary>
    [RelayCommand]
    private void ZoomToPoint()
    {
        if (Point is { } point)
        {
            var at = point.Value.Location;
            var reach = MathF.Max(1000f, MathF.Max(point.Place.SizeX, point.Place.SizeY));
            FocusRequested?.Invoke(this, SpawnArea.Rectangle(at.X - reach, at.Y - reach, at.X + reach, at.Y + reach));
        }
    }

    /// <summary>Switches to the Map page with the camera on the picked place and the place selected there.</summary>
    [RelayCommand(CanExecute = nameof(CanShowOnMap))]
    private void ShowOnMap()
    {
        if (Point is { } point)
        {
            _openOnMap?.Invoke(point.Place, HomeOf(point.Place));
        }
    }

    /// <summary>
    /// Where the Map's document of the places holds <paramref name="place"/>: a stock place (and a copy of one) where the game
    /// has it, a new place where it was made (the Map loads the places over its sector and draws the edits on top).
    /// </summary>
    private FVector HomeOf(SpawnPlace place)
    {
        var actor = new ActorRef(SpawnPlaces.StaticDataPath, place.ActorName);
        var root = _services.Projects.Current?.State is { } state
            ? SpawnPlacesEditor.AddedPlaces(state).Where(a => a.Actor == actor).Select(a => a.Root).FirstOrDefault()
            : null;
        var stock = root switch
        {
            AddSpawnPlaceOp added => (FVector?)added.Transform.Location,
            DuplicateActorOp copy => StockAt(SpawnPlaces.ParseName(copy.Source.Actor)),
            _ => StockAt(SpawnPlaces.ParseName(place.ActorName)),
        };
        return stock ?? place.Transform.Translation;
    }

    private FVector? StockAt((SpawnPlaceKind Kind, int Index)? stock) =>
        stock is { } s && _stock.FirstOrDefault(p => p.Kind == s.Kind && p.Index == s.Index) is { } place ? place.Transform.Translation : null;

    private bool CanShowOnMap() => Point is not null && _openOnMap is not null;

    /// <summary>
    /// Moves the place <paramref name="id"/> to (<paramref name="x"/>, <paramref name="y"/>) cm at <paramref name="z"/> (null:
    /// onto the ground when <paramref name="ground"/> and it is known and <see cref="SnapToGround"/> is on, or
    /// <paramref name="always"/>; else its height) turned to <paramref name="yaw"/> (null keeps it): one undo step.
    /// </summary>
    internal async Task MovePlaceAsync(int id, float x, float y, float? z, float? yaw, bool ground = true, bool always = false)
    {
        if (PlaceOf(id) is not { } place)
        {
            return;
        }

        if (Project() is null)
        {
            PlacesReady = RebuildAsync(); // the map lets go of the preview
            return;
        }

        var height = z;
        if (height is null && ground && (SnapToGround || always) && SpawnPlaceMoves.SnapsToGround(place) && _source is { } source)
        {
            height = SpawnPlaceMoves.GroundHeight(place, await Task.Run(() => source.GroundAt(x, y)).ConfigureAwait(true));
            if (height is null && always)
            {
                _services.Notifications.Warning(Loc.T("Spawns.Point.Ground"), Loc.T("Spawns.Point.NoGround"));
            }
        }

        // The project may have changed meanwhile: the move starts from what it has now (the place deleted: nothing).
        var actor = new ActorRef(SpawnPlaces.StaticDataPath, place.ActorName);
        if (Project() is not { } state || state.IsDeleted(actor))
        {
            return;
        }

        if (SpawnPlaceMoves.Move(place, state, x, y, height, yaw) is { } op)
        {
            _pointActor = place.ActorName;
            Record(op);
        }
        else
        {
            PlacesReady = RebuildAsync(); // nothing changed: the map lets go of the preview
        }
    }

    /// <summary>The card's choice of what the place spawns: recorded at once, one undo step.</summary>
    private void OnPointSettingChanged(SpawnPointViewModel point, string setting)
    {
        if (SpawnChoices.PropertyOf(point.Place.Kind) is not { } property || Project() is not { } state
            || point.Place.Group.Equals(SpawnPlaces.SettingName(setting), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Record(new SetSpawnChoiceOp(point.Actor, property, state.GetSpawnChoice(point.Actor, property), SettingValue(point.Place.Kind, setting)));
    }

    /// <summary>The open project's edits, or null after telling the user there is no project.</summary>
    private EditState? Project()
    {
        if (_services.Projects.Current?.State is { } state)
        {
            return state;
        }

        _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Spawns.NoProject"));
        return null;
    }
}
