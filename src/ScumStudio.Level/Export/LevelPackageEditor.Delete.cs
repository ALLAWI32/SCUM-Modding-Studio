using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;

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
        List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames, List<int> preload, List<string> warnings)
    {
        if (removed.Count == 0)
        {
            return 0;
        }

        var pinned = ReadLevelNativeReferences(package, levelIndex);
        var inert = new InertObject(imports, names, wide, addedNames);
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
    /// Turns the given components of kept actors (and their sub-objects) into inert objects: the ChildActorComponents whose
    /// child actors were deleted. Returns how many exports were replaced.
    /// </summary>
    private static int NeutralizeComponents(
        CookedPackage package, int levelIndex, IReadOnlyList<(string Actor, string Component)> components, List<ExportEntry> exports,
        List<ReadOnlyMemory<byte>> data, List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames, List<int> preload,
        List<string> warnings)
    {
        if (components.Count == 0)
        {
            return 0;
        }

        var pinned = ReadLevelNativeReferences(package, levelIndex);
        var inert = new InertObject(imports, names, wide, addedNames);
        var count = 0;
        foreach (var (actor, component) in components)
        {
            var actorIndex = FindExport(package, actor, levelIndex + 1);
            var componentIndex = actorIndex < 0 ? -1 : FindExport(package, component, actorIndex + 1);
            if (componentIndex < 0)
            {
                warnings.Add($"'{actor}.{component}' is not in the level; the component that spawned a deleted child actor was not removed.");
                continue;
            }

            var members = CollectMembers(package, componentIndex);
            if (members.Any(m => pinned.Contains(m + 1)))
            {
                warnings.Add($"'{actor}.{component}' is part of the level itself; it stays in the package.");
                continue;
            }

            count += inert.Replace(members, exports, data, preload);
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
        private readonly byte[] _payload;
        private int _objectClass;
        private int _objectTemplate;

        public InertObject(List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames)
        {
            _imports = imports;
            _names = names;
            _wide = wide;
            _addedNames = addedNames;
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
            }

            return members.Count;
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
