using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.App.Services;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.App.ViewModels;

/// <summary>The Spawns page's sections.</summary>
public enum SpawnTab
{
    /// <summary>Vehicle spawn presets (fuel, battery, parts) and the server's limits per vehicle.</summary>
    Vehicles,

    /// <summary>Threat zones (how often, how far, which encounters).</summary>
    Zombies,

    /// <summary>Server-wide spawn settings (zombies, hordes, sentries, drones, animals).</summary>
    Server,
}

/// <summary>
/// Spawns: what the game spawns and how, in sliders. Vehicles (and the two planes, the boats) spawn from
/// <c>VehiclePreset</c> data (<c>Vehicles/SpawningPresets/{AutomaticSpawn,ManualSpawn,Purchase}</c>): fuel and battery at
/// spawn, the chance each part is there and its condition. Zombies and NPCs come from the threat zones
/// (<c>Encounters/EncounterZones</c>, <c>EncounterZoneData</c>): spawn chance, delays, cooldown, distance, which
/// encounters. Both are stored values of the game's data, recorded in the project like vehicle and weapon values and
/// written by Export mod. The server's <c>ServerSettings.ini</c> (zombie and horde multipliers, sentries, drones, animals,
/// how many of each vehicle) is written directly; the server reads it when it starts.
/// </summary>
public sealed partial class SpawnsPageViewModel : PageViewModel, ISearchablePage, IDisposable
{
    private const string PresetsFolder = "/Game/ConZ_Files/Vehicles/SpawningPresets/";
    private const string ZonesFolder = "/Game/ConZ_Files/Encounters/EncounterZones/";

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
    private readonly Dictionary<string, IReadOnlyList<SpawnCard>> _cards = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SpawnEntry> _vehicleEntries = [];
    private IReadOnlyList<SpawnEntry> _zoneEntries = [];
    private ServerSettingsFile? _server;
    private int _load;

    /// <summary>Creates the page.</summary>
    public SpawnsPageViewModel(AppServices services, Action? openSetup = null)
        : base("spawns", "Spawns", "Vehicles, planes, zombies, sentries and animals: what spawns, where and how")
    {
        _services = services;
        _openSetup = openSetup ?? (() => { });
        _services.Workspace.CatalogChanged += OnCatalogChanged;
        _services.Projects.Changed += OnProjectChanged;
        _services.SettingsChanged += OnSettingsChanged;
        LoadServer();
        if (_services.Workspace.Catalog is { } catalog)
        {
            LoadEntries(catalog);
        }
    }

    /// <inheritdoc />
    public override string IconKey => "Icon.Pin";

    /// <summary>Selected section.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVehiclesTab), nameof(IsZombiesTab), nameof(IsServerTab), nameof(ShowList))]
    private SpawnTab _tab = SpawnTab.Vehicles;

    /// <summary>The list (vehicles or zones), filtered.</summary>
    [ObservableProperty]
    private IReadOnlyList<SpawnEntry> _entries = [];

    /// <summary>Selected vehicle preset or zone.</summary>
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

    /// <summary>Changes not applied yet (values and server settings).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(PendingText))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(DiscardCommand))]
    private int _pendingCount;

    /// <summary>Vehicles section shown.</summary>
    public bool IsVehiclesTab
    {
        get => Tab == SpawnTab.Vehicles;
        set
        {
            if (value)
            {
                Tab = SpawnTab.Vehicles;
            }
        }
    }

    /// <summary>Zombies section shown.</summary>
    public bool IsZombiesTab
    {
        get => Tab == SpawnTab.Zombies;
        set
        {
            if (value)
            {
                Tab = SpawnTab.Zombies;
            }
        }
    }

    /// <summary>Server section shown.</summary>
    public bool IsServerTab
    {
        get => Tab == SpawnTab.Server;
        set
        {
            if (value)
            {
                Tab = SpawnTab.Server;
            }
        }
    }

    /// <summary>The list and detail (not the server section).</summary>
    public bool ShowList => Tab != SpawnTab.Server;

    /// <summary>The game's files are connected.</summary>
    public bool HasCatalog => _services.Workspace.Catalog is not null;

    /// <summary>An entry is selected.</summary>
    public bool HasSelection => SelectedEntry is not null;

    /// <summary>Changes wait.</summary>
    public bool HasPending => PendingCount > 0;

    /// <summary>"3 changes".</summary>
    public string PendingText => Loc.F("Spawns.Pending", PendingCount);

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
    }

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        _cards.Clear();
        LoadServer();
        if (_services.Workspace.Catalog is { } catalog)
        {
            LoadEntries(catalog);
        }
    }

    partial void OnTabChanged(SpawnTab value) => RefreshList();

    partial void OnFilterTextChanged(string value) => RefreshList();

    partial void OnSelectedEntryChanged(SpawnEntry? value) => _ = ShowEntryAsync(value);

    [RelayCommand]
    private void OpenSetup() => _openSetup();

    private bool CanApply() => PendingCount > 0;

    /// <summary>
    /// Records the moved sliders as value edits in the project (exported with Export mod) and writes the changed server
    /// settings into <c>ServerSettings.ini</c>.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    public void Apply()
    {
        var rows = AllRows().ToList();
        var values = rows.OfType<SpawnSliderViewModel>().SelectMany(s => s.Changes()).ToList();
        var settings = rows.OfType<ServerSettingViewModel>().Where(s => s.IsPending).ToList();
        var recorded = 0;
        if (values.Count > 0)
        {
            if (!_services.Projects.HasProject)
            {
                _services.Notifications.Warning(Loc.T("History.NoProject"), Loc.T("Spawns.NoProject"));
            }
            else
            {
                foreach (var (target, value) in values)
                {
                    try
                    {
                        _services.Projects.Apply(new SetAssetValueOp(target.Package, target.Tunable.Export, target.Tunable.Path, target.Tunable.Kind.ToString(), target.Committed, value));
                        recorded++;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
                    {
                        _services.Notifications.Error(Loc.T("Module.NotRecorded"), $"{target.Tunable.Path}: {ex.Message}");
                    }
                }
            }
        }

        var written = 0;
        if (settings.Count > 0 && _server is { } server)
        {
            try
            {
                server.Save(settings.GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().NewText, StringComparer.OrdinalIgnoreCase));
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
            _services.Notifications.Success(Loc.T("Spawns.Saved"), Loc.F("Spawns.SavedDetail", recorded, written));
            _services.Logger.LogInformation("Spawns: {Values} value edit(s) recorded, {Settings} server setting(s) written.", recorded, written);
        }

        if (recorded > 0)
        {
            _cards.Clear();
            _ = ShowEntryAsync(SelectedEntry);
        }

        UpdatePending();
    }

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
        if (name.Equals("DA_EncounterZone_LowThreat", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("Spawns.WholeIsland");
        }

        var lpc = name.EndsWith("_LPC", StringComparison.OrdinalIgnoreCase);
        name = lpc ? name[..^4] : name;
        foreach (var prefix in new[] { "HTZ_", "MTZ_", "LTZ_" })
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

    private static string Words(string text)
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

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        _cards.Clear();
        OnPropertyChanged(nameof(HasCatalog));
        if (_services.Workspace.Catalog is { } catalog)
        {
            LoadEntries(catalog);
        }
        else
        {
            _vehicleEntries = _zoneEntries = [];
            RefreshList();
        }
    }

    private void OnProjectChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(() =>
    {
        // Undo/redo or another project: the applied values changed.
        if (!HasPending)
        {
            _cards.Clear();
            _ = ShowEntryAsync(SelectedEntry);
        }
    });

    private void OnSettingsChanged(object? sender, EventArgs e) => _services.Dispatcher.Post(() =>
    {
        LoadServer();
        _cards.Clear();
        _ = ShowEntryAsync(SelectedEntry);
    });

    private void LoadEntries(AssetCatalog catalog)
    {
        var packages = catalog.PackageFiles.Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName)).ToList();
        _vehicleEntries = packages
            .Where(p => p.StartsWith(PresetsFolder, StringComparison.OrdinalIgnoreCase))
            .Select(p => new SpawnEntry(p, PresetName(p), PresetGroup(p), IconFor(p)))
            .OrderBy(e => GroupOrder(e.PackagePath)).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _zoneEntries = packages
            .Where(p => p.StartsWith(ZonesFolder, StringComparison.OrdinalIgnoreCase)
                && (p.Contains("/Hight_Threat/", StringComparison.OrdinalIgnoreCase) || p.Contains("/Medium_Threat/", StringComparison.OrdinalIgnoreCase)
                    || p.EndsWith("/DA_EncounterZone_LowThreat", StringComparison.OrdinalIgnoreCase)))
            .Select(p => new SpawnEntry(p, ZoneName(p), ZoneGroup(p), "Icon.Warning"))
            .OrderBy(e => e.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        RefreshList();

        static int GroupOrder(string p) => p.Contains("/AutomaticSpawn/", StringComparison.OrdinalIgnoreCase) ? 0 : p.Contains("/Purchase/", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static string PresetGroup(string p) =>
        p.Contains("/AutomaticSpawn/", StringComparison.OrdinalIgnoreCase) ? Loc.T("Spawns.Group.World")
        : p.Contains("/Purchase/", StringComparison.OrdinalIgnoreCase) ? Loc.T("Spawns.Group.Trader")
        : Loc.T("Spawns.Group.Admin");

    private static string ZoneGroup(string p) =>
        p.Contains("/Hight_Threat/", StringComparison.OrdinalIgnoreCase) ? Loc.T("Spawns.Group.High")
        : p.Contains("/Medium_Threat/", StringComparison.OrdinalIgnoreCase) ? Loc.T("Spawns.Group.Medium")
        : Loc.T("Spawns.Group.Low");

    private static string IconFor(string p)
    {
        var vehicle = PresetVehicle(p);
        return vehicle.StartsWith("Kinglet", StringComparison.OrdinalIgnoreCase) ? "Icon.World"
            : vehicle is "Barba" or "Dinghy" or "SUP" or "BigRaft" or "SmallRaft" ? "Icon.Globe"
            : "Icon.Vehicle";
    }

    private void RefreshList()
    {
        var source = Tab switch { SpawnTab.Vehicles => _vehicleEntries, SpawnTab.Zombies => _zoneEntries, _ => [] };
        var filter = FilterText.Trim();
        var keep = SelectedEntry;
        Entries = source.Where(e => e.Matches(filter)).ToList();
        SelectedEntry = keep is not null && Entries.Contains(keep) ? keep : Entries.FirstOrDefault();
    }

    private async Task ShowEntryAsync(SpawnEntry? entry)
    {
        var load = ++_load;
        if (entry is null || _services.Workspace.Catalog is not { } catalog)
        {
            DetailCards = [];
            return;
        }

        if (_cards.TryGetValue(entry.PackagePath, out var cached))
        {
            DetailCards = cached;
            return;
        }

        IsLoading = true;
        try
        {
            var tunables = await Task.Run(() => TunableReader.Read(ModdableAssets.ReadPackage(catalog, entry.PackagePath))).ConfigureAwait(true);
            if (load != _load)
            {
                return;
            }

            var state = _services.Projects.Current?.State;
            SpawnTarget Target(Tunable t) => new(entry.PackagePath, t, state?.GetAssetValue(entry.PackagePath, t.Export, t.Path)?.Current ?? t.Value);
            var targets = tunables.Where(t => t.CanEdit).Select(Target).ToList();
            var cards = entry.PackagePath.StartsWith(PresetsFolder, StringComparison.OrdinalIgnoreCase)
                ? VehicleCards(entry, targets)
                : ZoneCards(targets);
            Watch(cards);
            _cards[entry.PackagePath] = cards;
            DetailCards = cards;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException)
        {
            _services.Logger.LogWarning("Spawns: {Package} could not be read: {Message}", entry.PackagePath, ex.Message);
            DetailCards = [];
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
        IReadOnlyList<SpawnTarget> With(string path) => targets.Where(t => t.Tunable.Path.Equals(path, StringComparison.Ordinal)).ToList();
        SpawnSliderViewModel Percent(string label, IReadOnlyList<SpawnTarget> on) => new(label, string.Empty, 0, 100, 1, "%", on);
        var cards = new List<SpawnCard>();

        var fuelLow = With("FuelAmountSpawnPercentageRange.LowerBound.Value");
        var fuelHigh = With("FuelAmountSpawnPercentageRange.UpperBound.Value");
        var batteryLow = With("BatteryChargeSpawnPercentageRange.LowerBound.Value");
        var batteryHigh = With("BatteryChargeSpawnPercentageRange.UpperBound.Value");
        var tank = new List<object>();
        if (fuelLow.Count > 0 && fuelHigh.Count > 0)
        {
            tank.Add(new SpawnRangeViewModel(Loc.T("Spawns.Fuel"), Loc.T("Spawns.Fuel.Tip"),
                Percent(Loc.T("Spawns.From"), fuelLow), Percent(Loc.T("Spawns.To"), fuelHigh), quickButtons: true));
        }

        if (batteryLow.Count > 0 && batteryHigh.Count > 0)
        {
            tank.Add(new SpawnRangeViewModel(Loc.T("Spawns.Battery"), Loc.T("Spawns.Battery.Tip"),
                Percent(Loc.T("Spawns.From"), batteryLow), Percent(Loc.T("Spawns.To"), batteryHigh), quickButtons: true));
        }

        cards.Add(new SpawnCard(Loc.T("Spawns.Card.Tank"), tank.Count == 0 ? Loc.T("Spawns.Card.Tank.Default") : Loc.T("Spawns.Card.Tank.Caption"), "Icon.Sliders", tank));

        var chance = With("SpawnChance");
        var conditionLow = With("SpawnHealthPercentageRange.LowerBound.Value");
        var conditionHigh = With("SpawnHealthPercentageRange.UpperBound.Value");
        var parts = new List<object>();
        if (chance.Count > 0)
        {
            parts.Add(new SpawnSliderViewModel(Loc.T("Spawns.PartChance"), Loc.T("Spawns.PartChance.Tip"), 0, 100, 1, "%", chance));
        }

        if (conditionLow.Count > 0 && conditionHigh.Count > 0)
        {
            parts.Add(new SpawnRangeViewModel(Loc.T("Spawns.PartCondition"), Loc.T("Spawns.PartCondition.Tip"),
                Percent(Loc.T("Spawns.From"), conditionLow), Percent(Loc.T("Spawns.To"), conditionHigh)));
        }

        if (parts.Count > 0)
        {
            cards.Add(new SpawnCard(Loc.T("Spawns.Card.Parts"), Loc.F("Spawns.Card.Parts.Caption", chance.Count), "Icon.Vehicle", parts));
        }

        if (_server is { } server)
        {
            var vehicle = PresetVehicle(entry.PackagePath);
            var ini = IniVehicleNames.GetValueOrDefault(vehicle, vehicle);
            var limits = new List<object>();
            foreach (var (suffix, label, max) in new[] { ("MaxAmount", "Spawns.MaxAmount", 200.0), ("MaxFunctionalAmount", "Spawns.MaxFunctional", 200.0), ("MinPurchasedAmount", "Spawns.MinPurchased", 50.0) })
            {
                var key = server.Values.Keys.FirstOrDefault(k => k.Equals($"scum.{ini}{suffix}", StringComparison.OrdinalIgnoreCase));
                if (key is not null)
                {
                    limits.Add(new ServerSettingViewModel(key, Loc.T(label), Loc.T(label + ".Tip"), server.Values[key], false, true, 0, max, 1));
                }
            }

            if (limits.Count > 0)
            {
                cards.Add(new SpawnCard(Loc.F("Spawns.Card.Limits", Words(ini)), Loc.T("Spawns.Card.Server.Caption"), "Icon.Server", limits));
            }
        }

        return cards;
    }

    private static IReadOnlyList<SpawnCard> ZoneCards(List<SpawnTarget> targets)
    {
        IReadOnlyList<SpawnTarget> With(string path) => targets.Where(t => t.Tunable.Path.Equals(path, StringComparison.Ordinal)).ToList();
        SpawnRangeViewModel? Range(string key, string path, double max, double step, string unit, double scale = 1) =>
            With(path + ".Min") is { Count: > 0 } low && With(path + ".Max") is { Count: > 0 } high
                ? new SpawnRangeViewModel(Loc.T(key), Loc.T(key + ".Tip"),
                    new SpawnSliderViewModel(Loc.T("Spawns.From"), string.Empty, 0, max, step, unit, low, scale),
                    new SpawnSliderViewModel(Loc.T("Spawns.To"), string.Empty, 0, max, step, unit, high, scale))
                : null;

        var when = new List<object>();
        if (With("EncounterSpawnChance") is { Count: > 0 } chance)
        {
            when.Add(new SpawnSliderViewModel(Loc.T("Spawns.ZoneChance"), Loc.T("Spawns.ZoneChance.Tip"), 0, 100, 1, "%", chance));
        }

        when.AddRange(new[]
        {
            Range("Spawns.FirstDelay", "InitialEncounterSpawnDelay", 1800, 10, "s"),
            Range("Spawns.CheckEvery", "EncounterSpawnCheckInterval", 1800, 10, "s"),
            Range("Spawns.Cooldown", "EncounterCooldownInterval", 3600, 10, "s"),
        }.OfType<object>());

        var where = new List<object>();
        if (Range("Spawns.Distance", "CharacterSpawnDistanceRange", 300, 1, "m", scale: 100) is { } distance)
        {
            where.Add(distance);
        }

        if (With("MovementDirectionSpawnHalfAngle") is { Count: > 0 } angle)
        {
            where.Add(new SpawnSliderViewModel(Loc.T("Spawns.Angle"), Loc.T("Spawns.Angle.Tip"), 0, 180, 5, "°", angle));
        }

        var what = targets.Where(t => t.Tunable.Path.StartsWith("EncounterData[", StringComparison.Ordinal) && t.Tunable.Path.EndsWith(".EncounterWeight", StringComparison.Ordinal))
            .Select((t, i) => (object)new SpawnSliderViewModel(Loc.F("Spawns.EncounterWeight", i + 1), Loc.T("Spawns.EncounterWeight.Tip"), 0, 100, 1, string.Empty, [t]))
            .ToList();

        var cards = new List<SpawnCard>
        {
            new(Loc.T("Spawns.Card.When"), Loc.T("Spawns.Card.When.Caption"), "Icon.History", when),
            new(Loc.T("Spawns.Card.Where"), Loc.T("Spawns.Card.Where.Caption"), "Icon.Ruler", where),
        };
        if (what.Count > 0)
        {
            cards.Add(new SpawnCard(Loc.T("Spawns.Card.What"), Loc.T("Spawns.Card.What.Caption"), "Icon.Layers", what));
        }

        return cards.Where(c => c.Rows.Count > 0).ToList();
    }

    private void LoadServer()
    {
        var path = ServerSettingsFile.PathFor(ModExportService.ResolveServerPaks(_services.Settings.Load().ServerPaksFolder));
        try
        {
            _server = path is not null && File.Exists(path) ? ServerSettingsFile.Load(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _services.Logger.LogWarning("Spawns: {File} could not be read: {Message}", path, ex.Message);
            _server = null;
        }

        var cards = new List<SpawnCard>();
        if (_server is { } server)
        {
            ServerSettingViewModel? Row(string key, string label, bool toggle = false, bool integer = false, double min = 0, double max = 5, double step = 0.05, string unit = "x") =>
                server.Get(key) is { } value ? new ServerSettingViewModel(key, Loc.T(label), Loc.T(label + ".Tip"), value, toggle, integer, min, max, step, toggle || integer ? string.Empty : unit) : null;
            SpawnCard Card(string title, string icon, params ServerSettingViewModel?[] rows) =>
                new(Loc.T(title), Loc.T(title + ".Caption"), icon, rows.OfType<object>().ToList());

            cards.Add(Card("Spawns.Server.Zombies", "Icon.Warning",
                Row("scum.MaxAllowedPuppets", "Spawns.S.MaxPuppets", integer: true, min: -1, max: 2000, step: 10),
                Row("scum.EncounterBaseCharacterAmountMultiplier", "Spawns.S.Amount"),
                Row("scum.EncounterExtraCharacterPerPlayerMultiplier", "Spawns.S.PerPlayer"),
                Row("scum.EncounterCharacterRespawnTimeMultiplier", "Spawns.S.RespawnTime"),
                Row("scum.EncounterCharacterRespawnBatchSizeMultiplier", "Spawns.S.RespawnBatch"),
                Row("scum.EncounterZoneActivationDistanceMultiplier", "Spawns.S.ActivationDistance"),
                Row("scum.PuppetHealthMultiplier", "Spawns.S.PuppetHealth"),
                Row("scum.PuppetRunningSpeedMultiplier", "Spawns.S.PuppetSpeed", max: 3),
                Row("scum.EncounterNeverRespawnCharacters", "Spawns.S.NeverRespawn", toggle: true),
                Row("scum.PuppetsCanOpenDoors", "Spawns.S.OpenDoors", toggle: true),
                Row("scum.PuppetsCanVaultWindows", "Spawns.S.VaultWindows", toggle: true)));
            cards.Add(Card("Spawns.Server.Hordes", "Icon.Layers",
                Row("scum.EncounterHordeActivationChanceMultiplier", "Spawns.S.HordeChance"),
                Row("scum.EncounterHordeBaseCharacterAmountMultiplier", "Spawns.S.HordeAmount"),
                Row("scum.EncounterHordeSpawnDistanceMultiplier", "Spawns.S.HordeDistance"),
                Row("scum.EnableLootPuppetHorde", "Spawns.S.LootHorde", toggle: true)));
            cards.Add(Card("Spawns.Server.Machines", "Icon.Shield",
                Row("scum.DisableSentrySpawning", "Spawns.S.NoSentries", toggle: true),
                Row("scum.EnableSentryRespawning", "Spawns.S.SentryRespawn", toggle: true),
                Row("scum.SentryHealthMultiplier", "Spawns.S.SentryHealth"),
                Row("scum.MaxAllowedDrones", "Spawns.S.Drones", integer: true, min: 0, max: 50, step: 1),
                Row("scum.DropshipWorldEncounterSpawnWeightMultiplier", "Spawns.S.Dropships")));
            cards.Add(Card("Spawns.Server.Animals", "Icon.Tree",
                Row("scum.AreAnimalsAllowedInWorld", "Spawns.S.Animals", toggle: true),
                Row("scum.AnimalGlobalDensityMultiplier", "Spawns.S.AnimalDensity"),
                Row("scum.MaxNonVirtualAnimalsInWorld", "Spawns.S.MaxAnimals", integer: true, min: 0, max: 2000, step: 10)));
        }

        ServerCards = cards.Where(c => c.Rows.Count > 0).ToList();
        Watch(ServerCards);
        OnPropertyChanged(nameof(HasServerFile));
        OnPropertyChanged(nameof(ServerFileText));
        UpdatePending();
    }

    private void Watch(IEnumerable<SpawnCard> cards)
    {
        foreach (var row in cards.SelectMany(c => c.Rows))
        {
            foreach (var observable in row is SpawnRangeViewModel r ? new INotifyPropertyChanged[] { r.From, r.To } : [(INotifyPropertyChanged)row])
            {
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

    private IEnumerable<object> AllRows() =>
        _cards.Values.SelectMany(c => c).Concat(ServerCards).SelectMany(c => c.Rows)
            .SelectMany(r => r is SpawnRangeViewModel range ? new object[] { range.From, range.To } : [r]);

    private void UpdatePending() =>
        PendingCount = AllRows().Count(r => r is SpawnSliderViewModel { IsPending: true } or ServerSettingViewModel { IsPending: true });
}
