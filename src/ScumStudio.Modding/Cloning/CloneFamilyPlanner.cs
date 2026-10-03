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
}

/// <summary>The packages a clone creates: old → new package paths, ordered with the primary asset first.</summary>
/// <param name="Template">The stock asset that is cloned.</param>
/// <param name="NewPrimary">Package path of the new primary asset.</param>
/// <param name="Packages">(old, new) package paths, the primary first.</param>
public sealed record ClonePlan(string Template, string NewPrimary, IReadOnlyList<KeyValuePair<string, string>> Packages)
{
    /// <summary>The rename map of the plan.</summary>
    public PackageMap ToMap() => new(Packages);
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

    /// <summary>Plans an item clone (weapon, magazine, ammunition, projectile): the item and its <c>_ES</c>.</summary>
    /// <exception cref="ArgumentException">Invalid or already used name.</exception>
    public static ClonePlan PlanItem(AssetCatalog catalog, string itemPackage, string newName)
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
        var packages = members
            .Select(m => new KeyValuePair<string, string>(m, PackageMap.Folder(m) + "/" + RenameLeaf(PackageMap.Leaf(m), token, newToken)))
            .ToList();
        Validate(catalog, template, packages);
        return new ClonePlan(template, newPrimary, packages);
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
