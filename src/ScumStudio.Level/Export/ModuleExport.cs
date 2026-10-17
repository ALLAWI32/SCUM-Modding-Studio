using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Formats.AssetRegistry;
using ScumStudio.Level.Editing;
using ScumStudio.Level.Projects;
using ScumStudio.Modding;
using ScumStudio.Modding.Catalog;
using ScumStudio.Modding.Cloning;
using ScumStudio.Modding.Registry;
using ScumStudio.Modding.Tuning;
using ScumStudio.Pak.Reading;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Level.Export;

/// <summary>A page of the studio whose changes can be exported as a mod of their own (owner 2026-10-17).</summary>
public enum ExportModule
{
    /// <summary>The Map page: levels, terrain holes, placed models, traders, imported mods.</summary>
    Map,

    /// <summary>The Spawns page: the island's spawn places and the spawn settings it edits.</summary>
    Spawns,

    /// <summary>The Vehicles page: vehicle clones, values, parts, textures.</summary>
    Vehicles,

    /// <summary>The Weapons page: weapons, magazines, ammunition, projectiles.</summary>
    Weapons,

    /// <summary>The Items page: every other inventory item.</summary>
    Items,

    /// <summary>The Craftables page (its own <see cref="CraftablesExporter"/> pak).</summary>
    Craftables,

    /// <summary>The Economy page: <c>EconomyOverride.json</c>, a server file, not a pak.</summary>
    Economy,
}

/// <summary>
/// One page's changes as a mod of its own: <c>pakchunk&lt;N+k&gt;-&lt;Name&gt;-&lt;Page&gt;_P.pak</c> (client and server, each with
/// a copied stock <c>.sig</c>), plus the Registry pak <c>pakchunk&lt;N+19&gt;-&lt;Name&gt;-Registry_P.pak</c>.
/// <para>
/// The game reads ONE <c>AssetRegistry.bin</c>, the highest pak's. Each page's pak carries the game's registry plus its own
/// new records, so it works alone. The Registry pak loads above all of them and carries the game's records plus the new
/// records of every page pak of the project found in the same output folder (read back from those paks): with several page
/// paks installed, it keeps each one's clones registered. It is written again after every page export.
/// </para>
/// </summary>
public static class ModuleExport
{
    /// <summary>Appended to the mod name for the Registry pak.</summary>
    public const string RegistryName = "Registry";

    /// <summary>Staging folder of the Registry pak inside the role folder.</summary>
    public const string RegistryStagingFolder = "staging-registry";

    /// <summary>Pages with a pak, lowest first (the order they load in).</summary>
    public static IReadOnlyList<ExportModule> PakModules { get; } =
        [ExportModule.Spawns, ExportModule.Map, ExportModule.Vehicles, ExportModule.Weapons, ExportModule.Items, ExportModule.Craftables];

    /// <summary>
    /// The pak chunk of a page's pak above the project's chunk <paramref name="baseChunk"/> (the whole project's pak is N, its
    /// Craftables pak N+1). Map loads above Spawns: when objects placed on the map clear spawn places, the Map pak's copy of the
    /// island's spawn places holds the Spawns page's edits too.
    /// </summary>
    public static int ChunkOf(ExportModule module, int baseChunk) => baseChunk + module switch
    {
        ExportModule.Spawns => 10,
        ExportModule.Map => 11,
        ExportModule.Vehicles => 12,
        ExportModule.Weapons => 13,
        ExportModule.Items => 14,
        ExportModule.Craftables => 15,
        _ => 16,
    };

    /// <summary>The Registry pak's chunk: above every page's pak.</summary>
    public static int RegistryChunk(int baseChunk) => baseChunk + 19;

    /// <summary>The page pak's mod name, e.g. <c>MyMod-Vehicles</c>.</summary>
    public static string ModName(string modName, ExportModule module) => ProjectExporter.SanitizeModName(modName) + "-" + module;

    /// <summary>The page pak's file name, e.g. <c>pakchunk912-MyMod-Vehicles_P.pak</c>.</summary>
    public static string PakFileName(string modName, ExportModule module, int baseChunk) =>
        ProjectExporter.PakFileName(ModName(modName, module), ChunkOf(module, baseChunk));

    /// <summary>The Registry pak's file name, e.g. <c>pakchunk919-MyMod-Registry_P.pak</c>.</summary>
    public static string RegistryPakFileName(string modName, int baseChunk) =>
        ProjectExporter.PakFileName(ProjectExporter.SanitizeModName(modName) + "-" + RegistryName, RegistryChunk(baseChunk));

    /// <summary>The page pak's staging folder inside the role folder (never the whole project's).</summary>
    public static string StagingFolder(ExportModule module) => "staging-" + module.ToString().ToLowerInvariant() + "-mod";

    /// <summary>The page pak's report inside the role folder.</summary>
    public static string ReportFileName(ExportModule module) => "export-report-" + module.ToString().ToLowerInvariant() + ".md";

    /// <summary>
    /// The page a vehicle/item package belongs to: a clone's packages go with its kind (a zone copy that a placed object on
    /// the map uses goes with the Map, like the NPC guards that made it), else the package's folder (vehicles, weapons and
    /// ammunition, items, spawner presets and zones), else the Map (ground looks, trees, guards).
    /// </summary>
    public static ExportModule OfAsset(EditState state, string package)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(package);
        if (state.FindCloneOf(package) is { } clone)
        {
            switch (clone.Kind.ToLowerInvariant())
            {
                case "vehicle":
                case "vehiclepart":
                    return ExportModule.Vehicles;
                case "weapon":
                case "magazine":
                case "ammo":
                case "projectile":
                    return ExportModule.Weapons;
                case "item":
                    return ExportModule.Items;
                case "zone":
                    return state.SpawnChoices.Any(c => !Spawns.SpawnPlaces.IsStaticData(c.Actor.Level) && c.Value.Contains(clone.NewPrimary, StringComparison.OrdinalIgnoreCase))
                        ? ExportModule.Map
                        : ExportModule.Spawns;
            }

            package = clone.Template; // another kind: where its template lives
        }

        var leaf = package[(package.LastIndexOf('/') + 1)..];
        var asset = ModdableAssets.Classify(package)
            ?? (leaf.EndsWith("_ES", StringComparison.OrdinalIgnoreCase) ? ModdableAssets.Classify(package[..^3]) : null);
        if (asset is not null)
        {
            return asset.Kind switch
            {
                ModdableKind.Vehicle => ExportModule.Vehicles,
                ModdableKind.Item => ExportModule.Items,
                _ => ExportModule.Weapons,
            };
        }

        // Stock packages outside the folder rules that one page edits (review 3.5.0: they went to the Map's or the Vehicles'
        // pak, and that page's own export said it had nothing): melee damage, animals, spawn presets, a stock vehicle's paint.
        var path = package.Replace('\\', '/');
        if (string.Equals(PackageMap.Normalize(path), DataTableEdits.WeaponDescTable, StringComparison.OrdinalIgnoreCase))
        {
            return ExportModule.Weapons;
        }

        if (path.Contains("/ConZ_Files/Hunting/BiomeData/", StringComparison.OrdinalIgnoreCase))
        {
            return ExportModule.Spawns;
        }

        if (path.Contains(VehicleParts.PresetFolder, StringComparison.OrdinalIgnoreCase))
        {
            // The Spawns page sets a preset's fuel, battery and part chances; the Vehicles page its default parts. One pak
            // ships the package with every edit of it: the Vehicles page's when it changed the parts.
            return state.AssetValueOverrides.Any(v => VehicleParts.IsEdit(v.Path) && string.Equals(PackageMap.Normalize(v.Package), PackageMap.Normalize(path), StringComparison.OrdinalIgnoreCase))
                ? ExportModule.Vehicles
                : ExportModule.Spawns;
        }

        if (path.Contains("/ConZ_Files/Vehicles/", StringComparison.OrdinalIgnoreCase) || path.Contains("/ConZ_Files/Models/Vehicles2/", StringComparison.OrdinalIgnoreCase))
        {
            return ExportModule.Vehicles;
        }

        if (path.Contains("/Items/Weapons/", StringComparison.OrdinalIgnoreCase) || path.Contains("/Items/Ammunition/", StringComparison.OrdinalIgnoreCase))
        {
            return ExportModule.Weapons;
        }

        if (path.Contains("/Items/SpawnerPresets", StringComparison.OrdinalIgnoreCase) || path.Contains("/Encounters/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/Spawn", StringComparison.OrdinalIgnoreCase))
        {
            return ExportModule.Spawns;
        }

        return path.Contains("/ConZ_Files/Items/", StringComparison.OrdinalIgnoreCase) ? ExportModule.Items : ExportModule.Map;
    }

    /// <summary>
    /// The page one value override belongs to: a row of the traders' <c>Table_TradeableDesc</c> goes with the page of the thing
    /// the row sells (every page sets prices there), any other value with its package (<see cref="OfAsset"/>).
    /// </summary>
    public static ExportModule OfValue(EditState state, AssetValueOverride value)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(value);
        return IsTradeTable(value.Package) && TradeRow(value.Path) is { } row ? OfTradeRow(state, row) : OfAsset(state, value.Package);
    }

    /// <summary>True for the traders' table, which several page paks write (<see cref="WriteRegistryPakAsync"/> merges it).</summary>
    public static bool IsTradeTable(string package) => string.Equals(PackageMap.Normalize(package), DataTableEdits.TradeableTable, StringComparison.OrdinalIgnoreCase);

    /// <summary>The row of a <c>Rows[&lt;row&gt;].…</c> path, or null.</summary>
    private static string? TradeRow(string path)
    {
        if (!path.StartsWith("Rows[", StringComparison.Ordinal))
        {
            return null;
        }

        var end = path.IndexOf(']', 5);
        return end > 5 ? path[5..end] : null;
    }

    /// <summary>The page of what a trade row (a class name, <c>BPC_Rager_C</c>) sells: a clone's own page, else by the game's name rules.</summary>
    private static ExportModule OfTradeRow(EditState state, string row)
    {
        var leaf = row.EndsWith("_C", StringComparison.OrdinalIgnoreCase) ? row[..^2] : row;
        if (state.AssetClones.FirstOrDefault(c => string.Equals(PackageMap.Leaf(c.NewPrimary), leaf, StringComparison.OrdinalIgnoreCase)) is { } clone)
        {
            return OfAsset(state, clone.NewPrimary);
        }

        string[] weapons = ["Weapon_", "Magazine_", "Cal_", "BP_WeaponBullet_", "1H_", "2H_"];
        return leaf.StartsWith("BPC_", StringComparison.OrdinalIgnoreCase) ? ExportModule.Vehicles
            : weapons.Any(p => leaf.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ? ExportModule.Weapons
            : ExportModule.Items;
    }

    /// <summary>
    /// The part of <paramref name="state"/> a page's pak exports: Map = every level but the island's spawn places (kept for the
    /// spawn places its placed objects clear, see <see cref="ChunkOf"/>), the terrain holes and the Map's asset edits; Spawns =
    /// the island's spawn places and its asset edits; Vehicles, Weapons, Items = their asset edits. Craftables and Economy have
    /// no part of it (their own files).
    /// </summary>
    public static EditState StateOf(EditState state, ExportModule module)
    {
        ArgumentNullException.ThrowIfNull(state);
        return module switch
        {
            ExportModule.Map => state.Where(_ => true, p => OfAsset(state, p) == ExportModule.Map, holes: true, v => OfValue(state, v) == ExportModule.Map),
            ExportModule.Spawns => state.Where(Spawns.SpawnPlaces.IsStaticData, p => OfAsset(state, p) == ExportModule.Spawns, holes: false, v => OfValue(state, v) == ExportModule.Spawns),
            ExportModule.Vehicles or ExportModule.Weapons or ExportModule.Items =>
                state.Where(_ => false, p => OfAsset(state, p) == module, holes: false, v => OfValue(state, v) == module),
            _ => new EditState(),
        };
    }

    /// <summary>
    /// True when the page has something to export in <paramref name="project"/> (Craftables and Economy: their files exist).
    /// The Map's state keeps the spawn places only for the objects it places, so spawn edits alone are not the Map's.
    /// </summary>
    public static bool HasChanges(Project project, ExportModule module)
    {
        ArgumentNullException.ThrowIfNull(project);
        return module switch
        {
            ExportModule.Craftables => File.Exists(Path.Combine(project.DirectoryPath, Modding.Crafting.CraftablesFile.FileName)),
            ExportModule.Economy => File.Exists(Path.Combine(project.DirectoryPath, Economy.EconomyOverride.FileName)),
            ExportModule.Map => !StateOf(project.State, module).Where(l => !Spawns.SpawnPlaces.IsStaticData(l), _ => true, holes: true).IsEmpty,
            _ => !StateOf(project.State, module).IsEmpty,
        };
    }

    /// <summary>
    /// The packages the other pages' paks of <paramref name="state"/> create (their clones), mapped to those paks' file
    /// names: what <paramref name="module"/>'s pak may refer to (<see cref="ExportOptions.Elsewhere"/>).
    /// </summary>
    public static Func<string, string?> Elsewhere(EditState state, ExportModule module, string modName, int baseChunk)
    {
        ArgumentNullException.ThrowIfNull(state);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clone in state.AssetClones)
        {
            var owner = OfAsset(state, clone.NewPrimary);
            if (owner == module)
            {
                continue;
            }

            foreach (var pair in clone.Packages)
            {
                map[pair.New] = PakFileName(modName, owner, baseChunk);
            }
        }

        return package => map.GetValueOrDefault(package);
    }

    /// <summary>The packages under <paramref name="staging"/> (another pak of the same export) mapped to <paramref name="pakName"/>.</summary>
    public static Func<string, string?> ShippedBy(string staging, string projectName, string pakName)
    {
        var shipped = PackagesIn(staging, projectName);
        return package => shipped.Contains(package) ? pakName : null;
    }

    /// <summary>The install rule of a page's pak, for its report and the app.</summary>
    public static string InstallNote(ProjectSourceRole role, string pakName, string registryPakName, IReadOnlyList<string> needs)
    {
        ArgumentNullException.ThrowIfNull(needs);
        var side = role == ProjectSourceRole.Server
            ? $"This is the SERVER pak {pakName}, one page of the project: it goes into the dedicated server's mods folder, and every player needs the CLIENT pak of the same name in the game's Paks\\~mods."
            : $"This is the CLIENT pak {pakName}, one page of the project: every player needs it in the game's Paks\\~mods, and a server needs the SERVER pak of the same name in its mods folder.";
        var registry = registryPakName.Length == 0 ? string.Empty
            : $" Copy {registryPakName} the same way (client and server) whenever you install two or more page paks of this project: the game reads only one list of assets (AssetRegistry.bin, the highest pak's), and that pak's list holds the new assets of every page pak in this folder.";
        var needed = needs.Count == 0 ? string.Empty : " It uses what " + string.Join(", ", needs) + " adds: install that pak too.";
        return side + registry + needed + " Do not install the whole-project pak of the same project with its page paks (they would hold the same changes twice). Restart the game and the server after copying.";
    }

    /// <summary>
    /// Writes the Registry pak of <paramref name="modName"/> for one cook: the game's registry (<paramref name="catalog"/>'s stock
    /// one) plus the new records of every page pak of the project in the role folder, read back from those paks. With two or
    /// more page paks there shipping the traders' <c>Table_TradeableDesc</c> (each with its own clones' rows and prices) and
    /// the project's <paramref name="state"/>, it also carries that table with the rows and prices of all of them (review
    /// 3.5.0: the highest page pak's copy hid the other pages' clones from the traders). Returns null (after removing an old
    /// Registry pak) when there is neither. The package check runs on it like on every pak.
    /// </summary>
    public static async Task<ExportResult?> WriteRegistryPakAsync(
        string modName, AssetCatalog catalog, ExportOptions options, ProjectSourceRole role, ILogger? logger = null, CancellationToken cancellationToken = default,
        EditState? state = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        logger ??= NullLogger.Instance;
        var roleDirectory = Path.Combine(Path.GetFullPath(options.OutputDirectory), ProjectExporter.RoleFolder(role));
        var pakPath = Path.Combine(roleDirectory, RegistryPakFileName(modName, options.PakChunkIndex));
        var warnings = new List<string>(options.Warnings);
        var sources = new List<(string Source, AssetRegistryFile Registry)>();
        var shippedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var present = new HashSet<ExportModule>();
        var tablePaks = 0;
        foreach (var module in PakModules)
        {
            var modulePak = Path.Combine(roleDirectory, PakFileName(modName, module, options.PakChunkIndex));
            if (!File.Exists(modulePak))
            {
                continue;
            }

            present.Add(module);
            var (packages, registry) = ReadPak(modulePak, catalog.ProjectName);
            tablePaks += packages.Contains(DataTableEdits.TradeableTable) ? 1 : 0;
            foreach (var package in packages)
            {
                shippedBy[package] = Path.GetFileName(modulePak);
            }

            if (registry is not null)
            {
                sources.Add((Path.GetFileName(modulePak), registry));
            }
        }

        var tables = state is not null && tablePaks >= 2 ? TradeTable(state, catalog, present, shippedBy, warnings) : [];
        var staging = Path.Combine(roleDirectory, RegistryStagingFolder);
        if (sources.Count == 0 && tables.Count == 0)
        {
            foreach (var file in new[] { pakPath, SigCopier.GetSigPath(pakPath) }.Where(File.Exists))
            {
                File.Delete(file);
                logger.LogInformation("Registry pak: removed {File} (no page pak adds a record).", file);
            }

            return null;
        }

        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        var stock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = new List<RegisteredAsset>();
        if (sources.Count > 0)
        {
            if (RegistryMerge.ReadStock(catalog) is not { } stockBytes)
            {
                throw new InvalidOperationException($"No stock {AssetModBuilder.AssetRegistryPath} in {catalog.DisplayName}: the Registry pak cannot be built.");
            }

            var merged = RegistryMerge.Build(AssetRegistryFile.Parse(stockBytes), sources, shippedBy.ContainsKey, warnings);
            var target = Path.Combine([staging, .. AssetModBuilder.AssetRegistryPath.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, merged.Save(), cancellationToken).ConfigureAwait(false);
            stock.UnionWith(AssetRegistryFile.Parse(stockBytes).Assets.Select(a => a.ObjectPath));
            added.AddRange(merged.Assets.Where(a => !stock.Contains(a.ObjectPath)).Select(a => new RegisteredAsset(a.ObjectPath, a.AssetClass)));
        }

        var assets = new List<ExportedAsset>();
        foreach (var table in tables)
        {
            var virtualPath = catalog.TryGetPackageFile(table.PackagePath, out var existing) ? existing.Path.Replace('\\', '/') : table.HeaderFilePath(catalog.ProjectName);
            var dot = virtualPath.LastIndexOf('.');
            var file = Path.Combine([staging, .. virtualPath[..dot].Split('/', StringSplitOptions.RemoveEmptyEntries)]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await table.Bytes.WriteAsync(file, virtualPath[dot..], cancellationToken).ConfigureAwait(false);
            if (table.UBulk is not null)
            {
                await File.WriteAllBytesAsync(file + ".ubulk", table.UBulk, cancellationToken).ConfigureAwait(false);
            }

            assets.Add(new ExportedAsset(table.PackagePath, virtualPath, false));
        }

        var validation = ProjectExporter.Validate(staging, catalog, options with { Elsewhere = p => shippedBy.GetValueOrDefault(p) }, null, cancellationToken);
        string? sigPath = null;
        if (options.WritePak)
        {
            var writer = new PakWriter(new PakWriterOptions { Compression = options.Compression, IncludeAssetRegistry = sources.Count > 0 }, logger);
            await writer.WriteFromDirectoryAsync(staging, pakPath, null, cancellationToken).ConfigureAwait(false);
            if ((options.StockSigPath ?? ProjectExporter.FindStockSig(catalog)) is { } sigSource)
            {
                sigPath = SigCopier.Copy(sigSource, pakPath, overwrite: true, logger);
            }
            else
            {
                warnings.Add($"No stock .sig found: copy {SigCopier.DefaultStockSigName} from the game's Paks folder to {SigCopier.GetSigPath(pakPath)}.");
            }
        }

        logger.LogInformation("Registry pak: {Records} new record(s) of {Paks} page pak(s), {Tables} shared table(s).", added.Count, sources.Count, tables.Count);
        return new ExportResult
        {
            Role = role,
            ModName = ProjectExporter.SanitizeModName(modName) + "-" + RegistryName,
            StagingDirectory = staging,
            PakPath = options.WritePak ? pakPath : null,
            SigPath = sigPath,
            Levels = [],
            Assets = assets,
            Registered = added,
            Warnings = warnings,
            Validation = validation,
        };
    }

    /// <summary>
    /// The traders' table with the rows of every clone the page paks in the folder ship (<paramref name="shippedBy"/>) and the
    /// prices set on the rows of the pages whose pak is there (<paramref name="present"/>), built from the game's table.
    /// </summary>
    private static List<BuiltAssetPackage> TradeTable(EditState state, AssetCatalog catalog, IReadOnlySet<ExportModule> present,
        IReadOnlyDictionary<string, string> shippedBy, List<string> warnings)
    {
        var sold = state.AssetClones
            .Where(c => shippedBy.ContainsKey(PackageMap.Normalize(c.NewPrimary)) && present.Contains(OfAsset(state, c.NewPrimary)))
            .OrderBy(c => c.NewPrimary, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ClonePlan(c.Template, c.NewPrimary, c.Packages.Select(p => new KeyValuePair<string, string>(p.Old, p.New)).ToList()))
            .ToList();
        var prices = state.AssetValueOverrides
            .Where(v => IsTradeTable(v.Package) && present.Contains(OfValue(state, v)))
            .Select(v => new TunableEdit(v.Export, v.Path, v.Current))
            .ToList();
        var edits = new Dictionary<string, IReadOnlyList<TunableEdit>>(StringComparer.OrdinalIgnoreCase);
        if (prices.Count > 0)
        {
            edits[DataTableEdits.TradeableTable] = prices;
        }

        var built = AssetModBuilder.Build(catalog, new AssetModRequest([], edits) { SoldElsewhere = sold, WriteRegistry = false });
        warnings.AddRange(built.Warnings);
        return [.. built.Packages.Where(p => IsTradeTable(p.PackagePath))];
    }

    /// <summary>
    /// The page paks in <paramref name="roleDirectory"/> that load above <paramref name="module"/>'s pak and ship some of the same
    /// packages: a pak ships a package whole and the game takes the highest pak's copy, so this page's changes to them do not
    /// show while that pak is installed too (review 3.5.0: the Craftables pak's copy of a gun given a tag hid the Weapons page's
    /// weight, the Map pak's island spawn places hid newer Spawns edits). Exporting that page again builds on what is there
    /// now. The traders' table is left out: the Registry pak carries it merged. Empty when this page's pak is not there.
    /// </summary>
    public static IReadOnlyList<(ExportModule Above, string AbovePak, int Count, string Example)> HiddenBy(
        string roleDirectory, string modName, ExportModule module, string projectName, int baseChunk = ProjectExporter.DefaultPakChunkIndex)
    {
        var own = Path.Combine(roleDirectory, PakFileName(modName, module, baseChunk));
        if (!PakModules.Contains(module) || !File.Exists(own))
        {
            return [];
        }

        var mine = ReadPak(own, projectName).Packages;
        var hidden = new List<(ExportModule, string, int, string)>();
        foreach (var other in PakModules.Where(m => ChunkOf(m, baseChunk) > ChunkOf(module, baseChunk)))
        {
            var pak = Path.Combine(roleDirectory, PakFileName(modName, other, baseChunk));
            if (!File.Exists(pak))
            {
                continue;
            }

            var shared = ReadPak(pak, projectName).Packages.Where(p => mine.Contains(p) && !IsTradeTable(p)).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (shared.Count > 0)
            {
                hidden.Add((other, Path.GetFileName(pak), shared.Count, shared[0]));
            }
        }

        return hidden;
    }

    /// <summary>The packages a pak holds and its <c>AssetRegistry.bin</c> (null: none), read with no key (the studio's paks are not encrypted).</summary>
    public static (IReadOnlySet<string> Packages, AssetRegistryFile? Registry) ReadPak(string pakFile, string projectName)
    {
        using var source = PakFileSource.OpenFile(pakFile, new PakFileSourceOptions { ProjectName = projectName });
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AssetRegistryFile? registry = null;
        foreach (var path in source.EnumerateFiles())
        {
            if (AssetPaths.IsPackageFile(path))
            {
                packages.Add(AssetPaths.ToPackagePath(path, projectName));
            }
            else if (path.EndsWith("AssetRegistry.bin", StringComparison.OrdinalIgnoreCase) && source.TryGetBytes(path, out var bytes))
            {
                registry = AssetRegistryFile.Parse(bytes);
            }
        }

        return (packages, registry);
    }

    private static HashSet<string> PackagesIn(string staging, string projectName) =>
        Directory.Exists(staging)
            ? Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories).Where(AssetPaths.IsPackageFile)
                .Select(f => AssetPaths.ToPackagePath(Path.GetRelativePath(staging, f).Replace('\\', '/'), projectName))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
