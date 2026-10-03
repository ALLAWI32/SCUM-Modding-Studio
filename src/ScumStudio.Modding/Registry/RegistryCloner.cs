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
    /// <param name="registry">Registry to extend (the effective one of the source, so other mods' records stay).</param>
    /// <param name="map">The clone's package map.</param>
    /// <param name="warnings">Receives tags that could not be remapped.</param>
    public static IReadOnlyList<RegisteredAsset> Register(AssetRegistryFile registry, PackageMap map, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(map);
        var byObject = new Dictionary<string, AssetData>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in registry.Assets)
        {
            byObject.TryAdd(asset.ObjectPath, asset);
        }

        var added = new List<RegisteredAsset>();
        foreach (var (oldPackage, newPackage) in map.Packages)
        {
            var oldLeaf = PackageMap.Leaf(oldPackage);
            var newLeaf = PackageMap.Leaf(newPackage);
            foreach (var suffix in new[] { string.Empty, "_C" })
            {
                var newObject = $"{newPackage}.{newLeaf}{suffix}";
                if (!byObject.TryGetValue($"{oldPackage}.{oldLeaf}{suffix}", out var template) || byObject.ContainsKey(newObject))
                {
                    continue;
                }

                var tags = registry.GetTags(template);
                var type = tags.FirstOrDefault(t => t.Key == "PrimaryAssetType").Value?.Text;
                if (type is null)
                {
                    continue;
                }

                var remapped = new List<(string, AssetRegistryValue)>(tags.Count);
                var ok = true;
                foreach (var (key, value) in tags)
                {
                    AssetRegistryValue? v = value.Kind switch
                    {
                        AssetRegistryValueKind.NumberlessExportPath when value.ExportPath is { Count: 3 } p =>
                            AssetRegistryValue.NumberlessExport(p[0], map.RemapObject(p[1]), map.RemapName(p[2])),
                        AssetRegistryValueKind.NumberlessName => AssetRegistryValue.NumberlessName(map.RemapObject(value.Text)),
                        AssetRegistryValueKind.AnsiString => AssetRegistryValue.Ansi(map.RemapText(value.Text)),
                        AssetRegistryValueKind.LocalizedText => value,
                        _ => null,
                    };
                    if (v is null)
                    {
                        warnings?.Add($"{newObject}: tag '{key}' of kind {value.Kind} cannot be remapped; record skipped.");
                        ok = false;
                        break;
                    }

                    remapped.Add((key, v));
                }

                if (!ok)
                {
                    continue;
                }

                var record = registry.AddAsset(template, newObject, PackageMap.Folder(newPackage), newPackage, newLeaf + suffix, remapped);
                byObject[record.ObjectPath] = record;
                added.Add(new RegisteredAsset(newObject, type));
            }
        }

        return added;
    }
}
