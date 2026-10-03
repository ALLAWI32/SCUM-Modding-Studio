namespace ScumStudio.Assets.Catalog;

/// <summary>One export of a package as listed in its export map (no export data is deserialized).</summary>
/// <param name="Index">Zero-based export index.</param>
/// <param name="Name">Export object name.</param>
/// <param name="ClassName">Class name (e.g. <c>StaticMesh</c>, <c>BlueprintGeneratedClass</c>); empty when unresolved.</param>
/// <param name="OuterName">Name of the outer object, or null for a top-level export.</param>
/// <param name="SerialSize">Serialized size of the export data in bytes.</param>
public sealed record ExportInfo(int Index, string Name, string ClassName, string? OuterName, long SerialSize);

/// <summary>A cooked package known to an <see cref="AssetCatalog"/>.</summary>
/// <param name="FilePath">Provider path including extension, e.g. <c>SCUM/Content/ConZ_Files/X/SM_Y.uasset</c>.</param>
/// <param name="PackagePath">UE package path, e.g. <c>/Game/ConZ_Files/X/SM_Y</c>.</param>
/// <param name="ClassName">
/// Class of the package's main export (the export named like the package; the first export when none is), or null when
/// classes were not resolved or the header could not be read.
/// </param>
/// <param name="MainExportName">
/// Name of that main export when it differs from the package name (e.g. package <c>SK_X_Dashboard</c> holding
/// <c>SK_X_Dashboard_V1</c>); null when it matches or was not resolved.
/// </param>
public sealed record PackageEntry(string FilePath, string PackagePath, string? ClassName, string? MainExportName = null)
{
    /// <summary>Package (asset) name, e.g. <c>SM_Y</c>.</summary>
    public string Name => PackagePath[(PackagePath.LastIndexOf('/') + 1)..];

    /// <summary>Folder part of <see cref="PackagePath"/>, e.g. <c>/Game/ConZ_Files/X</c>.</summary>
    public string Folder => PackagePath[..Math.Max(PackagePath.LastIndexOf('/'), 0)];

    /// <summary>Object path of the main asset, e.g. <c>/Game/ConZ_Files/X/SM_Y.SM_Y</c>.</summary>
    public string ObjectPath => PackagePath + "." + (MainExportName ?? Name);

    /// <summary>True for a level package (<c>.umap</c>).</summary>
    public bool IsMap => FilePath.EndsWith(".umap", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A folder in a <see cref="PackageIndex"/> tree.</summary>
public sealed class PackageFolder
{
    private readonly SortedDictionary<string, PackageFolder> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PackageEntry> _packages = [];

    internal PackageFolder(string name, string path)
    {
        Name = name;
        Path = path;
    }

    /// <summary>Folder name (empty for the root).</summary>
    public string Name { get; }

    /// <summary>Package-style folder path, e.g. <c>/Game/ConZ_Files</c> (empty for the root).</summary>
    public string Path { get; }

    /// <summary>Sub-folders sorted by name (case-insensitive).</summary>
    public IEnumerable<PackageFolder> Folders => _folders.Values;

    /// <summary>Packages directly in this folder, sorted by name.</summary>
    public IReadOnlyList<PackageEntry> Packages => _packages;

    /// <summary>Total number of packages in this folder and below.</summary>
    public int TotalPackageCount => _packages.Count + _folders.Values.Sum(f => f.TotalPackageCount);

    /// <summary>Returns the direct sub-folder named <paramref name="name"/>, or null.</summary>
    public PackageFolder? GetFolder(string name) => _folders.TryGetValue(name, out var f) ? f : null;

    internal PackageFolder GetOrAdd(string name)
    {
        if (!_folders.TryGetValue(name, out var folder))
        {
            folder = new PackageFolder(name, Path + "/" + name);
            _folders.Add(name, folder);
        }

        return folder;
    }

    internal void Add(PackageEntry entry) => _packages.Add(entry);

    internal void Sort()
    {
        _packages.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        foreach (var f in _folders.Values)
        {
            f.Sort();
        }
    }
}

/// <summary>
/// Lightweight index of the packages in an <see cref="AssetCatalog"/>: a flat, sorted list and a folder tree keyed by UE
/// package paths (<c>/Game/...</c>), optionally with the class of each package's main export (read from package headers only).
/// </summary>
public sealed class PackageIndex
{
    private readonly Dictionary<string, PackageEntry> _byPackagePath;

    internal PackageIndex(IReadOnlyList<PackageEntry> entries, bool hasClasses)
    {
        Entries = entries;
        HasClasses = hasClasses;
        _byPackagePath = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
        Root = new PackageFolder(string.Empty, string.Empty);
        foreach (var entry in entries)
        {
            _byPackagePath.TryAdd(entry.PackagePath, entry);
            var folder = Root;
            foreach (var part in entry.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                folder = folder.GetOrAdd(part);
            }

            folder.Add(entry);
        }

        Root.Sort();
    }

    /// <summary>All packages sorted by package path (ordinal, case-insensitive).</summary>
    public IReadOnlyList<PackageEntry> Entries { get; }

    /// <summary>True when <see cref="PackageEntry.ClassName"/> was resolved while building.</summary>
    public bool HasClasses { get; }

    /// <summary>Root of the folder tree (its children are <c>Game</c>, <c>Engine</c>, ...).</summary>
    public PackageFolder Root { get; }

    /// <summary>Number of packages.</summary>
    public int Count => Entries.Count;

    /// <summary>Finds a package by package path, object path or provider file path.</summary>
    public PackageEntry? Find(string path, string projectName = AssetPaths.DefaultProjectName)
    {
        var packagePath = path.Replace('\\', '/').StartsWith('/')
            ? AssetPaths.SplitObjectPath(path).PackagePath
            : AssetPaths.ToPackagePath(path, projectName);
        return _byPackagePath.TryGetValue(packagePath, out var e) ? e : null;
    }

    /// <summary>Returns the folder for a package-style path such as <c>/Game/ConZ_Files</c>, or null.</summary>
    public PackageFolder? GetFolder(string folderPath)
    {
        var folder = Root;
        foreach (var part in folderPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            folder = folder.GetFolder(part);
            if (folder is null)
            {
                return null;
            }
        }

        return folder;
    }

    /// <summary>Packages whose main class equals <paramref name="className"/> (case-insensitive, <c>U</c>/<c>A</c> prefix optional).</summary>
    public IEnumerable<PackageEntry> WithClass(string className) =>
        Entries.Where(e => e.ClassName is not null && ClassNameMatches(e.ClassName, className));

    /// <summary>Distinct main classes with their package counts, most common first.</summary>
    public IReadOnlyList<(string ClassName, int Count)> ClassHistogram() =>
        Entries.Where(e => e.ClassName is not null)
            .GroupBy(e => e.ClassName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(t => t.Item2).ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Case-insensitive class-name match that also accepts the C++ prefix (<c>UStaticMesh</c> matches <c>StaticMesh</c>).
    /// </summary>
    public static bool ClassNameMatches(string actual, string wanted)
    {
        if (string.Equals(actual, wanted, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return wanted.Length > 1 && wanted[0] is 'U' or 'A' or 'u' or 'a' && char.IsUpper(wanted[1])
            && string.Equals(actual, wanted[1..], StringComparison.OrdinalIgnoreCase);
    }
}
