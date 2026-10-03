namespace ScumStudio.Assets.Catalog;

/// <summary>
/// Conversions between the path spellings used for cooked assets:
/// <list type="bullet">
/// <item><description>virtual file path: <c>SCUM/Content/ConZ_Files/Foo/SM_Bar.uasset</c> (what CUE4Parse's provider stores),</description></item>
/// <item><description>package path: <c>/Game/ConZ_Files/Foo/SM_Bar</c> (UE long package name),</description></item>
/// <item><description>object path: <c>/Game/ConZ_Files/Foo/SM_Bar.SM_Bar</c> (package path + '.' + export name).</description></item>
/// </list>
/// All helpers accept any of the three (with either slash direction) and are case-preserving.
/// </summary>
public static class AssetPaths
{
    /// <summary>Default project (root folder) name of SCUM's cooked content.</summary>
    public const string DefaultProjectName = "SCUM";

    /// <summary>File extensions that start a cooked package (<c>.uasset</c>, <c>.umap</c>).</summary>
    public static IReadOnlyList<string> PackageExtensions { get; } = [".uasset", ".umap"];

    /// <summary>True when <paramref name="path"/> ends with a package extension (<c>.uasset</c> / <c>.umap</c>).</summary>
    public static bool IsPackageFile(string path) =>
        path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Converts a provider file path (<c>SCUM/Content/A/B.uasset</c>) to a UE package path (<c>/Game/A/B</c>).
    /// Paths outside <c>&lt;Project&gt;/Content</c> keep their root: <c>Engine/Content/X.uasset</c> becomes <c>/Engine/X</c>;
    /// anything else becomes <c>/</c> + the path without extension.
    /// </summary>
    public static string ToPackagePath(string filePath, string projectName = DefaultProjectName)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var p = Clean(filePath);
        p = StripExtension(p);
        var parts = p.Split('/');
        if (parts.Length >= 2 && string.Equals(parts[1], "Content", StringComparison.OrdinalIgnoreCase))
        {
            var root = string.Equals(parts[0], projectName, StringComparison.OrdinalIgnoreCase) ? "Game" : parts[0];
            return "/" + root + (parts.Length > 2 ? "/" + string.Join('/', parts, 2, parts.Length - 2) : string.Empty);
        }

        return "/" + p;
    }

    /// <summary>
    /// Converts a package path (<c>/Game/A/B</c>), object path (<c>/Game/A/B.B</c>) or file path to the provider file path
    /// without extension (<c>SCUM/Content/A/B</c>).
    /// </summary>
    public static string ToFilePathWithoutExtension(string path, string projectName = DefaultProjectName)
    {
        ArgumentNullException.ThrowIfNull(path);
        var (package, _) = SplitObjectPath(path);
        var p = Clean(package);
        var slash = p.IndexOf('/');
        var root = slash < 0 ? p : p[..slash];
        var rest = slash < 0 ? string.Empty : p[(slash + 1)..];
        if (string.Equals(root, "Game", StringComparison.OrdinalIgnoreCase))
        {
            return projectName + "/Content" + (rest.Length > 0 ? "/" + rest : string.Empty);
        }

        if (string.Equals(root, "Engine", StringComparison.OrdinalIgnoreCase) && !rest.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
        {
            return "Engine/Content" + (rest.Length > 0 ? "/" + rest : string.Empty);
        }

        return p;
    }

    /// <summary>
    /// Splits an object path into package path and object name. <c>/Game/A/B.B</c> gives (<c>/Game/A/B</c>, <c>B</c>);
    /// <c>/Game/A/B</c> gives (<c>/Game/A/B</c>, <c>B</c>) (the asset's main export); <c>/Game/A/B.B:Sub</c> gives
    /// (<c>/Game/A/B</c>, <c>Sub</c>). File extensions (<c>.uasset</c>, <c>.umap</c>, <c>.uexp</c>, <c>.ubulk</c>) are removed.
    /// </summary>
    public static (string PackagePath, string ObjectName) SplitObjectPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var p = path.Trim().Replace('\\', '/');
        // Soft object reference syntax: Class'/Game/A/B.B'
        var quote = p.IndexOf('\'');
        if (quote >= 0 && p.EndsWith('\'') && p.Length > quote + 2)
        {
            p = p[(quote + 1)..^1];
        }

        p = StripExtension(p);
        var lastSlash = p.LastIndexOf('/');
        var colon = p.IndexOf(':', lastSlash + 1);
        string? subObject = null;
        if (colon >= 0)
        {
            subObject = p[(colon + 1)..];
            p = p[..colon];
        }

        var dot = p.IndexOf('.', lastSlash + 1);
        string package;
        string name;
        if (dot >= 0)
        {
            package = p[..dot];
            name = p[(dot + 1)..];
        }
        else
        {
            package = p;
            name = p[(lastSlash + 1)..];
        }

        return (package, string.IsNullOrEmpty(subObject) ? name : subObject);
    }

    /// <summary>Builds <c>/Game/A/B.B</c> style object path from a package path or file path.</summary>
    public static string ToObjectPath(string packageOrFilePath, string? objectName = null, string projectName = DefaultProjectName)
    {
        var package = packageOrFilePath.StartsWith('/') ? SplitObjectPath(packageOrFilePath).PackagePath : ToPackagePath(packageOrFilePath, projectName);
        var name = string.IsNullOrEmpty(objectName) ? package[(package.LastIndexOf('/') + 1)..] : objectName;
        return package + "." + name;
    }

    /// <summary>
    /// Normalises an object path to the <c>/Game/...</c> form. CUE4Parse names loaded packages after their provider file path
    /// (<c>SCUM/Content/A/B.B</c>) but imports after their package path (<c>/Game/A/B.B</c>); this maps the former onto the latter
    /// and leaves paths that already start with '/' unchanged.
    /// </summary>
    public static string NormalizeObjectPath(string objectPath, string projectName = DefaultProjectName)
    {
        ArgumentNullException.ThrowIfNull(objectPath);
        var p = objectPath.Trim().Replace('\\', '/');
        if (p.Length == 0 || p.StartsWith('/'))
        {
            return p;
        }

        var lastSlash = p.LastIndexOf('/');
        var dot = p.IndexOfAny(['.', ':'], lastSlash + 1);
        var package = dot < 0 ? p : p[..dot];
        var rest = dot < 0 ? string.Empty : p[dot..];
        return ToPackagePath(package, projectName) + rest;
    }

    private static string Clean(string path)
    {
        var p = path.Trim().Replace('\\', '/');
        while (p.StartsWith('/'))
        {
            p = p[1..];
        }

        return p.TrimEnd('/');
    }

    private static string StripExtension(string path)
    {
        foreach (var ext in new[] { ".uasset", ".umap", ".uexp", ".ubulk", ".uptnl" })
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return path[..^ext.Length];
            }
        }

        return path;
    }
}
