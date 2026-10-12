using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.VirtualFileSystem;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;

namespace ScumStudio.Modding.Registry;

/// <summary>
/// The <c>AssetRegistry.bin</c> a pak ships (P0 #1, #21: a mod only adds): the game's own registry with every record as it
/// is, plus the records other mods' registries add for packages that ship in the same pak, plus ours
/// (<see cref="RegistryCloner"/>). Never another registry as a whole (an imported mod's, an installed pak's, or the studio's
/// own earlier export): the highest pak's registry is the one the game reads, and one that lacks a stock record hides
/// that asset from the game.
/// </summary>
public static class RegistryMerge
{
    /// <summary>
    /// The stock registry: <c>SCUM/AssetRegistry.bin</c> of the highest-ranked mounted container that is not a patch or mod
    /// pak (<c>*_P.pak</c>), never a loose folder. Null for a catalog without such a container (a loose extract) or file.
    /// </summary>
    public static byte[]? ReadStock(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Provider is not IVfsFileProvider provider)
        {
            return null;
        }

        var containers = provider.MountedVfs
            .Where(v => !PackageOrigins.IsModContainer(v.Name))
            .OrderByDescending(v => v.ReadOrder)
            .ThenByDescending(v => v.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var container in containers)
        {
            var file = container.Files.FirstOrDefault(f => string.Equals(f.Key, AssetModBuilder.AssetRegistryPath, StringComparison.OrdinalIgnoreCase)).Value;
            if (file is not null)
            {
                return file.Read();
            }
        }

        return null;
    }

    /// <summary>
    /// The registry to build on: <see cref="ReadStock"/>, else (a loose source, no pak mounted) the one the catalog reads,
    /// with a warning that it is not the stock registry. Null when there is none, and for paks without a stock registry: the
    /// one they read then is a mod pak's or a loose overlay's, and building on it would ship that one as the game's.
    /// </summary>
    public static byte[]? ReadBase(AssetCatalog catalog, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        catalog.Provider.Files.TryGetValue(AssetModBuilder.AssetRegistryPath, out var file);
        var pak = file is VfsEntry { Vfs.Name: var name } ? name : null;
        if (ReadStock(catalog) is { } stock)
        {
            // An installed mod pak with a registry of its own: its records are not in ours, and the game reads only one.
            if (pak is not null && PackageOrigins.IsModContainer(pak))
            {
                warnings.Add($"{pak} ships an AssetRegistry.bin of its own: its records are not in this mod's registry (only the game's are), so whichever of the two paks loads last hides the other's new items. Import it into the project to carry its records.");
            }

            return stock;
        }

        if (file is null)
        {
            return null;
        }

        if (catalog.Provider is IVfsFileProvider { MountedVfs.Count: > 0 })
        {
            warnings.Add($"{AssetModBuilder.AssetRegistryPath}: no game pak of {catalog.DisplayName} has one, only {pak ?? "a loose folder read over the paks"}, which is not the game's: nothing is registered.");
            return null;
        }

        warnings.Add($"{AssetModBuilder.AssetRegistryPath}: {catalog.DisplayName} has no game pak with a registry, so the registry built on is the one it reads, not the game's stock registry.");
        return file.Read();
    }

    /// <summary>
    /// Adds to <paramref name="stock"/> (whose own records stay as they are) every record of the mods' registries whose
    /// object path it does not have and whose package ships (<paramref name="ships"/>, by package name); where two mods
    /// have the same new record, the later one's is taken, as its package is the one that ships. One warning per mod
    /// names the records left out. Returns <paramref name="stock"/>.
    /// </summary>
    public static AssetRegistryFile Build(AssetRegistryFile stock, IEnumerable<(string Source, AssetRegistryFile Registry)> mods,
        Func<string, bool> ships, ICollection<string> warnings)
    {
        ArgumentNullException.ThrowIfNull(stock);
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(ships);
        ArgumentNullException.ThrowIfNull(warnings);
        var present = stock.Assets.Select(a => a.ObjectPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taken = new Dictionary<string, (string Source, AssetRegistryFile Registry, AssetData Asset)>(StringComparer.OrdinalIgnoreCase);
        var leftOut = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string> LeftOut(string source) => leftOut.TryGetValue(source, out var list) ? list : leftOut[source] = [];
        foreach (var (source, registry) in mods)
        {
            foreach (var asset in registry.Assets.Where(a => !present.Contains(a.ObjectPath)))
            {
                if (ships(asset.PackageName))
                {
                    taken[asset.ObjectPath] = (source, registry, asset);
                }
                else
                {
                    LeftOut(source).Add(asset.ObjectPath);
                }
            }
        }

        foreach (var (source, registry, asset) in taken.Values)
        {
            try
            {
                stock.ImportAsset(registry, asset);
            }
            catch (NotSupportedException)
            {
                LeftOut(source).Add(asset.ObjectPath);
            }
        }

        foreach (var (source, paths) in leftOut)
        {
            warnings.Add($"{source}: {paths.Count} record(s) of its AssetRegistry.bin are not in the shipped registry (their package does not ship, or a tag cannot be copied): "
                + string.Join(", ", paths.Take(5)) + (paths.Count > 5 ? ", …" : string.Empty) + ".");
        }

        return stock;
    }
}
