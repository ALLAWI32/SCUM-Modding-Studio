namespace ScumStudio.Formats.Packages;

/// <summary>
/// A name table entry (FNameEntrySerialized): the string plus the two 16-bit hashes stored after it.
/// </summary>
/// <param name="Value">The name string.</param>
/// <param name="NonCasePreservingHash">Stored <c>StrCrc_DEPRECATED(upper)</c> low 16 bits.</param>
/// <param name="CasePreservingHash">Stored <c>StrCrc32</c> low 16 bits.</param>
/// <param name="IsWide">True when the string was stored as UTF-16.</param>
public sealed record NameEntry(string Value, ushort NonCasePreservingHash, ushort CasePreservingHash, bool IsWide = false);

/// <summary>An import table entry (28 bytes: FName ClassPackage, FName ClassName, int32 Outer, FName ObjectName).</summary>
/// <param name="ClassPackage">Class package name (e.g. <c>/Script/CoreUObject</c>).</param>
/// <param name="ClassName">Class name (e.g. <c>Package</c>).</param>
/// <param name="OuterIndex">FPackageIndex of the outer (0 = none).</param>
/// <param name="ObjectName">Object name.</param>
public sealed record ImportEntry(FNameRef ClassPackage, FNameRef ClassName, int OuterIndex, FNameRef ObjectName)
{
    /// <summary>Size on disk.</summary>
    public const int SerializedSize = 28;
}

/// <summary>
/// An export table entry (FObjectExport, 104 bytes in UE 4.27 cooked packages). Integer flags that are
/// UE bools are kept as int32 so a rebuild is byte-exact whatever value the cooker wrote.
/// </summary>
public sealed record ExportEntry
{
    /// <summary>Size on disk.</summary>
    public const int SerializedSize = 104;

    /// <summary>ClassIndex (FPackageIndex).</summary>
    public int ClassIndex { get; init; }

    /// <summary>SuperIndex (FPackageIndex).</summary>
    public int SuperIndex { get; init; }

    /// <summary>TemplateIndex (FPackageIndex).</summary>
    public int TemplateIndex { get; init; }

    /// <summary>OuterIndex (FPackageIndex).</summary>
    public int OuterIndex { get; init; }

    /// <summary>ObjectName.</summary>
    public FNameRef ObjectName { get; init; }

    /// <summary>ObjectFlags (EObjectFlags).</summary>
    public uint ObjectFlags { get; init; }

    /// <summary>SerialSize (bytes of this export in the .uexp).</summary>
    public long SerialSize { get; init; }

    /// <summary>SerialOffset (absolute: TotalHeaderSize + offset in the .uexp).</summary>
    public long SerialOffset { get; init; }

    /// <summary>bForcedExport.</summary>
    public int ForcedExport { get; init; }

    /// <summary>bNotForClient.</summary>
    public int NotForClient { get; init; }

    /// <summary>bNotForServer.</summary>
    public int NotForServer { get; init; }

    /// <summary>PackageGuid.</summary>
    public FGuid PackageGuid { get; init; }

    /// <summary>PackageFlags.</summary>
    public uint PackageFlags { get; init; }

    /// <summary>bNotAlwaysLoadedForEditorGame.</summary>
    public int NotAlwaysLoadedForEditorGame { get; init; }

    /// <summary>bIsAsset.</summary>
    public int IsAsset { get; init; }

    /// <summary>FirstExportDependency (index into the preload dependency array, -1 = none).</summary>
    public int FirstExportDependency { get; init; }

    /// <summary>SerializationBeforeSerializationDependencies count.</summary>
    public int SerializationBeforeSerializationDependencies { get; init; }

    /// <summary>CreateBeforeSerializationDependencies count.</summary>
    public int CreateBeforeSerializationDependencies { get; init; }

    /// <summary>SerializationBeforeCreateDependencies count.</summary>
    public int SerializationBeforeCreateDependencies { get; init; }

    /// <summary>CreateBeforeCreateDependencies count.</summary>
    public int CreateBeforeCreateDependencies { get; init; }

    /// <summary>Total number of preload dependency entries this export owns.</summary>
    public int PreloadDependencyTotal =>
        SerializationBeforeSerializationDependencies + CreateBeforeSerializationDependencies +
        SerializationBeforeCreateDependencies + CreateBeforeCreateDependencies;
}
