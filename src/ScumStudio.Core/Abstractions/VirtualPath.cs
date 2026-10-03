namespace ScumStudio.Core.Abstractions;

/// <summary>
/// Helpers for the virtual paths used by <see cref="IFileSource"/>
/// (forward slashes, no leading slash, case-insensitive).
/// </summary>
public static class VirtualPath
{
    /// <summary>Comparer to use for dictionaries/sets keyed by virtual path.</summary>
    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Canonicalises a path: backslashes become '/', duplicate slashes collapse, leading
    /// "./", "/" and trailing "/" are removed. Does not change case.
    /// </summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var parts = path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p != ".");
        return string.Join('/', parts);
    }

    /// <summary>Joins segments with '/' and normalises the result.</summary>
    public static string Combine(params string[] segments) => Normalize(string.Join('/', segments));

    /// <summary>
    /// True when <paramref name="path"/> equals <paramref name="directory"/> or lies below it
    /// (case-insensitive). An empty directory contains everything.
    /// </summary>
    public static bool IsUnder(string path, string directory)
    {
        var p = Normalize(path);
        var d = Normalize(directory);
        if (d.Length == 0)
        {
            return true;
        }

        return p.Length > d.Length
            ? p.StartsWith(d, StringComparison.OrdinalIgnoreCase) && p[d.Length] == '/'
            : string.Equals(p, d, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>File extension including the dot (e.g. ".umap"), or empty.</summary>
    public static string GetExtension(string path)
    {
        var name = GetFileName(path);
        var dot = name.LastIndexOf('.');
        return dot < 0 ? string.Empty : name[dot..];
    }

    /// <summary>Last path segment.</summary>
    public static string GetFileName(string path)
    {
        var p = Normalize(path);
        var slash = p.LastIndexOf('/');
        return slash < 0 ? p : p[(slash + 1)..];
    }

    /// <summary>Parent directory, or empty for a top-level entry.</summary>
    public static string GetDirectory(string path)
    {
        var p = Normalize(path);
        var slash = p.LastIndexOf('/');
        return slash < 0 ? string.Empty : p[..slash];
    }

    /// <summary>
    /// Replaces the extension (e.g. ".uasset" to ".uexp"); a null or empty <paramref name="newExtension"/> removes it.
    /// </summary>
    public static string ChangeExtension(string path, string? newExtension)
    {
        var p = Normalize(path);
        var ext = GetExtension(p);
        var stem = ext.Length == 0 ? p : p[..^ext.Length];
        if (string.IsNullOrEmpty(newExtension))
        {
            return stem;
        }

        return newExtension[0] == '.' ? stem + newExtension : stem + "." + newExtension;
    }
}
