using System.Numerics;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Level.Model;

namespace ScumStudio.Viewport;

/// <summary>What a spawn marker stands for.</summary>
public enum SpawnKind
{
    /// <summary>Loot spawner (an item spawner group, a world item spawner, a loot point in a building).</summary>
    Loot,

    /// <summary>Loot area (an item spawner volume).</summary>
    LootArea,

    /// <summary>Sentry spawner.</summary>
    Sentry,

    /// <summary>Creature spawn point in the bunkers (Razors, kill boxes, tagged spawners).</summary>
    Creature,

    /// <summary>Where a car shop puts the vehicles it sells.</summary>
    Vehicle,

    /// <summary>Where players can drop in (event drop zones).</summary>
    PlayerDrop,
}

/// <summary>
/// The game's spawn places, drawn as coloured pins: actors that only spawn things (no mesh of their own) get a pin that
/// picks and moves like any object; loot points and vehicle boxes inside buildings get pins that show where they are
/// (they move with their building). The pins are generated meshes (<see cref="MeshKey"/>), so the scene pipeline draws,
/// culls and picks them like the game's meshes.
/// </summary>
public static class SpawnMarkers
{
    /// <summary>Prefix of the marker mesh keys.</summary>
    public const string Prefix = "#spawn/";

    private const float Width = 110f;
    private const float Height = 260f;

    /// <summary>Bright pin colours per kind (linear RGBA), never white.</summary>
    public static Vector4 Color(SpawnKind kind) => kind switch
    {
        SpawnKind.Loot => new(1f, 0.72f, 0.08f, 1f),
        SpawnKind.LootArea => new(1f, 0.4f, 0.03f, 1f),
        SpawnKind.Sentry => new(1f, 0.1f, 0.08f, 1f),
        SpawnKind.Creature => new(0.62f, 0.18f, 1f, 1f),
        SpawnKind.Vehicle => new(0.08f, 0.78f, 1f, 1f),
        _ => new(0.2f, 1f, 0.35f, 1f),
    };

    /// <summary>The marker mesh key of <paramref name="kind"/>.</summary>
    public static string MeshKey(SpawnKind kind) => Prefix + kind;

    /// <summary>True for a marker mesh key.</summary>
    public static bool IsMarker(string meshPath) => meshPath.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The spawn kind of an actor that only spawns things, or null.</summary>
    public static SpawnKind? KindOf(ActorRecord actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.ClassName switch
        {
            "ItemSpawnerGroup" or "WorldItemSpawner" => SpawnKind.Loot,
            "ItemSpawnerVolume" => SpawnKind.LootArea,
            "SentrySpawner2" => SpawnKind.Sentry,
            "BP_RazorSpawnPoint_C" or "BP_EasyKillBoxSpawnPoint_C" or "BP_KillBoxC4SpawnPoint_C" or "BP_TaggedSpawner_C" => SpawnKind.Creature,
            "BP_DropZoneLocationMarker_C" => SpawnKind.PlayerDrop,
            _ => null,
        };
    }

    /// <summary>The spawn kind of a spawn component inside another actor (a building's loot point), or null.</summary>
    public static SpawnKind? KindOf(ComponentRecord component)
    {
        ArgumentNullException.ThrowIfNull(component);
        return component.ClassName switch
        {
            "ItemSpawnerComponent" or "WorldItemSpawnerComponent" => SpawnKind.Loot,
            "VehicleSpawnBoxComponent" => SpawnKind.Vehicle,
            _ => null,
        };
    }

    /// <summary>
    /// Where a pin stands for a spawn at <paramref name="world"/>: upright and unscaled, drop zones larger, the loot points
    /// inside buildings (<paramref name="inside"/>) half size so a house full of them stays readable.
    /// </summary>
    public static FTransform PinAt(FTransform world, SpawnKind kind, bool inside = false)
    {
        var size = kind == SpawnKind.PlayerDrop ? 4f : inside ? 0.5f : 1f;
        return new(FQuat.Identity, world.Translation, new FVector(size, size, size));
    }

    /// <summary>The marker mesh for <paramref name="kind"/>: a pin (point down at the spawn place) in its colour.</summary>
    public static PreparedMeshAsset Asset(SpawnKind kind)
    {
        var key = MeshKey(kind);
        var mesh = Pin(key);
        return new PreparedMeshAsset(key, mesh, null)
        {
            MaterialSlots = [key],
            MaterialTints = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase) { [key] = Color(kind) },
        };
    }

    /// <summary>The asset for a marker mesh key, or null when it is none.</summary>
    public static PreparedMeshAsset? AssetFor(string meshPath) =>
        IsMarker(meshPath) && Enum.TryParse<SpawnKind>(meshPath[Prefix.Length..], out var kind) ? Asset(kind) : null;

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

        return new MeshData("SpawnPin", positions, normals, [], indices, [new MeshSection(material, 0, indices.Length)],
            new BoundingBox(new Vector3(-w, -w, 0), new Vector3(w, w, Height)));
    }
}
