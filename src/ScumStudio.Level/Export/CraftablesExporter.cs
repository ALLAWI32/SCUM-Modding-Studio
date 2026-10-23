using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.Assets.Catalog;
using ScumStudio.Assets.Textures;
using ScumStudio.Formats.Packages;
using ScumStudio.Level.Import;
using ScumStudio.Level.Projects;
using ScumStudio.Modding;
using ScumStudio.Modding.Crafting;
using ScumStudio.Pak.Writing;

namespace ScumStudio.Level.Export;

/// <summary>
/// The Craftables mod: the project's craftables (<see cref="CraftablesFile"/>) built by <see cref="CraftablesPlanner"/> and
/// <see cref="AssetModBuilder"/> into a pak of their own, <c>pakchunk&lt;N+1&gt;-&lt;Name&gt;Craftables_P.pak</c> next to the
/// project's pak, with a copied stock <c>.sig</c>. It holds only new packages. Its <c>AssetRegistry.bin</c> is the project
/// pak's (when that one wrote one, else the game's stock one) plus the craftables' records: the highest pak's registry is
/// the one the game reads, so it must keep the project's vehicles and items too.
/// </summary>
public static partial class CraftablesExporter
{
    /// <summary>Appended to the mod name for the pak's name.</summary>
    public const string Suffix = "Craftables";

    /// <summary>Staging folder of the craftables pak inside the role folder.</summary>
    public const string StagingFolderName = "staging-craftables";

    /// <summary>
    /// Packages the game already has that the Craftables pak may still ship (P0 #1: it only adds). Reviewed for 3.1.1 and
    /// empty: every craftable is new packages, and anything else at a game path stops the export.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedStockPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds and packs <paramref name="craftables"/> for one cook. <paramref name="baseRegistry"/> is the project pak's
    /// registry of the same export (null: the game's stock one). Returns null when the list is empty, after deleting the
    /// Craftables pak an earlier export left in the role folder (<see cref="RemoveStale"/>).
    /// <para>
    /// Its report (review 3.5.1: it wrote none, so a craftables-only folder had no install note): the Craftables page's pak
    /// writes <c>export-report-craftables.md</c> like every page pak; beside the project's pak of the same export it adds its
    /// section to that pak's <c>export-report.md</c>; <paramref name="alone"/> (the project has no pak of its own in this
    /// export: no map, vehicle or item edits) it writes <c>export-report.md</c>, over an earlier refusal's, and names an
    /// earlier export's project pak still in the folder (never deleted here).
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No craftable was built (the reasons are listed; the previous pak stays, since removing it would take craftables
    /// players have placed out of the game), or the pak would ship a package the game already has. Both are found before
    /// anything is written, and <see cref="Check"/> finds them before the project's pak is.
    /// </exception>
    public static async Task<ExportResult?> ExportAsync(
        CraftablesFile craftables, string modName, AssetCatalog catalog, ExportOptions options, ProjectSourceRole role,
        byte[]? baseRegistry = null, ILogger? logger = null, CancellationToken cancellationToken = default, bool alone = false)
    {
        ArgumentNullException.ThrowIfNull(craftables);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        logger ??= NullLogger.Instance;
        var module = options.Module == ExportModule.Craftables; // the Craftables page's own mod (ModuleExport)
        var name = module ? ModuleExport.ModName(modName, ExportModule.Craftables) : ProjectExporter.SanitizeModName(modName) + Suffix;
        var roleDirectory = ProjectExporter.ExportFolder(options.OutputDirectory, modName, module, role);
        if (craftables.IsEmpty)
        {
            // The project's pak of this export left the old layout's Craftables pak for this one to replace (RemoveOldLayout).
            var oldLayout = !module && options.WritePak ? Path.Combine(Path.GetFullPath(options.OutputDirectory), ProjectExporter.RoleFolder(role)) : null;
            foreach (var removed in (module ? RemoveStaleModule(roleDirectory, modName, options.PakChunkIndex) : RemoveStale(roleDirectory, modName, options.PakChunkIndex))
                         .Concat(oldLayout is not null && Directory.Exists(oldLayout) ? RemoveStale(oldLayout, modName, options.PakChunkIndex) : []))
            {
                logger.LogInformation("Craftables: removed {File} (the project has no craftables now).", removed);
            }

            return null;
        }

        // A stock package the plan overrides (an item's setup given a new tag) is built on the project pak's staged copy, so
        // that pak's edits of it (Module page) are not hidden by this higher pak; only while that pak is there.
        // As the Craftables page's own pak (review 3.5.0) it loads above the other page paks: it builds on the staged copy of the
        // highest of them that ships the package (the Weapons page's weight of the gun given a tag), only while that pak is there.
        var work = ProjectExporter.WorkFolder(roleDirectory);
        var projectStaging = Path.Combine(work, ProjectExporter.StagingFolderName);
        // Alone, the project pak in the folder is an earlier export's (named in the report), never built on.
        var projectPak = !module && !alone && (!options.WritePak || File.Exists(Path.Combine(roleDirectory, ProjectExporter.PakFileName(modName, options.PakChunkIndex))));
        var below = module
            ? ModuleExport.PakModules.Where(m => m != ExportModule.Craftables && (!options.WritePak || File.Exists(Path.Combine(roleDirectory, ModuleExport.PakFileName(modName, m, options.PakChunkIndex)))))
                .OrderByDescending(m => ModuleExport.ChunkOf(m, options.PakChunkIndex)).Select(m => Path.Combine(work, ModuleExport.StagingFolder(m))).ToList()
            : projectPak ? [projectStaging] : [];
        Func<string, CookedPackage?> basePackage = p => catalog.TryGetPackageFile(p, out var f) ? below.Select(s => StagedPackage(s, f.Path, p)).FirstOrDefault(s => s is not null) : null;
        var (plan, built, imports, importedAssets, warnings) = Prepare(craftables, catalog, baseRegistry, writeRegistry: true, basePackage, options.ModelPictures ?? NoDrawing);
        var staging = Path.Combine(work, module ? ModuleExport.StagingFolder(ExportModule.Craftables) : StagingFolderName);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        var assets = new List<ExportedAsset>(importedAssets);
        foreach (var folder in imports)
        {
            UeCook.CopyTree(folder, staging);
        }

        foreach (var package in built.Packages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var virtualPath = catalog.TryGetPackageFile(package.PackagePath, out var existing)
                ? existing.Path.Replace('\\', '/')
                : package.HeaderFilePath(catalog.ProjectName);
            var dot = virtualPath.LastIndexOf('.');
            var target = Path.Combine([staging, .. virtualPath[..dot].Split('/', StringSplitOptions.RemoveEmptyEntries)]);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await package.Bytes.WriteAsync(target, virtualPath[dot..], cancellationToken).ConfigureAwait(false);
            if (package.UBulk is not null)
            {
                await File.WriteAllBytesAsync(target + ".ubulk", package.UBulk, cancellationToken).ConfigureAwait(false);
            }

            assets.Add(new ExportedAsset(package.PackagePath, virtualPath, package.IsClone));
        }

        if (built.AssetRegistry is not null)
        {
            var registry = Path.Combine([staging, .. AssetModBuilder.AssetRegistryPath.Split('/')]);
            Directory.CreateDirectory(Path.GetDirectoryName(registry)!);
            await File.WriteAllBytesAsync(registry, built.AssetRegistry, cancellationToken).ConfigureAwait(false);
        }

        // The server cook's sound events (an imported model's, a clone's) become the server's 12-byte stubs, like the project pak's.
        var stubbed = await ProjectExporter.StubEventsAsync(staging, catalog, options, role, warnings, cancellationToken).ConfigureAwait(false);

        // Refused, it says so in its own report (the project's pak above may be written by now) and the earlier Craftables pak
        // stays as it is, like every pak a refused export finds.
        var pakTarget = Path.Combine(roleDirectory, module ? ModuleExport.PakFileName(modName, ExportModule.Craftables, options.PakChunkIndex) : PakFileName(modName, options.PakChunkIndex));
        var refusalReport = Path.Combine(roleDirectory, ModuleExport.ReportFileName(ExportModule.Craftables));
        ValidationResult? validation;
        try
        {
            validation = ProjectExporter.Validate(staging, catalog, options, null, cancellationToken, listDirectory: roleDirectory);
        }
        catch (ModValidationException refused)
        {
            throw ProjectExporter.WriteRefusal(refused, name, role, refusalReport, options.WritePak ? pakTarget : null, warnings);
        }

        ProjectExporter.ClearRefusal(refusalReport);
        string? pakPath = null;
        string? sigPath = null;
        if (options.WritePak)
        {
            pakPath = pakTarget;
            var writer = new PakWriter(new PakWriterOptions { Compression = options.Compression, IncludeAssetRegistry = built.AssetRegistry is not null }, logger);
            await writer.WriteFromDirectoryAsync(staging, pakPath, null, cancellationToken).ConfigureAwait(false);
            var sigSource = options.StockSigPath ?? ProjectExporter.FindStockSig(catalog);
            if (sigSource is null)
            {
                warnings.Add($"No stock .sig found: copy {SigCopier.DefaultStockSigName} from the game's Paks folder to {SigCopier.GetSigPath(pakPath)}.");
            }
            else
            {
                sigPath = SigCopier.Copy(sigSource, pakPath, overwrite: true, logger);
            }

            // The pak has them now: their asset names and folders stay (the game saves what players build by them).
            if (craftables.ProjectDirectory is { } project)
            {
                try
                {
                    var marked = CraftablesFile.MarkExported(project, plan.Entries.Select(e => e.Token).OfType<string>());
                    if (marked.Count > 0)
                    {
                        logger.LogInformation("Craftables: {Count} craftable(s) exported for the first time; their asset names and folders are fixed now.", marked.Count);
                    }
                }
                catch (IOException ex)
                {
                    warnings.Add($"craftables.json could not record which craftables were exported ({ex.Message}); keep their asset names and folders as they are.");
                }
            }
        }

        logger.LogInformation("Craftables: {Count} craftable(s), {Recipes} game recipe(s) with new amounts, {Packages} package(s), {Registered} registry record(s).",
            plan.Entries.Count, plan.Request.RecipeAmounts.Count, assets.Count, built.Registered.Count);

        // Alone (no map, vehicle or item edits now), the project's pak an earlier export left beside it is not this export's:
        // the Craftables pak's list of assets no longer holds its records. It stays (a pair with its server pak), named.
        var earlier = Path.Combine(roleDirectory, ProjectExporter.PakFileName(modName, options.PakChunkIndex));
        if (!module && alone && File.Exists(earlier))
        {
            warnings.Add($"{Path.GetFileName(earlier)} in this folder is from an EARLIER export (written {File.GetLastWriteTime(earlier).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)}): "
                + "this project has no map, vehicle or item edits now, so this export did not build it, and the Craftables pak's list of assets (the one the game reads) does not hold the new vehicles or items it may add. "
                + "Delete it and its .sig, or add those edits again and export.");
        }

        var result = new ExportResult
        {
            Role = role,
            ModName = name,
            StagingDirectory = staging,
            PakPath = pakPath,
            SigPath = sigPath,
            Levels = [],
            Assets = assets,
            Registered = built.Registered,
            AssetValues = built.Applied,
            Warnings = warnings,
            StubbedEvents = stubbed,
            Validation = validation,
        };
        var reportPath = WriteReport(result, catalog, roleDirectory, modName, options.PakChunkIndex, module, alone, logger);

        // What an export before 3.5.1 left loose beside the mod's folder goes once this pak is written (a staging-only run keeps it).
        foreach (var old in options.WritePak ? ProjectExporter.RemoveOldLayout(options.OutputDirectory, modName, options.PakChunkIndex, role) : [])
        {
            logger.LogInformation("Removed {Path} (an export before 3.5.1 left it loose in the output folder).", old);
        }

        return result with { ReportPath = reportPath };
    }

    /// <summary>
    /// The report of a written Craftables pak (see <see cref="ExportAsync"/>): its own file for the Craftables page's pak, a section
    /// of the project pak's report beside it, or the folder's <c>export-report.md</c> when it is the only pak of the export.
    /// Null when it cannot be written (the pak is written: that stays a success, with a log line).
    /// </summary>
    private static string? WriteReport(ExportResult result, AssetCatalog catalog, string roleDirectory, string modName, int pakChunkIndex, bool module, bool alone, ILogger logger)
    {
        var utf8 = new System.Text.UTF8Encoding(false);
        var path = Path.Combine(roleDirectory, module ? ModuleExport.ReportFileName(ExportModule.Craftables) : ProjectExporter.ReportFileName);
        try
        {
            if (module)
            {
                File.WriteAllText(path, ProjectExporter.BuildReport(result, catalog, ExportModule.Craftables, ModuleExport.RegistryPakFileName(modName, pakChunkIndex), craftables: true), utf8);
            }
            else if (!alone && File.Exists(path) && !ProjectExporter.IsRefusalReport(path))
            {
                File.AppendAllText(path, CraftablesSection(result), utf8);
            }
            else
            {
                File.WriteAllText(path, ProjectExporter.BuildReport(result, catalog, craftables: true), utf8);
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Craftables: the report {Path} could not be written ({Message}); the pak is written.", path, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The install line of a Craftables pak that replaces the game's entity setups of items given a new ingredient tag (an
    /// item's own, or a project group's): the game accepts those items only through this copy (r40, redriver6155: "only
    /// predefined ingredients will work"), so it names them and what hides them. Null when the pak replaces none.
    /// </summary>
    public static string? TaggedSetupsNote(ExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var setups = result.Assets.Where(a => !a.IsClone && a.PackagePath.EndsWith("_ES", StringComparison.OrdinalIgnoreCase))
            .Select(a => Modding.Cloning.PackageMap.Leaf(a.PackagePath)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return setups.Count == 0 ? null
            : $"New ingredient tags: this pak also replaces the game's entity setups of {setups.Count} item(s), {string.Join(", ", setups)}, so that they count as the new tags "
              + "(an item's own tag or a group's). The game accepts these items as ingredients only through this pak: every player needs it, a server needs the SERVER pak "
              + "of the same export (it runs the crafting), and another mod that changes the same items and loads before this pak takes the tags away (then only the "
              + "game's own ingredients work). Export again after every game update.";
    }

    /// <summary>The Craftables pak's section of the project pak's report (the same export, the same folder).</summary>
    private static string CraftablesSection(ExportResult result)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Craftables pak");
        sb.AppendLine();
        sb.Append("- Files: ").AppendLine(result.PakPath is null ? "(no pak written: staged files only)"
            : Path.GetFileName(result.PakPath) + (result.SigPath is null ? " (copy a stock .sig next to it)" : " + " + Path.GetFileName(result.SigPath)) + ", installed with the project's pak above");
        if (TaggedSetupsNote(result) is { } tagged)
        {
            sb.Append("- ").AppendLine(tagged);
        }
        sb.Append("- Packages: ").Append(result.Assets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Append(", registry records added: ").AppendLine(result.Registered.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sb.Append("- Work files (hidden): ").AppendLine(result.StagingDirectory);
        foreach (var asset in result.Assets)
        {
            sb.Append("- ").Append(asset.IsClone ? "New: " : "Overridden: ").AppendLine(asset.VirtualPath);
        }

        if (result.Warnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Craftables warnings");
            foreach (var warning in result.Warnings)
            {
                sb.Append("- ").AppendLine(warning);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Plans and builds <paramref name="craftables"/> without writing anything, and stops as <see cref="ExportAsync"/> would.
    /// Call it for each cook before the project's pak is written: a Craftables pak refused after it would leave the project's
    /// pak newer than the Craftables pak, whose registry is the one the game reads.
    /// </summary>
    /// <exception cref="InvalidOperationException">No craftable would be built, or the pak would ship a package the game already has.</exception>
    public static void Check(CraftablesFile craftables, AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(craftables);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!craftables.IsEmpty)
        {
            _ = Prepare(craftables, catalog, null, writeRegistry: false);
        }
    }

    private sealed record Prepared(CraftablesPlan Plan, AssetModBuildResult Built, IReadOnlyList<string> Imports, IReadOnlyList<ExportedAsset> ImportedAssets, List<string> Warnings);

    // The craftables whose imported model is usable, planned and built; stops before anything is written when nothing would
    // be built or the pak would ship a package the game already has (P0 #1: the Craftables pak only adds).
    private static Prepared Prepare(CraftablesFile craftables, AssetCatalog catalog, byte[]? baseRegistry, bool writeRegistry, Func<string, CookedPackage?>? basePackage = null,
        Func<CraftPictureRequest, TextureImage?>? draw = null)
    {
        var warnings = new List<string>();
        var items = new List<Craftable>();
        var importedFolders = new Dictionary<Craftable, string>(ReferenceEqualityComparer.Instance);
        foreach (var craftable in craftables.Items)
        {
            if (craftable.Imported is not null)
            {
                if (ImportedFolder(craftables.ProjectDirectory, craftable.Imported, out var problem) is not { } folder)
                {
                    warnings.Add($"{craftable.Name}: left out (its imported model {craftable.Imported} {problem}).");
                    continue;
                }

                importedFolders[craftable] = folder;
            }

            items.Add(craftable);
        }

        var plan = CraftablesPlanner.PlanWithStockRecipes(catalog, items, craftables.RecipeOverrides, craftables.ProjectDirectory, craftables.Groups);
        if (draw is not null)
        {
            plan = MakePictures(plan, catalog, craftables.ProjectDirectory, draw, warnings); // a check (no writing) draws nothing
        }

        if (basePackage is not null)
        {
            plan = plan with { Request = plan.Request with { BasePackage = basePackage } };
        }

        warnings.AddRange(plan.Warnings);
        var built = AssetModBuilder.Build(catalog, plan.Request with { BaseRegistry = baseRegistry, SellClones = false, WriteRegistry = writeRegistry });
        warnings.AddRange(built.Warnings);

        // Models imported from 3D files (UeCook) of the planned craftables: their cooked mesh, materials and textures ship
        // at their own paths (a craftable left out ships nothing).
        var planned = plan.Entries.Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var imports = items.Where(c => planned.Contains(c.Name) && importedFolders.ContainsKey(c)).Select(c => importedFolders[c]).ToList();
        var importedAssets = imports
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(AssetPaths.IsPackageFile)
                .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/')))
            .Select(virtualPath => new ExportedAsset(AssetPaths.ToPackagePath(virtualPath, catalog.ProjectName), virtualPath, IsClone: false))
            .ToList();

        // A package the game already has (a stock table, a stock recipe) stops it here, and the previous pak stays as it was;
        // only the item setups the user opted in to give a new tag and the game's recipes whose amounts per skill level the
        // user overrides (plan.StockOverrides) may be shipped at their stock path.
        var stockOverrides = plan.StockOverrides.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var overridden in plan.StockOverrides)
        {
            warnings.Add(plan.Request.RecipeAmounts.ContainsKey(overridden)
                ? $"The Craftables pak replaces the game's recipe {overridden} (your amounts per skill level): another mod that changes the same recipe conflicts with it (the pak loaded last wins); export again after every game update."
                : $"The Craftables pak replaces the game's {overridden} (an item given a new ingredient tag: its own, or a group's of the project): export again after every game update.");
        }

        var atGamePaths = GamePathsIn(catalog, built.Packages.Select(p => p.PackagePath).Where(p => !stockOverrides.Contains(p)).Concat(importedAssets.Select(a => a.PackagePath)));
        if (atGamePaths.Count > 0)
        {
            throw new InvalidOperationException("The Craftables pak would change packages the game already has, so it was not written: " + string.Join(", ", atGamePaths) + ".");
        }

        if (built.Packages.Count == 0 && importedAssets.Count == 0)
        {
            throw new InvalidOperationException("No craftable was built. " + string.Join(" ", warnings));
        }

        return new Prepared(plan, built, imports, importedAssets, warnings);
    }

    /// <summary>The packages of <paramref name="packagePaths"/> that <paramref name="catalog"/> already has and <see cref="AllowedStockPaths"/> does not list.</summary>
    public static IReadOnlyList<string> GamePathsIn(AssetCatalog catalog, IEnumerable<string> packagePaths)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(packagePaths);
        return packagePaths.Where(p => !AllowedStockPaths.Contains(p) && catalog.PackageExists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The folder of an imported model (N064: craftables.json can be edited by hand): <paramref name="imported"/> must be
    /// exactly <c>imports/&lt;token&gt;</c> inside the project folder, and every file in it must lie in the token's own
    /// <c>SCUM/Content/ScumStudio/Imports/&lt;token&gt;/</c>, so it cannot ship anything at a game path. Null, with
    /// <paramref name="problem"/> set, otherwise.
    /// </summary>
    public static string? ImportedFolder(string? projectDirectory, string imported, out string problem)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var parts = imported.Split('/', '\\');
        if (projectDirectory is null || parts.Length != 2 || !string.Equals(parts[0], UeCook.ImportsFolder, StringComparison.OrdinalIgnoreCase)
            || parts[1].Length == 0 || parts[1] is "." or ".." || parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            problem = $"is not {UeCook.ImportsFolder}/<name> in the project folder";
            return null;
        }

        var root = Path.GetFullPath(Path.Combine(projectDirectory, UeCook.ImportsFolder));
        var folder = Path.GetFullPath(Path.Combine(root, parts[1]));
        if (!string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(folder))
        {
            problem = "is not in the project folder";
            return null;
        }

        var own = string.Join('/', "SCUM", "Content", UeCook.ImportsRoot["/Game/".Length..], parts[1]) + "/";
        var outside = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .FirstOrDefault(f => !f.StartsWith(own, StringComparison.OrdinalIgnoreCase));
        problem = outside is null ? string.Empty : $"holds {outside}, outside {own}";
        return outside is null ? folder : null;
    }

    /// <summary>File name of the Craftables pak of <paramref name="modName"/> next to the project's pak of chunk <paramref name="pakChunkIndex"/>.</summary>
    public static string PakFileName(string modName, int pakChunkIndex) =>
        ProjectExporter.PakFileName(ProjectExporter.SanitizeModName(modName) + Suffix, pakChunkIndex + 1);

    /// <summary>
    /// Deletes the Craftables pak (and its .sig) of <paramref name="modName"/> from <paramref name="directory"/>: left from an
    /// export with craftables, the game would still load its recipes, and as the highest pak its old registry would hide the
    /// project pak's new records. An earlier refusal's NOT EXPORTED report of that pak goes too (review 3.5.1: it would name a
    /// pak that is gone and that is not exported any more). Returns the deleted paks and signatures.
    /// </summary>
    public static IReadOnlyList<string> RemoveStale(string directory, string modName, int pakChunkIndex)
    {
        var pak = Path.Combine(directory, PakFileName(modName, pakChunkIndex));
        var removed = new[] { pak, SigCopier.GetSigPath(pak) }.Where(File.Exists).ToList();
        removed.ForEach(File.Delete);
        ProjectExporter.ClearRefusal(Path.Combine(directory, ModuleExport.ReportFileName(ExportModule.Craftables)));
        return removed;
    }

    /// <summary>
    /// Deletes the Craftables page's own pak (and its .sig) of <paramref name="modName"/> from <paramref name="directory"/>
    /// (<see cref="ModuleExport"/>), and an earlier refusal's NOT EXPORTED report of it.
    /// </summary>
    public static IReadOnlyList<string> RemoveStaleModule(string directory, string modName, int pakChunkIndex)
    {
        var pak = Path.Combine(directory, ModuleExport.PakFileName(modName, ExportModule.Craftables, pakChunkIndex));
        var removed = new[] { pak, SigCopier.GetSigPath(pak) }.Where(File.Exists).ToList();
        removed.ForEach(File.Delete);
        ProjectExporter.ClearRefusal(Path.Combine(directory, ModuleExport.ReportFileName(ExportModule.Craftables)));
        return removed;
    }

    /// <summary>
    /// The packages the project's own pak of the same export (<paramref name="main"/>) ships, named by that pak
    /// (<see cref="ExportOptions.Elsewhere"/>): the Craftables pak's registry keeps that pak's records (its clones), and the
    /// package check must find their packages there, not report them missing.
    /// </summary>
    public static Func<string, string?> ProjectPakPackages(ExportResult main, AssetCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(catalog);
        return ModuleExport.ShippedBy(main.StagingDirectory, catalog.ProjectName, Path.GetFileName(main.PakPath) ?? main.ModName);
    }

    /// <summary>The registry the project's own pak staged in <paramref name="result"/>, or null.</summary>
    public static byte[]? StagedRegistry(ExportResult? result)
    {
        var path = result is null ? null : Path.Combine([result.StagingDirectory, .. AssetModBuilder.AssetRegistryPath.Split('/')]);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>The package <paramref name="staging"/> holds at <paramref name="virtualPath"/> (<c>SCUM/Content/…/X.uasset</c>), or null.</summary>
    private static CookedPackage? StagedPackage(string staging, string virtualPath, string packagePath)
    {
        var header = Path.Combine([staging, .. virtualPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        var stem = header[..^Path.GetExtension(header).Length];
        return File.Exists(header) && File.Exists(stem + ".uexp")
            ? CookedPackage.Parse(File.ReadAllBytes(header), File.ReadAllBytes(stem + ".uexp"), File.Exists(stem + ".ubulk") ? File.ReadAllBytes(stem + ".ubulk") : null, packagePath)
            : null;
    }
}
