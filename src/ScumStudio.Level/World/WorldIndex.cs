using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Reading;

namespace ScumStudio.Level.World;

/// <summary>
/// Index of The_Island's packages (persistent level, ~1,900 sublevels, landscape tiles, BuiltData, HLOD) grouped by map
/// cell and kind. Built from a file list alone (<see cref="Build"/>), so it works without the persistent level; when
/// <c>The_Island.umap</c> is available its StreamingLevels can be cross-checked (<see cref="WithStreamingLevels"/>).
/// </summary>
public sealed class WorldIndex
{
    /// <summary>Virtual folder of The_Island's packages.</summary>
    public const string DefaultRoot = "SCUM/Content/ConZ_Files/Maps/The_Island";

    /// <summary>Package path of the persistent level.</summary>
    public const string PersistentLevelPackagePath = "/Game/ConZ_Files/Maps/The_Island/The_Island";

    private readonly Dictionary<string, WorldPackage> _byName;
    private readonly Dictionary<string, WorldPackage> _byPackagePath;

    private WorldIndex(string root, IReadOnlyList<WorldPackage> packages, StreamingCrossCheck? crossCheck)
    {
        Root = root;
        Packages = packages;
        CrossCheck = crossCheck;
        _byName = new Dictionary<string, WorldPackage>(StringComparer.OrdinalIgnoreCase);
        _byPackagePath = new Dictionary<string, WorldPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packages)
        {
            _byPackagePath.TryAdd(p.PackagePath, p);

            // Level names win over same-named non-level packages.
            if (!_byName.TryGetValue(p.Name, out var existing) || (!existing.IsMap && p.IsMap))
            {
                _byName[p.Name] = p;
            }
        }
    }

    /// <summary>Virtual root folder the index was built from.</summary>
    public string Root { get; }

    /// <summary>All packages, sorted by kind, then cell, then name.</summary>
    public IReadOnlyList<WorldPackage> Packages { get; }

    /// <summary>The persistent level, when present.</summary>
    public WorldPackage? PersistentLevel => Packages.FirstOrDefault(p => p.Kind == WorldPackageKind.Persistent);

    /// <summary>Every level package other than the persistent level.</summary>
    public IEnumerable<WorldPackage> Sublevels => Packages.Where(p => p.IsSublevel);

    /// <summary>Result of <see cref="WithStreamingLevels"/>, or null for a names-only index.</summary>
    public StreamingCrossCheck? CrossCheck { get; }

    /// <summary>
    /// Builds the index from virtual file paths (other files and paths outside <paramref name="root"/> are ignored;
    /// <c>.uexp</c>/<c>.ubulk</c> companions are skipped).
    /// </summary>
    public static WorldIndex Build(IEnumerable<string> filePaths, string root = DefaultRoot, string projectName = AssetPaths.DefaultProjectName)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var normalizedRoot = VirtualPath.Normalize(root);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packages = new List<WorldPackage>();
        foreach (var file in filePaths)
        {
            var entry = WorldNameParser.Classify(file, normalizedRoot, projectName);
            if (entry is not null && seen.Add(entry.PackagePath))
            {
                packages.Add(entry);
            }
        }

        // Link each level to its BuiltData package (same folder, <Level>_BuiltData).
        var builtData = packages
            .Where(p => p.Kind == WorldPackageKind.BuiltData)
            .GroupBy(p => (p.Folder, p.Owner ?? string.Empty), FolderNameComparer.Instance)
            .ToDictionary(g => g.Key, g => g.First().PackagePath, FolderNameComparer.Instance);
        for (var i = 0; i < packages.Count; i++)
        {
            var p = packages[i];
            if (p.IsMap && builtData.TryGetValue((p.Folder, p.Name), out var bd))
            {
                packages[i] = p with { BuiltDataPackage = bd };
            }
        }

        return new WorldIndex(normalizedRoot, Sort(packages), null);
    }

    /// <summary>Builds the index from an <see cref="IFileSource"/> (paks, loose folders or a composite).</summary>
    public static WorldIndex FromFileSource(IFileSource source, string root = DefaultRoot, string projectName = AssetPaths.DefaultProjectName)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Build(source.EnumerateFiles(root, recursive: true), root, projectName);
    }

    /// <summary>Builds the index from an <see cref="AssetCatalog"/>'s package list.</summary>
    public static WorldIndex FromCatalog(AssetCatalog catalog, string root = DefaultRoot)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var index = Build(catalog.PackageFiles, root, catalog.ProjectName);
        return string.Equals(VirtualPath.Normalize(root), DefaultRoot, StringComparison.OrdinalIgnoreCase) ? index.WithPluginLevels(catalog) : index;
    }

    /// <summary>Folder of the game's feature plugins (DLC packs); each may add sublevels of The_Island.</summary>
    public const string PluginsRoot = "SCUM/Plugins/GameFeatures";

    /// <summary>
    /// The island's sublevels that the game's DLC plugins add (<c>SCUM/Plugins/GameFeatures/&lt;Pack&gt;/Content/World/Maps/The_Island/</c>,
    /// e.g. the Wild Hunter traders' grottos), classified like the rest. Hektor (Discord): "the traders in the Wild Hunter
    /// packs remain after you delete everything": the app never listed those levels.
    /// </summary>
    public WorldIndex WithPluginLevels(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var plugins = new List<WorldPackage>();
        var seen = new HashSet<string>(Packages.Select(p => p.PackagePath), StringComparer.OrdinalIgnoreCase);
        foreach (var file in catalog.PackageFiles)
        {
            var path = VirtualPath.Normalize(file);
            if (!path.StartsWith(PluginsRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // SCUM/Plugins/GameFeatures/<Pack>/Content/World/Maps/The_Island/<Level>.umap
            var island = path.IndexOf("/Content/World/Maps/The_Island/", StringComparison.OrdinalIgnoreCase);
            if (island < 0)
            {
                continue;
            }

            var pluginRoot = path[..(island + "/Content/World/Maps/The_Island".Length)];
            if (WorldNameParser.Classify(path, pluginRoot, catalog.ProjectName) is { IsMap: true } entry && seen.Add(entry.PackagePath))
            {
                plugins.Add(entry);
            }
        }

        return plugins.Count == 0 ? this : new WorldIndex(Root, Sort([.. Packages, .. plugins]), CrossCheck);
    }

    /// <summary>
    /// Builds the index from <paramref name="catalog"/> and, when the persistent level is present and
    /// <paramref name="reader"/> is given, cross-checks its StreamingLevels. A persistent level that cannot be read is
    /// logged and the names-only index returned.
    /// </summary>
    public static WorldIndex Load(AssetCatalog catalog, ILevelReader? reader, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var index = FromCatalog(catalog);
        return index.TryCrossCheck(reader, logger, cancellationToken);
    }

    /// <summary>
    /// Reads the persistent level's StreamingLevels with <paramref name="reader"/> and returns the cross-checked index,
    /// or this index when there is no persistent level, no reader, or reading fails (logged as a warning).
    /// </summary>
    public WorldIndex TryCrossCheck(ILevelReader? reader, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        logger ??= NullLogger.Instance;
        if (reader is null || PersistentLevel is not { } persistent)
        {
            return this;
        }

        try
        {
            var streaming = reader.ReadStreamingLevels(persistent.PackagePath, cancellationToken);
            return WithStreamingLevels(streaming);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogWarning("Could not read the streaming levels of {Level}: {Message}", persistent.PackagePath, ex.Message);
            return this;
        }
    }

    /// <summary>
    /// Returns a copy whose sublevels carry <see cref="WorldPackage.IsStreamed"/> and whose <see cref="CrossCheck"/>
    /// compares <paramref name="streamingLevels"/> (from the persistent level) with the packages found.
    /// </summary>
    public WorldIndex WithStreamingLevels(IReadOnlyList<StreamingLevelInfo> streamingLevels)
    {
        ArgumentNullException.ThrowIfNull(streamingLevels);
        var streamed = new HashSet<string>(streamingLevels.Select(s => s.PackagePath), StringComparer.OrdinalIgnoreCase);
        var packages = Packages
            .Select(p => p.IsSublevel ? p with { IsStreamed = streamed.Contains(p.PackagePath) } : p)
            .ToList();
        var missing = streamingLevels.Where(s => !_byPackagePath.TryGetValue(s.PackagePath, out var p) || !p.IsMap).ToList();
        var notStreamed = packages.Where(p => p.IsStreamed == false).ToList();
        var check = new StreamingCrossCheck(streamingLevels.Count, streamingLevels.Count - missing.Count, missing, notStreamed);
        return new WorldIndex(Root, packages, check);
    }

    /// <summary>
    /// Returns a copy whose sublevels carry their cooked <c>FWorldTileInfo</c> (<see cref="WorldPackage.Tile"/>), read from
    /// each package header in <paramref name="catalog"/>, with <see cref="WorldPackage.ParentPackagePath"/> resolved against
    /// this index. A package that cannot be read keeps no tile (logged at debug level). ~1,900 headers take a few seconds
    /// from paks; <paramref name="progress"/> receives the number of packages processed.
    /// </summary>
    public WorldIndex WithTileInfo(AssetCatalog catalog, ILogger? logger = null, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        logger ??= NullLogger.Instance;
        var packages = new List<WorldPackage>(Packages.Count);
        var done = 0;
        var read = 0;
        foreach (var p in Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = p;
            if (p.IsSublevel && TryReadTile(catalog, p, logger) is { } tile)
            {
                read++;
                updated = p with { Tile = tile, ParentPackagePath = ResolveParent(tile) };
            }

            packages.Add(updated);
            progress?.Report(++done);
        }

        logger.LogDebug("Read tile info for {Read} of {Total} packages.", read, Packages.Count);
        return new WorldIndex(Root, packages, CrossCheck);
    }

    /// <summary>The parent tile of <paramref name="package"/> (needs <see cref="WithTileInfo"/>), or null.</summary>
    public WorldPackage? Parent(WorldPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return package.ParentPackagePath is { } parent && _byPackagePath.TryGetValue(parent, out var p) ? p : null;
    }

    /// <summary>The tiles whose parent is <paramref name="package"/> (needs <see cref="WithTileInfo"/>), sorted by name.</summary>
    public IReadOnlyList<WorldPackage> Children(WorldPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return Packages
            .Where(p => p.ParentPackagePath is { } parent && string.Equals(parent, package.PackagePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Root tiles of the composition: sublevels with a tile that names no parent (needs <see cref="WithTileInfo"/>).</summary>
    public IReadOnlyList<WorldPackage> RootTiles =>
        Packages.Where(p => p.Tile is { HasParent: false }).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private string? ResolveParent(WorldTileInfo tile)
    {
        if (!tile.HasParent)
        {
            return null;
        }

        var name = tile.ParentTilePackageName;
        if (name.Contains('/'))
        {
            return _byPackagePath.TryGetValue(name, out var byPath) ? byPath.PackagePath : name;
        }

        return _byName.TryGetValue(name, out var byName) && byName.IsMap ? byName.PackagePath : null;
    }

    private static WorldTileInfo? TryReadTile(AssetCatalog catalog, WorldPackage package, ILogger logger)
    {
        try
        {
            if (!catalog.TryGetPackageFile(package.PackagePath, out var file))
            {
                return null;
            }

            // Only the header is needed: the summary points at the tile info inside the .umap.
            return WorldTileInfo.TryRead(CookedPackage.Parse(file.Read(), [], null, package.PackagePath));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            logger.LogDebug("No tile info for {Package}: {Message}", package.PackagePath, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Finds a package by name (<c>A_0_Outpost</c>), package path, object path or virtual file path (case-insensitive).
    /// A bare name prefers the level over a same-named non-level package.
    /// </summary>
    public WorldPackage? Find(string nameOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        var text = nameOrPath.Trim().Replace('\\', '/');
        if (!text.Contains('/'))
        {
            var bare = AssetPaths.SplitObjectPath(text).PackagePath;
            return _byName.TryGetValue(bare, out var byName) ? byName : null;
        }

        var packagePath = text.StartsWith('/')
            ? AssetPaths.SplitObjectPath(text).PackagePath
            : AssetPaths.ToPackagePath(text);
        return _byPackagePath.TryGetValue(packagePath, out var byPath) ? byPath : null;
    }

    /// <summary>Packages whose name parses to <paramref name="cell"/> (any kind).</summary>
    public IReadOnlyList<WorldPackage> InCell(MapCell cell) => Packages.Where(p => p.Cell == cell).ToList();

    /// <summary>Packages of the given kind.</summary>
    public IReadOnlyList<WorldPackage> OfKind(WorldPackageKind kind) => Packages.Where(p => p.Kind == kind).ToList();

    /// <summary>Number of packages per kind (kinds without packages are omitted).</summary>
    public IReadOnlyDictionary<WorldPackageKind, int> CountByKind() =>
        Packages.GroupBy(p => p.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>Number of level packages per cell (levels without a cell are omitted).</summary>
    public IReadOnlyDictionary<MapCell, int> SublevelCountByCell() =>
        Sublevels.Where(p => p.Cell is not null).GroupBy(p => p.Cell!.Value).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

    private static List<WorldPackage> Sort(List<WorldPackage> packages) =>
        packages
            .OrderBy(p => p.Kind)
            .ThenBy(p => p.Cell is null ? 1 : 0)
            .ThenBy(p => p.Cell)
            .ThenBy(p => p.Folder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private sealed class FolderNameComparer : IEqualityComparer<(string Folder, string Name)>
    {
        public static readonly FolderNameComparer Instance = new();

        public bool Equals((string Folder, string Name) x, (string Folder, string Name) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Folder, y.Folder) && StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name);

        public int GetHashCode((string Folder, string Name) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Folder), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
