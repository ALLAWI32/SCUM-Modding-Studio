using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Level.Export;

/// <summary>A new actor made by copying an existing one of the same level (the export of <c>DuplicateActorOp</c>).</summary>
/// <param name="SourceActor">Actor to copy (object name in the pristine level).</param>
/// <param name="NewName">Object name of the copy (unique in the level).</param>
/// <param name="RootTransform">Relative transform of the copy's root component; null keeps the source's.</param>
/// <param name="RootComponent">
/// Name of the source's root component, for actors that store no <c>RootComponent</c> property (Blueprint instances whose
/// root comes from the class); null = read the property.
/// </param>
public sealed record ActorCopy(string SourceActor, string NewName, TransformValue? RootTransform, string? RootComponent = null);

/// <summary>
/// Copying actors inside a cooked level package: the actor's export and every export under it (components, nested
/// objects) are appended to the export table under new names, object references between them are remapped in the copied
/// payloads, their event-driven-loader preload dependencies are appended with the same remapping, the level's own
/// dependency groups are re-emitted with the new actor and the actor is appended to <c>ULevel::Actors</c>. Actors that
/// reference other actors of the level (e.g. Blueprints with stored child actors) are refused: the copy would share them.
/// </summary>
public static partial class LevelPackageEditor
{

    private static (List<int> AddedPackageIndices, List<string> AddedNames) CopyActors(
        CookedPackage package, int levelIndex, IReadOnlyList<ActorCopy> copies, List<ExportEntry> exports, List<ReadOnlyMemory<byte>> data,
        List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames, List<int> preload, List<string> warnings)
    {
        var addedIndices = new List<int>();
        var added = new List<string>();
        if (copies.Count == 0)
        {
            return (addedIndices, added);
        }

        var levelPackageIndex = levelIndex + 1;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var copy in copies)
        {
            var sourceIndex = FindExport(package, copy.SourceActor, levelPackageIndex);
            if (sourceIndex < 0)
            {
                warnings.Add($"Actor '{copy.SourceActor}' was not found in the level; '{copy.NewName}' was not created.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(copy.NewName) || FindExport(package, copy.NewName, levelPackageIndex) >= 0 || !taken.Add(copy.NewName))
            {
                warnings.Add($"An actor named '{copy.NewName}' already exists in the level; the copy of '{copy.SourceActor}' was not created.");
                continue;
            }

            var members = CollectMembers(package, sourceIndex);
            var memberSet = new HashSet<int>(members);
            var external = new SortedSet<string>(StringComparer.Ordinal);
            var blocks = new PropertyBlock[members.Count];
            var payloads = new byte[members.Count][];
            for (var k = 0; k < members.Count; k++)
            {
                payloads[k] = package.GetExportBytes(members[k]);
                blocks[k] = PropertyReader.ReadPayload(package, payloads[k], members[k]);
                foreach (var reference in ObjectReferences(blocks[k].Properties))
                {
                    var target = reference.Index - 1;
                    if (reference.Index > 0 && !memberSet.Contains(target) && IsActorOfLevel(package, target, levelPackageIndex))
                    {
                        external.Add(package.ResolveName(package.Exports[target].ObjectName));
                    }
                }
            }

            if (external.Count > 0)
            {
                // Stored child actors come along: the general (cross-package) copier handles them.
                var created = ImportActors(package, levelIndex, [new ForeignActorCopy(package, copy.SourceActor, copy.NewName, copy.RootTransform, copy.RootComponent)],
                    exports, data, imports, names, wide, addedNames, preload, added, warnings);
                addedIndices.AddRange(created);
                continue;
            }

            // New package indices: appended in member order, the actor first.
            var remap = new Dictionary<int, int>();
            for (var k = 0; k < members.Count; k++)
            {
                remap[members[k] + 1] = exports.Count + k + 1;
            }

            var rootOld = -1;
            if (copy.RootTransform is not null)
            {
                if (blocks[0].Find(RootComponentProperty)?.Value is ObjectValue { Index: > 0 } root && memberSet.Contains(root.Index - 1))
                {
                    rootOld = root.Index - 1;
                }
                else if (copy.RootComponent is { } rootName && FindExport(package, rootName, sourceIndex + 1) is var named && named >= 0 && memberSet.Contains(named))
                {
                    rootOld = named;
                }
                else
                {
                    warnings.Add($"'{copy.NewName}': the source stores no RootComponent{(copy.RootComponent is null ? string.Empty : $" and has no component '{copy.RootComponent}'")}, so the copy keeps the source transform.");
                }
            }

            for (var k = 0; k < members.Count; k++)
            {
                var source = package.Exports[members[k]];
                var payload = payloads[k];
                RemapReferences(payload, blocks[k].Properties, remap);
                if (k == 0 && blocks[0].Find(ParentComponentProperty)?.Value is ObjectValue { Index: > 0 } parent && !remap.ContainsKey(parent.Index)
                    && parent.Offset >= 0 && parent.Offset + 4 <= payload.Length)
                {
                    // A copy of a building's door (a child actor) is no child of the building's component, which still spawns
                    // the original: a child actor nobody owns does not begin play in a streamed level, a door that never opens
                    // (salvador, Discord). It stays attached to that component (its transform is relative to it).
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(parent.Offset), 0);
                }

                var first = -1;
                if (source.FirstExportDependency >= 0)
                {
                    first = preload.Count;
                    AppendGroups(package, source, remap, preload, extraCreateBeforeSerialize: null);
                }

                var outer = k == 0 ? levelPackageIndex : remap.TryGetValue(source.OuterIndex, out var newOuter) ? newOuter : source.OuterIndex;
                var name = k == 0 ? MakeName(copy.NewName, names, wide, addedNames) : source.ObjectName;
                exports.Add(source with { ObjectName = name, OuterIndex = outer, FirstExportDependency = first });
                data.Add(payload);
            }

            if (rootOld >= 0 && copy.RootTransform is { } value)
            {
                var k = members.IndexOf(rootOld);
                var newIndex = remap[rootOld + 1] - 1;
                var patched = PatchTransformPayload(payloads[k], blocks[k], value, $"{copy.NewName}.{package.ResolveName(package.Exports[rootOld].ObjectName)}", names, wide, addedNames, warnings);
                if (patched is not null)
                {
                    data[newIndex] = patched;
                }
            }

            addedIndices.Add(remap[sourceIndex + 1]);
            added.Add(copy.NewName);
        }

        // The caller registers the new actors with the level export (RegisterActorsWithLevel) once for every kind of addition.
        return (addedIndices, added);
    }

    /// <summary>The export and, in table order, every export whose outer chain leads to it.</summary>
    private static List<int> CollectMembers(CookedPackage package, int exportIndex)
    {
        var members = new List<int> { exportIndex };
        var set = new HashSet<int> { exportIndex };
        var grew = true;
        while (grew)
        {
            grew = false;
            for (var i = 0; i < package.Exports.Count; i++)
            {
                var outer = package.Exports[i].OuterIndex;
                if (!set.Contains(i) && outer > 0 && set.Contains(outer - 1))
                {
                    members.Add(i);
                    set.Add(i);
                    grew = true;
                }
            }
        }

        return members;
    }

    private static bool IsActorOfLevel(CookedPackage package, int exportIndex, int levelPackageIndex) =>
        exportIndex >= 0 && exportIndex < package.Exports.Count && package.Exports[exportIndex].OuterIndex == levelPackageIndex;

    /// <summary>
    /// Appends the four preload-dependency groups of <paramref name="source"/> to <paramref name="preload"/>, remapping
    /// indices through <paramref name="remap"/>; <paramref name="extraCreateBeforeSerialize"/> is added to the second group.
    /// </summary>
    private static void AppendGroups(CookedPackage package, ExportEntry source, IReadOnlyDictionary<int, int> remap, List<int> preload, IReadOnlyList<int>? extraCreateBeforeSerialize)
    {
        var original = package.ReadPreloadDependencies();
        var pos = source.FirstExportDependency;
        int[] counts =
        [
            source.SerializationBeforeSerializationDependencies,
            source.CreateBeforeSerializationDependencies,
            source.SerializationBeforeCreateDependencies,
            source.CreateBeforeCreateDependencies,
        ];
        for (var group = 0; group < counts.Length; group++)
        {
            for (var k = 0; k < counts[group]; k++, pos++)
            {
                var entry = pos >= 0 && pos < original.Length ? original[pos] : 0;
                preload.Add(remap.TryGetValue(entry, out var mapped) ? mapped : entry);
            }

            if (group == 1 && extraCreateBeforeSerialize is not null)
            {
                preload.AddRange(extraCreateBeforeSerialize);
            }
        }
    }

    /// <summary>Every object reference (ObjectProperty values, also inside structs, arrays, sets and maps) of a tag list.</summary>
    private static IEnumerable<ObjectValue> ObjectReferences(IEnumerable<PropertyTag> tags) =>
        tags.SelectMany(t => ObjectReferences(t.Value));

    private static IEnumerable<ObjectValue> ObjectReferences(PropertyValue value)
    {
        switch (value)
        {
            case ObjectValue o:
                yield return o;
                break;
            case StructValue s:
                foreach (var o in ObjectReferences(s.Properties))
                {
                    yield return o;
                }

                break;
            case ArrayValue a:
                foreach (var o in a.Items.SelectMany(ObjectReferences))
                {
                    yield return o;
                }

                break;
            case SetValue set:
                foreach (var o in set.Items.SelectMany(ObjectReferences))
                {
                    yield return o;
                }

                break;
            case MapValue map:
                foreach (var entry in map.Entries)
                {
                    foreach (var o in ObjectReferences(entry.Key).Concat(ObjectReferences(entry.Value)))
                    {
                        yield return o;
                    }
                }

                break;
            case PerPlatformValue p:
                foreach (var o in ObjectReferences(p.Default))
                {
                    yield return o;
                }

                break;
            case NativeStructValue n:
                foreach (var o in n.Fields.SelectMany(f => ObjectReferences(f.Value)))
                {
                    yield return o;
                }

                break;
        }
    }

    /// <summary>Rewrites, in place, every object reference of <paramref name="tags"/> that <paramref name="remap"/> knows.</summary>
    private static void RemapReferences(byte[] payload, IEnumerable<PropertyTag> tags, IReadOnlyDictionary<int, int> remap)
    {
        foreach (var reference in ObjectReferences(tags))
        {
            if (reference.Index > 0 && remap.TryGetValue(reference.Index, out var mapped) && reference.Offset >= 0 && reference.Offset + 4 <= payload.Length)
            {
                BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(reference.Offset), mapped);
            }
        }
    }

    /// <summary>
    /// An FName for <paramref name="text"/>: a trailing <c>_N</c> becomes the FName number (UE's convention, so CUE4Parse
    /// and the engine print the same text back); the base string is added to the name table when missing.
    /// </summary>
    private static FNameRef MakeName(string text, List<string> names, List<bool> wide, List<string> addedNames) =>
        GetOrAddName(names, wide, addedNames, text); // GetOrAddName splits the number like FName
}
