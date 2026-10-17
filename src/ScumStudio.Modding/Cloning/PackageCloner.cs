using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Modding.Cloning;

/// <summary>A cloned package: header + .uexp bytes, the copied .ubulk and what changed.</summary>
/// <param name="OldPackagePath">Package that was cloned.</param>
/// <param name="NewPackagePath">Package path of the clone.</param>
/// <param name="Bytes">The rebuilt header and .uexp.</param>
/// <param name="UBulk">The source .ubulk, copied verbatim (null when the source has none).</param>
/// <param name="RenamedNames">Name-table entries whose text changed.</param>
public sealed record ClonedPackage(string OldPackagePath, string NewPackagePath, PackageBytes Bytes, byte[]? UBulk, int RenamedNames)
{
    /// <summary>Header extension of the source (<c>.uasset</c>; <c>.umap</c> is never cloned here).</summary>
    public string HeaderExtension { get; init; } = ".uasset";

    /// <summary>Names written whole, not split, because data the studio cannot decode may refer to them.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Rename-clone of one cooked package (port of <c>clone()</c> in the owner's <c>clone_vehicle.py</c>): every FName of the
/// import and export tables is remapped through a <see cref="PackageMap"/> as (text, number), the way the cooker stores it
/// (<c>CR_Improvised_Bow_20</c> is "CR_Improvised_Bow" number 21), every export payload is copied byte-for-byte except
/// the decoded FNames that must take another name (rewritten in place, same size), and the result is re-parsed to prove
/// the payloads are as intended and the primary export renamed.
/// </summary>
public static class PackageCloner
{
    /// <summary>Clones <paramref name="source"/> under <paramref name="map"/>.</summary>
    /// <param name="source">Pristine package (header + .uexp, optional .ubulk).</param>
    /// <param name="oldPackagePath">The source's package path (key in <paramref name="map"/>).</param>
    /// <param name="map">Rename map; must map <paramref name="oldPackagePath"/>.</param>
    /// <remarks>
    /// A name-table entry whose header references all keep their number is renamed in place, so the payloads' FName
    /// references to it follow. One whose number must change (a numbered template leaf, a redirect to <c>Mesh_1</c>) keeps
    /// its text and the header references point at the split new name instead; a payload reference to it is rewritten,
    /// followed or refused as <see cref="FollowPayloadNames"/> says.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="oldPackagePath"/> is not in the map.</exception>
    /// <exception cref="InvalidOperationException">
    /// The remapped name table has duplicates, undecoded payload data refers to a name whose number must change, or
    /// verification failed.
    /// </exception>
    public static ClonedPackage Clone(CookedPackage source, string oldPackagePath, PackageMap map)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(map);
        var newPackagePath = map.Map(oldPackagePath) ?? throw new ArgumentException($"{oldPackagePath} is not in the package map.", nameof(oldPackagePath));

        var input = PackageWriter.ToBuildInput(source);
        var old = input.Names;
        var refs = input.Imports.SelectMany(im => new[] { im.ClassPackage, im.ClassName, im.ObjectName })
            .Concat(input.Exports.Select(e => e.ObjectName))
            .Distinct()
            .ToList();
        var wanted = refs.ToDictionary(r => r, r => map.RemapFName(old[r.Index], r.Number));
        var byIndex = refs.ToLookup(r => r.Index);

        var names = new List<string>(old.Count);
        for (var i = 0; i < old.Count; i++)
        {
            var own = byIndex[i].ToList();
            if (own.Count == 0)
            {
                var text = map.RemapName(old[i]);
                names.Add(FNameRef.Split(text).Number == 0 ? text : old[i]);
                continue;
            }

            var first = wanted[own[0]].Text;
            names.Add(own.All(r => wanted[r].Number == r.Number && string.Equals(wanted[r].Text, first, StringComparison.Ordinal)) ? first : old[i]);
        }

        var warnings = new List<string>();
        var payloads = FollowPayloadNames(source, oldPackagePath, map, names, byIndex, warnings);
        var duplicate = names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Cloning {oldPackagePath} as {newPackagePath} would give the name '{duplicate.Key}' twice in the name table; choose another name.");
        }

        var renamed = Enumerable.Range(0, old.Count).Count(i => !string.Equals(names[i], old[i], StringComparison.Ordinal));
        var remap = refs.ToDictionary(r => r, r => wanted[r] == (names[r.Index], r.Number) ? r : new FNameRef(GetOrAdd(names, wanted[r].Text), wanted[r].Number));
        var wide = names.Select((n, i) => !IsAscii(n) && (i >= old.Count || (input.NameIsWide is { } flags && i < flags.Count && flags[i]))).ToArray();
        var bytes = PackageWriter.Build(input with
        {
            Names = names,
            NameIsWide = wide,
            NameHashOverride = null,
            Imports = input.Imports.Select(im => im with { ClassPackage = remap[im.ClassPackage], ClassName = remap[im.ClassName], ObjectName = remap[im.ObjectName] }).ToArray(),
            Exports = input.Exports.Select(e => e with { ObjectName = remap[e.ObjectName] }).ToArray(),
            ExportData = payloads,
        });
        Verify(source, bytes, newPackagePath, PackageMap.Leaf(PackageMap.Normalize(oldPackagePath)), payloads);
        return new ClonedPackage(PackageMap.Normalize(oldPackagePath), newPackagePath, bytes, source.UBulk, renamed + names.Count - old.Count)
        {
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Re-parses <paramref name="bytes"/> and checks every export payload equals the source's (or
    /// <paramref name="payloads"/>, the source's with FNames rewritten) and, when <paramref name="oldLeaf"/> is given, that
    /// no outermost export is still named after it.
    /// </summary>
    internal static void Verify(CookedPackage source, PackageBytes bytes, string packagePath, string? oldLeaf = null, IReadOnlyList<ReadOnlyMemory<byte>>? payloads = null)
    {
        var check = CookedPackage.Parse(bytes.UAsset, bytes.UExp, null, packagePath);
        if (check.Exports.Count != source.Exports.Count || check.Imports.Count != source.Imports.Count)
        {
            throw new InvalidOperationException($"{packagePath}: export/import count changed while cloning.");
        }

        for (var i = 0; i < source.Exports.Count; i++)
        {
            // A texture that moved carries its skip offsets along (TextureSkipOffsets): the same payload at its new place.
            var expected = payloads?[i] ?? source.GetExportData(i);
            var actual = check.GetExportData(i).Span;
            if (!actual.SequenceEqual(expected.Span)
                && !actual.SequenceEqual(TextureSkipOffsets.Relocate(expected, check.Names, source.Exports[i].SerialOffset, check.Exports[i].SerialOffset).Span))
            {
                throw new InvalidOperationException($"{packagePath}: export {i + 1} payload changed while cloning.");
            }
        }

        var newLeaf = PackageMap.Leaf(PackageMap.Normalize(packagePath));
        if (oldLeaf is not null && !string.Equals(oldLeaf, newLeaf, StringComparison.OrdinalIgnoreCase)
            && check.Exports.FirstOrDefault(e => e.OuterIndex == 0 && string.Equals(e.ObjectName.Format(check.Names), oldLeaf, StringComparison.OrdinalIgnoreCase)) is not null)
        {
            throw new InvalidOperationException($"{packagePath}: the export {oldLeaf} was not renamed to {newLeaf} while cloning.");
        }
    }

    /// <summary>
    /// Keeps every payload FName (index, number) reading as its rename and returns the payloads to write. An FName the
    /// property reader decodes (a tag's name or type, a NameProperty, a soft path's asset path, an enum value, a delegate's
    /// function) that would read differently is rewritten in place with the split name, the cooker's form. Bytes it cannot
    /// decode (an export's native tail, an unknown struct) are searched for the raw pair, numbers 0, the header's and the
    /// map's numbered keys': an entry kept under its old text whose pair is found there takes the whole new text in place,
    /// as clones were written before the number rule, with a warning; a pair that still reads differently after that
    /// (<c>Bow_20</c> to <c>CR_SS_Y</c>) refuses the clone. A pair inside a decoded non-name value (an int and a zero) is
    /// chance and left alone; one found by chance in undecoded bytes only refuses a clone or keeps an unused name.
    /// </summary>
    private static ReadOnlyMemory<byte>[] FollowPayloadNames(
        CookedPackage source, string oldPackagePath, PackageMap map, List<string> names, ILookup<int, FNameRef> header, List<string> warnings)
    {
        var old = source.Names;
        var layouts = Enumerable.Range(0, source.Exports.Count).Select(e => NameLayout.Read(source, e)).ToArray();
        bool Reads(string text, int index, int number) =>
            string.Equals(new FNameRef(0, number).Format([text]), Display(map.RemapFName(old[index], number)), StringComparison.Ordinal);

        var numbers = map.Objects.Keys.Concat(map.Packages.Keys).Select(k => FNameRef.Split(k).Number).Append(0).ToHashSet();
        for (var i = 0; i < old.Count; i++)
        {
            var index = i;
            var tried = numbers.Concat(header[i].Select(r => r.Number)).Distinct().ToList();
            bool Wrong(string text, int number) => !Reads(text, index, number) && layouts.Any(l => l.HoldsUndecoded(index, number));
            if (!tried.Any(n => Wrong(names[index], n)))
            {
                continue;
            }

            var whole = map.RemapName(old[i]);
            var refused = tried.FirstOrDefault(n => Wrong(whole, n), -1);
            if (refused >= 0)
            {
                throw new InvalidOperationException($"{oldPackagePath}: a payload refers to {new FNameRef(i, refused).Format(old)}; the clone cannot rename it.");
            }

            names[i] = whole;
            warnings.Add($"{oldPackagePath}: data the studio cannot decode may refer to {old[i]}, so it is renamed to '{whole}' as one name with no number, as before the FName number rule.");
        }

        var payloads = new ReadOnlyMemory<byte>[layouts.Length];
        for (var e = 0; e < layouts.Length; e++)
        {
            var data = source.GetExportData(e);
            byte[]? copy = null;
            foreach (var slot in layouts[e].Slots.Where(s => s >= 0 && s + 8 <= data.Length))
            {
                var span = data.Span[slot..];
                var name = new FNameRef(BinaryPrimitives.ReadInt32LittleEndian(span), BinaryPrimitives.ReadInt32LittleEndian(span[4..]));
                if (name.Index < 0 || name.Index >= old.Count || name.Number < 0 || Reads(names[name.Index], name.Index, name.Number))
                {
                    continue;
                }

                var (text, number) = map.RemapFName(old[name.Index], name.Number);
                copy ??= data.ToArray();
                PackagePatcher.WriteFName(copy, slot, new FNameRef(GetOrAdd(names, text), number));
            }

            payloads[e] = copy ?? data;
        }

        return payloads;
    }

    private static string Display((string Text, int Number) name) => new FNameRef(0, name.Number).Format([name.Text]);

    private static int GetOrAdd(List<string> names, string text)
    {
        var index = names.FindIndex(n => string.Equals(n, text, StringComparison.Ordinal));
        if (index < 0)
        {
            index = names.Count;
            names.Add(text);
        }

        return index;
    }

    private static bool IsAscii(string s)
    {
        foreach (var c in s)
        {
            if (c > 127)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Where one export's payload holds FNames: the slots the property reader decoded, and the bytes it could not decode.</summary>
    private sealed class NameLayout
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly List<(int Start, int End)> _undecoded = [];

        private NameLayout(ReadOnlyMemory<byte> data) => _data = data;

        /// <summary>Export-relative offsets of decoded FNames.</summary>
        public SortedSet<int> Slots { get; } = new();

        public static NameLayout Read(CookedPackage package, int export)
        {
            var layout = new NameLayout(package.GetExportData(export));
            try
            {
                var block = PropertyReader.ReadExport(package, export);
                layout.AddTags(block.Properties);
                layout._undecoded.Add((block.EndOffset, layout._data.Length));
            }
            catch (Exception ex) when (ex is FormatException or EndOfStreamException or NotSupportedException or ArgumentOutOfRangeException)
            {
                layout.Slots.Clear();
                layout._undecoded.Clear();
                layout._undecoded.Add((0, layout._data.Length));
            }

            return layout;
        }

        /// <summary>True when the raw pair (index, number) overlaps bytes the reader could not decode.</summary>
        public bool HoldsUndecoded(int index, int number)
        {
            Span<byte> pair = stackalloc byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(pair, index);
            BinaryPrimitives.WriteInt32LittleEndian(pair[4..], number);
            var data = _data.Span;
            for (var from = 0; from < data.Length;)
            {
                var k = data[from..].IndexOf(pair);
                if (k < 0)
                {
                    return false;
                }

                var at = from + k;
                if (!Slots.Contains(at) && _undecoded.Any(r => at < r.End && at + 8 > r.Start))
                {
                    return true;
                }

                from = at + 1;
            }

            return false;
        }

        private void AddTags(IEnumerable<PropertyTag> tags)
        {
            foreach (var tag in tags)
            {
                AddTagNames(tag);
                AddValue(tag.Value);
            }
        }

        // Name and type, then the struct, enum or inner type name at +24 and a map's value type at +32.
        private void AddTagNames(PropertyTag tag)
        {
            Slots.Add(tag.Offset);
            Slots.Add(tag.Offset + 8);
            if (tag.StructName is not null || tag.EnumName is not null || tag.InnerType is not null)
            {
                Slots.Add(tag.Offset + 24);
            }

            if (tag.ValueType is not null)
            {
                Slots.Add(tag.Offset + 32);
            }
        }

        private void AddValue(PropertyValue value)
        {
            switch (value)
            {
                case NameValue or SoftObjectValue or EnumValue:
                    Slots.Add(value.Offset);
                    break;
                case DelegateValue:
                    Slots.Add(value.Offset + 4);
                    break;
                case FieldPathValue path:
                    for (var k = 0; k < path.Path.Count; k++)
                    {
                        Slots.Add(value.Offset + 4 + (8 * k));
                    }

                    break;
                case MulticastDelegateValue multicast:
                    AddValues(multicast.Delegates);
                    break;
                case StructValue s:
                    AddTags(s.Properties);
                    break;
                case NativeStructValue native:
                    AddValues(native.Fields.Select(f => f.Value));
                    break;
                case PerPlatformValue perPlatform:
                    AddValue(perPlatform.Default);
                    break;
                case ArrayValue array:
                    if (array.InnerTag is { } inner)
                    {
                        AddTagNames(inner);
                    }

                    AddValues(array.Items);
                    break;
                case SetValue set:
                    AddValues(set.Removed.Concat(set.Items));
                    break;
                case MapValue entries:
                    AddValues(entries.RemovedKeys.Concat(entries.Entries.SelectMany(m => new[] { m.Key, m.Value })));
                    break;
                case RawValue or TextValue:
                    _undecoded.Add((value.Offset, value.Offset + value.Size));
                    break;
            }
        }

        private void AddValues(IEnumerable<PropertyValue> values)
        {
            foreach (var value in values)
            {
                AddValue(value);
            }
        }
    }
}
