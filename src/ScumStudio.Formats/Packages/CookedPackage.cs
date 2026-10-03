using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Formats.Packages;

/// <summary>
/// A cooked UE 4.27 package split into <c>.uasset</c> (header) + <c>.uexp</c> (export payloads) + optional
/// <c>.ubulk</c> (port of <c>ue4pkg.py</c> class <c>Pkg</c>). Instances are immutable views over the loaded bytes.
/// </summary>
public sealed class CookedPackage
{
    private string[] _names = [];
    private Dictionary<string, int>? _nameIndex;

    private CookedPackage(string? basePath, byte[] uasset, byte[] uexp, byte[]? ubulk)
    {
        BasePath = basePath;
        UAsset = uasset;
        UExp = uexp;
        UBulk = ubulk;
        Summary = null!;
        NameEntries = [];
        Imports = [];
        Exports = [];
    }

    /// <summary>Path without extension this package was loaded from (null when built from bytes).</summary>
    public string? BasePath { get; }

    /// <summary>The raw <c>.uasset</c> bytes.</summary>
    public byte[] UAsset { get; }

    /// <summary>The raw <c>.uexp</c> bytes (empty when the file does not exist).</summary>
    public byte[] UExp { get; }

    /// <summary>The raw <c>.ubulk</c> bytes, or null.</summary>
    public byte[]? UBulk { get; }

    /// <summary>The package summary.</summary>
    public PackageSummary Summary { get; private set; }

    /// <summary>Name table entries including the stored hashes.</summary>
    public IReadOnlyList<NameEntry> NameEntries { get; private set; }

    /// <summary>Name table strings.</summary>
    public IReadOnlyList<string> Names => _names;

    /// <summary>Import table.</summary>
    public IReadOnlyList<ImportEntry> Imports { get; private set; }

    /// <summary>Export table.</summary>
    public IReadOnlyList<ExportEntry> Exports { get; private set; }

    /// <summary>Offset just after the import table.</summary>
    public int ImportEnd { get; private set; }

    /// <summary>Offset just after the export table.</summary>
    public int ExportEnd { get; private set; }

    /// <summary>Name string to name index (first occurrence).</summary>
    public IReadOnlyDictionary<string, int> NameIndex
    {
        get
        {
            if (_nameIndex is null)
            {
                var index = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < _names.Length; i++)
                {
                    index.TryAdd(_names[i], i);
                }

                _nameIndex = index;
            }

            return _nameIndex;
        }
    }

    /// <summary>
    /// Loads <c>{basePath}.uasset</c> and, when present, <c>.uexp</c> and <c>.ubulk</c>.
    /// </summary>
    /// <param name="basePath">Path without extension (a trailing .uasset/.umap/.uexp is stripped; .umap is honoured).</param>
    public static CookedPackage Load(string basePath)
    {
        var (header, stem) = ResolveHeaderPath(basePath);
        var uexp = stem + ".uexp";
        var ubulk = stem + ".ubulk";
        return Parse(
            File.ReadAllBytes(header),
            File.Exists(uexp) ? File.ReadAllBytes(uexp) : [],
            File.Exists(ubulk) ? File.ReadAllBytes(ubulk) : null,
            stem);
    }

    /// <summary>Asynchronous version of <see cref="Load"/>.</summary>
    public static async Task<CookedPackage> LoadAsync(string basePath, CancellationToken cancellationToken = default)
    {
        var (header, stem) = ResolveHeaderPath(basePath);
        var uexp = stem + ".uexp";
        var ubulk = stem + ".ubulk";
        var ua = await File.ReadAllBytesAsync(header, cancellationToken).ConfigureAwait(false);
        var ux = File.Exists(uexp) ? await File.ReadAllBytesAsync(uexp, cancellationToken).ConfigureAwait(false) : [];
        var ub = File.Exists(ubulk) ? await File.ReadAllBytesAsync(ubulk, cancellationToken).ConfigureAwait(false) : null;
        return Parse(ua, ux, ub, stem);
    }

    /// <summary>
    /// Returns (header file, path without extension) for a package path given with or without extension.
    /// <c>.umap</c> is used when no <c>.uasset</c> exists.
    /// </summary>
    public static (string HeaderPath, string BasePath) ResolveHeaderPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var ext = Path.GetExtension(path);
        if (ext.Equals(".umap", StringComparison.OrdinalIgnoreCase))
        {
            return (path, path[..^ext.Length]);
        }

        var stem = ext.Equals(".uasset", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".uexp", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".ubulk", StringComparison.OrdinalIgnoreCase)
            ? path[..^ext.Length]
            : path;
        if (File.Exists(stem + ".uasset"))
        {
            return (stem + ".uasset", stem);
        }

        if (File.Exists(stem + ".umap"))
        {
            return (stem + ".umap", stem);
        }

        throw new FileNotFoundException($"No .uasset or .umap for package '{stem}'.", stem + ".uasset");
    }

    /// <summary>Parses a package from in-memory bytes.</summary>
    /// <param name="uasset">Header bytes.</param>
    /// <param name="uexp">Export payload bytes (may be empty).</param>
    /// <param name="ubulk">Bulk bytes or null.</param>
    /// <param name="basePath">Optional path for display.</param>
    /// <exception cref="FormatException">The header is not a supported package.</exception>
    public static CookedPackage Parse(byte[] uasset, byte[] uexp, byte[]? ubulk = null, string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(uasset);
        ArgumentNullException.ThrowIfNull(uexp);
        var pkg = new CookedPackage(basePath, uasset, uexp, ubulk);
        try
        {
            pkg.ParseHeader();
        }
        catch (EndOfStreamException ex)
        {
            throw new FormatException($"Truncated package header{(basePath is null ? "" : $" in '{basePath}'")}: {ex.Message}", ex);
        }

        return pkg;
    }

    // port of ue4pkg.py Pkg.parse
    private void ParseHeader()
    {
        var r = new ByteReader(UAsset);
        var tag = r.U32();
        if (tag != PackageSummary.PackageMagic)
        {
            throw new FormatException($"Not an Unreal package (magic 0x{tag:X8}).");
        }

        var legacy = r.I32();
        var legacyUe3 = legacy != -4 ? r.I32() : 0;
        var fileVerUe4 = r.I32();
        var verUe5 = legacy <= -8 ? r.I32() : 0;
        var licensee = r.I32();
        var ver = fileVerUe4 == 0 ? PackageSummary.UnversionedUE4Version : fileVerUe4;
        var custom = new List<CustomVersionInfo>();
        if (legacy <= -2)
        {
            var n = CheckedCount(r.I32(), 20, r);
            for (var i = 0; i < n; i++)
            {
                custom.Add(new CustomVersionInfo(r.Guid(), r.I32()));
            }
        }

        var totalHeader = r.I32();
        var folder = r.FString();
        var flags = r.U32();
        var nameCount = r.I32();
        var nameOff = r.I32();
        int gatherCount = 0, gatherOff = 0;
        if (ver >= 459)
        {
            gatherCount = r.I32();
            gatherOff = r.I32();
        }

        var exportCount = r.I32();
        var exportOff = r.I32();
        var importCount = r.I32();
        var importOff = r.I32();
        var dependsOff = r.I32();
        int softCount = 0, softOff = 0, searchableOff = 0;
        if (ver >= 384)
        {
            softCount = r.I32();
            softOff = r.I32();
        }

        if (ver >= 510)
        {
            searchableOff = r.I32();
        }

        var thumbOff = r.I32();
        var guid = r.Guid();
        var filterEditor = (flags & PackageSummary.PkgFilterEditorOnly) != 0;
        FGuid? persistent = null, owner = null;
        if (ver >= 516 && !filterEditor)
        {
            persistent = r.Guid();
        }

        if (ver is >= 516 and < 518 && !filterEditor)
        {
            owner = r.Guid();
        }

        var generations = new List<GenerationInfo>();
        var genCount = CheckedCount(r.I32(), 8, r);
        for (var i = 0; i < genCount; i++)
        {
            generations.Add(new GenerationInfo(r.I32(), r.I32()));
        }

        EngineVersionInfo? savedBy = null, compatible = null;
        if (ver >= 336)
        {
            savedBy = ReadEngineVersion(r);
        }

        if (ver >= 444)
        {
            compatible = ReadEngineVersion(r);
        }

        var compression = r.U32();
        var chunkCount = CheckedCount(r.I32(), 16, r);
        var chunks = new List<byte[]>();
        for (var i = 0; i < chunkCount; i++)
        {
            chunks.Add(r.Raw(16));
        }

        var source = r.U32();
        var addCount = CheckedCount(r.I32(), 4, r);
        var additional = new List<string>();
        for (var i = 0; i < addCount; i++)
        {
            additional.Add(r.FString());
        }

        var numTex = legacy > -7 ? r.I32() : 0;
        var assetRegOff = r.I32();
        var bulkStart = r.I64();
        var worldTile = ver >= 224 ? r.I32() : 0;
        var chunkIds = new List<int>();
        if (ver >= 326)
        {
            var n = CheckedCount(r.I32(), 4, r);
            for (var i = 0; i < n; i++)
            {
                chunkIds.Add(r.I32());
            }
        }

        int preloadCount = 0, preloadOff = 0;
        if (ver >= 507)
        {
            preloadCount = r.I32();
            preloadOff = r.I32();
        }

        Summary = new PackageSummary
        {
            LegacyFileVersion = legacy,
            LegacyUE3Version = legacyUe3,
            FileVersionUE4 = fileVerUe4,
            FileVersionUE5 = verUe5,
            FileVersionLicensee = licensee,
            CustomVersions = custom,
            TotalHeaderSize = totalHeader,
            FolderName = folder,
            PackageFlags = flags,
            NameCount = nameCount,
            NameOffset = nameOff,
            GatherableTextDataCount = gatherCount,
            GatherableTextDataOffset = gatherOff,
            ExportCount = exportCount,
            ExportOffset = exportOff,
            ImportCount = importCount,
            ImportOffset = importOff,
            DependsOffset = dependsOff,
            SoftPackageReferencesCount = softCount,
            SoftPackageReferencesOffset = softOff,
            SearchableNamesOffset = searchableOff,
            ThumbnailTableOffset = thumbOff,
            Guid = guid,
            PersistentGuid = persistent,
            OwnerPersistentGuid = owner,
            Generations = generations,
            SavedByEngineVersion = savedBy,
            CompatibleWithEngineVersion = compatible,
            CompressionFlags = compression,
            CompressedChunks = chunks,
            PackageSource = source,
            AdditionalPackagesToCook = additional,
            NumTextureAllocations = numTex,
            AssetRegistryDataOffset = assetRegOff,
            BulkDataStartOffset = bulkStart,
            WorldTileInfoDataOffset = worldTile,
            ChunkIds = chunkIds,
            PreloadDependencyCount = preloadCount,
            PreloadDependencyOffset = preloadOff,
            SummaryEnd = r.Position,
        };

        // names
        r.Position = nameOff;
        var names = new string[CheckedCount(nameCount, 5, r)];
        var entries = new NameEntry[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            var (value, wide) = r.FStringWithEncoding();
            var h1 = r.U16();
            var h2 = r.U16();
            names[i] = value;
            entries[i] = new NameEntry(value, h1, h2, wide);
        }

        _names = names;
        NameEntries = entries;

        // imports
        r.Position = importOff;
        var imports = new ImportEntry[CheckedCount(importCount, ImportEntry.SerializedSize, r)];
        for (var i = 0; i < imports.Length; i++)
        {
            imports[i] = new ImportEntry(r.FName(), r.FName(), r.I32(), r.FName());
        }

        Imports = imports;
        ImportEnd = r.Position;

        // exports
        r.Position = exportOff;
        var exports = new ExportEntry[CheckedCount(exportCount, ExportEntry.SerializedSize, r)];
        for (var i = 0; i < exports.Length; i++)
        {
            exports[i] = new ExportEntry
            {
                ClassIndex = r.I32(),
                SuperIndex = r.I32(),
                TemplateIndex = r.I32(),
                OuterIndex = r.I32(),
                ObjectName = r.FName(),
                ObjectFlags = r.U32(),
                SerialSize = r.I64(),
                SerialOffset = r.I64(),
                ForcedExport = r.I32(),
                NotForClient = r.I32(),
                NotForServer = r.I32(),
                PackageGuid = r.Guid(),
                PackageFlags = r.U32(),
                NotAlwaysLoadedForEditorGame = r.I32(),
                IsAsset = r.I32(),
                FirstExportDependency = r.I32(),
                SerializationBeforeSerializationDependencies = r.I32(),
                CreateBeforeSerializationDependencies = r.I32(),
                SerializationBeforeCreateDependencies = r.I32(),
                CreateBeforeCreateDependencies = r.I32(),
            };
        }

        Exports = exports;
        ExportEnd = r.Position;
    }

    private static int CheckedCount(int count, int minElementSize, ByteReader r)
    {
        if (count < 0 || (long)count * minElementSize > r.Data.Length)
        {
            throw new FormatException($"Implausible element count {count} near offset {r.Position}.");
        }

        return count;
    }

    private static EngineVersionInfo ReadEngineVersion(ByteReader r) =>
        new(r.U16(), r.U16(), r.U16(), r.U32(), r.FString());

    /// <summary>Formats an FName reference (port of <c>Pkg.nm</c>).</summary>
    public string ResolveName(FNameRef name) => name.Format(_names);

    /// <summary>Resolves a name string to an FName reference (see <see cref="FNameRef.Resolve"/>).</summary>
    public FNameRef FindName(string name) => FNameRef.Resolve(NameIndex, name);

    /// <summary>
    /// Describes an FPackageIndex (port of <c>Pkg.ref</c>): <c>None</c>, <c>IMP:name</c> or <c>EXP:name</c>.
    /// </summary>
    public string ResolveIndex(int packageIndex)
    {
        if (packageIndex == 0)
        {
            return "None";
        }

        if (packageIndex < 0)
        {
            var i = -packageIndex - 1;
            return i < Imports.Count ? "IMP:" + ResolveName(Imports[i].ObjectName) : $"IMP#{packageIndex}";
        }

        return packageIndex - 1 < Exports.Count ? "EXP:" + ResolveName(Exports[packageIndex - 1].ObjectName) : $"EXP#{packageIndex}";
    }

    /// <summary>Object name of the import or export an FPackageIndex points to, or null for 0 / out of range.</summary>
    public string? GetObjectName(int packageIndex)
    {
        if (packageIndex < 0 && -packageIndex - 1 < Imports.Count)
        {
            return ResolveName(Imports[-packageIndex - 1].ObjectName);
        }

        if (packageIndex > 0 && packageIndex - 1 < Exports.Count)
        {
            return ResolveName(Exports[packageIndex - 1].ObjectName);
        }

        return null;
    }

    /// <summary>
    /// Full object path of an import or export (outer chain joined with '.' after the package, ':' inside objects
    /// is not distinguished), e.g. <c>/Game/Foo/Bar.Bar</c>.
    /// </summary>
    public string GetFullPath(int packageIndex)
    {
        var parts = new List<string>();
        var guard = 0;
        while (packageIndex != 0 && guard++ < 64)
        {
            var name = GetObjectName(packageIndex);
            if (name is null)
            {
                break;
            }

            parts.Add(name);
            packageIndex = packageIndex < 0 ? Imports[-packageIndex - 1].OuterIndex : Exports[packageIndex - 1].OuterIndex;
        }

        parts.Reverse();
        return string.Join('.', parts);
    }

    /// <summary>Class name of an export (the object name of its ClassIndex).</summary>
    public string GetExportClassName(int exportIndex) =>
        GetObjectName(Exports[exportIndex].ClassIndex) ?? "None";

    /// <summary>Offset of an export's payload inside the <c>.uexp</c> (SerialOffset - TotalHeaderSize).</summary>
    public int GetExportUExpOffset(int exportIndex) =>
        checked((int)(Exports[exportIndex].SerialOffset - Summary.TotalHeaderSize));

    /// <summary>
    /// The payload bytes of an export sliced from the <c>.uexp</c> (port of <c>Pkg.export_bytes</c>).
    /// </summary>
    public ReadOnlyMemory<byte> GetExportData(int exportIndex)
    {
        var (offset, length) = GetExportRange(exportIndex);
        return new ReadOnlyMemory<byte>(UExp, offset, length);
    }

    /// <summary>The payload bytes of an export as a new array.</summary>
    public byte[] GetExportBytes(int exportIndex) => GetExportData(exportIndex).ToArray();

    /// <summary>(uexp offset, length) of an export, clamped to the file like a Python slice.</summary>
    public (int Offset, int Length) GetExportRange(int exportIndex)
    {
        var e = Exports[exportIndex];
        var off = e.SerialOffset - Summary.TotalHeaderSize;
        var start = (int)Math.Clamp(off, 0, UExp.Length);
        var end = (int)Math.Clamp(off + e.SerialSize, start, UExp.Length);
        return (start, end - start);
    }

    /// <summary>Preload dependency array (port of <c>ue4write.read_preload</c>).</summary>
    public int[] ReadPreloadDependencies()
    {
        var s = Summary;
        var result = new int[s.PreloadDependencyCount];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = BinaryPrimitives.ReadInt32LittleEndian(UAsset.AsSpan(s.PreloadDependencyOffset + 4 * i, 4));
        }

        return result;
    }

    /// <summary>
    /// Raw asset registry block between AssetRegistryDataOffset and PreloadDependencyOffset
    /// (port of <c>ue4write.read_assetreg</c>).
    /// </summary>
    public byte[] ReadAssetRegistryData()
    {
        var s = Summary;
        var start = Math.Clamp(s.AssetRegistryDataOffset, 0, UAsset.Length);
        var end = Math.Clamp(s.PreloadDependencyOffset, start, UAsset.Length);
        return UAsset.AsSpan(start, end - start).ToArray();
    }

    /// <summary>
    /// Stored (NonCasePreserving, CasePreserving) hashes per name (port of <c>ue4write.stored_name_hashes</c>;
    /// later duplicates win like the Python dict).
    /// </summary>
    public IReadOnlyDictionary<string, (ushort, ushort)> GetStoredNameHashes()
    {
        var map = new Dictionary<string, (ushort, ushort)>(StringComparer.Ordinal);
        foreach (var n in NameEntries)
        {
            map[n.Value] = (n.NonCasePreservingHash, n.CasePreservingHash);
        }

        return map;
    }

    /// <summary>Reads the tagged property block at the start of an export payload.</summary>
    public PropertyBlock ReadProperties(int exportIndex) => PropertyReader.ReadExport(this, exportIndex);

    /// <summary>Text dump of the header, equivalent to <c>ue4pkg.Pkg.describe()</c>.</summary>
    public string Describe()
    {
        var s = Summary;
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("== ").Append(BasePath ?? "<memory>").Append('\n');
        sb.Append(ci, $"ver_ue4={s.EffectiveVersionUE4} lic={s.FileVersionLicensee} legacy={s.LegacyFileVersion} flags=0x{s.PackageFlags:x} total_header={s.TotalHeaderSize} uasset={UAsset.Length} uexp={UExp.Length} ubulk={(UBulk is null ? "None" : UBulk.Length.ToString(ci))}\n");
        sb.Append(ci, $"unversioned={Py(s.IsUnversionedProperties)} filter_editor_only={Py(s.IsFilterEditorOnly)} cooked={Py(s.IsCooked)} bulk_start={s.BulkDataStartOffset} custom_versions={s.CustomVersions.Count} engine={s.SavedByEngineVersion?.ToString() ?? "None"}\n");
        sb.Append(ci, $"names({_names.Length}): [").Append(string.Join(", ", _names.Select(PyRepr))).Append("]\n");
        sb.Append(ci, $"imports({Imports.Count}):\n");
        for (var i = 0; i < Imports.Count; i++)
        {
            var im = Imports[i];
            sb.Append(ci, $"  -{i + 1}: {ResolveName(im.ClassPackage)}.{ResolveName(im.ClassName)} {ResolveName(im.ObjectName)} outer={ResolveIndex(im.OuterIndex)}\n");
        }

        sb.Append(ci, $"exports({Exports.Count}):");
        for (var i = 0; i < Exports.Count; i++)
        {
            var e = Exports[i];
            sb.Append(ci, $"\n  {i + 1}: {ResolveName(e.ObjectName)} class={ResolveIndex(e.ClassIndex)} outer={ResolveIndex(e.OuterIndex)} super={ResolveIndex(e.SuperIndex)} size={e.SerialSize} off={e.SerialOffset} flags=0x{e.ObjectFlags:x}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Text dump of the preload (event-driven loader) dependencies of every export, or of <paramref name="exportIndex"/>
    /// only: <c>FirstExportDependency</c> and the four consecutive groups it points at in the preload array —
    /// SerializationBeforeSerialization, CreateBeforeSerialization, SerializationBeforeCreate, CreateBeforeCreate.
    /// </summary>
    public string DescribeDependencies(int exportIndex = -1)
    {
        var preload = ReadPreloadDependencies();
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(ci, $"preload dependencies({preload.Length}):\n");
        for (var i = 0; i < Exports.Count; i++)
        {
            if (exportIndex >= 0 && i != exportIndex)
            {
                continue;
            }

            var e = Exports[i];
            sb.Append(ci, $"  {i + 1}: {ResolveName(e.ObjectName)} first={e.FirstExportDependency}");
            if (e.FirstExportDependency < 0)
            {
                sb.Append(" (none)\n");
                continue;
            }

            var pos = e.FirstExportDependency;
            Group("serialize-before-serialize", e.SerializationBeforeSerializationDependencies);
            Group("create-before-serialize", e.CreateBeforeSerializationDependencies);
            Group("serialize-before-create", e.SerializationBeforeCreateDependencies);
            Group("create-before-create", e.CreateBeforeCreateDependencies);
            sb.Append('\n');

            void Group(string label, int count)
            {
                sb.Append(ci, $"\n      {label}({count}):");
                for (var k = 0; k < count && pos < preload.Length; k++, pos++)
                {
                    sb.Append(' ').Append(ResolveIndex(preload[pos]));
                }
            }
        }

        return sb.ToString();
    }

    private static string Py(bool value) => value ? "True" : "False";

    private static string PyRepr(string value) =>
        value.Contains('\'') && !value.Contains('"') ? "\"" + value + "\"" : "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
}
