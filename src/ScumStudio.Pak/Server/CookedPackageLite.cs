using System.Text;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Pak.Server;

/// <summary>
/// Minimal UE 4.27 cooked package (.uasset + .uexp) reader/rebuilder, just enough to rename a small package through its
/// name table (port of ue4pkg.py <c>Pkg.parse</c> and ue4write.py <c>build_package</c>). The full reader/writer lives in
/// ScumStudio.Formats; this copy keeps ScumStudio.Pak free of that dependency for the server-variant step.
/// </summary>
internal sealed class CookedPackageLite
{
    public const uint PackageMagic = 0x9E2A83C1;
    private const int ImportSize = 28;
    private const int ExportSize = 104;

    private CookedPackageLite()
    {
    }

    public required byte[] Uasset { get; init; }

    public required byte[] Uexp { get; init; }

    public required Summary S { get; init; }

    public required List<string> Names { get; init; }

    public required List<Import> Imports { get; init; }

    public required List<Export> Exports { get; init; }

    /// <summary>Parses a package from its .uasset and .uexp bytes (port of ue4pkg.py Pkg.parse).</summary>
    public static CookedPackageLite Parse(byte[] uasset, byte[] uexp)
    {
        var r = new BinaryReader(new MemoryStream(uasset), Encoding.UTF8);
        var s = new Summary();
        if (r.ReadUInt32() != PackageMagic)
        {
            throw new InvalidDataException("Not an Unreal package (bad magic).");
        }

        s.Legacy = r.ReadInt32();
        if (s.Legacy <= -8)
        {
            throw new NotSupportedException("UE5 package summaries are not supported.");
        }

        if (s.Legacy != -4)
        {
            s.LegacyUe3 = r.ReadInt32();
        }

        s.FileVersionUe4 = r.ReadInt32();
        s.VersionLicensee = r.ReadInt32();
        var verUe4 = s.FileVersionUe4 == 0 ? 522 : s.FileVersionUe4; // unversioned: 4.27 = 522
        if (verUe4 < 510)
        {
            throw new NotSupportedException($"Package version {verUe4} is older than UE 4.26/4.27.");
        }

        if (s.Legacy <= -2)
        {
            var n = r.ReadInt32();
            for (var i = 0; i < n; i++)
            {
                s.CustomVersions.Add((r.ReadBytes(16), r.ReadInt32()));
            }
        }

        s.TotalHeaderSize = r.ReadInt32();
        s.FolderName = PakBinary.ReadFString(r);
        s.Flags = r.ReadUInt32();
        s.NameCount = r.ReadInt32();
        s.NameOffset = r.ReadInt32();
        s.GatherCount = r.ReadInt32();
        s.GatherOffset = r.ReadInt32();
        s.ExportCount = r.ReadInt32();
        s.ExportOffset = r.ReadInt32();
        s.ImportCount = r.ReadInt32();
        s.ImportOffset = r.ReadInt32();
        s.DependsOffset = r.ReadInt32();
        s.SoftPackageCount = r.ReadInt32();
        s.SoftPackageOffset = r.ReadInt32();
        s.SearchableNamesOffset = r.ReadInt32();
        s.ThumbnailOffset = r.ReadInt32();
        s.Guid = r.ReadBytes(16);
        var filterEditorOnly = (s.Flags & 0x80000000) != 0;
        if (!filterEditorOnly)
        {
            throw new NotSupportedException("Only cooked (FilterEditorOnly) packages are supported.");
        }

        var generations = r.ReadInt32();
        r.ReadBytes(8 * generations);
        s.SavedBy = ReadEngineVersion(r);
        s.CompatibleWith = ReadEngineVersion(r);
        s.CompressionFlags = r.ReadUInt32();
        if (r.ReadInt32() != 0)
        {
            throw new NotSupportedException("Compressed package chunks are not supported.");
        }

        s.PackageSource = r.ReadUInt32();
        if (r.ReadInt32() != 0)
        {
            throw new NotSupportedException("AdditionalPackagesToCook is not supported.");
        }

        if (s.Legacy > -7)
        {
            r.ReadInt32(); // NumTextureAllocations
        }

        s.AssetRegistryOffset = r.ReadInt32();
        s.BulkDataStartOffset = r.ReadInt64();
        s.WorldTileInfoOffset = r.ReadInt32();
        var chunkCount = r.ReadInt32();
        for (var i = 0; i < chunkCount; i++)
        {
            s.ChunkIds.Add(r.ReadInt32());
        }

        s.PreloadCount = r.ReadInt32();
        s.PreloadOffset = r.ReadInt32();

        var names = new List<string>(s.NameCount);
        r.BaseStream.Position = s.NameOffset;
        for (var i = 0; i < s.NameCount; i++)
        {
            names.Add(PakBinary.ReadFString(r));
            r.ReadUInt32(); // stored hashes (recomputed on write)
        }

        var imports = new List<Import>(s.ImportCount);
        for (var i = 0; i < s.ImportCount; i++)
        {
            var raw = uasset.AsSpan(s.ImportOffset + (i * ImportSize), ImportSize).ToArray();
            imports.Add(new Import(raw));
        }

        var exports = new List<Export>(s.ExportCount);
        for (var i = 0; i < s.ExportCount; i++)
        {
            var raw = uasset.AsSpan(s.ExportOffset + (i * ExportSize), ExportSize).ToArray();
            exports.Add(new Export(raw));
        }

        return new CookedPackageLite { Uasset = uasset, Uexp = uexp, S = s, Names = names, Imports = imports, Exports = exports };
    }

    /// <summary>Renders an FName (index, number) like ue4pkg.py <c>nm</c>: "Base" or "Base_{n-1}".</summary>
    public string NameOf(int index, int number)
    {
        var baseName = index >= 0 && index < Names.Count ? Names[index] : $"<bad {index}>";
        return number == 0 ? baseName : $"{baseName}_{number - 1}";
    }

    /// <summary>Class of an export as ue4pkg.py renders it ("IMP:AkAudioEvent", "EXP:..." or "None").</summary>
    public string ClassOf(Export export)
    {
        var idx = export.ClassIndex;
        if (idx == 0)
        {
            return "None";
        }

        if (idx < 0)
        {
            var import = Imports[-idx - 1];
            return "IMP:" + NameOf(import.ObjectNameIndex, import.ObjectNameNumber);
        }

        return idx - 1 < Exports.Count ? "EXP:" + NameOf(Exports[idx - 1].ObjectNameIndex, Exports[idx - 1].ObjectNameNumber) : $"EXP#{idx}";
    }

    /// <summary>Serialized bytes of export <paramref name="i"/> (ue4pkg.py <c>export_bytes</c>).</summary>
    public byte[] ExportBytes(int i)
    {
        var e = Exports[i];
        var offset = checked((int)(e.SerialOffset - S.TotalHeaderSize));
        return Uexp.AsSpan(offset, checked((int)e.SerialSize)).ToArray();
    }

    /// <summary>
    /// Rebuilds the package with a new name table (same count and order), keeping imports, exports (whose name indexes
    /// therefore follow the renamed entries), export payloads, depends map, asset registry data and preload dependencies
    /// (port of ue4write.py <c>build_package</c>).
    /// </summary>
    public (byte[] Uasset, byte[] Uexp) Rebuild(IReadOnlyList<string> names)
    {
        if (names.Count != Names.Count)
        {
            throw new ArgumentException("The name table must keep its size and order.", nameof(names));
        }

        var payloads = Enumerable.Range(0, Exports.Count).Select(ExportBytes).ToList();
        var dependsLength = S.AssetRegistryOffset - S.DependsOffset;
        var depends = Uasset.AsSpan(S.DependsOffset, dependsLength).ToArray();
        var assetRegistry = Uasset.AsSpan(S.AssetRegistryOffset, S.PreloadOffset - S.AssetRegistryOffset).ToArray();
        var preload = Uasset.AsSpan(S.PreloadOffset, 4 * S.PreloadCount).ToArray();

        byte[] nameBlock;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            foreach (var name in names)
            {
                WriteFStringLikePython(w, name);
                var (h1, h2) = NameHashes.Compute(name);
                w.Write(h1);
                w.Write(h2);
            }

            w.Flush();
            nameBlock = ms.ToArray();
        }

        var importBlock = Imports.SelectMany(i => i.Raw).ToArray();
        var summaryLength = WriteSummary(names.Count, default).Length;
        var nameOffset = summaryLength;
        var importOffset = nameOffset + nameBlock.Length;
        var exportOffset = importOffset + importBlock.Length;
        var dependsOffset = exportOffset + (ExportSize * Exports.Count);
        var assetRegistryOffset = dependsOffset + depends.Length;
        var preloadOffset = assetRegistryOffset + assetRegistry.Length;
        var totalHeader = preloadOffset + preload.Length;

        using var exportsStream = new MemoryStream();
        using (var w = new BinaryWriter(exportsStream, Encoding.UTF8, leaveOpen: true))
        {
            long offset = totalHeader;
            for (var i = 0; i < Exports.Count; i++)
            {
                var raw = (byte[])Exports[i].Raw.Clone();
                BitConverter.TryWriteBytes(raw.AsSpan(28, 8), (long)payloads[i].Length);
                BitConverter.TryWriteBytes(raw.AsSpan(36, 8), offset);
                w.Write(raw);
                offset += payloads[i].Length;
            }
        }

        var uexpLength = payloads.Sum(p => (long)p.Length);
        var layout = new Layout(totalHeader, nameOffset, importOffset, exportOffset, dependsOffset, assetRegistryOffset, preloadOffset, totalHeader + uexpLength);
        var summary = WriteSummary(names.Count, layout);

        byte[] uasset = [.. summary, .. nameBlock, .. importBlock, .. exportsStream.ToArray(), .. depends, .. assetRegistry, .. preload];
        if (uasset.Length != totalHeader)
        {
            throw new InvalidOperationException($"Internal error: header size {uasset.Length} != {totalHeader}.");
        }

        using var uexp = new MemoryStream();
        foreach (var payload in payloads)
        {
            uexp.Write(payload);
        }

        uexp.Write(BitConverter.GetBytes(PackageMagic));
        return (uasset, uexp.ToArray());
    }

    private byte[] WriteSummary(int nameCount, Layout layout)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(PackageMagic);
        w.Write(S.Legacy);
        if (S.Legacy != -4)
        {
            w.Write(S.LegacyUe3);
        }

        w.Write(S.FileVersionUe4);
        w.Write(S.VersionLicensee);
        if (S.Legacy <= -2)
        {
            w.Write(S.CustomVersions.Count);
            foreach (var (guid, version) in S.CustomVersions)
            {
                w.Write(guid);
                w.Write(version);
            }
        }

        w.Write(layout.TotalHeader);
        WriteFStringLikePython(w, S.FolderName);
        w.Write(S.Flags);
        w.Write(nameCount);
        w.Write(layout.NameOffset);
        w.Write(S.GatherCount);
        w.Write(S.GatherOffset);
        w.Write(Exports.Count);
        w.Write(layout.ExportOffset);
        w.Write(Imports.Count);
        w.Write(layout.ImportOffset);
        w.Write(layout.DependsOffset);
        w.Write(S.SoftPackageCount);
        w.Write(S.SoftPackageOffset);
        w.Write(S.SearchableNamesOffset);
        w.Write(S.ThumbnailOffset);
        w.Write(S.Guid);
        w.Write(1); // one generation: (export count, name count)
        w.Write(Exports.Count);
        w.Write(nameCount);
        WriteEngineVersion(w, S.SavedBy);
        WriteEngineVersion(w, S.CompatibleWith);
        w.Write(S.CompressionFlags);
        w.Write(0);
        w.Write(S.PackageSource);
        w.Write(0);
        if (S.Legacy > -7)
        {
            w.Write(0);
        }

        w.Write(layout.AssetRegistryOffset);
        w.Write(layout.BulkDataStart);
        w.Write(S.WorldTileInfoOffset);
        w.Write(S.ChunkIds.Count);
        foreach (var chunk in S.ChunkIds)
        {
            w.Write(chunk);
        }

        w.Write(S.PreloadCount);
        w.Write(layout.PreloadOffset);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>ue4write.py W.fstr: ASCII when possible, else UTF-16 with negative length.</summary>
    private static void WriteFStringLikePython(BinaryWriter w, string value) => PakBinary.WriteFString(w, value);

    private static EngineVersion ReadEngineVersion(BinaryReader r) =>
        new(r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt16(), r.ReadUInt32(), PakBinary.ReadFString(r));

    private static void WriteEngineVersion(BinaryWriter w, EngineVersion v)
    {
        w.Write(v.Major);
        w.Write(v.Minor);
        w.Write(v.Patch);
        w.Write(v.Changelist);
        WriteFStringLikePython(w, v.Branch);
    }

    private readonly record struct Layout(
        int TotalHeader, int NameOffset, int ImportOffset, int ExportOffset, int DependsOffset, int AssetRegistryOffset, int PreloadOffset, long BulkDataStart);

    internal sealed record EngineVersion(ushort Major, ushort Minor, ushort Patch, uint Changelist, string Branch);

    /// <summary>Summary fields that are carried over unchanged.</summary>
    internal sealed class Summary
    {
        public int Legacy { get; set; }

        public int LegacyUe3 { get; set; }

        public int FileVersionUe4 { get; set; }

        public int VersionLicensee { get; set; }

        public List<(byte[] Guid, int Version)> CustomVersions { get; } = [];

        public int TotalHeaderSize { get; set; }

        public string FolderName { get; set; } = string.Empty;

        public uint Flags { get; set; }

        public int NameCount { get; set; }

        public int NameOffset { get; set; }

        public int GatherCount { get; set; }

        public int GatherOffset { get; set; }

        public int ExportCount { get; set; }

        public int ExportOffset { get; set; }

        public int ImportCount { get; set; }

        public int ImportOffset { get; set; }

        public int DependsOffset { get; set; }

        public int SoftPackageCount { get; set; }

        public int SoftPackageOffset { get; set; }

        public int SearchableNamesOffset { get; set; }

        public int ThumbnailOffset { get; set; }

        public byte[] Guid { get; set; } = new byte[16];

        public EngineVersion SavedBy { get; set; } = new(0, 0, 0, 0, string.Empty);

        public EngineVersion CompatibleWith { get; set; } = new(0, 0, 0, 0, string.Empty);

        public uint CompressionFlags { get; set; }

        public uint PackageSource { get; set; }

        public int AssetRegistryOffset { get; set; }

        public long BulkDataStartOffset { get; set; }

        public int WorldTileInfoOffset { get; set; }

        public List<int> ChunkIds { get; } = [];

        public int PreloadCount { get; set; }

        public int PreloadOffset { get; set; }
    }

    /// <summary>A 28-byte FObjectImport: ClassPackage, ClassName, OuterIndex, ObjectName.</summary>
    internal sealed record Import(byte[] Raw)
    {
        public int ClassNameIndex => BitConverter.ToInt32(Raw, 8);

        public int ClassNameNumber => BitConverter.ToInt32(Raw, 12);

        public int ObjectNameIndex => BitConverter.ToInt32(Raw, 20);

        public int ObjectNameNumber => BitConverter.ToInt32(Raw, 24);
    }

    /// <summary>A 104-byte FObjectExport (UE 4.27 cooked layout).</summary>
    internal sealed record Export(byte[] Raw)
    {
        public int ClassIndex => BitConverter.ToInt32(Raw, 0);

        public int ObjectNameIndex => BitConverter.ToInt32(Raw, 16);

        public int ObjectNameNumber => BitConverter.ToInt32(Raw, 20);

        public long SerialSize => BitConverter.ToInt64(Raw, 28);

        public long SerialOffset => BitConverter.ToInt64(Raw, 36);
    }
}
