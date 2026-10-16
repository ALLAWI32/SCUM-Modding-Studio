using System.Text.RegularExpressions;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Modding.Textures;

/// <summary>
/// A new texture as a journaled value edit: package <c>&lt;clone folder&gt;/Textures/T_…</c>, export its leaf, path
/// <see cref="PixelsPath"/>, value <c>&lt;template object path&gt;|&lt;project file&gt;</c>. The export writes the picture in the
/// template's cooked layout and format (<see cref="TextureWriter"/>); an empty value (an undone replace) writes nothing.
/// </summary>
public static partial class TextureWrites
{
    /// <summary>The edit path of a texture's pixels.</summary>
    public const string PixelsPath = "#Pixels";

    /// <summary>True for <see cref="PixelsPath"/>.</summary>
    public static bool IsEdit(string path) => string.Equals(path, PixelsPath, StringComparison.Ordinal);

    /// <summary>The edit value: the stock texture whose layout is copied and the picture (a file of the project folder).</summary>
    public static string Value(string templateObjectPath, string projectFile) => $"{templateObjectPath}|{projectFile.Replace('\\', '/')}";

    /// <summary>Splits a <see cref="Value"/>; null when it is empty or malformed.</summary>
    public static (string Template, string File)? Parse(string? value)
    {
        var bar = value?.IndexOf('|') ?? -1;
        return bar <= 0 || bar == value!.Length - 1 ? null : (value[..bar], value[(bar + 1)..]);
    }

    /// <summary>
    /// The package of a clone's own texture: <c>&lt;folder of the clone&gt;/Textures/T_&lt;clone&gt;_&lt;material&gt;_&lt;parameter initials&gt;</c>.
    /// The leaf always ends in letters: a leaf ending in <c>_&lt;digits&gt;</c> is stored as a numbered FName.
    /// </summary>
    public static string NewPackage(string clonePrimary, string materialLeaf, string parameter)
    {
        var primary = PackageMap.Normalize(clonePrimary);
        var clone = Strip(PackageMap.Leaf(primary), "BPC_", "BP_");
        var material = Strip(PackageMap.Leaf(materialLeaf[(materialLeaf.LastIndexOf('.') + 1)..]), "MI_");
        var initials = string.Concat(NonLetters().Split(parameter).Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0])));
        return $"{PackageMap.Folder(primary)}/Textures/T_{Clean(clone)}_{Clean(material)}_{(initials.Length > 0 ? initials : "Tex")}";
    }

    /// <summary>True when a package leaf ends in <c>_&lt;digits&gt;</c> (a numbered FName: refused for new packages).</summary>
    public static bool EndsInNumber(string packagePath) => NumberedLeaf().IsMatch(PackageMap.Leaf(PackageMap.Normalize(packagePath)));

    /// <summary>
    /// The full path of <paramref name="file"/> inside <paramref name="root"/>; null when either is missing or the file
    /// would lie outside the folder.
    /// </summary>
    public static string? ResolveFile(string? root, string file)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, file));
        var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string Strip(string leaf, params string[] prefixes) =>
        prefixes.FirstOrDefault(p => leaf.StartsWith(p, StringComparison.OrdinalIgnoreCase)) is { } p ? leaf[p.Length..] : leaf;

    private static string Clean(string text) => InvalidName().Replace(text, "_").Trim('_');

    [GeneratedRegex("[^A-Za-z]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonLetters();

    [GeneratedRegex("[^A-Za-z0-9_]+", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidName();

    [GeneratedRegex(@"_\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedLeaf();
}
