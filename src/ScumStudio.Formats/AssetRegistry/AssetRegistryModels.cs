namespace ScumStudio.Formats.AssetRegistry;

/// <summary>Value types of the fixed tag store (low 3 bits of a value id).</summary>
public enum AssetRegistryValueKind
{
    /// <summary>ANSI string (0).</summary>
    AnsiString = 0,

    /// <summary>Wide string (1).</summary>
    WideString = 1,

    /// <summary>Name without number (2).</summary>
    NumberlessName = 2,

    /// <summary>Name with number (3).</summary>
    Name = 3,

    /// <summary>Export path of numberless names (4).</summary>
    NumberlessExportPath = 4,

    /// <summary>Export path (5).</summary>
    ExportPath = 5,

    /// <summary>Localized text (6), kept as the raw serialized FString.</summary>
    LocalizedText = 6,
}

/// <summary>A decoded tag value (port of the tuples returned by <c>assetreg.py Registry.value_str</c>).</summary>
/// <param name="Kind">Value type.</param>
/// <param name="Text">Display text (the string, name, or <c>Class'Package.Object'</c> for export paths).</param>
public sealed record AssetRegistryValue(AssetRegistryValueKind Kind, string Text)
{
    /// <summary>Name number (Name kind only; stored value, 0 = none).</summary>
    public int NameNumber { get; init; }

    /// <summary>(Class, Object, Package) for export path kinds.</summary>
    public IReadOnlyList<string>? ExportPath { get; init; }

    /// <summary>Raw serialized text (int32 length + chars) for <see cref="AssetRegistryValueKind.LocalizedText"/>.</summary>
    public byte[]? RawText { get; init; }

    /// <summary>Creates an ANSI string value.</summary>
    public static AssetRegistryValue Ansi(string text) => new(AssetRegistryValueKind.AnsiString, text);

    /// <summary>Creates a numberless name value.</summary>
    public static AssetRegistryValue NumberlessName(string name) => new(AssetRegistryValueKind.NumberlessName, name);

    /// <summary>Creates a numberless export path value.</summary>
    public static AssetRegistryValue NumberlessExport(string className, string objectName, string packageName) =>
        new(AssetRegistryValueKind.NumberlessExportPath, $"{className}'{packageName}.{objectName}'")
        {
            ExportPath = [className, objectName, packageName],
        };

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>One asset record (FAssetData) of the registry.</summary>
/// <param name="Index">Position in the asset list.</param>
/// <param name="ObjectPath">e.g. <c>/Game/Foo/Bar.Bar</c>.</param>
/// <param name="PackagePath">e.g. <c>/Game/Foo</c>.</param>
/// <param name="AssetClass">e.g. <c>SkeletalMesh</c>.</param>
/// <param name="PackageName">e.g. <c>/Game/Foo/Bar</c>.</param>
/// <param name="AssetName">e.g. <c>Bar</c>.</param>
/// <param name="TagMapHandle">Fixed tag map handle (bit 63 numberless keys, bits 32..47 pair count, low 32 first pair).</param>
public sealed record AssetData(int Index, string ObjectPath, string PackagePath, string AssetClass, string PackageName, string AssetName, ulong TagMapHandle)
{
    /// <summary>Number of tag pairs referenced by the handle.</summary>
    public int TagCount => (int)((TagMapHandle >> 32) & 0xFFFF);

    /// <summary>True when the handle refers to numberless key pairs.</summary>
    public bool HasNumberlessKeys => (TagMapHandle >> 63) != 0;
}
