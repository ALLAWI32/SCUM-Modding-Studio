using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Builds small cooked UE 4.27 packages (tagged properties, unversioned summary) with the in-house
/// <see cref="PackageWriter"/>, so the CUE4Parse level reader can be tested on real bytes without game files.
/// Package indices follow UE: exports are +1-based, imports -1-based.
/// </summary>
internal sealed class SyntheticLevelPackage
{
    private readonly List<string> _names = [];
    private readonly Dictionary<string, int> _nameIndex = new(StringComparer.Ordinal);
    private readonly List<ImportEntry> _imports = [];
    private readonly List<ExportEntry> _exports = [];
    private readonly List<byte[]> _payloads = [];
    private readonly Dictionary<(string Package, string Class), int> _classes = [];

    public SyntheticLevelPackage()
    {
        Name("None");
    }

    public FNameRef Name(string value)
    {
        if (!_nameIndex.TryGetValue(value, out var index))
        {
            index = _names.Count;
            _names.Add(value);
            _nameIndex[value] = index;
        }

        return new FNameRef(index);
    }

    /// <summary>Adds an import and returns its package index (negative).</summary>
    public int Import(string classPackage, string className, int outer, string objectName)
    {
        _imports.Add(new ImportEntry(Name(classPackage), Name(className), outer, Name(objectName)));
        return -_imports.Count;
    }

    /// <summary>Import of a package object (e.g. <c>/Script/Engine</c> or <c>/Game/X/SM_Y</c>).</summary>
    public int PackageImport(string packageName) => Import("/Script/CoreUObject", "Package", 0, packageName);

    /// <summary>Import of a native class, e.g. <c>ScriptClass("/Script/Engine", "StaticMeshActor")</c> (cached).</summary>
    public int ScriptClass(string package, string className)
    {
        if (!_classes.TryGetValue((package, className), out var index))
        {
            var pkg = _classes.TryGetValue((package, string.Empty), out var p) ? p : _classes[(package, string.Empty)] = PackageImport(package);
            index = Import("/Script/CoreUObject", "Class", pkg, className);
            _classes[(package, className)] = index;
        }

        return index;
    }

    /// <summary>Import of an object inside another package (e.g. a StaticMesh or a Blueprint class).</summary>
    public int ObjectImport(string packageName, string classPackage, string className, string objectName) =>
        Import(classPackage, className, PackageImport(packageName), objectName);

    /// <summary>Adds an export (payload set later) and returns its package index (positive).</summary>
    public int Export(string name, int classIndex, int outer, int template = 0, uint flags = 0x8)
    {
        _exports.Add(new ExportEntry
        {
            ClassIndex = classIndex,
            TemplateIndex = template,
            OuterIndex = outer,
            ObjectName = Name(name),
            ObjectFlags = flags,
            FirstExportDependency = -1,
        });
        _payloads.Add([]);
        return _exports.Count;
    }

    public void SetPayload(int exportIndex, byte[] payload) => _payloads[exportIndex - 1] = payload;

    /// <summary>Tagged properties + <c>None</c> + the UObject <c>bHasGuid</c> (0), then optional native data.</summary>
    public byte[] Properties(Action<TagWriter>? tags = null, Action<ByteWriter>? native = null)
    {
        var w = new ByteWriter();
        var t = new TagWriter(this, w);
        tags?.Invoke(t);
        t.None();
        w.I32(0); // bHasGuid
        native?.Invoke(w);
        return w.ToArray();
    }

    /// <summary>
    /// Builds the package. <paramref name="assetRegistryData"/> replaces the default empty asset registry block (which is
    /// where a cooked sublevel also carries its <c>FWorldTileInfo</c>); <paramref name="summary"/> lets a test carry the
    /// offsets of an earlier build (e.g. <c>WorldTileInfoDataOffset</c>) into a rebuild.
    /// </summary>
    public PackageBytes Build(ReadOnlyMemory<byte>? assetRegistryData = null, PackageSummary? summary = null) => PackageWriter.Build(new PackageBuildInput
    {
        Summary = summary ?? new PackageSummary
        {
            LegacyFileVersion = -7,
            LegacyUE3Version = 864,
            FileVersionUE4 = 0,
            FileVersionLicensee = 0,
            FolderName = "None",
            PackageFlags = PackageSummary.PkgFilterEditorOnly,
            Guid = new FGuid(7, 7, 7, 7),
            SavedByEngineVersion = new EngineVersionInfo(0, 0, 0, 0, string.Empty),
            CompatibleWithEngineVersion = new EngineVersionInfo(0, 0, 0, 0, string.Empty),
            ChunkIds = [],
        },
        Names = _names,
        Imports = _imports,
        Exports = _exports,
        ExportData = _payloads.Select(p => (ReadOnlyMemory<byte>)p).ToList(),
        AssetRegistryData = assetRegistryData ?? new byte[4],
    });

    /// <summary>Writes FPropertyTag-framed values (UE 4.27 tagged serialization).</summary>
    internal sealed class TagWriter(SyntheticLevelPackage package, ByteWriter writer)
    {
        public void None() => writer.FName(package.Name("None"));

        public void Object(string name, int packageIndex)
        {
            Header(name, "ObjectProperty", 4);
            writer.U8(0);
            writer.I32(packageIndex);
        }

        /// <summary>An array of object references (e.g. <c>OverrideMaterials</c>).</summary>
        public void ObjectArray(string name, params int[] packageIndices)
        {
            Header(name, "ArrayProperty", 4 + 4 * packageIndices.Length);
            writer.FName(package.Name("ObjectProperty"));
            writer.U8(0);
            writer.I32(packageIndices.Length);
            foreach (var index in packageIndices)
            {
                writer.I32(index);
            }
        }

        public void Bool(string name, bool value)
        {
            Header(name, "BoolProperty", 0);
            writer.U8(value ? (byte)1 : (byte)0);
            writer.U8(0);
        }

        public void Vector(string name, float x, float y, float z) => Struct(name, "Vector", w => { w.F32(x); w.F32(y); w.F32(z); });

        public void Rotator(string name, float pitch, float yaw, float roll) => Struct(name, "Rotator", w => { w.F32(pitch); w.F32(yaw); w.F32(roll); });

        public void Quat(string name, float x, float y, float z, float w) => Struct(name, "Quat", b => { b.F32(x); b.F32(y); b.F32(z); b.F32(w); });

        /// <summary>A struct serialized as nested tagged properties (e.g. <c>Transform</c>).</summary>
        public void TaggedStruct(string name, string structName, Action<TagWriter> inner) =>
            Struct(name, structName, w =>
            {
                var t = new TagWriter(package, w);
                inner(t);
                t.None();
            });

        public void SoftObject(string name, string assetPath, string subPath = "")
        {
            var value = new ByteWriter();
            value.FName(package.Name(assetPath));
            value.FString(subPath);
            Header(name, "SoftObjectProperty", value.Length);
            writer.U8(0);
            writer.Raw(value.WrittenSpan);
        }

        public void NameValue(string name, string value)
        {
            Header(name, "NameProperty", 8);
            writer.U8(0);
            writer.FName(package.Name(value));
        }

        private void Struct(string name, string structName, Action<ByteWriter> value)
        {
            var body = new ByteWriter();
            value(body);
            Header(name, "StructProperty", body.Length);
            writer.FName(package.Name(structName));
            writer.Guid(default);
            writer.U8(0);
            writer.Raw(body.WrittenSpan);
        }

        private void Header(string name, string type, int size)
        {
            writer.FName(package.Name(name));
            writer.FName(package.Name(type));
            writer.I32(size);
            writer.I32(0); // ArrayIndex
        }
    }
}
