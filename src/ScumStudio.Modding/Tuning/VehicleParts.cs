using System.Buffers.Binary;
using System.Globalization;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Tuning;

/// <summary>
/// The parts a SCUM vehicle spawns with. A spawn preset (<c>VehiclePreset</c>: <c>Vehicles/SpawningPresets/ManualSpawn</c>,
/// <c>AutomaticSpawn</c>, <c>Purchase</c>) is a tree of <c>VehiclePresetNode</c> exports: <c>AttachmentClass</c> (soft path
/// of a <c>BPC_*</c> attachment Blueprint) and <c>Children</c>, one object reference per slot of that attachment
/// (<c>_slots[i]</c> of its CDO, whose <c>PossibleAttachmentClasses</c> are the parts that fit), <c>None</c> = the slot
/// spawns empty. One <see cref="TunableKind.Part"/> tunable per slot: export = the parent node, path = <c>Children[i]</c>,
/// value = the class path or empty.
/// </summary>
public static class VehicleParts
{
    /// <summary>Folder of every vehicle spawn preset.</summary>
    public const string PresetFolder = ModdableAssets.ConZ + "Vehicles/SpawningPresets/";

    private const string NodeClass = "VehiclePresetNode";

    /// <summary>True for an edit this class writes (a preset node's <c>Children[i]</c>).</summary>
    public static bool IsEdit(string path) => path.StartsWith("Children[", StringComparison.Ordinal);

    /// <summary>Every slot of every node of <paramref name="preset"/>, its part and the parts that fit.</summary>
    /// <param name="preset">A vehicle spawn preset.</param>
    /// <param name="load">Loads an attachment package (stock or clone) by package path, null when missing.</param>
    public static IReadOnlyList<Tunable> Read(CookedPackage preset, Func<string, CookedPackage?> load)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(load);
        var keys = TunableReader.ExportKeys(preset);
        var result = new List<Tunable>();
        for (var i = 0; i < preset.Exports.Count; i++)
        {
            if (preset.GetExportClassName(i) != NodeClass || Node(preset, i) is not ({ } own, { } children))
            {
                continue;
            }

            var slots = SlotsOf(own, load);
            for (var s = 0; s < children.Items.Count; s++)
            {
                var current = children.Items[s] is ObjectValue { Index: > 0 } o ? Node(preset, o.Index - 1).Class ?? string.Empty : string.Empty;
                var fits = s < slots.Count ? slots[s] : [];
                var choices = new List<string> { string.Empty };
                choices.AddRange(fits.Where(f => !choices.Contains(f, StringComparer.OrdinalIgnoreCase)));
                if (!choices.Contains(current, StringComparer.OrdinalIgnoreCase))
                {
                    choices.Add(current);
                }

                result.Add(new Tunable(keys[i], $"Children[{s.ToString(CultureInfo.InvariantCulture)}]", TunableKind.Part, current)
                {
                    Name = SlotLabel(fits.Count > 0 ? fits : current.Length > 0 ? [current] : [], s),
                    ExportClass = own, // the part that holds the slot (a 3D view highlights it for an empty slot)
                    Group = Label(own),
                    Choices = choices,
                });
            }
        }

        return result;
    }

    /// <summary>Readable part name: <c>/Game/…/BPC_WolfsWagen_Door_FrontLeft.BPC_WolfsWagen_Door_FrontLeft_C</c> → <c>Door FrontLeft</c>.</summary>
    public static string Label(string classPath)
    {
        var leaf = PackageMap.Leaf(PackageOf(classPath));
        leaf = leaf.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase) ? leaf[4..] : leaf;
        var token = leaf.IndexOf('_');
        return (token > 0 && token < leaf.Length - 1 ? leaf[(token + 1)..] : leaf).Replace('_', ' ');
    }

    /// <summary>The package of an object path (<c>/Game/A/B.B_C</c> → <c>/Game/A/B</c>).</summary>
    public static string PackageOf(string objectPath)
    {
        var dot = objectPath.LastIndexOf('.');
        return dot > objectPath.LastIndexOf('/') ? objectPath[..dot] : objectPath;
    }

    /// <summary>The attachment classes a preset spawns (the soft paths of its nodes), for a 3D view of the vehicle.</summary>
    public static IReadOnlyList<string> Spawned(CookedPackage preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var reachable = new List<string>();
        var stack = new Stack<int>(Enumerable.Range(0, preset.Exports.Count).Where(i => preset.GetExportClassName(i) == NodeClass && preset.Exports[i].OuterIndex > 0
            && preset.GetExportClassName(preset.Exports[i].OuterIndex - 1) != NodeClass));
        while (stack.TryPop(out var i))
        {
            if (Node(preset, i) is not ({ } own, var children))
            {
                continue;
            }

            reachable.Add(own);
            foreach (var child in children?.Items.OfType<ObjectValue>().Where(c => c.Index > 0) ?? [])
            {
                stack.Push(child.Index - 1);
            }
        }

        return reachable;
    }

    /// <summary>
    /// Writes slot edits (<see cref="TunableEdit.Value"/> = class path or empty) into <paramref name="preset"/>: an emptied
    /// slot's reference becomes <c>None</c>; a filled slot's node gets the new <c>AttachmentClass</c> (its own children stay);
    /// an empty slot gets a new <c>VehiclePresetNode</c> export. New soft paths are added to the name table.
    /// </summary>
    /// <exception cref="InvalidOperationException">A missing node or slot, or a value that is not a class path.</exception>
    public static PackageBytes Apply(CookedPackage preset, IEnumerable<TunableEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(edits);
        var list = edits.ToList();
        var keys = TunableReader.ExportKeys(preset);
        var tables = PackageTables.Of(preset, 0);
        var payloads = Enumerable.Range(0, preset.Exports.Count).Select(i => preset.GetExportData(i).ToArray()).ToList();
        var nodeName = tables.Name(NodeClass);
        var nextNumber = preset.Exports.Where(e => e.ObjectName.Index == nodeName.Index).Select(e => e.ObjectName.Number).DefaultIfEmpty(0).Max() + 1;
        var childGroup = ChildDependencyGroup(preset, tables);

        foreach (var edit in list)
        {
            var parent = Array.IndexOf(keys, edit.Export);
            if (parent < 0 || preset.GetExportClassName(parent) != NodeClass || Node(preset, parent).Children is not { } children)
            {
                throw new InvalidOperationException($"{preset.BasePath}: no preset node '{edit.Export}'.");
            }

            var slot = SlotIndex(edit.Path);
            if (slot < 0 || slot >= children.Items.Count || children.Items[slot] is not ObjectValue item)
            {
                throw new InvalidOperationException($"{preset.BasePath}: node '{edit.Export}' has no slot '{edit.Path}'.");
            }

            var value = edit.Value.Trim();
            if (value.Length == 0)
            {
                BinaryPrimitives.WriteInt32LittleEndian(payloads[parent].AsSpan(item.Offset), 0);
                continue;
            }

            if (!value.StartsWith('/') || !value.Contains('.', StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"'{value}' is not an attachment class path.");
            }

            tables.Name(PackageOf(value));
            var path = tables.Name(value);
            if (item.Index > 0)
            {
                var child = item.Index - 1;
                var soft = preset.ReadProperties(child).Find("AttachmentClass")?.Value as SoftObjectValue
                           ?? throw new InvalidOperationException($"{preset.BasePath}: node '{keys[child]}' has no part class.");
                BinaryPrimitives.WriteInt32LittleEndian(payloads[child].AsSpan(soft.Offset), path.Index);
                BinaryPrimitives.WriteInt32LittleEndian(payloads[child].AsSpan(soft.Offset + 4), path.Number);
                continue;
            }

            // An empty slot: a new leaf node like the parent (same class, template and flags) under the parent.
            var created = tables.Exports.Count;
            tables.Exports.Add(tables.Exports[parent] with
            {
                OuterIndex = parent + 1,
                ObjectName = new FNameRef(nodeName.Index, nextNumber++),
                FirstExportDependency = -1,
                SerializationBeforeSerializationDependencies = 0,
                CreateBeforeSerializationDependencies = 0,
                SerializationBeforeCreateDependencies = 0,
                CreateBeforeCreateDependencies = 0,
            });
            payloads.Add(LeafNode(tables, path));
            var parentGroups = tables.DependencyGroups(parent);
            for (var g = 0; g < 4; g++)
            {
                // The parent's own class/template dependencies, its outer swapped for the parent itself.
                foreach (var d in parentGroups[g].Where(d => d < 0 || d == tables.Exports[parent].OuterIndex))
                {
                    tables.AddDependency(created, g, d == tables.Exports[parent].OuterIndex ? parent + 1 : d);
                }
            }

            if (childGroup >= 0)
            {
                tables.AddDependency(parent, childGroup, created + 1);
            }

            BinaryPrimitives.WriteInt32LittleEndian(payloads[parent].AsSpan(item.Offset), created + 1);
        }

        var bytes = PackageWriter.Build(PackageWriter.ToBuildInput(preset) with
        {
            Names = tables.Names,
            NameIsWide = tables.Wide,
            Imports = tables.Imports,
            Exports = tables.Exports,
            PreloadDependencies = tables.Preload,
            ExportData = payloads.Select(p => (ReadOnlyMemory<byte>)p).ToArray(),
        });

        var check = CookedPackage.Parse(bytes.UAsset, bytes.UExp, preset.UBulk, preset.BasePath);
        var written = Read(check, _ => null).ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        foreach (var edit in list)
        {
            if (!written.TryGetValue(edit.Export + "|" + edit.Path, out var now) || !string.Equals(now, edit.Value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{preset.BasePath}: slot '{edit.Export}|{edit.Path}' did not read back as '{edit.Value}'.");
            }
        }

        return bytes;
    }

    private static (string? Class, ArrayValue? Children) Node(CookedPackage package, int exportIndex)
    {
        var block = package.ReadProperties(exportIndex);
        return (block.Find("AttachmentClass")?.Value is SoftObjectValue soft && soft.AssetPath.Length > 0 && soft.AssetPath != "None" ? soft.AssetPath : null,
            block.Find("Children")?.Value as ArrayValue);
    }

    /// <summary>The <c>PossibleAttachmentClasses</c> of each <c>_slots</c> entry of the attachment's CDO (empty when not stored).</summary>
    private static List<List<string>> SlotsOf(string classPath, Func<string, CookedPackage?> load)
    {
        var slots = new List<List<string>>();
        if (load(PackageOf(classPath)) is not { } package)
        {
            return slots;
        }

        var cdo = Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.ResolveName(package.Exports[i].ObjectName).StartsWith("Default__", StringComparison.Ordinal), -1);
        if (cdo < 0 || package.ReadProperties(cdo).Find("_slots")?.Value is not ArrayValue array)
        {
            return slots;
        }

        foreach (var slot in array.Items.OfType<StructValue>())
        {
            slots.Add(slot.Properties.FirstOrDefault(p => p.Name == "PossibleAttachmentClasses")?.Value is ArrayValue classes
                ? classes.Items.OfType<SoftObjectValue>().Select(c => c.AssetPath).Where(c => c.Length > 0 && c != "None").ToList()
                : []);
        }

        return slots;
    }

    /// <summary>One part: its label; several: the words that tell them apart (<c>ArmorHeavy / ArmorLight</c>).</summary>
    private static string SlotLabel(IReadOnlyList<string> fits, int slot)
    {
        if (fits.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Slot {slot + 1}");
        }

        var labels = fits.Select(Label).ToList();
        if (labels.Count == 1)
        {
            return labels[0];
        }

        var words = labels.Select(l => l.Split(' ')).ToList();
        var common = words.Skip(1).Aggregate(words[0].AsEnumerable(), (a, w) => a.Intersect(w)).ToHashSet();
        var parts = words.Select(w => string.Join(' ', w.Where(x => !common.Contains(x)))).Where(x => x.Length > 0).Distinct().ToList();
        return parts.Count > 1 ? string.Join(" / ", parts) : labels[0];
    }

    private static int SlotIndex(string path) =>
        path.Length > "Children[]".Length && path[^1] == ']' && int.TryParse(path["Children[".Length..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1;

    /// <summary>The preload group in which a node lists its child nodes (-1: nodes do not list them).</summary>
    private static int ChildDependencyGroup(CookedPackage preset, PackageTables tables)
    {
        for (var i = 0; i < preset.Exports.Count; i++)
        {
            if (preset.GetExportClassName(i) != NodeClass)
            {
                continue;
            }

            var groups = tables.DependencyGroups(i);
            for (var g = 0; g < 4; g++)
            {
                if (groups[g].Any(d => d > 0 && preset.Exports[d - 1].OuterIndex == i + 1))
                {
                    return g;
                }
            }
        }

        return -1;
    }

    /// <summary>A leaf node's tagged properties: <c>AttachmentClass</c> (SoftObjectProperty), <c>None</c>, and the 4-byte tail every node has.</summary>
    private static byte[] LeafNode(PackageTables tables, FNameRef path)
    {
        var bytes = new byte[49];
        void Name(int at, FNameRef n)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), n.Index);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 4), n.Number);
        }

        Name(0, tables.Name("AttachmentClass"));
        Name(8, tables.Name("SoftObjectProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 12); // FName + empty FString sub-path
        Name(25, path);
        Name(37, tables.Name("None"));
        return bytes;
    }
}
