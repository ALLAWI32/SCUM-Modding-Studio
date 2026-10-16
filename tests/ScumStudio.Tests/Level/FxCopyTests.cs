using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Copies of lights, Niagara effects and meshes (D15, D04; the real game files: <see cref="FxCopyRealTests"/>). A Niagara
/// component's user parameters are natively serialized structs the reader could not decode: a copy into another level
/// carried them with the source level's name indices and its type import (the game reads other names, other types or
/// none). Every copy now gets those re-indexed, its own <c>LightGuid</c> and <c>MapBuildDataId</c> (as UE gives a moved or
/// duplicated component), and its level as the create-before-create dependency every cooked actor has.
/// </summary>
public sealed class FxCopyTests
{
    private const string Engine = "/Script/Engine";
    private const string Niagara = "/Script/Niagara";
    private const string SourceLevel = "/Game/ConZ_Files/Maps/The_Island/A_0_Fx";
    private static readonly FGuid LampGuid = new(0x11111111, 0x22222222, 0x33333333, 0x44444444);
    private static readonly FGuid PostBuildId = new(0x55555555, 0x66666666, 0x77777777, 0x88888888);

    [Fact]
    public void TheReaderDecodesANiagaraComponentsUserParameters()
    {
        var source = Source();
        var block = source.ReadProperties(Index(source, "NiagaraComponent0", "Moths_1"));
        Assert.DoesNotContain(Values(block.Properties), v => v is RawValue);
        var (sorted, redirect) = Parameters(block);
        Assert.Equal(["User.SpawnRate", "User.Color"], sorted);
        Assert.Equal(("User.SpawnRate", "Emitter.SpawnRate"), redirect);
    }

    [Fact]
    public void ALightAnEffectAndAMeshPastedIntoAnotherLevelKeepTheirNamesAndGetTheirLevelAndKeysOfTheirOwn()
    {
        var source = Source();
        var target = Target();
        var (bytes, report) = LevelPackageEditor.Apply(target, new LevelEditRequest
        {
            ForeignCopies = [new(source, "Lamp_1", "Lamp_Added", null), new(source, "Moths_1", "Moths_Added", null), new(source, "Post_1", "Post_Added", null)],
        });
        Assert.Equal(["Lamp_Added", "Moths_Added", "Post_Added"], report.AddedActors);
        Assert.Empty(report.Warnings);
        var written = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SyntheticLevels.LevelPath);
        Assert.Empty(PackageLint.Check(written).Except(PackageLint.Check(target))); // no FName the copy added breaks a rule

        // The effect's user parameters: their names and their type (an import) name the same things in the target.
        var block = written.ReadProperties(Index(written, "NiagaraComponent0", "Moths_Added"));
        Assert.DoesNotContain(Values(block.Properties), v => v is RawValue);
        var (sorted, redirect) = Parameters(block);
        Assert.Equal(["User.SpawnRate", "User.Color"], sorted);
        Assert.Equal(("User.SpawnRate", "Emitter.SpawnRate"), redirect);
        var types = Values(block.Properties).OfType<ObjectValue>().Where(o => o.Index < 0).Select(o => written.GetFullPath(o.Index)).Distinct().ToList();
        Assert.Equal(["/Game/FX/NS_Moths.NS_Moths", "/Script/Niagara.NiagaraFloat"], types.Order(StringComparer.Ordinal));

        // Keys of its own: the light's guid and the mesh's build data id are new (and the source's stay).
        Assert.NotEqual(LampGuid, LightGuid(written, "Lamp_Added"));
        Assert.NotEqual(default, LightGuid(written, "Lamp_Added"));
        Assert.NotEqual(PostBuildId, MapBuildDataId(written, "Post_Added"));
        Assert.Equal(LampGuid, LightGuid(source, "Lamp_1"));

        // Every copied actor waits for its level, as cooked actors do (the source's level is not in this package).
        var level = LevelPackageEditor.FindLevelExport(written) + 1;
        foreach (var actor in report.AddedActors)
        {
            Assert.Equal([level], CreateBeforeCreate(written, Index(written, actor, null)));
        }

        Assert.Equal([Index(written, "Moths_Added", null) + 1], CreateBeforeCreate(written, Index(written, "NiagaraComponent0", "Moths_Added")));
    }

    [Fact]
    public void ALightAnEffectAndAMeshDuplicatedInTheirLevelGetKeysOfTheirOwnRepeatably()
    {
        var source = Source();
        var request = new LevelEditRequest { Copies = [new("Lamp_1", "Lamp_Copy", null), new("Moths_1", "Moths_Copy", null), new("Post_1", "Post_Copy", null)] };
        var (bytes, report) = LevelPackageEditor.Apply(source, request);
        Assert.Equal(["Lamp_Copy", "Moths_Copy", "Post_Copy"], report.AddedActors);
        var written = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SourceLevel);
        Assert.NotEqual(LampGuid, LightGuid(written, "Lamp_Copy"));
        Assert.Equal(LampGuid, LightGuid(written, "Lamp_1"));
        Assert.NotEqual(PostBuildId, MapBuildDataId(written, "Post_Copy"));
        Assert.Equal(PostBuildId, MapBuildDataId(written, "Post_1"));
        var (sorted, redirect) = Parameters(written.ReadProperties(Index(written, "NiagaraComponent0", "Moths_Copy")));
        Assert.Equal(["User.SpawnRate", "User.Color"], sorted);
        Assert.Equal(("User.SpawnRate", "Emitter.SpawnRate"), redirect);

        // The same edit writes the same bytes (the keys derive from the level and the copy, not from chance).
        var (again, _) = LevelPackageEditor.Apply(source, request);
        Assert.Equal(bytes.UExp, again.UExp);
    }

    [Fact]
    public void AMeshWhoseNativeDataHasAnotherLayoutIsCopiedAsItIs()
    {
        var source = Source(extraMeshData: true);
        var (bytes, _) = LevelPackageEditor.Apply(Target(), new LevelEditRequest { ForeignCopies = [new(source, "Post_1", "Post_Added", null)] });
        var written = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SyntheticLevels.LevelPath);
        var copied = written.GetExportBytes(Index(written, "StaticMeshComponent0", "Post_Added"));
        var original = source.GetExportBytes(Index(source, "StaticMeshComponent0", "Post_1"));
        var copiedTail = copied.AsSpan(written.ReadProperties(Index(written, "StaticMeshComponent0", "Post_Added")).EndOffset).ToArray();
        var originalTail = original.AsSpan(source.ReadProperties(Index(source, "StaticMeshComponent0", "Post_1")).EndOffset).ToArray();
        Assert.Equal(originalTail, copiedTail); // not understood, not written into
    }

    private static (List<string> Sorted, (string Key, string Value) Redirect) Parameters(PropertyBlock block)
    {
        var store = Assert.IsType<StructValue>(block.Find("OverrideParameters")!.Value);
        var sorted = Assert.IsType<ArrayValue>(store.Find("SortedParameterOffsets")!.Value).Items.Select(i => VariableName(i)).ToList();
        var entry = Assert.Single(Assert.IsType<MapValue>(store.Find("UserParameterRedirects")!.Value).Entries);
        return (sorted, (VariableName(entry.Key), VariableName(entry.Value)));

        static string VariableName(PropertyValue value) => Assert.IsType<NameValue>(Assert.IsType<NativeStructValue>(value).Fields[0].Value).Value;
    }

    private static FGuid LightGuid(CookedPackage package, string actor)
    {
        var block = package.ReadProperties(Index(package, "LightComponent0", actor));
        return Assert.IsType<GuidValue>(block.Find("LightGuid")!.Value).Value;
    }

    /// <summary>The mesh's first LOD build data id: after the tags, the guid flag (0), the LOD count and the strip flags.</summary>
    private static FGuid MapBuildDataId(CookedPackage package, string actor)
    {
        var index = Index(package, "StaticMeshComponent0", actor);
        var payload = package.GetExportBytes(index);
        var at = package.ReadProperties(index).EndOffset + 4 + 4 + 2;
        return new FGuid(
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at + 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at + 8)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(at + 12)));
    }

    private static int[] CreateBeforeCreate(CookedPackage package, int export)
    {
        var e = package.Exports[export];
        var start = e.FirstExportDependency + e.SerializationBeforeSerializationDependencies + e.CreateBeforeSerializationDependencies + e.SerializationBeforeCreateDependencies;
        return e.FirstExportDependency < 0 ? [] : package.ReadPreloadDependencies().Skip(start).Take(e.CreateBeforeCreateDependencies).ToArray();
    }

    /// <summary>The export named <paramref name="name"/> under the actor named <paramref name="actor"/> (or directly under the level when null).</summary>
    private static int Index(CookedPackage package, string name, string? actor)
    {
        for (var i = package.Exports.Count - 1; i >= 0; i--)
        {
            var outer = package.Exports[i].OuterIndex - 1;
            if (package.ResolveName(package.Exports[i].ObjectName) == name
                && (actor is null ? outer >= 0 && package.GetExportClassName(outer) == "Level" : outer >= 0 && package.ResolveName(package.Exports[outer].ObjectName) == actor))
            {
                return i;
            }
        }

        throw new InvalidOperationException(name + " not found");
    }

    private static IEnumerable<PropertyValue> Values(IEnumerable<PropertyTag> tags) => tags.SelectMany(t => Values(t.Value));

    private static IEnumerable<PropertyValue> Values(PropertyValue value) =>
        new[] { value }.Concat(value switch
        {
            StructValue s => Values(s.Properties),
            ArrayValue a => a.Items.SelectMany(Values),
            MapValue m => m.Entries.SelectMany(e => Values(e.Key).Concat(Values(e.Value))),
            NativeStructValue n => n.Fields.SelectMany(f => Values(f.Value)),
            _ => [],
        });

    private static CookedPackage Target()
    {
        var bytes = SyntheticLevels.BuildLevel();
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SyntheticLevels.LevelPath);
    }

    /// <summary>
    /// A_0_Fx: a point light (its LightGuid stored), a Niagara actor whose component has user parameters (two sorted
    /// parameters, one redirect) and an actor whose mesh has one LOD build data id; every export with the dependencies a
    /// cooker writes. Names are added first so their indices differ from the target's. <paramref name="extraMeshData"/>: the
    /// mesh's native data goes on after its LODData (a layout the copier does not know).
    /// </summary>
    private static CookedPackage Source(bool extraMeshData = false)
    {
        var p = new SyntheticLevelPackage();
        foreach (var padding in new[] { "Pad_A", "Pad_B", "Pad_C", "Pad_D", "User.Color", "User.SpawnRate", "Emitter.SpawnRate", "NiagaraFloat", "UnderlyingType" })
        {
            p.Name(padding);
        }

        var niagaraPackage = p.PackageImport(Niagara);
        var floatType = p.Import("/Script/CoreUObject", "ScriptStruct", niagaraPackage, "NiagaraFloat");
        var system = p.ObjectImport("/Game/FX/NS_Moths", Niagara, "NiagaraSystem", "NS_Moths");
        var world = p.Export("A_0_Fx", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var lamp = p.Export("Lamp_1", p.ScriptClass(Engine, "PointLight"), level);
        var light = p.Export("LightComponent0", p.ScriptClass(Engine, "PointLightComponent"), lamp);
        var moths = p.Export("Moths_1", p.ScriptClass(Niagara, "NiagaraActor"), level);
        var effect = p.Export("NiagaraComponent0", p.ScriptClass(Niagara, "NiagaraComponent"), moths);
        var post = p.Export("Post_1", p.ScriptClass(Engine, "Actor"), level);
        var mesh = p.Export("StaticMeshComponent0", p.ScriptClass(Engine, "StaticMeshComponent"), post);

        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => SyntheticLevels.LevelNative(w, [lamp, moths, post])));
        p.SetPayload(lamp, p.Properties(t =>
        {
            t.Object("LightComponent", light);
            t.Object("RootComponent", light);
        }));
        p.SetPayload(light, p.Properties(t =>
        {
            t.Float("Intensity", 5000);
            t.NativeStruct("LightGuid", "Guid", w => w.Guid(LampGuid));
        }));
        p.SetPayload(moths, p.Properties(t =>
        {
            t.Object("NiagaraComponent", effect);
            t.Object("RootComponent", effect);
        }));
        p.SetPayload(effect, p.Properties(t =>
        {
            t.Object("Asset", system);
            t.TaggedStruct("OverrideParameters", "NiagaraUserRedirectionParameterStore", store =>
            {
                store.NativeStructArray("SortedParameterOffsets", "NiagaraVariableWithOffset", 2, w =>
                {
                    Variable(p, w, "User.SpawnRate", floatType);
                    w.I32(0); // offset
                    Variable(p, w, "User.Color", floatType);
                    w.I32(4);
                });
                store.StructMap("UserParameterRedirects", 1, w =>
                {
                    Variable(p, w, "User.SpawnRate", floatType);
                    w.I32(0); // no value bytes
                    Variable(p, w, "Emitter.SpawnRate", floatType);
                    w.I32(4);
                    w.F32(12.5f);
                });
            });
        }));
        p.SetPayload(post, p.Properties(t => t.Object("RootComponent", mesh)));
        p.SetPayload(mesh, p.Properties(t => t.Vector("RelativeLocation", 0, 0, 50), w =>
        {
            w.I32(1); // one LOD
            w.U8(1); // stripped: editor data
            w.U8(1); // stripped: override colours
            w.Guid(PostBuildId);
            if (extraMeshData)
            {
                w.I32(64); // something after the LODData (an instanced mesh's data, say)
            }
        }));

        p.Dependencies(lamp, [], [light], [p.ScriptClass(Engine, "PointLight")], [level]);
        p.Dependencies(light, [], [], [p.ScriptClass(Engine, "PointLightComponent")], [lamp]);
        p.Dependencies(moths, [], [effect], [p.ScriptClass(Niagara, "NiagaraActor")], [level]);
        p.Dependencies(effect, [], [system, floatType], [p.ScriptClass(Niagara, "NiagaraComponent")], [moths]);
        p.Dependencies(post, [], [mesh], [p.ScriptClass(Engine, "Actor")], [level]);
        p.Dependencies(mesh, [], [], [p.ScriptClass(Engine, "StaticMeshComponent")], [post]);
        var bytes = p.Build();
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SourceLevel);
    }

    /// <summary>FNiagaraVariableBase as 4.27 writes it: the name, then the type definition's tagged members.</summary>
    private static void Variable(SyntheticLevelPackage p, ByteWriter w, string name, int type)
    {
        w.FName(p.Name(name));
        var tags = new SyntheticLevelPackage.TagWriter(p, w);
        tags.Object("ClassStructOrEnum", type);
        tags.Typed("UnderlyingType", "UInt16Property", b => b.U16(0));
        tags.None();
    }
}
