using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ScumStudio.App.Localization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Export;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;
using ScumStudio.Viewport;

namespace ScumStudio.App.ViewModels;

/// <summary>
/// The Spawns page's game data from the connected catalog: package values, the chance lists of zones and biomes (read once
/// per catalog), which zone settings share a list, the island's spawn places, the ground under them and type previews.
/// Every member may run on a worker.
/// </summary>
internal sealed class CatalogSpawnSource : ISpawnSource
{
    private const string CharactersRoot = "/Game/ConZ_Files/";

    private readonly AssetCatalog _catalog;
    private readonly ILogger _logger;
    private readonly Lazy<IReadOnlyList<string>> _packages;
    private readonly Lazy<Dictionary<string, IReadOnlyList<string>>> _users;
    private readonly Lazy<Dictionary<string, string>> _byName;
    private readonly Lazy<IReadOnlyList<SpawnPlace>> _places;
    private readonly Lazy<Func<SpawnPlace, bool>> _aboveGround;
    private readonly ConcurrentDictionary<string, IReadOnlyList<SpawnWeightList>> _lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly GroundHeights _ground;
    private readonly object _groundGate = new();

    /// <summary>Creates the source.</summary>
    public CatalogSpawnSource(AssetCatalog catalog, ILogger logger)
    {
        _catalog = catalog;
        _logger = logger;
        _packages = new(() => catalog.PackageFiles.Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName)).ToList());
        _users = new(CountUsers);
        _byName = new(() => Packages.Where(p => p.StartsWith(CharactersRoot, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p[(p.LastIndexOf('/') + 1)..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));
        _places = new(() => Guarded(() => SpawnPlaces.ReadFrom(catalog), [], "the island's spawn places"));
        _aboveGround = new(() => SpawnGroundCheck.OutsideUnderground(Guarded(() => SpawnGroundCheck.UndergroundBoxes(catalog), [], "the underground levels")));
        _ground = new GroundHeights(catalog);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Packages => _packages.Value;

    /// <inheritdoc />
    public IReadOnlyList<SpawnPlace> Places => _places.Value;

    /// <inheritdoc />
    public IReadOnlyList<Tunable> Read(string packagePath) => TunableReader.Read(ModdableAssets.ReadPackage(_catalog, packagePath));

    /// <inheritdoc />
    public IReadOnlyList<SpawnWeightList> WeightLists(string packagePath) =>
        _lists.GetOrAdd(packagePath, p => p.StartsWith(SpawnChoices.BiomesFolder, StringComparison.OrdinalIgnoreCase) ? SpawnChoices.ForBiome(_catalog, p) : SpawnChoices.ForZone(_catalog, p));

    /// <inheritdoc />
    public IReadOnlyList<string> SettingsUsing(string package) => _users.Value.GetValueOrDefault(package) ?? [];

    /// <inheritdoc />
    public float? GroundAt(float x, float y)
    {
        lock (_groundGate)
        {
            return Guarded(() => _ground.At(x, y), null, "the ground");
        }
    }

    /// <inheritdoc />
    public bool AboveGround(SpawnPlace place) => _aboveGround.Value(place);

    /// <inheritdoc />
    public CloneAssetOp ZoneCopy(string zonePackage, IReadOnlyCollection<string> planned)
    {
        var name = ZoneCopies.SuggestName(zonePackage[(zonePackage.LastIndexOf('/') + 1)..], ZoneCopies.Taken(_catalog, planned));
        return ZoneCopies.Plan(_catalog, zonePackage, name, planned);
    }

    /// <inheritdoc />
    public PreviewModel? Preview(string? blueprint, SpawnCategory category)
    {
        var loader = new MeshPreviewLoader(_catalog, _logger);
        if (blueprint is not null && _byName.Value.TryGetValue(blueprint, out var package) && loader.LoadBlueprint(package) is { } model)
        {
            return model;
        }

        return category switch
        {
            SpawnCategory.Zombies => loader.LoadMesh(SpawnModels.ZombieMesh),
            SpawnCategory.Npcs => loader.LoadMesh(SpawnModels.NpcMesh),
            _ => null,
        };
    }

    // Every zone settings package, its lists read once: the settings that read each shared list.
    private Dictionary<string, IReadOnlyList<string>> CountUsers()
    {
        var users = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in Packages.Where(SpawnsPageViewModel.IsZoneSettings))
        {
            var name = zone[(zone.LastIndexOf('/') + 1)..];
            foreach (var package in WeightLists(zone).Where(l => l.Level > 0).Select(l => l.Package).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                (users.TryGetValue(package, out var list) ? list : users[package] = []).Add(name);
            }
        }

        _logger.LogInformation("Spawns: {Count} shared spawn lists indexed.", users.Count);
        return users.ToDictionary(u => u.Key, u => (IReadOnlyList<string>)u.Value, StringComparer.OrdinalIgnoreCase);
    }

    private T Guarded<T>(Func<T> read, T fallback, string what)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            _logger.LogWarning("Spawns: {What} could not be read: {Message}", what, ex.Message);
            return fallback;
        }
    }
}

/// <summary>Who an edit of a chance list reaches: the island's threat zones that spawn from it, and the whole island.</summary>
/// <param name="Zones">Threat zones (<see cref="SpawnChoices.ZonesUsing(IReadOnlyList{SpawnPlace}, string)"/> summed over the settings that read the list).</param>
/// <param name="Island">The low-threat settings (<see cref="SpawnChoices.LowThreatZone"/>, everywhere outside the zones) read it.</param>
public readonly record struct SpawnReach(int Zones, bool Island)
{
    /// <summary>The reach of a list read by <paramref name="settings"/> (zone settings names), counted in <paramref name="places"/>.</summary>
    public static SpawnReach Of(IEnumerable<string> settings, IReadOnlyList<SpawnPlace> places)
    {
        var names = settings.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new SpawnReach(names.Sum(s => SpawnChoices.ZonesUsing(places, s).Places),names.Contains(SpawnChoices.LowThreatZone, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>One spawn place inside the selected area of the Spawns map.</summary>
public sealed partial class SpawnAreaPlaceItem : ObservableObject
{
    private readonly Action<SpawnAreaPlaceItem> _checked;

    /// <summary>Creates the row of the place <paramref name="id"/> (its marker id on the map).</summary>
    public SpawnAreaPlaceItem(int id, SpawnPlace place, Action<SpawnAreaPlaceItem> isCheckedChanged, bool isChecked = false)
    {
        Id = id;
        Place = place;
        _checked = isCheckedChanged;
        _isChecked = isChecked;
        Actor = new ActorRef(SpawnPlaces.StaticDataPath, place.ActorName);
    }

    /// <summary>Its marker id (index in the page's places).</summary>
    public int Id { get; }

    /// <summary>The place.</summary>
    public SpawnPlace Place { get; }

    /// <summary>Its actor in the island's static data (what the edit ops name).</summary>
    public ActorRef Actor { get; }

    /// <summary>Picked for Remove, the settings change and the ground fix.</summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>"Threat zone", "Vehicle point"...</summary>
    public string KindText => SpawnsPageViewModel.KindName(Place.Kind);

    /// <summary>What it spawns (zone settings, vehicle group, biome, Exterior / Interior).</summary>
    public string Setting => Place.Group;

    /// <summary>The kind's icon.</summary>
    public string IconKey => SpawnsPageViewModel.KindIcon(Place.Kind);

    /// <summary>Hover text: actor name and position.</summary>
    public string Tip => $"{Place.ActorName} · X {Place.Transform.Translation.X / 100:0} m, Y {Place.Transform.Translation.Y / 100:0} m, Z {Place.Transform.Translation.Z / 100:0.0} m";

    partial void OnIsCheckedChanged(bool value) => _checked(this);
}

/// <summary>A kind of spawn place in the New place choice.</summary>
/// <param name="Kind">The kind.</param>
public sealed record SpawnKindOption(SpawnPlaceKind Kind)
{
    /// <summary>Its name in the user's language.</summary>
    public string Name => SpawnsPageViewModel.KindName(Kind);
}

/// <summary>A spawn point below the ground, from the under-ground check.</summary>
public sealed partial class SpawnGroundItem : ObservableObject
{
    /// <summary>Creates the row.</summary>
    public SpawnGroundItem(int id, SpawnGroundFinding finding)
    {
        Id = id;
        Finding = finding;
    }

    /// <summary>The place's marker id.</summary>
    public int Id { get; }

    /// <summary>The finding.</summary>
    public SpawnGroundFinding Finding { get; }

    /// <summary>Picked for "Put on the ground".</summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Actor name.</summary>
    public string Name => Finding.Place.ActorName;

    /// <summary>"1.4 m".</summary>
    public string DepthText => $"{Finding.Depth / 100:0.0} {Loc.T("Spawns.Unit.Metres")}";

    /// <summary>The kind of point.</summary>
    public string KindText => SpawnsPageViewModel.KindName(Finding.Place.Kind);
}
