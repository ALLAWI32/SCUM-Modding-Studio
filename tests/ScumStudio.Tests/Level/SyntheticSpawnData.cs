using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Tests.Formats;

namespace ScumStudio.Tests.Level;

/// <summary>
/// Small cooked packages of SCUM's spawn data written as loose files (no game files needed): encounter classes with their
/// <c>_possibleCharacters</c> presets, zone settings (<c>EncounterZoneData</c>) with their <c>EncounterData</c> rows.
/// </summary>
internal sealed class SyntheticSpawnData : IDisposable
{
    public const string Encounters = "/Game/ConZ_Files/Encounters/EncounterClasses/";
    public const string Zones = "/Game/ConZ_Files/Encounters/EncounterZones/";
    public const string Presets = "/Game/ConZ_Files/Encounters/Character_Presets/";

    public SyntheticSpawnData()
    {
        Root = Path.Combine(Path.GetTempPath(), "ss-spawns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "SCUM", "Content"));
    }

    public string Root { get; }

    /// <summary>
    /// An encounter class <c>/Game/…/EncounterClasses/<paramref name="name"/></c>: its defaults draw from
    /// <paramref name="presets"/> (object paths under <see cref="Presets"/>, by weight); none stored when empty. Its parent
    /// is the encounter class <paramref name="parent"/>, else the game's native encounter.
    /// </summary>
    public void Encounter(string name, string? parent, params (string Preset, float Weight)[] presets)
    {
        var b = new Builder();
        var classOf = b.ObjectImport("/Script/Engine.BlueprintGeneratedClass", "/Script/CoreUObject", "Class");
        var super = parent is null
            ? b.ObjectImport("/Script/ConZ.Encounter", "/Script/CoreUObject", "Class")
            : b.ObjectImport($"{Encounters}{parent}.{parent}_C", "/Script/Engine", "BlueprintGeneratedClass");
        var keys = presets.Select(p => b.ObjectImport(p.Preset, "/Script/ConZ", "EncounterCharacterSpawnPreset")).ToList();
        b.Export(name + "_C", classOf, super, b.Properties(_ => { }));
        b.Export("Default__" + name + "_C", 1, 0, b.Properties(w =>
        {
            if (presets.Length > 0)
            {
                var map = new ByteWriter();
                map.I32(0);
                map.I32(presets.Length);
                for (var i = 0; i < presets.Length; i++)
                {
                    map.I32(keys[i]);
                    map.F32(presets[i].Weight);
                }

                b.Tag(w, "_possibleCharacters", "MapProperty", map.Length, x => { x.FName(b.N("ObjectProperty")); x.FName(b.N("FloatProperty")); x.U8(0); });
                w.Raw(map.WrittenSpan);
            }

            b.Tag(w, "_maxNumberOfCharacters", "IntProperty", 4, x => x.U8(0));
            w.I32(4);
            b.Tag(w, "_spawnRadius", "FloatProperty", 4, x => x.U8(0));
            w.F32(1500f);
        }));
        Write(Encounters + name, b.Build());
    }

    /// <summary>Zone settings <c>/Game/…/EncounterZones/<paramref name="folderAndName"/></c> with one row per encounter class name and weight.</summary>
    public void Zone(string folderAndName, params (string Encounter, float Weight)[] rows) => Write(Zones + folderAndName, ZoneBytes(folderAndName, rows));

    /// <summary>A package with nothing in it, at <paramref name="packagePath"/> (a name the game already has).</summary>
    public void Empty(string packagePath)
    {
        var b = new Builder();
        b.Export(packagePath[(packagePath.LastIndexOf('/') + 1)..], b.ObjectImport("/Script/Engine.DataAsset", "/Script/CoreUObject", "Class"), 0, b.Properties(_ => { }));
        Write(packagePath, b.Build());
    }

    /// <summary>The bytes of zone settings named after <paramref name="folderAndName"/>'s leaf (see <see cref="Zone"/>).</summary>
    public static PackageBytes ZoneBytes(string folderAndName, params (string Encounter, float Weight)[] rows)
    {
        var b = new Builder();
        var name = folderAndName[(folderAndName.LastIndexOf('/') + 1)..];
        var classOf = b.ObjectImport("/Script/ConZ.EncounterZoneData", "/Script/CoreUObject", "Class");
        var classes = rows.Select(r => b.ObjectImport($"{Encounters}{r.Encounter}.{r.Encounter}_C", "/Script/Engine", "BlueprintGeneratedClass")).ToList();
        b.Export(name, classOf, 0, b.Properties(w =>
        {
            var items = new ByteWriter();
            for (var i = 0; i < rows.Length; i++)
            {
                b.Tag(items, "EncounterClass", "ObjectProperty", 4, x => x.U8(0));
                items.I32(classes[i]);
                b.Tag(items, "EncounterWeight", "FloatProperty", 4, x => x.U8(0));
                items.F32(rows[i].Weight);
                items.FName(b.N("None"));
            }

            var array = new ByteWriter();
            array.I32(rows.Length);
            b.Tag(array, "EncounterData", "StructProperty", items.Length, x => { x.FName(b.N("EncounterZoneEncounterData")); x.Guid(default); x.U8(0); });
            array.Raw(items.WrittenSpan);
            b.Tag(w, "EncounterData", "ArrayProperty", array.Length, x => { x.FName(b.N("StructProperty")); x.U8(0); });
            w.Raw(array.WrittenSpan);
            b.Tag(w, "EncounterSpawnChance", "FloatProperty", 4, x => x.U8(0));
            w.F32(80f);
        }));
        return b.Build();
    }

    public AssetCatalog Open() => AssetCatalog.OpenLoose(Root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // a file still open by a catalog: the system's temp cleanup takes it
        }
    }

    private void Write(string packagePath, PackageBytes bytes)
    {
        var stem = Path.Combine([Root, "SCUM", "Content", .. packagePath["/Game/".Length..].Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(stem)!);
        File.WriteAllBytes(stem + ".uasset", bytes.UAsset);
        File.WriteAllBytes(stem + ".uexp", bytes.UExp);
    }

    /// <summary>Name, import and export tables of one package and its tagged payloads.</summary>
    private sealed class Builder
    {
        private readonly List<string> _names = ["None"];
        private readonly List<ImportEntry> _imports = [];
        private readonly List<ExportEntry> _exports = [];
        private readonly List<ReadOnlyMemory<byte>> _payloads = [];

        public FNameRef N(string name)
        {
            var index = _names.IndexOf(name);
            if (index < 0)
            {
                index = _names.Count;
                _names.Add(name);
            }

            return new FNameRef(index);
        }

        /// <summary>The import of <paramref name="objectPath"/> (<c>/Package/Path.Object</c>), its package import added first.</summary>
        public int ObjectImport(string objectPath, string classPackage, string className)
        {
            var dot = objectPath.LastIndexOf('.');
            var package = _imports.FindIndex(i => i.OuterIndex == 0 && _names[i.ObjectName.Index] == objectPath[..dot]);
            if (package < 0)
            {
                _imports.Add(new ImportEntry(N("/Script/CoreUObject"), N("Package"), 0, N(objectPath[..dot])));
                package = _imports.Count - 1;
            }

            _imports.Add(new ImportEntry(N(classPackage), N(className), -(package + 1), N(objectPath[(dot + 1)..])));
            return -_imports.Count;
        }

        public void Export(string name, int classIndex, int superIndex, byte[] payload)
        {
            _exports.Add(new ExportEntry { ClassIndex = classIndex, SuperIndex = superIndex, ObjectName = N(name), ObjectFlags = 0x1, IsAsset = _exports.Count == 0 ? 1 : 0, FirstExportDependency = -1 });
            _payloads.Add(payload);
        }

        public void Tag(ByteWriter w, string name, string type, int size, Action<ByteWriter> extra)
        {
            w.FName(N(name));
            w.FName(N(type));
            w.I32(size);
            w.I32(0);
            extra(w);
        }

        /// <summary>Tagged properties written by <paramref name="write"/>, then None and the object's (absent) guid.</summary>
        public byte[] Properties(Action<ByteWriter> write)
        {
            var w = new ByteWriter();
            write(w);
            w.FName(N("None"));
            w.I32(0);
            return w.ToArray();
        }

        public PackageBytes Build() => PackageWriter.Build(new PackageBuildInput
        {
            Summary = SyntheticPackage.DefaultSummary(),
            Names = _names,
            Imports = _imports,
            Exports = _exports,
            ExportData = _payloads,
        });
    }
}
