using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Editing;
using ScumStudio.Modding.Tuning;
using TransformValue = ScumStudio.Level.Model.TransformValue;

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
/// posts along the route (the armed NPC encounters spawn as a group at such points), and a threat zone over the route gets
/// its own copy of zone settings whose only weighted encounter is the chosen NPC one. Whether the game spawns the guards at
/// those posts, and how a zone inside the place's own zone is treated, only the game shows.
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
    /// <paramref name="postsPerRobot"/> posts along each route, the guard zone's own settings (<paramref name="zoneCopy"/>, a
    /// copy of <paramref name="type"/>'s stock zone settings: every other encounter of <paramref name="zoneRows"/> weighs
    /// 0), and the guard zone over all the posts.
    /// </summary>
    /// <param name="routes">The robots to replace.</param>
    /// <param name="type">The NPCs to spawn.</param>
    /// <param name="postsPerRobot">Posts per robot (1 to <see cref="SpawnPlaces.MaxGroupPosts"/>).</param>
    /// <param name="zoneCopy">The copy of <see cref="NpcGuardType.ZonePackage"/> (<see cref="ZoneCopies.Plan"/>).</param>
    /// <param name="zoneRows">The encounter rows of the stock zone settings (<see cref="SpawnChoices.ForZone"/>, its level 0 list).</param>
    /// <param name="taken">Actor names the island data's document already has (the project's added places).</param>
    /// <exception cref="ArgumentException">No route, or the zone settings have no row for the encounter.</exception>
    public static IReadOnlyList<EditOp> Plan(IReadOnlyList<SentryRoute> routes, NpcGuardType type, int postsPerRobot, CloneAssetOp zoneCopy, SpawnWeightList zoneRows, IEnumerable<string> taken)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(zoneCopy);
        ArgumentNullException.ThrowIfNull(zoneRows);
        ArgumentNullException.ThrowIfNull(taken);
        if (routes.Count == 0 || routes.Any(r => r.Path.Count == 0))
        {
            throw new ArgumentException("No sentry route to replace.", nameof(routes));
        }

        var own = zoneRows.Entries.Where(e => e.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase) && e.Path is not null).ToList();
        if (own.Count == 0 || !zoneRows.IsEditable)
        {
            throw new ArgumentException($"{zoneRows.Name} has no stored row for {type.Name}.", nameof(zoneRows));
        }

        var names = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        string Name(SpawnPlaceKind kind)
        {
            var name = SpawnPlaces.NewName(kind, names);
            names.Add(name);
            return name;
        }

        var ops = new List<EditOp>();
        var all = new List<FVector>();
        foreach (var route in routes)
        {
            ops.Add(new DeleteActorOp(route.Spawner));
            var posts = Posts(route.Path, postsPerRobot);
            all.AddRange(posts);
            ops.Add(new AddSpawnPlaceOp(Name(SpawnPlaceKind.CharacterGroup), SpawnPlaceKind.CharacterGroup, PostType,
                new TransformValue(posts[0], FRotator.Zero, FVector.One), Points: posts.Select(p => p - posts[0]).ToList()));
        }

        // The guard zone's own settings: only the chosen NPC encounter keeps a weight.
        ops.Add(zoneCopy);
        var export = zoneCopy.NewPrimary[(zoneCopy.NewPrimary.LastIndexOf('/') + 1)..];
        foreach (var row in zoneRows.Entries.Where(e => e.Path is not null))
        {
            var mine = own.Contains(row);
            var weight = mine ? (row.Weight > 0f ? row.Weight : 100f) : 0f;
            if (weight != row.Weight)
            {
                ops.Add(new SetAssetValueOp(zoneCopy.NewPrimary, export, row.Path!, nameof(TunableKind.Float), TunableValue.Format(row.Weight), TunableValue.Format(weight)));
            }
        }

        var (min, max) = (new FVector(all.Min(p => p.X), all.Min(p => p.Y), all.Min(p => p.Z)), new FVector(all.Max(p => p.X), all.Max(p => p.Y), all.Max(p => p.Z)));
        // A zone is an ellipse: through the corners of the posts' box (its half sizes times the square root of 2), and a margin.
        var center = (min + max) * 0.5f;
        var half = new FVector(MathF.Max(((max.X - min.X) * 0.5f * MathF.Sqrt(2f)) + ZoneMargin, MinZoneHalfSize), MathF.Max(((max.Y - min.Y) * 0.5f * MathF.Sqrt(2f)) + ZoneMargin, MinZoneHalfSize), 1f);
        ops.Add(new AddSpawnPlaceOp(Name(SpawnPlaceKind.Zone), SpawnPlaceKind.Zone, zoneCopy.NewPrimary,
            new TransformValue(center, FRotator.Zero, new FVector(half.X / 100f, half.Y / 100f, 1f)))); // a zone's size is its scale in metres
        return ops;
    }
}
