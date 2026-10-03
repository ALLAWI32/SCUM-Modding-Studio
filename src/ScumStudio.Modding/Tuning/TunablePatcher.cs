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
/// export is resized (port of <c>set_display_name</c>). The package is rebuilt by <see cref="PackageWriter"/> and
/// re-parsed; every edit is read back to prove it landed.
/// </summary>
public static class TunablePatcher
{
    /// <summary>Applies <paramref name="edits"/> to <paramref name="package"/>.</summary>
    /// <exception cref="InvalidOperationException">An edit names a missing export/property, a read-only value, or an invalid value.</exception>
    public static TunablePatchResult Apply(CookedPackage package, IEnumerable<TunableEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(edits);
        var list = edits.ToList();
        var keys = TunableReader.ExportKeys(package);
        var payloads = Enumerable.Range(0, package.Exports.Count).Select(i => package.GetExportData(i).ToArray()).ToArray();
        var applied = new List<(string, string, string)>();

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
        var bytes = PackageWriter.Build(input);
        VerifyReadBack(package, bytes, list);
        return new TunablePatchResult(bytes, applied);
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
        foreach (var edit in edits)
        {
            var key = edit.Export + "|" + edit.Path;
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
