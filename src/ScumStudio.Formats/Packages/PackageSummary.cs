namespace ScumStudio.Formats.Packages;

/// <summary>An engine version record (FEngineVersion) as stored in the package summary.</summary>
public sealed record EngineVersionInfo(ushort Major, ushort Minor, ushort Patch, uint Changelist, string Branch)
{
    /// <inheritdoc />
    public override string ToString() => $"({Major}, {Minor}, {Patch}, {Changelist}, '{Branch}')";
}

/// <summary>A custom version entry (FCustomVersion, optimized layout: guid + int32).</summary>
public sealed record CustomVersionInfo(FGuid Key, int Version);

/// <summary>A generation record (export count, name count).</summary>
public sealed record GenerationInfo(int ExportCount, int NameCount);

/// <summary>
/// FPackageFileSummary of a cooked UE 4.27 package (port of the summary part of <c>ue4pkg.py Pkg.parse</c>).
/// Field names follow the Unreal names; offsets are absolute positions in the <c>.uasset</c>.
/// </summary>
public sealed record PackageSummary
{
    /// <summary>Package file magic (<c>PACKAGE_FILE_TAG</c>).</summary>
    public const uint PackageMagic = 0x9E2A83C1;

    /// <summary><c>PKG_FilterEditorOnly</c>.</summary>
    public const uint PkgFilterEditorOnly = 0x80000000;

    /// <summary><c>PKG_UnversionedProperties</c>.</summary>
    public const uint PkgUnversionedProperties = 0x2000;

    /// <summary><c>PKG_Cooked</c>.</summary>
    public const uint PkgCooked = 0x200;

    /// <summary>UE4 object version used when the file stores 0 (unversioned): 4.27 = 522.</summary>
    public const int UnversionedUE4Version = 522;

    /// <summary>LegacyFileVersion (SCUM: -7).</summary>
    public int LegacyFileVersion { get; init; }

    /// <summary>LegacyUE3Version (present when LegacyFileVersion != -4).</summary>
    public int LegacyUE3Version { get; init; }

    /// <summary>FileVersionUE4 as stored in the file (SCUM: 0 = unversioned).</summary>
    public int FileVersionUE4 { get; init; }

    /// <summary>Effective UE4 version (<see cref="FileVersionUE4"/>, or 522 when 0).</summary>
    public int EffectiveVersionUE4 => FileVersionUE4 == 0 ? UnversionedUE4Version : FileVersionUE4;

    /// <summary>FileVersionUE5 (only present when LegacyFileVersion &lt;= -8).</summary>
    public int FileVersionUE5 { get; init; }

    /// <summary>FileVersionLicenseeUE4.</summary>
    public int FileVersionLicensee { get; init; }

    /// <summary>Custom versions (empty for SCUM cooked packages).</summary>
    public IReadOnlyList<CustomVersionInfo> CustomVersions { get; init; } = [];

    /// <summary>TotalHeaderSize = size of the .uasset; export serial offsets start here.</summary>
    public int TotalHeaderSize { get; init; }

    /// <summary>FolderName (usually "None").</summary>
    public string FolderName { get; init; } = "None";

    /// <summary>PackageFlags (SCUM: 0x80000000).</summary>
    public uint PackageFlags { get; init; }

    /// <summary>NameCount.</summary>
    public int NameCount { get; init; }

    /// <summary>NameOffset.</summary>
    public int NameOffset { get; init; }

    /// <summary>GatherableTextDataCount.</summary>
    public int GatherableTextDataCount { get; init; }

    /// <summary>GatherableTextDataOffset.</summary>
    public int GatherableTextDataOffset { get; init; }

    /// <summary>ExportCount.</summary>
    public int ExportCount { get; init; }

    /// <summary>ExportOffset.</summary>
    public int ExportOffset { get; init; }

    /// <summary>ImportCount.</summary>
    public int ImportCount { get; init; }

    /// <summary>ImportOffset.</summary>
    public int ImportOffset { get; init; }

    /// <summary>DependsOffset.</summary>
    public int DependsOffset { get; init; }

    /// <summary>SoftPackageReferencesCount.</summary>
    public int SoftPackageReferencesCount { get; init; }

    /// <summary>SoftPackageReferencesOffset.</summary>
    public int SoftPackageReferencesOffset { get; init; }

    /// <summary>SearchableNamesOffset.</summary>
    public int SearchableNamesOffset { get; init; }

    /// <summary>ThumbnailTableOffset.</summary>
    public int ThumbnailTableOffset { get; init; }

    /// <summary>Package guid.</summary>
    public FGuid Guid { get; init; }

    /// <summary>PersistentGuid (only when not FilterEditorOnly).</summary>
    public FGuid? PersistentGuid { get; init; }

    /// <summary>OwnerPersistentGuid (versions 516..517, not FilterEditorOnly).</summary>
    public FGuid? OwnerPersistentGuid { get; init; }

    /// <summary>Generations.</summary>
    public IReadOnlyList<GenerationInfo> Generations { get; init; } = [];

    /// <summary>SavedByEngineVersion.</summary>
    public EngineVersionInfo? SavedByEngineVersion { get; init; }

    /// <summary>CompatibleWithEngineVersion.</summary>
    public EngineVersionInfo? CompatibleWithEngineVersion { get; init; }

    /// <summary>CompressionFlags.</summary>
    public uint CompressionFlags { get; init; }

    /// <summary>Compressed chunk records (16 bytes each; always empty in practice).</summary>
    public IReadOnlyList<byte[]> CompressedChunks { get; init; } = [];

    /// <summary>PackageSource.</summary>
    public uint PackageSource { get; init; }

    /// <summary>AdditionalPackagesToCook.</summary>
    public IReadOnlyList<string> AdditionalPackagesToCook { get; init; } = [];

    /// <summary>NumTextureAllocations (only when LegacyFileVersion &gt; -7).</summary>
    public int NumTextureAllocations { get; init; }

    /// <summary>AssetRegistryDataOffset.</summary>
    public int AssetRegistryDataOffset { get; init; }

    /// <summary>BulkDataStartOffset (= TotalHeaderSize + uexp payload length for cooked packages).</summary>
    public long BulkDataStartOffset { get; init; }

    /// <summary>WorldTileInfoDataOffset.</summary>
    public int WorldTileInfoDataOffset { get; init; }

    /// <summary>ChunkIDs.</summary>
    public IReadOnlyList<int> ChunkIds { get; init; } = [];

    /// <summary>PreloadDependencyCount.</summary>
    public int PreloadDependencyCount { get; init; }

    /// <summary>PreloadDependencyOffset.</summary>
    public int PreloadDependencyOffset { get; init; }

    /// <summary>Offset of the first byte after the summary.</summary>
    public int SummaryEnd { get; init; }

    /// <summary>True when PKG_FilterEditorOnly is set.</summary>
    public bool IsFilterEditorOnly => (PackageFlags & PkgFilterEditorOnly) != 0;

    /// <summary>True when PKG_UnversionedProperties is set (not the case for SCUM).</summary>
    public bool IsUnversionedProperties => (PackageFlags & PkgUnversionedProperties) != 0;

    /// <summary>True when PKG_Cooked is set.</summary>
    public bool IsCooked => (PackageFlags & PkgCooked) != 0;
}
