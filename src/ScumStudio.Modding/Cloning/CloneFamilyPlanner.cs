using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Catalog;

namespace ScumStudio.Modding.Cloning;

/// <summary>Options of a vehicle clone.</summary>
public sealed record VehicleCloneOptions
{
    /// <summary>Also clone the vehicle's attachment classes (chassis, doors, …) so they can get their own names and values.</summary>
    public bool IncludeAttachments { get; init; } = true;

    /// <summary>Also clone the manual spawn presets that reference the vehicle (what <c>#SpawnVehicle</c> uses).</summary>
    public bool IncludeSpawnPresets { get; init; } = true;

    /// <summary>Give the clone its own paint: copies of the meshes that wear paint and of that paint (see <see cref="CloneFamilyPlanner.PaintPackages"/>).</summary>
    public bool IncludePaint { get; init; } = true;
}

/// <summary>The packages a clone creates: old → new package paths, ordered with the primary asset first.</summary>
/// <param name="Template">The stock asset that is cloned.</param>
/// <param name="NewPrimary">Package path of the new primary asset.</param>
/// <param name="Packages">(old, new) package paths, the primary first.</param>
public sealed record ClonePlan(string Template, string NewPrimary, IReadOnlyList<KeyValuePair<string, string>> Packages)
{
    /// <summary>
    /// (stock, existing) package paths the clones point at instead (a craftable's chosen mesh in place of the template's):
    /// remapped in the clones' names like <see cref="Packages"/>, but nothing is copied to them.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> Redirects { get; init; } = [];

    /// <summary>The rename map of the plan.</summary>
    public PackageMap ToMap() => new(Packages.Concat(Redirects));
}

/// <summary>
/// Decides which packages a clone copies and what they are called — the generic form of the owner's
/// <c>variant_jobs</c>: an item is cloned with its entity setup (<c>X</c>, <c>X_ES</c>); a vehicle (<c>BPC_T</c>) with
/// every package of its folder whose name carries the token <c>T</c> and is reachable from it (entity setup, item
/// container, anim/physics assets, attachments), plus the manual spawn presets that reference it. Meshes, materials and
/// textures live in other folders and stay shared with the stock asset.
/// </summary>
public static partial class CloneFamilyPlanner
{
    /// <summary>Folder of the manual spawn presets.</summary>
    public const string ManualSpawnPresets = ModdableAssets.ConZ + "Vehicles/SpawningPresets/ManualSpawn";

    /// <summary>True when <paramref name="name"/> is a valid new asset name (letters, digits, '_' and '-', starts with a letter).</summary>
    public static bool IsValidName(string? name) => name is not null && ValidName().IsMatch(name);

    /// <summary>
    /// Plans an item clone (weapon, magazine, ammunition, projectile): the item and its <c>_ES</c>, and with
    /// <paramref name="includePaint"/> the meshes it shows that wear paint and that paint (the clone's colour is its own).
    /// </summary>
    /// <exception cref="ArgumentException">Invalid or already used name.</exception>
    public static ClonePlan PlanItem(AssetCatalog catalog, string itemPackage, string newName, bool includePaint = true)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var template = PackageMap.Normalize(itemPackage);
        if (!IsValidName(newName))
        {
            throw new ArgumentException($"'{newName}' is not a valid asset name (letters, digits, '_' or '-', starting with a letter).", nameof(newName));
        }

        var folder = PackageMap.Folder(template);
        var newPrimary = $"{folder}/{newName}";
        var packages = new List<KeyValuePair<string, string>> { new(template, newPrimary) };
        var es = template + "_ES";
        if (catalog.PackageExists(es))
        {
            packages.Add(new(es, newPrimary + "_ES"));
        }

        if (includePaint)
        {
            var leaf = PackageMap.Leaf(template);
            packages.AddRange(PaintPackages(catalog, packages.Select(p => p.Key))
                .Select(p => new KeyValuePair<string, string>(p, PackageMap.Folder(p) + "/" + RenameLeaf(PackageMap.Leaf(p), leaf, newName))));
        }

        Validate(catalog, template, packages);
        return new ClonePlan(template, newPrimary, packages);
    }

    /// <summary>Plans a vehicle clone <c>BPC_&lt;Token&gt;</c> → <c>BPC_&lt;newToken&gt;</c>.</summary>
    /// <exception cref="ArgumentException">Invalid or already used name, or not a vehicle Blueprint.</exception>
    public static ClonePlan PlanVehicle(AssetCatalog catalog, string vehiclePackage, string newToken, VehicleCloneOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        options ??= new VehicleCloneOptions();
        var template = PackageMap.Normalize(vehiclePackage);
        var leaf = PackageMap.Leaf(template);
        if (!leaf.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{template} is not a vehicle Blueprint (BPC_*).", nameof(vehiclePackage));
        }

        if (newToken.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase))
        {
            newToken = newToken[4..];
        }

        if (!IsValidName(newToken))
        {
            throw new ArgumentException($"'{newToken}' is not a valid vehicle name (letters, digits, '_' or '-', starting with a letter).", nameof(newToken));
        }

        var token = ModdableAssets.VehicleToken(leaf);
        var folder = PackageMap.Folder(template);
        var members = new List<string> { template };
        var seen = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(members);

        void Consider(string candidate)
        {
            var c = PackageMap.Normalize(candidate);
            if (seen.Contains(c) || !catalog.PackageExists(c))
            {
                return;
            }

            var inFolder = c.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
            var isPreset = c.StartsWith(ManualSpawnPresets + "/", StringComparison.OrdinalIgnoreCase);
            if (!(inFolder || isPreset) || !CarriesToken(PackageMap.Leaf(c), token))
            {
                return;
            }

            if (!options.IncludeAttachments && c.Contains("/Attachments/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            seen.Add(c);
            members.Add(c);
            queue.Enqueue(c);
        }

        Consider($"{folder}/{token}_ES");
        if (options.IncludeSpawnPresets)
        {
            foreach (var preset in PresetsReferencing(catalog, template))
            {
                Consider(preset);
            }
        }

        while (queue.Count > 0)
        {
            foreach (var referenced in ModdableAssets.ReadImportedPackages(catalog, queue.Dequeue()))
            {
                Consider(referenced);
            }
        }

        var newPrimary = $"{folder}/BPC_{newToken}";
        if (options.IncludePaint)
        {
            members.AddRange(PaintPackages(catalog, members));
        }

        var packages = members
            .Select(m => new KeyValuePair<string, string>(m, PackageMap.Folder(m) + "/" + RenameLeaf(PackageMap.Leaf(m), token, newToken)))
            .ToList();
        Validate(catalog, template, packages);
        return new ClonePlan(template, newPrimary, packages);
    }

    /// <summary>The materials a clone's paint lives in (car paint and weapon materials); their instances are what it copies.</summary>
    private static readonly string[] PaintMasters =
    [
        ModdableAssets.ConZ + "Materials/Car/M_Car_01",
        ModdableAssets.ConZ + "Materials/M_Weapons_Master",
    ];

    /// <summary>
    /// The meshes the <paramref name="family"/> shows that wear paint (a material instance of <see cref="PaintMasters"/>),
    /// and those material instances: meshes and materials live outside the family's folder and are shared with the stock
    /// asset, so a clone that is to be painted on its own needs copies of both (the copied meshes then use the copied paint,
    /// the copied Blueprints the copied meshes). Meshes without paint stay shared.
    /// </summary>
    public static IReadOnlyList<string> PaintPackages(AssetCatalog catalog, IEnumerable<string> family)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(family);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paintCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in family.ToList())
        {
            foreach (var imported in ModdableAssets.ReadImportedPackages(catalog, member))
            {
                if (!seen.Add(imported) || !IsMesh(catalog, imported))
                {
                    continue;
                }

                var paints = ModdableAssets.ReadImportedPackages(catalog, imported).Where(m => IsPaint(catalog, m, paintCache, 0)).ToList();
                if (paints.Count == 0)
                {
                    continue;
                }

                result.Add(imported);
                result.AddRange(paints.Where(p => !result.Contains(p, StringComparer.OrdinalIgnoreCase)));
            }
        }

        return result;
    }

    private static bool IsMesh(AssetCatalog catalog, string package)
    {
        try
        {
            return catalog.GetExports(package).Any(e => e.ClassName is "SkeletalMesh" or "StaticMesh");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>A material instance whose parent chain reaches a paint master (up to three instances deep).</summary>
    private static bool IsPaint(AssetCatalog catalog, string package, Dictionary<string, bool> cache, int depth)
    {
        if (cache.TryGetValue(package, out var known))
        {
            return known;
        }

        var leaf = PackageMap.Leaf(package);
        var result = false;
        if (depth < 3 && leaf.StartsWith("MI_", StringComparison.OrdinalIgnoreCase) && package.StartsWith("/Game/", StringComparison.Ordinal))
        {
            var imports = ModdableAssets.ReadImportedPackages(catalog, package);
            result = imports.Any(i => PaintMasters.Contains(i, StringComparer.OrdinalIgnoreCase))
                     || imports.Any(i => PackageMap.Leaf(i).StartsWith("MI_", StringComparison.OrdinalIgnoreCase) && IsPaint(catalog, i, cache, depth + 1));
        }

        cache[package] = result;
        return result;
    }

    /// <summary>Manual spawn presets whose header refers to <paramref name="vehiclePackage"/> (import or soft path).</summary>
    public static IReadOnlyList<string> PresetsReferencing(AssetCatalog catalog, string vehiclePackage)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var prefix = ManualSpawnPresets + "/";
        return catalog.PackageFiles
            .Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
            .Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => ModdableAssets.ReadImportedPackages(catalog, p).Contains(vehiclePackage, StringComparer.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// New leaf for a family member: the token replaced (case-insensitive), or its squashed form (no underscores, as in
    /// <c>KingletMarinerManualSpawnPreset</c>), else the new token prefixed.
    /// </summary>
    public static string RenameLeaf(string leaf, string token, string newToken)
    {
        if (leaf.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return Regex.Replace(leaf, Regex.Escape(token), newToken.Replace("$", "$$", StringComparison.Ordinal), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        var squashed = token.Replace("_", string.Empty, StringComparison.Ordinal);
        if (squashed.Length > 0 && leaf.Contains(squashed, StringComparison.OrdinalIgnoreCase))
        {
            var newSquashed = newToken.Replace("_", string.Empty, StringComparison.Ordinal).Replace("$", "$$", StringComparison.Ordinal);
            return Regex.Replace(leaf, Regex.Escape(squashed), newSquashed, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return newToken + "_" + leaf;
    }

    private static bool CarriesToken(string leaf, string token) =>
        leaf.Contains(token, StringComparison.OrdinalIgnoreCase)
        || leaf.Contains(token.Replace("_", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

    private static void Validate(AssetCatalog catalog, string template, IReadOnlyList<KeyValuePair<string, string>> packages)
    {
        foreach (var (oldPath, newPath) in packages)
        {
            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The new name gives {newPath} the same path as the stock package.");
            }

            if (catalog.PackageExists(newPath))
            {
                throw new ArgumentException($"{newPath} already exists in the game files; choose another name.");
            }
        }

        var duplicate = packages.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Two packages of {template} would both be called {duplicate.Key}.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_\-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();
}
