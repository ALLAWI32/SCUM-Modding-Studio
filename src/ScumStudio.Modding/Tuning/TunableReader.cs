using System.Globalization;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Modding.Tuning;

/// <summary>Reads every <see cref="Tunable"/> stored in a cooked package.</summary>
public static class TunableReader
{
    /// <summary>Exports whose properties are engine bookkeeping, never gameplay values.</summary>
    private static readonly HashSet<string> SkippedClasses = new(StringComparer.Ordinal)
    {
        "SCS_Node", "SimpleConstructionScript", "InheritableComponentHandler", "BlueprintGeneratedClass", "Function",
        "AnimBlueprintGeneratedClass", "MetaData",
    };

    /// <summary>Internal flags that look editable but only describe cooked data.</summary>
    private static readonly HashSet<string> SkippedNames = new(StringComparer.Ordinal)
    {
        "bHasValidCookedData", "UCSSerializationIndex", "bNetAddressable", "CreationMethod", "bIsEditorOnly",
    };

    /// <summary>Every tunable of every export, in export order then property order.</summary>
    public static IReadOnlyList<Tunable> Read(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var result = new List<Tunable>();
        var keys = ExportKeys(package);
        for (var i = 0; i < package.Exports.Count; i++)
        {
            var className = package.GetExportClassName(i);
            if (SkippedClasses.Contains(className))
            {
                continue;
            }

            PropertyBlock block;
            try
            {
                block = package.ReadProperties(i);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException or NotSupportedException)
            {
                continue;
            }

            var group = FriendlyExport(keys[i]);
            foreach (var (path, tag) in EnumerateAll(package, i, block))
            {
                if (SkippedNames.Contains(tag.Name) || ToTunable(package, keys[i], className, group, path, tag) is not { } t)
                {
                    continue;
                }

                result.Add(t);
            }
        }

        return result;
    }

    /// <summary>The export's tagged values, then a DataTable's row members as <c>Rows[Row].Member</c> (see <see cref="DataTableRows"/>).</summary>
    internal static IEnumerable<(string Path, PropertyTag Tag)> EnumerateAll(CookedPackage package, int exportIndex, PropertyBlock block)
    {
        IReadOnlyList<DataTableRow> rows;
        try
        {
            rows = DataTableRows.Read(package, exportIndex);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            rows = [];
        }

        var all = block.EnumerateAll().Concat(rows.SelectMany(r => r.EnumerateAll().Select(m => (Path: DataTableRows.PathOf(r.Name, m.Path), m.Tag)))).ToList();
        return all.Concat(all.SelectMany(x => NativeFloats(x.Path, x.Tag)));
    }

    /// <summary>
    /// The float fields of natively serialized struct items (a curve's <c>Keys[i].Value</c>: SCUM's engine torque curves)
    /// as tags of their own, so they read and patch like any stored float.
    /// </summary>
    private static IEnumerable<(string Path, PropertyTag Tag)> NativeFloats(string path, PropertyTag tag)
    {
        if (tag.Value is not ArrayValue array)
        {
            yield break;
        }

        for (var i = 0; i < array.Items.Count; i++)
        {
            if (array.Items[i] is not NativeStructValue item)
            {
                continue;
            }

            foreach (var field in item.Fields.Where(f => f.Value is FloatValue))
            {
                yield return ($"{path}[{i}].{field.Name}", new PropertyTag { Name = field.Name, Type = "FloatProperty", Size = 4, ValueOffset = field.Value.Offset, Value = field.Value });
            }
        }
    }

    /// <summary>Export keys: object name, with <c>#n</c> for the n-th repeat (n ≥ 1) of the same name.</summary>
    public static string[] ExportKeys(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new string[package.Exports.Count];
        for (var i = 0; i < keys.Length; i++)
        {
            var name = package.ResolveName(package.Exports[i].ObjectName);
            var n = seen.TryGetValue(name, out var count) ? count : 0;
            seen[name] = n + 1;
            keys[i] = n == 0 ? name : name + "#" + n.ToString(CultureInfo.InvariantCulture);
        }

        return keys;
    }

    /// <summary>Readable export name: <c>Default__Weapon_RPK-74_C</c> → <c>Weapon_RPK-74 (defaults)</c>, <c>X_GEN_VARIABLE</c> → <c>X</c>.</summary>
    public static string FriendlyExport(string exportKey)
    {
        var k = exportKey;
        if (k.StartsWith("Default__", StringComparison.Ordinal))
        {
            k = k["Default__".Length..];
            if (k.EndsWith("_C", StringComparison.Ordinal))
            {
                k = k[..^2];
            }

            return k + " (defaults)";
        }

        return k.EndsWith("_GEN_VARIABLE", StringComparison.Ordinal) ? k[..^"_GEN_VARIABLE".Length] : k;
    }

    private static Tunable? ToTunable(CookedPackage package, string export, string className, string group, string path, PropertyTag tag)
    {
        var nested = path.Contains('.') || path.Contains('[');
        var parent = nested ? path[..LastSeparator(path)] : string.Empty;
        var fullGroup = parent.Length == 0 ? group : group + " › " + parent;
        Tunable Make(TunableKind kind, string value) => new(export, path, kind, value)
        {
            Name = tag.Name,
            ExportClass = className,
            Group = fullGroup,
        };

        switch (tag.Value)
        {
            case FloatValue f:
                return Make(TunableKind.Float, TunableValue.Format(f.Value));
            case DoubleValue d:
                return Make(TunableKind.Double, d.Value.ToString("R", CultureInfo.InvariantCulture));
            case IntValue v:
                return Make(TunableKind.Int, v.Value.ToString(CultureInfo.InvariantCulture));
            case Int8Value v:
                return Make(TunableKind.Int, v.Value.ToString(CultureInfo.InvariantCulture));
            case Int16Value v:
                return Make(TunableKind.Int, v.Value.ToString(CultureInfo.InvariantCulture));
            case Int64Value v:
                return Make(TunableKind.Int, v.Value.ToString(CultureInfo.InvariantCulture));
            case ByteValue v:
                return Make(TunableKind.UInt, v.Value.ToString(CultureInfo.InvariantCulture));
            case UInt16Value v:
                return Make(TunableKind.UInt, v.Value.ToString(CultureInfo.InvariantCulture));
            case UInt32Value v:
                return Make(TunableKind.UInt, v.Value.ToString(CultureInfo.InvariantCulture));
            case UInt64Value v:
                return Make(TunableKind.UInt, v.Value.ToString(CultureInfo.InvariantCulture));
            case BoolValue b when tag.Type == "BoolProperty":
                return Make(TunableKind.Bool, b.Value ? "true" : "false");
            case EnumValue e when tag.Size == 8:
                var enumType = e.EnumType ?? EnumPrefix(e.Value);
                return Make(TunableKind.Enum, e.Value) with
                {
                    EnumType = enumType,
                    Choices = EnumChoices(package, enumType, e.Value),
                };
            case TextValue t:
                var text = t.SourceString ?? (t.TableId is not null ? $"[{t.TableId}:{t.Key}]" : string.Empty);
                return Make(TunableKind.Text, text) with
                {
                    CanEdit = !nested && tag.ArrayIndex == 0,
                    ReadOnlyReason = nested ? "Text inside a struct cannot be resized safely." : null,
                };
            case VectorValue v when tag.Size == 12:
                return Make(TunableKind.Vector, TunableValue.Format(v.X, v.Y, v.Z));
            case RotatorValue r when tag.Size == 12:
                return Make(TunableKind.Rotator, TunableValue.Format(r.Pitch, r.Yaw, r.Roll));
            case LinearColorValue c when tag.Size == 16:
                return Make(TunableKind.Color, TunableValue.Format(c.R, c.G, c.B, c.A));
            default:
                return null;
        }
    }

    private static int LastSeparator(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot < 0 ? path.LastIndexOf('[') : dot;
    }

    private static string? EnumPrefix(string value)
    {
        var sep = value.IndexOf("::", StringComparison.Ordinal);
        return sep > 0 ? value[..sep] : null;
    }

    private static IReadOnlyList<string> EnumChoices(CookedPackage package, string? enumType, string current)
    {
        var prefix = (enumType ?? EnumPrefix(current)) is { } t ? t + "::" : null;
        if (prefix is null)
        {
            return [current];
        }

        var choices = package.Names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        if (!choices.Contains(current, StringComparer.Ordinal))
        {
            choices.Add(current);
        }

        choices.Sort(StringComparer.Ordinal);
        return choices;
    }
}
