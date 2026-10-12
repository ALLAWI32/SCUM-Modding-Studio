using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.VirtualFileSystem;

namespace ScumStudio.Assets.Catalog;

/// <summary>
/// Where a package of an <see cref="AssetCatalog"/> comes from: the pak (or loose folder) that provides it, and whether
/// the game itself ships it. A mod's <c>_P.pak</c> (the studio's own included) is never stock, so an existence check on
/// "the game files" can tell the game's packages from what a mod added.
/// </summary>
public static class PackageOrigins
{
    /// <summary>
    /// The file name of the pak that provides <paramref name="packagePath"/> (the one that wins the read order), the loose
    /// folder it was read from, or null when the catalog has no such package.
    /// </summary>
    public static string? ContainerOf(this AssetCatalog catalog, string packagePath)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.TryGetPackageFile(packagePath, out var file))
        {
            return null;
        }

        return file switch
        {
            VfsEntry entry => entry.Vfs.Name,
            OsGameFile loose => LooseRoot(catalog, loose.ActualFile.FullName) ?? loose.ActualFile.DirectoryName,
            _ => null,
        };
    }

    /// <summary>
    /// True when a mounted container whose name does not end in <c>_P</c> (a game pak, not a patch or mod pak) holds
    /// <paramref name="packagePath"/>, even when a mod pak overrides it. In a catalog with no pak containers at all (a loose
    /// extract) every package counts as stock.
    /// </summary>
    public static bool IsStock(this AssetCatalog catalog, string packagePath)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.TryGetPackageFile(packagePath, out var file))
        {
            return false;
        }

        if (catalog.Provider is not IVfsFileProvider vfs || vfs.MountedVfs.Count == 0)
        {
            return true;
        }

        if (file is VfsEntry entry && !IsModContainer(entry.Vfs.Name))
        {
            return true;
        }

        return vfs.MountedVfs.Any(v => !IsModContainer(v.Name) && v.Files.ContainsKey(file.Path));
    }

    /// <summary>True for a patch or mod container (<c>*_P.pak</c>, <c>*_P.utoc</c>), which the game loads over its own.</summary>
    public static bool IsModContainer(string containerFileName) =>
        Path.GetFileNameWithoutExtension(containerFileName ?? string.Empty).EndsWith("_P", StringComparison.OrdinalIgnoreCase);

    private static string? LooseRoot(AssetCatalog catalog, string file) =>
        (catalog.Provider as ScumFileProvider)?.LooseRoots
            .Where(r => file.StartsWith(r.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Length)
            .FirstOrDefault();
}
