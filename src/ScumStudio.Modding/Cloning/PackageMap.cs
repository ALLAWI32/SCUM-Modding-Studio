using System.Text.RegularExpressions;

namespace ScumStudio.Modding.Cloning;

/// <summary>
/// The rename map of a clone: old package path → new package path, plus the object-name map derived from the package
/// leaf names (<c>Weapon_RPK-74</c> → <c>Weapon_RPK-74_Gold</c>). Port of <c>use_maps</c>/<c>remap</c>/<c>remap_obj</c>
/// from the owner's <c>clone_vehicle.py</c>: cooked data refers to every object by FName index, so a clone only rewrites
/// name-table strings; object names follow the package leaf (<c>X_C</c>, <c>Default__X_C</c>, numbered <c>X_3</c>).
/// </summary>
public sealed partial class PackageMap
{
    private readonly Dictionary<string, string> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _objects = new(StringComparer.Ordinal);

    /// <summary>Creates a map from (old, new) package paths.</summary>
    /// <exception cref="ArgumentException">A path is not a /Game package path, or an old path is mapped twice.</exception>
    public PackageMap(IEnumerable<KeyValuePair<string, string>> packages, IEnumerable<KeyValuePair<string, string>>? extraObjects = null)
    {
        ArgumentNullException.ThrowIfNull(packages);
        foreach (var (oldPath, newPath) in packages)
        {
            var o = Normalize(oldPath);
            var n = Normalize(newPath);
            if (!_packages.TryAdd(o, n))
            {
                throw new ArgumentException($"{o} is mapped twice.", nameof(packages));
            }

            var oldLeaf = Leaf(o);
            var newLeaf = Leaf(n);
            if (!string.Equals(oldLeaf, newLeaf, StringComparison.Ordinal))
            {
                _objects[oldLeaf] = newLeaf;
            }
        }

        foreach (var (o, n) in extraObjects ?? [])
        {
            _objects[o] = n;
        }
    }

    /// <summary>Old → new package paths.</summary>
    public IReadOnlyDictionary<string, string> Packages => _packages;

    /// <summary>Old → new object (leaf) names.</summary>
    public IReadOnlyDictionary<string, string> Objects => _objects;

    /// <summary>The new path of <paramref name="oldPackagePath"/>, or null when it is not mapped.</summary>
    public string? Map(string oldPackagePath) => _packages.TryGetValue(Normalize(oldPackagePath), out var n) ? n : null;

    /// <summary>
    /// Remaps an object name: exact leaf, <c>X_C</c>, <c>Default__…</c> and numbered FName text <c>X_3</c> (port of
    /// <c>remap_obj</c>).
    /// </summary>
    public string RemapObject(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.StartsWith("Default__", StringComparison.Ordinal))
        {
            return "Default__" + RemapObject(name["Default__".Length..]);
        }

        if (_objects.TryGetValue(name, out var mapped))
        {
            return mapped;
        }

        if (name.EndsWith("_C", StringComparison.Ordinal) && _objects.TryGetValue(name[..^2], out var cls))
        {
            return cls + "_C";
        }

        var underscore = name.LastIndexOf('_');
        if (underscore > 0 && underscore < name.Length - 1)
        {
            var tail = name[(underscore + 1)..];
            if (tail.All(char.IsAsciiDigit) && _objects.TryGetValue(name[..underscore], out var numbered))
            {
                return numbered + "_" + tail;
            }
        }

        return name;
    }

    /// <summary>
    /// Remaps a name-table entry: a package path, an object path <c>pkg.obj[:sub]</c>, or a bare object name (port of
    /// <c>remap</c>).
    /// </summary>
    public string RemapName(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.StartsWith('/'))
        {
            return RemapObject(entry);
        }

        var dot = entry.IndexOf('.');
        var package = dot < 0 ? entry : entry[..dot];
        var newPackage = Map(package) ?? package;
        if (dot < 0)
        {
            return newPackage;
        }

        var rest = entry[(dot + 1)..];
        var colon = rest.IndexOf(':');
        var obj = colon < 0 ? rest : rest[..colon];
        var sub = colon < 0 ? string.Empty : rest[colon..];
        return newPackage + "." + RemapObject(obj) + sub;
    }

    /// <summary>
    /// Remaps free text: every <c>/Game/…</c> path inside it, or the whole text as an object name when it holds no path
    /// (port of <c>remap_text</c>; used for registry string tags such as <c>BlueprintPath</c>).
    /// </summary>
    public string RemapText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains("/Game/", StringComparison.Ordinal)
            ? GamePath().Replace(text, m => RemapName(m.Value))
            : RemapObject(text);
    }

    /// <summary>Package path of a /Game path without extension or object part.</summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var p = path.Trim().Replace('\\', '/');
        var dot = p.IndexOf('.', p.LastIndexOf('/') + 1);
        if (dot > 0)
        {
            p = p[..dot];
        }

        if (!p.StartsWith('/'))
        {
            throw new ArgumentException($"'{path}' is not a package path (/Game/...).", nameof(path));
        }

        return p;
    }

    /// <summary>Last path segment.</summary>
    public static string Leaf(string packagePath)
    {
        var slash = packagePath.LastIndexOf('/');
        return slash < 0 ? packagePath : packagePath[(slash + 1)..];
    }

    /// <summary>Folder of a package path (no trailing slash).</summary>
    public static string Folder(string packagePath)
    {
        var slash = packagePath.LastIndexOf('/');
        return slash <= 0 ? "/" : packagePath[..slash];
    }

    [GeneratedRegex(@"/Game/[A-Za-z0-9_\-/]+(?:\.[A-Za-z0-9_\-]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex GamePath();
}
