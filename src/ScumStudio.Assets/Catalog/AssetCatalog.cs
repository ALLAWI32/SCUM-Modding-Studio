using System.Diagnostics.CodeAnalysis;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Pak.Objects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScumStudio.Assets.Catalog;

/// <summary>How an <see cref="AssetCatalog"/> was opened.</summary>
public enum AssetSourceKind
{
    /// <summary>A folder of <c>.pak</c>/<c>.utoc</c> containers (or a single pak).</summary>
    Paks,

    /// <summary>Loose cooked files extracted to a folder (<c>SCUM/Content/...</c> layout).</summary>
    Loose,

    /// <summary>An externally created CUE4Parse provider.</summary>
    Provider,
}

/// <summary>
/// Read-only facade over a CUE4Parse file provider (UE 4.27 versions): package and object loading by UE path,
/// export listings, class queries and a <see cref="PackageIndex"/> folder tree. The renderer and editors use it to reach
/// cooked assets whether they come from the game's paks or from loose extracted files.
/// </summary>
/// <remarks>
/// Paths accepted by every method: object paths (<c>/Game/A/B.B</c>), package paths (<c>/Game/A/B</c>) and provider file
/// paths (<c>SCUM/Content/A/B.uasset</c>). Instances are safe for concurrent reads (CUE4Parse providers are).
/// Loaded packages are kept in a small LRU cache (<see cref="PackageCacheSize"/>).
/// </remarks>
public sealed class AssetCatalog : IDisposable
{
    private readonly bool _ownsProvider;
    private readonly ILogger _logger;
    private readonly object _cacheLock = new();
    private readonly LinkedList<(string Key, IPackage Package)> _lru = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, IPackage Package)>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string>? _packageFiles;
    private PackageIndex? _index;
    private bool _disposed;

    private AssetCatalog(AbstractFileProvider provider, bool ownsProvider, AssetSourceKind kind, string displayName, string projectName, ILogger? logger, string? sourcePath = null)
    {
        Provider = provider;
        _ownsProvider = ownsProvider;
        SourceKind = kind;
        DisplayName = displayName;
        ProjectName = projectName;
        SourcePath = sourcePath;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// The opened source on disk: the Paks folder or single <c>.pak</c> for <see cref="AssetSourceKind.Paks"/>, the project
    /// root (the folder holding <c>Content</c>) for <see cref="AssetSourceKind.Loose"/>; null for a wrapped provider.
    /// </summary>
    public string? SourcePath { get; }

    /// <summary>The CUE4Parse provider (owned by this catalog unless created with <see cref="FromProvider"/> and <c>ownsProvider=false</c>).</summary>
    public AbstractFileProvider Provider { get; }

    /// <summary>How the catalog was opened.</summary>
    public AssetSourceKind SourceKind { get; }

    /// <summary>Human-readable description of the source (folder or pak list).</summary>
    public string DisplayName { get; }

    /// <summary>Project (virtual root) name, <c>SCUM</c> by default.</summary>
    public string ProjectName { get; }

    /// <summary>Maximum number of packages kept in the load cache (0 disables caching). Default 64.</summary>
    public int PackageCacheSize { get; set; } = 64;

    /// <summary>Number of containers that could not be mounted (encrypted without a matching key).</summary>
    public int UnmountedContainerCount => Provider is IVfsFileProvider vfs ? vfs.UnloadedVfs.Count : 0;

    /// <summary>
    /// Opens <paramref name="source"/>, detecting its kind: a <c>.pak</c> file, a folder with <c>.pak</c>/<c>.utoc</c> files
    /// directly inside (paks), otherwise a loose folder (see <see cref="OpenLoose"/>).
    /// </summary>
    public static AssetCatalog Open(string source, AssetCatalogOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var full = Path.GetFullPath(source);
        if (File.Exists(full))
        {
            return OpenPaks(full, options);
        }

        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Asset source not found: {full}");
        }

        var hasContainers = Directory.EnumerateFiles(full, "*", SearchOption.TopDirectoryOnly).Any(IsContainer);
        return hasContainers ? OpenPaks(full, options) : OpenLoose(full, options);
    }

    /// <summary>
    /// Opens every <c>.pak</c>/<c>.utoc</c> directly inside <paramref name="paksDirectoryOrFile"/> (e.g. <c>SCUM\Content\Paks</c>),
    /// or a single pak file. Encrypted containers are mounted when <see cref="AssetCatalogOptions.AesKey"/> matches.
    /// </summary>
    /// <exception cref="ArgumentException">The AES key text is not 64 hex digits (the message never contains the key).</exception>
    public static AssetCatalog OpenPaks(string paksDirectoryOrFile, AssetCatalogOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paksDirectoryOrFile);
        options ??= new AssetCatalogOptions();
        var key = options.AesKey is null ? null : ParseAesKey(options.AesKey);
        var full = Path.GetFullPath(paksDirectoryOrFile);
        List<string> containers;
        if (File.Exists(full))
        {
            containers = [full];
        }
        else if (Directory.Exists(full))
        {
            containers = Directory.EnumerateFiles(full, "*", SearchOption.TopDirectoryOnly)
                .Where(IsContainer)
                .Where(f => options.PakFileFilter?.Invoke(Path.GetFileName(f)) ?? true)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            throw new DirectoryNotFoundException($"Paks folder not found: {full}");
        }

        var provider = new ScumFileProvider();
        try
        {
            foreach (var container in containers)
            {
                provider.RegisterVfs(container);
            }

            var mounted = provider.Mount();
            if (key is not null)
            {
                mounted += provider.SubmitAesKey(key);
                Array.Clear(key);
            }

            foreach (var vfs in provider.MountedVfs)
            {
                vfs.IsConcurrent = true;
            }

            provider.AliasDoubledProjectPaths(options.ProjectName);
            AddOverlays(provider, options);
            var logger = options.Logger ?? NullLogger.Instance;
            var locked = provider.UnloadedVfs.Count(v => v.IsEncrypted);
            if (locked > 0)
            {
                logger.LogWarning("{Count} encrypted container(s) were not mounted: {Reason}.", locked,
                    options.AesKey is null ? "no AES key was provided" : "the AES key does not match");
            }

            // CUE4Parse inflates zlib through a native library this app does not ship: such a pak (Hektor's map) is read by
            // unpacking it, which inflates in .NET (PakFileSource, the Import mod button).
            var zlib = CUE4Parse.Compression.ZlibHelper.Instance is null
                ? provider.Files.Values.OfType<FPakEntry>().Count(e => e.IsCompressed && e.CompressionMethod == CUE4Parse.Compression.CompressionMethod.Zlib)
                : 0;
            if (zlib > 0)
            {
                logger.LogWarning("{Count} file(s) are zlib-compressed and cannot be read straight from the pak: import it (Projects → Import mod) or unpack it (scumstudio pak unpack) and open the folder.", zlib);
            }

            logger.LogDebug("Mounted {Mounted} of {Total} containers from {Source}.", mounted, containers.Count, full);
            var display = containers.Count == 1 ? containers[0] : $"{containers.Count} containers in {full}";
            return new AssetCatalog(provider, true, AssetSourceKind.Paks, display, options.ProjectName, options.Logger, full);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens loose cooked files. <paramref name="directory"/> may be the project folder (contains <c>Content/</c>), its parent
    /// (contains <c>SCUM/Content/</c>, e.g. an unpacked mod tree) or the <c>Content</c> folder itself. Files are mounted as
    /// <c>SCUM/Content/...</c> regardless of the folder's own name.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">No <c>Content</c> folder could be located.</exception>
    public static AssetCatalog OpenLoose(string directory, AssetCatalogOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        options ??= new AssetCatalogOptions();
        var project = FindLooseProjectRoot(directory, options.ProjectName)
                      ?? throw new DirectoryNotFoundException(
                          $"No '{options.ProjectName}/Content' or 'Content' folder found at {Path.GetFullPath(directory)}.");
        var provider = new ScumFileProvider();
        try
        {
            var count = provider.AddLooseProject(project, options.ProjectName, 0);
            AddOverlays(provider, options);
            (options.Logger ?? NullLogger.Instance).LogDebug("Mounted {Count} loose files from {Dir}.", count, project);
            return new AssetCatalog(provider, true, AssetSourceKind.Loose, project, options.ProjectName, options.Logger, project);
        }
        catch
        {
            provider.Dispose();
            throw;
        }
    }

    /// <summary>Wraps an existing provider (e.g. the one of a <c>PakFileSource</c>).</summary>
    public static AssetCatalog FromProvider(AbstractFileProvider provider, bool ownsProvider = false,
        string projectName = AssetPaths.DefaultProjectName, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new AssetCatalog(provider, ownsProvider, AssetSourceKind.Provider, provider.GetType().Name, projectName, logger);
    }

    /// <summary>
    /// Locates the loose project folder (the one containing <c>Content/</c>) for <paramref name="directory"/>:
    /// the folder itself, its <c>&lt;projectName&gt;</c> child, its single child that contains <c>Content/</c>, or its parent when
    /// the folder itself is named <c>Content</c>. Returns null when none matches.
    /// </summary>
    public static string? FindLooseProjectRoot(string directory, string projectName = AssetPaths.DefaultProjectName)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(directory));
        if (!dir.Exists)
        {
            return null;
        }

        if (string.Equals(dir.Name, "Content", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null)
        {
            return dir.Parent.FullName;
        }

        if (HasContent(dir.FullName))
        {
            return dir.FullName;
        }

        var named = Path.Combine(dir.FullName, projectName);
        if (HasContent(named))
        {
            return named;
        }

        var candidates = dir.EnumerateDirectories().Where(d => HasContent(d.FullName)).ToList();
        return candidates.Count == 1 ? candidates[0].FullName : null;

        static bool HasContent(string d) => Directory.Exists(Path.Combine(d, "Content"));
    }

    /// <summary>Provider paths of every package (<c>.uasset</c>/<c>.umap</c>), de-duplicated and sorted (ordinal, case-insensitive).</summary>
    public IReadOnlyList<string> PackageFiles
    {
        get
        {
            ThrowIfDisposed();
            var doubled = ProjectName + "/" + ProjectName + "/";
            return _packageFiles ??= Provider.Files.Keys
                .Where(AssetPaths.IsPackageFile)
                .Where(p => !p.StartsWith(doubled, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>True when a package exists for <paramref name="path"/>.</summary>
    public bool PackageExists(string path) => TryGetPackageFile(path, out _);

    /// <summary>Resolves the provider <see cref="GameFile"/> of a package (object, package or file path).</summary>
    public bool TryGetPackageFile(string path, [NotNullWhen(true)] out GameFile? file)
    {
        ThrowIfDisposed();
        var stem = AssetPaths.ToFilePathWithoutExtension(path, ProjectName);
        foreach (var ext in AssetPaths.PackageExtensions)
        {
            if (Provider.Files.TryGetValue(stem + ext, out file))
            {
                return true;
            }
        }

        file = null;
        return false;
    }

    /// <summary>Loads a package (exports are deserialized lazily on access).</summary>
    /// <exception cref="FileNotFoundException">No such package.</exception>
    public IPackage LoadPackage(string path)
    {
        if (!TryGetPackageFile(path, out var file))
        {
            throw new FileNotFoundException($"Package not found: {path}", path);
        }

        var key = file.Path;
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Package;
            }
        }

        var package = Provider.LoadPackage(file);
        lock (_cacheLock)
        {
            if (PackageCacheSize > 0 && !_cache.ContainsKey(key))
            {
                _cache[key] = _lru.AddFirst((key, package));
                while (_lru.Count > PackageCacheSize)
                {
                    _cache.Remove(_lru.Last!.Value.Key);
                    _lru.RemoveLast();
                }
            }
        }

        return package;
    }

    /// <summary>Loads a package; returns false (and logs at debug level) when it is missing or unreadable.</summary>
    public bool TryLoadPackage(string path, [NotNullWhen(true)] out IPackage? package)
    {
        try
        {
            package = LoadPackage(path);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Could not load package {Path}.", path);
            package = null;
            return false;
        }
    }

    /// <summary>
    /// Loads an object by path. <c>/Game/A/B.C</c> loads export <c>C</c>; <c>/Game/A/B</c> and <c>/Game/A/B.B</c> load the export
    /// named like the package (the main asset), falling back to the first export when no export has that name.
    /// </summary>
    /// <exception cref="FileNotFoundException">No such package.</exception>
    /// <exception cref="KeyNotFoundException">The package has no such export.</exception>
    /// <exception cref="InvalidCastException">The export is not a <typeparamref name="T"/>.</exception>
    public T LoadObject<T>(string objectPath) where T : UObject
    {
        var obj = LoadObject(objectPath);
        return obj as T ?? throw new InvalidCastException($"{objectPath} is a {obj.ExportType}, not a {typeof(T).Name}.");
    }

    /// <summary>Loads an object by path (see <see cref="LoadObject{T}"/>).</summary>
    public UObject LoadObject(string objectPath)
    {
        var (packagePath, objectName) = AssetPaths.SplitObjectPath(objectPath);
        var package = LoadPackage(packagePath);
        var export = package.GetExportOrNull(objectName, StringComparison.OrdinalIgnoreCase);
        var packageName = packagePath[(packagePath.LastIndexOf('/') + 1)..];
        if (export is null && string.Equals(objectName, packageName, StringComparison.OrdinalIgnoreCase) && package.ExportMapLength > 0)
        {
            // "Main asset" request (/Game/A/B or /Game/A/B.B) for a package whose asset export has another name.
            export = package.GetExport(0);
        }

        return export ?? throw new KeyNotFoundException($"Package {packagePath} has no export named '{objectName}'.");
    }

    /// <summary>Loads an object of type <typeparamref name="T"/>; false when missing, unreadable or of another type.</summary>
    public bool TryLoadObject<T>(string objectPath, [NotNullWhen(true)] out T? obj) where T : UObject
    {
        try
        {
            obj = LoadObject(objectPath) as T;
            return obj is not null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Could not load object {Path}.", objectPath);
            obj = null;
            return false;
        }
    }

    /// <summary>
    /// Returns the first export of the package assignable to <typeparamref name="T"/> (useful when the asset export is not
    /// named like the package), or null.
    /// </summary>
    public T? LoadFirstExport<T>(string packagePath) where T : UObject
    {
        var package = LoadPackage(packagePath);
        for (var i = 0; i < package.ExportMapLength; i++)
        {
            if (package.GetExport(i) is T t)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>Lists a package's exports from its header (no export data is deserialized; payload files are not read).</summary>
    public IReadOnlyList<ExportInfo> GetExports(string path)
    {
        if (!TryGetPackageFile(path, out var file))
        {
            throw new FileNotFoundException($"Package not found: {path}", path);
        }

        return ReadExportInfos(ReadHeader(file));
    }

    /// <summary>
    /// Class of the main export of a package (the export named like the package, else the first export), read from the header;
    /// null when the package is missing or unreadable.
    /// </summary>
    public string? GetMainClassName(string path)
    {
        if (!TryGetPackageFile(path, out var file))
        {
            return null;
        }

        try
        {
            return MainExport(ReadExportInfos(ReadHeader(file)), PackageName(file.Path))?.ClassName;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Could not read the header of {Path}.", file.Path);
            return null;
        }
    }

    /// <summary>
    /// Builds (and caches as <see cref="Index"/>) the package index. With <paramref name="resolveClasses"/> every package header is
    /// read (in parallel) to find the main export's class; without it only the file list is used (instant even for full game paks).
    /// </summary>
    public PackageIndex BuildIndex(bool resolveClasses = true, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = PackageFiles;
        var entries = new PackageEntry[files.Count];
        var done = 0;
        var parallel = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.For(0, files.Count, parallel, i =>
        {
            var file = files[i];
            ExportInfo? main = null;
            var name = PackageName(file);
            if (resolveClasses)
            {
                try
                {
                    main = Provider.Files.TryGetValue(file, out var gameFile)
                        ? MainExport(ReadExportInfos(ReadHeader(gameFile)), name)
                        : null;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogDebug(ex, "Could not read the header of {Path}.", file);
                }
            }

            var exportName = main is null || string.Equals(main.Name, name, StringComparison.OrdinalIgnoreCase) ? null : main.Name;
            entries[i] = new PackageEntry(file, AssetPaths.ToPackagePath(file, ProjectName), main?.ClassName, exportName);
            var n = Interlocked.Increment(ref done);
            if (progress is not null && (n % 256 == 0 || n == files.Count))
            {
                progress.Report(n);
            }
        });

        var sorted = entries.OrderBy(e => e.PackagePath, StringComparer.OrdinalIgnoreCase).ToList();
        var index = new PackageIndex(sorted, resolveClasses);
        _index = index;
        return index;
    }

    /// <summary>Asynchronous <see cref="BuildIndex"/> (runs on the thread pool).</summary>
    public Task<PackageIndex> BuildIndexAsync(bool resolveClasses = true, IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => BuildIndex(resolveClasses, progress, cancellationToken), cancellationToken);

    /// <summary>The index built last by <see cref="BuildIndex"/>, or null.</summary>
    public PackageIndex? Index => _index;

    /// <summary>
    /// Packages whose main class matches <paramref name="className"/> (e.g. <c>StaticMesh</c>, <c>SkeletalMesh</c>,
    /// <c>Texture2D</c>, <c>World</c>); builds the class index on first use.
    /// </summary>
    public IEnumerable<PackageEntry> FindByClass(string className)
    {
        var index = _index is { HasClasses: true } ? _index : BuildIndex(resolveClasses: true);
        return index.WithClass(className);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_cacheLock)
        {
            _cache.Clear();
            _lru.Clear();
        }

        if (_ownsProvider)
        {
            Provider.Dispose();
        }
    }

    /// <summary>Parses 64 hex digits (optional 0x, separators ' ', '-', '_') into 32 bytes; never echoes the input.</summary>
    internal static byte[] ParseAesKey(string text)
    {
        var s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        var key = new byte[32];
        var nibbles = 0;
        foreach (var c in s)
        {
            if (c is ' ' or '-' or '_')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(c) || nibbles == 64)
            {
                Array.Clear(key);
                throw new ArgumentException("The AES key must be 64 hexadecimal characters (256-bit), optionally prefixed with 0x.", nameof(text));
            }

            var v = c <= '9' ? c - '0' : (char.ToUpperInvariant(c) - 'A' + 10);
            key[nibbles / 2] |= (byte)(nibbles % 2 == 0 ? v << 4 : v);
            nibbles++;
        }

        if (nibbles != 64)
        {
            Array.Clear(key);
            throw new ArgumentException("The AES key must be 64 hexadecimal characters (256-bit), optionally prefixed with 0x.", nameof(text));
        }

        return key;
    }

    private static bool IsContainer(string file) =>
        file.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase);

    private static void AddOverlays(ScumFileProvider provider, AssetCatalogOptions options)
    {
        var order = 1_000_000L;
        foreach (var overlay in options.LooseOverlays)
        {
            var root = FindLooseProjectRoot(overlay, options.ProjectName)
                       ?? throw new DirectoryNotFoundException($"No Content folder found in overlay {Path.GetFullPath(overlay)}.");
            provider.AddLooseProject(root, options.ProjectName, order++);
        }
    }

    /// <summary>Reads only the package header (summary, names, imports, exports); the .uexp is not opened.</summary>
    private IPackage ReadHeader(GameFile file)
    {
        if (file is FPakEntry or OsGameFile)
        {
            return new Package(file.CreateReader(), null, (Lazy<CUE4Parse.UE4.Readers.FArchive?>?)null, null, Provider, useLazySerialization: true);
        }

        return Provider.LoadPackage(file);
    }

    private static IReadOnlyList<ExportInfo> ReadExportInfos(IPackage package)
    {
        var list = new List<ExportInfo>(package.ExportMapLength);
        if (package is Package legacy)
        {
            for (var i = 0; i < legacy.ExportMap.Length; i++)
            {
                var e = legacy.ExportMap[i];
                var cls = legacy.ResolvePackageIndex(e.ClassIndex)?.Name.Text ?? string.Empty;
                var outer = e.OuterIndex is { IsNull: false } ? legacy.ResolvePackageIndex(e.OuterIndex)?.Name.Text : null;
                list.Add(new ExportInfo(i, e.ObjectName.Text, cls, outer, e.SerialSize));
            }

            return list;
        }

        for (var i = 0; i < package.ExportMapLength; i++)
        {
            var resolved = package.ResolvePackageIndex(new FPackageIndex(package, i + 1));
            list.Add(new ExportInfo(i, resolved?.Name.Text ?? string.Empty, resolved?.Class?.Name.Text ?? string.Empty,
                resolved?.Outer is { } o && o.ExportIndex >= 0 ? o.Name.Text : null, 0));
        }

        return list;
    }

    /// <summary>The export named like the package (top-level), else the first export; null for an empty export map.</summary>
    private static ExportInfo? MainExport(IReadOnlyList<ExportInfo> exports, string packageName) =>
        exports.Count == 0
            ? null
            : exports.FirstOrDefault(e => e.OuterName is null && string.Equals(e.Name, packageName, StringComparison.OrdinalIgnoreCase)) ?? exports[0];

    private static string PackageName(string filePath)
    {
        var name = filePath[(filePath.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
