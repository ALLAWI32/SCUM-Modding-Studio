using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.Packages;
using ScumStudio.Formats.Properties;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;

namespace ScumStudio.Level.Export;

/// <summary>
/// The loot a static mesh asset carries itself: its own <c>AssetUserData</c> entries of class <c>ExamineAssetData</c>.
/// </summary>
/// <param name="HasExamineData">True when the asset has at least one examine entry.</param>
/// <param name="Presets">The presets of its entries (class paths, <c>/Game/.../Examine_X.Examine_X_C</c>), in order.</param>
/// <param name="Complete">False when an entry names no preset the studio can read (its loot is not fully known).</param>
public sealed record MeshLootInfo(bool HasExamineData, IReadOnlyList<string> Presets, bool Complete)
{
    /// <summary>A mesh asset without examine data (or one that could not be read).</summary>
    public static MeshLootInfo None { get; } = new(false, [], true);
}

/// <summary>
/// Loot of mesh assets, and their loot-free copies. SCUM's searchable meshes (crates, lockers, desks, trash piles) carry an
/// <c>ExamineAssetData</c> entry in the mesh asset itself (<c>SM_Locker_closed_Military</c>: <c>Examine_Wardrobe_Locker</c>),
/// and many placed components of a level carry entries of their own too (the same preset, another one, or more). A
/// component's entries are written by the export (see <see cref="LevelPackageEditor"/>'s loot patches), the mesh asset's
/// are not: the game still finds those (redriver6155: "default and custom loot merged. it keeps both"). A component whose
/// loot the project changes therefore ends in one of the two layouts the game's own levels use: entries on the component
/// and a mesh without any (a loot-free copy of the mesh, <c>SM_Locker_closed_Military_NoLoot</c>, written into the mod), or
/// no entries on the component and the mesh asset's own loot when that is exactly what is wanted.
/// </summary>
public sealed class MeshLoot
{
    /// <summary>Suffix of a loot-free copy's name (<c>SM_Locker_A_closed_NoLoot</c>).</summary>
    public const string CopySuffix = "_NoLoot";

    private const string ExamineClassSuffix = "ExamineAssetData";
    private const string UserDataProperty = "AssetUserData";

    private readonly Func<string, MeshLootInfo> _describe;
    private readonly Func<string, ClonedPackage>? _build;
    private readonly Dictionary<string, MeshLootInfo> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly SortedSet<string> _copies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ClonedPackage> _built = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Reads mesh packages with <paramref name="readPackage"/> (a package path; null when it cannot be read).</summary>
    public MeshLoot(Func<string, CookedPackage?> readPackage)
        : this(Describer(readPackage ?? throw new ArgumentNullException(nameof(readPackage))), Builder(readPackage))
    {
    }

    /// <summary>
    /// Takes the loot of a mesh object path from <paramref name="describe"/> and builds copies with <paramref name="build"/>
    /// (a mesh package path; throws when it cannot); without a builder every copy is taken as buildable (tests).
    /// </summary>
    internal MeshLoot(Func<string, MeshLootInfo> describe, Func<string, ClonedPackage>? build = null)
    {
        _describe = describe;
        _build = build;
    }

    /// <summary>
    /// The mesh packages of <paramref name="catalog"/> (the game's files, with the mods read over them): the loot is read
    /// from the header and <c>.uexp</c> only, a copy is built from the whole package (its <c>.ubulk</c> too).
    /// </summary>
    public static MeshLoot FromCatalog(AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new MeshLoot(Describer(path => Read(catalog, path, withBulk: false)), Builder(path => Read(catalog, path, withBulk: true)));

        static CookedPackage? Read(AssetCatalog catalog, string path, bool withBulk)
        {
            try
            {
                if (!catalog.TryGetPackageFile(path, out var file))
                {
                    return null;
                }

                if (withBulk)
                {
                    return ModdableAssets.ReadPackage(catalog, path);
                }

                var stem = file.Path.Replace('\\', '/');
                stem = stem[..stem.LastIndexOf('.')];
                return catalog.Provider.Files.TryGetValue(stem + ".uexp", out var uexp) ? CookedPackage.Parse(file.Read(), uexp.Read(), null, PackageMap.Normalize(path)) : null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or FormatException or InvalidDataException or IOException or ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>The loot-free copies asked for (<see cref="LootFreeCopy"/>), as the stock mesh object paths.</summary>
    public IReadOnlyCollection<string> Requested
    {
        get
        {
            lock (_lock)
            {
                return _copies.ToList();
            }
        }
    }

    /// <summary>The loot <paramref name="meshObjectPath"/> carries itself (read once per mesh).</summary>
    public MeshLootInfo Of(string? meshObjectPath)
    {
        if (string.IsNullOrWhiteSpace(meshObjectPath) || IsCopy(meshObjectPath))
        {
            return MeshLootInfo.None;
        }

        lock (_lock)
        {
            if (_known.TryGetValue(meshObjectPath, out var known))
            {
                return known;
            }
        }

        var info = _describe(meshObjectPath);
        lock (_lock)
        {
            _known[meshObjectPath] = info;
        }

        return info;
    }

    /// <summary>The copies built (<see cref="LootFreeCopy"/>), by stock mesh object path, for the export to write.</summary>
    public IReadOnlyDictionary<string, ClonedPackage> Built
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, ClonedPackage>(_built, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>Why a copy could not be built, by stock mesh object path.</summary>
    public string? FailureOf(string meshObjectPath)
    {
        lock (_lock)
        {
            return _failed.GetValueOrDefault(meshObjectPath);
        }
    }

    /// <summary>
    /// The object path of the loot-free copy of <paramref name="meshObjectPath"/>, built now (once per mesh), or null when
    /// it cannot be built (<see cref="FailureOf"/> says why): a level never points at a copy the export would not have.
    /// </summary>
    public string? LootFreeCopy(string meshObjectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meshObjectPath);
        lock (_lock)
        {
            if (_failed.ContainsKey(meshObjectPath))
            {
                return null;
            }

            if (_copies.Contains(meshObjectPath))
            {
                return CopyPath(meshObjectPath);
            }
        }

        ClonedPackage? built = null;
        if (_build is not null)
        {
            try
            {
                built = _build(PackagePathOf(meshObjectPath));
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or FormatException or InvalidDataException or IOException
                                           or ArgumentException or EndOfStreamException or IndexOutOfRangeException or NotSupportedException)
            {
                lock (_lock)
                {
                    _failed[meshObjectPath] = ex.Message;
                }

                return null;
            }
        }

        lock (_lock)
        {
            _copies.Add(meshObjectPath);
            if (built is not null)
            {
                _built[meshObjectPath] = built;
            }
        }

        return CopyPath(meshObjectPath);
    }

    /// <summary>
    /// What one mesh component is to be written as for the loot <paramref name="wanted"/>, given the loot of its mesh
    /// asset: the presets of its own entries, and the mesh to draw (null = keep the one it has). Wanted loot that the mesh
    /// asset gives already, exactly, needs no entry and no copy; otherwise the component gets the wanted entries and, when
    /// the mesh carries examine data, the loot-free copy. <c>NoCopy</c> is true when that copy could not be built (the
    /// mesh's own loot then stays).
    /// </summary>
    public (IReadOnlyList<string> Entries, string? Mesh, bool NoCopy) Settle(string? meshObjectPath, IReadOnlyList<string> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var mesh = Of(meshObjectPath);
        if (!mesh.HasExamineData)
        {
            return (wanted, null, false);
        }

        if (mesh.Complete && wanted.Count > 0 && wanted.SequenceEqual(mesh.Presets, StringComparer.OrdinalIgnoreCase))
        {
            return ([], null, false);
        }

        var copy = LootFreeCopy(meshObjectPath!);
        return (wanted, copy, copy is null);
    }

    /// <summary><c>/Game/A/SM_X.SM_X</c> to <c>/Game/A/SM_X_NoLoot.SM_X_NoLoot</c> (a package path gives a package path).</summary>
    public static string CopyPath(string meshObjectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meshObjectPath);
        var package = PackagePathOf(meshObjectPath);
        var copy = package + CopySuffix;
        return meshObjectPath.Length == package.Length ? copy : copy + "." + PackageMap.Leaf(copy);
    }

    /// <summary>True for the path of a loot-free copy.</summary>
    public static bool IsCopy(string path) =>
        PackagePathOf(path).EndsWith(CopySuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stock mesh a loot-free copy was made of (<see cref="CopyPath"/> backwards); any other path as it is.</summary>
    public static string StockPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!IsCopy(path))
        {
            return path;
        }

        var package = PackagePathOf(path);
        var stock = package[..^CopySuffix.Length];
        return path.Length == package.Length ? stock : stock + "." + PackageMap.Leaf(stock);
    }

    /// <summary>The loot a mesh package carries itself (see <see cref="MeshLootInfo"/>).</summary>
    public static MeshLootInfo Read(CookedPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var (_, _, _, examine) = UserData(package);
        if (examine.Count == 0)
        {
            return MeshLootInfo.None;
        }

        var presets = new List<string>();
        foreach (var entry in examine)
        {
            if (package.ReadProperties(entry - 1).Find("SpawnerPreset")?.Value is StructValue preset
                && preset.Find("Preset")?.Value is ObjectValue { Index: not 0 } reference)
            {
                presets.Add(package.GetFullPath(reference.Index));
            }
        }

        return new MeshLootInfo(true, presets, presets.Count == examine.Count);
    }

    /// <summary>
    /// Builds the loot-free copy of one mesh package: a rename-copy under <see cref="CopyPath"/> (the cloner keeps every
    /// payload byte for byte) whose mesh lists no examine entry in its <c>AssetUserData</c> any more (rest, perch and quest
    /// data stay), the examine objects themselves left in the package empty (no preset). The mesh data after its properties
    /// is checked to be the stock mesh's, byte for byte.
    /// </summary>
    /// <param name="stock">The stock mesh package.</param>
    /// <param name="stockPackagePath">Its package path.</param>
    /// <exception cref="InvalidOperationException">The package is not a static mesh with examine data, or the copy does not check out.</exception>
    public static ClonedPackage BuildCopy(CookedPackage stock, string stockPackagePath)
    {
        ArgumentNullException.ThrowIfNull(stock);
        var stockPath = PackageMap.Normalize(stockPackagePath);
        var (stockMesh, _, _, stockExamine) = UserData(stock);
        if (stockMesh < 0 || stockExamine.Count == 0)
        {
            throw new InvalidOperationException($"{stockPath} is not a static mesh with examine data of its own.");
        }

        var copyPath = CopyPath(stockPath);
        var cloned = PackageCloner.Clone(stock, stockPath, new PackageMap([new KeyValuePair<string, string>(stockPath, copyPath)]));
        var package = CookedPackage.Parse(cloned.Bytes.UAsset, cloned.Bytes.UExp, cloned.UBulk, copyPath);
        var (mesh, tag, items, examine) = UserData(package);
        if (mesh < 0 || tag is null)
        {
            throw new InvalidOperationException($"{copyPath}: the copy's mesh or its {UserDataProperty} was not found.");
        }

        var input = PackageWriter.ToBuildInput(package);
        var data = input.ExportData.ToList();
        data[mesh] = LevelPackageEditor.WriteObjectArray(package.GetExportBytes(mesh), tag, items.Where(i => !examine.Contains(i)).ToList());
        foreach (var entry in examine)
        {
            // Its properties go (the terminating None stays), the rest of the object's data stays.
            var payload = package.GetExportBytes(entry - 1);
            var end = package.ReadProperties(entry - 1).EndOffset;
            data[entry - 1] = payload[(end - 8)..];
        }

        var bytes = PackageWriter.Build(input with { ExportData = data });
        var built = CookedPackage.Parse(bytes.UAsset, bytes.UExp, cloned.UBulk, copyPath);
        var (builtMesh, _, _, builtExamine) = UserData(built);
        if (builtMesh < 0 || builtExamine.Count > 0 || Read(built).Presets.Count > 0)
        {
            throw new InvalidOperationException($"{copyPath}: the copy still lists examine data.");
        }

        if (!NativeData(built, builtMesh).SequenceEqual(NativeData(stock, stockMesh)))
        {
            throw new InvalidOperationException($"{copyPath}: the copy's mesh data differs from {stockPath}'s.");
        }

        return cloned with { Bytes = bytes };
    }

    /// <summary>
    /// The package's static mesh export (0-based, -1 when none), its <c>AssetUserData</c> tag, the entries listed (package
    /// indices) and those of them that are examine data.
    /// </summary>
    private static (int Mesh, PropertyTag? Tag, List<int> Items, List<int> Examine) UserData(CookedPackage package)
    {
        var leaf = package.BasePath is { } basePath ? PackageMap.Leaf(PackageMap.Normalize(basePath)) : null;
        var mesh = -1;
        for (var i = 0; i < package.Exports.Count; i++)
        {
            if (package.GetExportClassName(i) == "StaticMesh"
                && (mesh < 0 || string.Equals(package.ResolveName(package.Exports[i].ObjectName), leaf, StringComparison.OrdinalIgnoreCase)))
            {
                mesh = i;
            }
        }

        if (mesh < 0 || package.ReadProperties(mesh).Find(UserDataProperty) is not { Value: ArrayValue list } tag)
        {
            return (mesh, null, [], []);
        }

        var items = list.Items.OfType<ObjectValue>().Select(o => o.Index).ToList();
        var examine = items.Where(i => i > 0 && i <= package.Exports.Count
                                       && package.GetExportClassName(i - 1).EndsWith(ExamineClassSuffix, StringComparison.Ordinal)).ToList();
        return (mesh, tag, items, examine);
    }

    private static Func<string, ClonedPackage> Builder(Func<string, CookedPackage?> read) => package =>
        BuildCopy(read(package) ?? throw new FileNotFoundException($"{package} could not be read.", package), package);

    private static Func<string, MeshLootInfo> Describer(Func<string, CookedPackage?> read) => mesh =>
    {
        try
        {
            return read(PackagePathOf(mesh)) is { } package ? Read(package) : MeshLootInfo.None;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException or ArgumentOutOfRangeException or NotSupportedException or IndexOutOfRangeException)
        {
            return MeshLootInfo.None;
        }
    };

    private static byte[] NativeData(CookedPackage package, int export)
    {
        var payload = package.GetExportBytes(export);
        return payload[package.ReadProperties(export).EndOffset..];
    }

    private static string PackagePathOf(string path)
    {
        var p = path.Replace('\\', '/');
        var dot = p.IndexOf('.', p.LastIndexOf('/') + 1);
        return dot > 0 ? p[..dot] : p;
    }
}
