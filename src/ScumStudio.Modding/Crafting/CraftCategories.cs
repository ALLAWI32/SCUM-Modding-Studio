using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Crafting;

/// <summary>
/// A section of the game's crafting menu: the gameplay tag stock recipes carry in their <c>CraftingMetadata_RecipeCategory</c>
/// (<c>CraftingCategoryTag</c>, with <c>DisplayDomain</c> and <c>DisplayPriority</c>).
/// </summary>
/// <param name="Tag">The gameplay tag (<c>CraftingCategory.Items.MeleeWeapons</c>).</param>
/// <param name="Label">Its English name (<see cref="CraftCategories.Label"/>).</param>
/// <param name="Group">The first part of the label (<c>Ranged weapons</c>).</param>
/// <param name="Placeable">A section of placed things (<c>PlaceableCraftingRecipe</c>), not of inventory items.</param>
/// <param name="Priority">The lowest display priority of its recipes.</param>
/// <param name="Domain">The <c>DisplayDomain</c> most of its recipes store, or null.</param>
/// <param name="Recipes">Its stock recipes (<c>CR_*</c> names, sorted).</param>
public sealed record CraftCategory(string Tag, string Label, string Group, bool Placeable, int Priority, string? Domain, IReadOnlyList<string> Recipes)
{
    /// <summary>A stock recipe of the section.</summary>
    public string SampleRecipe => Recipes.Count > 0 ? Recipes[0] : string.Empty;
}

/// <summary>
/// The sections of the game's crafting menu, read from the stock recipes (owner: "every craftable gets a 'Shows in'
/// category"). The menu lists the tags the game registers, with the names and order of its <c>CraftingUIData</c>
/// (<c>CategoryNames</c>, <c>ItemCategoryOrder</c>, <c>PlaceableCategoryOrder</c>): an unregistered tag is invalid, so a
/// craftable can only show in a section stock recipes already use, never in one of a new name.
/// </summary>
public static class CraftCategories
{
    /// <summary>
    /// The recipe whose section a building (a mesh under <c>/Models/Buildings/</c>) shows in: the modular walls of base
    /// building. The game files a modular piece's section only on its first tier (<c>_Twig</c>); the stronger tiers
    /// (<c>CR_Base_Modular_Wall_Wood</c> …) are its upgrades and carry none (<see cref="SectionOf"/> finds theirs).
    /// </summary>
    public const string WallRecipe = "CR_Base_Modular_Wall_Twig";

    /// <summary>The recipe whose section a sofa shows in.</summary>
    public const string SofaRecipe = "CR_Sofa_Improvised_Single";

    /// <summary>The class of a recipe's section sub-object.</summary>
    public const string MetadataClass = "CraftingMetadata_RecipeCategory";

    // Read once per catalog: about 900 recipe packages.
    private static readonly ConditionalWeakTable<AssetCatalog, Lazy<IReadOnlyList<CraftCategory>>> Sections = new();

    /// <summary>
    /// Every section of the catalog's stock recipes (<c>Items/Crafting/Recipes/</c> and the DLC packs' <c>CR_*</c>; skins,
    /// unreadable packages and the studio's own <c>CR_SS_*</c> are skipped), building sections first, then by label. The
    /// first call reads the recipe packages, so it runs on a worker.
    /// </summary>
    public static IReadOnlyList<CraftCategory> Read(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Sections.GetValue(catalog, c => new Lazy<IReadOnlyList<CraftCategory>>(() => Scan(c))).Value;
    }

    /// <summary>True for a stock recipe package <see cref="Read"/> looks at.</summary>
    public static bool IsStockRecipe(string packagePath)
    {
        var leaf = PackageMap.Leaf(packagePath);
        return leaf.StartsWith("CR_", StringComparison.OrdinalIgnoreCase) && !leaf.StartsWith("CR_SS_", StringComparison.OrdinalIgnoreCase)
            && !leaf.EndsWith("_Skin", StringComparison.OrdinalIgnoreCase) && !packagePath.Contains("/Skins/", StringComparison.OrdinalIgnoreCase)
            && (packagePath.Contains("/Crafting/Recipes/", StringComparison.OrdinalIgnoreCase) || packagePath.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The section of one recipe package (its <see cref="CraftCategory.Recipes"/> = the package's name), or null when it has none.</summary>
    public static CraftCategory? Of(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var recipe = Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.GetExportClassName(i).EndsWith("CraftingRecipe", StringComparison.Ordinal), -1);
        var index = CategoryExport(package);
        if (recipe < 0 || index < 0)
        {
            return null;
        }

        var recipeClass = package.GetExportClassName(recipe);
        var placeable = recipeClass.Contains("Placeable", StringComparison.Ordinal);
        var block = package.ReadProperties(index);
        if ((!placeable && !recipeClass.Contains("Item", StringComparison.Ordinal)) || TagName(block) is not { } tag)
        {
            return null;
        }

        var priority = block.Find("DisplayPriority")?.Value switch { ByteValue b => b.Value, IntValue i => i.Value, _ => 0 };
        var domain = block.Find("DisplayDomain")?.Value is NameValue { Value: not ("None" or "") } d ? d.Value : null;
        var label = Label(tag);
        return new CraftCategory(tag, label, Group(tag), placeable, priority, domain, [PackageMap.Leaf(package.BasePath ?? string.Empty)]);
    }

    /// <summary>
    /// One section per tag of <paramref name="recipes"/> (<see cref="Of"/> per package): building when any of its recipes is
    /// placed, the lowest priority, the most common domain, every recipe; building sections first, then by label.
    /// </summary>
    public static IReadOnlyList<CraftCategory> Merge(IEnumerable<CraftCategory> recipes) =>
        recipes.GroupBy(r => r.Tag, StringComparer.Ordinal)
            .Select(g => g.First() with
            {
                Placeable = g.Any(r => r.Placeable),
                Priority = g.Min(r => r.Priority),
                Domain = g.Where(r => r.Domain is not null).GroupBy(r => r.Domain).OrderByDescending(d => d.Count()).Select(d => d.Key).FirstOrDefault(),
                Recipes = g.SelectMany(r => r.Recipes).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            })
            .OrderByDescending(c => c.Placeable).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// The parts of a tag the menu shows: <c>CraftingCategory.Items.RangedWeapons.Firearms</c> → <c>RangedWeapons</c>,
    /// <c>Firearms</c> (the root and the Items/Placeables level are the same for a whole menu).
    /// </summary>
    public static IReadOnlyList<string> Segments(string tag)
    {
        var parts = tag.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 1 && parts[0] == "CraftingCategory")
        {
            parts.RemoveAt(0);
        }

        if (parts.Count > 1 && parts[0] is "Items" or "Placeables")
        {
            parts.RemoveAt(0);
        }

        return parts;
    }

    /// <summary>A tag in plain English: <c>CraftingCategory.Placeables.BaseBuilding.Walls</c> → <c>Base building › Walls</c>.</summary>
    public static string Label(string tag) => string.Join(" › ", Segments(tag).Select(Humanize));

    /// <summary>The first part of <see cref="Label"/>.</summary>
    public static string Group(string tag) => Segments(tag) is [var first, ..] ? Humanize(first) : tag;

    /// <summary>One CamelCase part of a tag in words: <c>ArrowsAndBolts</c> → <c>Arrows and bolts</c>; acronyms stay (<c>WFP</c>).</summary>
    public static string Humanize(string segment)
    {
        var words = Regex.Split(segment.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|\\s+", RegexOptions.CultureInvariant)
            .Where(w => w.Length > 0).ToList();
        return string.Join(' ', words.Select((w, i) => i == 0 || w.All(char.IsUpper) ? w : w.ToLowerInvariant()));
    }

    /// <summary>True when a craftable of <paramref name="kind"/> is placed in the world (a building recipe; a base element of the game too), false for an inventory item.</summary>
    public static bool IsPlaceable(CraftKind kind) => kind is CraftKind.Furniture or CraftKind.Station or CraftKind.Element;

    /// <summary>
    /// The section stock recipe <paramref name="recipe"/> (a <c>CR_*</c> name or package path) shows in, or null. A modular
    /// base piece's upgrade tier (<c>CR_Base_Modular_Wall_Wood</c>, which stores no section) shows in its first tier's
    /// (<c>CR_Base_Modular_Wall_Twig</c>).
    /// </summary>
    public static CraftCategory? SectionOf(IReadOnlyList<CraftCategory> categories, string recipe)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var leaf = PackageMap.Leaf(recipe);
        CraftCategory? Find(string name) => categories.FirstOrDefault(c => c.Recipes.Contains(name, StringComparer.OrdinalIgnoreCase));
        return Find(leaf) ?? (FirstTier(leaf) is { } first ? Find(first) : null);
    }

    /// <summary>The first tier (<c>_Twig</c>) of a modular base piece's upgrade recipe, or null for any other recipe.</summary>
    public static string? FirstTier(string recipe)
    {
        var leaf = PackageMap.Leaf(recipe);
        var cut = leaf.LastIndexOf('_');
        return leaf.StartsWith("CR_Base_Modular_", StringComparison.OrdinalIgnoreCase) && cut > "CR_Base_Modular".Length && !leaf.EndsWith("_Twig", StringComparison.OrdinalIgnoreCase)
            ? leaf[..cut] + "_Twig"
            : null;
    }

    /// <summary>True for furniture whose default section is not its template's: a building or a sofa.</summary>
    public static bool HasOwnDefault(Craftable craftable) =>
        craftable.Kind == CraftKind.Furniture && (IsBuilding(craftable) || IsSofa(craftable));

    /// <summary>
    /// The section a craftable shows in when none is chosen: its template recipe's (<paramref name="templateRecipe"/>),
    /// except furniture made from a building (a mesh under <c>/Models/Buildings/</c>: the wooden wall's, base building)
    /// or a sofa (the improvised sofa's). Null when the list has none of them.
    /// </summary>
    public static CraftCategory? DefaultFor(Craftable craftable, IReadOnlyList<CraftCategory> categories, string templateRecipe)
    {
        ArgumentNullException.ThrowIfNull(craftable);
        if (craftable.Kind == CraftKind.Furniture && IsBuilding(craftable) && SectionOf(categories, WallRecipe) is { } wall)
        {
            return wall;
        }

        if (craftable.Kind == CraftKind.Furniture && IsSofa(craftable) && SectionOf(categories, SofaRecipe) is { } sofa)
        {
            return sofa;
        }

        return SectionOf(categories, templateRecipe);
    }

    /// <summary>
    /// Null when <paramref name="tag"/> is one of the game's sections of the craftable's recipe class (building or crafting);
    /// otherwise the warning, and the craftable shows in its default section.
    /// </summary>
    public static string? Refusal(Craftable craftable, string tag, IReadOnlyList<CraftCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(craftable);
        var placeable = IsPlaceable(craftable.Kind);
        return categories.Any(c => c.Placeable == placeable && string.Equals(c.Tag, tag, StringComparison.Ordinal))
            ? null
            : $"{craftable.Name}: the section {tag} is not one of the game's {(placeable ? "building" : "crafting")} sections; it shows in its default section.";
    }

    /// <summary>The index of the package's <see cref="MetadataClass"/> export, or -1.</summary>
    public static int CategoryExport(CookedPackage package) =>
        Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.GetExportClassName(i) == MetadataClass, -1);

    /// <summary>The <c>CraftingCategoryTag.TagName</c> of a section sub-object, or null.</summary>
    internal static NameValue? TagNameValue(PropertyBlock block) =>
        block.Find("CraftingCategoryTag")?.Value is StructValue tag && tag.Properties.FirstOrDefault(p => p.Name == "TagName")?.Value is NameValue name ? name : null;

    private static string? TagName(PropertyBlock block) => TagNameValue(block) is { Value: { Length: > 0 } tag } && tag != "None" ? tag : null;

    private static bool IsBuilding(Craftable craftable) =>
        $"{craftable.Source} {craftable.Mesh}".Contains("/Models/Buildings/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSofa(Craftable craftable) =>
        Regex.IsMatch($"{PackageMap.Leaf(craftable.Source)} {PackageMap.Leaf(craftable.Mesh)} {craftable.Name}", "sofa|couch", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IReadOnlyList<CraftCategory> Scan(AssetCatalog catalog)
    {
        var entries = (catalog.Index ?? catalog.BuildIndex(resolveClasses: false)).Entries;
        return Merge(entries.Where(e => IsStockRecipe(e.PackagePath)).AsParallel().Select(e =>
        {
            try
            {
                return Of(ModdableAssets.ReadPackage(catalog, e.PackagePath));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException)
            {
                return null; // an unreadable recipe names no section
            }
        }).OfType<CraftCategory>().ToList());
    }
}
