using ScumStudio.Assets.Catalog;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Tuning;

namespace ScumStudio.Modding.Crafting;

/// <summary>
/// The storage of an item that holds others (a chest, a cabinet, the fridge; read from the paks, SCUM 1.3.3): its entity
/// setup's <c>EntityGridInventoryComponentSetup</c> stores <c>NumRows</c> and <c>NumColumns</c> (bytes, 1-255) and
/// <c>MaxContainedWeight</c>. The game's fridge (<c>Refrigerator</c>, native class <c>RefrigeratorItem</c>, 30 x 15, 500) is
/// a world item no player can build or carry (its <c>_ES</c> has <c>GridInventoryRowSpan</c> 255, its CDO
/// <c>_canBeDropped</c> false); keeping food cool is the native class's code and needs electricity nearby (its
/// <c>RangedResourceConsumerComponentMultipleProviders</c>, 200): a copy of its Blueprint keeps both, a pak cannot change
/// them. defolt_0999's "large refrigerators" is therefore a base element of the game that spawns a copy of the fridge
/// (<see cref="Carrier"/>), placed and owned like a player's chest, never a world item spawner (D02).
/// </summary>
public static class ItemStorage
{
    /// <summary>The class of the storage setup in an item's <c>_ES</c>.</summary>
    public const string SetupClass = "EntityGridInventoryComponentSetup";

    /// <summary>The game's fridge item.</summary>
    public const string Fridge = ModdableAssets.ConZ + "Items/Equipment/Refrigerator";

    /// <summary>
    /// The base element a placed container is built as: the large wooden cabinet (a <c>BaseBuildingItemComponent</c> whose
    /// <c>_itemClass</c> spawns its container item), the size of a fridge.
    /// </summary>
    public const string Carrier = ModdableAssets.ConZ + "BaseBuilding/BaseElements/BP_Cabinet_Large_Wood";

    /// <summary>The storage values the page offers.</summary>
    public static IReadOnlyList<string> Editable { get; } = ["NumRows", "NumColumns", "MaxContainedWeight"];

    /// <summary>The storage values <paramref name="item"/>'s entity setup stores (empty when it holds nothing or cannot be read).</summary>
    public static IReadOnlyList<Tunable> Of(AssetCatalog catalog, string item)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(item);
        var setup = PackageMap.Normalize(item) + "_ES";
        try
        {
            if (!catalog.PackageExists(setup))
            {
                return [];
            }

            var package = ModdableAssets.ReadPackage(catalog, setup);
            var exports = Enumerable.Range(0, package.Exports.Count).Where(i => package.GetExportClassName(i) == SetupClass)
                .Select(i => package.ResolveName(package.Exports[i].ObjectName)).ToHashSet(StringComparer.Ordinal);
            return TunableReader.Read(package).Where(t => exports.Contains(t.Export) && Editable.Contains(t.Path, StringComparer.Ordinal)).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// True for an item that is a placed container (the game's fridge): it stores things (<see cref="Of"/>) and fits in no
    /// inventory (its <c>_ES</c> has <c>GridInventoryRowSpan</c> 255), so crafting it into the bag cannot work.
    /// </summary>
    public static bool IsPlacedContainer(AssetCatalog catalog, string item)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (Of(catalog, item).Count == 0)
        {
            return false;
        }

        try
        {
            var package = ModdableAssets.ReadPackage(catalog, PackageMap.Normalize(item) + "_ES");
            return TunableReader.Read(package).Any(t => t.Path == "GridInventoryRowSpan" && t.Value == "255");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Why <paramref name="value"/> is no valid <paramref name="key"/>, or null (rows and columns 1-255, weight above 0).</summary>
    public static string? Refusal(string key, string value)
    {
        if (!decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return $"'{value}' is not a number";
        }

        return key switch
        {
            "NumRows" or "NumColumns" when number != decimal.Truncate(number) || number is < 1 or > 255 => $"{key} must be a whole number from 1 to 255",
            "MaxContainedWeight" when number <= 0 => $"{key} must be above 0",
            _ => null,
        };
    }
}
