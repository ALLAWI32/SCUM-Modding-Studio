using ScumStudio.Formats.IO;

namespace ScumStudio.Formats.Packages;

/// <summary>Everything <see cref="PackageWriter.Build"/> needs to lay out a package.</summary>
public sealed record PackageBuildInput
{
    /// <summary>Summary to copy the version/flag/guid/engine fields from; offsets and counts are recomputed.</summary>
    public required PackageSummary Summary { get; init; }

    /// <summary>Name table.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>Import table (FName references index <see cref="Names"/>).</summary>
    public required IReadOnlyList<ImportEntry> Imports { get; init; }

    /// <summary>
    /// Export table. SerialSize and SerialOffset are ignored and recomputed from <see cref="ExportData"/>.
    /// </summary>
    public required IReadOnlyList<ExportEntry> Exports { get; init; }

    /// <summary>Export payloads, aligned with <see cref="Exports"/>.</summary>
    public required IReadOnlyList<ReadOnlyMemory<byte>> ExportData { get; init; }

    /// <summary>Preload dependency array (FPackageIndex values).</summary>
    public IReadOnlyList<int> PreloadDependencies { get; init; } = [];

    /// <summary>Asset registry block written after the depends map (default: int32 0 = no objects).</summary>
    public ReadOnlyMemory<byte> AssetRegistryData { get; init; } = new byte[4];

    /// <summary>Optional per-name hash override (e.g. the hashes stored in the original package).</summary>
    public IReadOnlyDictionary<string, (ushort NonCasePreserving, ushort CasePreserving)>? NameHashOverride { get; init; }

    /// <summary>
    /// Optional per-name "store as UTF-16" flags aligned with <see cref="Names"/>. Null = ASCII when possible,
    /// UTF-16 otherwise (the Python writer's rule).
    /// </summary>
    public IReadOnlyList<bool>? NameIsWide { get; init; }
}

/// <summary>The two files of a split cooked package.</summary>
/// <param name="UAsset">Header bytes.</param>
/// <param name="UExp">Export payloads followed by the 4-byte package tag.</param>
public sealed record PackageBytes(byte[] UAsset, byte[] UExp)
{
    /// <summary>Writes <c>{basePath}.uasset</c> (or <paramref name="headerExtension"/>) and <c>{basePath}.uexp</c>.</summary>
    public async Task WriteAsync(string basePath, string headerExtension = ".uasset", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(basePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(basePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(basePath + headerExtension, UAsset, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(basePath + ".uexp", UExp, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Writes a complete cooked package from its tables (port of <c>ue4write.py build_package</c>): unversioned summary,
/// names with hashes, 28-byte imports, 104-byte exports, a zero depends map, the asset registry block and preload
/// dependencies; every offset and size is recomputed.
/// </summary>
public static class PackageWriter
{
    /// <summary>Builds (uasset, uexp) bytes. Port of <c>build_package</c>.</summary>
    /// <exception cref="ArgumentException">Export payload count does not match the export count.</exception>
    public static PackageBytes Build(PackageBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var s = input.Summary;
        var names = input.Names;
        var imports = input.Imports;
        var exports = input.Exports;
        var data = input.ExportData;
        var preload = input.PreloadDependencies;
        var assetReg = input.AssetRegistryData.Span;
        if (exports.Count != data.Count)
        {
            throw new ArgumentException($"{exports.Count} exports but {data.Count} payloads.", nameof(input));
        }

        // --- names block
        var wn = new ByteWriter(names.Count * 24);
        for (var i = 0; i < names.Count; i++)
        {
            var n = names[i];
            bool? wide = input.NameIsWide is { } flags && i < flags.Count ? flags[i] : null;
            wn.FString(n, wide);
            var storedWide = wide ?? !ByteWriter.IsAscii(n);
            if (wide == false && !ByteWriter.IsLatin1(n))
            {
                storedWide = true;
            }

            var (h1, h2) = input.NameHashOverride is { } ov && ov.TryGetValue(n, out var stored)
                ? stored
                : NameHashes.Compute(n, storedWide);
            wn.U16(h1);
            wn.U16(h2);
        }

        // --- imports block
        var wi = new ByteWriter(imports.Count * ImportEntry.SerializedSize);
        foreach (var im in imports)
        {
            CheckName(im.ClassPackage, names.Count);
            CheckName(im.ClassName, names.Count);
            CheckName(im.ObjectName, names.Count);
            wi.FName(im.ClassPackage);
            wi.FName(im.ClassName);
            wi.I32(im.OuterIndex);
            wi.FName(im.ObjectName);
        }

        // --- summary (fixed size given the same strings)
        var summaryLength = WriteSummary(s, names.Count, imports.Count, exports.Count, preload.Count, default).Length;
        var nameOff = summaryLength;
        var importOff = nameOff + wn.Length;
        var exportOff = importOff + wi.Length;
        var dependsOff = exportOff + ExportEntry.SerializedSize * exports.Count;
        var assetRegOff = dependsOff + 4 * exports.Count;
        var preloadOff = assetRegOff + assetReg.Length;
        var totalHeader = preloadOff + 4 * preload.Count;

        // --- exports block
        var we = new ByteWriter(exports.Count * ExportEntry.SerializedSize);
        long off = totalHeader;
        for (var i = 0; i < exports.Count; i++)
        {
            var e = exports[i];
            var len = data[i].Length;
            CheckName(e.ObjectName, names.Count);
            we.I32(e.ClassIndex);
            we.I32(e.SuperIndex);
            we.I32(e.TemplateIndex);
            we.I32(e.OuterIndex);
            we.FName(e.ObjectName);
            we.U32(e.ObjectFlags);
            we.I64(len);
            we.I64(off);
            we.I32(e.ForcedExport);
            we.I32(e.NotForClient);
            we.I32(e.NotForServer);
            we.Guid(e.PackageGuid);
            we.U32(e.PackageFlags);
            we.I32(e.NotAlwaysLoadedForEditorGame);
            we.I32(e.IsAsset);
            we.I32(e.FirstExportDependency);
            we.I32(e.SerializationBeforeSerializationDependencies);
            we.I32(e.CreateBeforeSerializationDependencies);
            we.I32(e.SerializationBeforeCreateDependencies);
            we.I32(e.CreateBeforeCreateDependencies);
            off += len;
        }

        var uexpLen = off - totalHeader;
        var bulkStart = totalHeader + uexpLen;
        var offsets = new SummaryOffsets(totalHeader, nameOff, importOff, exportOff, dependsOff, assetRegOff, preloadOff, bulkStart);
        var ua = WriteSummary(s, names.Count, imports.Count, exports.Count, preload.Count, offsets);
        ua.Raw(wn.WrittenSpan);
        ua.Raw(wi.WrittenSpan);
        ua.Raw(we.WrittenSpan);
        for (var i = 0; i < exports.Count; i++)
        {
            ua.I32(0);
        }

        ua.Raw(assetReg);
        foreach (var p in preload)
        {
            ua.I32(p);
        }

        if (ua.Length != totalHeader)
        {
            throw new InvalidOperationException($"Header layout error: wrote {ua.Length} bytes, expected {totalHeader}.");
        }

        var ux = new ByteWriter((int)Math.Min(int.MaxValue, uexpLen + 4));
        foreach (var d in data)
        {
            ux.Raw(d.Span);
        }

        ux.U32(PackageSummary.PackageMagic);
        return new PackageBytes(ua.ToArray(), ux.ToArray());
    }

    /// <summary>
    /// Rebuilds a package from its own parsed tables (what <c>roundtrip_check</c> does). With
    /// <paramref name="useStoredNameHashes"/> the hashes stored in the original name table are reused.
    /// </summary>
    public static PackageBytes Rebuild(CookedPackage package, bool useStoredNameHashes = false) =>
        Build(ToBuildInput(package, useStoredNameHashes));

    /// <summary>Creates a build input carrying every table of <paramref name="package"/> unchanged.</summary>
    public static PackageBuildInput ToBuildInput(CookedPackage package, bool useStoredNameHashes = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        return new PackageBuildInput
        {
            Summary = package.Summary,
            Names = package.Names.ToArray(),
            NameIsWide = package.NameEntries.Select(n => n.IsWide).ToArray(),
            Imports = package.Imports.ToArray(),
            Exports = package.Exports.ToArray(),
            ExportData = Enumerable.Range(0, package.Exports.Count).Select(package.GetExportData).ToArray(),
            PreloadDependencies = package.ReadPreloadDependencies(),
            AssetRegistryData = package.ReadAssetRegistryData(),
            NameHashOverride = useStoredNameHashes ? package.GetStoredNameHashes() : null,
        };
    }

    private static void CheckName(FNameRef name, int count)
    {
        if (name.Index < 0 || name.Index >= count)
        {
            throw new ArgumentException($"FName index {name.Index} outside the name table ({count} names).");
        }
    }

    private readonly record struct SummaryOffsets(
        int TotalHeader, int NameOff, int ImportOff, int ExportOff, int DependsOff, int AssetRegOff, int PreloadOff, long BulkStart);

    // port of the nested summary() in build_package; version-dependent fields follow ue4pkg.Pkg.parse.
    private static ByteWriter WriteSummary(PackageSummary s, int nameCount, int importCount, int exportCount, int preloadCount, SummaryOffsets o)
    {
        var ver = s.EffectiveVersionUE4;
        var w = new ByteWriter(512);
        w.U32(PackageSummary.PackageMagic);
        w.I32(s.LegacyFileVersion);
        if (s.LegacyFileVersion != -4)
        {
            w.I32(s.LegacyUE3Version);
        }

        w.I32(s.FileVersionUE4);
        if (s.LegacyFileVersion <= -8)
        {
            w.I32(s.FileVersionUE5);
        }

        w.I32(s.FileVersionLicensee);
        if (s.LegacyFileVersion <= -2)
        {
            w.I32(s.CustomVersions.Count);
            foreach (var cv in s.CustomVersions)
            {
                w.Guid(cv.Key);
                w.I32(cv.Version);
            }
        }

        w.I32(o.TotalHeader);
        w.FString(s.FolderName);
        w.U32(s.PackageFlags);
        w.I32(nameCount);
        w.I32(o.NameOff);
        if (ver >= 459)
        {
            w.I32(s.GatherableTextDataCount);
            w.I32(s.GatherableTextDataOffset);
        }

        w.I32(exportCount);
        w.I32(o.ExportOff);
        w.I32(importCount);
        w.I32(o.ImportOff);
        w.I32(o.DependsOff);
        if (ver >= 384)
        {
            w.I32(s.SoftPackageReferencesCount);
            w.I32(s.SoftPackageReferencesOffset);
        }

        if (ver >= 510)
        {
            w.I32(s.SearchableNamesOffset);
        }

        w.I32(s.ThumbnailTableOffset);
        w.Guid(s.Guid);
        if (ver >= 516 && !s.IsFilterEditorOnly)
        {
            w.Guid(s.PersistentGuid ?? default);
        }

        if (ver is >= 516 and < 518 && !s.IsFilterEditorOnly)
        {
            w.Guid(s.OwnerPersistentGuid ?? default);
        }

        // build_package always writes one generation with the new counts.
        w.I32(1);
        w.I32(exportCount);
        w.I32(nameCount);
        if (ver >= 336)
        {
            WriteEngineVersion(w, s.SavedByEngineVersion);
        }

        if (ver >= 444)
        {
            WriteEngineVersion(w, s.CompatibleWithEngineVersion);
        }

        w.U32(s.CompressionFlags);
        w.I32(s.CompressedChunks.Count);
        foreach (var c in s.CompressedChunks)
        {
            w.Raw(c);
        }

        w.U32(s.PackageSource);
        w.I32(s.AdditionalPackagesToCook.Count);
        foreach (var a in s.AdditionalPackagesToCook)
        {
            w.FString(a);
        }

        if (s.LegacyFileVersion > -7)
        {
            w.I32(s.NumTextureAllocations);
        }

        w.I32(o.AssetRegOff);
        w.I64(o.BulkStart);
        if (ver >= 224)
        {
            w.I32(RelocatedWorldTileInfoOffset(s, o));
        }

        if (ver >= 326)
        {
            w.I32(s.ChunkIds.Count);
            foreach (var c in s.ChunkIds)
            {
                w.I32(c);
            }
        }

        if (ver >= 507)
        {
            w.I32(preloadCount);
            w.I32(o.PreloadOff);
        }

        return w;
    }

    /// <summary>
    /// The cooked <c>FWorldTileInfo</c> of a World Composition sublevel sits inside the block copied as "asset registry
    /// data" (between AssetRegistryDataOffset and PreloadDependencyOffset). When the tables in front of it change size the
    /// block moves, so the stored offset is carried along; anything else (0 = no tile info) is written verbatim.
    /// </summary>
    private static int RelocatedWorldTileInfoOffset(PackageSummary s, SummaryOffsets o)
    {
        var stored = s.WorldTileInfoDataOffset;
        if (stored <= 0 || s.AssetRegistryDataOffset <= 0 || stored < s.AssetRegistryDataOffset)
        {
            return stored;
        }

        if (s.PreloadDependencyOffset > 0 && stored >= s.PreloadDependencyOffset)
        {
            return stored;
        }

        return o.AssetRegOff + (stored - s.AssetRegistryDataOffset);
    }

    private static void WriteEngineVersion(ByteWriter w, EngineVersionInfo? v)
    {
        v ??= new EngineVersionInfo(0, 0, 0, 0, string.Empty);
        w.U16(v.Major);
        w.U16(v.Minor);
        w.U16(v.Patch);
        w.U32(v.Changelist);
        w.FString(v.Branch);
    }
}
