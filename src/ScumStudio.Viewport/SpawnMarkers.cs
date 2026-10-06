using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;
using ScumStudio.Level.Spawns;

namespace ScumStudio.Viewport;

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
}

/// <summary>
/// The game's spawn places, drawn as coloured pins: actors that only spawn things (no mesh of their own) get a pin that
/// picks and moves like any object; loot points inside buildings get pins that show where they are (they move with their
/// building); a building's fixed-item spawners and vehicle boxes pick and move as parts of it (<see cref="IsSpawnPart"/>). The pins are generated meshes (<see cref="MeshKey"/>), so the scene pipeline draws,
/// culls and picks them like the game's meshes.
/// </summary>
public static class SpawnMarkers
{
    /// <summary>Prefix of the marker mesh keys.</summary>
    public const string Prefix = "#spawn/";

    // Small pins (owner: "a small pin, not a big one"): a hand wide, a bit over a metre tall.
    private const float Width = 40f;
    private const float Height = 120f;
    private const float RingRadius = 100f;

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
        _ => new(0.2f, 1f, 0.35f, 1f),
    };

    /// <summary>The marker mesh key of <paramref name="kind"/>.</summary>
    public static string MeshKey(SpawnKind kind) => Prefix + kind;

    /// <summary>True for a marker mesh key.</summary>
    public static bool IsMarker(string meshPath) => meshPath.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>True for an actor that is only a loot spawner (its loot points pick and move as the actor).</summary>
    public static bool IsLootSpawner(ActorRecord actor) => actor.ClassName is "ItemSpawnerGroup" or "WorldItemSpawner";

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
    /// point its spawner component and marker index, and for a spawn part (<see cref="IsSpawnPart"/>) its component). Loot
    /// points come from the item spawners' markers (a spawner group's pick as the group; a building's pick as themselves), a
    /// sentry spawner adds its patrol points, a car shop its vehicle boxes, the other spawners stand at the actor.
    /// </summary>
    public static IEnumerable<(SpawnKind Kind, FTransform Pin, bool PicksActor, string Label, (string Component, int Index)? Marker, ComponentRecord? Part)> PinsOf(ActorRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var lootSpawner = IsLootSpawner(actor);
        var points = 0;
        foreach (var component in actor.Components.Where(c => c.IsSceneComponent))
        {
            var part = IsSpawnPart(actor, component) ? component : null;
            for (var i = 0; i < component.SpawnMarkers.Count; i++)
            {
                var m = component.SpawnMarkers[i];
                points++;
                yield return (SpawnKind.Loot, PinAt(m.Local * component.WorldTransform, SpawnKind.Loot, inside: !lootSpawner), lootSpawner,
                    $"{actor.Name}/{component.Name} [{i}] {m.Preset} {m.Probability:0.#}% x{m.MinQuantity}-{m.MaxQuantity}", lootSpawner || part is not null ? null : (component.Name, i), part);
            }

            if (component.ClassName == "VehicleSpawnBoxComponent")
            {
                yield return (SpawnKind.Vehicle, PinAt(component.WorldTransform, SpawnKind.Vehicle, inside: true), false, $"{actor.Name}/{component.Name}", null, part);
            }
        }

        // A trade post's traders stand where its NPCs do; the pins pick the trade post (move, copy, delete it whole).
        foreach (var trader in actor.TraderMarkers)
        {
            yield return (SpawnKind.Trader, PinAt(trader.Local * actor.WorldTransform, SpawnKind.Trader), true, $"{trader.Name} ({trader.Type})", null, null);
        }

        if (lootSpawner && points == 0)
        {
            yield return (SpawnKind.Loot, PinAt(actor.WorldTransform, SpawnKind.Loot), true, actor.Name, null, null);
        }

        if (KindOf(actor) is { } kind)
        {
            yield return (kind, PinAt(actor.WorldTransform, kind), true, actor.Name, null, null);
            if (kind is SpawnKind.Zone or SpawnKind.Animal)
            {
                // The zone's ellipse (the root's scale is its size in metres) or the area's circle, flat around the centre.
                var t = actor.WorldTransform;
                var yaw = FQuat.MakeFromEuler(new FVector(0f, 0f, t.Rotation.Rotator().Yaw));
                yield return (kind == SpawnKind.Zone ? SpawnKind.ZoneRing : SpawnKind.AnimalRing, new FTransform(yaw, t.Translation, new FVector(t.Scale3D.X, t.Scale3D.Y, 1f)), true, actor.Name, null, null);
            }

            for (var i = 0; i < actor.PatrolPoints.Count; i++)
            {
                var at = new FTransform(actor.WorldTransform.TransformPosition(actor.PatrolPoints[i]));
                yield return (SpawnKind.Patrol, PinAt(at, SpawnKind.Patrol, inside: true), true, $"{actor.Name} patrol {i + 1}", null, null);
            }
        }
    }

    /// <summary>The kind a marker mesh key draws (<see cref="MeshKey"/>), or null for any other mesh.</summary>
    public static SpawnKind? KindOfMesh(string meshPath) =>
        IsMarker(meshPath) && Enum.TryParse<SpawnKind>(meshPath[Prefix.Length..], out var kind) ? kind : null;

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
    /// Where a pin stands for a spawn at <paramref name="world"/>: upright and unscaled, drop zones larger, the loot points
    /// inside buildings (<paramref name="inside"/>) half size so a house full of them stays readable.
    /// </summary>
    public static FTransform PinAt(FTransform world, SpawnKind kind, bool inside = false)
    {
        var size = kind == SpawnKind.PlayerDrop ? 4f : kind is SpawnKind.Zone or SpawnKind.Animal ? 3f : kind == SpawnKind.Trader ? 1.6f : inside ? 0.7f : 1f;
        return new(FQuat.Identity, world.Translation, new FVector(size, size, size));
    }

    /// <summary>The NPC's Blueprint name from its class path (<c>BP_ArmsDealer_01</c>).</summary>
    public static string NpcName(string npcClass)
    {
        var name = npcClass[(npcClass.LastIndexOf('.') + 1)..];
        return name.EndsWith("_C", StringComparison.Ordinal) ? name[..^2] : name;
    }

    /// <summary>The marker mesh for <paramref name="kind"/>: a pin (point down at the spawn place) in its colour.</summary>
    public static PreparedMeshAsset Asset(SpawnKind kind)
    {
        var key = MeshKey(kind);
        var mesh = kind is SpawnKind.ZoneRing or SpawnKind.AnimalRing ? Ring(key) : Pin(key);
        return new PreparedMeshAsset(key, mesh, null)
        {
            MaterialSlots = [key],
            MaterialTints = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase) { [key] = Color(kind) },
        };
    }

    /// <summary>The asset for a marker mesh key, or null when it is none.</summary>
    public static PreparedMeshAsset? AssetFor(string meshPath) =>
        IsMarker(meshPath) && Enum.TryParse<SpawnKind>(meshPath[Prefix.Length..], out var kind) ? Asset(kind) : null;

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

    private static MeshData Build(string name, List<(Vector3 A, Vector3 B, Vector3 C)> faces, string material, BoundingBox bounds)
    {
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
