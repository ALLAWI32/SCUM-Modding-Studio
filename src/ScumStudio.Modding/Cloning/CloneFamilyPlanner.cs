using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats;
using ScumStudio.Formats.Packages;
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

    /// <summary>
    /// True when <paramref name="name"/> is a valid new asset name: letters, digits, '_' and '-', starts with a letter, and
    /// does not end in <c>_&lt;number&gt;</c>, which the engine reads as an FName number (<c>Rager_2</c> is "Rager" number 3).
    /// </summary>
    public static bool IsValidName(string? name) => name is not null && ValidName().IsMatch(name) && FNameRef.Split(name).Number == 0;

    /// <summary>
    /// <paramref name="name"/> as a clone may be called: a name ending in <c>_&lt;number&gt;</c> (<c>Rager_1</c>, which the engine
    /// would read as "Rager" number 2) ends in <c>_v&lt;number&gt;</c> instead (<c>Rager_v1</c>), the rule the family's own
    /// leaves follow; any other name is kept.
    /// </summary>
    public static string SafeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var (text, number) = FNameRef.Split(name);
        return number == 0 ? name : $"{text}_v{(number - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The <paramref name="number"/>-th numbered name of <paramref name="stem"/> (the game's name without <c>BPC_</c>):
    /// <c>Rager1</c>, <c>Rager2</c> …, or <c>SF19_v1</c> when the stem ends in a digit. Never ends in <c>_&lt;number&gt;</c>.
    /// </summary>
    public static string NumberedName(string stem, int number)
    {
        ArgumentException.ThrowIfNullOrEmpty(stem);
        var n = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return char.IsAsciiDigit(stem[^1]) ? $"{stem}_v{n}" : stem + n;
    }

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
            throw new ArgumentException($"'{newName}' is not a valid asset name (letters, digits, '_' or '-', starting with a letter, not ending in _<number>: Rager2 or Rager_v2, not Rager_2).", nameof(newName));
        }

        // #SpawnItem finds an item by its name alone (PrimaryAssetName): the name of an item in another folder would give the
        // game two items of one name (an Items-page clone called "Nails" beside Equipment/Active_Items/Nails).
        if (TakenItemName(catalog, newName) is { } taken)
        {
            throw new ArgumentException($"'{newName}' is already the name of {taken}; #SpawnItem finds items by their name alone, so choose another one.", nameof(newName));
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
            throw new ArgumentException($"'{newToken}' is not a valid vehicle name (letters, digits, '_' or '-', starting with a letter, not ending in _<number>: Rager2 or Rager_v2, not Rager_2).", nameof(newToken));
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
            var member = queue.Dequeue();
            foreach (var referenced in ModdableAssets.ReadImportedPackages(catalog, member))
            {
                Consider(referenced);
            }

            // D01: a member's own entity setup (the item container's X_ES) names it by a soft path; no member imports it.
            if (EntitySetupOf(catalog, member) is { } setup)
            {
                Consider(setup);
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

    /// <summary>
    /// The entity setup of <paramref name="package"/>: the <c>X_ES</c> beside it whose name table holds the class
    /// <c>X.X_C</c> (its <c>Actor</c> soft class, which is how the game finds the setup of an item such as a vehicle's item
    /// container), or null. A twin that does not name it (a static mesh <c>SM_x_ES</c>) is not one.
    /// </summary>
    public static string? EntitySetupOf(AssetCatalog catalog, string package)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var path = PackageMap.Normalize(package);
        var setup = path + "_ES";
        if (path.EndsWith("_ES", StringComparison.OrdinalIgnoreCase) || !catalog.TryGetPackageFile(setup, out var file))
        {
            return null;
        }

        try
        {
            var actor = $"{path}.{PackageMap.Leaf(path)}_C";
            return CookedPackage.Parse(file.Read(), [], null, setup).Names.Contains(actor, StringComparer.OrdinalIgnoreCase) ? setup : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="plan"/> with the entity setup (<see cref="EntitySetupOf"/>) of every copied package that it leaves
    /// behind added as <c>&lt;new&gt;_ES</c>. Without it the copy has no setup of its own, unlike every stock item: a cloned
    /// vehicle's item container had none (D01: the saved cloned cars crashed the game on the next start). Plans recorded by
    /// earlier versions lack it; the export completes them. A setup whose new path is taken is left out with a warning.
    /// </summary>
    public static ClonePlan WithEntitySetups(AssetCatalog catalog, ClonePlan plan, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(plan);
        var copied = plan.Packages.Select(p => PackageMap.Normalize(p.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taken = plan.Packages.Select(p => PackageMap.Normalize(p.Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<KeyValuePair<string, string>>();
        foreach (var (oldPath, newPath) in plan.Packages)
        {
            if (EntitySetupOf(catalog, oldPath) is not { } setup || copied.Contains(setup))
            {
                continue;
            }

            var newSetup = PackageMap.Normalize(newPath) + "_ES";
            if (taken.Contains(newSetup) || catalog.PackageExists(newSetup))
            {
                warnings?.Add($"{plan.NewPrimary}: {setup} is not copied, {newSetup} exists already; {newPath} has no entity setup of its own.");
                continue;
            }

            added.Add(new(setup, newSetup));
            copied.Add(setup);
            taken.Add(newSetup);
        }

        return added.Count == 0 ? plan : plan with { Packages = [.. plan.Packages, .. added] };
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
    /// <c>KingletMarinerManualSpawnPreset</c>), else the new token prefixed. A leaf that would end in <c>_&lt;number&gt;</c>
    /// (a paint mesh <c>SK_Winchester_1897</c>) ends in <c>_v&lt;number&gt;</c> instead, so it never splits into an FName number.
    /// </summary>
    public static string RenameLeaf(string leaf, string token, string newToken)
    {
        string renamed;
        var squashed = token.Replace("_", string.Empty, StringComparison.Ordinal);
        if (leaf.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            renamed = Regex.Replace(leaf, Regex.Escape(token), newToken.Replace("$", "$$", StringComparison.Ordinal), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        else if (squashed.Length > 0 && leaf.Contains(squashed, StringComparison.OrdinalIgnoreCase))
        {
            var newSquashed = newToken.Replace("_", string.Empty, StringComparison.Ordinal).Replace("$", "$$", StringComparison.Ordinal);
            renamed = Regex.Replace(leaf, Regex.Escape(squashed), newSquashed, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        else
        {
            renamed = newToken + "_" + leaf;
        }

        var (text, number) = FNameRef.Split(renamed);
        return number == 0 ? renamed : $"{text}_v{(number - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// The package path of a game item or vehicle package (the folders the game registers primary assets from) whose name is
    /// <paramref name="name"/> or <c>&lt;name&gt;_ES</c>, or null.
    /// </summary>
    private static string? TakenItemName(AssetCatalog catalog, string name)
    {
        foreach (var file in catalog.PackageFiles)
        {
            var leaf = Path.GetFileNameWithoutExtension(file);
            if (!leaf.StartsWith(name, StringComparison.OrdinalIgnoreCase) || (leaf.Length != name.Length && !leaf.Equals(name + "_ES", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var path = AssetPaths.ToPackagePath(file, catalog.ProjectName);
            if (path.StartsWith(ModdableAssets.ConZ + "Items/", StringComparison.OrdinalIgnoreCase) || path.StartsWith(ModdableAssets.ConZ + "Vehicles/", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
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
                throw new ArgumentException($"{newPath} already exists in the game files; choose another name. ({catalog.ContainerOf(newPath)})");
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
