using CUE4Parse.UE4.Objects.UObject;
using ScumStudio.Assets.Catalog;

namespace ScumStudio.Modding.Catalog;

/// <summary>
/// Finds the inventory icon of an item: the <c>GridInventoryIcon</c> (then <c>GeneralPurposeIcon</c>,
/// <c>HandsAndShouldersIcon</c>) soft object path stored on its entity setup (<c>X_ES</c>), e.g.
/// <c>Weapon_RPK-74_ES</c> → <c>/Game/.../Icons/ICO_RPK74_Inventory.ICO_RPK74_Inventory</c>.
/// </summary>
public static class InventoryIcons
{
    /// <summary>Icon properties of an entity setup, best first.</summary>
    public static IReadOnlyList<string> IconProperties { get; } = ["GridInventoryIcon", "GeneralPurposeIcon", "HandsAndShouldersIcon"];

    /// <summary>Object path of the icon texture of <paramref name="entitySetupPackage"/>, or null when it stores none.</summary>
    public static string? FindIconPath(AssetCatalog catalog, string? entitySetupPackage)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (string.IsNullOrEmpty(entitySetupPackage))
        {
            return null;
        }

        try
        {
            if (!catalog.TryLoadPackage(entitySetupPackage, out var package))
            {
                return null;
            }

            var exports = package.GetExports().ToList();
            foreach (var name in IconProperties)
            {
                foreach (var export in exports)
                {
                    if (export.TryGetValue<FSoftObjectPath>(out var soft, name) && soft.AssetPathName.Text is { Length: > 0 } path && path != "None")
                    {
                        return path;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Unreadable entity setup: no icon.
        }

        return null;
    }
}
