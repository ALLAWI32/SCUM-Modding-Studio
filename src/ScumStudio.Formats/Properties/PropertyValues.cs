using System.Globalization;

namespace ScumStudio.Formats.Properties;

/// <summary>
/// Base of all decoded property values. <see cref="Offset"/> is the export-relative offset of the value's first byte
/// and <see cref="Size"/> its serialized length, so any value (also array items and struct members) can be
/// patched in place with the same size.
/// </summary>
public abstract record PropertyValue
{
    /// <summary>Export-relative offset of the serialized value.</summary>
    public int Offset { get; init; }

    /// <summary>Serialized size in bytes.</summary>
    public int Size { get; init; }

    /// <summary>Formats a float the way Python prints it (shortest round-trip form).</summary>
    protected static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
}

/// <summary>BoolProperty value (lives in the tag; also used for bool items of arrays).</summary>
public sealed record BoolValue(bool Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value ? "True" : "False";
}

/// <summary>Int8Property.</summary>
public sealed record Int8Value(sbyte Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Int16Property.</summary>
public sealed record Int16Value(short Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>IntProperty (also FrameNumber).</summary>
public sealed record IntValue(int Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Int64Property (also DateTime / Timespan ticks).</summary>
public sealed record Int64Value(long Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>ByteProperty without enum (and byte items of arrays).</summary>
public sealed record ByteValue(byte Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>UInt16Property.</summary>
public sealed record UInt16Value(ushort Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>UInt32Property.</summary>
public sealed record UInt32Value(uint Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>UInt64Property.</summary>
public sealed record UInt64Value(ulong Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>FloatProperty.</summary>
public sealed record FloatValue(float Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => F(Value);
}

/// <summary>DoubleProperty.</summary>
public sealed record DoubleValue(double Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>NameProperty (8 bytes: FName).</summary>
public sealed record NameValue(string Value, FNameRef Ref) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>StrProperty (FString).</summary>
public sealed record StrValue(string Value, bool IsWide) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => "'" + Value + "'";
}

/// <summary>
/// TextProperty (FText). History types None (-1), Base (0) and StringTableEntry (11) are decoded; others keep
/// <see cref="Raw"/> with the bytes after the history type.
/// </summary>
public sealed record TextValue(uint Flags, sbyte HistoryType) : PropertyValue
{
    /// <summary>Namespace (Base history).</summary>
    public string? Namespace { get; init; }

    /// <summary>Key (Base or StringTableEntry history).</summary>
    public string? Key { get; init; }

    /// <summary>Source / culture invariant string.</summary>
    public string? SourceString { get; init; }

    /// <summary>String table id (StringTableEntry history).</summary>
    public string? TableId { get; init; }

    /// <summary>Undecoded remainder for other history types.</summary>
    public byte[]? Raw { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        HistoryType switch
        {
            11 => $"Text[{TableId}:{Key}]",
            _ when SourceString is not null => $"Text'{SourceString}'",
            _ => $"Text(history {HistoryType})",
        };
}

/// <summary>
/// ObjectProperty / ClassProperty / InterfaceProperty / WeakObjectProperty: an FPackageIndex and its
/// <c>Pkg.ref</c> description (<c>None</c>, <c>IMP:x</c>, <c>EXP:x</c>).
/// </summary>
public sealed record ObjectValue(int Index, string Reference) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Reference;
}

/// <summary>SoftObjectProperty / SoftClassProperty / SoftObjectPath struct: FName asset path + FString sub path.</summary>
public sealed record SoftObjectValue(string AssetPath, FNameRef AssetPathRef, string SubPath) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"('{AssetPath}', '{SubPath}')";
}

/// <summary>EnumProperty, or ByteProperty with an enum: the enum value FName.</summary>
public sealed record EnumValue(string? EnumType, string Value, FNameRef Ref) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>A struct serialized as tagged properties up to <c>None</c>.</summary>
public sealed record StructValue(string StructName, IReadOnlyList<PropertyTag> Properties) : PropertyValue
{
    /// <summary>First member with the given name (and array index), or null.</summary>
    public PropertyTag? Find(string name, int arrayIndex = 0) =>
        Properties.FirstOrDefault(p => p.Name == name && p.ArrayIndex == arrayIndex);

    /// <inheritdoc />
    public override string ToString() => $"{StructName}{{{Properties.Count} props}}";
}

/// <summary>Native <c>Vector</c> (3 floats).</summary>
public sealed record VectorValue(float X, float Y, float Z) : PropertyValue
{
    /// <summary>As System.Numerics vector.</summary>
    public System.Numerics.Vector3 ToVector3() => new(X, Y, Z);

    /// <inheritdoc />
    public override string ToString() => $"({F(X)}, {F(Y)}, {F(Z)})";
}

/// <summary>Native <c>Vector2D</c>.</summary>
public sealed record Vector2DValue(float X, float Y) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"({F(X)}, {F(Y)})";
}

/// <summary>Native <c>Vector4</c> or <c>Plane</c> (4 floats).</summary>
public sealed record Vector4Value(float X, float Y, float Z, float W) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"({F(X)}, {F(Y)}, {F(Z)}, {F(W)})";
}

/// <summary>Native <c>Rotator</c> (pitch, yaw, roll in degrees).</summary>
public sealed record RotatorValue(float Pitch, float Yaw, float Roll) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"('rot', {F(Pitch)}, {F(Yaw)}, {F(Roll)})";
}

/// <summary>Native <c>Quat</c>.</summary>
public sealed record QuatValue(float X, float Y, float Z, float W) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"('quat', {F(X)}, {F(Y)}, {F(Z)}, {F(W)})";
}

/// <summary>Native <c>LinearColor</c>.</summary>
public sealed record LinearColorValue(float R, float G, float B, float A) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"({F(R)}, {F(G)}, {F(B)}, {F(A)})";
}

/// <summary>Native <c>Color</c> (4 bytes, stored B, G, R, A).</summary>
public sealed record ColorValue(byte B, byte G, byte R, byte A) : PropertyValue
{
    /// <summary>Raw hex as props.py prints it (b g r a).</summary>
    public string Hex => $"{B:x2}{G:x2}{R:x2}{A:x2}";

    /// <inheritdoc />
    public override string ToString() => Hex;
}

/// <summary>Native <c>Guid</c>, also LazyObjectProperty.</summary>
public sealed record GuidValue(FGuid Value) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => Value.ToString();
}

/// <summary>Native <c>IntPoint</c>.</summary>
public sealed record IntPointValue(int X, int Y) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"({X}, {Y})";
}

/// <summary>Native <c>IntVector</c>.</summary>
public sealed record IntVectorValue(int X, int Y, int Z) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"({X}, {Y}, {Z})";
}

/// <summary>Native <c>Box</c> (Min, Max, u8 IsValid) or <c>Box2D</c> (Z components zero).</summary>
public sealed record BoxValue(VectorValue Min, VectorValue Max, byte IsValid, bool Is2D = false) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"Box(min={Min}, max={Max}, valid={IsValid})";
}

/// <summary>Native <c>BoxSphereBounds</c> (origin, extent, radius).</summary>
public sealed record BoxSphereBoundsValue(VectorValue Origin, VectorValue BoxExtent, float SphereRadius) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"('bounds', origin={Origin}, extent={BoxExtent}, radius={F(SphereRadius)})";
}

/// <summary>A natively serialized FTransform (rotation quat, translation, scale = 10 floats).</summary>
public sealed record TransformValue(QuatValue Rotation, VectorValue Translation, VectorValue Scale3D) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"('xform', rot={Rotation}, t={Translation}, s={Scale3D})";
}

/// <summary>A named field of a natively serialized struct.</summary>
public sealed record NativeField(string Name, PropertyValue Value);

/// <summary>
/// Another natively serialized struct (e.g. <c>RichCurveKey</c>, <c>SkeletalMeshSamplingLODBuiltData</c>,
/// <c>KeyHandleMap</c>), decoded field by field.
/// </summary>
public sealed record NativeStructValue(string StructName, IReadOnlyList<NativeField> Fields) : PropertyValue
{
    /// <summary>The field with the given name, or null.</summary>
    public PropertyValue? this[string name] => Fields.FirstOrDefault(f => f.Name == name)?.Value;

    /// <inheritdoc />
    public override string ToString() => $"{StructName}({string.Join(", ", Fields.Select(f => $"{f.Name}={f.Value}"))})";
}

/// <summary>PerPlatformFloat / PerPlatformInt / PerPlatformBool: cooked flag + default value.</summary>
public sealed record PerPlatformValue(bool Cooked, PropertyValue Default) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"('perplat', {Default})";
}

/// <summary>ArrayProperty (and natively serialized <c>GameplayTagContainer</c>).</summary>
/// <param name="InnerType">Element property type.</param>
/// <param name="Items">Decoded elements.</param>
public sealed record ArrayValue(string InnerType, IReadOnlyList<PropertyValue> Items) : PropertyValue
{
    /// <summary>Element struct name for arrays of structs (from the inner tag).</summary>
    public string? StructName { get; init; }

    /// <summary>The inner tag of an array of structs (name, size of all items, struct guid).</summary>
    public PropertyTag? InnerTag { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"Array<{StructName ?? InnerType}> x{Items.Count}";
}

/// <summary>SetProperty.</summary>
public sealed record SetValue(string InnerType, IReadOnlyList<PropertyValue> Removed, IReadOnlyList<PropertyValue> Items) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"Set<{InnerType}> x{Items.Count}";
}

/// <summary>One key/value pair of a map.</summary>
public sealed record MapEntry(PropertyValue Key, PropertyValue Value);

/// <summary>MapProperty.</summary>
public sealed record MapValue(string KeyType, string ValueType, IReadOnlyList<PropertyValue> RemovedKeys, IReadOnlyList<MapEntry> Entries) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"Map<{KeyType}, {ValueType}> x{Entries.Count}";
}

/// <summary>DelegateProperty (object + function name).</summary>
public sealed record DelegateValue(int Object, string ObjectReference, string FunctionName) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"{ObjectReference}::{FunctionName}";
}

/// <summary>Multicast (inline / sparse) delegate: list of bound delegates.</summary>
public sealed record MulticastDelegateValue(IReadOnlyList<DelegateValue> Delegates) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"[{string.Join(", ", Delegates)}]";
}

/// <summary>FieldPathProperty: path names + resolved owner.</summary>
public sealed record FieldPathValue(IReadOnlyList<string> Path, int Owner, string OwnerReference) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() => $"{OwnerReference}:{string.Join('.', Path)}";
}

/// <summary>Bytes that could not be decoded (unknown type or a layout mismatch); <see cref="Error"/> says why.</summary>
public sealed record RawValue(byte[] Bytes, string? Error = null) : PropertyValue
{
    /// <inheritdoc />
    public override string ToString() =>
        Convert.ToHexString(Bytes.AsSpan(0, Math.Min(32, Bytes.Length))).ToLowerInvariant() + (Error is null ? "" : $" <{Error}>");
}
