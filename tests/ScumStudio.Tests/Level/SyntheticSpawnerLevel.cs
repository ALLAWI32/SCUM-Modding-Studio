using System.Reflection;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Synthetic spawners for the spawn point tests (<see cref="SyntheticLevels"/> style): A_0_SpawnerLevel with a sentry that
/// stores its patrol path, a house whose loot spawner component the level stores without its <c>SpawnerMarkers</c> (they
/// are on the Blueprint's template, as a house's indoor loot points), and a spawner group whose markers sit on a component
/// beside its root; plus the house's Blueprint package BP_TestHouse.
/// </summary>
internal static class SyntheticSpawnerLevel
{
    public const string LevelPath = "/Game/ConZ_Files/Maps/The_Island/A_0_SpawnerLevel";
    public const string HousePackage = "/Game/ConZ_Files/Blueprints/BP_TestHouse";
    public const string Sentry = "SentrySpawner_0";
    public const string House = "BP_TestHouse_C_1";
    public const string HouseLoot = "LootSpawner";
    public const string Group = "ItemSpawnerGroup_1";
    public const string GroupLoot = "SpawnerComponent";
    private const string Engine = "/Script/Engine";
    private const string ConZ = "/Script/ConZ";

    public static readonly FVector SentryAt = new(0, 0, 100);
    public static readonly FVector[] Patrol = [new(100, 0, 0), new(200, 100, 0), new(300, 0, 50)];
    public static readonly FVector HouseAt = new(5000, 0, 0);
    public static readonly FVector HouseLootAt = new(200, 0, 0); // on the template, relative to the house's root
    public static readonly FVector[] HouseMarkers = [new(10, 0, 0), new(20, 0, 0), new(30, 0, 0)];
    public static readonly FVector GroupAt = new(-5000, 0, 0);
    public static readonly FVector GroupLootAt = new(0, 500, 0);
    public static readonly FVector[] GroupMarkers = [new(0, 0, 0), new(100, 0, 0), new(200, 0, 0)];

    // SyntheticLevels' ULevel tail (private there).
    private static readonly Action<ByteWriter, IReadOnlyList<int>> LevelNative = typeof(SyntheticLevels)
        .GetMethod("LevelNative", BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Action<ByteWriter, IReadOnlyList<int>>>();

    /// <summary>Writes the level and the house's Blueprint package under <paramref name="root"/> (a loose game folder).</summary>
    public static void WriteContent(string root)
    {
        Write(root, SyntheticLevels.MapsFolder + "/A_0_SpawnerLevel", BuildLevel(), ".umap");
        Write(root, "SCUM/Content/ConZ_Files/Blueprints/BP_TestHouse", BuildHousePackage(), ".uasset");
    }

    private static void Write(string root, string virtualBase, PackageBytes bytes, string extension) =>
        bytes.WriteAsync(Path.Combine([root, .. virtualBase.Split('/')]), extension).GetAwaiter().GetResult();

    public static PackageBytes BuildLevel()
    {
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_SpawnerLevel", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var bpClass = p.ObjectImport(HousePackage, Engine, "BlueprintGeneratedClass", "BP_TestHouse_C");
        var cdo = p.ObjectImport(HousePackage, HousePackage, "BP_TestHouse_C", "Default__BP_TestHouse_C");
        var rootTemplate = p.ObjectImport(HousePackage, Engine, "SceneComponent", "DefaultSceneRoot_GEN_VARIABLE");
        var lootTemplate = p.ObjectImport(HousePackage, ConZ, "ItemSpawnerComponent", "LootSpawner_GEN_VARIABLE");

        var sentry = p.Export(Sentry, p.ScriptClass(ConZ, "SentrySpawner2"), level);
        var sentryRoot = p.Export("Root", p.ScriptClass(Engine, "SceneComponent"), sentry);
        var house = p.Export(House, bpClass, level, template: cdo);
        var houseRoot = p.Export("DefaultSceneRoot", p.ScriptClass(Engine, "SceneComponent"), house, template: rootTemplate);
        var houseLoot = p.Export(HouseLoot, p.ScriptClass(ConZ, "ItemSpawnerComponent"), house, template: lootTemplate);
        var group = p.Export(Group, p.ScriptClass(ConZ, "ItemSpawnerGroup"), level);
        var groupRoot = p.Export("DefaultSceneRoot", p.ScriptClass(Engine, "SceneComponent"), group);
        var groupLoot = p.Export(GroupLoot, p.ScriptClass(ConZ, "ItemSpawnerComponent"), group);

        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => LevelNative(w, [sentry, house, group])));
        p.SetPayload(sentry, p.Properties(t =>
        {
            t.Object("RootComponent", sentryRoot);
            t.StructArray("PatrolPoints", "SentryPatrolPoint", [.. Patrol.Select(v => (Action<SyntheticLevelPackage.TagWriter>)(s => s.Vector("LocationRelativeToSentry", v.X, v.Y, v.Z)))]);
        }));
        p.SetPayload(sentryRoot, p.Properties(t => t.Vector("RelativeLocation", SentryAt.X, SentryAt.Y, SentryAt.Z)));
        p.SetPayload(house, p.Properties(t => t.Object("RootComponent", houseRoot)));
        p.SetPayload(houseRoot, p.Properties(t => t.Vector("RelativeLocation", HouseAt.X, HouseAt.Y, HouseAt.Z)));
        p.SetPayload(houseLoot, p.Properties(t => t.Object("AttachParent", houseRoot))); // no SpawnerMarkers: the template has them
        p.SetPayload(group, p.Properties(t => t.Object("RootComponent", groupRoot)));
        p.SetPayload(groupRoot, p.Properties(t => t.Vector("RelativeLocation", GroupAt.X, GroupAt.Y, GroupAt.Z)));
        p.SetPayload(groupLoot, p.Properties(t =>
        {
            t.Object("AttachParent", groupRoot);
            t.Vector("RelativeLocation", GroupLootAt.X, GroupLootAt.Y, GroupLootAt.Z);
            Markers(t, GroupMarkers);
        }));
        return p.Build();
    }

    /// <summary>BP_TestHouse: the CDO and the SCS templates; the loot spawner's template holds the three markers.</summary>
    public static PackageBytes BuildHousePackage()
    {
        var p = new SyntheticLevelPackage();
        var cdo = p.Export("Default__BP_TestHouse_C", p.ScriptClass(Engine, "Actor"), 0);
        var root = p.Export("DefaultSceneRoot_GEN_VARIABLE", p.ScriptClass(Engine, "SceneComponent"), 0);
        var loot = p.Export("LootSpawner_GEN_VARIABLE", p.ScriptClass(ConZ, "ItemSpawnerComponent"), 0);
        p.SetPayload(cdo, p.Properties(t => t.Object("RootComponent", root)));
        p.SetPayload(root, p.Properties());
        p.SetPayload(loot, p.Properties(t =>
        {
            t.Vector("RelativeLocation", HouseLootAt.X, HouseLootAt.Y, HouseLootAt.Z);
            Markers(t, HouseMarkers);
        }));
        return p.Build();
    }

    /// <summary><c>SpawnerMarkers</c>: one <c>ItemSpawnerMarker</c> per place, its <c>Transform</c> there (the preset left to the defaults).</summary>
    private static void Markers(SyntheticLevelPackage.TagWriter t, FVector[] places) =>
        t.StructArray("SpawnerMarkers", "ItemSpawnerMarker", [.. places.Select(v => (Action<SyntheticLevelPackage.TagWriter>)(m => m.TaggedStruct("Transform", "Transform", s =>
        {
            s.Quat("Rotation", 0, 0, 0, 1);
            s.Vector("Translation", v.X, v.Y, v.Z);
            s.Vector("Scale3D", 1, 1, 1);
        })))]);
}
