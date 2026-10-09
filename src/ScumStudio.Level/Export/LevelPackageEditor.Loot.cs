using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.IO;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Level.Editing;

namespace ScumStudio.Level.Export;

/// <summary>The loot of one mesh component (an actor of the level, pristine or created by this request).</summary>
/// <param name="Actor">Actor object name.</param>
/// <param name="Component">The mesh component's object name.</param>
/// <param name="Loot">What a search of it gives.</param>
public sealed record LootPatch(string Actor, string Component, LootSetting Loot);

/// <summary>
/// Loot of mesh components. SCUM searches a mesh through the component's <c>AssetUserData</c> entries of class
/// <c>ExamineAssetData</c> (<c>SpawnerPreset</c> = <c>ItemSpawnerPresetWithOverrides</c>, whose <c>Preset</c> names the
/// <c>Examine_*</c> preset class). A patch keeps the component's other user data, reuses its examine entries (a level's
/// own are empty and take sound and preset from their archetype; only <c>Preset</c> is written over), adds entries with
/// the first one's archetype when more presets are asked for, and leaves none when the component is not lootable.
/// </summary>
public static partial class LevelPackageEditor
{
    private const string ExamineClass = "ExamineAssetData";

    private static string? TryPatchLoot(
        CookedPackage package, int levelIndex, LootPatch patch, List<ExportEntry> exports, List<ReadOnlyMemory<byte>> data,
        List<ImportEntry> imports, List<string> names, List<bool> wide, List<string> addedNames, List<int> preload, List<string> warnings)
    {
        var label = $"{patch.Actor}.{patch.Component}";
        var actor = FindLevelActor(exports, names, levelIndex + 1, patch.Actor);
        var component = actor > 0 ? FindChild(exports, names, actor, patch.Component) : 0;
        if (component <= 0)
        {
            warnings.Add($"{label}: no such component in the level; its loot was not written.");
            return null;
        }

        FNameRef Name(string value) => GetOrAddName(names, wide, addedNames, value);
        int Import(string classPackage, string className, int outer, string objectName) =>
            GetOrAddImport(imports, names, wide, addedNames, classPackage, className, outer, objectName);

        PropertyBlock block;
        try
        {
            block = PropertyReader.ReadPayload(package, data[component - 1].ToArray(), names);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            warnings.Add($"{label}: its properties could not be read ({ex.Message}); its loot was not written.");
            return null;
        }

        var current = block.Find("AssetUserData") is { Value: ArrayValue list } tag ? (Tag: tag, Items: list.Items.OfType<ObjectValue>().Select(o => o.Index).ToList()) : (Tag: (PropertyTag?)null, Items: []);
        var examine = current.Items.Where(i => i > 0 && i <= exports.Count && IsExamine(exports[i - 1])).ToList();
        var kept = current.Items.Where(i => !examine.Contains(i)).ToList();
        var chosen = new List<int>();
        if (patch.Loot.Lootable)
        {
            // The examine class and its default from the level's own imports (the game module's), or /Script/ConZ.
            var scriptPackage = imports.FindIndex(i => NameEquals(names, i.ObjectName, ExamineClass) && NameEquals(names, i.ClassName, "Class")) is var found && found >= 0
                ? imports[found].OuterIndex
                : Import(CoreUObjectPackage, "Package", 0, "/Script/ConZ");
            var scriptName = names[imports[-scriptPackage - 1].ObjectName.Index];
            var examineClass = Import(CoreUObjectPackage, "Class", scriptPackage, ExamineClass);
            var template = examine.Count > 0 ? exports[examine[0] - 1].TemplateIndex : Import(scriptName, ExamineClass, scriptPackage, "Default__" + ExamineClass);
            for (var k = 0; k < patch.Loot.Presets.Count; k++)
            {
                var (presetPackage, presetClass) = SplitObjectPath(patch.Loot.Presets[k]);
                var presetImport = Import(EnginePackage, "BlueprintGeneratedClass", Import(CoreUObjectPackage, "Package", 0, presetPackage), presetClass);
                int entry;
                if (k < examine.Count)
                {
                    entry = examine[k];
                    var payload = data[entry - 1].ToArray();
                    data[entry - 1] = WithPreset(package, names, payload, Name, presetImport);
                    if (entry <= package.Exports.Count)
                    {
                        // Its preset class is read when it is: listed before it is serialized, as a cooked entry lists it.
                        var first = preload.Count;
                        AppendGroups(package, package.Exports[entry - 1], new Dictionary<int, int>(), preload, extraCreateBeforeSerialize: [presetImport]);
                        exports[entry - 1] = exports[entry - 1] with
                        {
                            FirstExportDependency = first,
                            CreateBeforeSerializationDependencies = package.Exports[entry - 1].CreateBeforeSerializationDependencies + 1,
                        };
                    }
                }
                else
                {
                    var payload = new ByteWriter(96);
                    WritePresetTag(payload, Name, presetImport);
                    payload.FName(Name("None"));
                    payload.I32(0);
                    var first = preload.Count;
                    preload.Add(presetImport); // create-before-serialize
                    preload.AddRange([examineClass, template]); // serialize-before-create
                    preload.Add(component); // create-before-create
                    exports.Add(new ExportEntry
                    {
                        ClassIndex = examineClass,
                        SuperIndex = 0,
                        TemplateIndex = template,
                        OuterIndex = component,
                        ObjectName = UniqueChildName(exports, names, wide, addedNames, component, "ExamineAssetData_" + k),
                        ObjectFlags = 0x8, // RF_Transactional, like the level's own entries
                        PackageGuid = exports[component - 1].PackageGuid,
                        PackageFlags = exports[component - 1].PackageFlags,
                        FirstExportDependency = first,
                        SerializationBeforeSerializationDependencies = 0,
                        CreateBeforeSerializationDependencies = 1,
                        SerializationBeforeCreateDependencies = 2,
                        CreateBeforeCreateDependencies = 1,
                    });
                    data.Add(payload.ToArray());
                    entry = exports.Count;
                }

                chosen.Add(entry);
            }
        }

        var items = kept.Concat(chosen).ToList();
        var bytes = data[component - 1].ToArray();
        data[component - 1] = current.Tag is { } existing ? WriteObjectArray(bytes, existing, items) : InsertObjectArray(bytes, block, Name, "AssetUserData", items);
        return label;

        bool IsExamine(ExportEntry e) => e.ClassIndex < 0 && NameEquals(names, imports[-e.ClassIndex - 1].ObjectName, ExamineClass);
    }

    /// <summary>The package index of the export named <paramref name="name"/> directly under <paramref name="outer"/>, or 0.</summary>
    private static int FindChild(List<ExportEntry> exports, List<string> names, int outer, string name)
    {
        for (var i = 0; i < exports.Count; i++)
        {
            if (exports[i].OuterIndex == outer && string.Equals(exports[i].ObjectName.Format(names), name, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private static FNameRef UniqueChildName(List<ExportEntry> exports, List<string> names, List<bool> wide, List<string> addedNames, int outer, string name)
    {
        var candidate = name;
        for (var n = 100; FindChild(exports, names, outer, candidate) > 0; n++)
        {
            candidate = "ExamineAssetData_" + n;
        }

        return GetOrAddName(names, wide, addedNames, candidate);
    }

    /// <summary>An examine entry's payload with <c>SpawnerPreset.Preset</c> naming <paramref name="presetImport"/> (the tag replaced or inserted).</summary>
    private static byte[] WithPreset(CookedPackage package, List<string> names, byte[] payload, Func<string, FNameRef> name, int presetImport)
    {
        var block = PropertyReader.ReadPayload(package, payload, names);
        var tag = new ByteWriter(64);
        WritePresetTag(tag, name, presetImport);
        var (start, end) = block.Find("SpawnerPreset") is { } old ? (old.Offset, old.ValueOffset + old.Size) : (block.EndOffset - 8, block.EndOffset - 8);
        var result = new byte[payload.Length - (end - start) + tag.Length];
        payload.AsSpan(0, start).CopyTo(result);
        tag.ToArray().CopyTo(result, start);
        payload.AsSpan(end).CopyTo(result.AsSpan(start + tag.Length));
        return result;
    }

    /// <summary><c>SpawnerPreset</c> (<c>ItemSpawnerPresetWithOverrides</c>) holding only <c>Preset</c>: the other members keep the archetype's.</summary>
    private static void WritePresetTag(ByteWriter w, Func<string, FNameRef> name, int presetImport)
    {
        var inner = new ByteWriter(40);
        WriteObjectTag(inner, name, "Preset", presetImport);
        inner.FName(name("None"));
        w.FName(name("SpawnerPreset"));
        w.FName(name(StructPropertyType));
        w.I32(inner.Length);
        w.I32(0);
        w.FName(name("ItemSpawnerPresetWithOverrides"));
        w.Guid(default);
        w.U8(0);
        w.Raw(inner.ToArray());
    }

    /// <summary><paramref name="payload"/> with an <c>ArrayProperty</c> of objects inserted before the block's <c>None</c>.</summary>
    /// <summary>An <c>ArrayProperty</c> of objects (package indices).</summary>
    private static void WriteObjectArrayTag(ByteWriter tag, Func<string, FNameRef> name, string property, IReadOnlyList<int> items)
    {
        tag.FName(name(property));
        tag.FName(name("ArrayProperty"));
        tag.I32(4 + 4 * items.Count);
        tag.I32(0);
        tag.FName(name("ObjectProperty"));
        tag.U8(0);
        tag.I32(items.Count);
        foreach (var item in items)
        {
            tag.I32(item);
        }
    }

    private static byte[] InsertObjectArray(byte[] payload, PropertyBlock block, Func<string, FNameRef> name, string property, IReadOnlyList<int> items)
    {
        var tag = new ByteWriter(48 + 4 * items.Count);
        WriteObjectArrayTag(tag, name, property, items);
        var at = block.EndOffset - 8;
        var result = new byte[payload.Length + tag.Length];
        payload.AsSpan(0, at).CopyTo(result);
        tag.ToArray().CopyTo(result, at);
        payload.AsSpan(at).CopyTo(result.AsSpan(at + tag.Length));
        return result;
    }
}
