using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using TransformValue = ScumStudio.Level.Model.TransformValue;

namespace ScumStudio.Level.Export;

/// <summary>
/// A new actor made by copying an actor of another (or the same) cooked level package: the export of
/// <c>AddBlueprintActorOp</c>, and of <c>DuplicateActorOp</c> for actors that own stored child actors.
/// </summary>
/// <param name="Source">The pristine package the actor is copied from (may be the edited package itself).</param>
/// <param name="SourceActor">Actor object name in <paramref name="Source"/>.</param>
/// <param name="NewName">Object name of the copy in the edited level (unique).</param>
/// <param name="RootTransform">Relative transform of the copy's root component; null keeps the source's.</param>
/// <param name="RootComponent">Name of the source's root component when the actor stores no <c>RootComponent</c> property.</param>
public sealed record ForeignActorCopy(CookedPackage Source, string SourceActor, string NewName, TransformValue? RootTransform, string? RootComponent = null)
{
    /// <summary>For a copied world item spawner: the item class written into its <c>_item</c>, or null to keep the source's.</summary>
    public string? Item { get; init; }
}

/// <summary>
/// Copying actors between cooked level packages. The source actor's exports — its components and the child actors
/// its <c>ChildActorComponent</c>s spawned, recursively — are appended to the target package: every FName in the copied
/// tagged properties is re-indexed into the target name table (keeping the FName number), imports the exports need
/// (classes, archetypes, meshes, templates) are added with their outer chains, object references between the copied
/// exports are remapped, references to exports that are not copied become null, and the event-driven-loader dependency
/// groups are rebuilt with the same mapping. Child actors are appended to <c>ULevel::Actors</c> like the actor itself.
/// </summary>
public static partial class LevelPackageEditor
{
    private static List<int> ImportActors(
        CookedPackage target, int levelIndex, IReadOnlyList<ForeignActorCopy> copies, List<ExportEntry> exports, List<ReadOnlyMemory<byte>> data,
        List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames, List<int> preload, List<string> added, List<string> warnings)
    {
        var addedIndices = new List<int>();
        var levelPackageIndex = levelIndex + 1;
        foreach (var copy in copies)
        {
            var source = copy.Source;
            int sourceLevel;
            try
            {
                sourceLevel = FindLevelExport(source);
            }
            catch (InvalidDataException ex)
            {
                warnings.Add($"'{copy.NewName}': the source package is not a level ({ex.Message}).");
                continue;
            }

            var sourceIndex = FindExport(source, copy.SourceActor, sourceLevel + 1);
            if (sourceIndex < 0)
            {
                warnings.Add($"Actor '{copy.SourceActor}' was not found in {source.BasePath ?? "the source level"}; '{copy.NewName}' was not created.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(copy.NewName) || FindExport(target, copy.NewName, levelPackageIndex) >= 0
                || added.Contains(copy.NewName, StringComparer.OrdinalIgnoreCase))
            {
                warnings.Add($"An actor named '{copy.NewName}' already exists in the level; the copy of '{copy.SourceActor}' was not created.");
                continue;
            }

            var session = new ImportSession(target, source, levelPackageIndex, sourceLevel + 1, exports, data, imports, names, wide, addedNames, preload, warnings);
            var created = session.Copy(sourceIndex, copy);
            if (created.Count > 0)
            {
                addedIndices.AddRange(created);
                added.Add(copy.NewName);
            }
        }

        return addedIndices;
    }

    /// <summary>One cross-package copy: holds the index maps while the members are appended.</summary>
    private sealed class ImportSession(
        CookedPackage target, CookedPackage source, int targetLevelPackageIndex, int sourceLevelPackageIndex,
        List<ExportEntry> exports, List<ReadOnlyMemory<byte>> data, List<ImportEntry> imports,
        List<string> names, List<bool> wide, List<string> addedNames, List<int> preload, List<string> warnings)
    {
        private readonly Dictionary<int, int> _importMap = [];
        private readonly Dictionary<int, int> _exportMap = [];
        private readonly bool _sameNames = ReferenceEquals(target, source);
        private readonly HashSet<string> _dropped = new(StringComparer.Ordinal);

        /// <summary>Appends the actor, its components and its stored child actors; returns the new actors' package indices (the actor first).</summary>
        public List<int> Copy(int sourceIndex, ForeignActorCopy copy)
        {
            var members = CollectMembersWithChildActors(sourceIndex);
            var payloads = new byte[members.Count][];
            var blocks = new PropertyBlock[members.Count];
            for (var k = 0; k < members.Count; k++)
            {
                payloads[k] = source.GetExportBytes(members[k]);
                blocks[k] = PropertyReader.ReadPayload(source, payloads[k], members[k]);
            }

            for (var k = 0; k < members.Count; k++)
            {
                _exportMap[members[k] + 1] = exports.Count + k + 1;
            }

            // Root component for the transform.
            var rootOld = -1;
            if (copy.RootTransform is not null)
            {
                if (blocks[0].Find(RootComponentProperty)?.Value is ObjectValue { Index: > 0 } root && _exportMap.ContainsKey(root.Index))
                {
                    rootOld = root.Index - 1;
                }
                else if (copy.RootComponent is { } rootName && FindExport(source, rootName, sourceIndex + 1) is var named && named >= 0 && _exportMap.ContainsKey(named + 1))
                {
                    rootOld = named;
                }
                else
                {
                    warnings.Add($"'{copy.NewName}': the source stores no RootComponent, so the copy keeps the source transform.");
                }
            }

            var newActors = new List<int>();
            var itemWritten = false;
            for (var k = 0; k < members.Count; k++)
            {
                var member = members[k];
                var entry = source.Exports[member];
                var payload = payloads[k];
                RemapNames(payload, blocks[k].Properties, blocks[k].EndOffset);
                RemapObjects(payload, blocks[k].Properties, copy.NewName);
                if (copy.Item is { } item && blocks[k].Find(ItemProperty)?.Value is SoftObjectValue soft && soft.Offset >= 0 && soft.Offset + 8 <= payload.Length)
                {
                    // The spawner's item is a soft path (an FName, then an empty sub path): the same size with another name.
                    var itemName = MakeName(item, names, wide, addedNames);
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(soft.Offset), itemName.Index);
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(soft.Offset + 4), itemName.Number);
                    itemWritten = true;
                }
                if (k == 0 || rootOld >= 0 && member == rootOld)
                {
                    // nothing extra: the transform is patched below once the names are in the target table
                }

                var isActor = entry.OuterIndex == sourceLevelPackageIndex;
                FNameRef objectName;
                int outer;
                if (k == 0)
                {
                    objectName = MakeName(copy.NewName, names, wide, addedNames);
                    outer = targetLevelPackageIndex;
                }
                else if (isActor)
                {
                    objectName = UniqueLevelName(RemapName(entry.ObjectName));
                    outer = targetLevelPackageIndex;
                }
                else
                {
                    objectName = RemapName(entry.ObjectName);
                    outer = MapIndex(entry.OuterIndex, $"outer of {source.ResolveName(entry.ObjectName)}");
                }

                var first = -1;
                int[] counts = [0, 0, 0, 0];
                if (entry.FirstExportDependency >= 0)
                {
                    first = preload.Count;
                    counts = AppendMappedGroups(entry);
                }

                exports.Add(entry with
                {
                    ClassIndex = MapIndex(entry.ClassIndex, "class"),
                    SuperIndex = MapIndex(entry.SuperIndex, "super"),
                    TemplateIndex = MapIndex(entry.TemplateIndex, "template"),
                    OuterIndex = outer,
                    ObjectName = objectName,
                    FirstExportDependency = first,
                    SerializationBeforeSerializationDependencies = counts[0],
                    CreateBeforeSerializationDependencies = counts[1],
                    SerializationBeforeCreateDependencies = counts[2],
                    CreateBeforeCreateDependencies = counts[3],
                });
                data.Add(payload);
                if (isActor)
                {
                    newActors.Add(_exportMap[member + 1]);
                }
            }

            if (rootOld >= 0 && copy.RootTransform is { } value)
            {
                var k = members.IndexOf(rootOld);
                var patched = PatchTransformPayload(payloads[k], blocks[k], value, $"{copy.NewName}.{source.ResolveName(source.Exports[rootOld].ObjectName)}", names, wide, addedNames, warnings);
                if (patched is not null)
                {
                    data[_exportMap[rootOld + 1] - 1] = patched;
                }
            }

            if (copy.Item is not null && !itemWritten)
            {
                warnings.Add($"'{copy.NewName}': {copy.SourceActor} spawns no item, so {copy.Item} was not set.");
            }

            if (_dropped.Count > 0)
            {
                warnings.Add($"'{copy.NewName}': references to {string.Join(", ", _dropped)} were cleared (those objects are not part of the copy).");
            }

            return newActors;
        }

        /// <summary>The actor's exports plus, recursively, every other level actor its payloads reference (stored child actors).</summary>
        private List<int> CollectMembersWithChildActors(int actorIndex)
        {
            var members = CollectMembers(source, actorIndex);
            var set = new HashSet<int>(members);
            var queue = new Queue<int>(members);
            while (queue.Count > 0)
            {
                var member = queue.Dequeue();
                var block = PropertyReader.ReadPayload(source, source.GetExportBytes(member), member);
                foreach (var reference in ObjectReferences(block.Properties))
                {
                    var exportIndex = reference.Index - 1;
                    if (reference.Index > 0 && !set.Contains(exportIndex) && IsActorOfLevel(source, exportIndex, sourceLevelPackageIndex))
                    {
                        foreach (var extra in CollectMembers(source, exportIndex))
                        {
                            if (set.Add(extra))
                            {
                                members.Add(extra);
                                queue.Enqueue(extra);
                            }
                        }
                    }
                }
            }

            return members;
        }

        /// <summary>Maps a source package index (import, copied export or null) to the target.</summary>
        private int MapIndex(int sourceIndex, string what)
        {
            if (sourceIndex == 0)
            {
                return 0;
            }

            if (sourceIndex < 0)
            {
                return MapImport(sourceIndex);
            }

            if (_exportMap.TryGetValue(sourceIndex, out var mapped))
            {
                return mapped;
            }

            _dropped.Add($"{source.ResolveName(source.Exports[sourceIndex - 1].ObjectName)} ({what})");
            return 0;
        }

        private int MapImport(int sourceImportIndex)
        {
            if (_importMap.TryGetValue(sourceImportIndex, out var mapped))
            {
                return mapped;
            }

            var import = source.Imports[-sourceImportIndex - 1];
            var outer = import.OuterIndex switch
            {
                0 => 0,
                < 0 => MapImport(import.OuterIndex),
                _ => MapIndex(import.OuterIndex, "import outer"),
            };
            var result = GetOrAddImport(imports, names, RemapName(import.ClassPackage), RemapName(import.ClassName), outer, RemapName(import.ObjectName));
            _importMap[sourceImportIndex] = result;
            return result;
        }

        /// <summary>The same FName (entry text and number) in the target name table.</summary>
        private FNameRef RemapName(FNameRef sourceName)
        {
            if (_sameNames)
            {
                return sourceName;
            }

            var text = sourceName.Index >= 0 && sourceName.Index < source.Names.Count ? source.Names[sourceName.Index] : "None";
            var mapped = GetOrAddName(names, wide, addedNames, text);
            return new FNameRef(mapped.Index, sourceName.Number);
        }

        /// <summary>A name not used by any export directly under the target level (or already appended there): bumps the FName number.</summary>
        private FNameRef UniqueLevelName(FNameRef name)
        {
            var candidate = name;
            while (exports.Any(e => e.OuterIndex == targetLevelPackageIndex && e.ObjectName == candidate))
            {
                candidate = new FNameRef(candidate.Index, candidate.Number == 0 ? 2 : candidate.Number + 1);
            }

            return candidate;
        }

        private int[] AppendMappedGroups(ExportEntry entry)
        {
            var original = source.ReadPreloadDependencies();
            var pos = entry.FirstExportDependency;
            int[] sizes =
            [
                entry.SerializationBeforeSerializationDependencies,
                entry.CreateBeforeSerializationDependencies,
                entry.SerializationBeforeCreateDependencies,
                entry.CreateBeforeCreateDependencies,
            ];
            var counts = new int[4];
            for (var group = 0; group < 4; group++)
            {
                for (var k = 0; k < sizes[group]; k++, pos++)
                {
                    var value = pos >= 0 && pos < original.Length ? original[pos] : 0;
                    int mapped;
                    if (value < 0)
                    {
                        mapped = MapImport(value);
                    }
                    else if (value > 0 && _exportMap.TryGetValue(value, out var e))
                    {
                        mapped = e;
                    }
                    else
                    {
                        continue; // null or an export that is not copied: the dependency is dropped
                    }

                    preload.Add(mapped);
                    counts[group]++;
                }
            }

            return counts;
        }

        private void RemapObjects(byte[] payload, IEnumerable<PropertyTag> tags, string label)
        {
            foreach (var reference in ObjectReferences(tags))
            {
                if (reference.Index != 0 && reference.Offset >= 0 && reference.Offset + 4 <= payload.Length)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(reference.Offset), MapIndex(reference.Index, "reference"));
                }
            }

            foreach (var delegateValue in DelegateReferences(tags))
            {
                if (delegateValue.Object != 0 && delegateValue.Offset >= 0 && delegateValue.Offset + 4 <= payload.Length)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(delegateValue.Offset), MapIndex(delegateValue.Object, "delegate"));
                }
            }
        }

        private static IEnumerable<DelegateValue> DelegateReferences(IEnumerable<PropertyTag> tags) =>
            tags.SelectMany(t => DelegateReferences(t.Value));

        private static IEnumerable<DelegateValue> DelegateReferences(PropertyValue value) => value switch
        {
            DelegateValue d => [d],
            MulticastDelegateValue m => m.Delegates,
            StructValue s => DelegateReferences(s.Properties),
            ArrayValue a => a.Items.SelectMany(DelegateReferences),
            SetValue set => set.Items.SelectMany(DelegateReferences),
            MapValue map => map.Entries.SelectMany(e => DelegateReferences(e.Key).Concat(DelegateReferences(e.Value))),
            _ => [],
        };

        /// <summary>Re-indexes every FName of a tagged block (tag headers, nested structs, name/enum/soft-path values, terminators).</summary>
        private void RemapNames(byte[] payload, IReadOnlyList<PropertyTag> tags, int terminatorEnd)
        {
            if (_sameNames)
            {
                return;
            }

            RemapTagList(payload, tags);
            RemapNameAt(payload, terminatorEnd - 8); // the top-level "None"
        }

        private void RemapTagList(byte[] payload, IReadOnlyList<PropertyTag> tags)
        {
            foreach (var tag in tags)
            {
                RemapTagHeader(payload, tag);
                RemapValue(payload, tag.Value);
            }
        }

        private void RemapTagHeader(byte[] payload, PropertyTag tag)
        {
            RemapNameAt(payload, tag.Offset); // property name
            RemapNameAt(payload, tag.Offset + 8); // type
            switch (tag.Type)
            {
                case "StructProperty":
                case "ByteProperty":
                case "EnumProperty":
                case "ArrayProperty":
                case "SetProperty":
                    RemapNameAt(payload, tag.Offset + 24);
                    break;
                case "MapProperty":
                    RemapNameAt(payload, tag.Offset + 24);
                    RemapNameAt(payload, tag.Offset + 32);
                    break;
            }
        }

        private void RemapValue(byte[] payload, PropertyValue value)
        {
            switch (value)
            {
                case NameValue or EnumValue or SoftObjectValue:
                    RemapNameAt(payload, value.Offset);
                    break;
                case DelegateValue:
                    RemapNameAt(payload, value.Offset + 4);
                    break;
                case MulticastDelegateValue m:
                    foreach (var d in m.Delegates)
                    {
                        RemapNameAt(payload, d.Offset + 4);
                    }

                    break;
                case StructValue s:
                    RemapTagList(payload, s.Properties);
                    RemapNameAt(payload, s.Offset + s.Size - 8); // the struct's own "None"
                    break;
                case ArrayValue a:
                    if (a.InnerTag is { } inner)
                    {
                        RemapTagHeader(payload, inner);
                    }

                    foreach (var item in a.Items)
                    {
                        RemapValue(payload, item);
                    }

                    break;
                case SetValue set:
                    foreach (var item in set.Removed.Concat(set.Items))
                    {
                        RemapValue(payload, item);
                    }

                    break;
                case MapValue map:
                    foreach (var key in map.RemovedKeys)
                    {
                        RemapValue(payload, key);
                    }

                    foreach (var entry in map.Entries)
                    {
                        RemapValue(payload, entry.Key);
                        RemapValue(payload, entry.Value);
                    }

                    break;
                case PerPlatformValue p:
                    RemapValue(payload, p.Default);
                    break;
                case NativeStructValue n:
                    foreach (var field in n.Fields)
                    {
                        RemapValue(payload, field.Value);
                    }

                    break;
                case FieldPathValue f:
                    // TArray<FName> Path then the owner: count at Offset, names follow.
                    for (var i = 0; i < f.Path.Count; i++)
                    {
                        RemapNameAt(payload, value.Offset + 4 + 8 * i);
                    }

                    break;
            }
        }

        private void RemapNameAt(byte[] payload, int offset)
        {
            if (offset < 0 || offset + 8 > payload.Length)
            {
                return;
            }

            var index = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset));
            var number = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset + 4));
            var mapped = RemapName(new FNameRef(index, number));
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), mapped.Index);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset + 4), mapped.Number);
        }
    }

    /// <summary>Index (negative) of the import with these FNames and outer in the target, adding it when missing.</summary>
    private static int GetOrAddImport(List<ImportEntry> imports, List<string> names, FNameRef classPackage, FNameRef className, int outer, FNameRef objectName)
    {
        for (var i = 0; i < imports.Count; i++)
        {
            var im = imports[i];
            if (im.OuterIndex == outer && SameName(names, im.ClassName, className) && SameName(names, im.ObjectName, objectName) && SameName(names, im.ClassPackage, classPackage))
            {
                return -(i + 1);
            }
        }

        imports.Add(new ImportEntry(classPackage, className, outer, objectName));
        return -imports.Count;
    }

    private static bool SameName(List<string> names, FNameRef a, FNameRef b) =>
        a.Number == b.Number && a.Index >= 0 && b.Index >= 0 && a.Index < names.Count && b.Index < names.Count
        && string.Equals(names[a.Index], names[b.Index], StringComparison.OrdinalIgnoreCase);
}
