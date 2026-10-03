using System.Buffers.Binary;
using System.Text.RegularExpressions;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Modding.Tuning;

/// <summary>
/// Material instance parameters by name. An edit path <c>VectorParameterValues[Base Color A].ParameterValue</c> sets that
/// parameter and adds it when the instance leaves it to its parent: SCUM's vehicle armour material (<c>MI_WW_Armor</c>)
/// never sets a paint colour or a colour mask, so it can only be painted by giving it those. A new entry is a copy of a
/// sibling entry of the same array (same tag layout, same package names) with the name, the value and the parent
/// material's expression guid put in. A texture the instance does not import yet is added to its imports (and to the
/// export's create-before-serialize dependencies, as the cooker lists them). An empty value means "not set" and changes nothing.
/// </summary>
public static partial class MaterialParameters
{
    /// <summary>The edit path of parameter <paramref name="name"/> in <paramref name="array"/> (e.g. <c>VectorParameterValues</c>).</summary>
    public static string PathOf(string array, string name) => $"{array}[{name}].ParameterValue";

    /// <summary>True for a by-name parameter path (as opposed to a stored value's <c>VectorParameterValues[0].ParameterValue</c>).</summary>
    public static bool IsKeyed(string path) => KeyedPath().IsMatch(path);

    /// <summary>Applies by-name parameter edits to <paramref name="package"/>.</summary>
    /// <param name="package">Material instance package.</param>
    /// <param name="edits">Edits with by-name paths (<see cref="PathOf"/>).</param>
    /// <param name="guids">The parent material's expression guid of each parameter name (16 bytes), or null for none.</param>
    /// <exception cref="InvalidOperationException">An export, array or texture import is missing, or a value does not parse.</exception>
    public static PackageBytes Apply(CookedPackage package, IReadOnlyList<TunableEdit> edits, IReadOnlyDictionary<string, byte[]>? guids = null)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(edits);
        var current = package;
        var bytes = PackageWriter.Rebuild(package);
        foreach (var edit in edits.Where(e => e.Value.Length > 0))
        {
            var match = KeyedPath().Match(edit.Path);
            if (!match.Success)
            {
                throw new InvalidOperationException($"'{edit.Path}' is not a parameter path.");
            }

            var exportIndex = Array.IndexOf(TunableReader.ExportKeys(current), edit.Export);
            if (exportIndex < 0)
            {
                throw new InvalidOperationException($"{package.BasePath}: no export '{edit.Export}'.");
            }

            // One edit at a time on a re-parsed package: offsets and the name table are always those of the current bytes.
            var names = current.Names.ToList();
            var wide = current.NameEntries.Select(n => n.IsWide).ToList();
            var tables = new Tables(names, wide, current.Imports.ToList(), current.Exports.ToList(), current.ReadPreloadDependencies().ToList(), exportIndex);
            var payloads = Enumerable.Range(0, current.Exports.Count).Select(i => current.GetExportData(i).ToArray()).ToArray();
            var name = match.Groups["name"].Value;
            payloads[exportIndex] = Set(current, payloads[exportIndex], exportIndex, match.Groups["array"].Value, name, edit.Value,
                guids is not null && guids.TryGetValue(name, out var guid) ? guid : null, tables);
            bytes = PackageWriter.Build(PackageWriter.ToBuildInput(current) with
            {
                Names = names,
                NameIsWide = wide,
                Imports = tables.Imports,
                Exports = tables.Exports,
                PreloadDependencies = tables.Preload,
                ExportData = payloads.Select(p => (ReadOnlyMemory<byte>)p).ToArray(),
            });
            current = CookedPackage.Parse(bytes.UAsset, bytes.UExp, package.UBulk, package.BasePath);
        }

        return bytes;
    }

    /// <summary>
    /// The expression guid of every parameter of a cooked <c>Material</c> (its <c>CachedExpressionData</c>), by name; empty
    /// when the package has no material or its cached data cannot be read.
    /// </summary>
    public static IReadOnlyDictionary<string, byte[]> ExpressionGuids(CookedPackage material)
    {
        ArgumentNullException.ThrowIfNull(material);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var i = 0; i < material.Exports.Count; i++)
        {
            if (material.GetExportClassName(i) != "Material")
            {
                continue;
            }

            var payload = material.GetExportData(i).Span;
            var block = material.ReadProperties(i);
            if (block.Find("CachedExpressionData")?.Value is not StructValue cached || Member(cached, "Parameters")?.Value is not StructValue parameters)
            {
                continue;
            }

            foreach (var entry in parameters.Properties.Where(p => p.Name == "RuntimeEntries").Select(p => p.Value).OfType<StructValue>())
            {
                if (Member(entry, "ParameterInfos")?.Value is ArrayValue infos && Member(entry, "ExpressionGuids")?.Value is ArrayValue ids && infos.Items.Count == ids.Items.Count)
                {
                    for (var k = 0; k < infos.Items.Count; k++)
                    {
                        if (infos.Items[k] is StructValue info && Member(info, "Name")?.Value is NameValue n && ids.Items[k].Size == 16)
                        {
                            result.TryAdd(n.Value, payload.Slice(ids.Items[k].Offset, 16).ToArray());
                        }
                    }
                }
            }
        }

        return result;
    }

    /// <summary>The package path of the material instance's parent (<c>Parent</c> import), or null.</summary>
    public static string? ParentPackage(CookedPackage instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (instance.Exports.Count == 0 || instance.ReadProperties(0).Find("Parent")?.Value is not ObjectValue { Index: < 0 } parent)
        {
            return null;
        }

        var import = instance.Imports[-parent.Index - 1];
        return import.OuterIndex < 0 ? instance.ResolveName(instance.Imports[-import.OuterIndex - 1].ObjectName) : null;
    }

    private static byte[] Set(CookedPackage package, byte[] payload, int exportIndex, string array, string name, string value, byte[]? guid, Tables tables)
    {
        var block = PropertyReader.ReadPayload(package, payload, exportIndex);
        if (block.Find(array) is not { Value: ArrayValue { InnerTag: { } inner, Items.Count: > 0 } list } tag)
        {
            throw new InvalidOperationException($"{package.BasePath}: no {array} entry to model a new one on.");
        }

        var items = list.Items.OfType<StructValue>().ToList();
        var existing = items.FirstOrDefault(i => NameOf(i) == name);
        if (existing is not null)
        {
            var copy = (byte[])payload.Clone();
            WriteValue(copy, Member(existing, "ParameterValue")!, array, value, tables);
            return copy;
        }

        var sibling = items[0];
        var entry = payload.AsSpan(sibling.Offset, sibling.Size).ToArray();
        var nameTag = Member((StructValue)Member(sibling, "ParameterInfo")!.Value, "Name")!;
        var nameRef = AddName(tables.Names, tables.Wide, name);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(nameTag.ValueOffset - sibling.Offset), nameRef.Index);
        BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(nameTag.ValueOffset - sibling.Offset + 4), nameRef.Number);
        WriteValue(entry, Member(sibling, "ParameterValue")!, array, value, tables, -sibling.Offset);
        if (Member(sibling, "ExpressionGUID") is { Size: 16 } guidTag)
        {
            (guid ?? new byte[16]).CopyTo(entry, guidTag.ValueOffset - sibling.Offset);
        }

        // Append after the last entry: the array's size, count and inner tag size grow by one entry.
        var at = tag.EndOffset;
        var result = new byte[payload.Length + entry.Length];
        payload.AsSpan(0, at).CopyTo(result);
        entry.CopyTo(result, at);
        payload.AsSpan(at).CopyTo(result.AsSpan(at + entry.Length));
        Add(result, tag.SizeFieldOffset, entry.Length);
        Add(result, tag.ValueOffset, 1);
        Add(result, inner.SizeFieldOffset, entry.Length);
        return result;
    }

    private static void WriteValue(byte[] target, PropertyTag tag, string array, string value, Tables tables, int shift = 0)
    {
        var at = tag.ValueOffset + shift;
        try
        {
            switch (array)
            {
                case "ScalarParameterValues" when tag.Type == "FloatProperty":
                    BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(at), TunableValue.ParseFloat(value));
                    break;
                case "VectorParameterValues" when tag.Size == 16:
                    var c = TunableValue.ParseFloats(value, 4);
                    for (var i = 0; i < 4; i++)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(target.AsSpan(at + (i * 4)), c[i]);
                    }

                    break;
                case "TextureParameterValues" when tag.Type == "ObjectProperty":
                    BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(at), TextureImport(tables, value));
                    break;
                default:
                    throw new InvalidOperationException($"{array}: unexpected value type {tag.TypeLabel}.");
            }
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{array}: '{value}' is not a value ({ex.Message}).", ex);
        }
    }

    /// <summary>The package's name, import, export and preload tables while one edit is made.</summary>
    private sealed record Tables(List<string> Names, List<bool> Wide, List<ImportEntry> Imports, List<ExportEntry> Exports, List<int> Preload, int ExportIndex);

    /// <summary>
    /// The import index of texture <paramref name="objectPath"/> (<c>/Game/…/T_X.T_X</c>), added (package and Texture2D
    /// imports, plus a create-before-serialize dependency of the edited export) when the package does not import it yet.
    /// </summary>
    private static int TextureImport(Tables t, string objectPath)
    {
        var dot = objectPath.LastIndexOf('.');
        var packagePath = dot > 0 ? objectPath[..dot] : objectPath;
        var objectName = dot > 0 ? objectPath[(dot + 1)..] : objectPath[(objectPath.LastIndexOf('/') + 1)..];
        bool Is(FNameRef n, string text) => string.Equals(n.Format(t.Names), text, StringComparison.OrdinalIgnoreCase);
        var package = t.Imports.FindIndex(im => im.OuterIndex == 0 && Is(im.ClassName, "Package") && Is(im.ObjectName, packagePath));
        if (package >= 0 && t.Imports.FindIndex(im => im.OuterIndex == -(package + 1) && Is(im.ObjectName, objectName)) is >= 0 and var existing)
        {
            return -(existing + 1);
        }

        if (package < 0)
        {
            t.Imports.Add(new ImportEntry(AddName(t.Names, t.Wide, "/Script/CoreUObject"), AddName(t.Names, t.Wide, "Package"), 0, AddName(t.Names, t.Wide, packagePath)));
            package = t.Imports.Count - 1;
        }

        t.Imports.Add(new ImportEntry(AddName(t.Names, t.Wide, "/Script/Engine"), AddName(t.Names, t.Wide, "Texture2D"), -(package + 1), AddName(t.Names, t.Wide, objectName)));
        var index = -t.Imports.Count;

        // The export's dependency groups are SBS, CBS, SBC, CBC in that order: the new one goes at the end of its CBS group,
        // and every later group of any export moves up by one.
        var export = t.Exports[t.ExportIndex];
        if (export.FirstExportDependency < 0)
        {
            t.Exports[t.ExportIndex] = export with { FirstExportDependency = t.Preload.Count, CreateBeforeSerializationDependencies = 1 };
            t.Preload.Add(index);
            return index;
        }

        var at = export.FirstExportDependency + export.SerializationBeforeSerializationDependencies + export.CreateBeforeSerializationDependencies;
        t.Preload.Insert(at, index);
        for (var i = 0; i < t.Exports.Count; i++)
        {
            if (i != t.ExportIndex && t.Exports[i].FirstExportDependency >= at)
            {
                t.Exports[i] = t.Exports[i] with { FirstExportDependency = t.Exports[i].FirstExportDependency + 1 };
            }
        }

        t.Exports[t.ExportIndex] = export with { CreateBeforeSerializationDependencies = export.CreateBeforeSerializationDependencies + 1 };
        return index;
    }
    private static FNameRef AddName(List<string> names, List<bool> wide, string name)
    {
        var index = names.IndexOf(name);
        if (index < 0)
        {
            index = names.Count;
            names.Add(name);
            wide.Add(!name.All(char.IsAscii));
        }

        return new FNameRef(index, 0);
    }

    private static void Add(byte[] data, int offset, int delta) =>
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset)) + delta);

    private static string? NameOf(StructValue item) =>
        Member(item, "ParameterInfo")?.Value is StructValue info && Member(info, "Name")?.Value is NameValue n ? n.Value : null;

    private static PropertyTag? Member(StructValue value, string name) => value.Properties.FirstOrDefault(p => p.Name == name);

    [GeneratedRegex(@"^(?<array>ScalarParameterValues|VectorParameterValues|TextureParameterValues)\[(?<name>[^\]]*[^\]0-9][^\]]*)\]\.ParameterValue$")]
    private static partial Regex KeyedPath();
}
