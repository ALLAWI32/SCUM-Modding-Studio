using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;
using ScumStudio.Modding.Catalog;

namespace ScumStudio.Viewport;

/// <summary>
/// The 3D model that stands for a spawn place (owner: "no arrows or pins, show me the object itself"): the first vehicle of
/// a world spawn group or of the car shops (its Blueprint package: the body with its stock parts, doors, hood and wheels,
/// as the 3D preview shows it), a zombie, a sentry robot, the trader's NPC and the razor of a razor spawn point (their
/// Blueprint packages: the whole character, body, head, hair and gear, posed from its idle), the first item of a loot
/// preset that has a mesh (mesh object paths) or, failing that, an inventory icon. Each is resolved once per group,
/// preset or class and kept; the preparer loads it (<see cref="SpawnMarkers.StandIn"/>) and falls back to the plain
/// stand-in when it cannot.
/// </summary>
public sealed class SpawnModels
{
    /// <summary>A dressed zombie, for the island's zombie spawn points and the bunkers' creature spawners.</summary>
    public const string ZombieMesh = "/Game/ConZ_Files/Characters/Zombies2/Models/Male_Zombie/Male_Zombie_Mid/SK_Dressed_Mid_01_V1.SK_Dressed_Mid_01_V1";

    /// <summary>The sentry robot, for sentry spawners and their patrol points.</summary>
    public const string SentryMesh = "/Game/ConZ_Files/Characters/Mechanoids/Sentry/SK_SentryRobot_01.SK_SentryRobot_01";

    /// <summary>An armed NPC body, for traders whose NPC class names no mesh.</summary>
    public const string NpcMesh = "/Game/ConZ_Files/Characters/NPCs/Armed_NPCs/NPC_LVL_01/LVL_01_V01/SK_NPC_LVL_01_V01.SK_NPC_LVL_01_V01";

    /// <summary>The razor creature's Blueprint (its skeletal mesh stands at <c>BP_RazorSpawnPoint</c>s).</summary>
    public const string RazorBlueprint = "/Game/ConZ_Files/Characters/NPCs/Creature/Razor/BP_Razor";

    private const string ItemsFolder = "/Game/ConZ_Files/Items/";

    // ponytail: a preset's first dozen items are tried for a mesh; a preset of only meshless items shows the crate.
    private const int ItemsTried = 12;

    private readonly ILogger _logger;
    private readonly MeshPreviewLoader _blueprints;
    private readonly Lazy<VehicleSpawnGroups> _vehicles;
    private readonly Lazy<LootTables> _loot;
    private readonly Lazy<Dictionary<string, string>> _items;
    private readonly ConcurrentDictionary<string, string?> _resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a resolver over <paramref name="catalog"/> (nothing is read until a model is asked for).</summary>
    public SpawnModels(AssetCatalog catalog, ILogger? logger = null)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? NullLogger.Instance;
        _blueprints = new MeshPreviewLoader(catalog, _logger);
        _vehicles = new(() => VehicleSpawnGroups.Read(catalog), LazyThreadSafetyMode.ExecutionAndPublication);
        _loot = new(() => LootTables.Read(catalog), LazyThreadSafetyMode.ExecutionAndPublication);
        _items = new(() => ItemIndex(catalog), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The game files the models come from.</summary>
    public AssetCatalog Catalog { get; }

    /// <summary>
    /// The model that stands for a spawn of <paramref name="kind"/> at <paramref name="actor"/>: a mesh object path, or for
    /// a vehicle its Blueprint package (no object name); null when it stays a pin: zones, loot zones, drop zones, effects
    /// and markers have no object; a loot point needs its <paramref name="marker"/> (a fixed item is drawn already); a
    /// trader its <paramref name="trader"/>.
    /// </summary>
    public string? ModelOf(SpawnKind kind, ActorRecord actor, SpawnMarker? marker = null, TraderMarker? trader = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return kind switch
        {
            SpawnKind.VehiclePlace => Cached("group:" + actor.ClassPath, () => Vehicle(actor.ClassPath)),
            SpawnKind.Vehicle => Cached("group:*", () => Vehicle("City") ?? Vehicle("Default") ?? _vehicles.Value.Groups.Select(Vehicle).FirstOrDefault(m => m is not null)),
            SpawnKind.Zombie => ZombieMesh,
            SpawnKind.Creature => (actor.ClassName == "BP_RazorSpawnPoint_C" ? Character(RazorBlueprint) : null) ?? ZombieMesh,
            SpawnKind.Sentry or SpawnKind.Patrol => SentryMesh,
            SpawnKind.Trader => (trader?.NpcClass is { Length: > 0 } npc ? Character(PackageOf(npc)) : null) ?? NpcMesh,
            SpawnKind.Loot => marker is { ItemClassPath: null, PresetPath: { } preset } ? Cached("preset:" + preset, () => PresetItem(preset)) : null,
            _ => null,
        };
    }

    private string? Cached(string key, Func<string?> resolve)
    {
        if (_resolved.TryGetValue(key, out var known))
        {
            return known;
        }

        string? model;
        try
        {
            model = resolve();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug("Spawn model {Key} could not be resolved: {Message}", key, ex.Message);
            model = null;
        }

        _resolved[key] = model;
        return model;
    }

    /// <summary>The Blueprint package of the first vehicle of <paramref name="group"/> that is in the game files.</summary>
    private string? Vehicle(string group) =>
        _vehicles.Value.VehiclesOf(group).Select(v => PackageOf(v.VehicleClass)).FirstOrDefault(p => p.Length > 0 && Catalog.TryGetPackageFile(p, out _));

    /// <summary>The skeletal or static mesh a Blueprint shows (its component templates / CDO, parent classes followed).</summary>
    private string? Blueprint(string package) => package.Length == 0 ? null : Cached("bp:" + package, () => _blueprints.FindMeshPath(package));

    /// <summary>
    /// A character's Blueprint package when it is in the game files (the whole figure: body, head, hair, gear, posed from
    /// its idle, composed by <see cref="MeshPreviewLoader.LoadBlueprint"/>), else null.
    /// </summary>
    private string? Character(string package) => package.Length > 0 && Catalog.TryGetPackageFile(package, out _) ? package : null;

    /// <summary>
    /// The mesh of the first item of a loot preset that has one; when none of the first items has a mesh (a preset of
    /// cards, papers, ammunition), the inventory icon of the first that has one (<see cref="SpawnMarkers.IconPrefix"/>).
    /// </summary>
    private string? PresetItem(string presetPath)
    {
        if (_loot.Value.Preset(presetPath) is not { } preset)
        {
            return null;
        }

        var names = preset.Nodes.SelectMany(n => _loot.Value.ItemsUnder(n.Tag)).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(ItemsTried).ToList();
        foreach (var name in names)
        {
            if (_items.Value.TryGetValue(name, out var package) && Blueprint(package) is { } mesh)
            {
                return mesh;
            }
        }

        foreach (var name in names)
        {
            if (_items.Value.TryGetValue(name + "_ES", out var setup) && InventoryIcons.FindIconPath(Catalog, setup) is { } icon)
            {
                return SpawnMarkers.IconPrefix + icon;
            }
        }

        return null;
    }

    /// <summary>Item package by its name (<c>Car_Battery</c> → <c>/Game/ConZ_Files/Items/Equipment/Active_Items/Car_Battery</c>).</summary>
    private static Dictionary<string, string> ItemIndex(AssetCatalog catalog)
    {
        var folder = AssetPaths.ToFilePathWithoutExtension(ItemsFolder, catalog.ProjectName);
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in catalog.PackageFiles.Where(f => f.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
        {
            var package = AssetPaths.ToPackagePath(file, catalog.ProjectName);
            index.TryAdd(package[(package.LastIndexOf('/') + 1)..], package);
        }

        return index;
    }

    /// <summary><c>/Game/A/B.B_C</c> → <c>/Game/A/B</c>.</summary>
    private static string PackageOf(string objectPath)
    {
        var dot = objectPath.IndexOf('.', objectPath.LastIndexOf('/') + 1);
        return dot < 0 ? objectPath : objectPath[..dot];
    }
}
