using System.Runtime.CompilerServices;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding.Crafting;

/// <summary>One level of a base element family: the recipe that makes it and the element it makes.</summary>
/// <param name="Recipe">The recipe package (level 0 the placeable recipe, then the <c>_upgradeRecipe</c> of the level before).</param>
/// <param name="Element">The base element package.</param>
/// <param name="Values">The <see cref="ElementSettings.Editable"/> values its class default stores, with the game's values.</param>
public sealed record ElementLevel(string Recipe, string Element, IReadOnlyList<Tunable> Values);

/// <summary>A base element of the game with its stock recipe and the game's whole upgrade chain.</summary>
/// <param name="Element">The base element (level 0).</param>
/// <param name="Recipe">Its stock placeable recipe.</param>
/// <param name="NativeClass"><c>BaseBuildingComponent</c>, or <c>BaseBuildingItemComponent</c> for an element that spawns an item.</param>
/// <param name="ItemClass">The item package a <c>BaseBuildingItemComponent</c> spawns (<c>_itemClass</c>: a chest, a lamp, a fridge), or null.</param>
/// <param name="Levels">Level 0 and its upgrades, in the game's order.</param>
/// <param name="IsRespawnPoint">The element stores <c>_isUsedForRespawn</c> true (beds, shelters).</param>
public sealed record ElementFamily(string Element, string Recipe, string NativeClass, string? ItemClass, IReadOnlyList<ElementLevel> Levels, bool IsRespawnPoint);

/// <summary>
/// The game's base element families (SDK build 149664, <c>UBaseBuildingComponent</c>): an element is a Blueprint of the
/// native <c>BaseBuildingComponent</c> or <c>BaseBuildingItemComponent</c> (whose <c>_itemClass</c> spawns an item), made
/// by the placeable recipe whose soft <c>Product</c> names it; its hard <c>_upgradeRecipe</c> makes the next level. A bed
/// is a respawn point by its own <c>_isUsedForRespawn</c>; the respawn times are global (<c>RespawnSettings</c>, the
/// server's <c>ServerSettings.ini</c>), never per element. DLC elements and modular ones (<c>BPC_*</c>,
/// <c>ModularBaseBuildingComponent</c>: walls, floors, doors) are not offered.
/// </summary>
public static class ElementFamilies
{
    private const string Placeables = ModdableAssets.ConZ + "Items/Crafting/Recipes/Placeables/";
    private const int MaxLevels = 8;

    private static readonly ConditionalWeakTable<AssetCatalog, Lazy<Index>> Indexes = new();

    // Element -> its stock recipe (a recipe that is not an upgrade wins), static mesh -> the element showing it, upgrade
    // recipe -> the element whose _upgradeRecipe it is.
    private sealed record Index(IReadOnlyDictionary<string, string> Recipes, IReadOnlyDictionary<string, string> Meshes, IReadOnlyDictionary<string, string> UpgradedFrom);

    /// <summary>
    /// The family of <paramref name="source"/>: a base element, or a static mesh (or Blueprint) showing one; null when it is
    /// none (<see cref="WhyNot"/> says why). The first call reads the placeable recipes and their elements (a few hundred
    /// package headers), so it runs on a worker.
    /// </summary>
    public static ElementFamily? Of(AssetCatalog catalog, string source) => Resolve(catalog, source).Family;

    /// <summary>Why <paramref name="source"/> has no family to clone, or null when it has one.</summary>
    public static string? WhyNot(AssetCatalog catalog, string source) => Resolve(catalog, source).Reason;

    private static (ElementFamily? Family, string? Reason) Resolve(AssetCatalog catalog, string source)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var element = PackageMap.Normalize(source);
        var isClass = Safe(() => catalog.GetExports(element))?.Any(e => e.ClassName == "BlueprintGeneratedClass") == true; // a mesh's header only
        var native = isClass ? CraftablesPlanner.NativeClassOf(catalog, element) : null;
        if (Refused(element, native) is { } refused)
        {
            return (null, refused);
        }

        var index = Indexes.GetValue(catalog, c => new Lazy<Index>(() => Build(c))).Value;
        if (!IsElement(native))
        {
            if (Mesh(catalog, element) is not { } mesh || !index.Meshes.TryGetValue(mesh, out var shown))
            {
                return (null, $"{PackageMap.Leaf(element)} is not a base element of the game and shows no base element's mesh");
            }

            element = shown;
        }

        // An upgrade level is cloned from the start of its chain: its only recipe upgrades a placed element.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { element };
        while (index.Recipes.TryGetValue(element, out var made) && index.UpgradedFrom.TryGetValue(made, out var before) && seen.Add(before))
        {
            element = before;
        }

        native = CraftablesPlanner.NativeClassOf(catalog, element);
        if (Refused(element, native) is { } modular)
        {
            return (null, modular);
        }

        if (!index.Recipes.TryGetValue(element, out var recipe) || !IsElement(native))
        {
            return (null, $"{PackageMap.Leaf(element)} has no placeable recipe in the game to clone");
        }

        try
        {
            return (Family(catalog, element, recipe, native!), null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or EndOfStreamException)
        {
            return (null, $"{PackageMap.Leaf(element)} cannot be read: {ex.Message}");
        }
    }

    private static ElementFamily Family(AssetCatalog catalog, string element, string recipe, string native)
    {
        var levels = new List<ElementLevel>();
        string? itemClass = null;
        var respawn = false;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (levels.Count < MaxLevels && visited.Add(element) && visited.Add(recipe))
        {
            var package = ModdableAssets.ReadPackage(catalog, element);
            var cdo = DefaultsOf(package, element);
            var properties = cdo >= 0 ? package.ReadProperties(cdo) : null;
            levels.Add(new ElementLevel(recipe, element, Values(package, element)));
            if (levels.Count == 1)
            {
                itemClass = native == "BaseBuildingItemComponent" ? ImportedPackage(package, properties?.Find("_itemClass")?.Value) : null;
                respawn = properties?.Find("_isUsedForRespawn")?.Value is BoolValue { Value: true };
            }

            if (ImportedPackage(package, properties?.Find("_upgradeRecipe")?.Value) is not { } next || Product(catalog, next) is not { } product
                || !catalog.PackageExists(product) || CraftablesPlanner.NativeClassOf(catalog, product) is var upgraded && (!IsElement(upgraded) || Refused(product, upgraded) is not null))
            {
                break;
            }

            (recipe, element) = (next, product);
        }

        return new ElementFamily(levels[0].Element, levels[0].Recipe, native, itemClass, levels, respawn);
    }

    private static bool IsElement(string? native) => native is "BaseBuildingComponent" or "BaseBuildingItemComponent";

    private static string? Refused(string path, string? native)
    {
        if (path.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase))
        {
            return $"{PackageMap.Leaf(path)} is DLC content: DLC base elements are not offered yet";
        }

        var modular = PackageMap.Leaf(path).StartsWith("BPC_", StringComparison.OrdinalIgnoreCase) && path.Contains("/BaseBuilding/", StringComparison.OrdinalIgnoreCase);
        return native == "ModularBaseBuildingComponent" || modular
            ? $"{PackageMap.Leaf(path)} is a modular base element (walls, floors, doors): modular elements are not offered yet"
            : null;
    }

    private static Index Build(AssetCatalog catalog)
    {
        var recipes = catalog.PackageFiles
            .Select(f => AssetPaths.ToPackagePath(f, catalog.ProjectName))
            .Where(p => p.StartsWith(Placeables, StringComparison.OrdinalIgnoreCase) && PackageMap.Leaf(p).StartsWith("CR_", StringComparison.OrdinalIgnoreCase)
                        && !p.Contains("/Skins/", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("_Skin", StringComparison.OrdinalIgnoreCase)) // CR_Bed_Improvised_Upgrade_Skin
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .AsParallel().AsOrdered()
            .Select(r => (Recipe: r, Product: Product(catalog, r)))
            .ToList();

        var byElement = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (recipe, product) in recipes)
        {
            if (product is not null && (!byElement.TryGetValue(product, out var known) || IsUpgrade(known) && !IsUpgrade(recipe)))
            {
                byElement[product] = recipe;
            }
        }

        // Base levels first, so a mesh an upgrade shares resolves to the element placed first.
        var elements = byElement.Keys.OrderBy(e => IsUpgrade(byElement[e])).ThenBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();
        var read = elements.AsParallel().AsOrdered().Select(e => (Element: e, Meshes: Safe(() => CraftablesPlanner.StaticMeshImports(catalog, e)) ?? [], Upgrade: Safe(() => UpgradeOf(catalog, e)))).ToList();
        var meshes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var upgradedFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (element, shown, upgrade) in read)
        {
            foreach (var mesh in shown)
            {
                meshes.TryAdd(mesh, element);
            }

            if (upgrade is not null)
            {
                upgradedFrom.TryAdd(upgrade, element);
            }
        }

        return new Index(byElement, meshes, upgradedFrom);
    }

    private static bool IsUpgrade(string recipe) => PackageMap.Leaf(recipe).StartsWith("CR_Upgrade_", StringComparison.OrdinalIgnoreCase);

    private static string? UpgradeOf(AssetCatalog catalog, string element)
    {
        var package = ModdableAssets.ReadPackage(catalog, element);
        var cdo = DefaultsOf(package, element);
        return cdo < 0 ? null : ImportedPackage(package, package.ReadProperties(cdo).Find("_upgradeRecipe")?.Value);
    }

    private static string? Product(AssetCatalog catalog, string recipe) => Safe(() => CraftablesPlanner.ProductPackage(catalog, recipe));

    private static string? Mesh(AssetCatalog catalog, string source) => Safe(() => CraftablesPlanner.MeshOf(catalog, source));

    private static T? Safe<T>(Func<T?> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or FormatException or ArgumentException or EndOfStreamException)
        {
            return null; // an unreadable package is no element
        }
    }

    /// <summary>The editable values the element's class default stores (top level), with the game's values.</summary>
    private static IReadOnlyList<Tunable> Values(CookedPackage package, string element)
    {
        var cdo = $"Default__{PackageMap.Leaf(element)}_C";
        return TunableReader.Read(package).Where(t => t.Export == cdo && ElementSettings.Editable.Contains(t.Path, StringComparer.Ordinal)).ToList();
    }

    /// <summary>
    /// The mesh the element's class default shows (<c>StaticMesh</c>, null when it inherits it) and how many
    /// <c>_staticMeshVariations</c> it stores; its <c>_weatherMaskMesh</c> is no look of its own.
    /// </summary>
    internal static (string? Mesh, int Variations) MeshOf(AssetCatalog catalog, string element)
    {
        var package = ModdableAssets.ReadPackage(catalog, element);
        var cdo = DefaultsOf(package, element);
        var properties = cdo >= 0 ? package.ReadProperties(cdo) : null;
        return (ImportedPackage(package, properties?.Find("StaticMesh")?.Value), properties?.Find("_staticMeshVariations")?.Value is ArrayValue list ? list.Items.Count : 0);
    }

    /// <summary>The index of the class default export <c>Default__&lt;leaf&gt;_C</c>, or -1.</summary>
    internal static int DefaultsOf(CookedPackage package, string element)
    {
        var name = $"Default__{PackageMap.Leaf(element)}_C";
        return Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.ResolveName(package.Exports[i].ObjectName) == name, -1);
    }

    /// <summary>The package an object or class property names: a hard import's outer package, or a soft path's package.</summary>
    private static string? ImportedPackage(CookedPackage package, PropertyValue? value) => value switch
    {
        ObjectValue { Index: < 0 } o when package.Imports[-o.Index - 1] is { OuterIndex: < 0 } import => package.ResolveName(package.Imports[-import.OuterIndex - 1].ObjectName),
        SoftObjectValue { AssetPath.Length: > 0 } soft => PackageMap.Normalize(soft.AssetPath),
        _ => null,
    };
}
