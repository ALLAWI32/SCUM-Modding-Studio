using System.Buffers.Binary;
using System.Numerics;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;

namespace ScumStudio.Modding.Tuning;

/// <summary>One socket of a skeleton or a skeletal mesh (a <c>SkeletalMeshSocket</c> export).</summary>
/// <param name="Name">Socket name (<c>s_Door_FrontLeft</c>).</param>
/// <param name="Bone">Bone it hangs on (<c>Root</c> on SCUM's cars).</param>
/// <param name="Location">RelativeLocation in the bone's space (cm).</param>
/// <param name="Rotation">RelativeRotation as pitch, yaw, roll (degrees).</param>
/// <param name="Scale">RelativeScale (1 = the part's own size).</param>
/// <param name="OnMesh">True for a socket of the mesh itself, false for one of the skeleton.</param>
public sealed record VehicleSocket(string Name, string Bone, Vector3 Location, Vector3 Rotation, Vector3 Scale, bool OnMesh);

/// <summary>
/// Where a vehicle's parts sit. An attachment's <c>ParentSocket</c> (<c>s_Door_FrontLeft</c>) is a socket of the vehicle's
/// skeleton (<c>SK_WolfsWagen_Skeleton</c>: 53 sockets, all on <c>Root</c>), which every clone and the stock car share.
/// UE 4.27 looks a socket up on the skeletal mesh before the skeleton (<c>USkeletalMesh::FindSocketInfo</c>, the mesh's
/// own <c>Sockets</c> first), so a part moves on a clone only through a socket of the clone's own copy of the mesh: an
/// existing one is patched in place, a missing one is added (a <c>SkeletalMeshSocket</c> export under the mesh, its
/// <c>Sockets</c> array grown or inserted). Edits: path <c>#Socket[name].RelativeLocation|RelativeRotation|RelativeScale|BoneName</c>.
/// </summary>
public static class VehicleSockets
{
    /// <summary>Path prefix of a socket edit.</summary>
    public const string Prefix = "#Socket[";

    /// <summary>Socket members an edit sets.</summary>
    public const string Location = "RelativeLocation", Rotation = "RelativeRotation", Scale = "RelativeScale", Bone = "BoneName";

    private const string SocketClass = "SkeletalMeshSocket";

    /// <summary>True for an edit this class writes.</summary>
    public static bool IsEdit(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The edit path of a socket member.</summary>
    public static string Path(string socket, string member) => $"{Prefix}{socket}].{member}";

    /// <summary>(socket, member) of an edit path, or null.</summary>
    public static (string Socket, string Member)? Parse(string path)
    {
        var close = path.LastIndexOf("].", StringComparison.Ordinal);
        return IsEdit(path) && close > Prefix.Length ? (path[Prefix.Length..close], path[(close + 2)..]) : null;
    }

    /// <summary>Every socket of a skeleton or skeletal mesh package.</summary>
    public static IReadOnlyList<VehicleSocket> Read(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var result = new List<VehicleSocket>();
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (package.GetExportClassName(i) == SocketClass && Row(package.ReadProperties(i)) is { } row)
            {
                var outer = package.Exports[i].OuterIndex;
                result.Add(row with { OnMesh = outer > 0 && package.GetExportClassName(outer - 1) == "SkeletalMesh" });
            }
        }

        return result;
    }

    /// <summary>The <c>SkeletalMesh</c> export of a package, or -1.</summary>
    public static int MeshExport(CookedPackage package) =>
        Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.GetExportClassName(i) == "SkeletalMesh", -1);

    /// <summary>The package of the first import of class <paramref name="className"/> (a vehicle's mesh, a mesh's skeleton), or null.</summary>
    public static string? ImportedPackage(CookedPackage package, string className)
    {
        ArgumentNullException.ThrowIfNull(package);
        foreach (var import in package.Imports)
        {
            if (package.ResolveName(import.ClassName) == className && import.OuterIndex < 0 && package.Imports[-import.OuterIndex - 1] is { } outer
                && package.ResolveName(outer.ClassName) == "Package")
            {
                return package.ResolveName(outer.ObjectName);
            }
        }

        return null;
    }

    /// <summary>The edits that give a mesh its own copy of <paramref name="socket"/> at a new place (bone, location, rotation, scale).</summary>
    public static IReadOnlyList<TunableEdit> Override(string meshExport, VehicleSocket socket, Vector3 location, Vector3 rotation, Vector3 scale) =>
    [
        new(meshExport, Path(socket.Name, Bone), socket.Bone),
        new(meshExport, Path(socket.Name, Location), TunableValue.Format(location.X, location.Y, location.Z)),
        new(meshExport, Path(socket.Name, Rotation), TunableValue.Format(rotation.X, rotation.Y, rotation.Z)),
        new(meshExport, Path(socket.Name, Scale), TunableValue.Format(scale.X, scale.Y, scale.Z)),
    ];

    /// <summary>
    /// Writes socket edits into a skeletal mesh package: a socket the mesh has is patched (an absent member is inserted as
    /// a tagged property before <c>None</c>); a socket it lacks is added (its <c>BoneName</c> edit is required), with the
    /// class and template imports, the cooker's preload dependencies (class and template before its creation, the mesh
    /// before it is created, the socket before the mesh is serialized) and the mesh's <c>Sockets</c> array. Read back.
    /// </summary>
    /// <exception cref="InvalidOperationException">No skeletal mesh, an unknown member, a new socket without a bone, or a value that did not read back.</exception>
    public static PackageBytes Apply(CookedPackage mesh, IEnumerable<TunableEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(edits);
        var list = edits.ToList();
        var meshIndex = MeshExport(mesh);
        if (meshIndex < 0)
        {
            throw new InvalidOperationException($"{mesh.BasePath}: no skeletal mesh to hold sockets.");
        }

        var tables = PackageTables.Of(mesh, meshIndex);
        var payloads = Enumerable.Range(0, mesh.Exports.Count).Select(i => mesh.GetExportData(i).ToArray()).ToList();
        var wanted = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var edit in list)
        {
            var (socket, member) = Parse(edit.Path) ?? throw new InvalidOperationException($"'{edit.Path}' is not a socket edit.");
            if (member is not (Location or Rotation or Scale or Bone))
            {
                throw new InvalidOperationException($"'{edit.Path}': sockets have no member '{member}'.");
            }

            (wanted.TryGetValue(socket, out var members) ? members : wanted[socket] = new(StringComparer.Ordinal))[member] = edit.Value.Trim();
        }

        foreach (var (name, members) in wanted)
        {
            var socket = Enumerable.Range(0, mesh.Exports.Count).FirstOrDefault(i => mesh.Exports[i].OuterIndex == meshIndex + 1
                && mesh.GetExportClassName(i) == SocketClass && Row(mesh.ReadProperties(i))?.Name == name, -1);
            if (socket < 0)
            {
                if (!members.TryGetValue(Bone, out var bone) || bone.Length == 0)
                {
                    throw new InvalidOperationException($"{mesh.BasePath}: socket '{name}' is not on the mesh; its bone is needed to add it.");
                }

                socket = AddSocket(mesh, tables, payloads, meshIndex, name, bone);
            }

            foreach (var (member, value) in members)
            {
                payloads[socket] = Write(mesh, tables, payloads[socket], member, value);
            }
        }

        var bytes = Build(mesh, tables, payloads);
        var check = CookedPackage.Parse(bytes.UAsset, bytes.UExp, mesh.UBulk, mesh.BasePath);
        var now = Read(check).Where(s => s.OnMesh).GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var (name, members) in wanted)
        {
            foreach (var (member, value) in members)
            {
                var ok = now.TryGetValue(name, out var s) && member switch
                {
                    Bone => s.Bone == value,
                    Location => TunableValue.AreEqual(TunableKind.Vector, Format(s.Location), value),
                    Rotation => TunableValue.AreEqual(TunableKind.Rotator, Format(s.Rotation), value),
                    _ => TunableValue.AreEqual(TunableKind.Vector, Format(s.Scale), value),
                };
                if (!ok)
                {
                    throw new InvalidOperationException($"{mesh.BasePath}: socket '{name}' {member} did not read back as '{value}'.");
                }
            }
        }

        return bytes;
    }

    /// <summary>"x, y, z" of a vector.</summary>
    public static string Format(Vector3 v) => TunableValue.Format(v.X, v.Y, v.Z);

    /// <summary>
    /// An FName as UE splits it: <c>s_Light_1</c> is (<c>s_Light</c>, 2) like the engine's own names, so the game's name
    /// table matches it; a suffix with a leading zero (<c>_01</c>) stays part of the text (<see cref="FNameRef.Split"/>).
    /// </summary>
    internal static FNameRef SplitName(PackageTables tables, string text)
    {
        var (head, number) = FNameRef.Split(text);
        return tables.Name(head) with { Number = number };
    }

    /// <summary>A NameProperty tag and its value (33 bytes).</summary>
    internal static byte[] NameTag(PackageTables tables, string name, FNameRef value)
    {
        var bytes = new byte[33];
        WriteName(bytes, 0, tables.Name(name));
        WriteName(bytes, 8, tables.Name("NameProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 8);
        WriteName(bytes, 25, value);
        return bytes;
    }

    /// <summary>A native 12-byte struct tag (Vector, Rotator) and its value (61 bytes, see HANDOFF section 7).</summary>
    internal static byte[] StructTag(PackageTables tables, string name, string structName, Vector3 value)
    {
        var bytes = new byte[61];
        WriteName(bytes, 0, tables.Name(name));
        WriteName(bytes, 8, tables.Name("StructProperty"));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 12);
        WriteName(bytes, 24, tables.Name(structName));
        WriteFloats(bytes.AsSpan(49), value);
        return bytes;
    }

    internal static void WriteName(byte[] bytes, int at, FNameRef name)
    {
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), name.Index);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 4), name.Number);
    }

    internal static byte[] Insert(byte[] payload, int at, byte[] bytes) => [.. payload.AsSpan(0, at), .. bytes, .. payload.AsSpan(at)];

    internal static void Add(byte[] buffer, int offset, int delta) =>
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset)) + delta);

    /// <summary>The import of <paramref name="objectName"/> (class <paramref name="classPackage"/>.<paramref name="className"/>) in package import <paramref name="outer"/>, added when missing.</summary>
    internal static int ImportOf(PackageTables tables, string classPackage, string className, int outer, string objectName)
    {
        bool Is(FNameRef n, string text) => string.Equals(n.Format(tables.Names), text, StringComparison.Ordinal);
        var found = tables.Imports.FindIndex(im => im.OuterIndex == outer && Is(im.ClassName, className) && Is(im.ObjectName, objectName));
        if (found >= 0)
        {
            return -(found + 1);
        }

        tables.Imports.Add(new ImportEntry(tables.Name(classPackage), tables.Name(className), outer, tables.Name(objectName)));
        return -tables.Imports.Count;
    }

    internal static PackageBytes Build(CookedPackage package, PackageTables tables, IReadOnlyList<byte[]> payloads) =>
        PackageWriter.Build(PackageWriter.ToBuildInput(package) with
        {
            Names = tables.Names,
            NameIsWide = tables.Wide,
            Imports = tables.Imports,
            Exports = tables.Exports,
            PreloadDependencies = tables.Preload,
            ExportData = payloads.Select(p => (ReadOnlyMemory<byte>)p).ToArray(),
        });

    private static VehicleSocket? Row(PropertyBlock block)
    {
        if (block.Find("SocketName")?.Value is not NameValue name)
        {
            return null;
        }

        static Vector3 Of(PropertyValue? v, Vector3 fallback) => v switch
        {
            VectorValue x => new Vector3(x.X, x.Y, x.Z),
            RotatorValue r => new Vector3(r.Pitch, r.Yaw, r.Roll),
            _ => fallback,
        };

        return new VehicleSocket(name.Value, (block.Find("BoneName")?.Value as NameValue)?.Value ?? "None",
            Of(block.Find(Location)?.Value, Vector3.Zero), Of(block.Find(Rotation)?.Value, Vector3.Zero), Of(block.Find(Scale)?.Value, Vector3.One), false);
    }

    /// <summary>A new socket export under the mesh (SocketName, BoneName, None, bHasGuid), listed in the mesh's <c>Sockets</c>.</summary>
    private static int AddSocket(CookedPackage mesh, PackageTables tables, List<byte[]> payloads, int meshIndex, string name, string bone)
    {
        var engine = ImportOf(tables, "/Script/CoreUObject", "Package", 0, "/Script/Engine");
        var socketClass = ImportOf(tables, "/Script/CoreUObject", "Class", engine, SocketClass);
        var template = ImportOf(tables, "/Script/Engine", SocketClass, engine, "Default__" + SocketClass);
        var objectName = tables.Name(SocketClass);
        var created = tables.Exports.Count;
        tables.Exports.Add(new ExportEntry
        {
            ClassIndex = socketClass,
            TemplateIndex = template,
            OuterIndex = meshIndex + 1,
            ObjectName = objectName with { Number = tables.Exports.Where(e => e.ObjectName.Index == objectName.Index).Select(e => e.ObjectName.Number).DefaultIfEmpty(0).Max() + 1 },
            ObjectFlags = 0x8, // RF_Transactional, as the skeleton's sockets
            FirstExportDependency = -1,
        });
        payloads.Add([.. NameTag(tables, "SocketName", SplitName(tables, name)), .. NameTag(tables, Bone, SplitName(tables, bone)), .. Name(tables, "None"), 0, 0, 0, 0]);
        tables.AddDependency(created, 2, socketClass);
        tables.AddDependency(created, 2, template);
        tables.AddDependency(created, 3, meshIndex + 1);
        tables.AddDependency(meshIndex, 1, created + 1);

        var meshPayload = payloads[meshIndex];
        var block = PropertyReader.ReadPayload(mesh, meshPayload, tables.Names, meshIndex);
        if (block.Find("Sockets") is { Value: ArrayValue } sockets)
        {
            meshPayload = Insert(meshPayload, sockets.EndOffset, BitConverter.GetBytes(created + 1));
            Add(meshPayload, sockets.SizeFieldOffset, 4);
            Add(meshPayload, sockets.ValueOffset, 1);
        }
        else
        {
            var tag = new byte[41];
            WriteName(tag, 0, tables.Name("Sockets"));
            WriteName(tag, 8, tables.Name("ArrayProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 8);
            WriteName(tag, 24, tables.Name("ObjectProperty"));
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(33), 1);
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(37), created + 1);
            meshPayload = Insert(meshPayload, block.EndOffset - 8, tag);
        }

        payloads[meshIndex] = meshPayload;
        return created;
    }

    private static byte[] Name(PackageTables tables, string name)
    {
        var bytes = new byte[8];
        WriteName(bytes, 0, tables.Name(name));
        return bytes;
    }

    private static byte[] Write(CookedPackage mesh, PackageTables tables, byte[] payload, string member, string value)
    {
        var block = PropertyReader.ReadPayload(mesh, payload, tables.Names);
        var tag = block.Find(member);
        if (member == Bone)
        {
            var bone = SplitName(tables, value);
            if (tag?.Value is NameValue n)
            {
                WriteName(payload, n.Offset, bone);
                return payload;
            }

            return Insert(payload, block.EndOffset - 8, NameTag(tables, Bone, bone));
        }

        Vector3 v;
        try
        {
            var f = TunableValue.ParseFloats(value, 3);
            v = new Vector3(f[0], f[1], f[2]);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{mesh.BasePath}: '{value}' is not three numbers ({ex.Message}).", ex);
        }

        if (tag is { Size: 12, Value: VectorValue or RotatorValue })
        {
            WriteFloats(payload.AsSpan(tag.ValueOffset), v);
            return payload;
        }

        return Insert(payload, block.EndOffset - 8, StructTag(tables, member, member == Rotation ? "Rotator" : "Vector", v));
    }

    private static void WriteFloats(Span<byte> target, Vector3 v)
    {
        BinaryPrimitives.WriteSingleLittleEndian(target, v.X);
        BinaryPrimitives.WriteSingleLittleEndian(target[4..], v.Y);
        BinaryPrimitives.WriteSingleLittleEndian(target[8..], v.Z);
    }
}
