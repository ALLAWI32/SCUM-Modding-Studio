using ScumStudio.Assets.Textures;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Textures;

namespace ScumStudio.Modding.Crafting;

/// <summary>Where the picture a craftable's recipe shows comes from when the export makes it.</summary>
public enum CraftPictureSource
{
    /// <summary>Drawn from its 3D model like the game's own icons (transparent background, a three-quarter view).</summary>
    Model,

    /// <summary>A PNG or JPG the user chose, kept in the project folder.</summary>
    File,
}

/// <summary>
/// A picture the export makes for one craftable (Jenni and the owner, 2026-10-16: "a bed must show a bed, a fridge a
/// fridge"): a new texture package next to the craftable's recipe, written in the layout and format of the picture the
/// recipe template shows (<see cref="Template"/>, a stock icon: BC7, one mip, UI group), which the copied recipe's
/// <c>Icon</c> is pointed at.
/// </summary>
/// <param name="Craftable">The craftable's name.</param>
/// <param name="Package">The new texture package (<see cref="CraftPictures.PackageFor"/>).</param>
/// <param name="Template">The stock icon the template recipe shows (package path): layout, size and registry record.</param>
/// <param name="Source">Its 3D model or a picture file.</param>
/// <param name="Mesh">The model drawn (a static mesh's package path, the path the catalog knows it by).</param>
/// <param name="File">The picture file (relative to the project folder) for <see cref="CraftPictureSource.File"/>, else null.</param>
/// <param name="Imported">The imported model's folder (relative to the project folder) when the mesh is not the game's, else null.</param>
public sealed record CraftPicture(string Craftable, string Package, string Template, CraftPictureSource Source, string Mesh, string? File = null, string? Imported = null)
{
    /// <summary>The new recipe that shows it (set by the planner): a recipe that is not built ships no picture.</summary>
    public string? Recipe { get; init; }
}

/// <summary>What a picture renderer is asked for: draw <paramref name="Mesh"/> into a square of <paramref name="Size"/> pixels.</summary>
/// <param name="Mesh">A static mesh's package path.</param>
/// <param name="Size">The edge of the picture in pixels (the template icon's).</param>
/// <param name="ImportedFolder">The full path of an imported model's cooked files (the mesh is read from there), else null.</param>
public sealed record CraftPictureRequest(string Mesh, int Size, string? ImportedFolder = null);

/// <summary>The values of <see cref="Craftable.Icon"/> and the paths of the pictures the export makes.</summary>
public static class CraftPictures
{
    /// <summary><see cref="Craftable.Icon"/> for "a picture drawn from its 3D model".</summary>
    public const string FromModel = "#Model";

    /// <summary>The project folder of the pictures the user imports (<c>icons/&lt;token&gt;.png</c>).</summary>
    public const string Folder = "icons";

    /// <summary>True for <see cref="FromModel"/>.</summary>
    public static bool IsFromModel(string? icon) => string.Equals(icon?.Trim(), FromModel, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a picture file of the project folder (<c>icons/x.png</c>): a relative path ending in .png, .jpg or .jpeg.</summary>
    public static bool IsProjectFile(string? icon) =>
        icon is { Length: > 0 } && !icon.StartsWith('/') && !IsFromModel(icon)
        && Path.GetExtension(icon).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";

    /// <summary>The project file a picture imported for the craftable of <paramref name="token"/> is kept as.</summary>
    public static string ProjectFileFor(string token, string extension) =>
        $"{Folder}/{token}{(extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant()}";

    /// <summary>
    /// The texture package of a craftable's own picture: <c>ICO_&lt;token&gt;</c> in the folder of its recipe (so it moves
    /// with the recipe), with <c>_Icon</c> after a token that ends in <c>_&lt;digits&gt;</c> (an old token such as
    /// <c>SS_Chair_01</c>): such a leaf would be stored as a numbered FName.
    /// </summary>
    public static string PackageFor(string recipePackage, string token)
    {
        var leaf = "ICO_" + token;
        var path = PackageMap.Folder(PackageMap.Normalize(recipePackage)) + "/" + leaf;
        return TextureWrites.EndsInNumber(path) ? path + "_Icon" : path;
    }

    /// <summary>
    /// <paramref name="plan"/> without the pictures of <paramref name="packages"/> (they could not be made): their recipes keep
    /// pointing at the template's picture instead of a texture that does not ship.
    /// </summary>
    public static CraftablesPlan Without(CraftablesPlan plan, IReadOnlySet<string> packages)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(packages);
        if (packages.Count == 0)
        {
            return plan;
        }

        // A redirect names a picture by the path the game knows it by (a DLC folder's /<Pack>/...), the plan by the catalog's.
        var gone = packages.Concat(packages.Select(CraftablesPlanner.GamePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Gone(string path) => gone.Contains(PackageMap.Normalize(path));
        var clones = plan.Request.Clones.Select(c => c.Redirects.Any(r => Gone(r.Value)) ? c with { Redirects = c.Redirects.Where(r => !Gone(r.Value)).ToList() } : c).ToList();
        return plan with
        {
            Request = plan.Request with { Clones = clones, Pictures = plan.Request.Pictures.Where(p => !Gone(p.Package)).ToList() },
            Pictures = plan.Pictures.Where(p => !Gone(p.Package)).ToList(),
        };
    }

    /// <summary>
    /// The pixels <paramref name="image"/> becomes for an icon of <paramref name="width"/> x <paramref name="height"/>: fitted
    /// inside without stretching, centred, on a transparent background.
    /// </summary>
    public static byte[] Fit(TextureImage image, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        if ((long)image.Width * height == (long)image.Height * width)
        {
            return ImageFiles.Fit(image, width, height);
        }

        var scale = Math.Min((float)width / image.Width, (float)height / image.Height);
        var w = Math.Clamp((int)MathF.Round(image.Width * scale), 1, width);
        var h = Math.Clamp((int)MathF.Round(image.Height * scale), 1, height);
        var fitted = ImageFiles.Fit(image, w, h);
        var rgba = new byte[width * height * 4];
        var x0 = (width - w) / 2;
        var y0 = (height - h) / 2;
        for (var y = 0; y < h; y++)
        {
            fitted.AsSpan(y * w * 4, w * 4).CopyTo(rgba.AsSpan((((y0 + y) * width) + x0) * 4));
        }

        return rgba;
    }
}

/// <summary>A picture made for the export, written by <see cref="AssetModBuilder"/> as a new texture.</summary>
/// <param name="Package">The new texture package.</param>
/// <param name="Template">The stock texture whose layout, format, size and registry record are copied.</param>
/// <param name="Rgba">The picture's pixels (RGBA8, top row first), <paramref name="Width"/> x <paramref name="Height"/>.</param>
/// <param name="Width">Its width in pixels: the template's.</param>
/// <param name="Height">Its height in pixels: the template's.</param>
/// <param name="Owner">The craftable it belongs to (named in warnings).</param>
/// <param name="Recipe">The new recipe that shows it: when that one is not built, neither is the picture.</param>
public sealed record PictureWrite(string Package, string Template, byte[] Rgba, int Width, int Height, string Owner, string? Recipe = null);
