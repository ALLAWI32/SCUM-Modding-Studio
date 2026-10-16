using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Modding.Tuning;

/// <summary>Result of <see cref="TunablePatcher.Apply"/>.</summary>
/// <param name="Bytes">The rebuilt package.</param>
/// <param name="Applied">Edits written, as (key, old value, new value).</param>
public sealed record TunablePatchResult(PackageBytes Bytes, IReadOnlyList<(string Key, string Old, string New)> Applied);

/// <summary>
/// Writes <see cref="TunableEdit"/>s into a cooked package. Numbers, bools, enums and vectors are same-size patches of
/// the value bytes (the cooked layout stays identical); top-level texts are rewritten as culture-invariant FText and the
/// export is resized (port of <c>set_display_name</c>); an object reference (<see cref="TunableReader.ObjectKind"/>) of a
/// mod's own copy gets the import of its new object (added when missing). The package is rebuilt by
/// <see cref="PackageWriter"/> and re-parsed; every edit is read back to prove it landed.
/// </summary>
public static class TunablePatcher
{
    /// <summary>
    /// Applies <paramref name="edits"/> to <paramref name="package"/>. With <paramref name="isClone"/> (a package the mod
    /// creates, such as a zone's own settings) an edit may re-point an object reference to another object path (empty for
    /// None); in a game asset that is refused, since every place that shares the asset would change with it.
    /// </summary>
    /// <exception cref="InvalidOperationException">An edit names a missing export/property, a read-only value, an invalid value, or an object reference of a shared game asset.</exception>
    public static TunablePatchResult Apply(CookedPackage package, IEnumerable<TunableEdit> edits, bool isClone = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(edits);
        var list = edits.ToList();
        var keys = TunableReader.ExportKeys(package);
        var payloads = Enumerable.Range(0, package.Exports.Count).Select(i => package.GetExportData(i).ToArray()).ToArray();
        var applied = new List<(string, string, string)>();
        PackageTables? tables = null;

        foreach (var group in list.GroupBy(e => e.Export, StringComparer.Ordinal))
        {
            var exportIndex = Array.IndexOf(keys, group.Key);
            if (exportIndex < 0)
            {
                throw new InvalidOperationException($"{package.BasePath}: no export '{group.Key}'.");
            }

            var block = package.ReadProperties(exportIndex);
            var byPath = TunableReader.EnumerateAll(package, exportIndex, block).GroupBy(x => x.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Tag, StringComparer.Ordinal);

            // Same-size edits first (offsets are those of the original payload), then text edits from the back so earlier offsets stay valid.
            var textEdits = new List<(PropertyTag Tag, TunableEdit Edit, string Old)>();
            foreach (var edit in group)
            {
                if (!byPath.TryGetValue(edit.Path, out var tag))
                {
                    throw new InvalidOperationException($"{package.BasePath}: export '{edit.Export}' has no property '{edit.Path}'.");
                }

                var old = Describe(tag);
                if (tag.Value is ObjectValue reference)
                {
                    if (!isClone)
                    {
                        throw new InvalidOperationException($"{package.BasePath}: '{edit.Path}' points at another asset and the package is shared by the game; re-point it in a copy of its own.");
                    }

                    tables ??= PackageTables.Of(package, exportIndex);
                    var index = ImportOf(package, tables with { ExportIndex = exportIndex }, reference, edit.Value.Trim());
                    BinaryPrimitives.WriteInt32LittleEndian(payloads[exportIndex].AsSpan(reference.Offset), index);
                    applied.Add((edit.Export + "|" + edit.Path, reference.Index == 0 ? string.Empty : package.GetFullPath(reference.Index), edit.Value.Trim()));
                    continue;
                }

                if (tag.Value is TextValue)
                {
                    if (edit.Path.Contains('.') || edit.Path.Contains('['))
                    {
                        throw new InvalidOperationException($"'{edit.Path}': text inside a struct cannot be resized safely.");
                    }

                    textEdits.Add((tag, edit, old));
                    continue;
                }

                WriteSameSize(package, payloads[exportIndex], tag, edit.Value);
                applied.Add((edit.Export + "|" + edit.Path, old, edit.Value));
            }

            foreach (var (tag, edit, old) in textEdits.OrderByDescending(t => t.Tag.ValueOffset))
            {
                payloads[exportIndex] = WriteText(payloads[exportIndex], tag, edit.Value);
                applied.Add((edit.Export + "|" + edit.Path, old, edit.Value));
            }
        }

        var input = PackageWriter.ToBuildInput(package) with { ExportData = payloads.Select(p => (ReadOnlyMemory<byte>)p).ToArray() };
        if (tables is not null)
        {
            input = input with
            {
                Names = tables.Names,
                NameIsWide = tables.Wide,
                Imports = tables.Imports,
                Exports = tables.Exports,
                PreloadDependencies = tables.Preload,
            };
        }

        var bytes = PackageWriter.Build(input);
        VerifyReadBack(package, bytes, list);
        return new TunablePatchResult(bytes, applied);
    }

    /// <summary>
    /// The package index <paramref name="objectPath"/> is written as: 0 for None (empty), else its import (added with the
    /// class of the import it replaces, or a Blueprint class for <c>*_C</c>).
    /// </summary>
    private static int ImportOf(CookedPackage package, PackageTables tables, ObjectValue old, string objectPath)
    {
        if (objectPath.Length == 0 || objectPath == "None")
        {
            return 0;
        }

        var dot = objectPath.LastIndexOf('.');
        if (!objectPath.StartsWith('/') || dot <= objectPath.LastIndexOf('/'))
        {
            throw new InvalidOperationException($"'{objectPath}' is not an object path (/Game/Folder/Package.Object).");
        }

        // The import's names are added whole; the engine reads a trailing _<number> as the name's number (audit C1), so the
        // game would look for another package or object.
        if (HasNameNumber(objectPath[..dot]) || HasNameNumber(objectPath[(dot + 1)..]))
        {
            throw new InvalidOperationException($"'{objectPath}' ends in _<number>, which the game reads as another name; not written.");
        }

        if (old.Index < 0)
        {
            var import = package.Imports[-old.Index - 1];
            return tables.Import(package.ResolveName(import.ClassPackage), package.ResolveName(import.ClassName), objectPath);
        }

        return objectPath.EndsWith("_C", StringComparison.Ordinal)
            ? tables.Import("/Script/Engine", "BlueprintGeneratedClass", objectPath)
            : throw new InvalidOperationException($"The class of '{objectPath}' is not known here (the reference held no import); not written.");
    }

    /// <summary>
    /// True when FName splits a number off <paramref name="name"/> (UE 4.27 <c>ParseNumber</c>): "_" then up to 10 digits,
    /// not starting with 0 unless it is the only digit, below int.MaxValue.
    /// </summary>
    private static bool HasNameNumber(string name)
    {
        var digits = name.Length - name.AsSpan().TrimEnd("0123456789").Length;
        var first = name.Length - digits;
        return digits is > 0 and <= 10 && first > 0 && name[first - 1] == '_' && (digits == 1 || name[first] != '0')
               && long.Parse(name.AsSpan(first), CultureInfo.InvariantCulture) < int.MaxValue;
    }

    private static string Describe(PropertyTag tag) => tag.Value switch
    {
        BoolValue b => b.Value ? "true" : "false",
        FloatValue f => TunableValue.Format(f.Value),
        TextValue t => t.SourceString ?? t.ToString(),
        VectorValue v => TunableValue.Format(v.X, v.Y, v.Z),
        RotatorValue r => TunableValue.Format(r.Pitch, r.Yaw, r.Roll),
        LinearColorValue c => TunableValue.Format(c.R, c.G, c.B, c.A),
        var v => v.ToString() ?? string.Empty,
    };

    private static void WriteSameSize(CookedPackage package, byte[] payload, PropertyTag tag, string text)
    {
        var value = tag.Value;
        var span = payload.AsSpan();
        try
        {
            switch (value)
            {
                case BoolValue when tag.Type == "BoolProperty":
                    span[tag.BoolValueOffset] = TunableValue.ParseBool(text) ? (byte)1 : (byte)0;
                    break;
                case FloatValue:
                    BinaryPrimitives.WriteSingleLittleEndian(span[value.Offset..], TunableValue.ParseFloat(text));
                    break;
                case DoubleValue:
                    BinaryPrimitives.WriteDoubleLittleEndian(span[value.Offset..], double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
                    break;
                case IntValue:
                    BinaryPrimitives.WriteInt32LittleEndian(span[value.Offset..], int.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture));
                    break;
                case Int8Value:
                    span[value.Offset] = unchecked((byte)sbyte.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture));
                    break;
                case Int16Value:
                    BinaryPrimitives.WriteInt16LittleEndian(span[value.Offset..], short.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture));
                    break;
                case Int64Value:
                    BinaryPrimitives.WriteInt64LittleEndian(span[value.Offset..], long.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture));
                    break;
                case ByteValue:
                    span[value.Offset] = byte.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                    break;
                case UInt16Value:
                    BinaryPrimitives.WriteUInt16LittleEndian(span[value.Offset..], ushort.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture));
                    break;
                case UInt32Value:
                    BinaryPrimitives.WriteUInt32LittleEndian(span[value.Offset..], uint.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture));
                    break;
                case UInt64Value:
                    BinaryPrimitives.WriteUInt64LittleEndian(span[value.Offset..], ulong.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture));
                    break;
                case EnumValue when tag.Size == 8:
                    var name = text.Trim();
                    var nameRef = package.Names.Contains(name, StringComparer.Ordinal)
                        ? package.FindName(name)
                        : throw new InvalidOperationException($"'{name}' is not a value this package knows (pick one of the listed choices).");
                    BinaryPrimitives.WriteInt32LittleEndian(span[value.Offset..], nameRef.Index);
                    BinaryPrimitives.WriteInt32LittleEndian(span[(value.Offset + 4)..], nameRef.Number);
                    break;
                case VectorValue or RotatorValue when tag.Size == 12:
                    WriteFloats(span[value.Offset..], TunableValue.ParseFloats(text, 3));
                    break;
                case LinearColorValue when tag.Size == 16:
                    WriteFloats(span[value.Offset..], TunableValue.ParseFloats(text, 4));
                    break;
                default:
                    throw new InvalidOperationException($"'{tag.Name}' ({tag.TypeLabel}) is not an editable value.");
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new InvalidOperationException($"'{tag.Name}': {ex.Message}", ex);
        }
    }

    private static void WriteFloats(Span<byte> target, float[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(target[(i * 4)..], values[i]);
        }
    }

    /// <summary>Culture-invariant FText: flags 0, history None (-1), bHasCultureInvariantString 1, FString.</summary>
    internal static byte[] EncodeInvariantText(string text)
    {
        var w = new List<byte>(16 + text.Length * 2);
        w.AddRange(BitConverter.GetBytes(0u));
        w.Add(unchecked((byte)(sbyte)-1));
        w.AddRange(BitConverter.GetBytes(1));
        if (text.Length == 0)
        {
            w.AddRange(BitConverter.GetBytes(0));
        }
        else if (text.All(c => c < 128))
        {
            w.AddRange(BitConverter.GetBytes(text.Length + 1));
            w.AddRange(Encoding.ASCII.GetBytes(text));
            w.Add(0);
        }
        else
        {
            w.AddRange(BitConverter.GetBytes(-(text.Length + 1)));
            w.AddRange(Encoding.Unicode.GetBytes(text));
            w.Add(0);
            w.Add(0);
        }

        return w.ToArray();
    }

    private static byte[] WriteText(byte[] payload, PropertyTag tag, string text)
    {
        var value = EncodeInvariantText(text);
        var result = new byte[payload.Length - tag.Size + value.Length];
        var span = result.AsSpan();
        payload.AsSpan(0, tag.ValueOffset).CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[tag.SizeFieldOffset..], value.Length);
        value.CopyTo(span[tag.ValueOffset..]);
        payload.AsSpan(tag.ValueOffset + tag.Size).CopyTo(span[(tag.ValueOffset + value.Length)..]);
        return result;
    }

    private static void VerifyReadBack(CookedPackage source, PackageBytes bytes, IReadOnlyList<TunableEdit> edits)
    {
        var check = CookedPackage.Parse(bytes.UAsset, bytes.UExp, source.UBulk, source.BasePath);
        var tunables = TunableReader.Read(check).ToDictionary(t => t.Key, StringComparer.Ordinal);
        var references = TunableReader.ObjectReferences(check).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            var key = edit.Export + "|" + edit.Path;
            if (references.TryGetValue(key, out var now))
            {
                if (!string.Equals(now, edit.Value.Trim() is "None" ? string.Empty : edit.Value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"{source.BasePath}: '{key}' did not read back as '{edit.Value}' after patching.");
                }

                continue;
            }

            if (!tunables.TryGetValue(key, out var t) || !TunableValue.AreEqual(t.Kind, t.Value, Normalize(t.Kind, edit.Value)))
            {
                throw new InvalidOperationException($"{source.BasePath}: '{key}' did not read back as '{edit.Value}' after patching.");
            }
        }
    }

    private static string Normalize(TunableKind kind, string value) => kind switch
    {
        TunableKind.Bool => TunableValue.ParseBool(value) ? "true" : "false",
        TunableKind.Enum => value.Trim(),
        _ => value,
    };
}
