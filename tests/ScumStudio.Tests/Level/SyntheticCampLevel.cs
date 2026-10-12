using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Tests.Level;

/// <summary>
/// A cooked camp level for the delete-companion tests (the owner: "If I delete a fire place, the fire stays burning in
/// game"). In cm: a fireplace mesh at the origin with its fire as separate mesh-less actors (a point light at 60 up, a
/// fire emitter, a Wwise sound 1 m aside, an ash decal), a rock and a high glow light attached to the fireplace, a house
/// Blueprint at x 400 whose fireplace part (y 300) carries its own fire, light and sound components and whose porch light
/// hangs by the walls, a camp ambience sound between the two (130 cm from the fireplace, 70 cm from the house), a lamp
/// 10 m away, the sun, the world settings and a trigger volume.
/// </summary>
internal static class SyntheticCampLevel
{
    public const string LevelName = "A_0_Camp";
    public const string LevelPath = "/Game/ConZ_Files/Maps/The_Island/" + LevelName;
    public const string FireplaceMesh = "/Game/ConZ_Files/Props/Camp/SM_Fireplace_01";
    public const string WallsMesh = "/Game/ConZ_Files/Buildings/House/SM_House_Walls";
    public const string FireTemplate = "/Game/ConZ_Files/Particles/Fire/P_FirePlace_Dynamic";
    public const string FireEvent = "/Game/WwiseAudio/Events/Ambience/Play_Fire_Loop";
    private const string Engine = "/Script/Engine";
    private const string AkAudio = "/Script/AkAudio";

    /// <summary>Writes the level as <c>SCUM/Content/ConZ_Files/Maps/The_Island/A_0_Camp.umap</c> under <paramref name="root"/>.</summary>
    public static void WriteCampContent(string root) =>
        Build().WriteAsync(Path.Combine([root, .. (SyntheticLevels.MapsFolder + "/" + LevelName).Split('/')]), ".umap").GetAwaiter().GetResult();

    public static PackageBytes Build()
    {
        var p = new SyntheticLevelPackage();
        var world = p.Export(LevelName, p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var fireplaceMesh = p.ObjectImport(FireplaceMesh, Engine, "StaticMesh", "SM_Fireplace_01");
        var wallsMesh = p.ObjectImport(WallsMesh, Engine, "StaticMesh", "SM_House_Walls");
        var rockMesh = p.ObjectImport(SyntheticLevels.RockPackage, Engine, "StaticMesh", "SM_Rock");
        var fireTemplate = p.ObjectImport(FireTemplate, Engine, "ParticleSystem", "P_FirePlace_Dynamic");
        var fireEvent = p.ObjectImport(FireEvent, AkAudio, "AkAudioEvent", "Play_Fire_Loop");
        var actors = new List<int>();
        var payloads = new List<(int Export, byte[] Payload)>();

        // An actor whose root is one component at a place (a mesh: with its StaticMesh, its LODData follows).
        (int Actor, int Root) Single(string name, int actorClass, string rootName, int rootClass, float x, float y, float z, Action<SyntheticLevelPackage.TagWriter>? extra = null, int attachTo = 0, bool mesh = false)
        {
            var actor = p.Export(name, actorClass, level);
            var root = p.Export(rootName, rootClass, actor);
            actors.Add(actor);
            payloads.Add((actor, p.Properties(t => t.Object("RootComponent", root))));
            payloads.Add((root, p.Properties(t =>
            {
                if (attachTo != 0)
                {
                    t.Object("AttachParent", attachTo);
                }

                extra?.Invoke(t);
                t.Vector("RelativeLocation", x, y, z);
            }, mesh ? w => w.I32(0) : null)));
            return (actor, root);
        }

        var worldSettings = p.Export("WorldSettings", p.ScriptClass(Engine, "WorldSettings"), level);
        actors.Add(worldSettings);
        payloads.Add((worldSettings, p.Properties()));

        var (_, fireplaceRoot) = Single("SM_Fireplace_1", p.ScriptClass(Engine, "StaticMeshActor"), "StaticMeshComponent0", p.ScriptClass(Engine, "StaticMeshComponent"), 0, 0, 0,
            t => t.Object("StaticMesh", fireplaceMesh), mesh: true);
        Single("FireLight_1", p.ScriptClass(Engine, "PointLight"), "LightComponent0", p.ScriptClass(Engine, "PointLightComponent"), 0, 0, 60);
        Single("P_Fire_1", p.ScriptClass(Engine, "Emitter"), "ParticleSystemComponent0", p.ScriptClass(Engine, "ParticleSystemComponent"), 0, 0, 0,
            t => t.Object("Template", fireTemplate));
        var akSound = p.ScriptClass(AkAudio, "AkAmbientSound");
        var akComponent = p.ScriptClass(AkAudio, "AkComponent");
        Single("Fire_Sound_1", akSound, "AkComponent0", akComponent, 100, 0, 0, t => t.Object("AkAudioEvent", fireEvent));
        Single("Ash_Decal_1", p.ScriptClass(Engine, "DecalActor"), "NewDecalComponent", p.ScriptClass(Engine, "DecalComponent"), 0, 0, -5);

        // The house: walls and a fireplace part, the fireplace's own fire, light and sound, and a porch light by the walls.
        var meshComponent = p.ScriptClass(Engine, "StaticMeshComponent");
        var pointLightComponent = p.ScriptClass(Engine, "PointLightComponent");
        var house = p.Export("BP_House_1", p.ScriptClass(Engine, "Actor"), level);
        var houseRoot = p.Export("DefaultSceneRoot", p.ScriptClass(Engine, "SceneComponent"), house);
        var walls = p.Export("Walls", meshComponent, house);
        var fireplace = p.Export("Fireplace", meshComponent, house);
        var fire = p.Export("Fire", p.ScriptClass(Engine, "ParticleSystemComponent"), house);
        var fireLight = p.Export("FireLight", pointLightComponent, house);
        var fireSound = p.Export("FireSound", akComponent, house);
        var porchLight = p.Export("PorchLight", pointLightComponent, house);
        actors.Add(house);
        payloads.Add((house, p.Properties(t => t.Object("RootComponent", houseRoot))));
        payloads.Add((houseRoot, p.Properties(t => t.Vector("RelativeLocation", 400, 0, 0))));
        payloads.Add((walls, p.Properties(t =>
        {
            t.Object("AttachParent", houseRoot);
            t.Object("StaticMesh", wallsMesh);
        }, w => w.I32(0))));
        payloads.Add((fireplace, p.Properties(t =>
        {
            t.Object("AttachParent", houseRoot);
            t.Object("StaticMesh", fireplaceMesh);
            t.Vector("RelativeLocation", 0, 300, 0);
        }, w => w.I32(0))));
        payloads.Add((fire, p.Properties(t =>
        {
            t.Object("AttachParent", fireplace);
            t.Object("Template", fireTemplate);
        })));
        payloads.Add((fireLight, p.Properties(t =>
        {
            t.Object("AttachParent", houseRoot);
            t.Vector("RelativeLocation", 0, 300, 60);
        })));
        payloads.Add((fireSound, p.Properties(t =>
        {
            t.Object("AttachParent", houseRoot);
            t.Object("AkAudioEvent", fireEvent);
            t.Vector("RelativeLocation", 0, 300, 0);
        })));
        payloads.Add((porchLight, p.Properties(t =>
        {
            t.Object("AttachParent", houseRoot);
            t.Vector("RelativeLocation", 0, -80, 200);
        })));

        Single("Camp_Ambience", akSound, "AkComponent0", akComponent, 230, 0, 0, t => t.Object("AkAudioEvent", fireEvent));
        Single("Lamp_1", p.ScriptClass(Engine, "PointLight"), "LightComponent0", pointLightComponent, 1000, 0, 0);
        Single("DirectionalLight_1", p.ScriptClass(Engine, "DirectionalLight"), "LightComponent0", p.ScriptClass(Engine, "DirectionalLightComponent"), 0, 0, 500);
        Single("Smoke_Volume", p.ScriptClass(Engine, "TriggerVolume"), "BrushComponent0", p.ScriptClass(Engine, "BrushComponent"), 0, 0, 0);

        // Attached to the fireplace: a rock with a mesh of its own beside it, a glow light 6 m up (beyond any margin).
        Single("Rock_1", p.ScriptClass(Engine, "StaticMeshActor"), "StaticMeshComponent0", meshComponent, 50, 0, 0,
            t => t.Object("StaticMesh", rockMesh), attachTo: fireplaceRoot, mesh: true);
        Single("Glow_1", p.ScriptClass(Engine, "PointLight"), "LightComponent0", pointLightComponent, 0, 0, 600, attachTo: fireplaceRoot);

        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level); // PersistentLevel
            w.I32(0); // ExtraReferencedObjects
            w.I32(0); // StreamingLevels
        }));
        p.SetPayload(level, p.Properties(native: w => LevelNative(w, actors)));
        foreach (var (export, payload) in payloads)
        {
            p.SetPayload(export, payload);
        }

        return p.Build();
    }

    /// <summary>ULevel native data after the tagged properties (as <see cref="SyntheticLevels"/> writes it).</summary>
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
        w.FString(LevelName);
        w.FString(string.Empty);
        w.I32(0); // Op
        w.I32(7777); // Port
        w.I32(1); // Valid
        w.I32(0); // Model
        w.I32(0); // ModelComponents
        w.I32(0); // LevelScriptActor
        w.I32(0); // NavListStart
        w.I32(0); // NavListEnd

        // FPrecomputedVisibilityHandler, FPrecomputedVolumeDistanceField: empty.
        for (var i = 0; i < 4; i++)
        {
            w.F32(0);
        }

        w.I32(0);
        w.I32(0);
        w.I32(0);
        for (var i = 0; i < 7; i++)
        {
            w.F32(0);
        }

        w.U8(0);
        w.I32(0);
        w.I32(0);
        w.I32(0);
        w.I32(0);
    }
}
