using System.Buffers.Binary;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Export;
using ScumStudio.Level.Model;

namespace ScumStudio.Tests.Level;

/// <summary>
/// The copy guard (D15, D04): lights and particle effects are copied (their copies: <see cref="FxCopyTests"/>), a brush with
/// native data the writer cannot carry is refused only when it goes into another level (and the Map says so at the paste),
/// and what is safe is re-indexed into the target level: a field path's owner, a string table id and a body's cooked format
/// name.
/// </summary>
public sealed class CopyGuardTests
{
    private const string Engine = "/Script/Engine";
    private const string SourceLevel = "/Game/ConZ_Files/Maps/The_Island/A_0_Source";

    [Fact]
    public void ALightIsCopiedInItsOwnLevelAndIntoAnother()
    {
        var source = Source();
        Assert.Null(LevelPackageEditor.CheckCopy(source, "Lamp_1", otherLevel: false));
        Assert.Null(LevelPackageEditor.CheckCopy(source, "Lamp_1", otherLevel: true));

        var (_, report) = LevelPackageEditor.Apply(source, new LevelEditRequest { Copies = [new ActorCopy("Lamp_1", "Lamp_Copy", null)] });
        Assert.Equal(["Lamp_Copy"], report.AddedActors);
        var (_, foreign) = LevelPackageEditor.Apply(Target(), new LevelEditRequest { ForeignCopies = [new ForeignActorCopy(source, "Lamp_1", "Lamp_Added", null)] });
        Assert.Equal(["Lamp_Added"], foreign.AddedActors);
    }

    [Fact]
    public void TheMapRefusesAVolumeOnlyForAPasteIntoAnotherLevel()
    {
        var brush = new ComponentRecord(1, "BrushComponent0", "BrushComponent", true, null, FTransform.Identity, FTransform.Identity, null, []);
        var volume = new ActorRecord(0, "Volume_1", "/Script/Engine.BlockingVolume", 1, [brush], FTransform.Identity, ActorKind.Volume, null, []);
        Assert.Equal(new CopyRefusal(CopyRefusalReason.NativeData, "BrushComponent0", "BrushComponent"), LevelPackageEditor.RefusedCopy(volume, null, otherLevel: true));
        Assert.Null(LevelPackageEditor.RefusedCopy(volume, null, otherLevel: false));

        var light = new ComponentRecord(1, "LightComponent0", "PointLightComponent", true, null, FTransform.Identity, FTransform.Identity, null, []);
        Assert.Null(LevelPackageEditor.RefusedCopy(new ActorRecord(0, "Lamp_1", "/Script/Engine.PointLight", 1, [light], FTransform.Identity, ActorKind.Light, null, []), null, otherLevel: true));
    }

    [Fact]
    public void ABrushWithNativeDataIsRefusedOnlyForAnotherLevel()
    {
        var source = Source();
        Assert.Equal(CopyRefusalReason.NativeData, LevelPackageEditor.CheckCopy(source, "Volume_1", otherLevel: true)?.Reason);
        Assert.Null(LevelPackageEditor.CheckCopy(source, "Volume_1", otherLevel: false));

        var (_, foreign) = LevelPackageEditor.Apply(Target(), new LevelEditRequest { ForeignCopies = [new ForeignActorCopy(source, "Volume_1", "Volume_Added", null)] });
        Assert.Empty(foreign.AddedActors);
        Assert.Contains(foreign.Warnings, w => w.Contains("BrushComponent0 (BrushComponent) holds native data", StringComparison.Ordinal));

        var (_, same) = LevelPackageEditor.Apply(source, new LevelEditRequest { Copies = [new ActorCopy("Volume_1", "Volume_Copy", null)] });
        Assert.Equal(new[] { "Volume_Copy" }, same.AddedActors);
    }

    [Fact]
    public void ACopyIntoAnotherLevelReindexesFieldPathOwnersStringTablesAndBodyFormats()
    {
        var source = Source();
        Assert.Null(LevelPackageEditor.CheckCopy(source, "Box_1", otherLevel: true));
        var (bytes, report) = LevelPackageEditor.Apply(Target(), new LevelEditRequest { ForeignCopies = [new ForeignActorCopy(source, "Box_1", "Box_Added", null)] });
        Assert.Equal(new[] { "Box_Added" }, report.AddedActors);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("Box_Added", StringComparison.Ordinal));

        var written = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SyntheticLevels.LevelPath);
        var actor = Index(written, "Box_Added", outer: null);
        var root = Index(written, "Root", outer: actor + 1);
        var body = Index(written, "Body", outer: root + 1);

        // The field path's owner is the copied component (not the source's index), its path name the same text.
        var properties = written.ReadProperties(root);
        var field = Assert.IsType<FieldPathValue>(properties.Find("Field")!.Value);
        Assert.Equal(root + 1, field.Owner);
        Assert.Equal(new[] { "SomeProp" }, field.Path);

        // The string table id names the same table in the target's name table.
        var text = Assert.IsType<TextValue>(properties.Find("Label")!.Value);
        Assert.Equal("ST_Test", text.TableId);

        // The body's cooked format name (after the tags: guid flag, BodySetupGuid, bCooked, count) names PhysXPC there too.
        var payload = written.GetExportBytes(body);
        var end = written.ReadProperties(body).EndOffset;
        var format = new FNameRef(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(end + 4 + 16 + 4 + 4)), BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(end + 4 + 16 + 4 + 8)));
        Assert.Equal("PhysXPC", written.ResolveName(format));
    }

    private static int Index(CookedPackage package, string name, int? outer)
    {
        for (var i = package.Exports.Count - 1; i >= 0; i--)
        {
            if (package.ResolveName(package.Exports[i].ObjectName) == name && (outer is null || package.Exports[i].OuterIndex == outer))
            {
                return i;
            }
        }

        throw new InvalidOperationException(name + " not found");
    }

    private static CookedPackage Target()
    {
        var bytes = SyntheticLevels.BuildLevel();
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SyntheticLevels.LevelPath);
    }

    /// <summary>
    /// A_0_Source: a lamp (a point light component), a box whose root has a field path, a string table text and a cooked
    /// body, and a volume whose brush has native data. Names are added first so their indices differ from the target's.
    /// </summary>
    private static CookedPackage Source()
    {
        var p = new SyntheticLevelPackage();
        foreach (var padding in new[] { "Pad_A", "Pad_B", "Pad_C", "ST_Test", "PhysXPC", "SomeProp" })
        {
            p.Name(padding);
        }

        var world = p.Export("A_0_Source", p.ScriptClass(Engine, "World"), 0);
        var level = p.Export("PersistentLevel", p.ScriptClass(Engine, "Level"), world);
        var lamp = p.Export("Lamp_1", p.ScriptClass(Engine, "Actor"), level);
        var lampRoot = p.Export("Root", p.ScriptClass(Engine, "SceneComponent"), lamp);
        var light = p.Export("LightComponent0", p.ScriptClass(Engine, "PointLightComponent"), lamp);
        var box = p.Export("Box_1", p.ScriptClass(Engine, "Actor"), level);
        var boxRoot = p.Export("Root", p.ScriptClass(Engine, "SceneComponent"), box);
        var body = p.Export("Body", p.ScriptClass(Engine, "BodySetup"), boxRoot);
        var volume = p.Export("Volume_1", p.ScriptClass(Engine, "BlockingVolume"), level);
        var brush = p.Export("BrushComponent0", p.ScriptClass(Engine, "BrushComponent"), volume);

        p.SetPayload(world, p.Properties(native: w =>
        {
            w.I32(level);
            w.I32(0);
            w.I32(0);
        }));
        p.SetPayload(level, p.Properties(native: w => SyntheticLevels.LevelNative(w, [lamp, box, volume])));
        p.SetPayload(lamp, p.Properties(t => t.Object("RootComponent", lampRoot)));
        p.SetPayload(lampRoot, p.Properties(t => t.Vector("RelativeLocation", 0, 0, 100)));
        p.SetPayload(light, p.Properties(t => t.Object("AttachParent", lampRoot)));
        p.SetPayload(box, p.Properties(t => t.Object("RootComponent", boxRoot)));
        p.SetPayload(boxRoot, p.Properties(t =>
        {
            t.Vector("RelativeLocation", 10, 20, 30);
            t.Typed("Field", "FieldPathProperty", w =>
            {
                w.I32(1);
                w.FName(p.Name("SomeProp"));
                w.I32(boxRoot); // the owner: the component itself
            });
            t.Typed("Label", "TextProperty", w =>
            {
                w.U32(0);
                w.U8(11); // StringTableEntry
                w.FName(p.Name("ST_Test"));
                w.FString("Key");
            });
        }));
        p.SetPayload(body, p.Properties(native: w =>
        {
            w.Guid(new FGuid(1, 2, 3, 4)); // BodySetupGuid
            w.I32(1); // bCooked
            w.I32(1); // one format
            w.FName(p.Name("PhysXPC"));
            w.U32(0x40); // inline payload
            w.I32(4); // elements
            w.I32(4); // size on disk
            w.I64(1234); // offset in the source file
            w.Raw(new byte[] { 1, 2, 3, 4 });
        }));
        p.SetPayload(volume, p.Properties(t => t.Object("RootComponent", brush)));
        p.SetPayload(brush, p.Properties(native: w => w.I32(7))); // native data the writer does not know
        var bytes = p.Build();
        return CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, SourceLevel);
    }
}
