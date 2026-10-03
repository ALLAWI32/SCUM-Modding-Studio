using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Catalog;

/// <summary>What a moddable asset is.</summary>
public enum ModdableKind
{
    /// <summary>A drivable/flyable/sailable vehicle Blueprint (<c>BPC_*</c> under <c>Vehicles/</c>).</summary>
    Vehicle,

    /// <summary>A weapon item (<c>Weapon_*</c> under <c>Items/Weapons/</c>).</summary>
    Weapon,

    /// <summary>A magazine (<c>Magazine_*</c> under <c>Items/Weapons/Weapon_Clips/</c>).</summary>
    Magazine,

    /// <summary>An ammunition item (<c>Cal_*</c> under <c>Items/Ammunition/</c>).</summary>
    Ammo,

    /// <summary>A projectile class (<c>BP_WeaponBullet_*</c>): muzzle velocity, damage, penetration.</summary>
    Projectile,
}

/// <summary>A stock (or cloned) asset the Vehicles/Weapons modules can tune and clone.</summary>
/// <param name="Kind">Kind.</param>
/// <param name="PackagePath">Package path (<c>/Game/ConZ_Files/…/Weapon_RPK-74</c>).</param>
/// <param name="Category">Sub-folder that groups it (Car, Airplane, Ranged_Weapons, …).</param>
public sealed record ModdableAsset(ModdableKind Kind, string PackagePath, string Category)
{
    /// <summary>Package leaf name.</summary>
    public string Name => PackageMap.Leaf(PackagePath);

    /// <summary>Entity setup package (<c>&lt;Name&gt;_ES</c>, vehicles: <c>&lt;Token&gt;_ES</c>) when it exists in the source.</summary>
    public string? EntitySetupPath { get; init; }
}

/// <summary>Finds vehicles, weapons, magazines, ammunition and projectiles in a catalog by the game's folder conventions.</summary>
public static class ModdableAssets
{
    /// <summary>Root of SCUM's game folders.</summary>
    public const string ConZ = "/Game/ConZ_Files/";

    /// <summary>Every moddable asset of <paramref name="catalog"/>, sorted by kind then name.</summary>
    public static IReadOnlyList<ModdableAsset> Find(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var packages = catalog.PackageFiles
            .Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
            .Where(p => p.StartsWith(ConZ, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var set = new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase);
        var result = new List<ModdableAsset>();
        foreach (var p in packages)
        {
            if (Classify(p) is not { } asset)
            {
                continue;
            }

            var es = EntitySetupCandidate(asset);
            result.Add(es is not null && set.Contains(es) ? asset with { EntitySetupPath = es } : asset);
        }

        return result.OrderBy(a => a.Kind).ThenBy(a => a.Category, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Classifies one package path, or null when it is not a moddable asset.</summary>
    public static ModdableAsset? Classify(string packagePath)
    {
        var p = PackageMap.Normalize(packagePath);
        var leaf = PackageMap.Leaf(p);
        if (leaf.EndsWith("_ES", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rel = p.StartsWith(ConZ, StringComparison.OrdinalIgnoreCase) ? p[ConZ.Length..] : null;
        if (rel is null)
        {
            return null;
        }

        var parts = rel.Split('/');
        if (parts[0].Equals("Vehicles", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3
            && leaf.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase)
            && !parts.Any(s => s.Equals("Attachments", StringComparison.OrdinalIgnoreCase) || s.Equals("SpawningPresets", StringComparison.OrdinalIgnoreCase)))
        {
            return new ModdableAsset(ModdableKind.Vehicle, p, parts[1]);
        }

        if (parts.Length >= 3 && parts[0].Equals("Items", StringComparison.OrdinalIgnoreCase))
        {
            if (parts[1].Equals("Weapons", StringComparison.OrdinalIgnoreCase))
            {
                if (leaf.StartsWith("Weapon_", StringComparison.OrdinalIgnoreCase))
                {
                    return new ModdableAsset(ModdableKind.Weapon, p, parts.Length > 3 ? parts[2] : "Weapons");
                }

                if (leaf.StartsWith("Magazine_", StringComparison.OrdinalIgnoreCase))
                {
                    return new ModdableAsset(ModdableKind.Magazine, p, parts.Length > 3 ? parts[2] : "Magazines");
                }
            }

            if (parts[1].Equals("Ammunition", StringComparison.OrdinalIgnoreCase))
            {
                if (leaf.StartsWith("Cal_", StringComparison.OrdinalIgnoreCase))
                {
                    return new ModdableAsset(ModdableKind.Ammo, p, "Ammunition");
                }

                if (leaf.StartsWith("BP_WeaponBullet_", StringComparison.OrdinalIgnoreCase))
                {
                    return new ModdableAsset(ModdableKind.Projectile, p, "Projectiles");
                }
            }
        }

        return null;
    }

    /// <summary>The conventional entity setup path of an asset (<c>X_ES</c>; vehicles <c>BPC_X</c> → <c>X_ES</c>).</summary>
    public static string? EntitySetupCandidate(ModdableAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var folder = PackageMap.Folder(asset.PackagePath);
        return asset.Kind switch
        {
            ModdableKind.Vehicle => $"{folder}/{VehicleToken(asset.Name)}_ES",
            ModdableKind.Projectile => null,
            _ => $"{asset.PackagePath}_ES",
        };
    }

    /// <summary>The vehicle's name token: <c>BPC_WolfsWagen</c> → <c>WolfsWagen</c>.</summary>
    public static string VehicleToken(string vehicleLeaf) =>
        vehicleLeaf.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase) ? vehicleLeaf[4..] : vehicleLeaf;

    /// <summary>Reads a package's raw header/uexp/ubulk from the catalog and parses it.</summary>
    /// <exception cref="FileNotFoundException">The package (or its .uexp) is not in the catalog.</exception>
    public static CookedPackage ReadPackage(AssetCatalog catalog, string packagePath)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.TryGetPackageFile(packagePath, out var file))
        {
            throw new FileNotFoundException($"Package not found: {packagePath}", packagePath);
        }

        var path = file.Path.Replace('\\', '/');
        var dot = path.LastIndexOf('.');
        var stem = dot > path.LastIndexOf('/') ? path[..dot] : path;
        var uasset = file.Read();
        if (!catalog.Provider.Files.TryGetValue(stem + ".uexp", out var uexpFile))
        {
            throw new FileNotFoundException($"{stem}.uexp is missing next to {path}.", stem + ".uexp");
        }

        var ubulk = catalog.Provider.Files.TryGetValue(stem + ".ubulk", out var ubulkFile) ? ubulkFile.Read() : null;
        return CookedPackage.Parse(uasset, uexpFile.Read(), ubulk, PackageMap.Normalize(packagePath));
    }

    /// <summary>
    /// Package paths a package refers to (header only): its package imports plus every <c>/Game/…</c> package or object
    /// path in its name table — SCUM vehicles and spawn presets reference attachments and vehicles through soft object
    /// paths, which are names, not imports. <c>/Script/</c> packages are excluded.
    /// </summary>
    public static IReadOnlyList<string> ReadImportedPackages(AssetCatalog catalog, string packagePath)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.TryGetPackageFile(packagePath, out var file))
        {
            return [];
        }

        CookedPackage header;
        try
        {
            header = CookedPackage.Parse(file.Read(), [], null, packagePath);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return [];
        }

        var imports = header.Imports
            .Where(i => i.OuterIndex == 0 && header.ResolveName(i.ClassName) == "Package")
            .Select(i => header.ResolveName(i.ObjectName));
        var soft = header.Names
            .Where(n => n.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.IndexOf('.') is var dot and > 0 ? n[..dot] : n);
        return imports.Concat(soft)
            .Where(n => n.StartsWith('/') && !n.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
