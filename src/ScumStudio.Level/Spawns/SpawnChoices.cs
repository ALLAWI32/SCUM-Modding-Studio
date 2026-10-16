using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Level.Spawns;

/// <summary>What a weighted list spawns, for grouping the lists the way the owner names them.</summary>
public enum SpawnCategory
{
    /// <summary>Zombies (puppets).</summary>
    Zombies,

    /// <summary>Armed NPCs (drifters, guards).</summary>
    Npcs,

    /// <summary>Animals.</summary>
    Animals,

    /// <summary>Dropship drones.</summary>
    Drones,

    /// <summary>Anything else (an encounter list mixing kinds).</summary>
    Other,
}

/// <summary>One weighted entry: what spawns and its stored weight (a tunable float of its package).</summary>
/// <param name="Name">What spawns (<c>BP_Zombie_Civilian_Fat_Male</c>, <c>Drifter_Class_01</c>, an encounter class).</param>
/// <param name="Path">The weight's tunable path (<c>CharacterClasses[2].Value</c>); null when the weight is not stored (the game's default).</param>
/// <param name="Weight">The stock weight (0 when not stored).</param>
public sealed record SpawnWeight(string Name, string? Path, float Weight);

/// <summary>
/// One list of weights the game draws from (a zone's encounters, an encounter's character presets, a preset's characters,
/// a biome's animals); the chance of an entry is its weight over the list's total.
/// </summary>
/// <param name="Package">The package that stores the weights (<c>/Game/.../Zombies_Civilian_All</c>).</param>
/// <param name="Export">The export key (<c>Zombies_Civilian_All</c>, <c>Default__X_C</c>).</param>
/// <param name="Category">What the list spawns.</param>
/// <param name="Level">0 = a zone's encounters / a biome's population, 1 = an encounter's presets, 2 = a preset's characters.</param>
/// <param name="Entries">The entries in stored order.</param>
public sealed record SpawnWeightList(string Package, string Export, SpawnCategory Category, int Level, IReadOnlyList<SpawnWeight> Entries)
{
    private readonly string? _of;

    /// <summary>The asset's name (<c>Zombies_Civilian_All</c>).</summary>
    public string Name => Package[(Package.LastIndexOf('/') + 1)..];

    /// <summary>
    /// The entry of the list above that this list details (an encounter's name for its presets, a preset's for its
    /// characters): <see cref="Name"/>, or the child encounter when the weights are its parent class's.
    /// </summary>
    public string Of { get => _of ?? Name; init => _of = value; }

    /// <summary>True when every weight is stored, so the list can be edited.</summary>
    public bool IsEditable => Entries.Count > 0 && Entries.All(e => e.Path is not null);
}

/// <summary>An encounter the game can put in a zone's settings, with what it spawns.</summary>
/// <param name="EncounterClass">The class path (<c>/Game/…/MTZ_Village_NPC_Encounter.MTZ_Village_NPC_Encounter_C</c>).</param>
/// <param name="Kind">What it spawns, from its presets (<see cref="SpawnChoices.KindOf"/>).</param>
/// <param name="Presets">Its character presets (object paths), its own or its parent class's.</param>
/// <param name="CharacterClasses">The character Blueprints of those presets (class paths), for the 3D preview.</param>
public sealed record EncounterType(string EncounterClass, SpawnCategory Kind, IReadOnlyList<string> Presets, IReadOnlyList<string> CharacterClasses)
{
    /// <summary>The class name (<c>MTZ_Village_NPC_Encounter</c>).</summary>
    public string Name => SpawnChoices.ClassName(EncounterClass);
}

/// <summary>A stored value that sets how many characters an encounter spawns, and the package that stores it.</summary>
/// <param name="Package">The encounter package that stores it (the class's own, or the parent class's it inherits).</param>
/// <param name="Value">The value (export <c>Default__X_C</c>).</param>
public sealed record EncounterCount(string Package, Tunable Value);

/// <summary>Where zone settings are used: an edit of them changes all of it ("changes every place that uses it").</summary>
/// <param name="Places">The threat zones of the island with those settings.</param>
/// <param name="WholeIsland">
/// They include <see cref="SpawnChoices.LowThreatZone"/>, the settings of the whole island outside the threat zones: the
/// edit reaches further than any number of places, so say "the whole island".
/// </param>
/// <param name="Unplaced">Settings that no threat zone of the island names (the game uses them some other way, or not at all): not in <paramref name="Places"/>, so name them.</param>
public sealed record SpawnUse(int Places, bool WholeIsland, IReadOnlyList<string> Unplaced);

/// <summary>Who shares a character preset: an edit of its weights changes every one of them.</summary>
/// <param name="Encounters">The encounter classes that draw from it.</param>
/// <param name="ZoneSettings">The zone settings that use those encounters.</param>
/// <param name="Use">Where those settings are used.</param>
public sealed record PresetUse(IReadOnlyList<string> Encounters, IReadOnlyList<string> ZoneSettings, SpawnUse Use);

/// <summary>
/// "What spawns here" for the Map's spawn editor (owner: "select a spawn: lists of what can go there, replace it, and the
/// percentage of every zombie"). Per spawner, the game data that decides it:
/// <list type="bullet">
/// <item>a sentry spawner (<c>SentrySpawner2</c>): <see cref="SentryClass"/>, its own soft class in the level;</item>
/// <item>a world vehicle point: <see cref="VehicleGroup"/> in the island's static data (the presets of that group spawn
/// there; a <c>VehiclePreset</c> stores no weight, the server's <c>MaxAmount</c> per vehicle limits them);</item>
/// <item>a threat zone: <see cref="ZoneData"/>, its <c>EncounterZoneData</c>, whose <c>EncounterData</c> weighs its
/// encounters; an encounter class's default object weighs its character presets (<c>_possibleCharacters</c>); a preset
/// (<c>EncounterCharacterSpawnPreset</c>) weighs its character Blueprints (<c>CharacterClasses</c>);</item>
/// <item>a hunting area: <see cref="Biome"/>, its <c>BiomeData</c>, whose <c>PopulationData</c> sets give every animal a
/// <c>PrevalenceNormalized</c> share.</item>
/// </list>
/// The weights are stored values of shared assets: an edit changes every place that uses the asset. The choices are per
/// spawner.
/// </summary>
public static class SpawnChoices
{
    /// <summary>A sentry spawner's choice: the sentry class (soft class path).</summary>
    public const string SentryClass = "SentryClass";

    /// <summary>A world vehicle point's choice: its level spawn group.</summary>
    public const string VehicleGroup = "VehicleLevelSpawnGroup";

    /// <summary>A threat zone's choice: its zone settings.</summary>
    public const string ZoneData = "_zoneData";

    /// <summary>A hunting area's choice: its biome.</summary>
    public const string Biome = "BiomeData";

    /// <summary>Folder of the sentry robot classes.</summary>
    public const string SentryFolder = "/Game/ConZ_Files/Characters/Mechanoids/Sentry/";

    /// <summary>Folder of the zone settings (<c>EncounterZoneData</c>).</summary>
    public const string ZonesFolder = "/Game/ConZ_Files/Encounters/EncounterZones/";

    /// <summary>Folder of the hunting biomes (<c>BiomeData</c>).</summary>
    public const string BiomesFolder = "/Game/ConZ_Files/Hunting/BiomeData/";

    /// <summary>The zone settings of the island outside every threat zone (the Spawns page's "Whole island").</summary>
    public const string LowThreatZone = "DA_EncounterZone_LowThreat";

    /// <summary>Folder of the encounter classes (what a zone's <c>EncounterData</c> rows name).</summary>
    public const string EncountersFolder = "/Game/ConZ_Files/Encounters/EncounterClasses/";

    /// <summary>Folder of the character presets (<c>Zombies/</c>, <c>NPCs/</c>, <c>Animals/</c>, <c>Airborne/</c>, <c>HordePresets/</c>).</summary>
    public const string PresetsFolder = "/Game/ConZ_Files/Encounters/Character_Presets/";

    internal const string GroupTagPrefix = "VehicleLevelSpawnGroup.";

    /// <summary>Parent classes followed for inherited presets (a <c>*_Child</c> encounter stores none of its own).</summary>
    private const int MaxParents = 8;

    /// <summary>Stored names of how many characters an encounter spawns (<c>_numberOfCharacters</c>, <c>SpawnAmount</c>, <c>MaxCount.Min</c>).</summary>
    private static readonly Regex CountName = new("(?i:count|amount|number|quantity)|Num[A-Z_]", RegexOptions.CultureInvariant);

    /// <summary>The choice a spawn place has, or null for a zombie / NPC point (what spawns there is its zone's).</summary>
    public static string? PropertyOf(SpawnPlaceKind kind) => kind switch
    {
        SpawnPlaceKind.Vehicle => VehicleGroup,
        SpawnPlaceKind.Zone => ZoneData,
        SpawnPlaceKind.Animal => Biome,
        _ => null,
    };

    /// <summary>
    /// The sentry robots a spawner can spawn: the character classes of <see cref="SentryFolder"/> (<c>BP_Sentry</c>,
    /// <c>BP_Sentry2</c>, <c>BP_SentryAbandonedBunker</c>; not their AI controllers), as class paths.
    /// </summary>
    public static IReadOnlyList<string> SentryClasses(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.PackageFiles
            .Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
            .Where(p => p.StartsWith(SentryFolder, StringComparison.OrdinalIgnoreCase) && p.IndexOf('/', SentryFolder.Length) < 0)
            .Select(p => p[(p.LastIndexOf('/') + 1)..] is var name && name.StartsWith("BP_Sentry", StringComparison.Ordinal) && !name.Contains("AIController", StringComparison.Ordinal) ? $"{p}.{name}_C" : null)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The package of an asset named <paramref name="name"/> under <paramref name="folder"/> (any depth), or null.</summary>
    public static string? PackageNamed(AssetCatalog catalog, string folder, string name)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.PackageFiles
            .Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
            .FirstOrDefault(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && p.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The settings of the threat zones whose ellipse holds <paramref name="at"/> (the zombies and NPCs of a point come from them).</summary>
    public static IReadOnlyList<string> ZonesAt(IEnumerable<SpawnPlace> places, FVector at)
    {
        ArgumentNullException.ThrowIfNull(places);
        return places.Where(p => p.Kind == SpawnPlaceKind.Zone && p.SizeX > 0f && p.SizeY > 0f && Inside(p))
            .Select(p => p.Group).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        bool Inside(SpawnPlace zone)
        {
            // The point in the zone's frame (turned by its yaw), then the ellipse test.
            var d = at - zone.Transform.Translation;
            var yaw = zone.Transform.Rotation.Rotator().Yaw * MathF.PI / 180f;
            var x = (d.X * MathF.Cos(yaw)) + (d.Y * MathF.Sin(yaw));
            var y = (-d.X * MathF.Sin(yaw)) + (d.Y * MathF.Cos(yaw));
            return (x * x / (zone.SizeX * zone.SizeX)) + (y * y / (zone.SizeY * zone.SizeY)) <= 1f;
        }
    }

    /// <summary>
    /// The weights behind a zone's settings: its encounters, each encounter's character presets, each preset's characters
    /// (a preset used by two encounters is listed once). Packages that cannot be read are left out.
    /// </summary>
    public static IReadOnlyList<SpawnWeightList> ForZone(AssetCatalog catalog, string zonePackage)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var lists = new List<SpawnWeightList>();
        if (Read(catalog, zonePackage) is not { } zone || Find(zone, "EncounterZoneData") is not { } zoneExport
            || zone.Package.ReadProperties(zoneExport).Find("EncounterData")?.Value is not ArrayValue data)
        {
            return lists;
        }

        var encounters = new List<(string Class, SpawnWeight Weight)>();
        for (var i = 0; i < data.Items.Count; i++)
        {
            if (data.Items[i] is StructValue item && SpawnPlaces.Member(item, "EncounterClass")?.Value is ObjectValue o && o.Index != 0)
            {
                var path = zone.Package.GetFullPath(o.Index);
                var weight = SpawnPlaces.Member(item, "EncounterWeight")?.Value as FloatValue;
                encounters.Add((path, new SpawnWeight(ClassName(path), weight is null ? null : $"EncounterData[{i}].EncounterWeight", weight?.Value ?? 0f)));
            }
        }

        lists.Add(new SpawnWeightList(zonePackage, zone.Keys[zoneExport], SpawnCategory.Other, 0, encounters.Select(e => e.Weight).ToList()));
        var presets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (encounterClass, _) in encounters)
        {
            // An encounter that stores no presets of its own (a *_Child) draws from its parent class's: those are listed.
            if (PresetsOf(catalog, encounterClass) is not { } stored)
            {
                continue;
            }

            var entries = stored.Map.Entries.Select((e, i) => (Path: e.Key is ObjectValue k && k.Index != 0 ? stored.Read.Package.GetFullPath(k.Index) : string.Empty, Weight: e.Value as FloatValue, Index: i)).ToList();
            lists.Add(new SpawnWeightList(stored.Package, stored.Export, KindOfPresets(entries.Select(e => e.Path)) ?? CategoryOf(ClassName(encounterClass)), 1,
                entries.Select(e => new SpawnWeight(ClassName(e.Path), e.Weight is null ? null : $"_possibleCharacters[{e.Index}].Value", e.Weight?.Value ?? 0f)).ToList())
            {
                Of = ClassName(encounterClass),
            });
            foreach (var preset in entries.Select(e => e.Path).Where(p => p.Length > 0 && presets.Add(p)))
            {
                if (ForPreset(catalog, preset[..preset.LastIndexOf('.')]) is { } characters)
                {
                    lists.Add(characters);
                }
            }
        }

        return lists;
    }

    /// <summary>The characters of an <c>EncounterCharacterSpawnPreset</c> (<c>CharacterClasses</c>: Blueprint → weight), or null.</summary>
    public static SpawnWeightList? ForPreset(AssetCatalog catalog, string presetPackage)
    {
        if (Read(catalog, presetPackage) is not { } preset || Find(preset, "EncounterCharacterSpawnPreset") is not { } export)
        {
            return null;
        }

        var block = preset.Package.ReadProperties(export);
        if (block.Find("CharacterClasses")?.Value is not MapValue map)
        {
            return null;
        }

        var type = block.Find("PresetType")?.Value is StructValue tag && SpawnPlaces.Member(tag, "TagName")?.Value is NameValue n ? n.Value : string.Empty;
        var category = type.EndsWith(".Zombie", StringComparison.Ordinal) ? SpawnCategory.Zombies
            : PresetKind(presetPackage) is var kind && kind != SpawnCategory.Other ? kind
            : CategoryOf(ClassName(presetPackage) + " " + type);
        return new SpawnWeightList(presetPackage, preset.Keys[export], category, 2,
            map.Entries.Select((e, i) => new SpawnWeight(e.Key is SoftObjectValue s ? ClassName(s.AssetPath) : $"#{i + 1}", e.Value is FloatValue ? $"CharacterClasses[{i}].Value" : null, e.Value is FloatValue f ? f.Value : 0f)).ToList());
    }

    /// <summary>
    /// The animals of a hunting biome: one list per <c>PopulationData</c> (the biome stores two), each animal with its
    /// <c>PrevalenceNormalized</c> share.
    /// </summary>
    public static IReadOnlyList<SpawnWeightList> ForBiome(AssetCatalog catalog, string biomePackage)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (Read(catalog, biomePackage) is not { } biome || Find(biome, "BiomeData") is not { } export)
        {
            return [];
        }

        var lists = new List<SpawnWeightList>();
        foreach (var population in biome.Package.ReadProperties(export).Properties.Where(p => p.Name == "PopulationData"))
        {
            var path = population.ArrayIndex == 0 ? "PopulationData" : $"PopulationData#{population.ArrayIndex}";
            if (population.Value is not StructValue s || SpawnPlaces.Member(s, "AnimalSpawnData")?.Value is not SetValue set)
            {
                continue;
            }

            var animals = set.Items.Select((item, i) => item is StructValue a
                ? new SpawnWeight(
                    SpawnPlaces.Member(a, "AnimalClass")?.Value is SoftObjectValue c ? ClassName(c.AssetPath) : $"#{i + 1}",
                    SpawnPlaces.Member(a, "PrevalenceNormalized")?.Value is FloatValue ? $"{path}.AnimalSpawnData[{i}].PrevalenceNormalized" : null,
                    SpawnPlaces.Member(a, "PrevalenceNormalized")?.Value is FloatValue f ? f.Value : 0f)
                : new SpawnWeight($"#{i + 1}", null, 0f)).ToList();
            lists.Add(new SpawnWeightList(biomePackage, biome.Keys[export], SpawnCategory.Animals, 0, animals));
        }

        return lists;
    }

    /// <summary>
    /// What an encounter class spawns, from the character presets it draws from (its own or its parent class's
    /// <c>_possibleCharacters</c>): <c>Zombies_*</c> and the zombie hordes are zombies, <c>Drifter_*</c> / <c>Guard_*</c>
    /// armed NPCs, <c>Animals_*</c> / <c>APEX_*</c> animals, <c>Dropship*</c> drones; presets of several kinds give
    /// <see cref="SpawnCategory.Other"/>. Only an encounter whose presets cannot be read falls back to its name
    /// (<see cref="CategoryOf"/>): <c>HTZ_Civilian_HotSpot_Character_Encounter_NPC</c> says NPC and may spawn zombies.
    /// </summary>
    public static SpawnCategory KindOf(AssetCatalog catalog, string encounterClass)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(encounterClass);
        return (PresetsOf(catalog, encounterClass) is { } stored ? KindOfPresets(PresetPaths(stored)) : null) ?? CategoryOf(ClassName(encounterClass));
    }

    /// <summary>
    /// Every encounter class of <see cref="EncountersFolder"/> that draws from character presets, with its kind, presets and
    /// characters, by kind then name: the list a zone's own settings pick from. Reads every encounter and preset package:
    /// call it on a worker, never on the UI thread.
    /// </summary>
    public static IReadOnlyList<EncounterType> EncounterCatalog(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var characters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var result = new List<EncounterType>();
        foreach (var package in PackagesUnder(catalog, EncountersFolder))
        {
            var name = package[(package.LastIndexOf('/') + 1)..];
            var presets = PresetsOf(catalog, $"{package}.{name}_C") is { } stored
                ? PresetPaths(stored).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [];
            if (presets.Count == 0)
            {
                continue; // not an encounter class, or one that spawns no characters (a cargo drop, a base class)
            }

            var classes = presets.SelectMany(p => characters.TryGetValue(p, out var known) ? known : characters[p] = CharactersOf(catalog, p))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            result.Add(new EncounterType($"{package}.{name}_C", KindOfPresets(presets) ?? CategoryOf(name), presets, classes));
        }

        return result.OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The encounter classes of every zone settings (<c>EncounterZoneData</c> under <see cref="ZonesFolder"/>), by settings
    /// name (<c>MTZ_Village</c>), as the rows of its <c>EncounterData</c>. Reads every zone package: call it on a worker.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ZoneEncounters(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in PackagesUnder(catalog, ZonesFolder))
        {
            if (Read(catalog, package) is { } zone && Find(zone, "EncounterZoneData") is { } export
                && zone.Package.ReadProperties(export).Find("EncounterData")?.Value is ArrayValue data)
            {
                result[package[(package.LastIndexOf('/') + 1)..]] = data.Items.OfType<StructValue>()
                    .Select(item => SpawnPlaces.Member(item, "EncounterClass")?.Value is ObjectValue o && o.Index != 0 ? zone.Package.GetFullPath(o.Index) : null)
                    .OfType<string>().ToList();
            }
        }

        return result;
    }

    /// <summary>True for <see cref="LowThreatZone"/> (by name or package path), the settings of the whole island outside the threat zones.</summary>
    public static bool IsWholeIsland(string zoneSettings)
    {
        ArgumentNullException.ThrowIfNull(zoneSettings);
        return string.Equals(zoneSettings[(zoneSettings.LastIndexOf('/') + 1)..], LowThreatZone, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where the zone settings <paramref name="zoneSettingsName"/> are used (an edit of the settings changes all of it).</summary>
    public static SpawnUse ZonesUsing(IReadOnlyList<SpawnPlace> places, string zoneSettingsName) => ZonesUsing(places, [zoneSettingsName]);

    /// <summary>
    /// Where the zone settings <paramref name="zoneSettings"/> (names) are used: the threat zones of <paramref name="places"/>
    /// with them, whether they include the whole island's (<see cref="LowThreatZone"/>, which no zone names) and those no
    /// zone names.
    /// </summary>
    public static SpawnUse ZonesUsing(IReadOnlyList<SpawnPlace> places, IEnumerable<string> zoneSettings)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(zoneSettings);
        return UseOf(ZoneCounts(places), zoneSettings);
    }

    /// <summary>For each encounter class, where the zone settings that draw from it are used (the catalogue's usage).</summary>
    public static IReadOnlyDictionary<string, SpawnUse> EncounterUsers(IReadOnlyList<SpawnPlace> places, IReadOnlyDictionary<string, IReadOnlyList<string>> zoneEncounters)
    {
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(zoneEncounters);
        var counts = ZoneCounts(places);
        return zoneEncounters
            .SelectMany(z => z.Value.Select(encounter => (Encounter: encounter, Settings: z.Key)))
            .GroupBy(x => x.Encounter, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => UseOf(counts, g.Select(x => x.Settings)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Who shares the character preset <paramref name="preset"/> (object or package path): the encounters of
    /// <paramref name="catalogue"/> that draw from it, the zone settings that use them and where those are used. A weight
    /// edit of the preset changes every one of them ("changes every place that uses it").
    /// </summary>
    public static PresetUse PresetUsers(IReadOnlyList<SpawnPlace> places, IReadOnlyList<EncounterType> catalogue, IReadOnlyDictionary<string, IReadOnlyList<string>> zoneEncounters, string preset)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(zoneEncounters);
        var package = PackageOf(preset);
        var encounters = catalogue.Where(e => e.Presets.Any(p => string.Equals(PackageOf(p), package, StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.EncounterClass).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = zoneEncounters.Where(z => z.Value.Any(encounters.Contains)).Select(z => z.Key).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new PresetUse(encounters.Order(StringComparer.OrdinalIgnoreCase).ToList(), settings, ZonesUsing(places, settings));
    }

    /// <summary>
    /// The stored values that set how many characters an encounter spawns (names with Count, Amount, Number, Quantity or
    /// Num_ of its class's defaults, or of the parent class it inherits them from), with the package that stores them.
    /// Empty when neither stores one (the game's built-in amount applies).
    /// </summary>
    public static IReadOnlyList<EncounterCount> CountValues(AssetCatalog catalog, string encounterClass)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (var (package, cdo, read) in ClassChain(catalog, encounterClass))
        {
            var counts = TunableReader.Read(read.Package)
                .Where(t => t.Export == cdo && t.Kind is TunableKind.Int or TunableKind.UInt or TunableKind.Float && CountName.IsMatch(t.Path))
                .Select(t => new EncounterCount(package, t)).ToList();
            if (counts.Count > 0)
            {
                return counts;
            }
        }

        return [];
    }

    /// <summary>
    /// What an encounter or preset spawns, by SCUM's names: dropships are drones (also a <c>*_Dropship_NPC_*</c>);
    /// <c>*_NPC_*</c>, drifters and guards are armed NPCs; animals, bears; <c>*_Character_*</c>, puppets and zombies are
    /// zombies. A name says less than the presets (<see cref="KindOf"/>): this is the fallback.
    /// </summary>
    public static SpawnCategory CategoryOf(string name) =>
        name.Contains("Dropship", StringComparison.OrdinalIgnoreCase) ? SpawnCategory.Drones
        : name.Contains("NPC", StringComparison.OrdinalIgnoreCase) || name.Contains("Drifter", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Guard", StringComparison.OrdinalIgnoreCase) ? SpawnCategory.Npcs
        : name.Contains("Animal", StringComparison.OrdinalIgnoreCase) || name.Contains("Bear", StringComparison.OrdinalIgnoreCase) ? SpawnCategory.Animals
        : name.Contains("Character", StringComparison.OrdinalIgnoreCase) || name.Contains("Puppet", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Zombie", StringComparison.OrdinalIgnoreCase) ? SpawnCategory.Zombies
        : SpawnCategory.Other;

    /// <summary>
    /// What a character preset spawns, by its name, else its folder (<see cref="PresetsFolder"/>): Other when neither
    /// says. Hordes are zombies except the APEX bears'.
    /// </summary>
    internal static SpawnCategory PresetKind(string preset)
    {
        var name = ClassName(preset);
        bool Named(string prefix) => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        bool In(string folder) => preset.Contains(PresetsFolder + folder, StringComparison.OrdinalIgnoreCase);
        return Named("Zombies_") ? SpawnCategory.Zombies
            : Named("Drifter_") || Named("Guard_") ? SpawnCategory.Npcs
            : Named("Dropship") ? SpawnCategory.Drones
            : Named("Animals_") || Named("APEX_") ? SpawnCategory.Animals
            : In("Zombies/") ? SpawnCategory.Zombies
            : In("NPCs/") ? SpawnCategory.Npcs
            : In("Animals/") || In("HordePresets/Apex/") ? SpawnCategory.Animals
            : In("Airborne/") ? SpawnCategory.Drones
            : In("HordePresets/") ? SpawnCategory.Zombies
            : SpawnCategory.Other;
    }

    /// <summary>The one kind the presets agree on; Other when they mix kinds; null when none says.</summary>
    private static SpawnCategory? KindOfPresets(IEnumerable<string> presets)
    {
        var kinds = presets.Where(p => p.Length > 0).Select(PresetKind).Where(k => k != SpawnCategory.Other).Distinct().ToList();
        return kinds.Count switch
        {
            0 => null,
            1 => kinds[0],
            _ => SpawnCategory.Other,
        };
    }

    /// <summary>
    /// The <c>_possibleCharacters</c> map an encounter class draws from: its defaults', or the nearest parent class's
    /// (<see cref="MaxParents"/>) when it stores none. Null when no class of the chain stores one or it cannot be read.
    /// </summary>
    private static (string Package, string Export, (CookedPackage Package, string[] Keys) Read, MapValue Map)? PresetsOf(AssetCatalog catalog, string encounterClass)
    {
        foreach (var (package, cdo, read) in ClassChain(catalog, encounterClass))
        {
            var index = Array.IndexOf(read.Keys, cdo);
            if (index >= 0 && read.Package.ReadProperties(index).Find("_possibleCharacters")?.Value is MapValue map)
            {
                return (package, cdo, read, map);
            }
        }

        return null;
    }

    private static IEnumerable<string> PresetPaths((string Package, string Export, (CookedPackage Package, string[] Keys) Read, MapValue Map) stored) =>
        stored.Map.Entries.Select(e => e.Key is ObjectValue k && k.Index != 0 ? stored.Read.Package.GetFullPath(k.Index) : string.Empty);

    /// <summary>
    /// An encounter class and its Blueprint parents, as (package, defaults export): the class export's <c>SuperIndex</c>
    /// is followed until a native class or <see cref="MaxParents"/>.
    /// </summary>
    private static IEnumerable<(string Package, string Cdo, (CookedPackage Package, string[] Keys) Read)> ClassChain(AssetCatalog catalog, string classPath)
    {
        var current = classPath;
        for (var depth = 0; depth <= MaxParents && current.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) && current.Contains('.', StringComparison.Ordinal); depth++)
        {
            var package = PackageOf(current);
            var className = current[(current.LastIndexOf('.') + 1)..];
            if (Read(catalog, package) is not { } read)
            {
                yield break;
            }

            yield return (package, "Default__" + className, read);
            var index = Array.IndexOf(read.Keys, className);
            if (index < 0 || read.Package.Exports[index].SuperIndex == 0)
            {
                yield break;
            }

            current = read.Package.GetFullPath(read.Package.Exports[index].SuperIndex);
        }
    }

    /// <summary>The character Blueprints (class paths) of a preset's <c>CharacterClasses</c>.</summary>
    private static IReadOnlyList<string> CharactersOf(AssetCatalog catalog, string preset)
    {
        if (Read(catalog, PackageOf(preset)) is not { } read || Find(read, "EncounterCharacterSpawnPreset") is not { } export
            || read.Package.ReadProperties(export).Find("CharacterClasses")?.Value is not MapValue map)
        {
            return [];
        }

        return map.Entries.Select(e => e.Key is SoftObjectValue s ? s.AssetPath : string.Empty).Where(p => p.Length > 0 && p != "None").ToList();
    }

    /// <summary>The package paths under <paramref name="folder"/> (any depth).</summary>
    private static IEnumerable<string> PackagesUnder(AssetCatalog catalog, string folder) => catalog.PackageFiles
        .Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
        .Where(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>/Game/A/B.B_C</c> → <c>/Game/A/B</c>.</summary>
    private static string PackageOf(string objectPath)
    {
        var dot = objectPath.IndexOf('.', objectPath.LastIndexOf('/') + 1);
        return dot < 0 ? objectPath : objectPath[..dot];
    }

    /// <summary>The number of threat zones of <paramref name="places"/> per zone settings name.</summary>
    private static Dictionary<string, int> ZoneCounts(IReadOnlyList<SpawnPlace> places) => places
        .Where(p => p.Kind == SpawnPlaceKind.Zone)
        .GroupBy(p => p.Group, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    private static SpawnUse UseOf(Dictionary<string, int> zones, IEnumerable<string> settings)
    {
        var names = settings.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var wholeIsland = names.Any(IsWholeIsland);
        var unplaced = names.Where(n => !IsWholeIsland(n) && !zones.ContainsKey(n)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new SpawnUse(names.Sum(n => zones.GetValueOrDefault(n)), wholeIsland, unplaced);
    }

    /// <summary><c>/Game/.../BP_Zombie_Police.BP_Zombie_Police_C</c> → <c>BP_Zombie_Police</c>.</summary>
    public static string ClassName(string path)
    {
        var name = path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('.')) + 1)..];
        return name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name;
    }

    private static (CookedPackage Package, string[] Keys)? Read(AssetCatalog catalog, string packagePath)
    {
        try
        {
            var package = ModdableAssets.ReadPackage(catalog, packagePath);
            return (package, TunableReader.ExportKeys(package));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static int? Find((CookedPackage Package, string[] Keys) read, string className)
    {
        for (var i = 0; i < read.Package.Exports.Count; i++)
        {
            if (read.Package.GetExportClassName(i) == className)
            {
                return i;
            }
        }

        return null;
    }
}
