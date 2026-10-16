using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Modding.Tuning;

/// <summary>
/// Values a Blueprint takes from its parent class without storing them. A cooked class default object (CDO) holds only
/// what differs from its parent, so a katana's entity setup (<c>2H_Katana_ES</c>, parent <c>/Script/SCUM.ItemEntitySetup</c>)
/// has no <c>MaxHealth</c>, and <c>2H_Katana1</c> has none of the values it shares with its parent <c>2H_Katana</c>
/// (CwHedgehog: "I want to change this weapon's MaxHealth. It seems to be inherited from a parent?"). Such a value is
/// written as a new tag of the parent's type into the package's own CDO; the parent and its other children stay as they are.
/// </summary>
public static class InheritedValues
{
    /// <summary>
    /// Values of the game's C++ classes that no file of a child stores, with the type the game files prove: 61 entity
    /// setups whose parent is <c>ItemEntitySetup</c> store <c>MaxHealth</c> as a FloatProperty (an oven 4500, a chest 1500),
    /// no weapon's does. The C++ default is not in any file, so its value is shown as unknown.
    /// </summary>
    public static IReadOnlyList<(string Class, string Name, TunableKind Kind)> Native { get; } =
    [
        ("/Script/SCUM.ItemEntitySetup", "MaxHealth", TunableKind.Float),
    ];

    /// <summary>The tag type written for each kind; other kinds are not offered.</summary>
    private static readonly Dictionary<TunableKind, string> TagTypes = new()
    {
        [TunableKind.Float] = "FloatProperty",
        [TunableKind.Double] = "DoubleProperty",
        [TunableKind.Int] = "IntProperty",
        [TunableKind.Bool] = "BoolProperty",
    };

    /// <summary>
    /// The values <paramref name="package"/>'s class default object inherits without storing them: each plain number or
    /// bool a Blueprint parent's CDO stores (nearest parent first, with its value), then the <see cref="Native"/> values of
    /// the first C++ class in the chain (value empty: not in the files). Each is a <see cref="Tunable"/> of the package's
    /// own CDO with <see cref="Tunable.InheritedFrom"/> set; empty when the package has no Blueprint class.
    /// </summary>
    /// <param name="package">A Blueprint package (an item, its entity setup, a clone of either).</param>
    /// <param name="readPackage">Reads a parent package by its package path, or returns null when it cannot.</param>
    public static IReadOnlyList<Tunable> Read(CookedPackage package, Func<string, CookedPackage?> readPackage)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(readPackage);
        if (FindClass(package) is not { } own)
        {
            return [];
        }

        var keys = TunableReader.ExportKeys(package);
        var export = keys[own.Cdo];
        var className = package.ResolveName(package.Exports[own.Class].ObjectName);
        var group = TunableReader.FriendlyExport(export);
        var seen = new HashSet<string>(StoredNames(package, own.Cdo), StringComparer.OrdinalIgnoreCase);
        var result = new List<Tunable>();
        Tunable Make(string name, TunableKind kind, string value, string parent) => new(export, name, kind, value)
        {
            Name = name,
            ExportClass = className,
            Group = group,
            InheritedFrom = parent,
        };

        var super = package.GetFullPath(package.Exports[own.Class].SuperIndex);
        for (var depth = 0; depth < 16 && super.Length > 0; depth++)
        {
            if (super.StartsWith("/Script/", StringComparison.Ordinal))
            {
                result.AddRange(Native.Where(n => n.Class == super && seen.Add(n.Name)).Select(n => Make(n.Name, n.Kind, string.Empty, super)));
                break;
            }

            var dot = super.LastIndexOf('.');
            if (dot <= 0 || readPackage(super[..dot]) is not { } parent || FindClass(parent, super[(dot + 1)..]) is not { } theirs)
            {
                break;
            }

            foreach (var tag in parent.ReadProperties(theirs.Cdo).Properties)
            {
                if (tag.ArrayIndex == 0 && KindOf(tag) is { } kind && seen.Add(tag.Name))
                {
                    result.Add(Make(tag.Name, kind, Describe(tag), super));
                }
            }

            super = parent.GetFullPath(parent.Exports[theirs.Class].SuperIndex);
        }

        return result;
    }

    /// <summary>
    /// Writes each inherited value into <paramref name="package"/>'s own CDO: a new tag of the value's kind (a name the
    /// package lacks is added to its name table), placed before the property list's end, then read back.
    /// </summary>
    /// <param name="package">The package (an item or entity setup, stock or a clone).</param>
    /// <param name="values">Each value (from <see cref="Read"/>) and its new text.</param>
    /// <exception cref="InvalidOperationException">A value is not inherited, of a kind that cannot be written, already stored, invalid, or did not read back.</exception>
    public static PackageBytes Add(CookedPackage package, IReadOnlyList<(Tunable Value, string Text)> values)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(values);
        var keys = TunableReader.ExportKeys(package);
        var names = package.Names.ToList();
        var wide = package.NameEntries.Select(n => n.IsWide).ToList();
        FNameRef Name(string name)
        {
            // As the cooker stores it: "Value_2" is the name "Value" with number 3 (FNameRef.Split).
            var (text, number) = FNameRef.Split(name);
            var index = names.IndexOf(text);
            if (index < 0)
            {
                index = names.Count;
                names.Add(text);
                wide.Add(!text.All(char.IsAscii));
            }

            return new FNameRef(index, number);
        }

        var payloads = Enumerable.Range(0, package.Exports.Count).Select(i => package.GetExportData(i).ToArray()).ToArray();
        foreach (var group in values.GroupBy(v => v.Value.Export, StringComparer.Ordinal))
        {
            var exportIndex = Array.IndexOf(keys, group.Key);
            if (exportIndex < 0 || !group.Key.StartsWith("Default__", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{package.BasePath}: '{group.Key}' is not this package's class defaults.");
            }

            var block = package.ReadProperties(exportIndex);
            var none = block.EndOffset - 8;
            if (none < 0 || BinaryPrimitives.ReadInt32LittleEndian(payloads[exportIndex].AsSpan(none)) != package.FindName("None").Index)
            {
                throw new InvalidOperationException($"{package.BasePath}: the property list of '{group.Key}' does not end as expected.");
            }

            var tags = new List<byte>();
            foreach (var (value, text) in group)
            {
                if (value.InheritedFrom is null || !TagTypes.TryGetValue(value.Kind, out var type) || value.Path.Contains('.') || value.Path.Contains('['))
                {
                    throw new InvalidOperationException($"{package.BasePath}: '{value.Path}' is not an inherited value that can be written.");
                }

                if (block.Find(value.Path) is not null)
                {
                    throw new InvalidOperationException($"{package.BasePath}: '{value.Path}' is stored already; change it as a stored value.");
                }

                tags.AddRange(Tag(Name(value.Path), Name(type), value.Kind, text));
            }

            var payload = payloads[exportIndex];
            var grown = new byte[payload.Length + tags.Count];
            payload.AsSpan(0, none).CopyTo(grown);
            tags.CopyTo(grown, none);
            payload.AsSpan(none).CopyTo(grown.AsSpan(none + tags.Count));
            payloads[exportIndex] = grown;
        }

        var bytes = PackageWriter.Build(PackageWriter.ToBuildInput(package) with
        {
            Names = names,
            NameIsWide = wide,
            ExportData = payloads.Select(p => (ReadOnlyMemory<byte>)p).ToArray(),
        });

        var check = TunableReader.Read(CookedPackage.Parse(bytes.UAsset, bytes.UExp, package.UBulk, package.BasePath)).ToDictionary(t => t.Key, StringComparer.Ordinal);
        foreach (var (value, text) in values)
        {
            if (!check.TryGetValue(value.Key, out var written) || !TunableValue.AreEqual(value.Kind, written.Value, text))
            {
                throw new InvalidOperationException($"{package.BasePath}: '{value.Key}' did not read back as '{text}' after it was added.");
            }
        }

        return bytes;
    }

    /// <summary>The package's Blueprint class export and its CDO (<c>Default__X_C</c>), or null.</summary>
    private static (int Class, int Cdo)? FindClass(CookedPackage package, string? className = null)
    {
        for (var i = 0; i < package.Exports.Count; i++)
        {
            var name = package.ResolveName(package.Exports[i].ObjectName);
            if (package.Exports[i].OuterIndex != 0 || !package.GetExportClassName(i).EndsWith("GeneratedClass", StringComparison.Ordinal)
                || (className is not null && !string.Equals(name, className, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var cdo = Enumerable.Range(0, package.Exports.Count).FirstOrDefault(j => package.ResolveName(package.Exports[j].ObjectName) == "Default__" + name, -1);
            return cdo < 0 ? null : (i, cdo);
        }

        return null;
    }

    private static IEnumerable<string> StoredNames(CookedPackage package, int exportIndex)
    {
        try
        {
            return package.ReadProperties(exportIndex).Properties.Select(p => p.Name).ToList();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException or NotSupportedException)
        {
            return [];
        }
    }

    private static TunableKind? KindOf(PropertyTag tag) => tag.Type switch
    {
        "FloatProperty" => TunableKind.Float,
        "DoubleProperty" => TunableKind.Double,
        "IntProperty" => TunableKind.Int,
        "BoolProperty" => TunableKind.Bool,
        _ => null,
    };

    private static string Describe(PropertyTag tag) => tag.Value switch
    {
        FloatValue f => TunableValue.Format(f.Value),
        DoubleValue d => d.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        IntValue v => v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        BoolValue b => b.Value ? "true" : "false",
        var v => v.ToString() ?? string.Empty,
    };

    /// <summary>One FPropertyTag and its value (UE 4.27: name, type, size, array index, [bool], no property guid, value).</summary>
    private static byte[] Tag(FNameRef name, FNameRef type, TunableKind kind, string text)
    {
        byte[] value;
        byte? flag = null;
        try
        {
            switch (kind)
            {
                case TunableKind.Float:
                    value = new byte[4];
                    BinaryPrimitives.WriteSingleLittleEndian(value, TunableValue.ParseFloat(text));
                    break;
                case TunableKind.Double:
                    value = new byte[8];
                    BinaryPrimitives.WriteDoubleLittleEndian(value, double.Parse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case TunableKind.Int:
                    value = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(value, int.Parse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    value = [];
                    flag = TunableValue.ParseBool(text) ? (byte)1 : (byte)0;
                    break;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new InvalidOperationException($"'{text}' is not a value of {kind}: {ex.Message}", ex);
        }

        var tag = new byte[16 + 8 + (flag is null ? 0 : 1) + 1 + value.Length];
        var span = tag.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, name.Index);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], name.Number);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], type.Index);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], type.Number);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], value.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], 0);
        var at = 24;
        if (flag is { } b)
        {
            tag[at++] = b;
        }

        tag[at++] = 0; // no property guid
        value.CopyTo(tag, at);
        return tag;
    }
}
