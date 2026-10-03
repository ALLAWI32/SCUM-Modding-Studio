using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Synthetic cooked content for the CUE4Parse level reader tests (also usable to generate files for manual CLI runs):
/// a sublevel, a Blueprint package with SCS templates, and a persistent level with StreamingLevels.
/// </summary>
internal static class SyntheticLevels
{
    public const string MapsFolder = "SCUM/Content/ConZ_Files/Maps/The_Island";
    public const string LevelPath = "/Game/ConZ_Files/Maps/The_Island/A_0_TestLevel";
    public const string RockPackage = "/Game/ConZ_Files/Models/Rocks/SM_Rock";
    public const string BlueprintPackage = "/Game/ConZ_Files/Blueprints/BP_Lamp";
    private const string Engine = "/Script/Engine";

    public static readonly FTransform Instance0 = new(new FVector(1, 2, 3));
    public static readonly FTransform Instance1 = new(new FRotator(0, 90, 0), new FVector(100, 0, 0), new FVector(3, 3, 3));

    public static void WriteContent(string root, bool withBlueprintPackage, WorldTileInfo? tile = null)
    {
        Write(root, MapsFolder + "/A_0_TestLevel", BuildLevel(tile), ".umap");
        if (withBlueprintPackage)
        {
            Write(root, "SCUM/Content/ConZ_Files/Blueprints/BP_Lamp", BuildBlueprintPackage(), ".uasset");
        }
    }

    public static void WritePersistentLevel(string root) =>
        Write(root, MapsFolder + "/The_Island", BuildPersistentLevel(), ".umap");

    private static void Write(string root, string virtualBase, PackageBytes bytes, string extension) =>
        bytes.WriteAsync(Path.Combine([root, .. virtualBase.Split('/')]), extension).GetAwaiter().GetResult();

    /// <summary>
    /// A_0_TestLevel: World, PersistentLevel, a StaticMeshActor with a mesh root and an attached door, an actor with a
    /// scene root and an ISM (2 instances, absolute rotation, no RootComponent stored), and a Blueprint actor whose bulb
    /// stores only AttachParent (the rest is on the Blueprint's SCS template).
    /// </summary>
    public static PackageBytes BuildLevel(WorldTileInfo? tile = null)
    {
        var p = new SyntheticLevelPackage();
        var world = p.Export("A_0_TestLevel", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var rock = p.ObjectImport(RockPackage, Engine, "StaticMesh", "SM_Rock");
        var bpClass = p.ObjectImport(BlueprintPackage, Engine, "BlueprintGeneratedClass", "BP_Lamp_C");
        var bpCdo = p.ObjectImport(BlueprintPackage, BlueprintPackage, "BP_Lamp_C", "Default__BP_Lamp_C");
        var rootTemplate = p.ObjectImport(BlueprintPackage, Engine, "SceneComponent", "DefaultSceneRoot_GEN_VARIABLE");
        var bulbTemplate = p.ObjectImport(BlueprintPackage, Engine, "StaticMeshComponent", "Bulb_GEN_VARIABLE");

        var house = p.Export("StaticMeshActor_1", p.ScriptClass(Engine, "StaticMeshActor"), level);
        var houseMesh = p.Export("StaticMeshComponent0", p.ScriptClass(Engine, "StaticMeshComponent"), house);
        var door = p.Export("Door", p.ScriptClass(Engine, "StaticMeshComponent"), house);
        var rocks = p.Export("Rocks_Actor", p.ScriptClass(Engine, "Actor"), level);
        var rocksRoot = p.Export("DefaultSceneRoot", p.ScriptClass(Engine, "SceneComponent"), rocks);
        var ism = p.Export("Rocks", p.ScriptClass(Engine, "InstancedStaticMeshComponent"), rocks);
        var lamp = p.Export("BP_Lamp_C_1", bpClass, level, template: bpCdo);
        var lampRoot = p.Export("DefaultSceneRoot", p.ScriptClass(Engine, "SceneComponent"), lamp, template: rootTemplate);
        var bulb = p.Export("Bulb", p.ScriptClass(Engine, "StaticMeshComponent"), lamp, template: bulbTemplate);

        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level); // PersistentLevel
            w.I32(0); // ExtraReferencedObjects
            w.I32(0); // StreamingLevels
        }));
        p.SetPayload(level, p.Properties(native: w => LevelNative(w, [house, 0, rocks, lamp])));
        p.SetPayload(house, p.Properties(t => t.Object("RootComponent", houseMesh)));
        p.SetPayload(houseMesh, p.Properties(t =>
        {
            t.Object("StaticMesh", rock);
            t.Vector("RelativeLocation", 1000, 0, 0);
            t.Rotator("RelativeRotation", 0, 90, 0);
            t.Vector("RelativeScale3D", 2, 2, 2);
        }, w => w.I32(0))); // LODData
        p.SetPayload(door, p.Properties(t =>
        {
            t.Object("AttachParent", houseMesh);
            t.Object("StaticMesh", rock);
            t.Vector("RelativeLocation", 100, 0, 0);
        }, w => w.I32(0)));
        p.SetPayload(rocks, p.Properties());
        p.SetPayload(rocksRoot, p.Properties(t => t.Vector("RelativeLocation", 0, 0, 100)));
        p.SetPayload(ism, p.Properties(t =>
        {
            t.Object("AttachParent", rocksRoot);
            t.Object("StaticMesh", rock);
            t.Vector("RelativeLocation", 10, 0, 0);
            t.Rotator("RelativeRotation", 0, 45, 0);
            t.Bool("bAbsoluteRotation", true);
        }, w =>
        {
            w.I32(0); // LODData
            w.I32(0); // bCooked
            w.I32(64); // PerInstanceSMData: element size (FMatrix)
            w.I32(2);
            WriteMatrix(w, Instance0);
            WriteMatrix(w, Instance1);
            w.I32(4); // PerInstanceSMCustomData: element size, count
            w.I32(0);
        }));
        p.SetPayload(lamp, p.Properties());
        p.SetPayload(lampRoot, p.Properties(t => t.Vector("RelativeLocation", -500, 250, 0)));
        p.SetPayload(bulb, p.Properties(t => t.Object("AttachParent", lampRoot), w => w.I32(0)));
        if (tile is null)
        {
            return p.Build();
        }

        // A World Composition tile: its FWorldTileInfo inside the asset registry block, the summary pointing at it (the
        // first build tells where that block lands).
        var tileBytes = new ByteWriter();
        tile.Write(tileBytes);
        var blob = new byte[4 + tileBytes.Length];
        tileBytes.WrittenSpan.CopyTo(blob.AsSpan(4));
        var first = CookedPackage.Parse(p.Build(blob).UAsset, []);
        return p.Build(blob, first.Summary with { WorldTileInfoDataOffset = first.Summary.AssetRegistryDataOffset + 4 });
    }

    /// <summary>BP_Lamp: the CDO (RootComponent -> DefaultSceneRoot_GEN_VARIABLE) and the SCS component templates.</summary>
    public static PackageBytes BuildBlueprintPackage()
    {
        var p = new SyntheticLevelPackage();
        var rock = p.ObjectImport(RockPackage, Engine, "StaticMesh", "SM_Rock");
        var cdo = p.Export("Default__BP_Lamp_C", p.ScriptClass(Engine, "Actor"), 0);
        var root = p.Export("DefaultSceneRoot_GEN_VARIABLE", p.ScriptClass(Engine, "SceneComponent"), 0);
        var bulb = p.Export("Bulb_GEN_VARIABLE", p.ScriptClass(Engine, "StaticMeshComponent"), 0);
        p.SetPayload(cdo, p.Properties(t => t.Object("RootComponent", root)));
        p.SetPayload(root, p.Properties());
        p.SetPayload(bulb, p.Properties(t =>
        {
            t.Object("StaticMesh", rock);
            t.Vector("RelativeLocation", 0, 0, 30);
            t.Vector("RelativeScale3D", 0.5f, 0.5f, 0.5f);
        }, w => w.I32(0)));
        return p.Build();
    }

    /// <summary>
    /// A level storing one Blueprint actor with nothing but its root component (as if the cook had dropped every other
    /// component), so a reader has to rebuild the actor from its Blueprint package; the root's template is the class's
    /// <c>DefaultSceneRoot_GEN_VARIABLE</c> and its outer the class import, like in cooked levels.
    /// </summary>
    public static PackageBytes BuildBlueprintOnlyLevel(string levelName, string blueprintPackage, string actorName, FVector location, FRotator rotation)
    {
        var className = blueprintPackage[(blueprintPackage.LastIndexOf('/') + 1)..] + "_C";
        var p = new SyntheticLevelPackage();
        var world = p.Export(levelName, p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var bpClass = p.ObjectImport(blueprintPackage, Engine, "BlueprintGeneratedClass", className);
        var cdo = p.ObjectImport(blueprintPackage, blueprintPackage, className, "Default__" + className);
        var rootTemplate = p.Import(Engine, "SceneComponent", bpClass, "DefaultSceneRoot_GEN_VARIABLE");
        var actor = p.Export(actorName, bpClass, level, template: cdo);
        var root = p.Export("DefaultSceneRoot", p.ScriptClass(Engine, "SceneComponent"), actor, template: rootTemplate);
        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => LevelNative(w, [actor])));
        p.SetPayload(actor, p.Properties(t => t.Object("RootComponent", root)));
        p.SetPayload(root, p.Properties(t =>
        {
            t.Vector("RelativeLocation", location.X, location.Y, location.Z);
            t.Rotator("RelativeRotation", rotation.Pitch, rotation.Yaw, rotation.Roll);
        }));
        return p.Build();
    }

    /// <summary>Writes <see cref="BuildBlueprintOnlyLevel"/> as <c>SCUM/Content/ConZ_Files/Maps/The_Island/&lt;levelName&gt;.umap</c>.</summary>
    public static void WriteBlueprintOnlyLevel(string root, string levelName, string blueprintPackage, string actorName, FVector location, FRotator rotation) =>
        Write(root, MapsFolder + "/" + levelName, BuildBlueprintOnlyLevel(levelName, blueprintPackage, actorName, location, rotation), ".umap");

    /// <summary>The_Island: World with two StreamingLevels (one present, one missing).</summary>
    public static PackageBytes BuildPersistentLevel()
    {
        var p = new SyntheticLevelPackage();
        var world = p.Export("The_Island", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var dynamic = p.Export("LevelStreamingDynamic_0", p.ScriptClass(Engine, "LevelStreamingDynamic"), world);
        var always = p.Export("LevelStreamingAlwaysLoaded_1", p.ScriptClass(Engine, "LevelStreamingAlwaysLoaded"), world);
        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(2);
            w.I32(dynamic);
            w.I32(always);
        }));
        p.SetPayload(level, p.Properties(native: w => LevelNative(w, [])));
        p.SetPayload(dynamic, p.Properties(t =>
        {
            t.SoftObject("WorldAsset", $"{LevelPath}.A_0_TestLevel");
            t.TaggedStruct("LevelTransform", "Transform", s =>
            {
                s.Quat("Rotation", 0, 0, 0, 1);
                s.Vector("Translation", 100, 0, 0);
            });
            t.Bool("bInitiallyLoaded", true);
        }));
        p.SetPayload(always, p.Properties(t =>
        {
            t.SoftObject("WorldAsset", "/Game/ConZ_Files/Maps/The_Island/A_0_Missing.A_0_Missing");
            t.Bool("bInitiallyVisible", true);
        }));
        return p.Build();
    }

    /// <summary>ULevel native data after the tagged properties (UE 4.27 <c>ULevel::Serialize</c>, cooked).</summary>
    private static void LevelNative(ByteWriter w, IReadOnlyList<int> actors)
    {
        w.I32(actors.Count);
        foreach (var a in actors)
        {
            w.I32(a);
        }

        // FURL
        w.FString("unreal");
        w.FString(string.Empty);
        w.FString("A_0_TestLevel");
        w.FString(string.Empty);
        w.I32(0); // Op
        w.I32(7777); // Port
        w.I32(1); // Valid
        w.I32(0); // Model
        w.I32(0); // ModelComponents
        w.I32(0); // LevelScriptActor
        w.I32(0); // NavListStart
        w.I32(0); // NavListEnd

        // FPrecomputedVisibilityHandler: origin XY, cell size XY/Z, bucket size, bucket count, buckets[]
        w.F32(0);
        w.F32(0);
        w.F32(0);
        w.F32(0);
        w.I32(0);
        w.I32(0);
        w.I32(0);

        // FPrecomputedVolumeDistanceField: max distance, box, size XYZ, data[]
        w.F32(0);
        for (var i = 0; i < 6; i++)
        {
            w.F32(0);
        }

        w.U8(0);
        w.I32(0);
        w.I32(0);
        w.I32(0);
        w.I32(0);
    }

    /// <summary>FMatrix as serialized in PerInstanceSMData: 16 floats, row-major, translation in the last row.</summary>
    private static void WriteMatrix(ByteWriter w, FTransform transform)
    {
        var m = transform.ToMatrixWithScale();
        foreach (var v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
        {
            w.F32(v);
        }
    }
}
