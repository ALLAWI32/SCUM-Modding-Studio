using Microsoft.Extensions.Logging;
using ScumStudio.Assets.Catalog;
using ScumStudio.Core.Abstractions;
using ScumStudio.Core.Games;
using ScumStudio.Level.Economy;
using ScumStudio.Level.Export;
using ScumStudio.Level.Projects;
using ScumStudio.Modding.Crafting;
using ScumStudio.Viewport;

namespace ScumStudio.App.Services;

/// <summary>What the Export card asks for.</summary>
/// <param name="Project">The open project.</param>
/// <param name="OutputFolder">Folder that receives <c>Client/</c> (and <c>Server/</c>).</param>
/// <param name="ModName">Pak name (default: the project name).</param>
/// <param name="IncludeServer">Also build the server pak from the configured server Paks folder.</param>
/// <param name="PakChunkIndex">The <c>pakchunkNNN</c> number.</param>
public sealed record ModExportRequest(
    Project Project,
    string OutputFolder,
    string? ModName,
    bool IncludeServer,
    int PakChunkIndex = ProjectExporter.DefaultPakChunkIndex)
{
    /// <summary>Also build the Craftables pak (<see cref="CraftablesExporter"/>) when the project has craftables.</summary>
    public bool IncludeCraftables { get; init; } = true;
}

/// <summary>
/// Runs <see cref="ProjectExporter"/> for the app: the client pak against the connected workspace catalog and, on request,
/// the server pak against the server Paks folder from the settings (opened with the stored key, never shown).
/// </summary>
public static class ModExportService
{
    /// <summary>Why <see cref="ExportAsync"/> cannot run right now, or null when it can.</summary>
    public static string? Blocker(AppServices services, Project? project)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (project is null)
        {
            return Localization.Loc.T("Export.OpenProjectFirst");
        }

        // A craftables file, even an emptied one, is something to export: the export removes the old Craftables pak. So is
        // an economy file on its own (audit F279: an economy-only project could not be exported).
        var economyOnly = project.State.IsEmpty && !File.Exists(Path.Combine(project.DirectoryPath, CraftablesFile.FileName));
        if (economyOnly && !File.Exists(Path.Combine(project.DirectoryPath, EconomyOverride.FileName)))
        {
            return Localization.Loc.T("Export.NothingYet");
        }

        if (services.Workspace.Catalog is null && !economyOnly)
        {
            return Localization.Loc.T("Export.ConnectFirst");
        }

        return null;
    }

    /// <summary>
    /// Exports the client pak and optionally the server pak; returns one result per cook. With nothing to build (a
    /// craftables-only project whose craftables were all removed, or left out by the unticked box) it only removes the
    /// Craftables pak an earlier export left in the output folder and returns no result.
    /// </summary>
    /// <exception cref="InvalidOperationException">No catalog, no edits, or the server folder is missing.</exception>
    public static async Task<IReadOnlyList<ExportResult>> ExportAsync(
        AppServices services, ModExportRequest request, IProgressSink progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        if (Blocker(services, request.Project) is { } blocker)
        {
            throw new InvalidOperationException(blocker);
        }

        // The client's game files were opened before this export's mod name was known: they leave out the same own paks
        // as the server's (re-opened only when that name changes which paks are read). They read the imported mods this
        // export carries (the report "ServerMods": game files that had not read them held a mod's BP_Prisoner to the game's).
        var ownNames = OwnPaks.ProjectNames(request.Project.Manifest.Name, request.ModName);
        await services.Workspace.UseModsAsync(ProjectMods.Folders(request.Project.DirectoryPath), ownNames, progress, cancellationToken).ConfigureAwait(false);
        var catalog = services.Workspace.Catalog!;
        var craftables = request.IncludeCraftables ? CraftablesFile.Load(request.Project.DirectoryPath) : new CraftablesFile();
        var modName = request.ModName ?? request.Project.Manifest.Name;
        if (request.Project.State.IsEmpty && craftables.IsEmpty)
        {
            var removed = new[] { ProjectSourceRole.Client, ProjectSourceRole.Server }
                .SelectMany(role => CraftablesExporter.RemoveStale(Path.Combine(Path.GetFullPath(request.OutputFolder), ProjectExporter.RoleFolder(role)), modName, request.PakChunkIndex))
                .ToList();
            services.Logger.LogInformation("Nothing to build for {Name}; removed {Count} old Craftables file(s).", modName, removed.Count);
            if (ExportEconomyOnly(services, request, out var unreadable) is { } written)
            {
                services.Notifications.Success(Localization.Loc.T("Economy.Exported"), Localization.Loc.F("Economy.ExportedDetail", written));
                return [];
            }

            if (unreadable is not null)
            {
                services.Notifications.Error(Localization.Loc.T("Economy.NotExported"), unreadable);
                return [];
            }

            services.Notifications.Info(Localization.Loc.T("Projects.Exported"), removed.Count > 0
                ? Localization.Loc.F("Export.CraftablesRemoved", string.Join(", ", removed.Select(Path.GetFileName)))
                : Localization.Loc.T("Export.NothingYet"));
            return [];
        }

        // Collision check: pieces the game's logs say got no collision since the last export are exported straight.
        var (straight, found) = CollisionDoctor.Check(request.Project, CollisionDoctor.GameLogs(services.Settings.Load().ServerPaksFolder));
        if (found.Count > 0)
        {
            services.Notifications.Warning(Localization.Loc.T("Collision.Title"),
                Localization.Loc.F("Collision.Replaced", found.Count, string.Join(", ", found.Take(6).Select(a => a.Actor))));
        }

        var options = new ExportOptions
        {
            OutputDirectory = request.OutputFolder,
            ModName = request.ModName,
            PakChunkIndex = request.PakChunkIndex,
            StraightPieces = straight,

            // Bent pieces' collision is built from the client's meshes for both cooks (the server cook has no geometry).
            BendMeshes = new BendSupport(catalog).Describe,

            // Imported mods go into both paks (the server needs a modded map's levels too).
            Mods = ProjectMods.Folders(request.Project.DirectoryPath),
        };

        // The economy page's file (placed traders' sections, the hidden ones left out), as the page and MCP write it; a
        // project without one exports none (the server keeps its own).
        if (ReferenceEquals(services.Projects.Current, request.Project) && File.Exists(Path.Combine(request.Project.DirectoryPath, EconomyOverride.FileName)))
        {
            // A file the store could not read holds the game's defaults: never written as the server's economy (review, economy).
            options = services.Economy.LoadError is { } error
                ? options with { LeaveOutEconomy = true, Warnings = [.. options.Warnings, ProjectExporter.EconomyUnreadable(error)] }
                : options with { Economy = services.Economy.ForExport() };
        }
        var exporter = new ProjectExporter(services.Logger);
        var results = new List<ExportResult>(4);

        // A craftable without a picture of its own gets one drawn from its 3D model (the client's meshes; the server cook
        // reuses the same pictures).
        using var pictures = new CraftIconRenderer(catalog, services.Logger);
        options = options with { ModelPictures = pictures.Render };

        // The project's pak (levels, vehicles, items), then the Craftables pak on top of its registry.
        async Task ExportRole(AssetCatalog source, ProjectSourceRole role, ExportOptions roleOptions)
        {
            ExportResult? main = null;
            if (!request.Project.State.IsEmpty)
            {
                main = await exporter.ExportAsync(request.Project, source, roleOptions, role, new StepProgress(progress), cancellationToken).ConfigureAwait(false);
                results.Add(main);
            }

            // Without craftables (none, or unticked) this removes the Craftables pak an earlier export left there.
            if (!craftables.IsEmpty)
            {
                progress.Report(Localization.Loc.T("Export.Craftables"), 0, 0);
            }

            // Its registry keeps the project pak's records: the package check finds their packages in that pak.
            var craftOptions = main is null ? roleOptions : roleOptions with { Elsewhere = CraftablesExporter.ProjectPakPackages(main, source) };
            if (await CraftablesExporter.ExportAsync(craftables, modName, source, craftOptions, role, CraftablesExporter.StagedRegistry(main),
                    services.Logger, cancellationToken).ConfigureAwait(false) is { } crafted)
            {
                results.Add(crafted);
            }
        }

        progress.Report(Localization.Loc.T("Export.ClientPak"), 0, 0);
        await ExportRole(catalog, ProjectSourceRole.Client, options).ConfigureAwait(false);
        CollisionDoctor.Exported(request.Project);
        if (results[0].SolidPieces > 0)
        {
            services.Notifications.Info(Localization.Loc.T("Collision.Title"), Localization.Loc.F("Collision.Solid", results[0].SolidPieces));
        }

        if (request.IncludeServer)
        {
            await ExportServerAsync(services, options, ownNames, progress, ExportRole, results, ProjectExporter.StagingFolderName, catalog).ConfigureAwait(false);
        }

        // A server pak an earlier export left beside this new client pak no longer matches it (D12, D13): it goes.
        var stale = RemoveStaleServerPaks(request.OutputFolder, modName, request.PakChunkIndex, results);
        if (stale.Count > 0)
        {
            services.Logger.LogInformation("Removed {Count} old server file(s): {Files}", stale.Count, string.Join(", ", stale));
            services.Notifications.Warning(Localization.Loc.T("Export.ServerSkipped"), Localization.Loc.F("Export.StaleServerPak", string.Join(", ", stale.Select(Path.GetFileName))));
        }

        foreach (var problem in ParityProblems(results))
        {
            services.Notifications.Warning(Localization.Loc.T("Export.ParityTitle"), problem);
        }

        // An imported mod's own references to what this game no longer has (report "ServerMods": a BP_Prisoner from before
        // a game update): the pak is written, the mod is named.
        foreach (var note in results.SelectMany(r => r.Validation?.Notes ?? []).Distinct(StringComparer.Ordinal).Take(4))
        {
            services.Notifications.Warning(Localization.Loc.T("Export.ModOutdated"), note);
        }

        if (results.Sum(r => r.RemovedItemSpawners.Count) is > 0 and var gone)
        {
            services.Notifications.Warning(Localization.Loc.T("Map.ItemSpawner.Title"), Localization.Loc.F("Export.ItemSpawnersRemoved", gone,
                string.Join(", ", results.Where(r => r.Role == ProjectSourceRole.Client).SelectMany(r => r.RemovedItemSpawners).Take(4))));
        }

        services.Logger.LogInformation("Exported {Name}: {Count} pak(s) to {Folder}.", results[0].ModName, results.Count, request.OutputFolder);
        return results;
    }

    /// <summary>
    /// The server cook of an export: opens the server's game paks from the settings (with the stored key, never shown) and runs
    /// <paramref name="exportRole"/> on them. The client paks are written by now: a server problem is a warning, never a
    /// failure. The cooks of the pak staged in <paramref name="mainStaging"/> are compared (levels, clones). The server's game files
    /// read <paramref name="overlays"/> (null: the mods the pak carries), as the client's read the project's imported mods.
    /// </summary>
    private static async Task ExportServerAsync(AppServices services, ExportOptions options, IReadOnlyList<string> ownNames, IProgressSink progress,
        Func<AssetCatalog, ProjectSourceRole, ExportOptions, Task> exportRole, List<ExportResult> results, string mainStaging, AssetCatalog clientCatalog,
        IReadOnlyList<string>? overlays = null)
    {
        var configured = services.Settings.Load().ServerPaksFolder;
        var serverFolder = ResolveServerPaks(configured);
        if (serverFolder is null)
        {
            services.Notifications.Warning(Localization.Loc.T("Export.ServerSkipped"), string.IsNullOrWhiteSpace(configured)
                ? Localization.Loc.T("Export.NoServerFolder")
                : Localization.Loc.F("Export.ServerNoCook", configured));
            return;
        }

        if (!string.Equals(Path.GetFullPath(serverFolder), Path.GetFullPath(configured!), StringComparison.OrdinalIgnoreCase))
        {
            // Settings pointed at the server's mods folder: use the game paks next to it from now on, and keep the
            // mods folder as the place the server pak is copied to.
            services.UpdateSettings(s => s with
            {
                ServerPaksFolder = serverFolder,
                ServerModsOutputFolder = string.IsNullOrWhiteSpace(s.ServerModsOutputFolder) ? configured : s.ServerModsOutputFolder,
            });
            services.Notifications.Info(Localization.Loc.T("Export.ServerFolderFixed"), Localization.Loc.F("Export.ServerFolderFixedDetail", serverFolder, configured));
        }

        try
        {
            progress.Report(Localization.Loc.T("Export.OpeningServer"), 0, 0);
            services.Keys.TryGet(out var key);
            using var server = AssetCatalog.OpenPaks(serverFolder, new AssetCatalogOptions
            {
                AesKey = key, Logger = services.Logger, LooseOverlays = overlays ?? options.Mods,
                PakFileFilter = OwnPaks.Filter(ownNames),
            });
            progress.Report(Localization.Loc.T("Export.ServerPak"), 0, 0);

            // Sound events of imported mods and clones become the stock server's 12-byte stubs (D12), read from the
            // server's own game paks, never the mods over them.
            var stubKey = key;
            var serverOptions = options with { ServerEventStubs = () => ServerEventStubs.FromServerPaks(serverFolder, stubKey, services.Logger), ClientCatalog = clientCatalog };
            await exportRole(server, ProjectSourceRole.Server, serverOptions).ConfigureAwait(false);

            // The two cooks must change the same levels the same way (and carry the same clones): what one side
            // lacks is listed with the server's warnings.
            var mains = results.Where(r => string.Equals(Path.GetFileName(r.StagingDirectory), mainStaging, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mains.FirstOrDefault(r => r.Role == ProjectSourceRole.Client) is { } clientMain && mains.FirstOrDefault(r => r.Role == ProjectSourceRole.Server) is { } serverMain
                && ModPackageValidator.CompareCooks(clientMain, serverMain) is { Count: > 0 } parity)
            {
                results[results.IndexOf(serverMain)] = serverMain with { Warnings = [.. serverMain.Warnings, .. parity] };
            }
        }
        catch (ServerEventStubException ex)
        {
            services.Logger.LogWarning("The server pak was refused: {Message}", ex.Message);
            services.Notifications.Warning(Localization.Loc.T("Export.ServerSkipped"),
                Localization.Loc.F("Export.ServerNoStub", ex.Events, ex.FirstEvent ?? string.Empty, ex.Reason));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException)
        {
            services.Logger.LogWarning("The server pak was not built: {Message}", ex.Message);
            services.Notifications.Warning(Localization.Loc.T("Export.ServerSkipped"), ex.Message);
        }
    }

    /// <summary>Why the page <paramref name="module"/> cannot be exported as its own mod right now, or null when it can.</summary>
    public static string? ModuleBlocker(AppServices services, Project? project, ExportModule module)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (project is null)
        {
            return Localization.Loc.T("Export.OpenProjectFirst");
        }

        if (!ModuleExport.HasChanges(project, module))
        {
            return Localization.Loc.F("Export.Module.Nothing", ModuleLabel(module));
        }

        return module != ExportModule.Economy && services.Workspace.Catalog is null ? Localization.Loc.T("Export.ConnectFirst") : null;
    }

    /// <summary>The page's name in the UI language.</summary>
    public static string ModuleLabel(ExportModule module) => Localization.Loc.T(module switch
    {
        ExportModule.Map => "Export.Module.Map",
        ExportModule.Spawns => "Export.Module.Spawns",
        ExportModule.Vehicles => "Export.Module.Vehicles",
        ExportModule.Weapons => "Export.Module.Weapons",
        ExportModule.Items => "Export.Module.Items",
        ExportModule.Craftables => "Export.Module.Craftables",
        _ => "Export.Module.Economy",
    });

    /// <summary>
    /// Exports one page's changes as a mod of its own (<see cref="ModuleExport"/>): its pak for the client and, when the server
    /// Paks folder holds the stock server cook, for the server (each with a copied stock .sig), then the Registry pak of every
    /// page pak in the output folder. The Economy page writes its <c>EconomyOverride.json</c> (a server file, no pak). Every
    /// pak passes the package check first. Returns one result per pak written.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing to export for the page, no catalog, or the package check refused the client pak.</exception>
    public static async Task<IReadOnlyList<ExportResult>> ExportModuleAsync(
        AppServices services, Project project, ExportModule module, string outputFolder, bool includeServer, IProgressSink progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(progress);
        if (ModuleBlocker(services, project, module) is { } blocker)
        {
            throw new InvalidOperationException(blocker);
        }

        if (module == ExportModule.Economy)
        {
            var request = new ModExportRequest(project, outputFolder, null, includeServer);
            if (ExportEconomyOnly(services, request, out var unreadable) is { } written)
            {
                services.Notifications.Success(Localization.Loc.T("Economy.Exported"), Localization.Loc.F("Economy.ExportedDetail", written));
                return [];
            }

            throw new InvalidOperationException(unreadable ?? Localization.Loc.F("Export.Module.Nothing", ModuleLabel(module)));
        }

        var modName = project.Manifest.Name;
        var ownNames = OwnPaks.ProjectNames(modName);
        await services.Workspace.UseModsAsync(services.Workspace.ModFolders, ownNames, progress, cancellationToken).ConfigureAwait(false);
        var catalog = services.Workspace.Catalog!;
        var state = ModuleExport.StateOf(project.State, module);
        var options = new ExportOptions
        {
            OutputDirectory = outputFolder,
            Module = module,
            ProjectDirectory = project.DirectoryPath,
            BendMeshes = new BendSupport(catalog).Describe,
            Mods = module == ExportModule.Map ? ProjectMods.Folders(project.DirectoryPath) : [], // imported mods are the map's
            LeaveOutEconomy = true, // the Economy page exports it
            Elsewhere = ModuleExport.Elsewhere(project.State, module, modName, ProjectExporter.DefaultPakChunkIndex),
        };
        if (module == ExportModule.Map)
        {
            var (straight, _) = CollisionDoctor.Check(project, CollisionDoctor.GameLogs(services.Settings.Load().ServerPaksFolder));
            options = options with { StraightPieces = straight };
        }

        var craftables = module == ExportModule.Craftables ? CraftablesFile.Load(project.DirectoryPath) : new CraftablesFile();
        using var pictures = new CraftIconRenderer(catalog, services.Logger);
        options = options with { ModelPictures = pictures.Render };
        var exporter = new ProjectExporter(services.Logger);
        var results = new List<ExportResult>(4);
        var stale = new List<string>();

        async Task ExportRole(AssetCatalog source, ProjectSourceRole role, ExportOptions roleOptions)
        {
            try
            {
                if (module == ExportModule.Craftables)
                {
                    if (await CraftablesExporter.ExportAsync(craftables, modName, source, roleOptions, role, null, services.Logger, cancellationToken).ConfigureAwait(false) is { } crafted)
                    {
                        results.Add(crafted);
                    }
                }
                else
                {
                    results.Add(await exporter.ExportAsync(state, modName, source, roleOptions, role, new StepProgress(progress), cancellationToken).ConfigureAwait(false));
                }
            }
            catch (Exception ex) when (role == ProjectSourceRole.Server && ex is InvalidOperationException or IOException or InvalidDataException)
            {
                // The client's page pak is new: the server's from an earlier export no longer matches it (D12, D13; review 3.5.0).
                // It goes, and the server's Registry pak is written again without it, before the problem is reported.
                stale.AddRange(RemoveModulePak(Path.Combine(Path.GetFullPath(outputFolder), ProjectExporter.RoleFolder(role)), modName, module));
                if (await ModuleExport.WriteRegistryPakAsync(modName, source, roleOptions, role, services.Logger, cancellationToken, project.State).ConfigureAwait(false) is { } kept)
                {
                    results.Add(kept);
                }

                throw;
            }

            // The game reads one registry: the Registry pak above every page pak in the folder keeps each one's records (and the
            // traders' table with every page pak's rows).
            if (await ModuleExport.WriteRegistryPakAsync(modName, source, roleOptions, role, services.Logger, cancellationToken, project.State).ConfigureAwait(false) is { } registry)
            {
                results.Add(registry);
            }
        }

        progress.Report(Localization.Loc.F("Export.Module.Client", ModuleLabel(module)), 0, 0);
        await ExportRole(catalog, ProjectSourceRole.Client, options).ConfigureAwait(false);
        if (includeServer)
        {
            // The server reads the project's imported mods like the client's game files do (review 3.5.0: a page's edit of a
            // package a mod changes was built on the mod's copy for the client and the game's for the server); only the Map's
            // pak carries them.
            await ExportServerAsync(services, options, ownNames, progress, ExportRole, results, ModuleExport.StagingFolder(module), catalog,
                ProjectMods.Folders(project.DirectoryPath)).ConfigureAwait(false);
        }

        // Without a server cook this time, the server's page pak from an earlier export no longer matches the new client one.
        if (!results.Any(r => r.Role == ProjectSourceRole.Server))
        {
            var serverFolder = Path.Combine(Path.GetFullPath(outputFolder), ProjectExporter.RoleFolder(ProjectSourceRole.Server));
            if (RemoveModulePak(serverFolder, modName, module) is { Count: > 0 } removed)
            {
                stale.AddRange(removed);

                // The server's Registry pak holds that pak's records: it goes too when no other page pak is left beside it.
                if (!ModuleExport.PakModules.Any(m => File.Exists(Path.Combine(serverFolder, ModuleExport.PakFileName(modName, m, ProjectExporter.DefaultPakChunkIndex)))))
                {
                    var registry = Path.Combine(serverFolder, ModuleExport.RegistryPakFileName(modName, ProjectExporter.DefaultPakChunkIndex));
                    foreach (var file in new[] { registry, ScumStudio.Pak.Writing.SigCopier.GetSigPath(registry) }.Where(File.Exists))
                    {
                        File.Delete(file);
                        stale.Add(file);
                    }
                }
            }
        }

        if (stale.Count > 0)
        {
            services.Logger.LogInformation("Removed {Count} old server file(s): {Files}", stale.Count, string.Join(", ", stale));
            services.Notifications.Warning(Localization.Loc.T("Export.ServerSkipped"), Localization.Loc.F("Export.StaleServerPak", string.Join(", ", stale.Select(Path.GetFileName))));
        }

        foreach (var problem in ParityProblems(results))
        {
            services.Notifications.Warning(Localization.Loc.T("Export.ParityTitle"), problem);
        }

        var clientFolder = Path.Combine(Path.GetFullPath(outputFolder), ProjectExporter.ClientFolderName);
        foreach (var (above, abovePak, count, example) in ModuleExport.HiddenBy(clientFolder, modName, module, catalog.ProjectName))
        {
            services.Notifications.Warning(Localization.Loc.T("Export.Module.HiddenTitle"),
                Localization.Loc.F("Export.Module.Hidden", abovePak, count, example, ModuleLabel(above), ModuleExport.PakFileName(modName, module, ProjectExporter.DefaultPakChunkIndex)));
        }

        services.Logger.LogInformation("Exported the {Module} page of {Name}: {Count} pak(s) to {Folder}.", module, modName, results.Count, outputFolder);
        return results;
    }

    /// <summary>Deletes the page's pak (and its .sig) in <paramref name="roleDirectory"/>; returns the deleted files.</summary>
    private static List<string> RemoveModulePak(string roleDirectory, string modName, ExportModule module)
    {
        var pak = Path.Combine(roleDirectory, ModuleExport.PakFileName(modName, module, ProjectExporter.DefaultPakChunkIndex));
        var removed = new List<string>();
        foreach (var file in new[] { pak, ScumStudio.Pak.Writing.SigCopier.GetSigPath(pak) }.Where(File.Exists))
        {
            File.Delete(file);
            removed.Add(file);
        }

        return removed;
    }

    /// <summary>
    /// One click on a page's Export button: <see cref="ExportModuleAsync"/> into the export folder of the Settings (or
    /// <paramref name="defaultFolder"/>), the server paks too when the server Paks folder holds the stock server cook, and the
    /// server paks (pak + sig) copied into the server mods folder of the Settings. Returns the results and the files copied.
    /// </summary>
    public static async Task<(IReadOnlyList<ExportResult> Results, IReadOnlyList<string> Installed)> QuickExportModuleAsync(
        AppServices services, Project project, ExportModule module, string defaultFolder, IProgressSink progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var settings = services.Settings.Load();
        var folder = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.ClientModsOutputFolder) ? defaultFolder : settings.ClientModsOutputFolder);
        var results = await ExportModuleAsync(services, project, module, folder, ResolveServerPaks(settings.ServerPaksFolder) is not null, progress, cancellationToken).ConfigureAwait(false);
        var installed = new List<string>();
        var target = services.Settings.Load().ServerModsOutputFolder;
        foreach (var result in results.Where(r => r.Role == ProjectSourceRole.Server && r.PakPath is not null))
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                break;
            }

            Directory.CreateDirectory(target);
            foreach (var file in new[] { result.PakPath, result.SigPath }.OfType<string>())
            {
                var copy = Path.Combine(target, Path.GetFileName(file));
                if (!string.Equals(Path.GetFullPath(copy), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(file, copy, overwrite: true);
                }

                installed.Add(copy);
            }
        }

        if (installed.Count > 0)
        {
            WarnRemoved(services, RemoveOtherInstallMode(target!, project.Manifest.Name, ProjectExporter.DefaultPakChunkIndex, pagesInstalled: true));
        }

        return (results, installed);
    }

    /// <summary>
    /// Deletes from the server's mods folder <paramref name="folder"/> the paks (and their .sig) of the other way of installing
    /// the same project: the whole project's pak and its Craftables pak once page paks are copied there, the page paks and
    /// the Registry pak once the whole project's paks are. The two must never be installed together (review 3.5.0: the
    /// Registry pak loads above the whole project's paks and its list of assets left out their craftables and clones, and old
    /// page paks hid newer copies). Returns the deleted files.
    /// </summary>
    public static IReadOnlyList<string> RemoveOtherInstallMode(string folder, string modName, int pakChunkIndex, bool pagesInstalled)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        IEnumerable<string> names = pagesInstalled
            ? [ProjectExporter.PakFileName(modName, pakChunkIndex), CraftablesExporter.PakFileName(modName, pakChunkIndex)]
            : [.. ModuleExport.PakModules.Select(m => ModuleExport.PakFileName(modName, m, pakChunkIndex)), ModuleExport.RegistryPakFileName(modName, pakChunkIndex)];
        var removed = new List<string>();
        foreach (var pak in names.Select(n => Path.Combine(folder, n)))
        {
            foreach (var file in new[] { pak, ScumStudio.Pak.Writing.SigCopier.GetSigPath(pak) }.Where(File.Exists))
            {
                File.Delete(file);
                removed.Add(file);
            }
        }

        return removed;
    }

    private static void WarnRemoved(AppServices services, IReadOnlyList<string> removed)
    {
        if (removed.Count > 0)
        {
            services.Logger.LogInformation("Removed {Count} file(s) of the other install mode from the server's mods folder: {Files}", removed.Count, string.Join(", ", removed));
            services.Notifications.Warning(Localization.Loc.T("Export.ParityTitle"), Localization.Loc.F("Export.OtherModeRemoved", string.Join(", ", removed.Select(Path.GetFileName))));
        }
    }

    /// <summary>
    /// An economy-only project (no map edits, no craftables): its <c>EconomyOverride.json</c> written to the output's
    /// <c>Server</c> and <c>Client</c> folders with the note; returns the server copy, or null when the project has no economy
    /// or its file could not be read (<paramref name="unreadable"/> says why; nothing is written then).
    /// </summary>
    private static string? ExportEconomyOnly(AppServices services, ModExportRequest request, out string? unreadable)
    {
        unreadable = null;
        if (!File.Exists(Path.Combine(request.Project.DirectoryPath, EconomyOverride.FileName)))
        {
            return null;
        }

        EconomyOverride? economy;
        if (ReferenceEquals(services.Projects.Current, request.Project))
        {
            economy = services.Economy.LoadError is null ? services.Economy.ForExport() : null;
            unreadable = services.Economy.LoadError is { } error ? ProjectExporter.EconomyUnreadable(error) : null;
        }
        else
        {
            // Not the open project: read as the command line does (a bad file is a warning, parked sections left out).
            (economy, unreadable) = ProjectExporter.LoadEconomy(request.Project);
            economy = unreadable is null ? ProjectExporter.EconomyFor(request.Project.State, economy) : null;
        }

        if (economy is null)
        {
            return null;
        }

        var output = Path.GetFullPath(request.OutputFolder);
        var server = economy.SaveTo(Path.Combine(output, ProjectExporter.ServerFolderName), withNote: true);
        economy.SaveTo(Path.Combine(output, ProjectExporter.ClientFolderName), withNote: true);
        services.Logger.LogInformation("Economy written to {Path} (an economy-only project).", server);
        return server;
    }

    /// <summary>
    /// Deletes the server paks (and their signatures) of <paramref name="modName"/> that an earlier export left in
    /// <c>&lt;root&gt;/Server</c> when this export built no server pak of that kind (<paramref name="results"/>): a server pak
    /// beside a newer client pak no longer matches it. Returns the deleted files.
    /// </summary>
    public static IReadOnlyList<string> RemoveStaleServerPaks(string root, string modName, int pakChunkIndex, IReadOnlyList<ExportResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var server = Path.Combine(Path.GetFullPath(root), ProjectExporter.RoleFolder(ProjectSourceRole.Server));
        if (!Directory.Exists(server) || !results.Any(r => r.Role == ProjectSourceRole.Client))
        {
            return [];
        }

        var removed = new List<string>();
        var craftables = ProjectExporter.SanitizeModName(modName) + CraftablesExporter.Suffix;
        // The main server pak goes only when a new client main pak was built (an old pair left by a craftables-only export stays a pair).
        if (results.Any(r => r.Role == ProjectSourceRole.Client && r.ModName != craftables) && !results.Any(r => r.Role == ProjectSourceRole.Server && r.ModName != craftables))
        {
            var pak = Path.Combine(server, ProjectExporter.PakFileName(modName, pakChunkIndex));
            foreach (var file in new[] { pak, ScumStudio.Pak.Writing.SigCopier.GetSigPath(pak) }.Where(File.Exists))
            {
                File.Delete(file);
                removed.Add(file);
            }
        }

        if (!results.Any(r => r.Role == ProjectSourceRole.Server && r.ModName == craftables))
        {
            removed.AddRange(CraftablesExporter.RemoveStale(server, modName, pakChunkIndex));
        }

        return removed;
    }

    /// <summary>
    /// The install rule of an export in the user's language (D13, D19): every player needs the client paks, the server the
    /// server paks, and both restart.
    /// </summary>
    public static string InstallNote(IReadOnlyList<ExportResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        static string Paks(IEnumerable<ExportResult> of) => string.Join(" + ", of.Select(r => Path.GetFileName(r.PakPath)).OfType<string>());
        var client = Paks(results.Where(r => r.Role == ProjectSourceRole.Client));
        var server = Paks(results.Where(r => r.Role == ProjectSourceRole.Server));
        return server.Length > 0 ? Localization.Loc.F("Export.Parity", client, server) : Localization.Loc.F("Export.ParityClientOnly", client);
    }

    /// <summary>
    /// What is wrong with the pairing of an export's paks, in the user's language: a Craftables pak built for one cook only
    /// (D13, D19: the server then does not know the recipes, and placed craftables can change).
    /// </summary>
    public static IReadOnlyList<string> ParityProblems(IReadOnlyList<ExportResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        static ExportResult? Crafted(IReadOnlyList<ExportResult> all, ProjectSourceRole role) =>
            all.FirstOrDefault(r => r.Role == role && r.ModName.EndsWith(CraftablesExporter.Suffix, StringComparison.Ordinal));
        var client = Crafted(results, ProjectSourceRole.Client);
        var server = Crafted(results, ProjectSourceRole.Server);
        if (client is not null && server is null)
        {
            return [Localization.Loc.F("Export.CraftablesServerMissing", Path.GetFileName(client.PakPath) ?? client.ModName)];
        }

        return server is not null && client is null ? [Localization.Loc.F("Export.CraftablesClientMissing", Path.GetFileName(server.PakPath) ?? server.ModName)] : [];
    }

    /// <summary>True when <paramref name="folder"/> holds the stock server cook (<c>pakchunk0*-WindowsServer.pak</c>), not just mod paks.</summary>
    public static bool IsServerPaksFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
        && Directory.EnumerateFiles(folder, "pakchunk0*-WindowsServer.pak").Any();

    /// <summary>
    /// The folder with the server's stock cook for the configured <paramref name="folder"/>: the folder itself, or, when it
    /// is the server's mods folder (owner's setup: <c>C:\SCUMServer\server_mods</c>), the Paks folder of the
    /// <c>SCUMServer.exe</c> installed next to it. Null when there is none.
    /// </summary>
    public static string? ResolveServerPaks(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return null;
        }

        if (IsServerPaksFolder(folder))
        {
            return folder;
        }

        var parent = Directory.GetParent(Path.GetFullPath(folder));
        if (parent?.Parent is null)
        {
            return null; // never search a whole drive
        }

        return GameLocator.FindDedicatedServersUnder(parent.FullName, maxDepth: 4, maxDirectories: 2_000)
            .Select(s => s.PaksDirectory)
            .FirstOrDefault(IsServerPaksFolder);
    }

    /// <summary>
    /// One-click export: the client pak into the last export folder (or Documents\ScumStudio Exports), the server pak too when
    /// the server Paks folder holds the stock server cook, and both copied (pak + sig) into the client/server mods folders
    /// set in Settings, so they are ready to load. Returns the results and the files copied into the mods folders.
    /// </summary>
    public static async Task<(IReadOnlyList<ExportResult> Results, IReadOnlyList<string> Installed)> QuickExportAsync(
        AppServices services, Project project, string defaultFolder, IProgressSink progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(project);
        var settings = services.Settings.Load();
        var folder = Path.GetFullPath(string.IsNullOrWhiteSpace(settings.ClientModsOutputFolder) ? defaultFolder : settings.ClientModsOutputFolder);
        var request = new ModExportRequest(project, folder, null, ResolveServerPaks(settings.ServerPaksFolder) is not null);
        var results = await ExportAsync(services, request, progress, cancellationToken).ConfigureAwait(false);
        var installed = new List<string>();
        foreach (var result in results)
        {
            var target = result.Role == ProjectSourceRole.Server ? services.Settings.Load().ServerModsOutputFolder : null;
            if (string.IsNullOrWhiteSpace(target) || result.PakPath is null)
            {
                continue;
            }

            Directory.CreateDirectory(target);
            foreach (var file in new[] { result.PakPath, result.SigPath }.OfType<string>())
            {
                var copy = Path.Combine(target, Path.GetFileName(file));
                if (!string.Equals(Path.GetFullPath(copy), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(file, copy, overwrite: true);
                }

                installed.Add(copy);
            }
        }

        // The server mods folder loses a Craftables pak copied by an earlier export when this one built none (none at all:
        // nothing was left to build).
        var serverMods = services.Settings.Load().ServerModsOutputFolder;
        if (!string.IsNullOrWhiteSpace(serverMods) && Directory.Exists(serverMods) && (results.Count == 0 || results.Any(r => r.Role == ProjectSourceRole.Server))
            && !results.Any(r => r.Role == ProjectSourceRole.Server && r.ModName == ProjectExporter.SanitizeModName(project.Manifest.Name) + CraftablesExporter.Suffix))
        {
            CraftablesExporter.RemoveStale(serverMods, project.Manifest.Name, request.PakChunkIndex);
        }

        // No server pak this time, but an earlier one sits in the server's mods folder: it no longer matches the new client
        // pak. That folder is the server's, so it is only named, never emptied.
        var oldServerPak = string.IsNullOrWhiteSpace(serverMods) ? null : Path.Combine(serverMods, ProjectExporter.PakFileName(project.Manifest.Name, request.PakChunkIndex));
        if (results.Count > 0 && !results.Any(r => r.Role == ProjectSourceRole.Server) && File.Exists(oldServerPak))
        {
            services.Notifications.Warning(Localization.Loc.T("Export.ParityTitle"), Localization.Loc.F("Export.StaleServerCopy", oldServerPak));
        }

        if (installed.Count > 0)
        {
            WarnRemoved(services, RemoveOtherInstallMode(serverMods!, project.Manifest.Name, request.PakChunkIndex, pagesInstalled: false));
        }

        return (results, installed);
    }

    private sealed class StepProgress(IProgressSink sink) : IProgress<string>
    {
        public void Report(string value) => sink.Report(value, 0, 0);
    }
}
