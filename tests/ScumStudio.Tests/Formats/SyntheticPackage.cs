using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Tests.Formats;

/// <summary>Builds small cooked-style packages and tagged property payloads in memory for unit tests.</summary>
internal sealed class SyntheticPackage
{
    public List<string> Names { get; } =
    [
        "/Script/CoreUObject", "/Script/Engine", "Package", "Class", "Actor", "Default__Actor", "MyActor", "None",
        "FloatProperty", "IntProperty", "BoolProperty", "StructProperty", "Vector", "Rotator", "RelativeLocation",
        "RelativeRotation", "Health", "bHidden", "ArrayProperty", "Points", "Transform", "Translation", "Scale3D",
        "Offset", "EnumProperty", "EMyEnum", "EMyEnum::Two", "Mode", "NameProperty", "Tag", "MapProperty", "Lookup",
        "StrProperty", "Label", "ObjectProperty", "Owner", "Things", "Count",
    ];

    public int N(string name) => Names.IndexOf(name) is var i and >= 0 ? i : throw new KeyNotFoundException(name);

    public static PackageSummary DefaultSummary() => new()
    {
        LegacyFileVersion = -7,
        LegacyUE3Version = 864,
        FileVersionUE4 = 0,
        FileVersionLicensee = 0,
        FolderName = "None",
        PackageFlags = PackageSummary.PkgFilterEditorOnly,
        Guid = new FGuid(1, 2, 3, 4),
        SavedByEngineVersion = new EngineVersionInfo(0, 0, 0, 0, string.Empty),
        CompatibleWithEngineVersion = new EngineVersionInfo(0, 0, 0, 0, string.Empty),
        ChunkIds = [],
    };

    public void Tag(ByteWriter w, string name, string type, int size, Action<ByteWriter>? extra = null)
    {
        w.FName(new FNameRef(N(name)));
        w.FName(new FNameRef(N(type)));
        w.I32(size);
        w.I32(0);
        extra?.Invoke(w);
    }

    public void None(ByteWriter w) => w.FName(new FNameRef(N("None")));

    /// <summary>
    /// A payload exercising the common tag kinds: float, int, bool, native Vector/Rotator, tagged Transform,
    /// array of Vector structs, enum, name, map int->float, string, object; then None + bHasGuid + 8 native bytes.
    /// </summary>
    public byte[] BuildPayload()
    {
        var w = new ByteWriter();
        Tag(w, "Health", "FloatProperty", 4, x => x.U8(0));
        w.F32(75.5f);
        Tag(w, "Count", "IntProperty", 4, x => x.U8(0));
        w.I32(-3);
        Tag(w, "bHidden", "BoolProperty", 0, x => { x.U8(1); x.U8(0); });
        Tag(w, "RelativeLocation", "StructProperty", 12, x => { x.FName(new FNameRef(N("Vector"))); x.Guid(default); x.U8(0); });
        w.F32(100f);
        w.F32(-200f);
        w.F32(300.25f);
        Tag(w, "RelativeRotation", "StructProperty", 12, x => { x.FName(new FNameRef(N("Rotator"))); x.Guid(default); x.U8(0); });
        w.F32(10f);
        w.F32(90f);
        w.F32(0f);

        // tagged Transform { Translation, Scale3D }
        var t = new ByteWriter();
        Tag(t, "Translation", "StructProperty", 12, x => { x.FName(new FNameRef(N("Vector"))); x.Guid(default); x.U8(0); });
        t.F32(1f);
        t.F32(2f);
        t.F32(3f);
        Tag(t, "Scale3D", "StructProperty", 12, x => { x.FName(new FNameRef(N("Vector"))); x.Guid(default); x.U8(0); });
        t.F32(1.25f);
        t.F32(1.4f);
        t.F32(1.15f);
        None(t);
        Tag(w, "Offset", "StructProperty", t.Length, x => { x.FName(new FNameRef(N("Transform"))); x.Guid(default); x.U8(0); });
        w.Raw(t.WrittenSpan);

        // Array<Vector> with inner struct tag
        var a = new ByteWriter();
        a.I32(2);
        Tag(a, "Points", "StructProperty", 24, x => { x.FName(new FNameRef(N("Vector"))); x.Guid(default); x.U8(0); });
        a.F32(1f);
        a.F32(2f);
        a.F32(3f);
        a.F32(4f);
        a.F32(5f);
        a.F32(6f);
        Tag(w, "Points", "ArrayProperty", a.Length, x => { x.FName(new FNameRef(N("StructProperty"))); x.U8(0); });
        w.Raw(a.WrittenSpan);

        Tag(w, "Mode", "EnumProperty", 8, x => { x.FName(new FNameRef(N("EMyEnum"))); x.U8(0); });
        w.FName(new FNameRef(N("EMyEnum::Two")));
        Tag(w, "Tag", "NameProperty", 8, x => x.U8(0));
        w.FName(new FNameRef(N("Health"), 3));

        var m = new ByteWriter();
        m.I32(0);
        m.I32(2);
        m.I32(1);
        m.F32(0.5f);
        m.I32(2);
        m.F32(0.25f);
        Tag(w, "Lookup", "MapProperty", m.Length, x => { x.FName(new FNameRef(N("IntProperty"))); x.FName(new FNameRef(N("FloatProperty"))); x.U8(0); });
        w.Raw(m.WrittenSpan);

        var s = new ByteWriter();
        s.FString("hello");
        Tag(w, "Label", "StrProperty", s.Length, x => x.U8(0));
        w.Raw(s.WrittenSpan);
        Tag(w, "Owner", "ObjectProperty", 4, x => x.U8(0));
        w.I32(-2);

        None(w);
        w.I32(0); // bHasGuid
        w.U64(0x1122334455667788);
        return w.ToArray();
    }

    /// <summary>Builds a one-export package whose export uses <see cref="BuildPayload"/>.</summary>
    public PackageBytes BuildPackage(byte[]? payload = null)
    {
        var imports = new List<ImportEntry>
        {
            new(new FNameRef(N("/Script/CoreUObject")), new FNameRef(N("Package")), 0, new FNameRef(N("/Script/Engine"))),
            new(new FNameRef(N("/Script/CoreUObject")), new FNameRef(N("Class")), -1, new FNameRef(N("Actor"))),
            new(new FNameRef(N("/Script/Engine")), new FNameRef(N("Actor")), -1, new FNameRef(N("Default__Actor"))),
        };
        var exports = new List<ExportEntry>
        {
            new()
            {
                ClassIndex = -2,
                TemplateIndex = -3,
                ObjectName = new FNameRef(N("MyActor")),
                ObjectFlags = 0x8,
                IsAsset = 1,
                FirstExportDependency = 0,
                SerializationBeforeCreateDependencies = 2,
            },
        };
        return PackageWriter.Build(new PackageBuildInput
        {
            Summary = DefaultSummary(),
            Names = Names,
            Imports = imports,
            Exports = exports,
            ExportData = [payload ?? BuildPayload()],
            PreloadDependencies = [-2, -3],
        });
    }
}
