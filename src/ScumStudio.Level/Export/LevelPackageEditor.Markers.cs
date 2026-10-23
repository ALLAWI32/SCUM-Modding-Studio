using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Level.Export;

/// <summary>
/// Point arrays a level's component takes from its Blueprint's template (a building's indoor loot points; seviscache: they
/// had no gizmo). Moved in the studio, the array is written onto the component itself: the template's elements are copied
/// in (names and imports re-indexed into the level, as a copied actor's), each point placed anew, and the tag inserted before
/// <c>None</c>. <see cref="Spawns.SpawnPoint.Source"/> indexes the first array found up the template chain, in its stored
/// order, as the level reader returns it.
/// </summary>
public static partial class LevelPackageEditor
{
    /// <summary>Templates followed up a chain (as the level reader's <c>MaxTemplateDepth</c>).</summary>
    private const int MaxTemplateHops = 8;

    private static string? InsertTemplateArray(
        CookedPackage package, int levelIndex, SpawnPointsPatch patch, string label, int exportIndex, byte[] payload, PropertyBlock block,
        List<ExportEntry> exports, List<ReadOnlyMemory<byte>> data, List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames,
        List<int> preload, Func<string, CookedPackage?>? packages, List<string> warnings)
    {
        if (packages is null || FindTemplateArray(package, exports[exportIndex].TemplateIndex, imports, names, patch.Array, packages) is not { } found)
        {
            warnings.Add($"'{label}': the array is not stored on the export (it comes from the class or a template); its spawn points were not written.");
            return null;
        }

        var (source, work, array) = found;

        // No point kept (a placed building with no loot inside): only the template's element type is needed.
        if (patch.Points.Count > 0 && array.Items.Any(i => i is not StructValue || HasUndecoded(i)))
        {
            warnings.Add($"'{label}': its template's {patch.Array} holds values this studio cannot read; its spawn points were not written.");
            return null;
        }

        if (patch.Points.Count > 0 && !ReferenceEquals(source, package))
        {
            var session = new ImportSession(package, source, levelIndex + 1, 0, exports, data, imports, names, wide, addedNames, preload, warnings);
            session.RemapValues(work, array.Items);
            if (session.Dropped.Count > 0)
            {
                warnings.Add($"'{label}': references to {string.Join(", ", session.Dropped)} in its template's points were cleared.");
            }
        }

        var items = new List<byte[]>(patch.Points.Count);
        foreach (var point in patch.Points)
        {
            if (point.Source < 0 || point.Source >= array.Items.Count)
            {
                warnings.Add($"'{label}': point source {point.Source} is outside the {array.Items.Count} element(s) of its template; that point was left out.");
                continue;
            }

            var item = (StructValue)array.Items[point.Source];
            var bytes = work.AsSpan(item.Offset, item.Size).ToArray();
            if (Spawns.SpawnPlacesEditor.WritePoint(bytes, item, point.Local, -item.Offset) is { } problem)
            {
                warnings.Add($"'{label}[{point.Source}]': {problem}; the point keeps its template's place.");
            }

            items.Add(bytes);
        }

        FNameRef Name(string value) => GetOrAddName(names, wide, addedNames, value);
        var inner = array.InnerTag!;
        var value = new ByteWriter(64 + items.Sum(b => b.Length));
        value.I32(items.Count);
        value.FName(Name(patch.Array));
        value.FName(Name(StructPropertyType));
        value.I32(items.Sum(b => b.Length));
        value.I32(0);
        value.FName(Name(inner.StructName ?? "None"));
        value.Guid(inner.StructGuid ?? default);
        value.U8(0);
        foreach (var b in items)
        {
            value.Raw(b);
        }

        var tag = new ByteWriter(40 + value.Length);
        tag.FName(Name(patch.Array));
        tag.FName(Name("ArrayProperty"));
        tag.I32(value.Length);
        tag.I32(0);
        tag.FName(Name(StructPropertyType));
        tag.U8(0);
        tag.Raw(value.WrittenSpan);

        var at = block.EndOffset - 8;
        byte[] written = [.. payload.AsSpan(0, at), .. tag.WrittenSpan, .. payload.AsSpan(at)];
        data[exportIndex] = written;

        // The presets the points name (imports now) exist before the component is read, as a cooked one lists them.
        var referenced = patch.Points.Where(p => p.Source >= 0 && p.Source < array.Items.Count).Select(p => array.Items[p.Source]).Distinct()
            .SelectMany(i => ObjectReferences(i))
            .Where(o => o.Offset >= 0 && o.Offset + 4 <= work.Length)
            .Select(o => BinaryPrimitives.ReadInt32LittleEndian(work.AsSpan(o.Offset)))
            .Where(i => i < 0)
            .Distinct()
            .ToList();
        if (referenced.Count > 0)
        {
            AddCreateBeforeSerialize(exports, preload, exportIndex, referenced);
        }

        return $"{label} ({items.Count} point(s), from its template)";
    }

    /// <summary>
    /// The first stored <paramref name="property"/> array up the template chain starting at <paramref name="templateIndex"/>
    /// of the level: the package holding it, a copy of that export's payload and the array as read from it; null when no
    /// template on the way can be read or none stores it.
    /// </summary>
    private static (CookedPackage Source, byte[] Payload, ArrayValue Array)? FindTemplateArray(
        CookedPackage level, int templateIndex, List<ImportEntry> imports, List<string> names, string property, Func<string, CookedPackage?> packages)
    {
        var current = templateIndex > 0 && templateIndex <= level.Exports.Count ? (level, templateIndex - 1) : ImportedObject(imports, names, templateIndex, packages);
        for (var hop = 0; hop <= MaxTemplateHops && current is { } at; hop++)
        {
            var (source, index) = at;
            var payload = source.GetExportBytes(index);
            try
            {
                if (PropertyReader.ReadPayload(source, payload, index).Find(property) is { Value: ArrayValue { InnerTag: not null } array })
                {
                    return (source, payload, array);
                }
            }
            catch (Exception ex) when (ex is FormatException or EndOfStreamException or ArgumentOutOfRangeException or NotSupportedException or InvalidDataException or IndexOutOfRangeException)
            {
                return null;
            }

            var next = source.Exports[index].TemplateIndex;
            current = next > 0 && next <= source.Exports.Count ? (source, next - 1) : ImportedObject(source.Imports, source.Names, next, packages);
        }

        return null;
    }

    /// <summary>The export an import names, in its own package as <paramref name="packages"/> loads it; null for a script object or when unreadable.</summary>
    private static (CookedPackage Package, int Export)? ImportedObject(IReadOnlyList<ImportEntry> imports, IReadOnlyList<string> names, int index, Func<string, CookedPackage?> packages)
    {
        var chain = new List<string>(); // the object, then its outers below the package
        for (var hops = 0; index < 0 && -index - 1 < imports.Count && hops < 16; hops++)
        {
            var import = imports[-index - 1];
            if (import.OuterIndex == 0)
            {
                var path = import.ObjectName.Format(names);
                if (chain.Count == 0 || path.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) || packages(path) is not { } package)
                {
                    return null;
                }

                var found = FindByChain(package, chain);
                return found >= 0 ? (package, found) : null;
            }

            chain.Add(import.ObjectName.Format(names));
            index = import.OuterIndex;
        }

        return null;
    }

    /// <summary>The export named <c>chain[0]</c> whose outers are named <c>chain[1..]</c> up to the package, or -1.</summary>
    private static int FindByChain(CookedPackage package, List<string> chain)
    {
        for (var i = 0; i < package.Exports.Count; i++)
        {
            var at = i + 1;
            var k = 0;
            for (; k < chain.Count && at > 0 && string.Equals(package.ResolveName(package.Exports[at - 1].ObjectName), chain[k], StringComparison.OrdinalIgnoreCase); k++)
            {
                at = package.Exports[at - 1].OuterIndex;
            }

            if (k == chain.Count && at == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>True when a value, or one inside it, could not be decoded (its bytes would be copied blind).</summary>
    private static bool HasUndecoded(PropertyValue value) => value switch
    {
        RawValue => true,
        StructValue s => s.Properties.Any(p => HasUndecoded(p.Value)),
        ArrayValue a => a.Items.Any(HasUndecoded),
        _ => false,
    };
}
