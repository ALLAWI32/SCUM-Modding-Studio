using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Crafting;

/// <summary>How an item picked as an ingredient can be asked for by a recipe.</summary>
public enum IngredientMatch
{
    /// <summary>The item has a tag of its own (<c>Lead_Plate</c> → <c>CI_Lead_Plate</c>): the recipe asks for that tag.</summary>
    OwnTag,

    /// <summary>The item satisfies only group tags (<c>CI_Group_Rifles</c>: any rifle), none of its own.</summary>
    GroupOnly,

    /// <summary>The item satisfies no tag: no recipe can ask for it unless it gets a new one.</summary>
    NoTag,

    /// <summary>Not an inventory item of the game.</summary>
    NotItem,
}

/// <summary>An ingredient tag of the game: a <c>CraftingIngredientTag</c> data asset (<c>CI_*</c>).</summary>
/// <param name="Name">Its name (<c>CI_Lead_Plate</c>).</param>
/// <param name="Path">Its package path.</param>
/// <param name="Caption">Its <c>ClassRepresentativeCaption</c> (the crafting menu's text), or null.</param>
/// <param name="Representative">The item package its <c>UIClassRepresentative</c> shows, or null.</param>
/// <param name="Items">How many items' entity setups list it in <c>CraftingIngredientInstances</c> (inherited lists count).</param>
public sealed record IngredientTag(string Name, string Path, string? Caption, string? Representative, int Items);

/// <summary>What a recipe can ask for when <see cref="Item"/> is picked as an ingredient.</summary>
/// <param name="Item">The item (package path).</param>
/// <param name="Match">How it matches.</param>
/// <param name="Tag">The tag the recipe asks for (<see cref="IngredientMatch.OwnTag"/>), else null.</param>
/// <param name="Groups">The other tags the item satisfies (all of them for <see cref="IngredientMatch.GroupOnly"/>).</param>
/// <param name="NewTag">The tag it would get when the user allows one (<see cref="IngredientIndex.NewTagName"/>).</param>
public sealed record IngredientChoice(string Item, IngredientMatch Match, string? Tag, IReadOnlyList<string> Groups, string NewTag);

/// <summary>One element of an entity setup's <c>CraftingIngredientInstances</c> (<c>FCraftingIngredientInstance</c>).</summary>
/// <param name="TagPath">The tag's object path (<c>/Game/…/CI_Lead_Plate.CI_Lead_Plate</c>).</param>
/// <param name="Amount">How much of the tag one item is worth.</param>
public sealed record IngredientInstance(string TagPath, float Amount);

/// <summary>
/// The game's ingredient tags and which items satisfy them. A tag does not list its items: an item's entity setup lists the
/// tags it satisfies (<c>UItemEntitySetup.CraftingIngredientInstances</c>, an array of <c>{Type, Amount}</c>; a setup that
/// stores none inherits its parent's). Built once per catalog on a worker (every item's setup is read).
/// </summary>
public sealed partial class IngredientIndex
{
    private static readonly ConditionalWeakTable<AssetCatalog, Lazy<Task<IngredientIndex>>> Cache = new();

    private readonly AssetCatalog _catalog;
    private readonly ItemSetups _setups;
    private readonly Dictionary<string, IngredientTag> _byName;
    private readonly Dictionary<string, IReadOnlyList<string>> _members;
    private readonly ConcurrentDictionary<string, (IReadOnlyList<IngredientInstance> Instances, string? StoredIn)> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _countsUses = new(StringComparer.OrdinalIgnoreCase);

    private IngredientIndex(AssetCatalog catalog)
    {
        _catalog = catalog;
        _setups = ItemSetups.Of(catalog);
        var items = CraftablesPlanner.Items(catalog);
        _members = items.AsParallel().SelectMany(i => ItemTags(i).Select(t => (Tag: t, Item: i))).AsSequential()
            .GroupBy(m => m.Tag, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(m => m.Item).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(PackageMap.Leaf, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
        var counts = _members.ToDictionary(m => m.Key, m => m.Value.Count, StringComparer.OrdinalIgnoreCase);
        var entries = (catalog.Index ?? catalog.BuildIndex(resolveClasses: false)).Entries;
        Tags = entries.Where(e => e.PackagePath.StartsWith(RecipeIngredients.TagFolder + "/", StringComparison.OrdinalIgnoreCase))
            .Select(e => ReadTag(catalog, e.PackagePath, counts))
            .OfType<IngredientTag>()
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _byName = Tags.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every ingredient tag of the game's tag folder, by name.</summary>
    public IReadOnlyList<IngredientTag> Tags { get; }

    /// <summary>The index of <paramref name="catalog"/> (waits for the worker the first time).</summary>
    public static IngredientIndex Of(AssetCatalog catalog) => OfAsync(catalog).GetAwaiter().GetResult();

    /// <summary>The index of <paramref name="catalog"/>, built on a worker once.</summary>
    public static Task<IngredientIndex> OfAsync(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Cache.GetValue(catalog, c => new Lazy<Task<IngredientIndex>>(() => Task.Run(() => new IngredientIndex(c)))).Value;
    }

    /// <summary>The tag named <paramref name="name"/>, or null.</summary>
    public IngredientTag? Tag(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// The items of the game that satisfy <paramref name="tag"/> (their entity setup, or a parent setup, lists it), by name;
    /// empty for a tag no item lists (a station's, or one only a mod's item carries).
    /// </summary>
    public IReadOnlyList<string> Members(string tag) => _members.GetValueOrDefault(tag) ?? [];

    /// <summary>
    /// True when <paramref name="item"/> is used up by uses or pieces: its entity setup (or a parent setup) has a
    /// <c>DiscreteUsageItemEntityComponentSetup</c> (glue, a repair kit, a cement bag) or a <c>StackableItemEntityComponentSetup</c>
    /// (arrows, paper). The game gives the <c>CICM_Uses</c> consumption method only to tags of such items (69 of the 75 items
    /// behind its 32 <c>CICM_Uses</c> tags, SCUM 1.3.4); 417 of the 487 items behind its tags of the default method (whole
    /// items) have neither.
    /// </summary>
    public bool CountsUses(string item) => _countsUses.GetOrAdd(PackageMap.Normalize(item), p => _setups.SetupOf(p) is { } setup && HasUsesSetup(_catalog, setup));

    /// <summary>The entity component setup classes that make an item count by uses or pieces (<see cref="CountsUses"/>).</summary>
    internal static readonly string[] UsesSetupClasses = ["DiscreteUsageItemEntityComponentSetup", "StackableItemEntityComponentSetup"];

    private static bool HasUsesSetup(AssetCatalog catalog, string setup)
    {
        string? package = setup;
        for (var depth = 0; depth < 16 && package is not null; depth++)
        {
            CookedPackage cooked;
            try
            {
                cooked = ModdableAssets.ReadPackage(catalog, package);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or FormatException)
            {
                return false;
            }

            if (Enumerable.Range(0, cooked.Exports.Count).Any(i => UsesSetupClasses.Contains(cooked.GetExportClassName(i), StringComparer.Ordinal)))
            {
                return true;
            }

            package = ItemSetups.ParentBlueprint(cooked);
        }

        return false;
    }

    /// <summary>The names of the tags <paramref name="item"/> satisfies (its setup's list, or the nearest parent setup's).</summary>
    public IReadOnlyList<string> ItemTags(string item) =>
        Instances(item).Select(i => PackageMap.Leaf(PackageMap.Normalize(i.TagPath))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The <c>CraftingIngredientInstances</c> <paramref name="item"/> has: its setup's own, else the nearest parent setup's
    /// (an array the child does not store is inherited); empty when it has none or no setup.
    /// </summary>
    public IReadOnlyList<IngredientInstance> Instances(string item) => InstancesWithSource(item).Instances;

    /// <summary>The entity setup package of <paramref name="item"/>, or null (<see cref="ItemSetups.SetupOf"/>).</summary>
    public string? SetupOf(string item) => _setups.SetupOf(item);

    /// <summary>
    /// How <paramref name="item"/> can be asked for: its own tag (one only it satisfies, one that shows it, or <c>CI_</c> + its
    /// name), else the group tags it satisfies, else none.
    /// </summary>
    public IngredientChoice Resolve(string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var path = PackageMap.Normalize(item);
        var newTag = NewTagName(path);
        if (!CraftablesPlanner.IsItem(_catalog, path))
        {
            return new IngredientChoice(path, IngredientMatch.NotItem, null, [], newTag);
        }

        var tags = ItemTags(path);
        if (tags.Count == 0)
        {
            return new IngredientChoice(path, IngredientMatch.NoTag, null, [], newTag);
        }

        var leaf = PackageMap.Leaf(path);
        bool Shows(IngredientTag t) => t.Items == 1 || string.Equals(t.Representative, path, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, "CI_" + leaf, StringComparison.OrdinalIgnoreCase);
        var own = tags.FirstOrDefault(t => !IsGroup(t) && Tag(t) is { } known && Shows(known)) ?? tags.FirstOrDefault(t => Tag(t) is { Items: 1 });
        return own is null
            ? new IngredientChoice(path, IngredientMatch.GroupOnly, null, tags, newTag)
            : new IngredientChoice(path, IngredientMatch.OwnTag, own, tags.Where(t => !string.Equals(t, own, StringComparison.OrdinalIgnoreCase)).ToList(), newTag);
    }

    /// <summary>
    /// The name of the tag an item gets when the user allows one: <c>CI_SSI_</c> + the item's name (other runs than Latin
    /// letters and digits as '_', a long one cut with a hash), never ending in '_' and a number (the engine would read it as
    /// an FName number: <c>_I</c> is added), so it is a valid asset name and never a station's <c>CI_SS_*</c>.
    /// </summary>
    public static string NewTagName(string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var leaf = NotLatin().Replace(PackageMap.Leaf(item.Split('.')[0]), "_").Trim('_');
        if (leaf.Length > 53)
        {
            leaf = leaf[..44].TrimEnd('_') + "_h" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item)))[..6];
        }

        var name = "CI_SSI_" + (leaf.Length > 0 ? leaf : "Item");
        return EndsInNumber().IsMatch(name) ? name + "_I" : name;
    }

    /// <summary>True for a group tag (<c>CI_Group_*</c>: any item of a kind).</summary>
    public static bool IsGroup(string tag) => tag.StartsWith("CI_Group_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The instances of <paramref name="item"/> and the setup package that stores them (null when none does).</summary>
    internal (IReadOnlyList<IngredientInstance> Instances, string? StoredIn) InstancesWithSource(string item) =>
        _instances.GetOrAdd(PackageMap.Normalize(item), p => _setups.SetupOf(p) is { } setup ? Read(_catalog, setup) : ([], null));

    /// <summary>The instances <paramref name="setup"/> stores, or else its nearest parent setup's, and the package that stores them.</summary>
    internal static (IReadOnlyList<IngredientInstance> Instances, string? StoredIn) Read(AssetCatalog catalog, string setup)
    {
        string? package = setup;
        for (var depth = 0; depth < 16 && package is not null; depth++)
        {
            CookedPackage cooked;
            try
            {
                cooked = ModdableAssets.ReadPackage(catalog, package);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or FormatException)
            {
                break;
            }

            if (ItemSetups.ClassDefaults(cooked)?.Find("CraftingIngredientInstances")?.Value is ArrayValue list)
            {
                return (StoredInstances(cooked, list), package);
            }

            package = ItemSetups.ParentBlueprint(cooked);
        }

        return ([], null);
    }

    /// <summary>The elements of a stored <c>CraftingIngredientInstances</c> array whose <c>Type</c> is an imported tag.</summary>
    internal static IReadOnlyList<IngredientInstance> StoredInstances(CookedPackage package, ArrayValue list) =>
        list.Items.OfType<StructValue>()
            .Select(s => (Type: s.Find("Type")?.Value, Amount: s.Find("Amount")?.Value is FloatValue f ? f.Value : 1f))
            .Where(s => s.Type is ObjectValue { Index: < 0 } o && package.Imports[-o.Index - 1].OuterIndex < 0)
            .Select(s =>
            {
                var import = package.Imports[-((ObjectValue)s.Type!).Index - 1];
                var outer = package.ResolveName(package.Imports[-import.OuterIndex - 1].ObjectName);
                return new IngredientInstance(outer + "." + package.ResolveName(import.ObjectName), s.Amount);
            })
            .ToList();

    private static IngredientTag? ReadTag(AssetCatalog catalog, string path, Dictionary<string, int> counts)
    {
        try
        {
            var package = ModdableAssets.ReadPackage(catalog, path);
            var index = Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.GetExportClassName(i) == "CraftingIngredientTag", -1);
            if (index < 0)
            {
                return null;
            }

            var block = package.ReadProperties(index);
            var caption = block.Find("ClassRepresentativeCaption")?.Value is TextValue { SourceString: { Length: > 0 } text } ? text : null;
            var shows = block.Find("UIClassRepresentative")?.Value is SoftObjectValue { AssetPath: { Length: > 0 } soft } ? PackageMap.Normalize(soft) : null;
            var name = PackageMap.Leaf(path);
            return new IngredientTag(name, path, caption, shows, counts.GetValueOrDefault(name));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or FormatException)
        {
            return null; // an unreadable tag is not offered
        }
    }

    [GeneratedRegex("[^A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NotLatin();

    [GeneratedRegex("_[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EndsInNumber();
}
