using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Formats.Properties;

/// <summary>
/// Reads tagged (versioned) property blocks of cooked UE 4.27 packages (port of <c>props.py</c>
/// <c>read_tagged</c> / <c>decode_value</c>, extended to all property types used by SCUM).
/// </summary>
/// <remarks>
/// Every tag value is decoded from its own <c>Size</c>-byte window, like props.py which slices <c>raw</c> first, so a
/// value that cannot be decoded never derails the rest of the block: it becomes a <see cref="RawValue"/>.
/// Natively serialized structs (binary, no tags) are listed in <see cref="NativeStructs"/>; every other struct is
/// read as tagged members up to <c>None</c>. Unlike props.py, <c>Transform</c> is read as a TAGGED struct
/// (FORMAT_NOTES: "element Transform (Translation/Scale3D tags)"); the 10-float layout is only a fallback.
/// </remarks>
public sealed class PropertyReader
{
    /// <summary>Struct names serialized natively (binary) inside tagged property blocks, with their fixed size (null = variable).</summary>
    public static IReadOnlyDictionary<string, int?> NativeStructs { get; } = new Dictionary<string, int?>(StringComparer.Ordinal)
    {
        // props.py decode_value native list (minus Transform, which is tagged in 4.27)
        ["Vector"] = 12,
        ["Rotator"] = 12,
        ["Quat"] = 16,
        ["Guid"] = 16,
        ["LinearColor"] = 16,
        ["Color"] = 4,
        ["Vector2D"] = 8,
        ["Vector4"] = 16,
        ["IntPoint"] = 8,
        ["PerPlatformFloat"] = 8,
        ["PerPlatformInt"] = 8,
        ["PerPlatformBool"] = null,
        ["SoftObjectPath"] = null,
        ["BoxSphereBounds"] = 28,
        // additional UE 4.27 structs with native serializers
        ["Box"] = 25,
        ["Box2D"] = 17,
        ["IntVector"] = 12,
        ["Plane"] = 16,
        ["SoftClassPath"] = null,
        ["StringAssetReference"] = null,
        ["StringClassReference"] = null,
        ["DateTime"] = 8,
        ["Timespan"] = 8,
        ["FrameNumber"] = 4,
        ["GameplayTagContainer"] = null,
        ["Vector_NetQuantize"] = 12,
        ["Vector_NetQuantize10"] = 12,
        ["Vector_NetQuantize100"] = 12,
        ["Vector_NetQuantizeNormal"] = 12,
        ["RichCurveKey"] = 27,
        ["SimpleCurveKey"] = 8,
        ["KeyHandleMap"] = 0,
        ["NavAgentSelector"] = 4,
        ["PerQualityLevelInt"] = null,
        ["SkeletalMeshSamplingLODBuiltData"] = null,
        ["SkeletalMeshSamplingRegionBuiltData"] = null,
    };

    private readonly IReadOnlyList<string> _names;
    private readonly Func<int, string> _resolveIndex;
    private readonly byte[] _data;
    private readonly int _base;

    /// <summary>Creates a reader over <paramref name="data"/>; offsets are reported relative to <paramref name="baseOffset"/>.</summary>
    /// <param name="names">Package name table.</param>
    /// <param name="resolveIndex">FPackageIndex formatter (e.g. <see cref="CookedPackage.ResolveIndex"/>).</param>
    /// <param name="data">Buffer holding the block.</param>
    /// <param name="baseOffset">Absolute position in <paramref name="data"/> that counts as offset 0.</param>
    public PropertyReader(IReadOnlyList<string> names, Func<int, string> resolveIndex, byte[] data, int baseOffset)
    {
        _names = names ?? throw new ArgumentNullException(nameof(names));
        _resolveIndex = resolveIndex ?? throw new ArgumentNullException(nameof(resolveIndex));
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _base = baseOffset;
    }

    /// <summary>Reads the property block at the start of export <paramref name="exportIndex"/> (0-based).</summary>
    /// <exception cref="FormatException">The export does not start with a readable tagged property block.</exception>
    public static PropertyBlock ReadExport(CookedPackage package, int exportIndex)
    {
        ArgumentNullException.ThrowIfNull(package);
        var (offset, length) = package.GetExportRange(exportIndex);
        var reader = new PropertyReader(package.Names, package.ResolveIndex, package.UExp, offset);
        var r = new ByteReader(package.UExp, offset, length);
        var props = reader.ReadTaggedList(r);
        return new PropertyBlock
        {
            ExportIndex = exportIndex,
            ExportName = package.ResolveName(package.Exports[exportIndex].ObjectName),
            ClassName = package.GetExportClassName(exportIndex),
            UExpBaseOffset = offset,
            PayloadLength = length,
            EndOffset = r.Position - offset,
            Properties = props,
        };
    }

    /// <summary>
    /// Reads a property block from an arbitrary export payload (e.g. an edited copy). Offsets are relative to the payload.
    /// </summary>
    public static PropertyBlock ReadPayload(CookedPackage package, byte[] payload, int exportIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(payload);
        var reader = new PropertyReader(package.Names, package.ResolveIndex, payload, 0);
        var r = new ByteReader(payload);
        var props = reader.ReadTaggedList(r);
        return new PropertyBlock
        {
            ExportIndex = exportIndex,
            ExportName = exportIndex >= 0 ? package.ResolveName(package.Exports[exportIndex].ObjectName) : null,
            ClassName = exportIndex >= 0 ? package.GetExportClassName(exportIndex) : null,
            UExpBaseOffset = exportIndex >= 0 ? package.GetExportUExpOffset(exportIndex) : 0,
            PayloadLength = payload.Length,
            EndOffset = r.Position,
            Properties = props,
        };
    }

    /// <summary>
    /// Reads tags until <c>None</c> (port of <c>read_tagged</c>); leaves <paramref name="r"/> just after the terminator.
    /// </summary>
    public IReadOnlyList<PropertyTag> ReadTaggedList(ByteReader r)
    {
        var props = new List<PropertyTag>();
        while (true)
        {
            var tagStart = r.Position;
            var nameRef = r.FName();
            var name = Name(nameRef);
            if (name == "None")
            {
                return props;
            }

            var type = Name(r.FName());
            var size = r.I32();
            var arrayIndex = r.I32();
            string? structName = null, enumName = null, inner = null, valueType = null;
            FGuid? structGuid = null, propGuid = null;
            bool? boolValue = null;
            var boolOffset = -1;
            switch (type)
            {
                case "StructProperty":
                    structName = Name(r.FName());
                    structGuid = r.Guid();
                    break;
                case "BoolProperty":
                    boolOffset = r.Position - _base;
                    boolValue = r.U8() != 0;
                    break;
                case "ByteProperty":
                case "EnumProperty":
                    enumName = Name(r.FName());
                    break;
                case "ArrayProperty":
                case "SetProperty":
                    inner = Name(r.FName());
                    break;
                case "MapProperty":
                    inner = Name(r.FName());
                    valueType = Name(r.FName());
                    break;
            }

            var hasGuid = r.U8() != 0;
            if (hasGuid)
            {
                propGuid = r.Guid();
            }

            var valueStart = r.Position;
            if (size < 0 || size > r.End - valueStart)
            {
                throw new FormatException($"Property '{name}' ({type}) at {tagStart - _base} has size {size} beyond the block end.");
            }

            PropertyValue value = type == "BoolProperty"
                ? new BoolValue(boolValue!.Value) { Offset = boolOffset, Size = 1 }
                : DecodeTagValue(type, structName, enumName, inner, valueType, valueStart, size);

            props.Add(new PropertyTag
            {
                Name = name,
                NameRef = nameRef,
                Type = type,
                Size = size,
                ArrayIndex = arrayIndex,
                StructName = structName,
                StructGuid = structGuid,
                BoolValue = boolValue,
                EnumName = enumName,
                InnerType = inner,
                ValueType = valueType,
                HasPropertyGuid = hasGuid,
                PropertyGuid = propGuid,
                Offset = tagStart - _base,
                BoolValueOffset = boolOffset,
                ValueOffset = valueStart - _base,
                Value = value,
            });
            r.Position = valueStart + size;
        }
    }

    private string Name(FNameRef name)
    {
        if (name.Index < 0 || name.Index >= _names.Count || name.Number < 0)
        {
            throw new FormatException($"Invalid FName ({name.Index}, {name.Number}).");
        }

        return name.Format(_names);
    }

    private int Rel(int absolute) => absolute - _base;

    private T At<T>(T value, int start, ByteReader r)
        where T : PropertyValue => value with { Offset = Rel(start), Size = r.Position - start };

    // Decodes the value window of one tag; tries alternatives until one consumes exactly `size` bytes.
    private PropertyValue DecodeTagValue(string type, string? structName, string? enumName, string? inner, string? valueType, int start, int size)
    {
        var attempts = new List<Func<ByteReader, PropertyValue>>();
        switch (type)
        {
            case "StructProperty":
                var sn = structName!;
                if (NativeStructs.ContainsKey(sn))
                {
                    attempts.Add(r => ReadNativeStruct(r, sn, size));
                    attempts.Add(r => ReadTaggedStruct(r, sn));
                }
                else
                {
                    attempts.Add(r => ReadTaggedStruct(r, sn));
                    if (size == 40 && sn == "Transform")
                    {
                        attempts.Add(r => ReadNativeTransform(r));
                    }
                }

                break;
            case "ByteProperty":
                attempts.Add(r => size == 1 || enumName is null or "None"
                    ? ReadElement(r, "ByteProperty", null, byteIsName: false)
                    : ReadEnum(r, enumName));
                break;
            case "EnumProperty":
                attempts.Add(r => ReadEnum(r, enumName));
                break;
            case "ArrayProperty":
                attempts.Add(r => ReadArray(r, inner!, size));
                break;
            case "SetProperty":
                attempts.Add(r => ReadSet(r, inner!, size));
                break;
            case "MapProperty":
                attempts.Add(r => ReadMap(r, inner!, valueType!, byteKeysAsNames: false));
                attempts.Add(r => ReadMap(r, inner!, valueType!, byteKeysAsNames: true));
                break;
            default:
                attempts.Add(r => ReadElement(r, type, null, byteIsName: false));
                break;
        }

        string? error = null;
        foreach (var attempt in attempts)
        {
            var r = new ByteReader(_data, start, size);
            try
            {
                var value = attempt(r);
                if (r.Position == start + size)
                {
                    return value;
                }

                error ??= $"decoded {r.Position - start} of {size} bytes";
            }
            catch (Exception ex) when (ex is FormatException or EndOfStreamException or NotSupportedException or ArgumentOutOfRangeException)
            {
                error ??= ex.Message;
            }
        }

        return new RawValue(_data.AsSpan(start, size).ToArray(), error) { Offset = Rel(start), Size = size };
    }

    private PropertyValue ReadEnum(ByteReader r, string? enumName)
    {
        var start = r.Position;
        var fn = r.FName();
        return At(new EnumValue(enumName, Name(fn), fn), start, r);
    }

    /// <summary>Reads one value of a non-container type (array/set/map element or a simple tag value).</summary>
    private PropertyValue ReadElement(ByteReader r, string type, string? structName, bool byteIsName)
    {
        var s = r.Position;
        switch (type)
        {
            case "BoolProperty": return At(new BoolValue(r.U8() != 0), s, r);
            case "Int8Property": return At(new Int8Value(r.I8()), s, r);
            case "Int16Property": return At(new Int16Value(r.I16()), s, r);
            case "IntProperty": return At(new IntValue(r.I32()), s, r);
            case "Int64Property": return At(new Int64Value(r.I64()), s, r);
            case "ByteProperty":
                if (byteIsName)
                {
                    var bn = r.FName();
                    return At(new EnumValue(null, Name(bn), bn), s, r);
                }

                return At(new ByteValue(r.U8()), s, r);
            case "UInt16Property": return At(new UInt16Value(r.U16()), s, r);
            case "UInt32Property": return At(new UInt32Value(r.U32()), s, r);
            case "UInt64Property": return At(new UInt64Value(r.U64()), s, r);
            case "FloatProperty": return At(new FloatValue(r.F32()), s, r);
            case "DoubleProperty": return At(new DoubleValue(r.F64()), s, r);
            case "NameProperty":
                var n = r.FName();
                return At(new NameValue(Name(n), n), s, r);
            case "StrProperty":
                var (str, wide) = r.FStringWithEncoding();
                return At(new StrValue(str, wide), s, r);
            case "TextProperty": return ReadText(r);
            case "ObjectProperty":
            case "ClassProperty":
            case "InterfaceProperty":
            case "WeakObjectProperty":
                var idx = r.I32();
                return At(new ObjectValue(idx, _resolveIndex(idx)), s, r);
            case "LazyObjectProperty": return At(new GuidValue(r.Guid()), s, r);
            case "SoftObjectProperty":
            case "SoftClassProperty":
            case "AssetObjectProperty":
            case "AssetClassProperty":
                return ReadSoftObjectPath(r);
            case "EnumProperty": return ReadEnum(r, null);
            case "StructProperty":
                return structName is not null && NativeStructs.ContainsKey(structName)
                    ? ReadNativeStruct(r, structName, null)
                    : ReadTaggedStruct(r, structName ?? "?");
            case "DelegateProperty": return ReadDelegate(r);
            case "MulticastDelegateProperty":
            case "MulticastInlineDelegateProperty":
            case "MulticastSparseDelegateProperty":
                var count = r.I32();
                CheckCount(count, 12, r);
                var list = new List<DelegateValue>(count);
                for (var i = 0; i < count; i++)
                {
                    list.Add(ReadDelegate(r));
                }

                return At(new MulticastDelegateValue(list), s, r);
            case "FieldPathProperty":
                var pc = r.I32();
                CheckCount(pc, 8, r);
                var path = new List<string>(pc);
                for (var i = 0; i < pc; i++)
                {
                    path.Add(Name(r.FName()));
                }

                var owner = r.I32();
                return At(new FieldPathValue(path, owner, _resolveIndex(owner)), s, r);
            default:
                throw new NotSupportedException($"Unsupported property type '{type}'.");
        }
    }

    private DelegateValue ReadDelegate(ByteReader r)
    {
        var s = r.Position;
        var obj = r.I32();
        var fn = Name(r.FName());
        return At(new DelegateValue(obj, _resolveIndex(obj), fn), s, r);
    }

    private SoftObjectValue ReadSoftObjectPath(ByteReader r)
    {
        var s = r.Position;
        var fn = r.FName();
        var path = Name(fn);
        var sub = r.FString();
        return At(new SoftObjectValue(path, fn, sub), s, r);
    }

    // FText: u32 flags, i8 history type, history payload.
    private TextValue ReadText(ByteReader r)
    {
        var s = r.Position;
        var flags = r.U32();
        var history = r.I8();
        var text = new TextValue(flags, history);
        switch (history)
        {
            case -1:
                var hasInvariant = r.I32() != 0;
                text = text with { SourceString = hasInvariant ? r.FString() : null };
                break;
            case 0:
                text = text with { Namespace = r.FString(), Key = r.FString(), SourceString = r.FString() };
                break;
            case 11:
                text = text with { TableId = Name(r.FName()), Key = r.FString() };
                break;
            default:
                text = text with { Raw = r.Raw(r.Remaining) };
                break;
        }

        return At(text, s, r);
    }

    private StructValue ReadTaggedStruct(ByteReader r, string structName)
    {
        var s = r.Position;
        var members = ReadTaggedList(r);
        return At(new StructValue(structName, members), s, r);
    }

    private TransformValue ReadNativeTransform(ByteReader r)
    {
        var s = r.Position;
        var q = ReadQuat(r);
        var t = ReadVector(r);
        var sc = ReadVector(r);
        return At(new TransformValue(q, t, sc), s, r);
    }

    private VectorValue ReadVector(ByteReader r)
    {
        var s = r.Position;
        return At(new VectorValue(r.F32(), r.F32(), r.F32()), s, r);
    }

    private QuatValue ReadQuat(ByteReader r)
    {
        var s = r.Position;
        return At(new QuatValue(r.F32(), r.F32(), r.F32(), r.F32()), s, r);
    }

    private PropertyValue ReadNativeStruct(ByteReader r, string structName, int? size)
    {
        var s = r.Position;
        switch (structName)
        {
            case "Vector":
            case "Vector_NetQuantize":
            case "Vector_NetQuantize10":
            case "Vector_NetQuantize100":
            case "Vector_NetQuantizeNormal":
                return ReadVector(r);
            case "Rotator": return At(new RotatorValue(r.F32(), r.F32(), r.F32()), s, r);
            case "Quat": return ReadQuat(r);
            case "Guid": return At(new GuidValue(r.Guid()), s, r);
            case "LinearColor": return At(new LinearColorValue(r.F32(), r.F32(), r.F32(), r.F32()), s, r);
            case "Color": return At(new ColorValue(r.U8(), r.U8(), r.U8(), r.U8()), s, r);
            case "Vector2D": return At(new Vector2DValue(r.F32(), r.F32()), s, r);
            case "Vector4":
            case "Plane":
                return At(new Vector4Value(r.F32(), r.F32(), r.F32(), r.F32()), s, r);
            case "IntPoint": return At(new IntPointValue(r.I32(), r.I32()), s, r);
            case "IntVector": return At(new IntVectorValue(r.I32(), r.I32(), r.I32()), s, r);
            case "PerPlatformFloat":
                var cf = r.I32() != 0;
                return At(new PerPlatformValue(cf, ReadElement(r, "FloatProperty", null, false)), s, r);
            case "PerPlatformInt":
                var ci = r.I32() != 0;
                return At(new PerPlatformValue(ci, ReadElement(r, "IntProperty", null, false)), s, r);
            case "PerPlatformBool":
                var cb = r.I32() != 0;
                var bs = r.Position;
                PropertyValue b = size == 8 ? At(new BoolValue(r.I32() != 0), bs, r) : ReadElement(r, "BoolProperty", null, false);
                return At(new PerPlatformValue(cb, b), s, r);
            case "SoftObjectPath":
            case "SoftClassPath":
            case "StringAssetReference":
            case "StringClassReference":
                return ReadSoftObjectPath(r);
            case "BoxSphereBounds":
                var o = ReadVector(r);
                var e = ReadVector(r);
                return At(new BoxSphereBoundsValue(o, e, r.F32()), s, r);
            case "Box":
                var mn = ReadVector(r);
                var mx = ReadVector(r);
                return At(new BoxValue(mn, mx, r.U8()), s, r);
            case "Box2D":
                var m2S = r.Position;
                var m2 = At(new VectorValue(r.F32(), r.F32(), 0), m2S, r);
                var x2S = r.Position;
                var x2 = At(new VectorValue(r.F32(), r.F32(), 0), x2S, r);
                return At(new BoxValue(m2, x2, r.U8(), Is2D: true), s, r);
            case "DateTime":
            case "Timespan":
                return At(new Int64Value(r.I64()), s, r);
            case "FrameNumber": return At(new IntValue(r.I32()), s, r);
            case "GameplayTagContainer":
                var n = r.I32();
                CheckCount(n, 8, r);
                var tags = new List<PropertyValue>(n);
                for (var i = 0; i < n; i++)
                {
                    tags.Add(ReadElement(r, "NameProperty", null, false));
                }

                return At(new ArrayValue("NameProperty", tags) { StructName = "GameplayTagContainer" }, s, r);
            case "RichCurveKey":
                return At(new NativeStructValue(structName,
                [
                    Field(r, "InterpMode", "ByteProperty"),
                    Field(r, "TangentMode", "ByteProperty"),
                    Field(r, "TangentWeightMode", "ByteProperty"),
                    Field(r, "Time", "FloatProperty"),
                    Field(r, "Value", "FloatProperty"),
                    Field(r, "ArriveTangent", "FloatProperty"),
                    Field(r, "ArriveTangentWeight", "FloatProperty"),
                    Field(r, "LeaveTangent", "FloatProperty"),
                    Field(r, "LeaveTangentWeight", "FloatProperty"),
                ]), s, r);
            case "SimpleCurveKey":
                return At(new NativeStructValue(structName, [Field(r, "Time", "FloatProperty"), Field(r, "Value", "FloatProperty")]), s, r);
            case "KeyHandleMap":
                // FKeyHandleMap::Serialize only writes when transacting: nothing in cooked data.
                return At(new NativeStructValue(structName, []), s, r);
            case "NavAgentSelector":
                return At(new NativeStructValue(structName, [Field(r, "PackedBits", "UInt32Property")]), s, r);
            case "PerQualityLevelInt":
                var cq = r.I32() != 0;
                var def = ReadElement(r, "IntProperty", null, false);
                var mapStart = r.Position;
                var pairs = r.I32();
                CheckCount(pairs, 8, r);
                var entries = new List<MapEntry>(pairs);
                for (var i = 0; i < pairs; i++)
                {
                    entries.Add(new MapEntry(ReadElement(r, "IntProperty", null, false), ReadElement(r, "IntProperty", null, false)));
                }

                var map = At(new MapValue("IntProperty", "IntProperty", [], entries), mapStart, r);
                return At(new NativeStructValue(structName, [new NativeField("Cooked", new BoolValue(cq)), new NativeField("Default", def), new NativeField("PerQuality", map)]), s, r);
            case "SkeletalMeshSamplingLODBuiltData":
                // FWeightedRandomSampler: TArray<float> Prob, TArray<int32> Alias, float TotalWeight
                return At(new NativeStructValue(structName, ReadWeightedSampler(r)), s, r);
            case "SkeletalMeshSamplingRegionBuiltData":
                var fields = new List<NativeField>
                {
                    new("TriangleIndices", ReadPlainArray(r, "IntProperty")),
                    new("Vertices", ReadPlainArray(r, "IntProperty")),
                    new("BoneIndices", ReadPlainArray(r, "IntProperty")),
                };
                fields.AddRange(ReadWeightedSampler(r));
                return At(new NativeStructValue(structName, fields), s, r);
            default:
                throw new NotSupportedException($"No native reader for struct '{structName}'.");
        }
    }

    private NativeField Field(ByteReader r, string name, string type) => new(name, ReadElement(r, type, null, false));

    private ArrayValue ReadPlainArray(ByteReader r, string type)
    {
        var s = r.Position;
        var n = r.I32();
        CheckCount(n, 4, r);
        var items = new List<PropertyValue>(n);
        for (var i = 0; i < n; i++)
        {
            items.Add(ReadElement(r, type, null, false));
        }

        return At(new ArrayValue(type, items), s, r);
    }

    private List<NativeField> ReadWeightedSampler(ByteReader r) =>
    [
        new("Prob", ReadPlainArray(r, "FloatProperty")),
        new("Alias", ReadPlainArray(r, "IntProperty")),
        Field(r, "TotalWeight", "FloatProperty"),
    ];

    private static void CheckCount(int count, int minSize, ByteReader r)
    {
        if (count < 0 || (long)count * minSize > r.Remaining)
        {
            throw new FormatException($"Implausible element count {count} at {r.Position}.");
        }
    }

    private ArrayValue ReadArray(ByteReader r, string inner, int size)
    {
        var s = r.Position;
        var n = r.I32();
        if (inner == "StructProperty")
        {
            // port of the inner-tag handling in props.py decode_value (ArrayProperty of StructProperty)
            var innerStart = r.Position;
            var innerNameRef = r.FName();
            var innerName = Name(innerNameRef);
            var innerType = Name(r.FName());
            var innerSize = r.I32();
            var innerIndex = r.I32();
            var sname = Name(r.FName());
            var sguid = r.Guid();
            var innerHasGuid = r.U8() != 0;
            FGuid? innerPropGuid = innerHasGuid ? r.Guid() : null;
            var itemsStart = r.Position;
            CheckCount(n, 1, r);
            var native = NativeStructs.TryGetValue(sname, out var fixedSize)
                         && (fixedSize is null || (long)fixedSize.Value * n == innerSize);
            var items = new List<PropertyValue>(n);
            try
            {
                for (var i = 0; i < n; i++)
                {
                    items.Add(native ? ReadNativeStruct(r, sname, fixedSize) : ReadTaggedStruct(r, sname));
                }
            }
            catch (Exception ex) when (ex is FormatException or EndOfStreamException or NotSupportedException)
            {
                throw new FormatException($"Array<{sname}> item {items.Count}: {ex.Message}", ex);
            }

            var innerTag = new PropertyTag
            {
                Name = innerName,
                NameRef = innerNameRef,
                Type = innerType,
                Size = innerSize,
                ArrayIndex = innerIndex,
                StructName = sname,
                StructGuid = sguid,
                HasPropertyGuid = innerHasGuid,
                PropertyGuid = innerPropGuid,
                Offset = Rel(innerStart),
                ValueOffset = Rel(itemsStart),
                Value = new RawValue([]) { Offset = Rel(itemsStart), Size = 0 },
            };
            return At(new ArrayValue(inner, items) { StructName = sname, InnerTag = innerTag }, s, r);
        }

        CheckCount(n, 0, r);
        var byteIsName = inner == "ByteProperty" && n > 0 && size - 4 == 8L * n;
        var list = new List<PropertyValue>(Math.Min(n, 1 << 16));
        for (var i = 0; i < n; i++)
        {
            list.Add(ReadElement(r, inner, null, byteIsName));
        }

        return At(new ArrayValue(inner, list), s, r);
    }

    private SetValue ReadSet(ByteReader r, string inner, int size)
    {
        var s = r.Position;
        var removedCount = r.I32();
        CheckCount(removedCount, 1, r);
        var removed = new List<PropertyValue>(removedCount);
        for (var i = 0; i < removedCount; i++)
        {
            removed.Add(ReadElement(r, inner, null, false));
        }

        var n = r.I32();
        CheckCount(n, 0, r);
        var byteIsName = inner == "ByteProperty" && n > 0 && size - 8 == 8L * n;
        var items = new List<PropertyValue>(Math.Min(n, 1 << 16));
        for (var i = 0; i < n; i++)
        {
            items.Add(ReadElement(r, inner, null, byteIsName));
        }

        return At(new SetValue(inner, removed, items), s, r);
    }

    private MapValue ReadMap(ByteReader r, string keyType, string valueType, bool byteKeysAsNames)
    {
        var s = r.Position;
        var removedCount = r.I32();
        CheckCount(removedCount, 1, r);
        var removed = new List<PropertyValue>(removedCount);
        for (var i = 0; i < removedCount; i++)
        {
            removed.Add(ReadElement(r, keyType, null, byteKeysAsNames));
        }

        var n = r.I32();
        CheckCount(n, 1, r);
        var entries = new List<MapEntry>(n);
        for (var i = 0; i < n; i++)
        {
            var k = ReadElement(r, keyType, null, byteKeysAsNames);
            var v = ReadElement(r, valueType, null, byteKeysAsNames);
            entries.Add(new MapEntry(k, v));
        }

        return At(new MapValue(keyType, valueType, removed, entries), s, r);
    }
}
