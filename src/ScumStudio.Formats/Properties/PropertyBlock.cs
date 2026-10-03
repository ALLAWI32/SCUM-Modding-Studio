using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ScumStudio.Formats.Properties;

/// <summary>
/// The tagged property block at the start of an export payload. Offsets of tags and values are relative to the
/// export payload start; <see cref="ToUExpOffset"/> converts them to <c>.uexp</c> file offsets for in-place patches.
/// </summary>
public sealed class PropertyBlock
{
    /// <summary>0-based export index (-1 when read from a detached payload).</summary>
    public int ExportIndex { get; init; } = -1;

    /// <summary>Export object name.</summary>
    public string? ExportName { get; init; }

    /// <summary>Export class name.</summary>
    public string? ClassName { get; init; }

    /// <summary>Offset of the export payload in the <c>.uexp</c> (SerialOffset - TotalHeaderSize).</summary>
    public int UExpBaseOffset { get; init; }

    /// <summary>Export payload length.</summary>
    public int PayloadLength { get; init; }

    /// <summary>Offset just after the terminating <c>None</c>.</summary>
    public int EndOffset { get; init; }

    /// <summary>Bytes after the property block (bHasGuid + native data for UObjects).</summary>
    public int NativeDataLength => PayloadLength - EndOffset;

    /// <summary>Top-level tags in file order.</summary>
    public required IReadOnlyList<PropertyTag> Properties { get; init; }

    /// <summary>Converts an export-relative offset to a <c>.uexp</c> offset.</summary>
    public int ToUExpOffset(int exportRelativeOffset) => UExpBaseOffset + exportRelativeOffset;

    /// <summary>First top-level tag named <paramref name="name"/> with the given static array index.</summary>
    public PropertyTag? Find(string name, int arrayIndex = 0) =>
        Properties.FirstOrDefault(p => p.Name == name && p.ArrayIndex == arrayIndex);

    /// <summary>
    /// Every tag in the block, depth first, with a path: struct members as <c>Parent.Child</c>, array-of-struct items
    /// as <c>Parent[i].Child</c>, static array indices as <c>Name#k</c> (only when k != 0).
    /// </summary>
    public IEnumerable<(string Path, PropertyTag Tag)> EnumerateAll() => Enumerate(Properties, string.Empty);

    /// <summary>All tags (at any depth) matching a name and optionally a type — the model-based <c>tag_offsets</c>.</summary>
    public IEnumerable<PropertyTag> FindAll(string name, string? type = null) =>
        EnumerateAll().Select(x => x.Tag).Where(t => t.Name == name && (type is null || t.Type == type));

    /// <summary>The tag at a path produced by <see cref="EnumerateAll"/>, or null.</summary>
    public PropertyTag? GetByPath(string path) =>
        EnumerateAll().FirstOrDefault(x => x.Path == path).Tag;

    /// <summary>Number of values anywhere in the block that could not be decoded.</summary>
    public int CountRawValues() => CountRaw(Properties);

    /// <summary>JSON-friendly representation.</summary>
    public JsonObject ToJson() => PropertyJson.ToJson(this);

    /// <summary>Indented text dump (port of <c>props.fmt</c>).</summary>
    public string Format() => string.Join('\n', PropertyFormatter.Format(Properties, 1));

    private static IEnumerable<(string, PropertyTag)> Enumerate(IEnumerable<PropertyTag> tags, string prefix)
    {
        foreach (var t in tags)
        {
            var path = prefix + t.Name + (t.ArrayIndex != 0 ? "#" + t.ArrayIndex.ToString(CultureInfo.InvariantCulture) : string.Empty);
            yield return (path, t);
            switch (t.Value)
            {
                case StructValue sv:
                    foreach (var x in Enumerate(sv.Properties, path + "."))
                    {
                        yield return x;
                    }

                    break;
                case ArrayValue av:
                    for (var i = 0; i < av.Items.Count; i++)
                    {
                        if (av.Items[i] is StructValue item)
                        {
                            foreach (var x in Enumerate(item.Properties, $"{path}[{i}]."))
                            {
                                yield return x;
                            }
                        }
                    }

                    break;
            }
        }
    }

    private static int CountRaw(IEnumerable<PropertyTag> tags) => tags.Sum(t => CountRaw(t.Value));

    private static int CountRaw(PropertyValue v) => v switch
    {
        RawValue => 1,
        StructValue sv => CountRaw(sv.Properties),
        ArrayValue a => a.Items.Sum(CountRaw),
        SetValue s => s.Removed.Sum(CountRaw) + s.Items.Sum(CountRaw),
        MapValue m => m.RemovedKeys.Sum(CountRaw) + m.Entries.Sum(e => CountRaw(e.Key) + CountRaw(e.Value)),
        PerPlatformValue p => CountRaw(p.Default),
        NativeStructValue n => n.Fields.Sum(f => CountRaw(f.Value)),
        _ => 0,
    };
}

/// <summary>Text formatter for property lists (port of <c>props.py fmt</c>).</summary>
public static class PropertyFormatter
{
    /// <summary>Formats tags as indented lines.</summary>
    public static IReadOnlyList<string> Format(IReadOnlyList<PropertyTag> props, int indent = 1)
    {
        ArgumentNullException.ThrowIfNull(props);
        var output = new List<string>();
        Append(output, props, indent);
        return output;
    }

    private static void Append(List<string> output, IReadOnlyList<PropertyTag> props, int indent)
    {
        var pad = new string(' ', 2 * indent);
        foreach (var p in props)
        {
            var head = $"{pad}{p.Name} ({p.TypeLabel}{(p.ArrayIndex != 0 ? $"[{p.ArrayIndex}]" : string.Empty)})";
            switch (p.Value)
            {
                case StructValue sv when sv.Properties.Count > 0:
                    output.Add(head + ":");
                    Append(output, sv.Properties, indent + 1);
                    break;
                case ArrayValue { StructName: not null, InnerTag: not null } av:
                    output.Add(head + $" Array<{av.StructName}> x{av.Items.Count}:");
                    var inner = new string(' ', 2 * (indent + 1));
                    for (var i = 0; i < Math.Min(100, av.Items.Count); i++)
                    {
                        if (av.Items[i] is StructValue item)
                        {
                            output.Add($"{inner}[{i}]");
                            Append(output, item.Properties, indent + 2);
                        }
                        else
                        {
                            output.Add($"{inner}[{i}] {av.Items[i]}");
                        }
                    }

                    break;
                case ArrayValue av:
                    output.Add(head + " = [" + string.Join(", ", av.Items.Take(64)) + (av.Items.Count > 64 ? ", ..." : string.Empty) + "]");
                    break;
                default:
                    output.Add(head + " = " + p.Value);
                    break;
            }
        }
    }
}

/// <summary>Converts property blocks and values to <see cref="JsonNode"/> trees.</summary>
public static class PropertyJson
{
    /// <summary>JSON for a whole block.</summary>
    public static JsonObject ToJson(PropertyBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return new JsonObject
        {
            ["export"] = block.ExportIndex >= 0 ? block.ExportIndex + 1 : null, // 1-based, like 'pkg info' 
            ["name"] = block.ExportName,
            ["class"] = block.ClassName,
            ["uexpOffset"] = block.UExpBaseOffset,
            ["size"] = block.PayloadLength,
            ["propertiesEnd"] = block.EndOffset,
            ["nativeBytes"] = block.NativeDataLength,
            ["properties"] = TagsToJson(block.Properties),
        };
    }

    /// <summary>JSON array of tags.</summary>
    public static JsonArray TagsToJson(IEnumerable<PropertyTag> tags)
    {
        var arr = new JsonArray();
        foreach (var t in tags)
        {
            arr.Add(TagToJson(t));
        }

        return arr;
    }

    /// <summary>JSON for one tag.</summary>
    public static JsonObject TagToJson(PropertyTag t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var o = new JsonObject
        {
            ["name"] = t.Name,
            ["type"] = t.Type,
        };
        if (t.StructName is not null)
        {
            o["struct"] = t.StructName;
        }

        if (t.EnumName is not null)
        {
            o["enum"] = t.EnumName;
        }

        if (t.InnerType is not null)
        {
            o["inner"] = t.InnerType;
        }

        if (t.ValueType is not null)
        {
            o["valueType"] = t.ValueType;
        }

        if (t.ArrayIndex != 0)
        {
            o["index"] = t.ArrayIndex;
        }

        if (t.HasPropertyGuid)
        {
            o["propertyGuid"] = t.PropertyGuid?.ToString();
        }

        o["size"] = t.Size;
        o["offset"] = t.ValueOffset;
        o["value"] = ValueToJson(t.Value);
        return o;
    }

    /// <summary>JSON for one value.</summary>
    public static JsonNode? ValueToJson(PropertyValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value switch
        {
            BoolValue v => JsonValue.Create(v.Value),
            Int8Value v => JsonValue.Create(v.Value),
            Int16Value v => JsonValue.Create(v.Value),
            IntValue v => JsonValue.Create(v.Value),
            Int64Value v => JsonValue.Create(v.Value),
            ByteValue v => JsonValue.Create(v.Value),
            UInt16Value v => JsonValue.Create(v.Value),
            UInt32Value v => JsonValue.Create(v.Value),
            UInt64Value v => JsonValue.Create(v.Value),
            FloatValue v => Num(v.Value),
            DoubleValue v => double.IsFinite(v.Value) ? JsonValue.Create(v.Value) : JsonValue.Create(v.Value.ToString(CultureInfo.InvariantCulture)),
            NameValue v => JsonValue.Create(v.Value),
            StrValue v => JsonValue.Create(v.Value),
            TextValue v => new JsonObject
            {
                ["history"] = (int)v.HistoryType,
                ["namespace"] = v.Namespace,
                ["key"] = v.Key,
                ["source"] = v.SourceString,
                ["table"] = v.TableId,
            },
            ObjectValue v => new JsonObject { ["index"] = v.Index, ["ref"] = v.Reference },
            SoftObjectValue v => new JsonObject { ["assetPath"] = v.AssetPath, ["subPath"] = v.SubPath },
            EnumValue v => JsonValue.Create(v.Value),
            StructValue v => new JsonObject { ["struct"] = v.StructName, ["properties"] = TagsToJson(v.Properties) },
            VectorValue v => new JsonObject { ["x"] = Num(v.X), ["y"] = Num(v.Y), ["z"] = Num(v.Z) },
            Vector2DValue v => new JsonObject { ["x"] = Num(v.X), ["y"] = Num(v.Y) },
            Vector4Value v => new JsonObject { ["x"] = Num(v.X), ["y"] = Num(v.Y), ["z"] = Num(v.Z), ["w"] = Num(v.W) },
            RotatorValue v => new JsonObject { ["pitch"] = Num(v.Pitch), ["yaw"] = Num(v.Yaw), ["roll"] = Num(v.Roll) },
            QuatValue v => new JsonObject { ["x"] = Num(v.X), ["y"] = Num(v.Y), ["z"] = Num(v.Z), ["w"] = Num(v.W) },
            LinearColorValue v => new JsonObject { ["r"] = Num(v.R), ["g"] = Num(v.G), ["b"] = Num(v.B), ["a"] = Num(v.A) },
            ColorValue v => new JsonObject { ["r"] = v.R, ["g"] = v.G, ["b"] = v.B, ["a"] = v.A },
            GuidValue v => JsonValue.Create(v.Value.ToString()),
            IntPointValue v => new JsonObject { ["x"] = v.X, ["y"] = v.Y },
            IntVectorValue v => new JsonObject { ["x"] = v.X, ["y"] = v.Y, ["z"] = v.Z },
            BoxValue v => new JsonObject { ["min"] = ValueToJson(v.Min), ["max"] = ValueToJson(v.Max), ["isValid"] = v.IsValid },
            BoxSphereBoundsValue v => new JsonObject { ["origin"] = ValueToJson(v.Origin), ["boxExtent"] = ValueToJson(v.BoxExtent), ["sphereRadius"] = Num(v.SphereRadius) },
            TransformValue v => new JsonObject { ["rotation"] = ValueToJson(v.Rotation), ["translation"] = ValueToJson(v.Translation), ["scale3D"] = ValueToJson(v.Scale3D) },
            PerPlatformValue v => new JsonObject { ["cooked"] = v.Cooked, ["default"] = ValueToJson(v.Default) },
            NativeStructValue v => NativeToJson(v),
            ArrayValue v => ArrayToJson(v),
            SetValue v => new JsonObject { ["inner"] = v.InnerType, ["items"] = ListToJson(v.Items) },
            MapValue v => MapToJson(v),
            DelegateValue v => new JsonObject { ["object"] = v.ObjectReference, ["function"] = v.FunctionName },
            MulticastDelegateValue v => ListToJson(v.Delegates),
            FieldPathValue v => new JsonObject { ["path"] = new JsonArray(v.Path.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()), ["owner"] = v.OwnerReference },
            RawValue v => new JsonObject { ["raw"] = Convert.ToHexString(v.Bytes).ToLowerInvariant(), ["error"] = v.Error },
            _ => JsonValue.Create(value.ToString()),
        };
    }

    private static JsonNode ArrayToJson(ArrayValue v)
    {
        if (v.StructName is null)
        {
            return ListToJson(v.Items);
        }

        return new JsonObject { ["struct"] = v.StructName, ["items"] = ListToJson(v.Items) };
    }

    private static JsonObject NativeToJson(NativeStructValue v)
    {
        var fields = new JsonObject();
        foreach (var f in v.Fields)
        {
            fields[f.Name] = ValueToJson(f.Value);
        }

        return new JsonObject { ["struct"] = v.StructName, ["fields"] = fields };
    }

    private static JsonObject MapToJson(MapValue v)
    {
        var entries = new JsonArray();
        foreach (var e in v.Entries)
        {
            entries.Add(new JsonObject { ["key"] = ValueToJson(e.Key), ["value"] = ValueToJson(e.Value) });
        }

        return new JsonObject { ["keyType"] = v.KeyType, ["valueType"] = v.ValueType, ["entries"] = entries };
    }

    private static JsonArray ListToJson<T>(IEnumerable<T> items)
        where T : PropertyValue
    {
        var arr = new JsonArray();
        foreach (var i in items)
        {
            arr.Add(ValueToJson(i));
        }

        return arr;
    }

    private static JsonNode? Num(float v) =>
        float.IsFinite(v) ? JsonValue.Create(v) : JsonValue.Create(v.ToString(CultureInfo.InvariantCulture));
}
