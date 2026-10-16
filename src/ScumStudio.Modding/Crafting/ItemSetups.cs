using System.Runtime.CompilerServices;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Crafting;

/// <summary>
/// The entity setup (<c>_ES</c>) of the items of a catalog: an item's name twin (<c>Lead_Plate</c> → <c>Lead_Plate_ES</c>),
/// or else the setup whose class default <c>Actor</c> (<c>UEntitySetup.Actor</c>, a soft class) names the item. 43 setups
/// of SCUM 1.3.3 are not named after their item (<c>AnimalBaitFeeder_Ridgeline_ES</c>, <c>12_Gauge_Birdshot</c> …), so
/// their items are found only this way. Built once per catalog on a worker (it reads those setups' class defaults).
/// </summary>
public sealed class ItemSetups
{
    private static readonly ConditionalWeakTable<AssetCatalog, Lazy<Task<ItemSetups>>> Cache = new();

    private readonly AssetCatalog _catalog;
    private readonly Dictionary<string, string> _others;

    private ItemSetups(AssetCatalog catalog, Dictionary<string, string> others)
    {
        _catalog = catalog;
        _others = others;
    }

    /// <summary>The items whose setup is not named after them, with that setup (package paths).</summary>
    public IReadOnlyDictionary<string, string> NotNamedAfterItem => _others;

    /// <summary>The setups of <paramref name="catalog"/> (waits for the worker the first time).</summary>
    public static ItemSetups Of(AssetCatalog catalog) => OfAsync(catalog).GetAwaiter().GetResult();

    /// <summary>The setups of <paramref name="catalog"/>, built on a worker once.</summary>
    public static Task<ItemSetups> OfAsync(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Cache.GetValue(catalog, c => new Lazy<Task<ItemSetups>>(() => Task.Run(() => Build(c)))).Value;
    }

    /// <summary>True for an item whose setup is not named after it (<see cref="CraftablesPlanner.IsItem"/> checks twins).</summary>
    public bool ContainsItem(string item) => _others.ContainsKey(PackageMap.Normalize(item));

    /// <summary>The entity setup package of <paramref name="item"/>: its twin, else the one whose <c>Actor</c> names it; null when none.</summary>
    public string? SetupOf(string item)
    {
        var path = PackageMap.Normalize(item);
        return _catalog.PackageExists(path + "_ES") ? path + "_ES" : _others.GetValueOrDefault(path);
    }

    /// <summary>The class default object's properties of the Blueprint class in <paramref name="package"/>, or null.</summary>
    internal static PropertyBlock? ClassDefaults(CookedPackage package)
    {
        var index = Enumerable.Range(0, package.Exports.Count)
            .FirstOrDefault(i => package.ResolveName(package.Exports[i].ObjectName).StartsWith("Default__", StringComparison.Ordinal), -1);
        return index < 0 ? null : package.ReadProperties(index);
    }

    /// <summary>The Blueprint package the Blueprint class of <paramref name="package"/> derives from, or null for a native parent.</summary>
    internal static string? ParentBlueprint(CookedPackage package)
    {
        var index = Enumerable.Range(0, package.Exports.Count).FirstOrDefault(i => package.GetExportClassName(i) == "BlueprintGeneratedClass", -1);
        if (index < 0 || package.Exports[index].SuperIndex >= 0 || package.Imports[-package.Exports[index].SuperIndex - 1] is not { OuterIndex: < 0 } super)
        {
            return null;
        }

        var outer = package.ResolveName(package.Imports[-super.OuterIndex - 1].ObjectName);
        return outer.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase) ? null : outer;
    }

    private static ItemSetups Build(AssetCatalog catalog)
    {
        var entries = (catalog.Index ?? catalog.BuildIndex(resolveClasses: false)).Entries;
        var paths = entries.Select(e => e.PackagePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byGamePath = entries.Select(e => e.PackagePath).Where(p => p.Contains("/Plugins/", StringComparison.OrdinalIgnoreCase))
            .GroupBy(CraftablesPlanner.GamePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var others = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var setups = entries.Select(e => e.PackagePath)
            .Where(p => p.EndsWith("_ES", StringComparison.OrdinalIgnoreCase) && !paths.Contains(p[..^3]))
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (var setup in setups)
        {
            if (ActorOf(catalog, setup) is not { } actor)
            {
                continue;
            }

            var item = paths.Contains(actor) ? actor : byGamePath.GetValueOrDefault(actor);
            if (item is not null && !item.EndsWith("_ES", StringComparison.OrdinalIgnoreCase) && !CraftablesPlanner.IsNotItemFolder(item)
                && IsBlueprintClass(catalog, setup) && IsBlueprintClass(catalog, item))
            {
                others.TryAdd(item, setup); // the first setup (path order) of an item named by two
            }
        }

        return new ItemSetups(catalog, others);
    }

    // The package the setup's own Actor soft class names, or null (an inherited Actor names the parent setup's item).
    private static string? ActorOf(AssetCatalog catalog, string setup)
    {
        try
        {
            return ClassDefaults(ModdableAssets.ReadPackage(catalog, setup))?.Find("Actor")?.Value is SoftObjectValue { AssetPath: { Length: > 0 } path }
                ? PackageMap.Normalize(path)
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static bool IsBlueprintClass(AssetCatalog catalog, string package)
    {
        try
        {
            return catalog.GetExports(package).Any(e => e.ClassName == "BlueprintGeneratedClass");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }
}
