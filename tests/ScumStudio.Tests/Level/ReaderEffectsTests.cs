using System.Text.Json;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Geometry;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Model;
using ScumStudio.Level.Reading;

namespace ScumStudio.Tests.Level;

/// <summary>
/// What the reader tells about the effects, sounds and volumes that belong to an object, so a delete can take them with it
/// (owner, 2026-10-12: "If I delete a fire place, the fire stays burning in game"): Wwise and Niagara components are scene
/// components, effect components name what they play, shapes and brushes have bounds, a swim volume says so, and
/// <c>level dump --json</c> shows all of it. Synthetic cooked level read back through CUE4Parse.
/// </summary>
public sealed class ReaderEffectsTests
{
    private const string LevelName = "A_0_Effects";
    private const string LevelPath = "/Game/ConZ_Files/Maps/The_Island/" + LevelName;
    private const string Engine = "/Script/Engine";
    private const string AkAudio = "/Script/AkAudio";
    private const string Flames = "/Game/ConZ_Files/Particles/Fire/P_FirePlace_Dynamic.P_FirePlace_Dynamic";
    private const string NiagaraFire = "/Game/ConZ_Files/Particles/Fire/NS_Fire_01_Exposed.NS_Fire_01_Exposed";
    private const string FireEvent = "/Game/WwiseAudio/Events/Fire/Play_Fireplace.Play_Fireplace";
    private const string Crackle = "/Game/ConZ_Files/Sounds/Fire/SC_Fire_Crackle.SC_Fire_Crackle";
    private const string AshDecal = "/Game/ConZ_Files/Decals/MI_Ash_Decal.MI_Ash_Decal";
    private const float Tolerance = 0.01f;

    [Fact]
    public void AnAkComponentIsASceneComponentAndItsActorStandsWhereItIs()
    {
        var doc = Load();

        var ambient = doc.FindActor("AkAmbientSound_1")!;
        Assert.Equal("AkComponent", ambient.Root!.ClassName);
        Assert.True(ambient.Root.IsSceneComponent);
        AssertNear(new FVector(-300, 400, 10), ambient.WorldTransform.Translation);

        // The fireplace's crackle is a child actor whose Wwise component stores no place of its own: it is the child's
        // root, so it stands at the fire (40 cm above it), not at the world's origin.
        var fire = doc.FindActor("Fireplace_FX_1")!;
        var sound = fire.FindComponent("FireSound/AkAudioComponent0")!;
        Assert.True(sound.IsSynthesized);
        Assert.True(sound.IsSceneComponent);
        AssertNear(new FVector(1000, 2000, 90), sound.WorldTransform.Translation);
        Assert.DoesNotContain(doc.Warnings, w => w.Contains("no root scene component", StringComparison.Ordinal));

        Assert.True(fire.FindComponent("Flames_Niagara")!.IsSceneComponent);
    }

    [Fact]
    public void AssetPathsOfParticleNiagaraAkSoundAndDecalComponents()
    {
        var doc = Load();
        var fire = doc.FindActor("Fireplace_FX_1")!;

        Assert.Equal(Flames, fire.FindComponent("Flames")!.AssetPath);
        Assert.Equal(NiagaraFire, fire.FindComponent("Flames_Niagara")!.AssetPath);
        Assert.Equal(Crackle, fire.FindComponent("Crackle")!.AssetPath);
        Assert.Equal(AshDecal, fire.FindComponent("Ash")!.AssetPath);
        Assert.Equal(FireEvent, fire.FindComponent("FireSound/AkAudioComponent0")!.AssetPath);
        Assert.Equal(FireEvent, doc.FindActor("AkAmbientSound_1")!.Root!.AssetPath);

        Assert.Null(fire.Root!.AssetPath);
        Assert.Null(fire.FindComponent("HeatBox")!.AssetPath);
        Assert.Null(fire.FindComponent("FireSound")!.AssetPath);
    }

    [Fact]
    public void ABoxComponentAndABrushVolumeHaveLocalBounds()
    {
        var doc = Load();
        var fire = doc.FindActor("Fireplace_FX_1")!;

        AssertBox(new FVector(-100, -50, -20), new FVector(100, 50, 20), fire.FindComponent("HeatBox")!.LocalBounds);
        AssertBox(new FVector(-150), new FVector(150), fire.FindComponent("HeatSphere")!.LocalBounds);
        AssertBox(new FVector(-22, -22, -44), new FVector(22, 22, 44), fire.FindComponent("HeatCapsule")!.LocalBounds); // engine defaults
        Assert.Null(fire.FindComponent("Flames")!.LocalBounds);
        Assert.Null(fire.Root!.LocalBounds);

        // The lake's brush: one box 2000 x 1000 x 400 turned 90 degrees and raised 100 cm, in the brush's own space.
        var lake = doc.FindActor("PhysicsVolume_Lake")!;
        AssertBox(new FVector(-500, -1000, -100), new FVector(500, 1000, 300), lake.Root!.LocalBounds);
        Assert.Null(doc.FindActor("PostProcessVolume_1")!.Root!.LocalBounds); // no brush body stored
    }

    [Fact]
    public void APhysicsVolumeWithBWaterVolumeIsAWaterVolumeAndAPostProcessVolumeIsNot()
    {
        var doc = Load();

        var lake = doc.FindActor("PhysicsVolume_Lake")!;
        Assert.Equal(ActorKind.Volume, lake.Kind);
        Assert.True(lake.WaterVolume);

        var post = doc.FindActor("PostProcessVolume_1")!;
        Assert.Equal(ActorKind.Volume, post.Kind);
        Assert.False(post.WaterVolume);
        Assert.False(doc.FindActor("Fireplace_FX_1")!.WaterVolume);
    }

    [Fact]
    public void TheDumpJsonCarriesAssetPathLocalBoundsAndWaterVolume()
    {
        using var json = JsonDocument.Parse(Load().ToJson());
        var actors = json.RootElement.GetProperty("actors").EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
        JsonElement Component(string actor, string name) =>
            actors[actor].GetProperty("components").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

        var flames = Component("Fireplace_FX_1", "Flames");
        Assert.Equal(Flames, flames.GetProperty("assetPath").GetString());
        Assert.False(flames.TryGetProperty("localBounds", out _));
        Assert.Equal(FireEvent, Component("Fireplace_FX_1", "FireSound/AkAudioComponent0").GetProperty("assetPath").GetString());

        var box = Component("Fireplace_FX_1", "HeatBox").GetProperty("localBounds");
        Assert.Equal(-100f, box.GetProperty("min")[0].GetSingle(), Tolerance);
        Assert.Equal(20f, box.GetProperty("max")[2].GetSingle(), Tolerance);

        var brush = Component("PhysicsVolume_Lake", "BrushComponent0").GetProperty("localBounds");
        Assert.Equal(-1000f, brush.GetProperty("min")[1].GetSingle(), Tolerance);
        Assert.Equal(300f, brush.GetProperty("max")[2].GetSingle(), Tolerance);

        Assert.True(actors["PhysicsVolume_Lake"].GetProperty("waterVolume").GetBoolean());
        Assert.False(actors["PostProcessVolume_1"].TryGetProperty("waterVolume", out _)); // left out when false
    }

    private static LevelDocument Load()
    {
        using var temp = new LevelTempDirectory();
        BuildLevel().WriteAsync(Path.Combine([temp.Path, .. ("SCUM/Content/ConZ_Files/Maps/The_Island/" + LevelName).Split('/')]), ".umap")
            .GetAwaiter().GetResult();
        using var catalog = AssetCatalog.OpenLoose(temp.Path);
        return LevelDocument.Load(new Cue4ParseLevelReader(catalog), LevelPath);
    }

    /// <summary>
    /// A fireplace's effects actor (flames, Niagara fire, crackle, ash decal, heat shapes and a child actor that plays the
    /// Wwise fire event), a stand-alone Wwise ambient sound, a lake's swim volume with a brush, and a post-process volume.
    /// </summary>
    private static PackageBytes BuildLevel()
    {
        var p = new SyntheticLevelPackage();
        var world = p.Export(LevelName, p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var flames = Asset(p, Flames, Engine, "ParticleSystem");
        var niagara = Asset(p, NiagaraFire, "/Script/Niagara", "NiagaraSystem");
        var fireEvent = Asset(p, FireEvent, AkAudio, "AkAudioEvent");
        var crackle = Asset(p, Crackle, Engine, "SoundCue");
        var ash = Asset(p, AshDecal, Engine, "MaterialInstanceConstant");
        var ambientClass = p.ScriptClass(AkAudio, "AkAmbientSound");

        var fire = p.Export("Fireplace_FX_1", p.ScriptClass(Engine, "Actor"), level);
        var fireRoot = p.Export("Root", p.ScriptClass(Engine, "SceneComponent"), fire);
        var particles = p.Export("Flames", p.ScriptClass(Engine, "ParticleSystemComponent"), fire);
        var niagaraFire = p.Export("Flames_Niagara", p.ScriptClass("/Script/Niagara", "NiagaraComponent"), fire);
        var audio = p.Export("Crackle", p.ScriptClass(Engine, "AudioComponent"), fire);
        var decal = p.Export("Ash", p.ScriptClass(Engine, "DecalComponent"), fire);
        var heatBox = p.Export("HeatBox", p.ScriptClass(Engine, "BoxComponent"), fire);
        var heatSphere = p.Export("HeatSphere", p.ScriptClass(Engine, "SphereComponent"), fire);
        var heatCapsule = p.Export("HeatCapsule", p.ScriptClass(Engine, "CapsuleComponent"), fire);
        var childSound = p.Export("FireSound", p.ScriptClass(Engine, "ChildActorComponent"), fire);
        var childTemplate = p.Export("FireSound_GEN_VARIABLE_AkAmbientSound_CAT", ambientClass, childSound);
        var childAk = p.Export("AkAudioComponent0", p.ScriptClass(AkAudio, "AkComponent"), childTemplate);

        var ambient = p.Export("AkAmbientSound_1", ambientClass, level);
        var ambientAk = p.Export("AkAudioComponent0", p.ScriptClass(AkAudio, "AkComponent"), ambient);

        var lake = p.Export("PhysicsVolume_Lake", p.ScriptClass(Engine, "PhysicsVolume"), level);
        var lakeBrush = p.Export("BrushComponent0", p.ScriptClass(Engine, "BrushComponent"), lake);
        var lakeBody = p.Export("BodySetup_0", p.ScriptClass(Engine, "BodySetup"), lakeBrush);

        var post = p.Export("PostProcessVolume_1", p.ScriptClass(Engine, "PostProcessVolume"), level);
        var postBrush = p.Export("BrushComponent0", p.ScriptClass(Engine, "BrushComponent"), post);

        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => LevelNative(w, [fire, ambient, lake, post])));

        p.SetPayload(fire, p.Properties(t => t.Object("RootComponent", fireRoot)));
        p.SetPayload(fireRoot, p.Properties(t => t.Vector("RelativeLocation", 1000, 2000, 50)));
        p.SetPayload(particles, p.Properties(t =>
        {
            t.Object("AttachParent", fireRoot);
            t.Object("Template", flames);
        }));
        p.SetPayload(niagaraFire, p.Properties(t =>
        {
            t.Object("AttachParent", fireRoot);
            t.Object("Asset", niagara);
        }));
        p.SetPayload(audio, p.Properties(t =>
        {
            t.Object("AttachParent", fireRoot);
            t.Object("Sound", crackle);
        }));
        p.SetPayload(decal, p.Properties(t =>
        {
            t.Object("AttachParent", fireRoot);
            t.Object("DecalMaterial", ash);
        }));
        p.SetPayload(heatBox, p.Properties(t =>
        {
            t.Object("AttachParent", fireRoot);
            t.Vector("BoxExtent", 100, 50, 20);
        }));
        p.SetPayload(heatSphere, p.Properties(t =>
        {
            t.Object("AttachParent", fireRoot);
            t.Float("SphereRadius", 150);
        }));
        p.SetPayload(heatCapsule, p.Properties(t => t.Object("AttachParent", fireRoot)));
        p.SetPayload(childSound, p.Properties(t =>
        {
            t.Object("AttachParent", fireRoot);
            t.Vector("RelativeLocation", 0, 0, 40);
            t.Object("ChildActorClass", ambientClass);
            t.Object("ChildActorTemplate", childTemplate);
        }));
        p.SetPayload(childTemplate, p.Properties());
        p.SetPayload(childAk, p.Properties(t => t.Object("AkAudioEvent", fireEvent)));

        p.SetPayload(ambient, p.Properties(t => t.Object("RootComponent", ambientAk)));
        p.SetPayload(ambientAk, p.Properties(t =>
        {
            t.Vector("RelativeLocation", -300, 400, 10);
            t.Object("AkAudioEvent", fireEvent);
        }));

        p.SetPayload(lake, p.Properties(t =>
        {
            t.Object("RootComponent", lakeBrush);
            t.Bool("bWaterVolume", true);
        }));
        p.SetPayload(lakeBrush, p.Properties(t =>
        {
            t.Vector("RelativeLocation", 5000, 0, -200);
            t.Object("BrushBodySetup", lakeBody);
        }));
        // UBodySetup: tagged AggGeom, no object guid, BodySetupGuid, bCooked (the layout LevelPackageEditor writes).
        p.SetPayload(lakeBody, p.Properties(
            t => t.TaggedStruct("AggGeom", "KAggregateGeom", g => g.StructArray("BoxElems", "KBoxElem", e =>
            {
                e.Vector("Center", 0, 0, 100);
                e.Rotator("Rotation", 0, 90, 0);
                e.Float("X", 2000);
                e.Float("Y", 1000);
                e.Float("Z", 400);
            })),
            w =>
            {
                w.Guid(new FGuid(1, 2, 3, 4));
                w.I32(0);
            }));

        p.SetPayload(post, p.Properties(t => t.Object("RootComponent", postBrush)));
        p.SetPayload(postBrush, p.Properties(t => t.Vector("RelativeLocation", 0, 0, 0)));
        return p.Build();
    }

    private static int Asset(SyntheticLevelPackage p, string objectPath, string classPackage, string className)
    {
        var dot = objectPath.LastIndexOf('.');
        return p.ObjectImport(objectPath[..dot], classPackage, className, objectPath[(dot + 1)..]);
    }

    /// <summary>ULevel native data after the tagged properties (as <see cref="SyntheticLevels"/> writes it).</summary>
    internal static void LevelNative(ByteWriter w, IReadOnlyList<int> actors, string levelName = LevelName)
    {
        w.I32(actors.Count);
        foreach (var a in actors)
        {
            w.I32(a);
        }

        w.FString("unreal");
        w.FString(string.Empty);
        w.FString(levelName);
        w.FString(string.Empty);
        w.I32(0);
        w.I32(7777);
        w.I32(1);
        for (var i = 0; i < 5; i++)
        {
            w.I32(0); // Model, ModelComponents, LevelScriptActor, NavListStart, NavListEnd
        }

        for (var i = 0; i < 4; i++)
        {
            w.F32(0); // precomputed visibility: origin XY, cell size XY/Z
        }

        w.I32(0);
        w.I32(0);
        w.I32(0);
        for (var i = 0; i < 7; i++)
        {
            w.F32(0); // volume distance field: max distance, box
        }

        w.U8(0);
        for (var i = 0; i < 4; i++)
        {
            w.I32(0);
        }
    }

    private static void AssertNear(FVector expected, FVector actual) =>
        Assert.True(expected.Equals(actual, Tolerance), $"Expected {expected}, got {actual}.");

    private static void AssertBox(FVector min, FVector max, BoundingBox? actual)
    {
        Assert.NotNull(actual);
        AssertNear(min, FVector.FromVector3(actual.Value.Min));
        AssertNear(max, FVector.FromVector3(actual.Value.Max));
    }
}
