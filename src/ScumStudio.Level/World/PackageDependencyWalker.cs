using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;

namespace ScumStudio.Level.World;

/// <summary>
/// Result of <see cref="PackageDependencyWalker.Walk"/>: the transitive closure of package references starting at a set of
/// level packages — every package the game would load for those levels (meshes, Blueprints, materials, textures,
/// built data, layer infos, …), which of them exist in the source and which do not.
/// </summary>
/// <param name="Roots">The starting packages (level package paths).</param>
/// <param name="Present">Package paths of the closure that exist in the source (roots included), sorted.</param>
/// <param name="Missing">Referenced <c>/Game/…</c> packages that are not in the source, sorted.</param>
/// <param name="Unresolved">Referenced packages outside <c>/Game</c> (engine, plugins) that the source does not contain; usually harmless for rendering.</param>
/// <param name="Files">Provider file paths (<c>SCUM/Content/…</c>) of every present package: header, <c>.uexp</c>, <c>.ubulk</c>, <c>.uptnl</c> when present; sorted.</param>
/// <param name="Depth">How many reference hops were followed from the roots.</param>
/// <param name="Warnings">Packages that exist but could not be parsed.</param>
public sealed record DependencyReport(
    IReadOnlyList<string> Roots,
    IReadOnlyList<string> Present,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> Files,
    int Depth,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Missing packages grouped by their folder (package path up to the last slash), most-missing first.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> MissingByFolder() => GroupByFolder(Missing);

    /// <summary>Present packages grouped by folder, largest first.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> PresentByFolder() => GroupByFolder(Present);

    /// <summary>Groups package paths by their parent folder (with a trailing slash).</summary>
    public static IReadOnlyList<KeyValuePair<string, int>> GroupByFolder(IEnumerable<string> packagePaths) =>
        packagePaths
            .GroupBy(FolderOf, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .OrderByDescending(p => p.Value)
            .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Folder of a package path, with a trailing slash (<c>/Game/A/B</c> → <c>/Game/A/</c>).</summary>
    public static string FolderOf(string packagePath)
    {
        var slash = packagePath.LastIndexOf('/');
        return slash <= 0 ? "/" : packagePath[..(slash + 1)];
    }
}

/// <summary>
/// Follows package imports from level packages through everything they reference, reading only package headers
/// (<c>.uasset</c>/<c>.umap</c> import tables, no export payloads). Used to answer "which files does this cell need?"
/// — the exact pak entry list an extraction script must pull, or the list of packages a loose slice still lacks.
/// </summary>
public sealed class PackageDependencyWalker
{
    private readonly AssetCatalog _catalog;
    private readonly ILogger? _logger;

    /// <summary>Sidecar extensions copied along with a package header.</summary>
    public static IReadOnlyList<string> SidecarExtensions { get; } = [".uexp", ".ubulk", ".uptnl"];

    /// <summary>Creates a walker over <paramref name="catalog"/>.</summary>
    public PackageDependencyWalker(AssetCatalog catalog, ILogger? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger;
    }

    /// <summary>
    /// Walks the reference closure of <paramref name="rootPackagePaths"/> up to <paramref name="maxDepth"/> hops
    /// (0 = the roots only, 1 = what the levels import directly: meshes, Blueprint classes, built data …).
    /// </summary>
    public DependencyReport Walk(IEnumerable<string> rootPackagePaths, int maxDepth = int.MaxValue, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPackagePaths);
        var roots = rootPackagePaths
            .Select(p => AssetPaths.SplitObjectPath(p).PackagePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var present = new List<string>();
        var missing = new List<string>();
        var unresolved = new List<string>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var frontier = new Queue<(string Path, int Depth)>();
        foreach (var root in roots)
        {
            if (seen.Add(root))
            {
                frontier.Enqueue((root, 0));
            }
        }

        var reached = 0;
        var visited = 0;
        while (frontier.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, depth) = frontier.Dequeue();
            reached = Math.Max(reached, depth);
            if (!_catalog.TryGetPackageFile(path, out var file))
            {
                (IsGamePackage(path) ? missing : unresolved).Add(path);
                continue;
            }

            present.Add(path);
            AddFiles(file.Path, files);
            visited++;
            progress?.Report(visited);
            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (var referenced in ReadReferencedPackages(path, file, warnings))
            {
                if (seen.Add(referenced))
                {
                    frontier.Enqueue((referenced, depth + 1));
                }
            }
        }

        present.Sort(StringComparer.OrdinalIgnoreCase);
        missing.Sort(StringComparer.OrdinalIgnoreCase);
        unresolved.Sort(StringComparer.OrdinalIgnoreCase);
        var fileList = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        _logger?.LogInformation("Dependency walk from {Roots} root(s): {Present} packages present ({Files} files), {Missing} missing, {Unresolved} engine/plugin references unresolved, depth {Depth}.",
            roots.Count, present.Count, fileList.Count, missing.Count, unresolved.Count, reached);
        return new DependencyReport(roots, present, missing, unresolved, fileList, reached, warnings);
    }

    /// <summary>Package paths imported by one package (its <c>Package</c>-class imports), excluding native <c>/Script/</c> packages.</summary>
    public IReadOnlyList<string> ReadReferencedPackages(string packagePath)
    {
        if (!_catalog.TryGetPackageFile(packagePath, out var file))
        {
            throw new FileNotFoundException($"Package not found: {packagePath}", packagePath);
        }

        var warnings = new List<string>();
        var result = ReadReferencedPackages(packagePath, file, warnings);
        return warnings.Count == 0 ? result : throw new InvalidDataException(warnings[0]);
    }

    private static bool IsGamePackage(string path) => path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase);

    private List<string> ReadReferencedPackages(string packagePath, CUE4Parse.FileProvider.Objects.GameFile file, List<string> warnings)
    {
        var result = new List<string>();
        byte[] header;
        try
        {
            header = file.Read();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            warnings.Add($"{packagePath}: header could not be read ({ex.Message}).");
            return result;
        }

        CookedPackage package;
        try
        {
            // Only the header is needed: the import table lives in the .uasset/.umap, never in the .uexp.
            package = CookedPackage.Parse(header, [], null, packagePath);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            warnings.Add($"{packagePath}: header could not be parsed ({ex.Message}).");
            return result;
        }

        foreach (var import in package.Imports)
        {
            if (import.OuterIndex != 0 || package.ResolveName(import.ClassName) != "Package")
            {
                continue;
            }

            var name = package.ResolveName(import.ObjectName);
            if (name.Length < 2 || name[0] != '/' || name.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(name);
        }

        return result;
    }

    private void AddFiles(string headerPath, HashSet<string> files)
    {
        var path = headerPath.Replace('\\', '/');
        files.Add(path);
        var dot = path.LastIndexOf('.');
        var stem = dot > path.LastIndexOf('/') ? path[..dot] : path;
        foreach (var extension in SidecarExtensions)
        {
            if (_catalog.Provider.Files.TryGetValue(stem + extension, out var sidecar))
            {
                files.Add(sidecar.Path.Replace('\\', '/'));
            }
        }
    }
}
