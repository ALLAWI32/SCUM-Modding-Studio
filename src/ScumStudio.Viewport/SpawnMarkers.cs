using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Viewport;

/// <summary>The 3D shape a spawn marker is drawn as.</summary>
public enum SpawnShape
{
    /// <summary>A small upright pin, tip on the spawn place (zones' and loot zones' centres, drop zones).</summary>
    Pin,

    /// <summary>A person-sized translucent capsule standing on the place (sentries, patrol points, zombies, creatures, traders).</summary>
    Capsule,

    /// <summary>A translucent box: a car shop's vehicle box at its real size, a car-sized box at a world vehicle spawn.</summary>
    Box,

    /// <summary>A small translucent crate at a loot point.</summary>
    Crate,

    /// <summary>A flat band around a zone or hunting area.</summary>
    Ring,
}

/// <summary>What a spawn marker stands for.</summary>
public enum SpawnKind
{
    /// <summary>A loot point: one place an item spawner puts loot (<see cref="SpawnMarker"/>).</summary>
    Loot,

    /// <summary>A loot zone (item spawner volume): changes the chance and kind of loot of the spawners inside it.</summary>
    LootZone,

    /// <summary>Sentry spawner: where a sentry stands.</summary>
    Sentry,

    /// <summary>A point of a sentry's patrol path.</summary>
    Patrol,

    /// <summary>Creature spawn point in the bunkers (Razors, kill boxes, tagged spawners).</summary>
    Creature,

    /// <summary>Where a car shop puts the vehicles it sells.</summary>
    Vehicle,

    /// <summary>Where players can drop in (event drop zones).</summary>
    PlayerDrop,

    /// <summary>A world vehicle spawn point of the island (cars, bikes, boats, planes; <see cref="SpawnPlaceKind.Vehicle"/>).</summary>
    VehiclePlace,

    /// <summary>A zombie / NPC spawn point of the island.</summary>
    Zombie,

    /// <summary>The centre of a threat zone.</summary>
    Zone,

    /// <summary>The outline of a threat zone (its ellipse).</summary>
    ZoneRing,

    /// <summary>The centre of a hunting area.</summary>
    Animal,

    /// <summary>The outline of a hunting area (its circle).</summary>
    AnimalRing,

    /// <summary>Where a trader (an outpost NPC that buys and sells) stands; picks as its trade post.</summary>
    Trader,

    /// <summary>A light, fire, smoke, sound or fog: an actor with nothing to draw but an effect (picks as the actor).</summary>
    Effect,

    /// <summary>Any other actor with no model of its own (an NPC, a quest marker, a trigger): a grey pin where it stands.</summary>
    Marker,
}

/// <summary>
/// The game's spawn places, drawn as coloured markers (half-transparent 3D stand-ins: a capsule where a person spawns, a
/// box where a car does, a crate at a loot point; pins and rings for zones, see <see cref="ShapeOf"/>): actors that only
/// spawn things (no mesh of their own) get a marker that picks and moves like any object; loot points inside buildings get
/// markers that show where they are (they move with their building); a building's fixed-item spawners and vehicle boxes
/// pick and move as parts of it (<see cref="IsSpawnPart"/>); the points of a stored point array (a sentry's patrol path, a
/// spawner group's loot points, <see cref="SpawnPointArrays"/>) pick, move, copy and delete on their own. The markers are
/// generated meshes (<see cref="MeshKey"/>), so the scene pipeline draws, culls and picks them like the game's meshes.
/// Where the object that spawns is known (<see cref="SpawnModels"/>: a vehicle, a zombie, a sentry, a trader's NPC, a loot
/// preset's item) the pin is that object's mesh, half transparent (<see cref="StandIn"/>); it picks and moves like the pin.
/// </summary>
public static partial class SpawnMarkers
{
    /// <summary>Prefix of the marker mesh keys.</summary>
    public const string Prefix = "#spawn/";

    /// <summary>Edge of the unit box mesh (cm), centred on its place; a vehicle box is scaled from it to its real size.</summary>
    public const float BoxSize = 100f;

    /// <summary>Opacity of the 3D stand-ins (owner: "translucent 3D shapes, not flat pins").</summary>
    public const float StandInAlpha = 0.5f;

    // Small pins (owner: "a small pin, not a big one"): a hand wide, a bit over a metre tall.
    private const float Width = 40f;
    private const float Height = 120f;
    private const float RingRadius = 100f;

    // A person: shoulder-wide, 1.8 m tall; a car: 4.5 x 2 x 1.6 m; a loot crate: 40 cm.
    private const float CapsuleRadius = 35f;
    private const float CapsuleHeight = 180f;
    private static readonly FVector CarSize = new(450f, 200f, 160f);
    private const float CrateSize = 40f;

    /// <summary>The shape <paramref name="kind"/> is drawn as.</summary>
    public static SpawnShape ShapeOf(SpawnKind kind) => kind switch
    {
        SpawnKind.Sentry or SpawnKind.Patrol or SpawnKind.Zombie or SpawnKind.Creature or SpawnKind.Trader => SpawnShape.Capsule,
        SpawnKind.Vehicle or SpawnKind.VehiclePlace => SpawnShape.Box,
        SpawnKind.Loot => SpawnShape.Crate,
        SpawnKind.ZoneRing or SpawnKind.AnimalRing => SpawnShape.Ring,
        _ => SpawnShape.Pin,
    };

    /// <summary>Bright pin colours per kind (linear RGBA), never white.</summary>
    public static Vector4 Color(SpawnKind kind) => kind switch
    {
        SpawnKind.Loot => new(1f, 0.72f, 0.08f, 1f),
        SpawnKind.LootZone => new(1f, 0.4f, 0.03f, 1f),
        SpawnKind.Sentry => new(1f, 0.1f, 0.08f, 1f),
        SpawnKind.Patrol => new(1f, 0.38f, 0.6f, 1f),
        SpawnKind.Creature => new(0.62f, 0.18f, 1f, 1f),
        SpawnKind.Vehicle => new(0.08f, 0.78f, 1f, 1f),
        SpawnKind.VehiclePlace => new(0.05f, 0.45f, 1f, 1f),
        SpawnKind.Zombie => new(0.55f, 1f, 0.05f, 1f),
        SpawnKind.Zone or SpawnKind.ZoneRing => new(1f, 0.05f, 0.35f, 1f),
        SpawnKind.Animal or SpawnKind.AnimalRing => new(0.3f, 0.15f, 0.05f, 1f), // brown, apart from the orange loot zones
        SpawnKind.Trader => new(0f, 1f, 0.72f, 1f),
        SpawnKind.Effect => new(1f, 0.55f, 0.15f, 1f),
        SpawnKind.Marker => new(0.75f, 0.75f, 0.8f, 1f),
        _ => new(0.2f, 1f, 0.35f, 1f),
    };

    /// <summary>Separates the kind from the model in a stand-in key (<c>#spawn/Zombie@/Game/.../SK_Zombie.SK_Zombie</c>, <c>#spawn/VehiclePlace@/Game/.../BPC_Rager</c>).</summary>
    public const char ModelSeparator = '@';

    /// <summary>Share of the kind's colour tinted into a model stand-in (a vehicle still looks like a vehicle, with a blue cast).</summary>
    public const float ModelTintShare = 0.25f;

    /// <summary>
    /// The marker mesh key of <paramref name="kind"/>: the generated pin or shape, or with <paramref name="model"/> (a mesh
    /// object path or a vehicle Blueprint package, see <see cref="SpawnModels"/>) that model drawn as a translucent stand-in
    /// (<see cref="StandIn"/>).
    /// </summary>
    public static string MeshKey(SpawnKind kind, string? model = null) => model is null ? Prefix + kind : Prefix + kind + ModelSeparator + model;

    /// <summary>The model of a stand-in key (<see cref="MeshKey"/>), or null for a plain marker or any other mesh.</summary>
    public static string? ModelOf(string meshPath)
    {
        ArgumentNullException.ThrowIfNull(meshPath);
        var at = IsMarker(meshPath) ? meshPath.IndexOf(ModelSeparator, Prefix.Length) : -1;
        return at > 0 && at + 1 < meshPath.Length ? meshPath[(at + 1)..] : null;
    }

    /// <summary>True for a marker mesh key.</summary>
    public static bool IsMarker(string meshPath) => meshPath.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>True for an actor that is only a loot spawner (its loot points pick and move as the actor).</summary>
    public static bool IsLootSpawner(ActorRecord actor) => actor.ClassName is "ItemSpawnerGroup" or "WorldItemSpawner";

    // Actors that stand for the level itself, not for a thing in it: never a pin.
    private static readonly HashSet<string> StructuralClasses = new(StringComparer.Ordinal)
    {
        "WorldSettings", "ConZWorldSettings", "LevelBounds", "LevelScriptActor", "InstancedFoliageActor", "Brush", "DefaultPhysicsVolume",
        "NavMeshBoundsVolume", "RecastNavMesh", "DistantLevelManager", "WorldComposition", "SphereReflectionCapture", "BoxReflectionCapture",
        "PlanarReflection", "LightmassImportanceVolume", "PrecomputedVisibilityVolume", "CullDistanceVolume", "WorldPartitionMiniMap",
    };

    /// <summary>
    /// True for an actor the map had nothing to draw for: no mesh of its own, no instances, no spawn or trader pin. A fire,
    /// a lamp, an NPC (Hektor, Discord: "if you delete a camp, fire animations remain behind, the tool doesn't show them; the
    /// traders stay put"). <paramref name="effect"/>: it is a light, particle, sound or fog, not something else.
    /// </summary>
    public static bool IsPinOnly(ActorRecord actor, out bool effect)
    {
        ArgumentNullException.ThrowIfNull(actor);
        effect = false;
        if (actor.RootComponent is null || actor.InstanceTransforms.Count > 0 || actor.TraderMarkers.Count > 0 || IsLootSpawner(actor) || KindOf(actor) is not null
            || actor.Kind is ActorKind.StaticMeshActor or ActorKind.Volume || actor.ClassName.StartsWith("Landscape", StringComparison.Ordinal)
            || StructuralClasses.Contains(actor.ClassName) || actor.Components.Any(c => c.StaticMeshPath is not null && c.IsVisible && !c.IsInstanced)
            || actor.Components.Any(c => c.SpawnMarkers.Count > 0))
        {
            return false;
        }

        effect = actor.Kind == ActorKind.Light || actor.Components.Any(c => IsEffectClass(c.ClassName)) || IsEffectClass(actor.ClassName);
        return true;
    }

    private static bool IsEffectClass(string className) =>
        className.Contains("Light", StringComparison.Ordinal) || className.Contains("Particle", StringComparison.Ordinal)
        || className.Contains("Niagara", StringComparison.Ordinal) || className.Contains("Audio", StringComparison.Ordinal)
        || className.Contains("Fog", StringComparison.Ordinal) || className.Contains("Emitter", StringComparison.Ordinal)
        || className.Contains("Fire", StringComparison.Ordinal) || className.Contains("Smoke", StringComparison.Ordinal);

    /// <summary>
    /// True for a spawn component a building stores that picks and moves on its own, as a part: a world item spawner of one
    /// fixed item (a house's drill press, stove or fridge) or a car shop's vehicle box (Discord igor: "there is no
    /// interaction with certain spawn objects"). Not the actor's root: that is the actor itself.
    /// </summary>
    public static bool IsSpawnPart(ActorRecord actor, ComponentRecord component)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(component);
        return component is { IsSynthesized: false, ExportIndex: >= 0 } && component.ExportIndex != actor.RootComponent
            && (component.ClassName == "VehicleSpawnBoxComponent" || component.SpawnMarkers is [{ ItemClassPath: not null }]);
    }

    /// <summary>
    /// The spawn kind of an actor that only spawns things (a pin at the actor), or null. Loot spawners are not here: their
    /// pins are their loot points.
    /// </summary>
    public static SpawnKind? KindOf(ActorRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (SpawnPlaces.KindOfClass(actor.ClassName) is { } place)
        {
            return place switch
            {
                SpawnPlaceKind.Vehicle => SpawnKind.VehiclePlace,
                SpawnPlaceKind.Zone => SpawnKind.Zone,
                SpawnPlaceKind.Animal => SpawnKind.Animal,
                _ => SpawnKind.Zombie,
            };
        }

        return actor.ClassName switch
        {
            "ItemSpawnerVolume" => SpawnKind.LootZone,
            "SentrySpawner2" => SpawnKind.Sentry,
            "BP_RazorSpawnPoint_C" or "BP_EasyKillBoxSpawnPoint_C" or "BP_KillBoxC4SpawnPoint_C" or "BP_TaggedSpawner_C" => SpawnKind.Creature,
            "BP_DropZoneLocationMarker_C" => SpawnKind.PlayerDrop,
            _ => null,
        };
    }

    /// <summary>
    /// Every pin of <paramref name="actor"/>: (kind, where, whether it picks as the actor, label, for a building's loot
    /// point its spawner component and marker index, for a spawn part (<see cref="IsSpawnPart"/>) its component, and for a
    /// point of a stored point array (<see cref="SpawnPointArrays"/>) the array's key and the point's index, and the model
    /// that stands there, <see cref="SpawnModels"/>, or null for the plain shape). Loot points come from the item spawners'
    /// markers (a building's pick as themselves; a stored array's pick as points of their own), a sentry spawner adds its
    /// patrol points (points of their own when stored), a car shop its vehicle boxes (the box's real size, or the vehicle on
    /// its floor), the other spawners stand at the actor. With <paramref name="models"/> a pin whose object is known is that
    /// object, unscaled and turned like the place (<see cref="ModelAt"/>); without, every pin is its generated shape.
    /// </summary>
    public static IEnumerable<(SpawnKind Kind, FTransform Pin, bool PicksActor, string Label, (string Component, int Index)? Marker, ComponentRecord? Part, (string Key, int Index)? Point, string? Model)> PinsOf(ActorRecord actor, SpawnModels? models = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var lootSpawner = IsLootSpawner(actor);
        var stored = SpawnPointArrays.Of(actor).Select(a => a.Component).ToList();
        var points = 0;
        foreach (var component in actor.Components.Where(c => c.IsSceneComponent))
        {
            var part = IsSpawnPart(actor, component) ? component : null;
            var editable = stored.Contains(component.Name, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < component.SpawnMarkers.Count; i++)
            {
                var m = component.SpawnMarkers[i];
                points++;
                var (lootPin, lootModel) = At(SpawnKind.Loot, m.Local * component.WorldTransform, inside: !lootSpawner, marker: m);
                yield return (SpawnKind.Loot, lootPin, lootSpawner && !editable,
                    $"{actor.Name}/{component.Name} [{i}] {m.Preset} {m.Probability:0.#}% x{m.MinQuantity}-{m.MaxQuantity}",
                    lootSpawner || part is not null || editable ? null : (component.Name, i), part, editable ? (component.Name, i) : null, lootModel);
            }

            if (component.ClassName == "VehicleSpawnBoxComponent")
            {
                // The box at its real size (the extent is a half size, the box mesh a metre): what the shop puts a car into.
                var w = component.WorldTransform;
                var extent = component.BoxExtent ?? new FVector(32f, 32f, 32f);
                var box = new FTransform(w.Rotation, w.Translation, w.Scale3D * extent * (2f / BoxSize));
                var vehicle = models?.ModelOf(SpawnKind.Vehicle, actor);
                yield return (SpawnKind.Vehicle, vehicle is null ? box : ModelAt(SpawnKind.Vehicle, box), false, $"{actor.Name}/{component.Name}", null, part, null, vehicle);
            }
        }

        // A trade post's traders stand where its NPCs do; the pins pick the trade post (move, copy, delete it whole).
        foreach (var trader in actor.TraderMarkers)
        {
            var (traderPin, npc) = At(SpawnKind.Trader, trader.Local * actor.WorldTransform, trader: trader);
            yield return (SpawnKind.Trader, traderPin, true, TraderLabel(trader), null, null, null, npc);
        }

        if (lootSpawner && points == 0)
        {
            yield return (SpawnKind.Loot, PinAt(actor.WorldTransform, SpawnKind.Loot), true, actor.Name, null, null, null, null);
        }

        if (IsPinOnly(actor, out var effect))
        {
            yield return (effect ? SpawnKind.Effect : SpawnKind.Marker, PinAt(actor.WorldTransform, effect ? SpawnKind.Effect : SpawnKind.Marker), true, actor.Name, null, null, null, null);
        }

        if (KindOf(actor) is { } kind)
        {
            var (pin, model) = At(kind, actor.WorldTransform);
            yield return (kind, pin, true, actor.Name, null, null, null, model);
            if (kind is SpawnKind.Zone or SpawnKind.Animal)
            {
                // The zone's ellipse (the root's scale is its size in metres) or the area's circle, flat around the centre.
                var t = actor.WorldTransform;
                var yaw = FQuat.MakeFromEuler(new FVector(0f, 0f, t.Rotation.Rotator().Yaw));
                yield return (kind == SpawnKind.Zone ? SpawnKind.ZoneRing : SpawnKind.AnimalRing, new FTransform(yaw, t.Translation, new FVector(t.Scale3D.X, t.Scale3D.Y, 1f)), true, actor.Name, null, null, null, null);
            }

            var patrol = stored.Contains(null);
            for (var i = 0; i < actor.PatrolPoints.Count; i++)
            {
                var at = new FTransform(actor.WorldTransform.TransformPosition(actor.PatrolPoints[i]));
                var (patrolPin, sentry) = At(SpawnKind.Patrol, at, inside: true);
                yield return (SpawnKind.Patrol, patrolPin, !patrol, $"{actor.Name} patrol {i + 1}", null, null, patrol ? (SpawnPointArrays.PatrolPoints, i) : null, sentry);
            }
        }

        // The pin's shape at the place, or the model that stands there (unscaled, its foot on the place).
        (FTransform Pin, string? Model) At(SpawnKind kind, FTransform world, bool inside = false, SpawnMarker? marker = null, TraderMarker? trader = null)
        {
            var pin = PinAt(world, kind, inside);
            var model = models?.ModelOf(kind, actor, marker, trader);
            return (model is null ? pin : ModelAt(kind, pin), model);
        }
    }

    /// <summary>The kind a marker mesh key draws (<see cref="MeshKey"/>, plain or with a model), or null for any other mesh.</summary>
    public static SpawnKind? KindOfMesh(string meshPath)
    {
        ArgumentNullException.ThrowIfNull(meshPath);
        if (!IsMarker(meshPath))
        {
            return null;
        }

        var at = meshPath.IndexOf(ModelSeparator, Prefix.Length);
        return Enum.TryParse<SpawnKind>(at < 0 ? meshPath[Prefix.Length..] : meshPath[Prefix.Length..at], out var kind) ? kind : null;
    }

    /// <summary>
    /// What a spawn actor is, for the properties panel: a text key and its arguments (<c>Spawn.Loot</c>: loot points and
    /// presets, <c>Spawn.Building</c>, <c>Spawn.Sentry</c>: patrol points, ...), or null for other actors.
    /// </summary>
    public static (string Key, object[] Args)? Describe(ActorRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var markers = actor.Components.SelectMany(c => c.SpawnMarkers).ToList();
        var presets = string.Join(", ", markers.GroupBy(m => m.Preset.Length > 0 ? m.Preset : "?").OrderByDescending(g => g.Count()).Select(g => $"{g.Key} x{g.Count()}"));
        if (IsLootSpawner(actor))
        {
            return ("Spawn.Loot", [markers.Count, presets]);
        }

        if (markers.Count > 0)
        {
            return ("Spawn.Building", [markers.Count, presets]);
        }

        if (actor.TraderMarkers.Count > 0)
        {
            return ("Spawn.Trader", [string.Join(", ", actor.TraderMarkers.Select(t => $"{t.Name} ({t.Type})")),
                string.Join(", ", actor.TraderMarkers.Select(t => NpcName(t.NpcClass)))]);
        }

        if (IsPinOnly(actor, out var effect))
        {
            var parts = string.Join(", ", actor.Components.Where(c => c.IsSceneComponent && c.ExportIndex != actor.RootComponent).Select(c => c.ClassName).Distinct().Take(4));
            return (effect ? "Spawn.Effect" : "Spawn.Marker", [actor.ClassName.EndsWith("_C", StringComparison.Ordinal) ? actor.ClassName[..^2] : actor.ClassName, parts]);
        }

        var boxes = actor.Components.Count(c => c.ClassName == "VehicleSpawnBoxComponent");
        var (sizeX, sizeY) = SpawnPlaces.SizeOf(actor.WorldTransform.Scale3D);
        return KindOf(actor) switch
        {
            SpawnKind.VehiclePlace => ("Spawn.VehiclePlace", [actor.ClassPath]),
            SpawnKind.Zombie => ("Spawn.Zombie", [actor.ClassPath]),
            SpawnKind.Zone => ("Spawn.Zone", [actor.ClassPath, Math.Round(sizeX / 100f), Math.Round(sizeY / 100f)]),
            SpawnKind.Animal => ("Spawn.Animal", [actor.ClassPath, Math.Round(sizeX / 100f)]),
            SpawnKind.LootZone => ("Spawn.LootZone", []),
            SpawnKind.Sentry => ("Spawn.Sentry", [actor.PatrolPoints.Count]),
            SpawnKind.Creature => ("Spawn.Creature", []),
            SpawnKind.PlayerDrop => ("Spawn.PlayerDrop", []),
            _ => boxes > 0 ? ("Spawn.CarShop", [boxes]) : null,
        };
    }

    /// <summary>
    /// Where a marker stands for a spawn at <paramref name="world"/>: a capsule or crate upright and unscaled, turned the
    /// place's way (its yaw; the loot crates inside buildings, <paramref name="inside"/>, smaller so a house full of them
    /// stays readable), a car-sized box standing on the place and turned like it, a pin upright and unscaled (drop zones larger).
    /// </summary>
    public static FTransform PinAt(FTransform world, SpawnKind kind, bool inside = false)
    {
        switch (ShapeOf(kind))
        {
            case SpawnShape.Capsule:
                return new(Yaw(world), world.Translation, FVector.One);
            case SpawnShape.Crate:
                return new(Yaw(world), world.Translation, new FVector(inside ? 0.7f : 1f));
            case SpawnShape.Box:
                return new(world.Rotation, world.Translation + world.Rotation.RotateVector(new FVector(0f, 0f, CarSize.Z / 2f)), CarSize / BoxSize);
            default:
                var size = kind == SpawnKind.PlayerDrop ? 4f : kind is SpawnKind.Zone or SpawnKind.Animal ? 3f : inside ? 0.7f : 1f;
                return new(FQuat.Identity, world.Translation, new FVector(size, size, size));
        }

        static FQuat Yaw(FTransform t) => FQuat.MakeFromEuler(new FVector(0f, 0f, t.Rotation.Rotator().Yaw));
    }

    /// <summary>
    /// Where a model stands for the pin <paramref name="pin"/> (<see cref="PinAt"/>): unscaled and turned like it, its foot
    /// on the place; for a box (a car shop's vehicle box, a world vehicle spawn) on the box's floor.
    /// </summary>
    public static FTransform ModelAt(SpawnKind kind, FTransform pin) => ShapeOf(kind) == SpawnShape.Box
        ? new(pin.Rotation, pin.Translation - pin.Rotation.RotateVector(new FVector(0f, 0f, pin.Scale3D.Z * BoxSize / 2f)), FVector.One)
        : kind == SpawnKind.Trader
            // The game turns a trader to face away from its marker's forward (the owner: the banker stood with his back to
            // the counter in the studio and the right way in game), so the stand-in turns half round.
            ? new(pin.Rotation * new FRotator(0f, 180f, 0f).Quaternion(), pin.Translation, FVector.One)
            : new(pin.Rotation, pin.Translation, FVector.One);

    /// <summary>The NPC's Blueprint name from its class path (<c>BP_ArmsDealer_01</c>).</summary>
    /// <summary>
    /// The label a trader's card shows: "Trader", the type in words and the sector its game name starts with
    /// (<c>B_4_Armory</c>/Armorer → "Trader Armorer B_4"; the bank, whose marker has no personality, "Trader Bank").
    /// The game name itself stays in the details (the server's EconomyOverride.json lists traders by it).
    /// </summary>
    public static string TraderLabel(TraderMarker trader)
    {
        ArgumentNullException.ThrowIfNull(trader);
        var type = trader.Type.Length > 0 ? TypeWords(trader.Type) : "Bank";
        var sector = SectorPrefix().Match(trader.Name);
        return sector.Success ? $"Trader {type} {sector.Groups[1].Value}" : $"Trader {type}";
    }

    /// <summary>"GeneralGoods" → "General goods", "MasterHunter" → "Master hunter".</summary>
    private static string TypeWords(string type)
    {
        var words = new System.Text.StringBuilder(type.Length + 4);
        for (var i = 0; i < type.Length; i++)
        {
            if (i > 0 && char.IsUpper(type[i]) && !char.IsUpper(type[i - 1]))
            {
                words.Append(' ').Append(char.ToLowerInvariant(type[i]));
            }
            else
            {
                words.Append(type[i]);
            }
        }

        return words.ToString();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^([A-Z]_\d+)_")]
    private static partial System.Text.RegularExpressions.Regex SectorPrefix();

    public static string NpcName(string npcClass)
    {
        var name = npcClass[(npcClass.LastIndexOf('.') + 1)..];
        return name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name;
    }

    /// <summary>
    /// The marker mesh for <paramref name="kind"/> in its colour: a pin (point down at the spawn place), a ring, or a
    /// half-transparent 3D stand-in (<see cref="ShapeOf"/>; the renderer draws tints with alpha below 1 blended after
    /// everything opaque, and they pick like any object).
    /// </summary>
    public static PreparedMeshAsset Asset(SpawnKind kind)
    {
        var key = MeshKey(kind);
        var shape = ShapeOf(kind);
        var mesh = shape switch
        {
            SpawnShape.Ring => Ring(key),
            SpawnShape.Capsule => Capsule(key),
            SpawnShape.Box => Cube(key, "SpawnBox", new Vector3(-BoxSize / 2f), new Vector3(BoxSize / 2f)),
            SpawnShape.Crate => Cube(key, "SpawnCrate", new Vector3(-CrateSize / 2f, -CrateSize / 2f, 0f), new Vector3(CrateSize / 2f, CrateSize / 2f, CrateSize)),
            _ => Pin(key),
        };
        var tint = shape is SpawnShape.Pin or SpawnShape.Ring ? Color(kind) : Color(kind) with { W = StandInAlpha };
        return new PreparedMeshAsset(key, mesh, null)
        {
            MaterialSlots = [key],
            MaterialTints = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase) { [key] = tint },
            Shimmer = true,
        };
    }

    /// <summary>Prefix of a stand-in model that is an icon texture, not a mesh (<c>icon:/Game/.../ICO_X.ICO_X</c>): a loot point whose item has no mesh.</summary>
    public const string IconPrefix = "icon:";

    /// <summary>Height of an icon card (cm): the loot crate's size.</summary>
    public const float IconSize = CrateSize;

    /// <summary>
    /// The inventory icon of a loot point's item as a stand-in under <paramref name="key"/>: a card <see cref="IconSize"/>
    /// tall (wide by the icon's aspect) standing on the place, drawn facing the camera (<see cref="PreparedMeshAsset.Billboard"/>)
    /// with the icon texture's transparent background cut out; <see cref="StandIn"/> tints it like any stand-in.
    /// </summary>
    public static PreparedMeshAsset IconAsset(string key, string texturePath, int width, int height)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(texturePath);
        var w = IconSize * (width > 0 && height > 0 ? (float)width / height : 1f);
        var h = IconSize;
        // Mesh x spans the camera's right, mesh z (GL up) the camera's up; texture origin top-left, V down.
        float[] positions = [-w / 2f, 0f, h, w / 2f, 0f, h, w / 2f, 0f, 0f, -w / 2f, 0f, 0f];
        float[] normals = [0f, -1f, 0f, 0f, -1f, 0f, 0f, -1f, 0f, 0f, -1f, 0f];
        float[] uvs = [0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f];
        var mesh = MeshData.Create("SpawnIcon", positions, [0u, 1u, 2u, 0u, 2u, 3u], normals, uvs, [new MeshSection(key, 0, 6)]);
        return new PreparedMeshAsset(key, mesh, texturePath)
        {
            MaterialSlots = [key],
            MaterialTextures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = texturePath },
            MaterialAlphaCutoffs = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase) { [key] = 0.5f },
            Shimmer = true,
            Billboard = true,
        };
    }

    /// <summary>
    /// The generated asset for a marker mesh key, or null when it is none. For a stand-in key with a model this is the
    /// kind's plain shape under that key: what is drawn when the model cannot be loaded (<see cref="LevelScenePreparer"/>
    /// loads the model); it keeps the requested key so the viewport finds it by the placement's path.
    /// </summary>
    public static PreparedMeshAsset? AssetFor(string meshPath) => KindOfMesh(meshPath) is { } kind ? Asset(kind) with { MeshPath = meshPath } : null;

    /// <summary>
    /// <paramref name="model"/> as the stand-in <paramref name="key"/> for a spawn of <paramref name="kind"/>: every LOD
    /// lifted so its lowest point is at the place (foot on the ground), every material half transparent
    /// (<see cref="StandInAlpha"/>) with a quarter of the kind's colour tinted in (<see cref="ModelTintShare"/>; textured
    /// materials keep their texture under the tint), shimmering (<see cref="PreparedMeshAsset.Shimmer"/>).
    /// </summary>
    public static PreparedMeshAsset StandIn(PreparedMeshAsset model, SpawnKind kind, string key)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrEmpty(key);
        var lift = model.Mesh.Bounds.IsEmpty ? 0f : -model.Mesh.Bounds.Min.Z;
        var lods = model.Lods.Select(lod => Lift(lod, lift)).ToList();
        var colour = Color(kind);
        var tints = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in lods.SelectMany(l => l.Sections).Select(s => s.MaterialName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var own = model.MaterialTints.TryGetValue(material, out var t) ? t : Vector4.One;
            // Hair, beard and eyelash cards draw a white coverage mask coloured only by their tint: the kind's colour would
            // turn them teal, so they keep their own dark tint (the rest of the figure carries the kind's hint).
            var groom = model.MaterialTextures.TryGetValue(material, out var texture) && texture.EndsWith("#mask", StringComparison.Ordinal);
            tints[material] = (groom ? own : Vector4.Lerp(own, colour, ModelTintShare)) with { W = StandInAlpha };
        }

        return model with { MeshPath = key, Mesh = lods[0], Lods = lods, MaterialTints = tints, IsEditorOnly = false, Shimmer = true };
    }

    private static MeshData Lift(MeshData mesh, float lift)
    {
        if (lift == 0f)
        {
            return mesh;
        }

        var positions = (float[])mesh.Positions.Clone();
        for (var i = 2; i < positions.Length; i += 3)
        {
            positions[i] += lift;
        }

        var up = new Vector3(0f, 0f, lift);
        return mesh with { Positions = positions, Bounds = new BoundingBox(mesh.Bounds.Min + up, mesh.Bounds.Max + up) };
    }

    // An upright band around the centre, radius 1 m (scaled to the zone's size), from 1 m below to 3 m above it so it
    // shows over hills and hollows; both sides drawn.
    private static MeshData Ring(string material)
    {
        const int segments = 96;
        var faces = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        for (var i = 0; i < segments; i++)
        {
            var a0 = i * MathF.Tau / segments;
            var a1 = (i + 1) * MathF.Tau / segments;
            Vector3 p0 = new(MathF.Cos(a0) * RingRadius, MathF.Sin(a0) * RingRadius, -100f), p1 = new(MathF.Cos(a1) * RingRadius, MathF.Sin(a1) * RingRadius, -100f);
            Vector3 q0 = p0 with { Z = 300f }, q1 = p1 with { Z = 300f };
            faces.Add((p0, p1, q1));
            faces.Add((p0, q1, q0));
            faces.Add((p0, q1, p1));
            faces.Add((p0, q0, q1));
        }

        return Build("SpawnRing", faces, material, new BoundingBox(new Vector3(-RingRadius, -RingRadius, -100f), new Vector3(RingRadius, RingRadius, 300f)));
    }

    // A stretched octahedron: tip on the spawn place, widest at two thirds of the height; flat faces (own vertices).
    private static MeshData Pin(string material)
    {
        var w = Width / 2;
        Vector3 tip = new(0, 0, 0), top = new(0, 0, Height);
        Vector3[] ring = [new(w, 0, Height * 0.66f), new(0, w, Height * 0.66f), new(-w, 0, Height * 0.66f), new(0, -w, Height * 0.66f)];
        var faces = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        for (var i = 0; i < 4; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % 4];
            faces.Add((tip, b, a));
            faces.Add((top, a, b));
        }

        return Build("SpawnPin", faces, material, new BoundingBox(new Vector3(-w, -w, 0), new Vector3(w, w, Height)));
    }

    // A person standing on the place: a cylinder with a half sphere on each end, flat faces (own vertices), outward normals.
    private static MeshData Capsule(string material)
    {
        const int segments = 12;
        const int capRings = 4;
        const float r = CapsuleRadius;
        var profile = new List<(float R, float Z)>();
        for (var i = 0; i <= capRings; i++)
        {
            var a = (-MathF.PI / 2f) + (i * (MathF.PI / 2f) / capRings);
            profile.Add((r * MathF.Cos(a), r + (r * MathF.Sin(a))));
        }

        for (var i = 0; i <= capRings; i++)
        {
            var a = i * (MathF.PI / 2f) / capRings;
            profile.Add((r * MathF.Cos(a), CapsuleHeight - r + (r * MathF.Sin(a))));
        }

        var faces = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        for (var j = 0; j + 1 < profile.Count; j++)
        {
            var (r0, z0) = profile[j];
            var (r1, z1) = profile[j + 1];
            for (var k = 0; k < segments; k++)
            {
                var a0 = k * MathF.Tau / segments;
                var a1 = (k + 1) * MathF.Tau / segments;
                Vector3 p0 = new(r0 * MathF.Cos(a0), r0 * MathF.Sin(a0), z0), p1 = new(r0 * MathF.Cos(a1), r0 * MathF.Sin(a1), z0);
                Vector3 q0 = new(r1 * MathF.Cos(a0), r1 * MathF.Sin(a0), z1), q1 = new(r1 * MathF.Cos(a1), r1 * MathF.Sin(a1), z1);
                faces.Add((p0, p1, q1));
                faces.Add((p0, q1, q0));
            }
        }

        return Build("SpawnCapsule", faces, material, new BoundingBox(new Vector3(-r, -r, 0f), new Vector3(r, r, CapsuleHeight)));
    }

    // An axis-aligned box from min to max, outward normals.
    private static MeshData Cube(string material, string name, Vector3 min, Vector3 max)
    {
        var faces = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        Vector3 C(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
        Quad(C(0, 0, 0), C(0, 1, 0), C(1, 1, 0), C(1, 0, 0)); // bottom (-Z)
        Quad(C(0, 0, 1), C(1, 0, 1), C(1, 1, 1), C(0, 1, 1)); // top (+Z)
        Quad(C(0, 0, 0), C(1, 0, 0), C(1, 0, 1), C(0, 0, 1)); // -Y
        Quad(C(0, 1, 0), C(0, 1, 1), C(1, 1, 1), C(1, 1, 0)); // +Y
        Quad(C(0, 0, 0), C(0, 0, 1), C(0, 1, 1), C(0, 1, 0)); // -X
        Quad(C(1, 0, 0), C(1, 1, 0), C(1, 1, 1), C(1, 0, 1)); // +X
        return Build(name, faces, material, new BoundingBox(min, max));

        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            faces.Add((a, b, c));
            faces.Add((a, c, d));
        }
    }

    private static MeshData Build(string name, List<(Vector3 A, Vector3 B, Vector3 C)> faces, string material, BoundingBox bounds)
    {
        faces = faces.Where(f => Vector3.Cross(f.B - f.A, f.C - f.A).LengthSquared() > 1e-6f).ToList(); // a capsule's poles have no area
        var positions = new float[faces.Count * 9];
        var normals = new float[faces.Count * 9];
        var indices = new uint[faces.Count * 3];
        var k = 0;
        foreach (var (a, b, c) in faces)
        {
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            foreach (var p in new[] { a, b, c })
            {
                positions[(k * 3) + 0] = p.X;
                positions[(k * 3) + 1] = p.Y;
                positions[(k * 3) + 2] = p.Z;
                normals[(k * 3) + 0] = n.X;
                normals[(k * 3) + 1] = n.Y;
                normals[(k * 3) + 2] = n.Z;
                indices[k] = (uint)k;
                k++;
            }
        }

        return new MeshData(name, positions, normals, [], indices, [new MeshSection(material, 0, indices.Length)], bounds);
    }
}
