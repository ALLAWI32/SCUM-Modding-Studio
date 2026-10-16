using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Crafting;

/// <summary>
/// The picture a craftable's recipe shows in the crafting menu (Jenni, 2026-10-13: "every craftable shows the improvised
/// chair"; audit N098/F166: the recipe's <c>Icon</c> was never written, so each copy kept its template's).
/// </summary>
public static partial class CraftablesPlanner
{
    /// <summary>The soft object property of a crafting recipe that holds its picture.</summary>
    public const string IconProperty = "Icon";

    /// <summary>The object path of the picture <paramref name="recipePackage"/> shows (its recipe's <c>Icon</c>), or null.</summary>
    public static string? RecipeIcon(AssetCatalog catalog, string recipePackage)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try
        {
            var package = ModdableAssets.ReadPackage(catalog, PackageMap.Normalize(recipePackage));
            return package.ReadProperties(RecipeIngredients.RecipeExport(package)).Find(IconProperty)?.Value is SoftObjectValue { AssetPath: { Length: > 0 } path } && path != "None"
                ? path
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException)
        {
            return null; // an unreadable recipe shows nothing we could name
        }
    }

    /// <summary>
    /// The picture of the craftable's source itself, or null when it has none (a static mesh: the game has no picture of
    /// it): an item's inventory icon (<see cref="InventoryIcons"/>, its entity setup's), else the icon of the stock recipe
    /// that builds the base element it is or shows.
    /// </summary>
    public static string? OwnIcon(AssetCatalog catalog, Craftable craftable)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(craftable);
        if (!craftable.Source.StartsWith('/'))
        {
            return null; // a model imported from a 3D file (its source is the file)
        }

        var source = PackageMap.Normalize(craftable.Source);
        if (InventoryIcons.FindIconPath(catalog, catalog.PackageExists(source + "_ES") ? source + "_ES" : null) is { } inventory)
        {
            return inventory;
        }

        try
        {
            return ElementFamilies.Of(catalog, craftable.Element?.Template ?? source) is { } family ? RecipeIcon(catalog, family.Recipe) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // best effort: no element family, no picture
        }
    }

    /// <summary>
    /// Points the copied recipe's picture at the craftable's: the one the user picked (<see cref="Craftable.Icon"/>, a
    /// texture the game ships; any other is refused with a warning), else its own (<see cref="OwnIcon"/>). A craftable with
    /// neither keeps the template's picture.
    /// </summary>
    private static void AddIconRedirect(AssetCatalog catalog, Craftable craftable, string recipeTemplate, List<KeyValuePair<string, string>> redirects, List<string> warnings)
    {
        if (RecipeIcon(catalog, recipeTemplate) is not { } shown)
        {
            return; // the template stores no picture: the game shows its class default
        }

        string? icon = null;
        if (craftable.Icon is { Length: > 0 } picked)
        {
            var package = picked.StartsWith('/') ? PackageMap.Normalize(picked) : picked; // a typed name, not a path, is refused below
            if (!picked.StartsWith('/') || !catalog.PackageExists(package))
            {
                warnings.Add($"{craftable.Name}: the picture {package} is not in the game files; it shows its own picture.");
            }
            else if (!catalog.IsStock(package))
            {
                warnings.Add($"{craftable.Name}: the picture {package} is not the game's own (another mod's pak would be needed); it shows its own picture.");
            }
            else
            {
                icon = package;
            }
        }

        icon ??= OwnIcon(catalog, craftable) is { } own ? PackageMap.Normalize(own) : null;
        var from = PackageMap.Normalize(shown);
        if (icon is null)
        {
            return; // a mesh has no picture of its own in the game: the template's stays (the Picture box picks one)
        }

        if (!string.Equals(from, icon, StringComparison.OrdinalIgnoreCase))
        {
            redirects.Add(new(from, GamePath(icon)));
        }
    }
}
