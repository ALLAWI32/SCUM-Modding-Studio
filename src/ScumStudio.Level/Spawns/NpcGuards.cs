using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Level.Spawns;

/// <summary>An armed NPC kind a guard post can spawn: an NPC encounter of the game and the stock zone settings that use it.</summary>
/// <param name="EncounterClass">The encounter class path (<c>/Game/…/MTZ_POI_Military_Base_NPC_Encounter.MTZ_POI_Military_Base_NPC_Encounter_C</c>).</param>
/// <param name="Characters">The character Blueprints it spawns (<c>BP_Guard_Lvl_3</c>, …), by name.</param>
/// <param name="ZonePackage">Stock zone settings with a row for it, copied as the guard zone's own settings.</param>
public sealed record NpcGuardType(string EncounterClass, IReadOnlyList<string> Characters, string ZonePackage)
{
    /// <summary>The encounter's name (<c>MTZ_POI_Military_Base_NPC_Encounter</c>).</summary>
    public string Name => SpawnChoices.ClassName(EncounterClass);

    /// <summary>What it spawns, for a list (<c>BP_Guard_Lvl_3, BP_Guard_Lvl_4</c>).</summary>
    public string CharactersText => string.Join(", ", Characters);
}

/// <summary>A sentry robot to replace: its spawner actor, where it stands and its patrol path in the world.</summary>
/// <param name="Spawner">The <c>SentrySpawner2</c> actor.</param>
/// <param name="Path">Where the robot walks: the spawner, then each patrol point (world space, centimetres).</param>
public sealed record SentryRoute(ActorRef Spawner, IReadOnlyList<FVector> Path);

/// <summary>
/// "Replace with NPC guards" (Discord igor8802: "when creating patrols at points of interest, the robots can be replaced with
/// NPCs"). What the game's data allows, read from the cooked files: the armed NPCs' AI (<c>BP_NPCGuardController</c>,
/// <c>BP_NPCDrifterController</c>) has idle, alerted, combat, investigate, open-door and return-to-post states and no patrol
/// state, and nothing a spawner or a spawn point stores gives an NPC a path; only a sentry robot follows
/// <c>PatrolPoints</c>. So the robot's route becomes guard posts, not a patrol: the spawner is removed (its guarded zone
/// manager forgets it on export), one group of zombie / NPC points (<see cref="SpawnPlaceKind.CharacterGroup"/>) stands on
/// posts along the route (the armed NPC encounters spawn as a group at such points), and an NPC area over the posts gets its
/// own copy of zone settings whose only weighted encounter is the chosen NPC one: a sub-zone of the place's own threat zone,
/// as the game's NPC-only areas are (r39). The game's NPC encounter is not a guard per post: 1 to 3 NPCs come only while a
/// player walks into the area, 30 to 200 m ahead and out of view, then the area rests 20 to 30 minutes; they leave when the
/// players do, a character under new-player protection gets none, and half the time zombies come with them. Whether they
/// come at these posts only the game shows.
/// </summary>
public static class NpcGuards
{
    /// <summary>Posts per robot the panel starts with.</summary>
    public const int DefaultPosts = 4;

    /// <summary>The guard zone reaches this far past the outermost post (cm).</summary>
    public const float ZoneMargin = 2500f;

    /// <summary>The guard zone's smallest half size (cm).</summary>
    public const float MinZoneHalfSize = 3000f;

    /// <summary>The posts' type in the island data (the robots patrol outdoors).</summary>
    public const string PostType = "Exterior";

    /// <summary>The guard area's <c>SubZonePriority</c>: the island's own NPC-only sub-zones (<c>HTZ_Military_TV_Bunker_NPC</c>) have 1.</summary>
    public const int SubZonePriority = 1;

    /// <summary>How the robots' spawner actors are named in the levels (<c>SentrySpawner_0</c>; their class is <c>SentrySpawner2</c>).</summary>
    public const string SpawnerName = "SentrySpawner";

    /// <summary>
    /// The values of the game's own NPC-only sub-zone settings (<c>HTZ_Military_TV_Bunker_NPC</c>) the guard area's settings
    /// copy gets: one encounter at a time in the area (a zone of the island spawns several side by side).
    /// </summary>
    public static IReadOnlyList<(string Path, TunableKind Kind, string Value)> NpcOnlyValues { get; } =
    [
        ("SingleEncounterPerZone", TunableKind.Bool, "true"),
    ];

    /// <summary>
    /// Every armed NPC encounter of the game that a stock zone settings uses, with what it spawns, by name. Reads every
    /// encounter, preset and zone package: call it on a worker.
    /// </summary>
    public static IReadOnlyList<NpcGuardType> Types(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var zones = SpawnChoices.ZoneEncounters(catalog);
        var result = new List<NpcGuardType>();
        foreach (var encounter in SpawnChoices.EncounterCatalog(catalog).Where(e => e.Kind == SpawnCategory.Npcs))
        {
            var zone = zones.Where(z => z.Value.Contains(encounter.EncounterClass, StringComparer.OrdinalIgnoreCase)).Select(z => z.Key)
                .Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (zone is not null && SpawnChoices.PackageNamed(catalog, SpawnChoices.ZonesFolder, zone) is { } package)
            {
                result.Add(new NpcGuardType(encounter.EncounterClass, encounter.CharacterClasses.Select(SpawnChoices.ClassName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), package));
            }
        }

        return result.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// What the stock zone settings <paramref name="zonePackage"/> store of <see cref="NpcOnlyValues"/>, by property path (a
    /// value not stored is not listed). Reads the package: call it on a worker.
    /// </summary>
    public static IReadOnlyDictionary<string, string> StoredNpcOnlyValues(AssetCatalog catalog, string zonePackage)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(zonePackage);
        var leaf = zonePackage[(zonePackage.LastIndexOf('/') + 1)..];
        return TunableReader.Read(Modding.Catalog.ModdableAssets.ReadPackage(catalog, zonePackage))
            .Where(t => t.Export == leaf && NpcOnlyValues.Any(v => v.Path == t.Path))
            .ToDictionary(t => t.Path, t => t.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// <paramref name="count"/> posts spread evenly along <paramref name="path"/> (an open line through its points, both ends
    /// included); a path without length gives its first point once.
    /// </summary>
    public static IReadOnlyList<FVector> Posts(IReadOnlyList<FVector> path, int count)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
        {
            return [];
        }

        count = Math.Clamp(count, 1, SpawnPlaces.MaxGroupPosts);
        var legs = path.Zip(path.Skip(1), (a, b) => (A: a, B: b, Length: FVector.Distance(a, b))).ToList();
        var total = legs.Sum(l => l.Length);
        if (count == 1 || total < 1f)
        {
            return [path[0]];
        }

        var posts = new List<FVector>(count);
        for (var k = 0; k < count; k++)
        {
            var along = total * k / (count - 1);
            for (var l = 0; l < legs.Count; l++)
            {
                var (a, b, length) = legs[l];
                if (along <= length || l == legs.Count - 1)
                {
                    var t = length < 1e-3f ? 0f : Math.Clamp(along / length, 0f, 1f);
                    posts.Add(a + ((b - a) * t));
                    break;
                }

                along -= length;
            }
        }

        return posts;
    }

    /// <summary>
    /// The edits of one replacement (one undo step): each robot's spawner removed, a group of
    /// <paramref name="postsPerRobot"/> posts along each route, the guard area's own settings (<paramref name="zoneCopy"/>, a
    /// copy of <paramref name="type"/>'s stock zone settings: every other encounter of <paramref name="zoneRows"/> weighs 0,
    /// and <see cref="NpcOnlyValues"/> as the game's NPC-only settings have them), and the guard area over all the posts.
    /// The area is written the way the game's data has an NPC-only area inside a place: a sub-zone (<see cref="AddSubZoneOp"/>,
    /// priority <see cref="SubZonePriority"/>) of the smallest stock threat zone of <paramref name="zones"/> that holds every
    /// post (r39: a second zone laid inside the place's own zone, which no zone of the island does, gave igor8802 no NPCs).
    /// Only where no stock zone holds every post is it a threat zone of its own.
    /// </summary>
    /// <param name="routes">The robots to replace.</param>
    /// <param name="type">The NPCs to spawn.</param>
    /// <param name="postsPerRobot">Posts per robot (1 to <see cref="SpawnPlaces.MaxGroupPosts"/>).</param>
    /// <param name="zoneCopy">The copy of <see cref="NpcGuardType.ZonePackage"/> (<see cref="ZoneCopies.Plan"/>).</param>
    /// <param name="zoneRows">The encounter rows of the stock zone settings (<see cref="SpawnChoices.ForZone"/>, its level 0 list).</param>
    /// <param name="taken">Actor names the island data's document already has (the project's added places).</param>
    /// <param name="zones">The island's places as the project has them (<see cref="SpawnPlaces.WithEdits"/>); null or none = no stock zone to hold the area.</param>
    /// <param name="stockValues">The values the stock zone settings store, by property path (<see cref="NpcOnlyValues"/>' old values); null = none stored.</param>
    /// <exception cref="ArgumentException">No route, or the zone settings have no row for the encounter.</exception>
    public static IReadOnlyList<EditOp> Plan(IReadOnlyList<SentryRoute> routes, NpcGuardType type, int postsPerRobot, CloneAssetOp zoneCopy, SpawnWeightList zoneRows, IEnumerable<string> taken,
        IReadOnlyList<SpawnPlace>? zones = null, IReadOnlyDictionary<string, string>? stockValues = null)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0 || routes.Any(r => r.Path.Count == 0))
        {
            throw new ArgumentException("No sentry route to replace.", nameof(routes));
        }

        // Each robot goes and its route becomes a group of posts; the area and the settings are the shared placement plan.
        var groups = routes.Select(r => new NpcPostGroup(Posts(r.Path, postsPerRobot), new DeleteActorOp(r.Spawner))).ToList();
        return NpcPlacement.Plan(groups, type, zoneCopy, zoneRows, taken, zones, stockValues);
    }
}
