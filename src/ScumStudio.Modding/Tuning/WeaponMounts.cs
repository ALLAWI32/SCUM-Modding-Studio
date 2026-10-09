using System.Buffers.Binary;
using System.Globalization;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Tuning;

/// <summary>
/// Which attachments a SCUM weapon takes. The weapon CDO's <c>_attachmentSockets</c> is an array of
/// <c>AttachmentSocket</c> (scope rail, magazine, muzzle, bayonet, light, ghillie, charm …), each with <c>Items</c>:
/// alternatives of <c>BoneName</c> + <c>MountType</c> (a <c>BP_MountType*_C</c> class import) + <c>MountedItem</c>
/// (None in the CDO). An attachment or magazine fits a socket when its CDO's <c>_attachmentSocketMountType</c> is one
/// of the socket's mount types. One <see cref="TunableKind.Mount"/> tunable per socket × mount type of the same kind:
/// path <c>_attachmentSockets[i].Items#&lt;mount class path&gt;</c>, value <c>true</c> when the socket takes it.
/// </summary>
public static class WeaponMounts
{
    /// <summary>The socket array of a weapon CDO.</summary>
    public const string Property = "_attachmentSockets";

    /// <summary>Folder searched for attachments and magazines (their mount types).</summary>
    public const string ItemFolder = ModdableAssets.ConZ + "Items/Weapons/";

    private const string Marker = ".Items#";

    /// <summary>True for an edit this class writes.</summary>
    public static bool IsEdit(string path) => path.StartsWith(Property + "[", StringComparison.Ordinal) && path.Contains(Marker, StringComparison.Ordinal);

    /// <summary>Mount type class path → the attachments/magazines (package paths) that carry it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Index(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var packages = catalog.PackageFiles.Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
            .Where(p => p.StartsWith(ItemFolder, StringComparison.OrdinalIgnoreCase) && !p.EndsWith("_ES", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var path in packages)
        {
            try
            {
                var package = ModdableAssets.ReadPackage(catalog, path);
                if (package.Names.Contains("_attachmentSocketMountType") && Cdo(package) is >= 0 and var cdo
                    && package.ReadProperties(cdo).Find("_attachmentSocketMountType")?.Value is ObjectValue { Index: < 0 } mount
                    && ImportPath(package, mount.Index) is { } mountPath)
                {
                    (index.TryGetValue(mountPath, out var list) ? list : index[mountPath] = []).Add(path);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or InvalidOperationException or ArgumentOutOfRangeException or EndOfStreamException)
            {
                // Not a cooked item package (textures, meshes, curves …).
            }
        }

        return index.ToDictionary(e => e.Key, e => (IReadOnlyList<string>)e.Value.Order(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One row per socket and mount type of the kinds the socket already takes (the folders of the attachments that fit:
    /// a magazine socket lists every magazine mount type, a muzzle every suppressor mount type). Empty when the weapon
    /// does not store its own sockets (a variant inheriting its parent's).
    /// </summary>
    public static IReadOnlyList<Tunable> Read(CookedPackage weapon, IReadOnlyDictionary<string, IReadOnlyList<string>> index)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        ArgumentNullException.ThrowIfNull(index);
        var result = new List<Tunable>();
        var cdo = Cdo(weapon);
        if (cdo < 0 || weapon.ReadProperties(cdo).Find(Property)?.Value is not ArrayValue sockets)
        {
            return result;
        }

        var key = TunableReader.ExportKeys(weapon)[cdo];
        var socketIndex = 0;
        foreach (var socket in sockets.Items.OfType<StructValue>())
        {
            var s = socketIndex++;
            var items = Items(socket)?.Value is ArrayValue a ? a.Items.OfType<StructValue>().ToList() : [];
            var present = items.Select(i => MountOf(weapon, i)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var folders = present.SelectMany(m => index.GetValueOrDefault(m) ?? []).Select(PackageMap.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var offered = present.Concat(index.Where(e => e.Value.Any(p => folders.Contains(PackageMap.Folder(p)))).Select(e => e.Key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var bones = items.Select(i => i.Properties.FirstOrDefault(p => p.Name == "BoneName")?.Value is NameValue n && n.Value != "None" ? n.Value : null)
                .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
            var group = string.Create(CultureInfo.InvariantCulture, $"Socket {s + 1}{(bones.Count > 0 ? " · " + string.Join(", ", bones) : string.Empty)}");
            foreach (var mount in offered.OrderBy(m => present.Contains(m, StringComparer.OrdinalIgnoreCase) ? 0 : 1).ThenBy(MountLabel, StringComparer.OrdinalIgnoreCase))
            {
                var fits = index.GetValueOrDefault(mount) ?? [];
                result.Add(new Tunable(key, $"{Property}[{s.ToString(CultureInfo.InvariantCulture)}]{Marker}{mount}", TunableKind.Mount,
                    present.Contains(mount, StringComparer.OrdinalIgnoreCase) ? "true" : "false")
                {
                    Name = MountLabel(mount) + (fits.Count > 0 ? ": " + string.Join(", ", fits.Take(4).Select(PackageMap.Leaf)) + (fits.Count > 4 ? " …" : string.Empty) : string.Empty),
                    ExportClass = weapon.GetExportClassName(cdo),
                    Group = group,
                    // A socket must keep one item to copy when a mount type is added back.
                    CanEdit = items.Count > 0,
                    ReadOnlyReason = items.Count > 0 ? null : "This socket has no item to copy.",
                });
            }
        }

        return result;
    }

    /// <summary>Readable mount type: <c>…/BP_MountTypeWeaponMagazine76239.BP_MountTypeWeaponMagazine76239_C</c> → <c>Magazine76239</c>.</summary>
    public static string MountLabel(string mountPath)
    {
        var leaf = PackageMap.Leaf(VehicleParts.PackageOf(mountPath));
        foreach (var prefix in new[] { "BP_MountType_", "BP_MountType" })
        {
            if (leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                leaf = leaf[prefix.Length..];
                break;
            }
        }

        leaf = leaf.StartsWith("Weapon_", StringComparison.OrdinalIgnoreCase) ? leaf[7..] : leaf.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) && leaf.Length > 6 ? leaf[6..] : leaf;
        return leaf.Replace('_', ' ');
    }

    /// <summary>
    /// Writes socket edits: a mount type set to <c>false</c> loses its items; one set to <c>true</c> that the socket lacks
    /// gets a copy of the socket's first item (same bone, nothing mounted) with that mount type (an import, added when the
    /// weapon lacks it). The socket array, each changed <c>Items</c> array and their struct tags are resized.
    /// </summary>
    /// <exception cref="InvalidOperationException">No socket array, a missing socket, or a socket with no item to copy.</exception>
    public static PackageBytes Apply(CookedPackage weapon, IEnumerable<TunableEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(weapon);
        ArgumentNullException.ThrowIfNull(edits);
        var list = edits.ToList();
        var cdo = Cdo(weapon);
        var payload = cdo >= 0 ? weapon.GetExportData(cdo).ToArray() : [];
        var block = cdo >= 0 ? PropertyReader.ReadPayload(weapon, payload, cdo) : null;
        if (block?.Find(Property) is not { Value: ArrayValue { InnerTag: { } outerInner } sockets } outer)
        {
            throw new InvalidOperationException($"{weapon.BasePath}: no {Property} stored.");
        }

        var tables = PackageTables.Of(weapon, cdo);
        var total = 0;
        var bySocket = list.GroupBy(e => SocketIndex(e.Path)).OrderByDescending(g => g.Key);
        foreach (var group in bySocket)
        {
            if (group.Key < 0 || group.Key >= sockets.Items.Count || sockets.Items[group.Key] is not StructValue socket || Items(socket) is not { Value: ArrayValue { InnerTag: { } inner } items } itemsTag)
            {
                throw new InvalidOperationException($"{weapon.BasePath}: no socket for '{group.First().Path}'.");
            }

            var structs = items.Items.OfType<StructValue>().ToList();
            var want = group.ToDictionary(e => e.Path[(e.Path.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..], e => TunableValue.ParseBool(e.Value), StringComparer.OrdinalIgnoreCase);
            var kept = structs.Where(i => MountOf(weapon, i) is not { } m || !want.TryGetValue(m, out var on) || on).ToList();
            var added = want.Where(w => w.Value && !structs.Any(i => string.Equals(MountOf(weapon, i), w.Key, StringComparison.OrdinalIgnoreCase))).Select(w => w.Key).ToList();
            if (added.Count > 0 && structs.Count == 0)
            {
                throw new InvalidOperationException($"{weapon.BasePath}: socket {group.Key + 1} has no item to copy.");
            }

            var written = new List<byte>();
            foreach (var item in kept)
            {
                written.AddRange(payload.AsSpan(item.Offset, item.Size).ToArray());
            }

            foreach (var mount in added)
            {
                var model = structs[0];
                var entry = payload.AsSpan(model.Offset, model.Size).ToArray();
                var mountTag = model.Properties.First(p => p.Name == "MountType");
                BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(mountTag.Value.Offset - model.Offset), tables.Import("/Script/Engine", "BlueprintGeneratedClass", mount));
                written.AddRange(entry);
            }

            var start = inner.ValueOffset; // the elements follow the inner struct tag and end with the Items tag
            var end = itemsTag.EndOffset;
            var delta = written.Count - (end - start);
            payload = [.. payload.AsSpan(0, start), .. written, .. payload.AsSpan(end)];
            Add(payload, itemsTag.SizeFieldOffset, delta);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(itemsTag.ValueOffset), kept.Count + added.Count);
            Add(payload, inner.SizeFieldOffset, delta);
            total += delta;
        }

        Add(payload, outer.SizeFieldOffset, total);
        Add(payload, outerInner.SizeFieldOffset, total);
        var data = Enumerable.Range(0, weapon.Exports.Count).Select(i => i == cdo ? (ReadOnlyMemory<byte>)payload : weapon.GetExportData(i)).ToArray();
        var bytes = PackageWriter.Build(PackageWriter.ToBuildInput(weapon) with
        {
            Names = tables.Names,
            NameIsWide = tables.Wide,
            Imports = tables.Imports,
            Exports = tables.Exports,
            PreloadDependencies = tables.Preload,
            ExportData = data,
        });

        var check = CookedPackage.Parse(bytes.UAsset, bytes.UExp, weapon.UBulk, weapon.BasePath);
        var now = Present(check);
        foreach (var edit in list)
        {
            var mount = edit.Path[(edit.Path.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length)..];
            if (now.Contains((SocketIndex(edit.Path), mount.ToUpperInvariant())) != TunableValue.ParseBool(edit.Value))
            {
                throw new InvalidOperationException($"{weapon.BasePath}: '{edit.Path}' did not read back as {edit.Value}.");
            }
        }

        return bytes;
    }

    private static HashSet<(int Socket, string Mount)> Present(CookedPackage weapon)
    {
        var set = new HashSet<(int, string)>();
        var cdo = Cdo(weapon);
        if (cdo >= 0 && weapon.ReadProperties(cdo).Find(Property)?.Value is ArrayValue sockets)
        {
            for (var s = 0; s < sockets.Items.Count; s++)
            {
                if (sockets.Items[s] is StructValue socket && Items(socket)?.Value is ArrayValue items)
                {
                    foreach (var mount in items.Items.OfType<StructValue>().Select(i => MountOf(weapon, i)).OfType<string>())
                    {
                        set.Add((s, mount.ToUpperInvariant()));
                    }
                }
            }
        }

        return set;
    }

    private static PropertyTag? Items(StructValue socket) => socket.Properties.FirstOrDefault(p => p.Name == "Items");

    private static string? MountOf(CookedPackage package, StructValue item) =>
        item.Properties.FirstOrDefault(p => p.Name == "MountType")?.Value is ObjectValue { Index: < 0 } o ? ImportPath(package, o.Index) : null;

    /// <summary>The object path of an import (<c>/Game/…/BP_X.BP_X_C</c>), when its outer is a package import.</summary>
    private static string? ImportPath(CookedPackage package, int index)
    {
        var import = package.Imports[-index - 1];
        if (import.OuterIndex >= 0)
        {
            return null;
        }

        var outer = package.Imports[-import.OuterIndex - 1];
        return package.ResolveName(outer.ObjectName) + "." + package.ResolveName(import.ObjectName);
    }

    private static int Cdo(CookedPackage package) =>
        Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.ResolveName(package.Exports[i].ObjectName).StartsWith("Default__", StringComparison.Ordinal), -1);

    private static int SocketIndex(string path)
    {
        var close = path.IndexOf(']', StringComparison.Ordinal);
        return close > Property.Length + 1 && int.TryParse(path[(Property.Length + 1)..close], NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1;
    }

    private static void Add(byte[] buffer, int offset, int delta) =>
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset), BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset)) + delta);
}
