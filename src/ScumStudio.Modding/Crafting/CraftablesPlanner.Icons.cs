using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Textures;

namespace ScumStudio.Modding.Crafting;

/// <summary>
/// The picture a craftable's recipe shows in the crafting menu (Jenni, 2026-10-13: "every craftable shows the improvised
/// chair"; audit N098/F166: the recipe's <c>Icon</c> was never written, so each copy kept its template's). The menu reads the
/// recipe's soft <c>Icon</c> (SCUM 1.3.4, read from the paks: <c>CR_Chair_Improvised_Wood</c> →
/// <c>/Game/ConZ_Files/Items/Item_Icons/Modular_BB_Icons/ICO_Chair_Improvised_Wood</c>, a 512 x 512 BC7 Texture2D, one mip
/// stored in the package, <c>TEXTUREGROUP_UI</c>, <c>NeverStream</c>, sRGB; 489 placeable recipes, all BC7 or BGRA8 in the UI
/// group, one mip). A craftable whose source has no picture of its own (a static mesh: Jenni's sofas, beds and fridges)
/// gets one drawn from its 3D model (<see cref="CraftPicture"/>, Jenni and the owner, 2026-10-16: "a bed must show a bed").
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
    /// The picture of the craftable's source itself, or null when it has none (a static mesh no game element shows: the game
    /// has no picture of it): an item's inventory icon (<see cref="InventoryIcons"/>, its entity setup's), else the icon of
    /// the stock recipe that builds the base element it is or shows, when the craftable still shows that element's mesh.
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
            if (ElementFamilies.Of(catalog, craftable.Element?.Template ?? source) is not { } family || family.BorrowsRecipe)
            {
                return null; // a borrowed recipe's picture is the chair's, not the element's
            }

            // An element given another mesh no longer looks like the game's element: its picture would show the old one.
            var mesh = GamePath(PackageMap.Normalize(craftable.Mesh));
            return family.Levels.Count > 0 && !StaticMeshImports(catalog, family.Levels[0].Element).Contains(mesh, StringComparer.OrdinalIgnoreCase)
                ? null
                : RecipeIcon(catalog, family.Recipe);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // best effort: no element family, no picture
        }
    }

    /// <summary>
    /// Points the copied recipe's picture at the craftable's and returns the picture the export must make, if any:
    /// <list type="bullet">
    /// <item>a texture the game ships the user picked (<see cref="Craftable.Icon"/>; any other is refused with a warning);</item>
    /// <item>a picture file of the project folder (<see cref="CraftPictures.IsProjectFile"/>) or "from its 3D model"
    /// (<see cref="CraftPictures.FromModel"/>): a new texture <see cref="CraftPictures.PackageFor"/>;</item>
    /// <item>else its own (<see cref="OwnIcon"/>), and without one a picture drawn from its 3D model.</item>
    /// </list>
    /// A recipe template that stores no picture, or whose picture cannot be read as a texture, keeps its own (with a warning
    /// when a picture was to be made).
    /// </summary>
    private static CraftPicture? AddIconRedirect(AssetCatalog catalog, Craftable craftable, string? shown, string newRecipe, string token,
        List<KeyValuePair<string, string>> redirects, List<string> warnings)
    {
        if (shown is null)
        {
            return null; // the template stores no picture: the game shows its class default
        }

        var from = PackageMap.Normalize(shown);
        var picked = craftable.Icon?.Trim();
        string? icon = null;
        CraftPicture? picture = null;
        if (picked is { Length: > 0 } && picked.StartsWith('/'))
        {
            var package = PackageMap.Normalize(picked);
            if (!catalog.PackageExists(package))
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
        else if (CraftPictures.IsProjectFile(picked))
        {
            picture = new CraftPicture(craftable.Name, CraftPictures.PackageFor(newRecipe, token), from, CraftPictureSource.File, craftable.Mesh, File: picked!.Replace('\\', '/'));
        }
        else if (picked is { Length: > 0 } && !CraftPictures.IsFromModel(picked))
        {
            warnings.Add($"{craftable.Name}: the picture '{picked}' is neither a picture of the game nor a picture file; it shows its own picture.");
        }

        if (icon is null && picture is null)
        {
            icon = CraftPictures.IsFromModel(picked) ? null : OwnIcon(catalog, craftable) is { } own ? PackageMap.Normalize(own) : null;
            if (icon is null)
            {
                picture = new CraftPicture(craftable.Name, CraftPictures.PackageFor(newRecipe, token), from, CraftPictureSource.Model, craftable.Mesh, Imported: craftable.Imported);
            }
        }

        if (picture is not null)
        {
            if (TemplateProblem(catalog, from) is { } problem)
            {
                warnings.Add($"{craftable.Name}: no picture of its own is made ({problem}); it shows its template's.");
                return null;
            }

            redirects.Add(new(from, GamePath(picture.Package)));
            return picture;
        }

        if (!string.Equals(from, icon, StringComparison.OrdinalIgnoreCase))
        {
            redirects.Add(new(from, GamePath(icon!)));
        }

        return null;
    }

    /// <summary>Why the template's picture cannot be the layout of a new one, or null when it can.</summary>
    private static string? TemplateProblem(AssetCatalog catalog, string template)
    {
        try
        {
            var package = ModdableAssets.ReadPackage(catalog, template);
            var index = CookedTexture2D.FindExport(package);
            if (index < 0)
            {
                return $"{PackageMap.Leaf(template)} is not a texture";
            }

            var texture = CookedTexture2D.Read(package, index);
            return texture.Platforms is [var platform] && Assets.Textures.BlockEncoder.IsSupported(platform.PixelFormat)
                ? null
                : $"{PackageMap.Leaf(template)} is a texture format the studio does not write";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException)
        {
            return $"{PackageMap.Leaf(template)} cannot be read: {ex.Message}";
        }
    }

    /// <summary>The size of the picture made in the place of <paramref name="template"/>: its own when both edges are powers of two, else 512 x 512.</summary>
    public static (int Width, int Height) PictureSize(AssetCatalog catalog, string template)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var (width, height) = TextureWriter.Size(ModdableAssets.ReadPackage(catalog, PackageMap.Normalize(template)));
        return Assets.Textures.ImageFiles.IsValidSize(width) && Assets.Textures.ImageFiles.IsValidSize(height) ? (width, height) : (512, 512);
    }

    /// <summary>
    /// The picture the crafting menu shows for the template: its recipe's <c>Icon</c>. An item recipe stores none (SCUM 1.3.4:
    /// <c>CR_Small_Battery_Charger</c>, <c>CR_Improvised_Knife</c>) and the menu shows the item's, so a craftable whose item
    /// is copied too (power) shows the copied item's inventory icon.
    /// </summary>
    private static string? ShownIcon(AssetCatalog catalog, string recipeTemplate, IReadOnlyList<KeyValuePair<string, string>> packages, CraftablePackages result) =>
        RecipeIcon(catalog, recipeTemplate)
        ?? (result.Item is { } item && packages.FirstOrDefault(p => string.Equals(p.Value, item + "_ES", StringComparison.OrdinalIgnoreCase)).Key is { } setup
            ? InventoryIcons.FindIconPath(catalog, setup)
            : null);
}
