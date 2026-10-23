using System.Buffers.Binary;
using System.Globalization;
using ScumStudio.Core.Mathematics;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Tuning;

/// <summary>
/// A vehicle part's own Blueprint (<c>BPC_&lt;Token&gt;_&lt;Part&gt;</c>, a <c>VehicleAttachment</c>) on a clone: its model
/// (<c>MeshSetup.Mesh</c>, a soft path; a static mesh stays static and a skeletal one skeletal, because doors and hoods
/// animate on their skeleton), the socket it hangs on (<c>ParentSocket</c>) and the slots of the parts it holds
/// (<c>_slots</c>: a new slot is a copy of a donor slot that takes one new class). Edits: <c>#MeshSetup.Mesh</c>,
/// <c>#ParentSocket</c>, <c>#Slots[n]</c> = <c>class path|donor slot</c> (n = the slot count: slots are only appended),
/// <c>#MeshSetup.Offset</c> = where the model sits on the socket (<see cref="PartModelFit"/>: a new model in place of the
/// game's). A part added to the car is a rename-clone of a donor part of the same vehicle that carries no gameplay system
/// (<see cref="DefaultDonor"/>) under <see cref="CustomName"/>, given these edits.
/// </summary>
public static class VehicleAttachmentEdits
{
    /// <summary>Path of the part's model.</summary>
    public const string MeshPath = "#MeshSetup.Mesh";

    /// <summary>
    /// Path of the transform between the part's socket and its model (<c>MeshSetup.Offset</c>, a tagged <c>Transform</c>;
    /// value <see cref="PartModelFit.Format"/>).
    /// </summary>
    public const string OffsetPath = "#MeshSetup.Offset";

    /// <summary>Path of the socket the part hangs on.</summary>
    public const string SocketPath = "#ParentSocket";

    /// <summary>Path prefix of an appended slot.</summary>
    public const string SlotsPrefix = "#Slots[";

    /// <summary>The <c>CloneAssetOp.Kind</c> of a part added to a car.</summary>
    public const string PartKind = "VehiclePart";

    /// <summary>The part a new part is copied from (a roof rack: a model and a socket, no engine, door or seat logic).</summary>
    public const string DefaultDonor = "RoofRack";

    /// <summary>True for an edit this class writes.</summary>
    public static bool IsEdit(string path) => path is MeshPath or OffsetPath or SocketPath || path.StartsWith(SlotsPrefix, StringComparison.Ordinal);

    /// <summary>The path of slot <paramref name="slot"/>.</summary>
    public static string SlotPath(int slot) => string.Create(CultureInfo.InvariantCulture, $"{SlotsPrefix}{slot}]");

    /// <summary>The value of an appended slot: the class it takes and the slot it copies.</summary>
    public static string SlotValue(string classPath, int donorSlot) => string.Create(CultureInfo.InvariantCulture, $"{classPath}|{donorSlot}");

    /// <summary>The class path of a Blueprint package (<c>/Game/A/BPC_X</c> → <c>/Game/A/BPC_X.BPC_X_C</c>).</summary>
    public static string ClassPath(string package) => package + "." + PackageMap.Leaf(package) + "_C";

    /// <summary>The part's model (<c>MeshSetup.Mesh</c> of its defaults), or null.</summary>
    public static string? MeshOf(CookedPackage attachment) =>
        Cdo(attachment) is var cdo && cdo >= 0 && Mesh(attachment.ReadProperties(cdo)) is { } soft && soft.AssetPath is { Length: > 0 } path && path != "None" ? path : null;

    /// <summary>The part's <c>MeshSetup.Offset</c> as its defaults store it (absent members at their defaults), or null when they store none.</summary>
    public static FTransform? OffsetOf(CookedPackage attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var cdo = Cdo(attachment);
        if (cdo < 0 || (attachment.ReadProperties(cdo).Find("MeshSetup")?.Value as StructValue)?.Find("Offset")?.Value is not StructValue offset)
        {
            return null;
        }

        var q = offset.Find("Rotation")?.Value as QuatValue;
        var t = offset.Find("Translation")?.Value as VectorValue;
        var s = offset.Find("Scale3D")?.Value as VectorValue;
        return new FTransform(q is null ? FQuat.Identity : new FQuat(q.X, q.Y, q.Z, q.W), t is null ? FVector.Zero : new FVector(t.X, t.Y, t.Z),
            s is null ? FVector.One : new FVector(s.X, s.Y, s.Z));
    }

    /// <summary>The socket (or bone) the part hangs on, or null when its defaults do not store one.</summary>
    public static string? ParentSocketOf(CookedPackage attachment) =>
        Cdo(attachment) is var cdo && cdo >= 0 && attachment.ReadProperties(cdo).Find("ParentSocket")?.Value is NameValue n ? n.Value : null;

    /// <summary>The classes each slot of the part takes.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> SlotsOf(CookedPackage attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var cdo = Cdo(attachment);
        if (cdo < 0 || attachment.ReadProperties(cdo).Find("_slots")?.Value is not ArrayValue slots)
        {
            return [];
        }

        return slots.Items.OfType<StructValue>()
            .Select(s => (IReadOnlyList<string>)((s.Find("PossibleAttachmentClasses")?.Value as ArrayValue)?.Items.OfType<SoftObjectValue>().Select(c => c.AssetPath).ToList() ?? []))
            .ToList();
    }

    /// <summary>
    /// The edit that shows <paramref name="meshObjectPath"/> for the part. <paramref name="classOf"/> gives a mesh's class
    /// (<c>StaticMesh</c>, <c>SkeletalMesh</c>) or null when unknown.
    /// </summary>
    /// <exception cref="InvalidOperationException">The new model is of the other mesh class (or not a mesh).</exception>
    public static TunableEdit SwapMesh(CookedPackage attachment, string meshObjectPath, Func<string, string?> classOf)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(classOf);
        var cdo = Cdo(attachment);
        var old = MeshOf(attachment) ?? throw new InvalidOperationException($"{attachment.BasePath}: the part has no model to swap.");
        var (was, now) = (classOf(old), classOf(meshObjectPath));
        if (now is not ("StaticMesh" or "SkeletalMesh") || was is not null && was != now)
        {
            throw new InvalidOperationException($"{meshObjectPath} is a {now ?? "missing asset"}, the part shows a {was}: a part keeps its kind of model.");
        }

        return new TunableEdit(TunableReader.ExportKeys(attachment)[cdo], MeshPath, meshObjectPath);
    }

    /// <summary>
    /// The package of a new part of the clone whose attachments live in <paramref name="attachmentsFolder"/>:
    /// <c>BPC_&lt;token&gt;_CustomA</c>, <c>…_CustomB</c> … (letters, never a number at the end: UE would split it off the name).
    /// </summary>
    public static string CustomName(string attachmentsFolder, string token, Func<string, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        for (var n = 0; ; n++)
        {
            var letters = string.Empty;
            for (var k = n; k >= 0; k = k / 26 - 1)
            {
                letters = (char)('A' + k % 26) + letters;
            }

            var path = $"{attachmentsFolder}/BPC_{token}_Custom{letters}";
            if (!taken(path))
            {
                return path;
            }
        }
    }

    /// <summary>The name of the socket a new part hangs on (<c>BPC_X_CustomB</c> → <c>s_CustomB</c>).</summary>
    public static string CustomSocket(string partPackage)
    {
        var leaf = PackageMap.Leaf(partPackage);
        return "s_" + leaf[(leaf.LastIndexOf('_') + 1)..];
    }

    /// <summary>Writes the edits into the part's defaults (new names added to the name table) and reads them back.</summary>
    /// <exception cref="InvalidOperationException">No defaults, a missing property, a slot that is not appended, or a value that did not read back.</exception>
    public static PackageBytes Apply(CookedPackage attachment, IEnumerable<TunableEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(edits);
        var list = edits.ToList();
        var cdo = Cdo(attachment);
        if (cdo < 0)
        {
            throw new InvalidOperationException($"{attachment.BasePath}: no defaults to edit.");
        }

        var tables = PackageTables.Of(attachment, cdo);
        var payloads = Enumerable.Range(0, attachment.Exports.Count).Select(i => attachment.GetExportData(i).ToArray()).ToList();
        var payload = payloads[cdo];
        PropertyBlock Block() => PropertyReader.ReadPayload(attachment, payload, tables.Names, cdo);
        foreach (var edit in list.OrderBy(e => SlotIndex(e.Path))) // slots in ascending order: each is appended after the last
        {
            var value = edit.Value.Trim();
            if (edit.Path == MeshPath)
            {
                var soft = Mesh(Block()) ?? throw new InvalidOperationException($"{attachment.BasePath}: the part has no MeshSetup.Mesh.");
                tables.Name(VehicleParts.PackageOf(value));
                VehicleSockets.WriteName(payload, soft.Offset, tables.Name(value));
            }
            else if (edit.Path == SocketPath)
            {
                var socket = VehicleSockets.SplitName(tables, value);
                var block = Block();
                payload = block.Find("ParentSocket")?.Value is NameValue n
                    ? Patch(payload, n.Offset, socket)
                    : VehicleSockets.Insert(payload, block.EndOffset - 8, VehicleSockets.NameTag(tables, "ParentSocket", socket));
            }
            else if (edit.Path == OffsetPath)
            {
                payload = WriteOffset(attachment, tables, payload, Block(), value);
            }
            else
            {
                payload = AppendSlot(attachment, tables, payload, Block(), edit.Path, value);
            }
        }

        payloads[cdo] = payload;
        var bytes = VehicleSockets.Build(attachment, tables, payloads);
        var check = CookedPackage.Parse(bytes.UAsset, bytes.UExp, attachment.UBulk, attachment.BasePath);
        var slots = SlotsOf(check);
        foreach (var edit in list)
        {
            var value = edit.Value.Trim();
            var ok = edit.Path switch
            {
                MeshPath => MeshOf(check) == value,
                SocketPath => ParentSocketOf(check) == value,
                OffsetPath => OffsetOf(check) is { } offset && offset.Equals(PartModelFit.Parse(value), 1e-4f),
                _ => SlotIndex(edit.Path) is var s && s >= 0 && s < slots.Count && slots[s].SequenceEqual([value.Split('|')[0]]),
            };
            if (!ok)
            {
                throw new InvalidOperationException($"{attachment.BasePath}: '{edit.Path}' did not read back as '{value}'.");
            }
        }

        return bytes;
    }

    private static byte[] Patch(byte[] payload, int offset, ScumStudio.Formats.FNameRef name)
    {
        VehicleSockets.WriteName(payload, offset, name);
        return payload;
    }

    /// <summary>
    /// <c>MeshSetup.Offset</c> written whole (Rotation as a native Quat, Translation and Scale3D as Vectors, then None): an
    /// offset the part has is replaced, a missing one is added before the end of <c>MeshSetup</c> (its size grows).
    /// </summary>
    private static byte[] WriteOffset(CookedPackage attachment, PackageTables tables, byte[] payload, PropertyBlock block, string value)
    {
        FTransform transform;
        try
        {
            transform = PartModelFit.Parse(value);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{attachment.BasePath}: '{value}' is not a transform ({ex.Message}).", ex);
        }

        if (block.Find("MeshSetup") is not { Value: StructValue setup } tag)
        {
            throw new InvalidOperationException($"{attachment.BasePath}: the part has no MeshSetup.");
        }

        var (t, s) = (transform.Translation, transform.Scale3D);
        byte[] members =
        [
            .. QuatTag(tables, "Rotation", transform.Rotation),
            .. VehicleSockets.StructTag(tables, "Translation", "Vector", new System.Numerics.Vector3(t.X, t.Y, t.Z)),
            .. VehicleSockets.StructTag(tables, "Scale3D", "Vector", new System.Numerics.Vector3(s.X, s.Y, s.Z)),
            .. NameBytes(tables, "None"),
        ];
        if (setup.Find("Offset") is { Value: StructValue } offset)
        {
            payload = [.. payload.AsSpan(0, offset.ValueOffset), .. members, .. payload.AsSpan(offset.EndOffset)];
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset.SizeFieldOffset), members.Length);
            VehicleSockets.Add(payload, tag.SizeFieldOffset, members.Length - offset.Size);
            return payload;
        }

        var header = new byte[49]; // name, StructProperty, size, array index, Transform, empty guid, no property guid
        VehicleSockets.WriteName(header, 0, tables.Name("Offset"));
        VehicleSockets.WriteName(header, 8, tables.Name("StructProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), members.Length);
        VehicleSockets.WriteName(header, 24, tables.Name("Transform"));
        byte[] whole = [.. header, .. members];
        payload = VehicleSockets.Insert(payload, tag.EndOffset - 8, whole);
        VehicleSockets.Add(payload, tag.SizeFieldOffset, whole.Length);
        return payload;
    }

    /// <summary>A native Quat struct tag and its value (65 bytes).</summary>
    private static byte[] QuatTag(PackageTables tables, string name, FQuat q)
    {
        var bytes = new byte[65];
        VehicleSockets.WriteName(bytes, 0, tables.Name(name));
        VehicleSockets.WriteName(bytes, 8, tables.Name("StructProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        VehicleSockets.WriteName(bytes, 24, tables.Name("Quat"));
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(49), q.X);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(53), q.Y);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(57), q.Z);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(61), q.W);
        return bytes;
    }

    private static byte[] NameBytes(PackageTables tables, string name)
    {
        var bytes = new byte[8];
        VehicleSockets.WriteName(bytes, 0, tables.Name(name));
        return bytes;
    }

    /// <summary>A copy of the donor slot, taking only the new class, at the end of <c>_slots</c> (array, inner tag and count resized).</summary>
    private static byte[] AppendSlot(CookedPackage attachment, PackageTables tables, byte[] payload, PropertyBlock block, string path, string value)
    {
        var bar = value.LastIndexOf('|');
        if (block.Find("_slots") is not { Value: ArrayValue { InnerTag: { } inner } slots } outer
            || SlotIndex(path) != slots.Items.Count
            || bar <= 0 || !int.TryParse(value[(bar + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var donor) || donor >= slots.Items.Count
            || slots.Items[donor] is not StructValue model
            || model.Find("PossibleAttachmentClasses") is not { Value: ArrayValue } classes)
        {
            throw new InvalidOperationException($"{attachment.BasePath}: '{path}' = '{value}' is not a new slot after the {CountOf(block)} the part has.");
        }

        var classPath = value[..bar];
        tables.Name(VehicleParts.PackageOf(classPath));
        var element = new byte[16]; // count 1, FName, empty FString sub path
        BinaryPrimitives.WriteInt32LittleEndian(element, 1);
        VehicleSockets.WriteName(element, 4, tables.Name(classPath));
        var entry = payload.AsSpan(model.Offset, model.Size).ToArray();
        var at = classes.ValueOffset - model.Offset;
        var copy = (byte[])[.. entry.AsSpan(0, at), .. element, .. entry.AsSpan(at + classes.Size)];
        VehicleSockets.Add(copy, classes.SizeFieldOffset - model.Offset, element.Length - classes.Size);

        payload = VehicleSockets.Insert(payload, outer.EndOffset, copy);
        VehicleSockets.Add(payload, outer.SizeFieldOffset, copy.Length);
        VehicleSockets.Add(payload, outer.ValueOffset, 1);
        VehicleSockets.Add(payload, inner.SizeFieldOffset, copy.Length);
        return payload;
    }

    private static int CountOf(PropertyBlock block) => (block.Find("_slots")?.Value as ArrayValue)?.Items.Count ?? 0;

    private static SoftObjectValue? Mesh(PropertyBlock block) =>
        (block.Find("MeshSetup")?.Value as StructValue)?.Find("Mesh")?.Value as SoftObjectValue;

    private static int SlotIndex(string path) =>
        path.StartsWith(SlotsPrefix, StringComparison.Ordinal) && path.EndsWith(']')
        && int.TryParse(path[SlotsPrefix.Length..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1;

    private static int Cdo(CookedPackage package) =>
        Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.ResolveName(package.Exports[i].ObjectName).StartsWith("Default__", StringComparison.Ordinal), -1);
}
