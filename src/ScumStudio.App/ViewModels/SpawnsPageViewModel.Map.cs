using System.Diagnostics;
using System.Numerics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Controls;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The Spawns map (owner: "a 2D map of the island ... where I select an area: a whole sector, or any area I draw"): the
/// island picture (<see cref="IslandMapPicture"/>), the category's spawn places as markers, the area picked on it and the
/// places inside (found on a worker in a <see cref="SpawnPlaceIndex"/>), new and removed places, what the checked places
/// spawn, a zone's own settings and the under-ground check (seviscache: "spawners below the terrain"). Every edit is one
/// journal entry of the project, exported to client and server with Export mod.
/// </summary>
public sealed partial class SpawnsPageViewModel
{
    private readonly Dictionary<string, string> _zoneCopies = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SpawnEntry> _stockZoneEntries = [];
    private IslandMapPicture? _pictures;
    private IReadOnlyList<SpawnPlace> _stock = [];
    private IReadOnlyList<SpawnPlace> _places = [];
    private SpawnPlaceIndex? _index;
    private Dictionary<SpawnPlace, int> _ids = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyList<SpawnAreaPlaceItem> _areaHits = [];
    private CancellationTokenSource? _areaCts;
    private CancellationTokenSource? _groundCts;
    private int _placesLoad;
    private int _rebuild;
    private bool _npcMarkers;
    private int _pictureLoad;

    /// <summary>Raised to show a part of the map (a ground finding).</summary>
    public event EventHandler<SpawnArea>? FocusRequested;

    /// <summary>The island picture under the map; null shows the grid only.</summary>
    [ObservableProperty]
    private IImage? _mapPicture;

    /// <summary>Where the picture comes from.</summary>
    [ObservableProperty]
    private string _mapSourceText = string.Empty;

    /// <summary>The picture is one the user imported.</summary>
    [ObservableProperty]
    private bool _hasImportedPicture;

    /// <summary>The spawn places as map markers (id = index in the page's places).</summary>
    [ObservableProperty]
    private IReadOnlyList<SpawnMapMarker> _markers = [];

    /// <summary>What a click on the map does.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPanTool), nameof(IsSectorTool), nameof(IsRectangleTool), nameof(IsPolygonTool), nameof(IsPlaceTool))]
    private SpawnMapTool _mapTool = SpawnMapTool.Sector;

    /// <summary>The selected area (two-way with the map).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArea))]
    private SpawnArea? _area;

    /// <summary>The category's places inside the area.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAreaPlaces))]
    private IReadOnlyList<SpawnAreaPlaceItem> _areaPlaces = [];

    /// <summary>The place whose settings are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOwnCopy))]
    private SpawnAreaPlaceItem? _selectedPlace;

    /// <summary>Markers drawn highlighted: the checked places and the selected one.</summary>
    [ObservableProperty]
    private IReadOnlySet<int>? _highlighted;

    /// <summary>"12 places, 2 zones", or what to do.</summary>
    [ObservableProperty]
    private string _areaText;

    /// <summary>The area query runs.</summary>
    [ObservableProperty]
    private bool _isSearchingArea;

    /// <summary>The kinds a new place of this category can be.</summary>
    [ObservableProperty]
    private IReadOnlyList<SpawnKindOption> _newKinds = [new(SpawnPlaceKind.Vehicle)];

    /// <summary>The kind of a new place (and of the places the setting choice applies to).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewKind))]
    private SpawnKindOption? _newKindOption;

    /// <summary>The kind of a new place.</summary>
    public SpawnPlaceKind NewKind
    {
        get => NewKindOption?.Kind ?? SpawnPlaceKind.Vehicle;
        set => NewKindOption = NewKinds.FirstOrDefault(k => k.Kind == value) ?? NewKindOption;
    }

    /// <summary>What a place of <see cref="NewKind"/> can spawn (zone settings, vehicle groups, biomes).</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _settingChoices = [];

    /// <summary>What the new place, or the checked places, spawn.</summary>
    [ObservableProperty]
    private string? _chosenSetting;

    /// <summary>The spawn points below the ground found by the last check.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGroundFindings))]
    private IReadOnlyList<SpawnGroundItem> _groundFindings = [];

    /// <summary>The ground check runs.</summary>
    [ObservableProperty]
    private bool _isCheckingGround;

    /// <summary>The ground check's result.</summary>
    [ObservableProperty]
    private string _groundText = string.Empty;

    /// <summary>The layers of the category.</summary>
    public SpawnMapLayers ShownLayers => Tab switch
    {
        SpawnTab.Vehicles => SpawnMapLayers.Vehicles,
        SpawnTab.Zombies => SpawnMapLayers.Zombies | SpawnMapLayers.Zones,
        SpawnTab.Npcs => SpawnMapLayers.Npcs | SpawnMapLayers.Zones,
        SpawnTab.Animals => SpawnMapLayers.Animals,
        _ => SpawnMapLayers.All,
    };

    /// <summary>Pan tool.</summary>
    public bool IsPanTool { get => MapTool == SpawnMapTool.Pan; set => PickTool(value, SpawnMapTool.Pan); }

    /// <summary>Sector tool.</summary>
    public bool IsSectorTool { get => MapTool == SpawnMapTool.Sector; set => PickTool(value, SpawnMapTool.Sector); }

    /// <summary>Rectangle tool.</summary>
    public bool IsRectangleTool { get => MapTool == SpawnMapTool.Rectangle; set => PickTool(value, SpawnMapTool.Rectangle); }

    /// <summary>Polygon tool.</summary>
    public bool IsPolygonTool { get => MapTool == SpawnMapTool.Polygon; set => PickTool(value, SpawnMapTool.Polygon); }

    /// <summary>New place tool.</summary>
    public bool IsPlaceTool { get => MapTool == SpawnMapTool.Place; set => PickTool(value, SpawnMapTool.Place); }

    /// <summary>An area is selected.</summary>
    public bool HasArea => Area is not null;

    /// <summary>The area holds places of the category.</summary>
    public bool HasAreaPlaces => AreaPlaces.Count > 0;

    /// <summary>The selected place is a threat zone (it can get its own settings).</summary>
    public bool CanOwnCopy => SelectedPlace?.Place.Kind == SpawnPlaceKind.Zone;

    /// <summary>The check found points below the ground.</summary>
    public bool HasGroundFindings => GroundFindings.Count > 0;

    /// <summary>UI-thread time of the last area query (its start and its result), for the 25,000-place test.</summary>
    internal TimeSpan LastAreaUiTime { get; private set; }

    /// <summary>The last area query (tests wait for it).</summary>
    internal Task AreaQuery { get; private set; } = Task.CompletedTask;

    /// <summary>The places index of the current places (tests wait for it).</summary>
    internal Task PlacesReady { get; private set; } = Task.CompletedTask;

    partial void OnAreaChanged(SpawnArea? value) => AreaQuery = QueryAreaAsync(value);

    partial void OnSelectedPlaceChanged(SpawnAreaPlaceItem? value)
    {
        UpdateHighlight();
        var leaf = value?.Place.Group;
        var entry = value?.Place.Kind switch
        {
            SpawnPlaceKind.Zone => _zoneEntries.FirstOrDefault(e => Leaf(e.PackagePath).Equals(leaf, StringComparison.OrdinalIgnoreCase)),
            SpawnPlaceKind.Animal => _biomeEntries.FirstOrDefault(e => Leaf(e.PackagePath).Equals(leaf, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        if (entry is not null)
        {
            SelectedEntry = entry;
        }

        if (value is not null && SettingChoices.Contains(value.Place.Group, StringComparer.OrdinalIgnoreCase))
        {
            ChosenSetting = SettingChoices.First(s => s.Equals(value.Place.Group, StringComparison.OrdinalIgnoreCase));
        }
    }

    partial void OnNewKindOptionChanged(SpawnKindOption? value) => FillSettingChoices();

    private void PickTool(bool value, SpawnMapTool tool)
    {
        if (value)
        {
            MapTool = tool;
        }
    }

    /// <summary>The category changed: its layers, its places in the area and the kinds a new place can be.</summary>
    private void OnCategoryChanged()
    {
        NewKinds = (Tab switch
        {
            SpawnTab.Zombies or SpawnTab.Npcs => new[] { SpawnPlaceKind.Zone, SpawnPlaceKind.Character },
            SpawnTab.Animals => [SpawnPlaceKind.Animal],
            _ => [SpawnPlaceKind.Vehicle],
        }).Select(k => new SpawnKindOption(k)).ToList();
        NewKindOption = NewKinds.FirstOrDefault(k => k.Kind == NewKindOption?.Kind) ?? NewKinds[0];
        FillSettingChoices();
        FilterArea();
        if ((Tab == SpawnTab.Npcs) != _npcMarkers)
        {
            PlacesReady = RebuildAsync(); // the zombie / NPC points change glyph
        }
    }

    private void FillSettingChoices()
    {
        IEnumerable<string> choices = NewKind switch
        {
            SpawnPlaceKind.Zone => _zoneEntries.Select(e => Leaf(e.PackagePath)),
            SpawnPlaceKind.Animal => _biomeEntries.Select(e => Leaf(e.PackagePath)),
            _ => _stock.Where(p => p.Kind == NewKind).Select(p => p.Group),
        };
        SettingChoices = choices.Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (ChosenSetting is null || !SettingChoices.Contains(ChosenSetting, StringComparer.OrdinalIgnoreCase))
        {
            ChosenSetting = SettingChoices.FirstOrDefault();
        }
    }

    /// <summary>Reads the island's places on a worker (once per catalog), then lays them out.</summary>
    private async Task LoadPlacesAsync()
    {
        var load = ++_placesLoad;
        if (_source is not { } source)
        {
            (_stock, _places, _index, Markers) = ([], [], null, []);
            return;
        }

        var stock = await Task.Run(() => source.Places).ConfigureAwait(true);
        if (load != _placesLoad)
        {
            return;
        }

        _stock = stock;
        FillSettingChoices();
        RefreshPlaces();
    }

    /// <summary>The places as the project has them (deleted, moved, re-pointed, new), then the index and markers.</summary>
    private void RefreshPlaces()
    {
        var state = _services.Projects.Current?.State;
        _places = state is null ? _stock : SpawnPlaces.WithEdits(_stock, state);
        _zoneEntries = WithZoneCopies(_stockZoneEntries);
        if (Tab is SpawnTab.Zombies or SpawnTab.Npcs)
        {
            RefreshList();
        }

        PlacesReady = RebuildAsync();
    }

    /// <summary>Indexes the places and builds the markers on a worker, then asks the area again.</summary>
    private async Task RebuildAsync()
    {
        var load = ++_rebuild;
        var (places, npcs) = (_places, _npcMarkers = Tab == SpawnTab.Npcs);
        var (index, ids, markers) = await Task.Run(() =>
        {
            var ids = new Dictionary<SpawnPlace, int>(places.Count, ReferenceEqualityComparer.Instance);
            var markers = new SpawnMapMarker[places.Count];
            for (var i = 0; i < places.Count; i++)
            {
                ids[places[i]] = i;
                markers[i] = Marker(i, places[i], npcs);
            }

            return (SpawnPlaceIndex.Build(places), ids, markers);
        }).ConfigureAwait(true);
        if (load != _rebuild)
        {
            return;
        }

        (_index, _ids, Markers) = (index, ids, markers);
        await (AreaQuery = QueryAreaAsync(Area)).ConfigureAwait(true);
    }

    private static SpawnMapMarker Marker(int id, SpawnPlace place, bool npcs)
    {
        var at = place.Transform.Translation;
        return place.Kind switch
        {
            SpawnPlaceKind.Vehicle => new SpawnMapMarker(id, at.X, at.Y, SpawnMapLayer.Vehicles),
            SpawnPlaceKind.Zone => new SpawnMapMarker(id, at.X, at.Y, SpawnMapLayer.Zones, place.SizeX, place.SizeY, TransformValue.FromTransform(place.Transform).Rotation.Yaw),
            SpawnPlaceKind.Animal => new SpawnMapMarker(id, at.X, at.Y, SpawnMapLayer.Animals, place.SizeX, place.SizeY),

            // A zombie / NPC point spawns what its zone draws: shown in the category the user looks at.
            _ => new SpawnMapMarker(id, at.X, at.Y, npcs ? SpawnMapLayer.Npcs : SpawnMapLayer.Zombies),
        };
    }

    /// <summary>
    /// The places inside <paramref name="area"/>, found on a worker (a new area cancels the previous query); the UI thread
    /// only starts it and shows the rows.
    /// </summary>
    private async Task QueryAreaAsync(SpawnArea? area)
    {
        var clock = Stopwatch.StartNew();
        _areaCts?.Cancel();
        var cts = _areaCts = new CancellationTokenSource();
        if (area is null || _index is not { } index)
        {
            _areaHits = [];
            SelectedPlace = null;
            FilterArea();
            LastAreaUiTime = clock.Elapsed;
            return;
        }

        IsSearchingArea = true;
        AreaText = Loc.T("Spawns.Area.Searching");
        var ids = _ids;

        // The same area again (after an edit): the checked and the selected places stay picked.
        var kept = _areaHits.Where(h => h.IsChecked).Select(h => h.Actor).ToHashSet();
        var selected = SelectedPlace?.Actor;
        var ui = clock.Elapsed;
        try
        {
            var hits = await Task.Run(() =>
            {
                var inside = index.Inside(area);
                cts.Token.ThrowIfCancellationRequested();
                return inside.Select(p => new SpawnAreaPlaceItem(ids[p], p, OnPlaceChecked, kept.Contains(new ActorRef(SpawnPlaces.StaticDataPath, p.ActorName))))
                    .OrderBy(h => h.Place.Kind).ThenBy(h => h.Place.Group, StringComparer.OrdinalIgnoreCase).ToList();
            }, cts.Token).ConfigureAwait(true);
            clock.Restart();
            if (cts.IsCancellationRequested)
            {
                await Newer(cts).ConfigureAwait(true);
                return;
            }

            _areaHits = hits;
            FilterArea();
            SelectedPlace = selected is null ? null : AreaPlaces.FirstOrDefault(h => h.Actor == selected);
            LastAreaUiTime = ui + clock.Elapsed;
        }
        catch (OperationCanceledException)
        {
            await Newer(cts).ConfigureAwait(true); // a newer query took over: this one ends with it
        }
        finally
        {
            if (ReferenceEquals(_areaCts, cts))
            {
                IsSearchingArea = false;
            }
        }
    }

    // The query that replaced the one of <paramref name="cts"/> (every start is assigned to AreaQuery), else none.
    private Task Newer(CancellationTokenSource cts) => ReferenceEquals(_areaCts, cts) ? Task.CompletedTask : AreaQuery;

    /// <summary>The category's places of the last query, and the count text.</summary>
    private void FilterArea()
    {
        bool Shown(SpawnPlaceKind kind) => Tab switch
        {
            SpawnTab.Vehicles => kind == SpawnPlaceKind.Vehicle,
            SpawnTab.Zombies or SpawnTab.Npcs => kind is SpawnPlaceKind.Zone or SpawnPlaceKind.Character or SpawnPlaceKind.TaggedCharacter,
            SpawnTab.Animals => kind == SpawnPlaceKind.Animal,
            _ => false,
        };

        AreaPlaces = _areaHits.Where(h => Shown(h.Place.Kind)).ToList();
        if (SelectedPlace is not null && !AreaPlaces.Contains(SelectedPlace))
        {
            SelectedPlace = null;
        }

        var zones = AreaPlaces.Count(h => h.Place.Kind is SpawnPlaceKind.Zone or SpawnPlaceKind.Animal);
        AreaText = Area is null ? Loc.T("Spawns.Area.Empty") : Loc.F("Spawns.Area.Count", AreaPlaces.Count - zones, zones);
        UpdateHighlight();
    }

    private void OnPlaceChecked(SpawnAreaPlaceItem item) => UpdateHighlight();

    private void UpdateHighlight()
    {
        var ids = AreaPlaces.Where(h => h.IsChecked).Select(h => h.Id).ToHashSet();
        if (SelectedPlace is { } selected)
        {
            ids.Add(selected.Id);
        }

        Highlighted = ids;
    }

    /// <summary>The checked places, else the selected one.</summary>
    private List<SpawnAreaPlaceItem> Picked() =>
        AreaPlaces.Where(h => h.IsChecked).ToList() is { Count: > 0 } checkedPlaces ? checkedPlaces
        : SelectedPlace is { } one ? [one] : [];

    /// <summary>Checks every place of the area (or unchecks them when all are checked).</summary>
    [RelayCommand]
    private void CheckAll()
    {
        var value = !AreaPlaces.All(h => h.IsChecked);
        foreach (var item in AreaPlaces)
        {
            item.IsChecked = value;
        }
    }

    /// <summary>A click on the map with the New place tool: a new spawn place (not a copy) there, on the ground when known.</summary>
    [RelayCommand]
    private Task PlaceAt(Vector2 world) => AddPlaceAsync(world);

    internal async Task AddPlaceAsync(Vector2 world)
    {
        if (_services.Projects.Current?.State is not { } state)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Spawns.NoProject"));
            return;
        }

        if (ChosenSetting is not { } setting)
        {
            _services.Notifications.Warning(Loc.T("Spawns.Place.New"), Loc.T("Spawns.Place.Setting.Tip"));
            return;
        }

        var kind = NewKind;
        var source = _source;
        var ground = source is null ? null : await Task.Run(() => source.GroundAt(world.X, world.Y)).ConfigureAwait(true);
        var lift = kind == SpawnPlaceKind.Vehicle ? SpawnGroundCheck.VehicleLift : SpawnGroundCheck.CharacterLift;
        var scale = kind switch
        {
            SpawnPlaceKind.Zone => new FVector(50f, 50f, 1f), // half sizes in metres, as the map edits them
            SpawnPlaceKind.Animal => new FVector(100f, 100f, 1f),
            _ => FVector.One,
        };
        var name = SpawnPlaces.NewName(kind, _places.Select(p => p.ActorName).Concat(state.AddedActors.Keys.Select(a => a.Actor)));
        var op = new AddSpawnPlaceOp(name, kind, SettingValue(kind, setting), new TransformValue(new FVector(world.X, world.Y, (ground ?? 0f) + lift), FRotator.Zero, scale));
        if (Record(op))
        {
            _services.Notifications.Success(Loc.T("Spawns.Place.New"), Loc.F("Spawns.Place.Added", name, SpawnPlaces.SettingName(setting)));
        }
    }

    /// <summary>Removes the checked (else the selected) places from the island's data: one undo step.</summary>
    [RelayCommand]
    private void RemovePlaces()
    {
        var picked = Picked();
        if (picked.Count == 0)
        {
            return;
        }

        var ops = picked.Select(p => (EditOp)new DeleteActorOp(p.Actor)).ToList();
        if (Record(ops.Count == 1 ? ops[0] : new BatchOp(Loc.T("Spawns.Place.Remove"), ops)))
        {
            _services.Notifications.Success(Loc.T("Spawns.Place.Remove"), Loc.F("Spawns.Place.Removed", ops.Count));
        }
    }

    /// <summary>The checked (else the selected) places spawn <see cref="ChosenSetting"/>: one undo step.</summary>
    [RelayCommand]
    private void SetSetting()
    {
        var state = _services.Projects.Current?.State;
        if (ChosenSetting is not { } setting || state is null)
        {
            return;
        }

        var ops = Picked().Where(p => SpawnChoices.PropertyOf(p.Place.Kind) is not null && p.Place.Kind == NewKind
                && !p.Place.Group.Equals(SpawnPlaces.SettingName(setting), StringComparison.OrdinalIgnoreCase))
            .Select(p =>
            {
                var property = SpawnChoices.PropertyOf(p.Place.Kind)!;
                return (EditOp)new SetSpawnChoiceOp(p.Actor, property, state.GetSpawnChoice(p.Actor, property), SettingValue(p.Place.Kind, setting));
            }).ToList();
        if (ops.Count > 0)
        {
            Record(ops.Count == 1 ? ops[0] : new BatchOp(Loc.T("Spawns.Place.Setting"), ops));
        }
    }

    /// <summary>
    /// The selected threat zone gets its own copy of its settings (one undo step: the copy and the zone pointing at it), so
    /// an edit of its encounter weights reaches no other zone.
    /// </summary>
    [RelayCommand]
    private void OwnCopy()
    {
        if (SelectedPlace is not { Place.Kind: SpawnPlaceKind.Zone } place || _source is not { } source)
        {
            return;
        }

        if (_services.Projects.Current?.State is not { } state)
        {
            _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Spawns.NoProject"));
            return;
        }

        var stock = _stockZoneEntries.FirstOrDefault(e => Leaf(e.PackagePath).Equals(place.Place.Group, StringComparison.OrdinalIgnoreCase))?.PackagePath;
        try
        {
            var planned = state.AssetClones.SelectMany(c => c.Packages.Select(p => p.New)).ToList();
            var clone = source.ZoneCopy(stock ?? place.Place.Group, planned);
            var old = state.GetSpawnChoice(place.Actor, SpawnChoices.ZoneData);
            if (Record(new BatchOp(Loc.T("Spawns.Zone.OwnCopy"), [clone, new SetSpawnChoiceOp(place.Actor, SpawnChoices.ZoneData, old, clone.NewPrimary)])))
            {
                _services.Notifications.Success(Loc.T("Spawns.Zone.OwnCopy"), Loc.F("Spawns.Zone.OwnCopy.Done", Leaf(clone.NewPrimary)));
            }
        }
        catch (ArgumentException ex)
        {
            _services.Notifications.Error(Loc.T("Module.NotRecorded"), ex.Message);
        }
    }

    /// <summary>Lists the spawn points of the area (else the island) below the ground, on a worker.</summary>
    [RelayCommand]
    private Task CheckGround() => CheckGroundAsync();

    internal async Task CheckGroundAsync()
    {
        if (_source is not { } source)
        {
            return;
        }

        _groundCts?.Cancel();
        var cts = _groundCts = new CancellationTokenSource();
        var places = Area is null ? _places : _areaHits.Select(h => h.Place).ToList();
        var ids = _ids;
        IsCheckingGround = true;
        GroundText = Loc.T("Spawns.Ground.Running");
        try
        {
            var findings = await Task.Run(() => SpawnGroundCheck.Run(places, source.AboveGround, source.GroundAt, null, cts.Token), cts.Token).ConfigureAwait(true);
            GroundFindings = findings.Select(f => new SpawnGroundItem(ids.GetValueOrDefault(f.Place, -1), f)).ToList();
            GroundText = findings.Count == 0 ? Loc.T("Spawns.Ground.None") : Loc.F("Spawns.Ground.Found", findings.Count);
        }
        catch (OperationCanceledException)
        {
            // a newer check took over
        }
        finally
        {
            if (ReferenceEquals(_groundCts, cts))
            {
                IsCheckingGround = false;
            }
        }
    }

    /// <summary>Puts the checked findings on the ground: one undo step.</summary>
    [RelayCommand]
    private void FixGround() => Snap(GroundFindings.Where(f => f.IsChecked).ToList());

    /// <summary>Puts every finding on the ground: one undo step.</summary>
    [RelayCommand]
    private void FixAllGround() => Snap(GroundFindings);

    /// <summary>Shows a finding on the map.</summary>
    [RelayCommand]
    private void GoTo(SpawnGroundItem? item)
    {
        if (item is null)
        {
            return;
        }

        var at = item.Finding.Place.Transform.Translation;
        Highlighted = new HashSet<int> { item.Id };
        FocusRequested?.Invoke(this, SpawnArea.Rectangle(at.X - 5000f, at.Y - 5000f, at.X + 5000f, at.Y + 5000f));
    }

    private void Snap(IReadOnlyList<SpawnGroundItem> items)
    {
        if (items.Count == 0 || _services.Projects.Current?.State is not { } state)
        {
            return;
        }

        var ops = SpawnGroundCheck.SnapOps(items.Select(i => i.Finding), state);
        if (ops.Count > 0 && Record(ops.Count == 1 ? ops[0] : new BatchOp(Loc.T("Spawns.Ground.FixAll"), ops)))
        {
            GroundFindings = GroundFindings.Except(items).ToList();
            GroundText = GroundFindings.Count == 0 ? Loc.T("Spawns.Ground.None") : Loc.F("Spawns.Ground.Found", GroundFindings.Count);
        }
    }

    /// <summary>Records <paramref name="op"/> in the project; a refusal is shown, not thrown.</summary>
    private bool Record(EditOp op)
    {
        try
        {
            _services.Projects.Apply(op);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            _services.Notifications.Error(Loc.T("Module.NotRecorded"), ex.Message);
            _services.Logger.LogWarning("Spawns: {Op} was not recorded: {Message}", op.Describe(), ex.Message);
            return false;
        }
    }

    /// <summary>What a place stores for <paramref name="setting"/>: a zone's or biome's package path, a group's name.</summary>
    private string SettingValue(SpawnPlaceKind kind, string setting)
    {
        var entries = kind switch
        {
            SpawnPlaceKind.Zone => _zoneEntries,
            SpawnPlaceKind.Animal => _biomeEntries,
            _ => [],
        };
        return entries.FirstOrDefault(e => Leaf(e.PackagePath).Equals(setting, StringComparison.OrdinalIgnoreCase))?.PackagePath ?? setting;
    }

    /// <summary>The zone list with the project's zone copies (their own settings), each read as the stock settings it copies.</summary>
    private IReadOnlyList<SpawnEntry> WithZoneCopies(IReadOnlyList<SpawnEntry> stock)
    {
        _zoneCopies.Clear();
        var clones = _services.Projects.Current?.State.AssetClones.Where(c => c.Kind == ZoneCopies.Kind).ToList() ?? [];
        if (clones.Count == 0)
        {
            return stock;
        }

        foreach (var clone in clones)
        {
            _zoneCopies[clone.NewPrimary] = clone.Template;
        }

        return stock.Concat(clones.Select(c => new SpawnEntry(c.NewPrimary, ZoneName(c.NewPrimary), ZoneGroup(c.Template), "Icon.Warning"))).ToList();
    }

    /// <summary>The values of the stock settings <paramref name="template"/> as its copy's (export named after the copy).</summary>
    private static IReadOnlyList<Tunable> AsCopy(IReadOnlyList<Tunable> values, string template, string copy) =>
        values.Select(v => v.Export.Equals(Leaf(template), StringComparison.OrdinalIgnoreCase) ? v with { Export = Leaf(copy) } : v).ToList();

    /// <summary>The chance lists of <paramref name="template"/> with its own list moved to the copy (the encounters' and presets' stay shared).</summary>
    private static IReadOnlyList<SpawnWeightList> AsCopy(IReadOnlyList<SpawnWeightList> lists, string template, string copy) =>
        lists.Select(l => l.Package.Equals(template, StringComparison.OrdinalIgnoreCase)
            ? l with { Package = copy, Export = l.Export.Equals(Leaf(template), StringComparison.OrdinalIgnoreCase) ? Leaf(copy) : l.Export }
            : l).ToList();

    private static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>The island picture: imported, else the game's map, else the terrain; decoded on a worker.</summary>
    private async Task LoadPictureAsync()
    {
        var load = ++_pictureLoad;
        _pictures ??= new IslandMapPicture(Path.Combine(_services.DataDirectory, "cache", "islandmap"), _services.Logger);
        var catalog = _fixedSource is null ? _services.Workspace.Catalog : null;
        try
        {
            var pictures = _pictures;
            var (result, bitmap) = await Task.Run(async () =>
            {
                var found = await pictures.GetAsync(catalog).ConfigureAwait(false);
                return (found, found.PngPath is { } png ? new Bitmap(png) : null);
            }).ConfigureAwait(true);
            if (load != _pictureLoad)
            {
                bitmap?.Dispose();
                return;
            }

            (MapPicture as IDisposable)?.Dispose();
            MapPicture = bitmap;
            MapSourceText = (bitmap is null ? IslandMapSource.None : result.Source) switch
            {
                IslandMapSource.Imported => Loc.T("Spawns.Map.Picture.Source.Imported"),
                IslandMapSource.Game => Loc.T("Spawns.Map.Picture.Source.Game"),
                IslandMapSource.Terrain => Loc.T("Spawns.Map.Picture.Source.Terrain"),
                _ => Loc.T("Spawns.Map.Picture.Source.None"),
            };
            HasImportedPicture = result.Source == IslandMapSource.Imported;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            _services.Logger.LogWarning("Spawns: no island picture: {Message}", ex.Message);
            MapSourceText = Loc.T("Spawns.Map.Picture.Source.None");
        }
    }

    /// <summary>Uses a picture of the user's (kept in the app's cache on this PC only) under the map.</summary>
    [RelayCommand]
    private async Task ImportPicture()
    {
        var file = await _services.Dialogs.OpenFileAsync(Loc.T("Spawns.Map.Picture.Import"), "png", "PNG").ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        _pictures ??= new IslandMapPicture(Path.Combine(_services.DataDirectory, "cache", "islandmap"), _services.Logger);
        if (!await _pictures.ImportAsync(file).ConfigureAwait(true))
        {
            _services.Notifications.Warning(Loc.T("Spawns.Map.Picture.Import"), Loc.F("Spawns.Map.Picture.NotAPicture", Path.GetFileName(file)));
            return;
        }

        await LoadPictureAsync().ConfigureAwait(true);
    }

    /// <summary>Forgets the imported picture: the game's map (or the terrain) again.</summary>
    [RelayCommand]
    private Task ClearPicture()
    {
        _pictures?.ClearImported();
        return LoadPictureAsync();
    }
}
