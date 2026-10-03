using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Level.Editing;

/// <summary>Helpers shared by the app, the CLI and the MCP tools for vehicle/item packages of a project.</summary>
public static class AssetEditing
{
    /// <summary>
    /// The package as the mod starts from: the stock package from <paramref name="catalog"/>, or — when a clone of the
    /// project created <paramref name="packagePath"/> — the in-memory rename-clone of its stock template.
    /// </summary>
    /// <exception cref="FileNotFoundException">Neither the package nor its template is in the catalog.</exception>
    public static CookedPackage ReadForEditing(AssetCatalog catalog, EditState? state, string packagePath)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var path = PackageMap.Normalize(packagePath);
        if (state?.FindCloneOf(path) is not { } clone)
        {
            return ModdableAssets.ReadPackage(catalog, path);
        }

        var pair = clone.Packages.First(p => string.Equals(p.New, path, StringComparison.OrdinalIgnoreCase));
        var map = new PackageMap(clone.Packages.Select(p => new KeyValuePair<string, string>(p.Old, p.New)));
        var cloned = PackageCloner.Clone(ModdableAssets.ReadPackage(catalog, pair.Old), pair.Old, map);
        return CookedPackage.Parse(cloned.Bytes.UAsset, cloned.Bytes.UExp, cloned.UBulk, path);
    }

    /// <summary>
    /// Resolves an asset name or package path to a package: a path in the catalog or created by a clone, a moddable
    /// asset's name (<c>Weapon_RPK-74</c>), an entity setup's name (<c>Weapon_RPK-74_ES</c>) or a clone package's name.
    /// Null when nothing matches.
    /// </summary>
    public static string? ResolvePackage(AssetCatalog catalog, EditState? state, string nameOrPath)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        var text = nameOrPath.Trim();
        var clonePackages = state?.AssetClones.SelectMany(c => c.Packages).Select(p => p.New).ToList() ?? [];
        if (text.StartsWith('/'))
        {
            var p = PackageMap.Normalize(text);
            return catalog.PackageExists(p) || clonePackages.Contains(p, StringComparer.OrdinalIgnoreCase) ? p : null;
        }

        var clone = clonePackages.FirstOrDefault(p => string.Equals(PackageMap.Leaf(p), text, StringComparison.OrdinalIgnoreCase));
        if (clone is not null)
        {
            return clone;
        }

        foreach (var a in ModdableAssets.Find(catalog))
        {
            if (string.Equals(a.Name, text, StringComparison.OrdinalIgnoreCase))
            {
                return a.PackagePath;
            }

            if (a.EntitySetupPath is { } es && string.Equals(PackageMap.Leaf(es), text, StringComparison.OrdinalIgnoreCase))
            {
                return es;
            }
        }

        return null;
    }
}
