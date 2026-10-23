using System.Buffers.Binary;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Modding.Tuning;

/// <summary>
/// Values a Blueprint takes from its parent class without storing them. A cooked class default object (CDO) holds only
/// what differs from its parent, so a katana's entity setup (<c>2H_Katana_ES</c>, parent <c>/Script/SCUM.ItemEntitySetup</c>)
/// has no <c>MaxHealth</c>, and <c>2H_Katana1</c> has none of the values it shares with its parent <c>2H_Katana</c>
/// (CwHedgehog: "I want to change this weapon's MaxHealth. It seems to be inherited from a parent?"). Such a value is
/// written as a new tag of the parent's type into the package's own CDO; the parent and its other children stay as they are.
/// </summary>
public static class InheritedValues
{
    /// <summary>
    /// Values of the game's C++ classes that no file of a child stores, with the type the game files prove: 61 entity
    /// setups whose parent is <c>ItemEntitySetup</c> store <c>MaxHealth</c> as a FloatProperty (an oven 4500, a chest 1500),
    /// no weapon's does. The C++ default is not in any file, so its value is shown as unknown.
    /// </summary>
    public static IReadOnlyList<(string Class, string Name, TunableKind Kind)> Native { get; } =
    [
        ("/Script/SCUM.ItemEntitySetup", "MaxHealth", TunableKind.Float),
    ];

    /// <summary>
    /// Values of the game's C++ classes that no file stores, whose default the game's own code sets (read from the
    /// constructor in <c>SCUM.exe</c> and <c>SCUMServer.exe</c>, SCUM 1.3.4, 2026-10-22). The planes (<c>BPC_Kinglet_Duster</c>,
    /// <c>BPC_Kinglet_Mariner</c>, parent <c>/Script/SCUM.Airplane</c>) store <c>_maxVelocity</c> (8332.5 cm/s, 300 km/h) but not
    /// <c>_propellerForce</c>, the pull of the propeller: the constructor sets it to 180000, and each frame the game pushes the
    /// plane forward at the propeller with <c>_propellerForce</c> x throttle x (this plane's <c>_maxVelocity</c> / its class
    /// default's), and none above <c>_airplaneHeightToStallInMeters</c> (r40, Discord unknownskill: "I change the speed of the
    /// plane but it does not apply"; the wheels' engine values move nothing in the air).
    /// </summary>
    public static IReadOnlyList<(string Class, string Name, TunableKind Kind, string Default)> NativeDefaults { get; } =
    [
        ("/Script/SCUM.Airplane", "_propellerForce", TunableKind.Float, "180000"),
    ];

    /// <summary>The grid inventory of an entity setup: what a bag, a vest or a pair of trousers holds.</summary>
    public const string GridInventory = "/Script/SCUM.EntityGridInventoryComponentSetup";

    /// <summary>
    /// Values of a default subobject of an entity setup's class defaults (its <see cref="GridInventory"/>, 444 of the game's
    /// item setups have one) that the subobject takes from its parent's copy or from the game's code when it does not store
    /// them, with the tag type the game files prove and the default the constructor sets (SCUM 1.3.4: 1 x 1 cells and a weight
    /// limit of 10). 195 of the 444 store <c>NumRows</c>: a colour variant (<c>Backpack_02_02_ES</c>) takes its grid from its
    /// parent (<c>Backpack_02_01_ES</c>), and the Improvised Courier Backpack stores 6 x 6 but no weight, so it holds the
    /// code's 10 (r40, Discord unknownskill: "changing a backpack, how much size it has inside, load capacity").
    /// </summary>
    public static IReadOnlyList<(string Class, string Name, TunableKind Kind, string TagType, string Default)> SubobjectNative { get; } =
    [
        (GridInventory, "NumRows", TunableKind.UInt, "ByteProperty", "1"),
        (GridInventory, "NumColumns", TunableKind.UInt, "ByteProperty", "1"),
        (GridInventory, "MaxContainedWeight", TunableKind.Float, "FloatProperty", "10"),
        (GridInventory, "HasContainedWeightLimit", TunableKind.Bool, "BoolProperty", "true"),
    ];

    /// <summary>
    /// Base element values a craftable's clone may get without its template storing them (the Craftables export only; the
    /// value pages do not list them), each with the type the game files prove (SCUM 1.3.3, 333 base elements): 31 store
    /// <c>_canBePlacedWithoutFlag</c> true (BoolProperty, so the C++ default is false: a flag is needed), 71
    /// <c>_mustBePlacedUpright</c> true, 65 <c>_placementHeightType</c> <c>HighestPointHeight</c> (EnumProperty; none stores
    /// another value). The improvised chair stores none of them.
    /// </summary>
    public static IReadOnlyList<(string Class, string Name, TunableKind Kind)> ElementNative { get; } =
    [
        .. new[] { "/Script/SCUM.BaseBuildingComponent", "/Script/SCUM.BaseBuildingItemComponent" }.SelectMany(c => new[]
        {
            (c, "_canBePlacedWithoutFlag", TunableKind.Bool),
            (c, "_mustBePlacedUpright", TunableKind.Bool),
            (c, "_placementHeightType", TunableKind.Enum),
        }),
    ];

    /// <summary>
    /// Values of a placed thing's recipe (a data asset of the native <c>PlaceableCraftingRecipe</c>, SDK build 149664) a copy
    /// of a game recipe may get without storing them (the Craftables export only), all Vector structs the game's own recipes
    /// store: <c>PlacementLocationOffset</c> (2 recipes, the Woodland pack's curtains: -5, 0, 20 and 0, 0, -30),
    /// <c>CollisionMeshScale</c> (the ghost's collision mesh, scaled about its pivot: wall lamps 0.6, the workbench 1, 1, 0.9,
    /// so a thing placed against a wall or the ground does not count as overlapping it) and
    /// <c>BoundsExtensionWhilePlacingMin</c>/<c>Max</c> (the chair's and the bed's -5, -5, 0: its box drawn in 5 cm).
    /// </summary>
    public static IReadOnlyList<(string Class, string Name, TunableKind Kind)> RecipeNative { get; } =
    [
        ("/Script/SCUM.PlaceableCraftingRecipe", "PlacementLocationOffset", TunableKind.Vector),
        ("/Script/SCUM.PlaceableCraftingRecipe", "CollisionMeshScale", TunableKind.Vector),
        ("/Script/SCUM.PlaceableCraftingRecipe", "BoundsExtensionWhilePlacingMin", TunableKind.Vector),
        ("/Script/SCUM.PlaceableCraftingRecipe", "BoundsExtensionWhilePlacingMax", TunableKind.Vector),
    ];

    /// <summary>
    /// Values of a zone's settings (a data asset of the native <c>EncounterZoneData</c>) a copy of the game's settings may get
    /// without storing them (the export only): <c>SingleEncounterPerZone</c>, a BoolProperty the game's NPC-only sub-zone
    /// settings store true (<c>HTZ_Military_TV_Bunker_NPC</c>, <c>HTZ_Military_TV_Bunker_Abandoned_NPC</c>) and the others
    /// leave out (false): one encounter at a time in the zone. The NPC area of a replaced robot takes it (r39).
    /// </summary>
    public static IReadOnlyList<(string Class, string Name, TunableKind Kind)> ZoneNative { get; } =
    [
        ("/Script/SCUM.EncounterZoneData", "SingleEncounterPerZone", TunableKind.Bool),
    ];

    /// <summary>The tag type written for each kind; other kinds are not offered.</summary>
    private static readonly Dictionary<TunableKind, string> TagTypes = new()
    {
        [TunableKind.Float] = "FloatProperty",
        [TunableKind.Double] = "DoubleProperty",
        [TunableKind.Int] = "IntProperty",
        [TunableKind.Bool] = "BoolProperty",
        [TunableKind.Enum] = "EnumProperty",
        [TunableKind.Vector] = "StructProperty",
    };

    /// <summary>
    /// The values <paramref name="package"/>'s class default object inherits without storing them: each plain number or
    /// bool a Blueprint parent's CDO stores (nearest parent first, with its value), then the <see cref="Native"/> values of
    /// the first C++ class in the chain (value empty: not in the files). Each is a <see cref="Tunable"/> of the package's
    /// own CDO with <see cref="Tunable.InheritedFrom"/> set; empty when the package has no Blueprint class.
    /// </summary>
    /// <param name="package">A Blueprint package (an item, its entity setup, a clone of either).</param>
    /// <param name="readPackage">Reads a parent package by its package path, or returns null when it cannot.</param>
    /// <param name="elementValues">
    /// Also the <see cref="ElementNative"/> values, and for a recipe or a zone's settings (data assets, no Blueprint class)
    /// the <see cref="RecipeNative"/> and <see cref="ZoneNative"/> ones (the exports write them).
    /// </param>
    public static IReadOnlyList<Tunable> Read(CookedPackage package, Func<string, CookedPackage?> readPackage, bool elementValues = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(readPackage);
        if (FindClass(package) is not { } own)
        {
            return elementValues ? DataAssetValues(package) : [];
        }

        var keys = TunableReader.ExportKeys(package);
        var export = keys[own.Cdo];
        var className = package.ResolveName(package.Exports[own.Class].ObjectName);
        var group = TunableReader.FriendlyExport(export);
        var seen = new HashSet<string>(StoredNames(package, own.Cdo), StringComparer.OrdinalIgnoreCase);
        var result = new List<Tunable>();
        Tunable Make(string name, TunableKind kind, string value, string parent, string? tagType = null) => new(export, name, kind, value)
        {
            Name = name,
            ExportClass = className,
            Group = group,
            InheritedFrom = parent,
            TagType = tagType ?? TagTypes.GetValueOrDefault(kind),
        };

        var super = package.GetFullPath(package.Exports[own.Class].SuperIndex);
        var parents = new List<(string Class, CookedPackage Package, int Cdo)>();
        var reachesCode = false;
        for (var depth = 0; depth < 16 && super.Length > 0; depth++)
        {
            if (super.StartsWith("/Script/", StringComparison.Ordinal))
            {
                result.AddRange(Native.Concat(elementValues ? ElementNative : []).Where(n => n.Class == super && seen.Add(n.Name)).Select(n => Make(n.Name, n.Kind, string.Empty, super)));
                result.AddRange(NativeDefaults.Where(n => n.Class == super && seen.Add(n.Name)).Select(n => Make(n.Name, n.Kind, n.Default, super)));
                reachesCode = true;
                break;
            }

            var dot = super.LastIndexOf('.');
            if (dot <= 0 || readPackage(super[..dot]) is not { } parent || FindClass(parent, super[(dot + 1)..]) is not { } theirs)
            {
                break;
            }

            foreach (var tag in parent.ReadProperties(theirs.Cdo).Properties)
            {
                if (tag.ArrayIndex == 0 && KindOf(tag) is { } kind && seen.Add(tag.Name))
                {
                    result.Add(Make(tag.Name, kind, Describe(tag), super, tag.Type));
                }
            }

            parents.Add((super, parent, theirs.Cdo));
            super = parent.GetFullPath(parent.Exports[theirs.Class].SuperIndex);
        }

        result.AddRange(SubobjectValues(package, own.Cdo, keys, parents, reachesCode));
        return result;
    }

    /// <summary>
    /// The <see cref="SubobjectNative"/> values each default subobject of the CDO (a <see cref="GridInventory"/>) does not
    /// store: its parents' copy of the same subobject (nearest first), then the game's code when the chain reaches it.
    /// </summary>
    private static List<Tunable> SubobjectValues(CookedPackage package, int cdo, string[] keys, List<(string Class, CookedPackage Package, int Cdo)> parents, bool reachesCode)
    {
        var result = new List<Tunable>();
        for (var i = 0; i < package.Exports.Count; i++)
        {
            var cls = package.GetFullPath(package.Exports[i].ClassIndex);
            var natives = SubobjectNative.Where(n => n.Class == cls).ToList();
            if (natives.Count == 0 || package.Exports[i].OuterIndex != cdo + 1)
            {
                continue;
            }

            var name = package.Exports[i].ObjectName.Format(package.Names);
            var seen = new HashSet<string>(StoredNames(package, i), StringComparer.OrdinalIgnoreCase);
            Tunable Make(string property, TunableKind kind, string value, string from, string tagType) => new(keys[i], property, kind, value)
            {
                Name = property,
                ExportClass = package.GetExportClassName(i),
                Group = TunableReader.FriendlyExport(keys[i]),
                InheritedFrom = from,
                TagType = tagType,
            };

            foreach (var (parentClass, parent, parentCdo) in parents)
            {
                var theirs = Enumerable.Range(0, parent.Exports.Count).FirstOrDefault(j => parent.Exports[j].OuterIndex == parentCdo + 1
                    && string.Equals(parent.Exports[j].ObjectName.Format(parent.Names), name, StringComparison.OrdinalIgnoreCase)
                    && parent.GetFullPath(parent.Exports[j].ClassIndex) == cls, -1);
                if (theirs < 0)
                {
                    continue;
                }

                foreach (var tag in parent.ReadProperties(theirs).Properties)
                {
                    var k = natives.FindIndex(n => n.Name == tag.Name);
                    if (tag.ArrayIndex == 0 && k >= 0 && seen.Add(tag.Name))
                    {
                        result.Add(Make(tag.Name, natives[k].Kind, Describe(tag), parentClass, natives[k].TagType));
                    }
                }
            }

            if (reachesCode)
            {
                result.AddRange(natives.Where(n => seen.Add(n.Name)).Select(n => Make(n.Name, n.Kind, n.Default, cls, n.TagType)));
            }
        }

        return result;
    }

    /// <summary>
    /// Writes each inherited value into <paramref name="package"/>'s own CDO: a new tag of the value's kind (a name the
    /// package lacks is added to its name table), placed before the property list's end, then read back.
    /// </summary>
    /// <param name="package">The package (an item or entity setup, stock or a clone).</param>
    /// <param name="values">Each value (from <see cref="Read"/>) and its new text.</param>
    /// <exception cref="InvalidOperationException">A value is not inherited, of a kind that cannot be written, already stored, invalid, or did not read back.</exception>
    public static PackageBytes Add(CookedPackage package, IReadOnlyList<(Tunable Value, string Text)> values)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(values);
        var keys = TunableReader.ExportKeys(package);
        var names = package.Names.ToList();
        var wide = package.NameEntries.Select(n => n.IsWide).ToList();
        FNameRef Name(string name)
        {
            // As the cooker stores it: "Value_2" is the name "Value" with number 3 (FNameRef.Split).
            var (text, number) = FNameRef.Split(name);
            var index = names.IndexOf(text);
            if (index < 0)
            {
                index = names.Count;
                names.Add(text);
                wide.Add(!text.All(char.IsAscii));
            }

            return new FNameRef(index, number);
        }

        var payloads = Enumerable.Range(0, package.Exports.Count).Select(i => package.GetExportData(i).ToArray()).ToArray();
        foreach (var group in values.GroupBy(v => v.Value.Export, StringComparer.Ordinal))
        {
            var exportIndex = Array.IndexOf(keys, group.Key);
            if (exportIndex < 0 || !(group.Key.StartsWith("Default__", StringComparison.Ordinal)
                                     || group.All(v => IsDataAssetValue(package, exportIndex, v.Value) || SubobjectTagType(package, exportIndex, v.Value) is not null)))
            {
                throw new InvalidOperationException($"{package.BasePath}: '{group.Key}' is not this package's class defaults.");
            }

            var block = package.ReadProperties(exportIndex);
            var none = block.EndOffset - 8;
            if (none < 0 || BinaryPrimitives.ReadInt32LittleEndian(payloads[exportIndex].AsSpan(none)) != package.FindName("None").Index)
            {
                throw new InvalidOperationException($"{package.BasePath}: the property list of '{group.Key}' does not end as expected.");
            }

            var tags = new List<byte>();
            foreach (var (value, text) in group)
            {
                var type = SubobjectTagType(package, exportIndex, value) ?? (TagTypes.TryGetValue(value.Kind, out var known) ? known : null);
                if (value.InheritedFrom is null || type is null || value.Path.Contains('.') || value.Path.Contains('['))
                {
                    throw new InvalidOperationException($"{package.BasePath}: '{value.Path}' is not an inherited value that can be written.");
                }

                if (block.Find(value.Path) is not null)
                {
                    throw new InvalidOperationException($"{package.BasePath}: '{value.Path}' is stored already; change it as a stored value.");
                }

                tags.AddRange(Tag(Name(value.Path), Name(type), type, value.Kind, text, Name));
            }

            var payload = payloads[exportIndex];
            var grown = new byte[payload.Length + tags.Count];
            payload.AsSpan(0, none).CopyTo(grown);
            tags.CopyTo(grown, none);
            payload.AsSpan(none).CopyTo(grown.AsSpan(none + tags.Count));
            payloads[exportIndex] = grown;
        }

        var bytes = PackageWriter.Build(PackageWriter.ToBuildInput(package) with
        {
            Names = names,
            NameIsWide = wide,
            ExportData = payloads.Select(p => (ReadOnlyMemory<byte>)p).ToArray(),
        });

        var check = TunableReader.Read(CookedPackage.Parse(bytes.UAsset, bytes.UExp, package.UBulk, package.BasePath)).ToDictionary(t => t.Key, StringComparer.Ordinal);
        foreach (var (value, text) in values)
        {
            if (!check.TryGetValue(value.Key, out var written) || !TunableValue.AreEqual(value.Kind, written.Value, text))
            {
                throw new InvalidOperationException($"{package.BasePath}: '{value.Key}' did not read back as '{text}' after it was added.");
            }
        }

        return bytes;
    }

    /// <summary>
    /// The <see cref="RecipeNative"/> values a data asset's top-level export of that class does not store (value empty: the
    /// C++ default, not in the files).
    /// </summary>
    private static IReadOnlyList<Tunable> DataAssetValues(CookedPackage package)
    {
        var keys = TunableReader.ExportKeys(package);
        var result = new List<Tunable>();
        for (var i = 0; i < package.Exports.Count; i++)
        {
            var cls = package.Exports[i].OuterIndex == 0 ? package.GetFullPath(package.Exports[i].ClassIndex) : string.Empty;
            var natives = RecipeNative.Concat(ZoneNative).Where(n => n.Class == cls).ToList();
            if (natives.Count == 0)
            {
                continue;
            }

            var stored = new HashSet<string>(StoredNames(package, i), StringComparer.OrdinalIgnoreCase);
            result.AddRange(natives.Where(n => !stored.Contains(n.Name)).Select(n => new Tunable(keys[i], n.Name, n.Kind, string.Empty)
            {
                Name = n.Name,
                ExportClass = package.GetExportClassName(i),
                Group = TunableReader.FriendlyExport(keys[i]),
                InheritedFrom = cls,
                TagType = TagTypes.GetValueOrDefault(n.Kind),
            }));
        }

        return result;
    }

    /// <summary>
    /// The tag type of a <see cref="SubobjectNative"/> value of the default subobject <paramref name="exportIndex"/> (outer:
    /// the class defaults, class: the value's), or null.
    /// </summary>
    private static string? SubobjectTagType(CookedPackage package, int exportIndex, Tunable value)
    {
        var outer = package.Exports[exportIndex].OuterIndex;
        if (value.InheritedFrom is null || outer <= 0 || !package.ResolveName(package.Exports[outer - 1].ObjectName).StartsWith("Default__", StringComparison.Ordinal))
        {
            return null;
        }

        var cls = package.GetFullPath(package.Exports[exportIndex].ClassIndex);
        return SubobjectNative.FirstOrDefault(n => n.Class == cls && n.Name == value.Path && n.Kind == value.Kind).TagType;
    }

    /// <summary>A <see cref="RecipeNative"/> value of the top-level data asset export <paramref name="exportIndex"/>.</summary>
    private static bool IsDataAssetValue(CookedPackage package, int exportIndex, Tunable value) =>
        package.Exports[exportIndex].OuterIndex == 0 && value.InheritedFrom == package.GetFullPath(package.Exports[exportIndex].ClassIndex)
        && RecipeNative.Concat(ZoneNative).Any(n => n.Class == value.InheritedFrom && n.Name == value.Path && n.Kind == value.Kind);

    /// <summary>The package's Blueprint class export and its CDO (<c>Default__X_C</c>), or null.</summary>
    private static (int Class, int Cdo)? FindClass(CookedPackage package, string? className = null)
    {
        for (var i = 0; i < package.Exports.Count; i++)
        {
            var name = package.ResolveName(package.Exports[i].ObjectName);
            if (package.Exports[i].OuterIndex != 0 || !package.GetExportClassName(i).EndsWith("GeneratedClass", StringComparison.Ordinal)
                || (className is not null && !string.Equals(name, className, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var cdo = Enumerable.Range(0, package.Exports.Count).FirstOrDefault(j => package.ResolveName(package.Exports[j].ObjectName) == "Default__" + name, -1);
            return cdo < 0 ? null : (i, cdo);
        }

        return null;
    }

    private static IEnumerable<string> StoredNames(CookedPackage package, int exportIndex)
    {
        try
        {
            return package.ReadProperties(exportIndex).Properties.Select(p => p.Name).ToList();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException or NotSupportedException)
        {
            return [];
        }
    }

    private static TunableKind? KindOf(PropertyTag tag) => tag.Type switch
    {
        "FloatProperty" => TunableKind.Float,
        "DoubleProperty" => TunableKind.Double,
        "IntProperty" => TunableKind.Int,
        "BoolProperty" => TunableKind.Bool,
        _ => null,
    };

    private static string Describe(PropertyTag tag) => tag.Value switch
    {
        FloatValue f => TunableValue.Format(f.Value),
        DoubleValue d => d.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
        IntValue v => v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ByteValue v => v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        BoolValue b => b.Value ? "true" : "false",
        var v => v.ToString() ?? string.Empty,
    };

    /// <summary>
    /// One FPropertyTag and its value (UE 4.27: name, type, size, array index, [bool | enum name | struct name + zero struct
    /// guid], no property guid, value). An enum's text is <c>EnumName::Value</c>
    /// (<c>EConZPlacementHeightType::HighestPointHeight</c>), its value that FName; a Vector is "x, y, z" (3 floats).
    /// </summary>
    private static byte[] Tag(FNameRef name, FNameRef type, string typeName, TunableKind kind, string text, Func<string, FNameRef> nameOf)
    {
        byte[] value;
        byte? flag = null;
        FNameRef? enumName = null;
        FNameRef? structName = null;
        try
        {
            switch (kind)
            {
                case TunableKind.UInt when typeName == "ByteProperty":
                    // A plain byte (a grid's NumRows): the tag names no enum (None), the value is one byte.
                    enumName = nameOf("None");
                    value = [byte.Parse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture)];
                    break;
                case TunableKind.Vector:
                    var xyz = TunableValue.ParseFloats(text, 3);
                    if (!xyz.All(float.IsFinite))
                    {
                        throw new FormatException("a vector needs three finite numbers");
                    }

                    structName = nameOf("Vector");
                    value = new byte[12];
                    for (var i = 0; i < 3; i++)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(value.AsSpan(i * 4), xyz[i]);
                    }

                    break;
                case TunableKind.Enum:
                    var separator = text.IndexOf("::", StringComparison.Ordinal);
                    if (separator <= 0 || separator + 2 >= text.Length || text.Trim() != text)
                    {
                        throw new FormatException("an enum value is written as EnumName::Value");
                    }

                    enumName = nameOf(text[..separator]);
                    var entry = nameOf(text);
                    value = new byte[8];
                    BinaryPrimitives.WriteInt32LittleEndian(value, entry.Index);
                    BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4), entry.Number);
                    break;
                case TunableKind.Float:
                    value = new byte[4];
                    BinaryPrimitives.WriteSingleLittleEndian(value, TunableValue.ParseFloat(text));
                    break;
                case TunableKind.Double:
                    value = new byte[8];
                    BinaryPrimitives.WriteDoubleLittleEndian(value, double.Parse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case TunableKind.Int:
                    value = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(value, int.Parse(text.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                default:
                    value = [];
                    flag = TunableValue.ParseBool(text) ? (byte)1 : (byte)0;
                    break;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw new InvalidOperationException($"'{text}' is not a value of {kind}: {ex.Message}", ex);
        }

        var tag = new byte[16 + 8 + (flag is null ? 0 : 1) + (enumName is null ? 0 : 8) + (structName is null ? 0 : 8 + 16) + 1 + value.Length];
        var span = tag.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, name.Index);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], name.Number);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], type.Index);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], type.Number);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], value.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], 0);
        var at = 24;
        if (flag is { } b)
        {
            tag[at++] = b;
        }

        if (enumName is { } enumType)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[at..], enumType.Index);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 4)..], enumType.Number);
            at += 8;
        }

        if (structName is { } structType)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[at..], structType.Index);
            BinaryPrimitives.WriteInt32LittleEndian(span[(at + 4)..], structType.Number);
            at += 8 + 16; // the struct guid stays zero, as the cooker writes it
        }

        tag[at++] = 0; // no property guid
        value.CopyTo(tag, at);
        return tag;
    }
}
