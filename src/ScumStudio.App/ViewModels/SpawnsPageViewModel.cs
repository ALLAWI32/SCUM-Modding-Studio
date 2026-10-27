using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <summary>The Spawns page's categories (owner: "vehicles, zombies, NPCs, animals, players").</summary>
public enum SpawnTab
{
    /// <summary>Vehicle spawn presets (fuel, battery, parts) and the server's limits per vehicle.</summary>
    Vehicles,

    /// <summary>Threat zones and the zombie types they spawn.</summary>
    Zombies,

    /// <summary>Threat zones and the armed NPC types (drifters, guards) and drones they spawn.</summary>
    Npcs,

    /// <summary>Hunting biomes and the animals they spawn.</summary>
    Animals,

    /// <summary>Where players spawn: the first-spawn answer and the server's respawn rules.</summary>
    Players,

    /// <summary>Server-wide spawn settings (zombies, hordes, sentries, drones, animals).</summary>
    Server,
}

/// <summary>
/// Spawns: what the game spawns and how. Vehicles (and the two planes, the boats) spawn from <c>VehiclePreset</c> data
/// (<c>Vehicles/SpawningPresets/{AutomaticSpawn,ManualSpawn,Purchase}</c>): fuel and battery at spawn, the chance each part
/// is there and its condition. Zombies and NPCs come from the threat zones (<c>Encounters/EncounterZones</c>,
/// <c>EncounterZoneData</c>): spawn chance, delays, cooldown, distance and the chance lists of encounters, presets and
/// characters (<see cref="SpawnChoices"/>). Animals come from the hunting biomes (<c>BiomeData</c>). All of these are
/// stored values of the game's data, recorded in the project (one undo step per save) and written by Export mod. The
/// server's <c>ServerSettings.ini</c> (multipliers, sentries, drones, animals, respawn rules, how many of each vehicle) is
/// written directly; the server reads it when it starts.
/// </summary>
public sealed partial class SpawnsPageViewModel : PageViewModel, ISearchablePage, IDisposable
{
    private const string PresetsFolder = "/Game/ConZ_Files/Vehicles/SpawningPresets/";

    // Vehicles whose ServerSettings.ini name differs from their preset's.
    private static readonly Dictionary<string, string> IniVehicleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CityBike"] = "Bicycle",
        ["MountainBike"] = "Bicycle",
        ["Barba"] = "Motorboat",
        ["WheelBarrowImprovised"] = "Wheelbarrow",
        ["WheelBarrowMetal"] = "Wheelbarrow",
    };

    private readonly AppServices _services;
    private readonly Action _openSetup;
    private readonly ISpawnSource? _fixedSource;
    private readonly Dictionary<string, IReadOnlyList<SpawnCard>> _cards = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ServerSettingViewModel> _serverRows = new(StringComparer.OrdinalIgnoreCase);
    private ISpawnSource? _source;
    private IReadOnlyList<SpawnEntry> _vehicleEntries = [];
    private IReadOnlyList<SpawnEntry> _zoneEntries = [];
    private IReadOnlyList<SpawnEntry> _biomeEntries = [];
    private ServerSettingsFile? _server;
    private string? _serverPaks;
    private bool _sharedConfirmed;
    private int _load;
    private int _entriesLoad;

    /// <summary>Creates the page over the connected game.</summary>
    public SpawnsPageViewModel(AppServices services, Action? openSetup = null)
        : this(services, null, openSetup)
    {
    }

    /// <summary>Creates the page over <paramref name="source"/> (tests) or, when null, the connected game.</summary>
    internal SpawnsPageViewModel(AppServices services, ISpawnSource? source, Action? openSetup = null)
        : base("spawns", "Spawns", "Vehicles, planes, zombies, sentries and animals: what spawns, where and how")
    {
        _services = services;
        _openSetup = openSetup ?? (() => { });
        _fixedSource = source;
        _services.Workspace.CatalogChanged += OnCatalogChanged;
        _services.Projects.Changed += OnProjectChanged;
        _services.SettingsChanged += OnSettingsChanged;
        _areaText = Loc.T("Spawns.Area.Empty");
        _newKindOption = _newKinds[0];
        LoadServer();
        Connect();
    }

    /// <inheritdoc />
    public override string IconKey => "Icon.Pin";

    /// <summary>Selected category.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVehiclesTab), nameof(IsZombiesTab), nameof(IsNpcsTab), nameof(IsAnimalsTab), nameof(IsPlayersTab), nameof(IsServerTab), nameof(ShowList), nameof(ShownLayers))]
    private SpawnTab _tab = SpawnTab.Vehicles;

    /// <summary>The list (vehicle presets, zones or biomes), filtered.</summary>
    [ObservableProperty]
    private IReadOnlyList<SpawnEntry> _entries = [];

    /// <summary>Selected vehicle preset, zone or biome.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SpawnEntry? _selectedEntry;

    /// <summary>List filter.</summary>
    [ObservableProperty]
    private string _filterText = string.Empty;

    /// <summary>Cards of the selected entry.</summary>
    [ObservableProperty]
    private IReadOnlyList<SpawnCard> _detailCards = [];

    /// <summary>Server settings cards.</summary>
    [ObservableProperty]
    private IReadOnlyList<SpawnCard> _serverCards = [];

    /// <summary>Reading the selected entry's values.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Reading the game's list of vehicle presets, zones and biomes (on a worker).</summary>
    [ObservableProperty]
    private bool _isLoadingEntries;

    /// <summary>Why the selected entry shows no values (read failure, nothing editable); empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailNote))]
    private string _detailNote = string.Empty;

    /// <summary>Changes not applied yet (values, chances and server settings).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(PendingText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(DiscardCommand))]
    private int _pendingCount;

    /// <summary>The save waits for a confirmation: a moved chance list is shared by other places.</summary>
    [ObservableProperty]
    private string _sharedConfirmText = string.Empty;

    /// <summary>Vehicles shown.</summary>
    public bool IsVehiclesTab { get => Tab == SpawnTab.Vehicles; set => Pick(value, SpawnTab.Vehicles); }

    /// <summary>Zombies shown.</summary>
    public bool IsZombiesTab { get => Tab == SpawnTab.Zombies; set => Pick(value, SpawnTab.Zombies); }

    /// <summary>NPCs shown.</summary>
    public bool IsNpcsTab { get => Tab == SpawnTab.Npcs; set => Pick(value, SpawnTab.Npcs); }

    /// <summary>Animals shown.</summary>
    public bool IsAnimalsTab { get => Tab == SpawnTab.Animals; set => Pick(value, SpawnTab.Animals); }

    /// <summary>Players shown.</summary>
    public bool IsPlayersTab { get => Tab == SpawnTab.Players; set => Pick(value, SpawnTab.Players); }

    /// <summary>Server shown.</summary>
    public bool IsServerTab { get => Tab == SpawnTab.Server; set => Pick(value, SpawnTab.Server); }

    /// <summary>The list and detail (not the Players and Server sections).</summary>
    public bool ShowList => Tab is not (SpawnTab.Players or SpawnTab.Server);

    /// <summary>The game's files are connected.</summary>
    public bool HasCatalog => _source is not null;

    /// <summary>An entry is selected.</summary>
    public bool HasSelection => SelectedEntry is not null;

    /// <summary>A note replaces the cards.</summary>
    public bool HasDetailNote => DetailNote.Length > 0;

    /// <summary>Changes wait.</summary>
    public bool HasPending => PendingCount > 0;

    /// <summary>"3 changes".</summary>
    public string PendingText => Loc.F("Spawns.Pending", PendingCount);

    /// <summary>The save waits for a confirmation.</summary>
    public bool IsConfirmingShared => SharedConfirmText.Length > 0;

    /// <summary>The server settings file was found.</summary>
    public bool HasServerFile => _server is not null;

    /// <summary>Where the server settings are, or why they are not.</summary>
    public string ServerFileText => _server?.FilePath ?? Loc.T("Spawns.NoServerFile");

    /// <inheritdoc />
    public void ApplySearch(string? text) => FilterText = text ?? string.Empty;

    /// <inheritdoc />
    public void Dispose()
    {
        _services.Workspace.CatalogChanged -= OnCatalogChanged;
        _services.Projects.Changed -= OnProjectChanged;
        _services.SettingsChanged -= OnSettingsChanged;
        _areaCts?.Cancel();
        _groundCts?.Cancel();
        (MapPicture as IDisposable)?.Dispose();
    }

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Point?.OnLanguageChanged();
        if (HasPending)
        {
            return; // unsaved edits stay; the texts follow on the next load
        }

        _cards.Clear();
        _weights.Clear();
        LoadServer(force: true);
        if (_source is not null)
        {
            _ = LoadEntriesAsync();
        }
    }

    partial void OnTabChanged(SpawnTab value)
    {
        RefreshList();
        FilterTypes();
        OnCategoryChanged();
        ShowNpcWizard();
    }

    partial void OnFilterTextChanged(string value) => RefreshList();

    partial void OnSelectedEntryChanged(SpawnEntry? value) => _ = ShowEntryAsync(value);

    partial void OnSharedConfirmTextChanged(string value) => OnPropertyChanged(nameof(IsConfirmingShared));

    private void Pick(bool value, SpawnTab tab)
    {
        if (value)
        {
            Tab = tab;
        }
    }

    [RelayCommand]
    private void OpenSetup() => _openSetup();

    private bool CanApply() => PendingCount > 0;

    /// <summary>
    /// Records the moved sliders and chances as value edits in the project (one undo step, exported with Export mod) and
    /// writes the changed server settings into <c>ServerSettings.ini</c>. A moved chance list that other zones share asks
    /// for a confirmation first.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    public void Apply()
    {
        var rows = AllRows().ToList();
        var values = rows.OfType<SpawnSliderViewModel>().SelectMany(s => s.Changes()).ToList();
        var groups = PendingGroups().ToList();
        var settings = rows.OfType<ServerSettingViewModel>().Where(s => s.IsPending).ToList();
        if (groups.Any(g => g.IsShared) && !_sharedConfirmed)
        {
            // A list read by the low-threat settings reaches the whole island; else the most threat zones one edit reaches.
            SharedConfirmText = groups.Any(g => g.IsIsland) ? Loc.T("Spawns.Shared.Confirm.Island")
                : Loc.F("Spawns.Shared.Confirm", groups.Max(g => g.Users));
            return;
        }

        _sharedConfirmed = false;
        SharedConfirmText = string.Empty;
        var recorded = 0;
        if (values.Count + groups.Count > 0)
        {
            if (_services.Projects.Current?.State is not { } state)
            {
                _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Spawns.NoProject"));
            }
            else
            {
                var ops = values.Select(v => (EditOp)new SetAssetValueOp(v.Target.Package, v.Target.Tunable.Export, v.Target.Tunable.Path, v.Target.Tunable.Kind.ToString(), v.Target.Committed, v.Value))
                    .Concat(groups.SelectMany(g => g.Changes(state))).ToList();
                try
                {
                    _services.Projects.Apply(ops.Count == 1 ? ops[0] : new BatchOp(Loc.T("Spawns.Saved"), ops));
                    recorded = ops.Count;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
                {
                    var outOfDate = ex.Message.Contains("out of date", StringComparison.OrdinalIgnoreCase);
                    _services.Notifications.Error(Loc.T("Module.NotRecorded"), outOfDate ? Loc.T("Spawns.OutOfDate") : ex.Message);
                    _services.Logger.LogWarning("Spawns: the edits were not recorded: {Message}", ex.Message);
                }
            }
        }

        var written = 0;
        if (settings.Count > 0 && _server is { } server)
        {
            try
            {
                server.Save(settings.ToDictionary(s => s.Key, s => s.NewText, StringComparer.OrdinalIgnoreCase));
                settings.ForEach(s => s.MarkSaved());
                written = settings.Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _services.Notifications.Error(Loc.T("Spawns.ServerNotSaved"), ex.Message);
            }
        }

        if (recorded + written > 0)
        {
            _services.Notifications.Success(Loc.T("Spawns.Saved"), Loc.F("Spawns.SavedDetail", recorded, written, _server?.FilePath ?? "-"));
            _services.Logger.LogInformation("Spawns: {Values} value edit(s) recorded, {Settings} server setting(s) written to {File}.", recorded, written, _server?.FilePath);
        }

        if (recorded > 0)
        {
            RefreshFromProject();
        }

        UpdatePending();
    }

    /// <summary>Saves although a moved chance list is shared (the user confirmed).</summary>
    [RelayCommand]
    private void ConfirmApply()
    {
        _sharedConfirmed = true;
        Apply();
    }

    /// <summary>Does not save the shared list; the moved values stay.</summary>
    [RelayCommand]
    private void CancelShared() => SharedConfirmText = string.Empty;

    /// <summary>Puts every slider back to its last applied value.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Discard()
    {
        foreach (var row in AllRows())
        {
            switch (row)
            {
                case SpawnSliderViewModel slider:
                    slider.Discard();
                    break;
                case ServerSettingViewModel setting:
                    setting.Reset();
                    break;
            }
        }

        foreach (var group in _weights.Values.SelectMany(g => g))
        {
            group.Discard();
        }

        SharedConfirmText = string.Empty;
        UpdatePending();
    }

    /// <summary>Readable name of a vehicle preset: <c>RagerSpawnPreset_RadiationZone_NoBattery</c> → "Rager · Radiation Zone No Battery".</summary>
    public static string PresetName(string packagePath)
    {
        var name = packagePath[(packagePath.LastIndexOf('/') + 1)..];
        var (vehicle, variant) = SplitPreset(name);
        return variant.Length == 0 ? Words(vehicle) : $"{Words(vehicle)} · {Words(variant)}";
    }

    /// <summary>The vehicle a preset is for: <c>KingletDusterManualSpawnPreset</c> → <c>KingletDuster</c>.</summary>
    public static string PresetVehicle(string packagePath) => SplitPreset(packagePath[(packagePath.LastIndexOf('/') + 1)..]).Vehicle;

    /// <summary>Readable zone name: <c>HTZ_Military_TV_Bunker</c> → "Military TV Bunker".</summary>
    public static string ZoneName(string packagePath)
    {
        var name = packagePath[(packagePath.LastIndexOf('/') + 1)..];
        if (name.Equals(SpawnChoices.LowThreatZone, StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("Spawns.WholeIsland");
        }

        var lpc = name.EndsWith("_LPC", StringComparison.OrdinalIgnoreCase);
        name = lpc ? name[..^4] : name;
        foreach (var prefix in new[] { "HTZ_", "MTZ_", "LTZ_", "BD_" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
            }
        }

        return Words(name) + (lpc ? " · " + Loc.T("Spawns.FewPlayers") : string.Empty);
    }

    private static (string Vehicle, string Variant) SplitPreset(string name)
    {
        foreach (var marker in new[] { "ManualSpawnPreset", "PurchaseSpawnPreset", "PurchasePreset", "SpawnPreset" })
        {
            var at = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0)
            {
                return (name[..at], name[(at + marker.Length)..].Trim('_'));
            }
        }

        return (name, string.Empty);
    }

    internal static string Words(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '_')
            {
                sb.Append(' ');
                continue;
            }

            if (i > 0 && char.IsUpper(c) && char.IsLower(text[i - 1]))
            {
                sb.Append(' ');
            }

            sb.Append(c);
        }

        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private void Connect()
    {
        _source = _fixedSource ?? (_services.Workspace.Catalog is { } catalog ? new CatalogSpawnSource(catalog, _services.Logger) : null);
        OnPropertyChanged(nameof(HasCatalog));
        if (_source is { } source)
        {
            _ = LoadEntriesAsync();
            _ = Task.Run(() => source.SettingsUsing(string.Empty)); // builds the shared-list index before the first zone is opened
        }
        else
        {
            _vehicleEntries = _zoneEntries = _biomeEntries = [];
            RefreshList();
        }

        _ = LoadPlacesAsync();
        _ = LoadPictureAsync();
        ResetNpcKinds();
    }

    private void OnCatalogChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(() =>
    {
        _cards.Clear();
        _weights.Clear();
        Connect();
        UpdatePending();
    });

    private void OnProjectChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(() =>
    {
        RefreshFromProject();
        RefreshPlaces();
    });

    /// <summary>Undo/redo, a save or another project: every cached value takes the applied value; moved sliders keep theirs.</summary>
    private void RefreshFromProject()
    {
        var state = _services.Projects.Current?.State;
        foreach (var slider in _cards.Values.SelectMany(c => c).SelectMany(c => c.Rows).SelectMany(Sliders))
        {
            slider.Refresh(t => state?.GetAssetValue(t.Package, t.Tunable.Export, t.Tunable.Path)?.Current ?? t.Tunable.Value);
        }

        foreach (var group in _weights.Values.SelectMany(g => g))
        {
            group.Reload(state);
        }

        UpdateOverall();
        UpdatePending();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(() =>
    {
        // Only another server matters here; grass, quality or language settings keep every unsaved edit. With another
        // server, only the server rows are rebuilt: the moved sliders of the game's data keep their values.
        var paks = _services.Settings.Load().ServerPaksFolder;
        if (!string.Equals(paks, _serverPaks, StringComparison.OrdinalIgnoreCase))
        {
            LoadServer(force: true);
            foreach (var (package, cards) in _cards.ToList())
            {
                IEnumerable<SpawnCard> server = package.StartsWith(PresetsFolder, StringComparison.OrdinalIgnoreCase) ? [LimitsCard(package)!]
                    : package.StartsWith(SpawnChoices.BiomesFolder, StringComparison.OrdinalIgnoreCase) ? BiomeCards() : [];
                _cards[package] = cards.Where(c => !c.Rows.OfType<ServerSettingViewModel>().Any()).Concat(server.OfType<SpawnCard>()).ToList();
            }

            if (SelectedEntry is { } entry && _cards.TryGetValue(entry.PackagePath, out var shown))
            {
                DetailCards = shown;
            }
        }

        UpdatePending();
    });

    /// <summary>
    /// The lists of vehicle presets, zones and biomes, built on a worker: the game's package list has tens of thousands
    /// of entries and is read the first time here (never on the UI thread).
    /// </summary>
    private async Task LoadEntriesAsync()
    {
        var source = _source!;
        var load = ++_entriesLoad;
        IsLoadingEntries = true;
        try
        {
            var (vehicles, zones, biomes) = await Task.Run(() => BuildEntries(source.Packages)).ConfigureAwait(true);
            if (load != _entriesLoad || !ReferenceEquals(source, _source))
            {
                return;
            }

            (_vehicleEntries, _stockZoneEntries, _biomeEntries) = (vehicles, zones, biomes);
            _zoneEntries = WithZoneCopies(zones);
            RefreshList();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _services.Logger.LogWarning("Spawns: the game's spawn data could not be listed: {Message}", ex.Message);
        }
        finally
        {
            if (load == _entriesLoad)
            {
                IsLoadingEntries = false;
            }
        }
    }

    private static (IReadOnlyList<SpawnEntry> Vehicles, IReadOnlyList<SpawnEntry> Zones, IReadOnlyList<SpawnEntry> Biomes) BuildEntries(IReadOnlyList<string> packages)
    {
        var vehicles = packages
            .Where(p => p.StartsWith(PresetsFolder, StringComparison.OrdinalIgnoreCase))
            .Select(p => new SpawnEntry(p, PresetName(p), PresetGroup(p), IconFor(p)))
            .OrderBy(e => GroupOrder(e.PackagePath)).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var zones = packages
            .Where(IsZoneSettings)
            .Select(p => new SpawnEntry(p, ZoneName(p), ZoneGroup(p), "Icon.Warning"))
            .OrderBy(e => ZoneOrder(e.PackagePath)).ThenBy(e => e.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var biomes = packages
            .Where(p => p.StartsWith(SpawnChoices.BiomesFolder, StringComparison.OrdinalIgnoreCase))
            .Select(p => new SpawnEntry(p, ZoneName(p), Loc.T("Spawns.Group.Biome"), "Icon.Tree"))
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return (vehicles, zones, biomes);

        static int GroupOrder(string p) => p.Contains("/AutomaticSpawn/", StringComparison.OrdinalIgnoreCase) ? 0 : p.Contains("/Purchase/", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        static int ZoneOrder(string p) => p.Contains("/Hight_Threat/", StringComparison.OrdinalIgnoreCase) ? 0 : p.Contains("/Medium_Threat/", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    /// <summary>
    /// A zone settings package: any <c>EncounterZoneData</c> under <c>EncounterZones/</c> (every threat folder, not only
    /// high and medium), not the encounter classes kept next to them.
    /// </summary>
    internal static bool IsZoneSettings(string packagePath)
    {
        if (!packagePath.StartsWith(SpawnChoices.ZonesFolder, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = packagePath[(packagePath.LastIndexOf('/') + 1)..];
        return name.StartsWith("DA_EncounterZone", StringComparison.OrdinalIgnoreCase) || !name.Contains("Encounter", StringComparison.OrdinalIgnoreCase);
    }

    private static string PresetGroup(string p) =>
        p.Contains("/AutomaticSpawn/", StringComparison.OrdinalIgnoreCase) ? Loc.T("Spawns.Group.World")
        : p.Contains("/Purchase/", StringComparison.OrdinalIgnoreCase) ? Loc.T("Spawns.Group.Trader")
        : Loc.T("Spawns.Group.Admin");

    private static string ZoneGroup(string p)
    {
        if (p.Contains("/Hight_Threat/", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("Spawns.Group.High");
        }

        if (p.Contains("/Medium_Threat/", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("Spawns.Group.Medium");
        }

        // The folder below EncounterZones/ names the group of any other zone (low threat, events, DLC).
        var rest = p[SpawnChoices.ZonesFolder.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 || rest.StartsWith("Low", StringComparison.OrdinalIgnoreCase) ? Loc.T("Spawns.Group.Low") : Words(rest[..slash]);
    }

    private static string IconFor(string p)
    {
        var vehicle = PresetVehicle(p);
        return vehicle.StartsWith("Kinglet", StringComparison.OrdinalIgnoreCase) ? "Icon.World"
            : vehicle is "Barba" or "Dinghy" or "SUP" or "BigRaft" or "SmallRaft" ? "Icon.Globe"
            : "Icon.Vehicle";
    }

    private void RefreshList()
    {
        var source = Tab switch
        {
            SpawnTab.Vehicles => _vehicleEntries,
            SpawnTab.Zombies or SpawnTab.Npcs => _zoneEntries,
            SpawnTab.Animals => _biomeEntries,
            _ => [],
        };
        var filter = FilterText.Trim();
        var keep = SelectedEntry;
        Entries = source.Where(e => e.Matches(filter)).ToList();
        SelectedEntry = keep is not null && Entries.Contains(keep) ? keep : Entries.FirstOrDefault();
    }

    private async Task ShowEntryAsync(SpawnEntry? entry)
    {
        var load = ++_load;
        // Never show the previous entry's sliders under the new title while it loads.
        DetailCards = [];
        DetailNote = string.Empty;
        ShowTypes(entry);
        if (entry is null || _source is not { } source)
        {
            IsLoading = false;
            return;
        }

        if (_cards.TryGetValue(entry.PackagePath, out var cached))
        {
            DetailCards = cached;
            DetailNote = cached.Count == 0 ? Loc.T("Spawns.NoEditableValues") : string.Empty;
            return;
        }

        IsLoading = true;
        try
        {
            var vehicle = entry.PackagePath.StartsWith(PresetsFolder, StringComparison.OrdinalIgnoreCase);
            var places = _places;
            var template = _zoneCopies.GetValueOrDefault(entry.PackagePath);
            var (data, lists, users) = await Task.Run(() =>
            {
                // A zone's own settings (a project copy) read as the stock settings they copy, written under their own name.
                var read = template is null ? source.Read(entry.PackagePath) : AsCopy(source.Read(template), template, entry.PackagePath);
                var weights = vehicle ? [] : template is null ? source.WeightLists(entry.PackagePath) : AsCopy(source.WeightLists(template), template, entry.PackagePath);
                var leaf = entry.PackagePath[(entry.PackagePath.LastIndexOf('/') + 1)..];
                var counted = places.Count > 0 ? places : source.Places; // the island's places may still be loading
                return (read, weights, weights.DistinctBy(l => l.Package + "|" + l.Export).ToDictionary(l => l.Package + "|" + l.Export,
                    l => SpawnReach.Of(l.Level == 0 ? [leaf] : source.SettingsUsing(l.Package), counted)));
            }).ConfigureAwait(true);
            if (load != _load)
            {
                return;
            }

            var state = _services.Projects.Current?.State;
            SpawnTarget Target(Tunable t) => new(entry.PackagePath, t, state?.GetAssetValue(entry.PackagePath, t.Export, t.Path)?.Current ?? t.Value);
            var targets = data.Where(t => t.CanEdit).Select(Target).ToList();
            var groups = vehicle ? [] : BuildGroups(entry.PackagePath, lists, users);
            var cards = vehicle ? VehicleCards(entry, targets)
                : entry.PackagePath.StartsWith(SpawnChoices.BiomesFolder, StringComparison.OrdinalIgnoreCase) ? BiomeCards()
                : ZoneCards(targets, groups);
            Watch(cards);
            _cards[entry.PackagePath] = cards;
            DetailCards = cards;
            DetailNote = cards.Count == 0 && groups.Count == 0 ? Loc.T("Spawns.NoEditableValues") : string.Empty;
            ShowTypes(entry);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            _services.Logger.LogWarning("Spawns: {Package} could not be read: {Message}", entry.PackagePath, ex.Message);
            if (load == _load)
            {
                DetailCards = [];
                DetailNote = Loc.F("Spawns.ReadFailed", entry.Name, ex.Message);
            }
        }
        finally
        {
            if (load == _load)
            {
                IsLoading = false;
            }
        }
    }

    private IReadOnlyList<SpawnCard> VehicleCards(SpawnEntry entry, List<SpawnTarget> targets)
    {
        IReadOnlyList<SpawnTarget> With(string path, bool partsOnly = false) => targets.Where(t => t.Tunable.Path.Equals(path, StringComparison.Ordinal)
            && (!partsOnly || t.Tunable.ExportClass.StartsWith("VehiclePresetNode", StringComparison.Ordinal))).ToList();
        var percent = Loc.T("Spawns.Unit.Percent");
        SpawnSliderViewModel Percent(string label, string tip, IReadOnlyList<SpawnTarget> on) => new(label, tip, 0, 100, 1, percent, on);
        var cards = new List<SpawnCard>();

        var tank = new List<object>();
        foreach (var (key, path, quick) in new[] { ("Spawns.Fuel", "FuelAmountSpawnPercentageRange", true), ("Spawns.Battery", "BatteryChargeSpawnPercentageRange", true) })
        {
            if (With(path + ".LowerBound.Value") is { Count: > 0 } low && With(path + ".UpperBound.Value") is { Count: > 0 } high)
            {
                tank.Add(new SpawnRangeViewModel(Loc.T(key), Loc.T(key + ".Tip"),
                    Percent(Loc.T("Spawns.From"), Loc.T(key + ".Tip"), low), Percent(Loc.T("Spawns.To"), Loc.T(key + ".Tip"), high), quick));
            }
        }

        cards.Add(new SpawnCard(Loc.T("Spawns.Card.Tank"), tank.Count == 0 ? Loc.T("Spawns.Card.Tank.Default") : Loc.T("Spawns.Card.Tank.Caption"), "Icon.Sliders", tank));

        // The parts are the preset's VehiclePresetNode exports; a node whose chance is the game's default stores none.
        var chance = With("SpawnChance", partsOnly: true);
        var nodes = targets.Where(t => t.Tunable.ExportClass.StartsWith("VehiclePresetNode", StringComparison.Ordinal)).Select(t => t.Tunable.Export).Distinct(StringComparer.Ordinal).Count();
        var conditionLow = With("SpawnHealthPercentageRange.LowerBound.Value", partsOnly: true);
        var conditionHigh = With("SpawnHealthPercentageRange.UpperBound.Value", partsOnly: true);
        var parts = new List<object>();
        if (chance.Count > 0)
        {
            parts.Add(Percent(Loc.T("Spawns.PartChance"), Loc.T("Spawns.PartChance.Tip"), chance));
        }

        if (conditionLow.Count > 0 && conditionHigh.Count > 0)
        {
            parts.Add(new SpawnRangeViewModel(Loc.T("Spawns.PartCondition"), Loc.T("Spawns.PartCondition.Tip"),
                Percent(Loc.T("Spawns.From"), Loc.T("Spawns.PartCondition.Tip"), conditionLow), Percent(Loc.T("Spawns.To"), Loc.T("Spawns.PartCondition.Tip"), conditionHigh)));
        }

        if (nodes > chance.Count)
        {
            parts.Add(new SpawnNoteViewModel(Loc.F("Spawns.PartsDefault", nodes - chance.Count), isWarning: false));
        }

        if (parts.Count > 0)
        {
            cards.Add(new SpawnCard(Loc.T("Spawns.Card.Parts"), Loc.F("Spawns.Card.Parts.Caption", chance.Count), "Icon.Vehicle", parts));
        }

        if (LimitsCard(entry.PackagePath) is { } limits)
        {
            cards.Add(limits);
        }

        return cards;
    }

    /// <summary>The server's limits for the preset's vehicle (one row per ini key, shared by every preset of it); null without a file.</summary>
    private SpawnCard? LimitsCard(string presetPackage)
    {
        if (_server is not { } server)
        {
            return null;
        }

        var vehicle = PresetVehicle(presetPackage);
        var ini = IniVehicleNames.GetValueOrDefault(vehicle, vehicle);
        var limits = new List<object>();
        foreach (var (suffix, label, max) in new[] { ("MaxAmount", "Spawns.MaxAmount", 200.0), ("MaxFunctionalAmount", "Spawns.MaxFunctional", 200.0), ("MinPurchasedAmount", "Spawns.MinPurchased", 50.0) })
        {
            if (server.Find(ini + suffix) is { } key)
            {
                limits.Add(ServerRow(key, label, integer: true, max: max, step: 1, unit: string.Empty));
            }
        }

        return limits.Count == 0 ? null : new SpawnCard(Loc.F("Spawns.Card.Limits", Words(ini)), Loc.T("Spawns.Card.Server.Caption"), "Icon.Server", limits);
    }

    private IReadOnlyList<SpawnCard> ZoneCards(List<SpawnTarget> targets, IReadOnlyList<SpawnWeightGroup> groups)
    {
        IReadOnlyList<SpawnTarget> With(string path) => targets.Where(t => t.Tunable.Path.Equals(path, StringComparison.Ordinal)).ToList();
        SpawnRangeViewModel? Range(string key, string path, double max, double step, string unit, double scale = 1) =>
            With(path + ".Min") is { Count: > 0 } low && With(path + ".Max") is { Count: > 0 } high
                ? new SpawnRangeViewModel(Loc.T(key), Loc.T(key + ".Tip"),
                    new SpawnSliderViewModel(Loc.T("Spawns.From"), Loc.T(key + ".Tip"), 0, max, step, unit, low, scale),
                    new SpawnSliderViewModel(Loc.T("Spawns.To"), Loc.T(key + ".Tip"), 0, max, step, unit, high, scale))
                : null;
        var seconds = Loc.T("Spawns.Unit.Seconds");

        var when = new List<object>();
        if (With("EncounterSpawnChance") is { Count: > 0 } chance)
        {
            when.Add(new SpawnSliderViewModel(Loc.T("Spawns.ZoneChance"), Loc.T("Spawns.ZoneChance.Tip"), 0, 100, 1, Loc.T("Spawns.Unit.Percent"), chance));
        }

        when.AddRange(new[]
        {
            Range("Spawns.FirstDelay", "InitialEncounterSpawnDelay", 1800, 10, seconds),
            Range("Spawns.CheckEvery", "EncounterSpawnCheckInterval", 1800, 10, seconds),
            Range("Spawns.Cooldown", "EncounterCooldownInterval", 3600, 10, seconds),
        }.OfType<object>());

        var where = new List<object>();
        if (Range("Spawns.Distance", "CharacterSpawnDistanceRange", 300, 1, Loc.T("Spawns.Unit.Metres"), scale: 100) is { } distance)
        {
            where.Add(distance);
        }

        if (With("MovementDirectionSpawnHalfAngle") is { Count: > 0 } angle)
        {
            where.Add(new SpawnSliderViewModel(Loc.T("Spawns.Angle"), Loc.T("Spawns.Angle.Tip"), 0, 180, 5, "°", angle));
        }

        // Which groups: the zone's own encounter list, by kind (from the presets each encounter picks), in percent.
        var what = new List<object>();
        if (groups.FirstOrDefault(g => g.List.Level == 0) is { } encounters)
        {
            // Its header says how many threat zones share the settings (or the whole island) and resets the list.
            what.Add(encounters.Header!);
            what.AddRange(encounters.Rows);
            if (!encounters.Rows.Any(r => r.Kind == SpawnCategory.Npcs))
            {
                what.Add(new SpawnNoteViewModel(Loc.T("Spawns.NoNpcGroup"), isWarning: false));
            }

            what.Add(new SpawnNoteViewModel(Loc.T("Spawns.AllWeightsZero"), isWarning: true, () => encounters.AllZero));
        }

        var cards = new List<SpawnCard>
        {
            new(Loc.T("Spawns.Card.When"), Loc.T("Spawns.Card.When.Caption"), "Icon.History", when),
            new(Loc.T("Spawns.Card.Where"), Loc.T("Spawns.Card.Where.Caption"), "Icon.Ruler", where),
            new(Loc.T("Spawns.Card.What"), Loc.T("Spawns.Card.What.Caption"), "Icon.Layers", what),
        };
        return cards.Where(c => c.Rows.Count > 0).ToList();
    }

    private IReadOnlyList<SpawnCard> BiomeCards()
    {
        var rows = new object?[]
        {
            ServerRowIfPresent("AreAnimalsAllowedInWorld", "Spawns.S.Animals", toggle: true),
            ServerRowIfPresent("AnimalGlobalDensityMultiplier", "Spawns.S.AnimalDensity"),
            ServerRowIfPresent("MaxNonVirtualAnimalsInWorld", "Spawns.S.MaxAnimals", integer: true, max: 2000, step: 10),
        }.OfType<object>().ToList();
        return rows.Count == 0 ? [] : [new SpawnCard(Loc.T("Spawns.Server.Animals"), Loc.T("Spawns.Card.Server.Caption"), "Icon.Server", rows)];
    }

    private void LoadServer(bool force = false)
    {
        var paks = _services.Settings.Load().ServerPaksFolder;
        if (!force && _server is not null && string.Equals(paks, _serverPaks, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _serverPaks = paks;
        var path = ServerSettingsFile.PathFor(ModExportService.ResolveServerPaks(paks));
        try
        {
            _server = path is not null && File.Exists(path) ? ServerSettingsFile.Load(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Logger.LogWarning("Spawns: {File} could not be read: {Message}", path, ex.Message);
            _server = null;
        }

        _serverRows = new Dictionary<string, ServerSettingViewModel>(StringComparer.OrdinalIgnoreCase);
        var cards = new List<SpawnCard>();
        if (_server is not null)
        {
            SpawnCard Card(string title, string icon, params ServerSettingViewModel?[] rows) =>
                new(Loc.T(title), Loc.T(title + ".Caption"), icon, rows.OfType<object>().ToList());

            cards.Add(Card("Spawns.Server.Zombies", "Icon.Warning",
                ServerRowIfPresent("MaxAllowedPuppets", "Spawns.S.MaxPuppets", integer: true, min: 0, max: 2000, step: 10, gameDefault: true),
                ServerRowIfPresent("EncounterBaseCharacterAmountMultiplier", "Spawns.S.Amount"),
                ServerRowIfPresent("EncounterExtraCharacterPerPlayerMultiplier", "Spawns.S.PerPlayer"),
                ServerRowIfPresent("EncounterCharacterRespawnTimeMultiplier", "Spawns.S.RespawnTime"),
                ServerRowIfPresent("EncounterCharacterRespawnBatchSizeMultiplier", "Spawns.S.RespawnBatch"),
                ServerRowIfPresent("EncounterZoneActivationDistanceMultiplier", "Spawns.S.ActivationDistance"),
                ServerRowIfPresent("PuppetHealthMultiplier", "Spawns.S.PuppetHealth"),
                ServerRowIfPresent("PuppetRunningSpeedMultiplier", "Spawns.S.PuppetSpeed", max: 3),
                ServerRowIfPresent("EncounterNeverRespawnCharacters", "Spawns.S.NeverRespawn", toggle: true),
                ServerRowIfPresent("PuppetsCanOpenDoors", "Spawns.S.OpenDoors", toggle: true),
                ServerRowIfPresent("PuppetsCanVaultWindows", "Spawns.S.VaultWindows", toggle: true)));
            cards.Add(Card("Spawns.Server.Hordes", "Icon.Layers",
                ServerRowIfPresent("EncounterHordeActivationChanceMultiplier", "Spawns.S.HordeChance"),
                ServerRowIfPresent("EncounterHordeBaseCharacterAmountMultiplier", "Spawns.S.HordeAmount"),
                ServerRowIfPresent("EncounterHordeSpawnDistanceMultiplier", "Spawns.S.HordeDistance"),
                ServerRowIfPresent("EnableLootPuppetHorde", "Spawns.S.LootHorde", toggle: true)));
            cards.Add(Card("Spawns.Server.Machines", "Icon.Shield",
                ServerRowIfPresent("DisableSentrySpawning", "Spawns.S.NoSentries", toggle: true),
                ServerRowIfPresent("EnableSentryRespawning", "Spawns.S.SentryRespawn", toggle: true),
                ServerRowIfPresent("SentryHealthMultiplier", "Spawns.S.SentryHealth"),
                ServerRowIfPresent("MaxAllowedDrones", "Spawns.S.Drones", integer: true, min: 0, max: 50, step: 1),
                ServerRowIfPresent("DropshipWorldEncounterSpawnWeightMultiplier", "Spawns.S.Dropships")));
            cards.Add(Card("Spawns.Server.Animals", "Icon.Tree",
                ServerRowIfPresent("AreAnimalsAllowedInWorld", "Spawns.S.Animals", toggle: true),
                ServerRowIfPresent("AnimalGlobalDensityMultiplier", "Spawns.S.AnimalDensity"),
                ServerRowIfPresent("MaxNonVirtualAnimalsInWorld", "Spawns.S.MaxAnimals", integer: true, min: 0, max: 2000, step: 10)));
        }

        ServerCards = cards.Where(c => c.Rows.Count > 0).ToList();
        PlayerCards = BuildPlayerCards();
        Watch(ServerCards.Concat(PlayerCards));
        OnPropertyChanged(nameof(HasServerFile));
        OnPropertyChanged(nameof(ServerFileText));
        UpdatePending();
    }

    /// <summary>The row of a <c>ServerSettings.ini</c> key present in the file (with or without <c>scum.</c>), else null.</summary>
    private ServerSettingViewModel? ServerRowIfPresent(string name, string label, bool toggle = false, bool integer = false, double min = 0, double max = 5, double step = 0.05, bool gameDefault = false) =>
        _server?.Find(name) is { } key ? ServerRow(key, label, toggle, integer, min, max, step, toggle || integer ? string.Empty : Loc.T("Spawns.Unit.Times"), gameDefault) : null;

    /// <summary>One row per key, shared by every card that shows it (two presets of one vehicle, the Animals and Server tabs).</summary>
    private ServerSettingViewModel ServerRow(string key, string label, bool toggle = false, bool integer = false, double min = 0, double max = 5, double step = 0.05, string? unit = null, bool gameDefault = false)
    {
        if (!_serverRows.TryGetValue(key, out var row))
        {
            row = new ServerSettingViewModel(key, Loc.T(label), Loc.T(label + ".Tip"), _server!.Values[key], toggle, integer, min, max, step, unit ?? string.Empty, gameDefault);
            _serverRows[key] = row;
            Watch([new SpawnCard(string.Empty, string.Empty, string.Empty, [row])]);
        }

        return row;
    }

    private readonly HashSet<object> _watched = new(ReferenceEqualityComparer.Instance);

    private void Watch(IEnumerable<SpawnCard> cards)
    {
        foreach (var row in cards.SelectMany(c => c.Rows))
        {
            foreach (var observable in row is SpawnRangeViewModel r ? new INotifyPropertyChanged[] { r.From, r.To } : row is INotifyPropertyChanged n ? [n] : [])
            {
                if (!_watched.Add(observable))
                {
                    continue;
                }

                observable.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(SpawnSliderViewModel.IsPending) or nameof(ServerSettingViewModel.IsPending))
                    {
                        UpdatePending();
                    }
                };
            }
        }
    }

    private static IEnumerable<SpawnSliderViewModel> Sliders(object row) => row switch
    {
        SpawnRangeViewModel range => [range.From, range.To],
        SpawnSliderViewModel slider => [slider],
        _ => [],
    };

    private IEnumerable<object> AllRows() =>
        _cards.Values.SelectMany(c => c).Concat(ServerCards).Concat(PlayerCards).SelectMany(c => c.Rows)
            .SelectMany(r => r is SpawnRangeViewModel range ? new object[] { range.From, range.To } : [r])
            .Where(r => r is not SpawnWeightRowViewModel)
            .Distinct(ReferenceEqualityComparer.Instance);

    private IEnumerable<SpawnWeightGroup> PendingGroups() => _weights.Values.SelectMany(g => g).Where(g => g.Rows.Any(r => r.IsPending)).Distinct();

    /// <summary>The page's name of a kind of spawn place.</summary>
    internal static string KindName(SpawnPlaceKind kind) => kind switch
    {
        SpawnPlaceKind.Vehicle => Loc.T("Spawns.Place.Kind.Vehicle"),
        SpawnPlaceKind.Character => Loc.T("Spawns.Place.Kind.Character"),
        SpawnPlaceKind.TaggedCharacter => Loc.T("Spawns.Place.Kind.TaggedCharacter"),
        SpawnPlaceKind.Zone => Loc.T("Spawns.Place.Kind.Zone"),
        SpawnPlaceKind.CharacterGroup => Loc.T("Spawns.Place.Kind.CharacterGroup"),
        _ => Loc.T("Spawns.Place.Kind.Animal"),
    };

    /// <summary>The icon of a kind of spawn place.</summary>
    internal static string KindIcon(SpawnPlaceKind kind) => kind switch
    {
        SpawnPlaceKind.Vehicle => "Icon.Vehicle",
        SpawnPlaceKind.Zone => "Icon.Warning",
        SpawnPlaceKind.Animal => "Icon.Tree",
        _ => "Icon.Pin",
    };

    private void UpdatePending()
    {
        PendingCount = AllRows().Count(r => r is SpawnSliderViewModel { IsPending: true } or ServerSettingViewModel { IsPending: true })
            + _weights.Values.SelectMany(g => g).Distinct().SelectMany(g => g.Rows).Count(r => r.IsPending);
        foreach (var note in _cards.Values.SelectMany(c => c).SelectMany(c => c.Rows).OfType<SpawnNoteViewModel>())
        {
            note.Update();
        }

        if (!HasPending)
        {
            SharedConfirmText = string.Empty;
        }
    }
}
