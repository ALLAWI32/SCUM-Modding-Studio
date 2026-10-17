using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Registry;

/// <summary>A registry record added for a clone.</summary>
/// <param name="ObjectPath">New object path (<c>/Game/…/X.X</c> or <c>…X.X_C</c>).</param>
/// <param name="PrimaryAssetType">The record's <c>PrimaryAssetType</c> (Item, Vehicle, VehiclePreset, LWObjectSetup …).</param>
public sealed record RegisteredAsset(string ObjectPath, string PrimaryAssetType);

/// <summary>
/// Registers cloned primary assets in <c>AssetRegistry.bin</c> (port of <c>register()</c> in <c>clone_vehicle.py</c>): for
/// every cloned package, its Blueprint record and its <c>_C</c> record are copied from the stock entry with every tag
/// remapped. The game only finds registered primary assets (<c>#SpawnItem</c>, <c>#SpawnVehicle</c>, entity setups), and
/// it matched entity setups and vehicles only through the <c>_C</c> record.
/// </summary>
public static class RegistryCloner
{
    /// <summary>Adds the records of the packages in <paramref name="map"/>; returns what was added.</summary>
    /// <param name="registry">Registry to extend (the stock one, plus what the same export already added).</param>
    /// <param name="map">The clone's package map.</param>
    /// <param name="warnings">Receives tags that could not be remapped and records that were kept.</param>
    /// <param name="owner">The clone or craftable the records belong to, named in the warnings (default: the new package).</param>
    /// <param name="copied">
    /// The (template, copy) pairs of <paramref name="map"/> that were copied (default: all). A redirect
    /// (<see cref="ClonePlan.Redirects"/>) is in the map only to rename references: its target exists already and its
    /// records are never touched.
    /// </param>
    /// <param name="kept">
    /// Object paths whose records are never replaced (default: every record <paramref name="registry"/> holds now), the
    /// game's own: a new path among them is skipped with a warning.
    /// </param>
    /// <param name="anyRecord">
    /// True: a record without a <c>PrimaryAssetType</c> (a texture's) is copied too, and <see cref="RegisteredAsset.PrimaryAssetType"/>
    /// is its asset class. False (the default): only primary assets.
    /// </param>
    /// <remarks>
    /// A record at the new object path that is not kept (one an earlier export left) is replaced, so its tags are the
    /// template's of today. Name and export path tags may carry FName numbers (<c>CR_Improvised_Bow_20</c>): they are
    /// remapped on their text and stored split again.
    /// </remarks>
    public static IReadOnlyList<RegisteredAsset> Register(AssetRegistryFile registry, PackageMap map, ICollection<string>? warnings = null, string? owner = null,
        IEnumerable<KeyValuePair<string, string>>? copied = null, IReadOnlySet<string>? kept = null, bool anyRecord = false)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(map);
        var byObject = new Dictionary<string, AssetData>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in registry.Assets)
        {
            byObject.TryAdd(asset.ObjectPath, asset);
        }

        kept ??= new HashSet<string>(byObject.Keys, StringComparer.OrdinalIgnoreCase);
        var added = new List<RegisteredAsset>();
        foreach (var (oldPackage, newPackage) in copied ?? map.Packages)
        {
            var oldLeaf = PackageMap.Leaf(oldPackage);
            var newLeaf = PackageMap.Leaf(newPackage);
            foreach (var suffix in new[] { string.Empty, "_C" })
            {
                var newObject = $"{newPackage}.{newLeaf}{suffix}";
                if (!byObject.TryGetValue($"{oldPackage}.{oldLeaf}{suffix}", out var template))
                {
                    continue;
                }

                var tags = registry.GetTags(template);
                var type = tags.FirstOrDefault(t => t.Key == "PrimaryAssetType").Value?.Text ?? (anyRecord ? template.AssetClass : null);
                if (type is null)
                {
                    continue;
                }

                if (kept.Contains(newObject))
                {
                    warnings?.Add($"{owner ?? newPackage}: {newObject} is a record of the game (or of the project's pak), kept as it is; the copy of {template.ObjectPath} is not registered.");
                    continue;
                }

                var remapped = new List<(string, AssetRegistryValue)>(tags.Count);
                var ok = true;
                foreach (var (key, value) in tags)
                {
                    AssetRegistryValue? v = value.Kind switch
                    {
                        AssetRegistryValueKind.NumberlessExportPath or AssetRegistryValueKind.ExportPath when value.ExportPath is { Count: 3 } p =>
                            AssetRegistryValue.AnyExport(p[0], map.RemapObject(p[1]), map.RemapName(p[2])),
                        AssetRegistryValueKind.NumberlessName or AssetRegistryValueKind.Name => AssetRegistryValue.AnyName(map.RemapObject(value.Text)),
                        AssetRegistryValueKind.AnsiString => AssetRegistryValue.Ansi(map.RemapText(value.Text)),
                        AssetRegistryValueKind.WideString => AssetRegistryValue.Wide(map.RemapText(value.Text)),
                        AssetRegistryValueKind.LocalizedText => value,
                        _ => null,
                    };
                    if (v is null)
                    {
                        warnings?.Add($"{owner ?? newPackage}: {newObject}: tag '{key}' of kind {value.Kind} cannot be remapped; record skipped.");
                        ok = false;
                        break;
                    }

                    remapped.Add((key, v));
                }

                if (!ok)
                {
                    continue;
                }

                var folder = PackageMap.Folder(newPackage);
                var record = byObject.TryGetValue(newObject, out var existing)
                    ? registry.ReplaceAsset(existing, template, newObject, folder, newPackage, newLeaf + suffix, remapped)
                    : registry.AddAsset(template, newObject, folder, newPackage, newLeaf + suffix, remapped);
                byObject[record.ObjectPath] = record;
                added.Add(new RegisteredAsset(newObject, type));
            }
        }

        return added;
    }
}
