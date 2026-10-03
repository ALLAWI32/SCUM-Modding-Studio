namespace ScumStudio.Formats.Properties;

/// <summary>
/// One serialized FPropertyTag plus its decoded value (port of the tag dict built by <c>props.py read_tagged</c>).
/// All offsets are relative to the start of the export payload (the <c>start</c> key of props.py); convert them to
/// <c>.uexp</c> offsets with <see cref="PropertyBlock.ToUExpOffset"/>.
/// </summary>
public sealed class PropertyTag
{
    /// <summary>Property name (formatted, e.g. <c>RelativeLocation</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The raw FName of the property name.</summary>
    public FNameRef NameRef { get; init; }

    /// <summary>Property type (e.g. <c>FloatProperty</c>, <c>StructProperty</c>).</summary>
    public required string Type { get; init; }

    /// <summary>Serialized value size in bytes.</summary>
    public int Size { get; init; }

    /// <summary>Static array index.</summary>
    public int ArrayIndex { get; init; }

    /// <summary>Struct name for <c>StructProperty</c>.</summary>
    public string? StructName { get; init; }

    /// <summary>Struct guid for <c>StructProperty</c>.</summary>
    public FGuid? StructGuid { get; init; }

    /// <summary>Value of a <c>BoolProperty</c> (stored in the tag, the value itself has size 0).</summary>
    public bool? BoolValue { get; init; }

    /// <summary>Enum name for <c>ByteProperty</c> / <c>EnumProperty</c> (<c>None</c> for a plain byte).</summary>
    public string? EnumName { get; init; }

    /// <summary>Inner type for <c>ArrayProperty</c> / <c>SetProperty</c>, key type for <c>MapProperty</c>.</summary>
    public string? InnerType { get; init; }

    /// <summary>Value type for <c>MapProperty</c>.</summary>
    public string? ValueType { get; init; }

    /// <summary>HasPropertyGuid flag.</summary>
    public bool HasPropertyGuid { get; init; }

    /// <summary>Property guid when <see cref="HasPropertyGuid"/>.</summary>
    public FGuid? PropertyGuid { get; init; }

    /// <summary>Offset of the tag header (its name FName).</summary>
    public int Offset { get; init; }

    /// <summary>Offset of the int32 Size field (tag start + 16), for size fix-ups after an insertion.</summary>
    public int SizeFieldOffset => Offset + 16;

    /// <summary>Offset of the bool byte of a <c>BoolProperty</c> (-1 for other types).</summary>
    public int BoolValueOffset { get; init; } = -1;

    /// <summary>Offset of the value (props.py <c>start</c>).</summary>
    public int ValueOffset { get; init; }

    /// <summary>Offset just after the value.</summary>
    public int EndOffset => ValueOffset + Size;

    /// <summary>Decoded value (<see cref="BoolValue"/> for booleans; <see cref="RawValue"/> when not decodable).</summary>
    public required PropertyValue Value { get; init; }

    /// <summary>Type label like props.fmt: <c>Type [Struct]</c>.</summary>
    public string TypeLabel => StructName is null ? Type : $"{Type} {StructName}";

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({TypeLabel}{(ArrayIndex != 0 ? $"[{ArrayIndex}]" : "")}) = {Value}";
}
