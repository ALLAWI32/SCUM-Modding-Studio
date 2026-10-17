using System.Buffers.Binary;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Level.Export;

/// <summary>
/// What a deleted actor leaves behind. Taking it out of <c>ULevel::Actors</c> is not enough: the game still creates every
/// export of the package and runs the class's code on it (B_4: the deleted trade posts kept spawning their traders at the
/// old spots, the deleted quest books logged "Quest Book Id is empty"). So the actor's export and every export under it
/// (components, nested objects) become plain <c>/Script/CoreUObject.Object</c>s with an empty property block: the indices
/// stay (nothing to remap in the other exports' payloads or native data), no class code runs, nothing spawns. A kept
/// export that still points at one gets a stale reference the property's class check nulls with a warning, as today.
/// </summary>
public static partial class LevelPackageEditor
{
    private const string ObjectClassName = "Object";
    private const string DefaultObjectName = "Default__Object";

    /// <summary>
    /// Turns the exports of the actors in <paramref name="removed"/> into inert objects (see the class remarks). Actors the
    /// level's own native data points at (its script actor, model) are left as they are, with a warning. Returns how many
    /// exports were replaced.
    /// </summary>
    private static int NeutralizeDeletedExports(
        CookedPackage package, int levelIndex, IReadOnlyList<string> removed, List<ExportEntry> exports, List<ReadOnlyMemory<byte>> data,
        List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames, List<int> preload, HashSet<int> replaced, List<string> warnings)
    {
        if (removed.Count == 0)
        {
            return 0;
        }

        var pinned = ReadLevelNativeReferences(package, levelIndex);
        var inert = new InertObject(imports, names, wide, addedNames, replaced);
        var count = 0;
        foreach (var name in removed)
        {
            var actorIndex = FindExport(package, name, levelIndex + 1);
            if (actorIndex < 0)
            {
                continue;
            }

            var members = CollectMembers(package, actorIndex);
            if (members.Any(m => pinned.Contains(m + 1)))
            {
                warnings.Add($"Actor '{name}' is part of the level itself (its script actor or model); its objects stay in the package.");
                continue;
            }

            count += inert.Replace(members, exports, data, preload);
        }

        return count;
    }

    /// <summary>
    /// Turns the given components of kept actors (and their sub-objects) into inert objects: a deleted fireplace part's fire,
    /// light and sound, a ChildActorComponent whose child actor was deleted. Each one done is added to
    /// <paramref name="done"/> as <c>Actor.Component</c>; returns how many exports were replaced.
    /// </summary>
    private static int NeutralizeComponents(
        CookedPackage package, int levelIndex, IReadOnlyList<(string Actor, string Component)> components, List<ExportEntry> exports,
        List<ReadOnlyMemory<byte>> data, List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames, List<int> preload,
        HashSet<int> replaced, List<string> done, List<string> warnings)
    {
        if (components.Count == 0)
        {
            return 0;
        }

        var pinned = ReadLevelNativeReferences(package, levelIndex);
        var inert = new InertObject(imports, names, wide, addedNames, replaced);
        var count = 0;
        foreach (var (actor, component) in components)
        {
            var actorIndex = FindExport(package, actor, levelIndex + 1);
            var componentIndex = actorIndex < 0 ? -1 : FindExport(package, component, actorIndex + 1);
            if (componentIndex < 0)
            {
                warnings.Add($"'{actor}.{component}' is not in the level; that component was not removed.");
                continue;
            }

            var members = CollectMembers(package, componentIndex);
            if (members.Any(m => pinned.Contains(m + 1)))
            {
                warnings.Add($"'{actor}.{component}' is part of the level itself; it stays in the package.");
                continue;
            }

            count += inert.Replace(members, exports, data, preload);
            done.Add($"{actor}.{package.ResolveName(package.Exports[componentIndex].ObjectName)}");
        }

        return count;
    }

    /// <summary>The imports and payload an export takes to become a plain <c>Object</c> (made once per package edit).</summary>
    private sealed class InertObject
    {
        private readonly List<ImportEntry> _imports;
        private readonly List<string> _names;
        private readonly List<bool> _wide;
        private readonly List<string> _addedNames;
        private readonly HashSet<int> _replaced;
        private readonly byte[] _payload;
        private int _objectClass;
        private int _objectTemplate;

        public InertObject(List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames, HashSet<int> replaced)
        {
            _imports = imports;
            _names = names;
            _wide = wide;
            _addedNames = addedNames;
            _replaced = replaced;
            var blank = new ByteWriter(12);
            blank.FName(GetOrAddName(names, wide, addedNames, "None"));
            blank.I32(0); // bHasGuid
            _payload = blank.ToArray();
        }

        /// <summary>Replaces the exports <paramref name="members"/> (0-based) with inert objects; returns how many.</summary>
        public int Replace(IReadOnlyList<int> members, List<ExportEntry> exports, List<ReadOnlyMemory<byte>> data, List<int> preload)
        {
            if (_objectClass == 0)
            {
                var core = GetOrAddImport(_imports, _names, _wide, _addedNames, CoreUObjectPackage, "Package", 0, CoreUObjectPackage);
                _objectClass = GetOrAddImport(_imports, _names, _wide, _addedNames, CoreUObjectPackage, "Class", core, ObjectClassName);
                _objectTemplate = GetOrAddImport(_imports, _names, _wide, _addedNames, CoreUObjectPackage, ObjectClassName, core, DefaultObjectName);
            }

            foreach (var i in members)
            {
                var entry = exports[i];
                var first = preload.Count;
                preload.AddRange([_objectClass, _objectTemplate]); // serialize-before-create
                if (entry.OuterIndex > 0)
                {
                    preload.Add(entry.OuterIndex); // create-before-create
                }

                exports[i] = entry with
                {
                    ClassIndex = _objectClass,
                    SuperIndex = 0,
                    TemplateIndex = _objectTemplate,
                    ObjectFlags = ActorFlags,
                    FirstExportDependency = first,
                    SerializationBeforeSerializationDependencies = 0,
                    CreateBeforeSerializationDependencies = 0,
                    SerializationBeforeCreateDependencies = 2,
                    CreateBeforeCreateDependencies = entry.OuterIndex > 0 ? 1 : 0,
                };
                data[i] = _payload;
                _replaced.Add(i);
            }

            return members.Count;
        }
    }

    /// <summary>
    /// After the deletions, what kept objects of the level still point at a deleted one is cleared (owner 2026-10-16: the
    /// game crashed on leaving a server and on Alt+F4 with a mod; the package check found kept actors pointing at deleted
    /// ones). The game would null such a reference itself when it loads ("Reference will be NULLed"), leaving lists with
    /// null entries that code tearing the level down (EndPlay, unregistering from a group, a door's or switch's targets)
    /// walks into. A single reference is written null, as the game would make it; a list of objects loses the entry. The
    /// level's own native data (actor list, script actor, model) is handled where it is written. Returns the references
    /// cleared, as <c>Object.Property</c> labels.
    /// </summary>
    private static List<string> ForgetDeletedObjects(CookedPackage package, IReadOnlySet<int> deleted, List<ReadOnlyMemory<byte>> data, List<string> names)
    {
        var forgotten = new List<string>();
        if (deleted.Count == 0)
        {
            return forgotten;
        }

        bool IsDeleted(int index) => index > 0 && deleted.Contains(index - 1);
        for (var i = 0; i < data.Count; i++)
        {
            if (deleted.Contains(i) || data[i].Length == 0)
            {
                continue;
            }

            var payload = data[i].ToArray();
            PropertyBlock block;
            try
            {
                block = PropertyReader.ReadPayload(package, payload, names);
            }
            catch (Exception ex) when (ex is FormatException or EndOfStreamException or ArgumentOutOfRangeException or InvalidDataException or IndexOutOfRangeException)
            {
                continue; // no tagged block this reader follows: left as it is (the package check reads it again)
            }

            var label = i < package.Exports.Count ? package.ResolveName(package.Exports[i].ObjectName) : $"#{i + 1}";
            var changed = false;
            foreach (var tag in block.Properties.Where(t => !IsObjectList(t)))
            {
                foreach (var (index, offset) in References(tag.Value).Where(r => IsDeleted(r.Index)))
                {
                    BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset), 0);
                    forgotten.Add($"{label}.{tag.Name}");
                    changed = true;
                }
            }

            // Lists of objects lose the entries (one list at a time: the payload shifts behind each one).
            while (block.Properties.FirstOrDefault(t => IsObjectList(t) && ((ArrayValue)t.Value).Items.Any(v => IsDeleted(((ObjectValue)v).Index))) is { } list)
            {
                var items = ((ArrayValue)list.Value).Items.Cast<ObjectValue>().Select(o => o.Index).ToList();
                var removed = items.RemoveAll(IsDeleted);
                forgotten.AddRange(Enumerable.Repeat($"{label}.{list.Name}[]", removed));
                payload = WriteObjectArray(payload, list, items);
                block = PropertyReader.ReadPayload(package, payload, names);
                changed = true;
            }

            if (changed)
            {
                data[i] = payload;
            }
        }

        return forgotten;
    }

    /// <summary>
    /// References whose holder the game never has without the target: a vehicle trader's bike and car lifts name it in
    /// <c>_assignedTradePost</c> (every lift of the game's outposts does, 4 + 4 checked 2026-10-16), so a deleted trader takes
    /// its lifts along instead of leaving lifts the game has never run without one.
    /// </summary>
    private static readonly string[] OwnerReferences = ["_assignedTradePost"];

    /// <summary>
    /// <paramref name="delete"/> plus the kept actors of the level that hold an <see cref="OwnerReferences"/> property naming
    /// one of them (each with a warning).
    /// </summary>
    private static IReadOnlyCollection<string> WithDependents(CookedPackage package, int levelIndex, IReadOnlyCollection<string> delete, List<string> warnings)
    {
        if (delete.Count == 0)
        {
            return delete;
        }

        var level = levelIndex + 1;
        var names = new HashSet<string>(delete, StringComparer.OrdinalIgnoreCase);
        var deleted = new Dictionary<int, string>();
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (package.Exports[i].OuterIndex == level && names.Contains(package.ResolveName(package.Exports[i].ObjectName)))
            {
                deleted[i + 1] = package.ResolveName(package.Exports[i].ObjectName);
            }
        }

        var added = new List<string>();
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (package.Exports[i].OuterIndex != level || deleted.ContainsKey(i + 1))
            {
                continue;
            }

            PropertyBlock block;
            try
            {
                block = package.ReadProperties(i);
            }
            catch (Exception ex) when (ex is FormatException or EndOfStreamException or ArgumentOutOfRangeException or InvalidDataException or IndexOutOfRangeException or NotSupportedException)
            {
                continue;
            }

            foreach (var property in OwnerReferences)
            {
                if (block.Find(property)?.Value is ObjectValue { Index: var target } && deleted.TryGetValue(target, out var owner))
                {
                    var name = package.ResolveName(package.Exports[i].ObjectName);
                    added.Add(name);
                    warnings.Add($"Actor '{name}' was removed with '{owner}': it belongs to it ({property}), and the game has none without one.");
                    break;
                }
            }
        }

        return added.Count == 0 ? delete : [.. delete, .. added];
    }

    private static bool IsObjectList(PropertyTag tag) =>
        tag.Value is ArrayValue { InnerType: "ObjectProperty", Items.Count: > 0 } array && array.Items.All(v => v is ObjectValue);

    /// <summary>Every object reference inside a property value (package index, offset of the index in the payload).</summary>
    private static IEnumerable<(int Index, int Offset)> References(PropertyValue value)
    {
        switch (value)
        {
            case ObjectValue o:
                yield return (o.Index, o.Offset);
                break;
            case DelegateValue d:
                yield return (d.Object, d.Offset);
                break;
            case MulticastDelegateValue m:
                foreach (var r in m.Delegates.SelectMany(References))
                {
                    yield return r;
                }

                break;
            case StructValue s:
                foreach (var r in s.Properties.SelectMany(p => References(p.Value)))
                {
                    yield return r;
                }

                break;
            case ArrayValue a:
                foreach (var r in a.Items.SelectMany(References))
                {
                    yield return r;
                }

                break;
            case SetValue set:
                foreach (var r in set.Items.SelectMany(References))
                {
                    yield return r;
                }

                break;
            case MapValue map:
                foreach (var r in map.Entries.SelectMany(e => References(e.Key).Concat(References(e.Value))))
                {
                    yield return r;
                }

                break;
        }
    }

    /// <summary>
    /// The export indices (1-based) the level's native data holds as typed pointers after the actor list
    /// (<c>ULevel::Serialize</c>: FURL, Model, ModelComponents, LevelScriptActor, NavListStart/End). A plain object there
    /// would be read as the wrong type, so those exports are never replaced.
    /// </summary>
    private static HashSet<int> ReadLevelNativeReferences(CookedPackage package, int levelIndex)
    {
        var result = new HashSet<int>();
        try
        {
            var layout = ReadActorArray(package, levelIndex);
            var r = new ByteReader(layout.Payload) { Position = layout.TailOffset };
            for (var k = 0; k < 4; k++)
            {
                r.FString(); // Protocol, Host, Map, Portal
            }

            var ops = r.I32();
            for (var k = 0; k < ops && k < 1024; k++)
            {
                r.FString();
            }

            r.I32(); // Port
            r.I32(); // Valid
            result.Add(r.I32()); // Model
            var components = r.I32();
            for (var k = 0; k < components && k < 65536; k++)
            {
                result.Add(r.I32());
            }

            result.Add(r.I32()); // LevelScriptActor
            result.Add(r.I32()); // NavListStart
            result.Add(r.I32()); // NavListEnd
        }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException or ArgumentOutOfRangeException or InvalidDataException or IndexOutOfRangeException)
        {
            // The tail could not be followed: keep what was read so far.
        }

        result.Remove(0);
        return result;
    }
}
