using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Formats.Packages;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Registry;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding;

/// <summary>What to build: clones and stored-value edits (on stock or cloned packages).</summary>
/// <param name="Clones">Clone plans, applied first.</param>
/// <param name="Edits">Package path → edits of that package (a clone's new path, or a stock package to override).</param>
public sealed record AssetModRequest(IReadOnlyList<ClonePlan> Clones, IReadOnlyDictionary<string, IReadOnlyList<TunableEdit>> Edits)
{
    /// <summary>True when nothing is requested.</summary>
    public bool IsEmpty => Clones.Count == 0 && Edits.Count == 0;
}

/// <summary>A finished package to stage under <c>SCUM/Content/…</c>.</summary>
/// <param name="PackagePath">Package path.</param>
/// <param name="Bytes">Header + .uexp.</param>
/// <param name="UBulk">.ubulk, if any.</param>
/// <param name="IsClone">True for a new (cloned) package, false for an overridden stock package.</param>
public sealed record BuiltAssetPackage(string PackagePath, PackageBytes Bytes, byte[]? UBulk, bool IsClone)
{
    /// <summary>Pak/staging path of the header (<c>SCUM/Content/…/X.uasset</c>).</summary>
    public string HeaderFilePath(string projectName = AssetPaths.DefaultProjectName) =>
        AssetPaths.ToFilePathWithoutExtension(PackagePath, projectName) + ".uasset";
}

/// <summary>Result of <see cref="AssetModBuilder.Build"/>.</summary>
/// <param name="Packages">Every package to stage.</param>
/// <param name="AssetRegistry">The merged <c>AssetRegistry.bin</c> when clones were registered, else null.</param>
/// <param name="Registered">Registry records added.</param>
/// <param name="Applied">Edits written, as (package, key, old, new).</param>
/// <param name="Warnings">Problems that did not stop the build.</param>
public sealed record AssetModBuildResult(
    IReadOnlyList<BuiltAssetPackage> Packages,
    byte[]? AssetRegistry,
    IReadOnlyList<RegisteredAsset> Registered,
    IReadOnlyList<(string Package, string Key, string Old, string New)> Applied,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds the vehicle/weapon part of a mod from pristine packages: clones every planned family (names remapped,
/// payloads verbatim), writes the stat edits, and registers the clones in the source's effective
/// <c>AssetRegistry.bin</c> (so other installed mods' records are kept). The same request applied to a client and a
/// server catalog gives the two cooks of the mod.
/// </summary>
public static class AssetModBuilder
{
    /// <summary>Provider path of the cooked registry.</summary>
    public const string AssetRegistryPath = "SCUM/AssetRegistry.bin";

    /// <summary>Builds <paramref name="request"/> against <paramref name="catalog"/>.</summary>
    public static AssetModBuildResult Build(AssetCatalog catalog, AssetModRequest request)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<string>();
        var built = new Dictionary<string, (CookedPackage Package, PackageBytes Bytes, bool IsClone)>(StringComparer.OrdinalIgnoreCase);
        var maps = new List<PackageMap>();

        foreach (var plan in request.Clones)
        {
            var map = plan.ToMap();
            maps.Add(map);
            foreach (var (oldPath, newPath) in plan.Packages)
            {
                CookedPackage source;
                try
                {
                    source = ModdableAssets.ReadPackage(catalog, oldPath);
                }
                catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException)
                {
                    warnings.Add($"{oldPath}: not cloned ({ex.Message}).");
                    continue;
                }

                var clone = PackageCloner.Clone(source, oldPath, map);
                var parsed = CookedPackage.Parse(clone.Bytes.UAsset, clone.Bytes.UExp, clone.UBulk, newPath);
                built[newPath] = (parsed, clone.Bytes, true);
            }
        }

        var applied = new List<(string, string, string, string)>();
        foreach (var (packagePath, edits) in request.Edits)
        {
            if (edits.Count == 0)
            {
                continue;
            }

            var path = PackageMap.Normalize(packagePath);
            CookedPackage package;
            var isClone = false;
            if (built.TryGetValue(path, out var existing))
            {
                package = existing.Package;
                isClone = existing.IsClone;
            }
            else
            {
                try
                {
                    package = ModdableAssets.ReadPackage(catalog, path);
                }
                catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException)
                {
                    warnings.Add($"{path}: edits skipped ({ex.Message}).");
                    continue;
                }
            }

            try
            {
                var result = TunablePatcher.Apply(package, edits);
                built[path] = (CookedPackage.Parse(result.Bytes.UAsset, result.Bytes.UExp, package.UBulk, path), result.Bytes, isClone);
                applied.AddRange(result.Applied.Select(a => (path, a.Key, a.Old, a.New)));
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add($"{path}: edits skipped ({ex.Message}).");
            }
        }

        byte[]? registryBytes = null;
        var registered = new List<RegisteredAsset>();
        if (maps.Count > 0)
        {
            if (catalog.Provider.Files.TryGetValue(AssetRegistryPath, out var registryFile))
            {
                var registry = AssetRegistryFile.Parse(registryFile.Read());
                foreach (var map in maps)
                {
                    registered.AddRange(RegistryCloner.Register(registry, map, warnings));
                }

                registryBytes = registry.Save();
            }
            else
            {
                warnings.Add($"{AssetRegistryPath} is not in {catalog.DisplayName}: the clones are built but not registered, so the game's spawn commands will not find them.");
            }
        }

        var packages = built
            .OrderBy(b => b.Key, StringComparer.OrdinalIgnoreCase)
            .Select(b => new BuiltAssetPackage(b.Key, b.Value.Bytes, b.Value.Package.UBulk, b.Value.IsClone))
            .ToList();
        return new AssetModBuildResult(packages, registryBytes, registered, applied, warnings);
    }
}
